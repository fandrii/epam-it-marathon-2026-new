using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Repositories.Orders;

public partial class OrderRepository
{
    public async Task<OrderSubmissionSnapshot?> GetOrderSnapshotAsync(string orderId, string customerId)
    {
        var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
        {
            TableName = OrdersTableName,
            Key = new() { ["orderId"] = StringValue(orderId), ["customerId"] = StringValue(customerId) },
            ConsistentRead = true
        });
        if (response.Item is null || response.Item.Count == 0)
            return null;

        var order = _dynamoDbContext.FromDocument<Order>(Document.FromAttributeMap(response.Item));
        return new OrderSubmissionSnapshot(order, response.Item);
    }

    public async Task SaveOrderIfUnchangedAsync(Order order, OrderSubmissionSnapshot original)
    {
        if (!original.Attributes.TryGetValue("orderId", out var orderId) ||
            !original.Attributes.TryGetValue("customerId", out var customerId) ||
            !string.Equals(order.OrderId, orderId.S, StringComparison.Ordinal) ||
            !string.Equals(order.CustomerId, customerId.S, StringComparison.Ordinal))
            throw new ValidationException("The existing order identity cannot be changed.");

        var document = _dynamoDbContext.ToDocument(order, new ToDocumentConfig
        {
            OverrideTableName = OrdersTableName
        }).ToAttributeMap();
        var values = new Dictionary<string, AttributeValue>
        {
            [":reservation"] = StringValue(order.ReservationId),
            [":status"] = NumberValue((int)order.Status)
        };
        var condition = OrderSnapshotCondition(original, values);
        string update = "SET #status = :status";
        if (document.TryGetValue("dishes", out var dishes) && dishes.NULL != true)
        {
            values[":dishes"] = dishes;
            update += ", #dishes = :dishes";
        }
        else
        {
            update += " REMOVE #dishes";
        }

        try
        {
            await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = OrdersTableName,
                Key = new() { ["orderId"] = orderId, ["customerId"] = customerId },
                UpdateExpression = update,
                ConditionExpression = condition,
                ExpressionAttributeNames = new() { ["#dishes"] = "dishes", ["#status"] = "status" },
                ExpressionAttributeValues = values
            });
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ValidationException("The order changed concurrently. Refresh and retry.");
        }
    }

    private static string OrderSnapshotCondition(OrderSubmissionSnapshot snapshot,
        Dictionary<string, AttributeValue> values)
    {
        var condition = "attribute_exists(orderId) AND reservationId = :reservation";
        // Raw originals remain unchanged while the caller mutates the loaded entity.
        foreach (var name in new[] { "dishes", "status" })
        {
            if (snapshot.Attributes.TryGetValue(name, out var original))
            {
                values[":original" + name] = original;
                condition += $" AND #{name} = :original{name}";
            }
            else
            {
                condition += $" AND attribute_not_exists(#{name})";
            }
        }
        return condition;
    }
}
