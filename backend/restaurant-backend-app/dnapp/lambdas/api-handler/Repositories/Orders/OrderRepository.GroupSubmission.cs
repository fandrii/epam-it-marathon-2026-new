using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.Orders;

public partial class OrderRepository
{
    public async Task<OrderSubmissionSnapshot?> GetOrderForGroupSubmissionAsync(string reservationId)
    {
        // The GSI finds keys only. Read the base-table item consistently before
        // merging, and retain its raw attributes for the conditional write.
        Dictionary<string, AttributeValue>? nextKey = null;
        var seen = new HashSet<(string OrderId, string CustomerId)>();
        OrderSubmissionSnapshot? found = null;
        do
        {
            var page = await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = OrdersTableName,
                IndexName = ReservationIdGlobalIndex,
                KeyConditionExpression = "#reservation = :reservation",
                ExpressionAttributeNames = new() { ["#reservation"] = "reservationId" },
                ExpressionAttributeValues = new() { [":reservation"] = StringValue(reservationId) },
                ExclusiveStartKey = nextKey
            });

            foreach (var candidate in page.Items ?? [])
            {
                if (!candidate.TryGetValue("orderId", out var orderId) ||
                    !candidate.TryGetValue("customerId", out var customerId) ||
                    !seen.Add((orderId.S, customerId.S)))
                    continue;

                var snapshot = await GetOrderSnapshotAsync(orderId.S, customerId.S);
                if (snapshot is null)
                    continue;

                var order = snapshot.Order;
                if (!string.Equals(order.ReservationId, reservationId, StringComparison.Ordinal) ||
                    order.Status == OrderStatus.Cancelled)
                    continue;
                if (found is not null)
                    throw new ValidationException("Multiple active orders were found for this reservation.");
                found = snapshot;
            }
            nextKey = page.LastEvaluatedKey;
        } while (nextKey is { Count: > 0 });
        return found;
    }

    public async Task SubmitExistingGroupOrderAtomicallyAsync(OrderSubmissionSnapshot snapshot,
        IReadOnlyList<OrderItem> mergedItems, GroupReservation groupReservation, Reservation reservation)
    {
        var order = snapshot.Order;
        var values = new Dictionary<string, AttributeValue>
        {
            [":reservation"] = StringValue(reservation.Id),
            [":dishes"] = MergedDishes(snapshot, mergedItems),
            [":submitted"] = NumberValue((int)OrderStatus.SubmittedByCustomer)
        };
        var condition = OrderSnapshotCondition(snapshot, values);

        var request = new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new() { Update = new Update
                {
                    TableName = OrdersTableName,
                    Key = new() { ["orderId"] = StringValue(order.OrderId), ["customerId"] = StringValue(order.CustomerId) },
                    UpdateExpression = "SET #dishes = :dishes, #status = :submitted",
                    ConditionExpression = condition,
                    ExpressionAttributeNames = new() { ["#dishes"] = "dishes", ["#status"] = "status" },
                    ExpressionAttributeValues = values
                } },
                new() { Update = new Update
                {
                    TableName = ResourceNames.GroupReservationsTableName,
                    Key = new() { ["groupReservationId"] = StringValue(groupReservation.GroupReservationId) },
                    UpdateExpression = "SET #status = :submitted",
                    ConditionExpression = "attribute_exists(groupReservationId) AND reservationId = :reservation AND #status = :open",
                    ExpressionAttributeNames = new() { ["#status"] = "status" },
                    ExpressionAttributeValues = new()
                    {
                        [":reservation"] = StringValue(reservation.Id),
                        [":open"] = NumberValue((int)GroupReservationStatus.Open),
                        [":submitted"] = NumberValue((int)GroupReservationStatus.Submitted)
                    }
                } },
                // Conditional no-op update uses existing UpdateItem permissions
                // and ensures Owner/status validation still holds at commit time.
                new() { Update = new Update
                {
                    TableName = ResourceNames.ReservationsTableName,
                    Key = new() { ["id"] = StringValue(reservation.Id) },
                    UpdateExpression = "SET #status = :reserved",
                    ConditionExpression = "attribute_exists(id) AND customerId = :owner AND #status = :reserved",
                    ExpressionAttributeNames = new() { ["#status"] = "status" },
                    ExpressionAttributeValues = new()
                    {
                        [":owner"] = StringValue(reservation.CustomerId!),
                        [":reserved"] = NumberValue((int)ReservationStatus.Reserved)
                    }
                } }
            ]
        };
        try
        {
            await _dynamoDbClient.TransactWriteItemsAsync(request);
        }
        catch (TransactionCanceledException exception) when (IsGroupSubmissionConflict(exception))
        {
            throw new ValidationException("The order, reservation or group changed concurrently, or the group was already submitted. Refresh and retry.");
        }
    }

    private static bool IsGroupSubmissionConflict(TransactionCanceledException exception)
    {
        var reasons = exception.CancellationReasons;
        return reasons is { Count: > 0 } &&
            reasons.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") &&
            reasons.All(reason => reason.Code is null or "None" or "ConditionalCheckFailed" or "TransactionConflict");
    }

    private static AttributeValue MergedDishes(OrderSubmissionSnapshot snapshot, IReadOnlyList<OrderItem> items)
    {
        // Retain existing item metadata, including fields not mapped by OrderItem.
        // Only quantities change; fresh dishes receive the usual Pending state.
        var originals = new Dictionary<string, Dictionary<string, AttributeValue>>(StringComparer.Ordinal);
        if (snapshot.Attributes.TryGetValue("dishes", out var dishes))
            foreach (var item in dishes.L ?? [])
                if (item.M is not null && item.M.TryGetValue("dishId", out var id))
                    originals.TryAdd(id.S, item.M);

        return new AttributeValue
        {
            L = items.Select(item =>
            {
                var attributes = originals.TryGetValue(item.DishId, out var original)
                    ? new Dictionary<string, AttributeValue>(original)
                    : new Dictionary<string, AttributeValue>
                    {
                        ["dishId"] = StringValue(item.DishId),
                        ["state"] = NumberValue((int)item.State)
                    };
                attributes["quantity"] = NumberValue(item.Quantity);
                return new AttributeValue { M = attributes };
            }).ToList()
        };
    }

    private static AttributeValue StringValue(string value) => new() { S = value };
    private static AttributeValue NumberValue(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}
