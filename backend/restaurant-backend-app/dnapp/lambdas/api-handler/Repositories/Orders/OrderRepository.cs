using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.Orders;

public partial class OrderRepository : IOrderRepository
{
    private readonly string OrdersTableName = ResourceNames.OrdersTableName;
    private readonly string CustomerIdGlobalIndex = ResourceNames.OrdersTableCustomerIdGlobalIndex;
    private readonly string ReservationIdGlobalIndex = ResourceNames.OrdersTableReservationIdGlobalIndex;

    private readonly IDynamoDBContext _dynamoDbContext;
    private readonly IAmazonDynamoDB _dynamoDbClient;

    public OrderRepository(IDynamoDBContext dynamoDbContext, IAmazonDynamoDB dynamoDbClient)
    {
        _dynamoDbContext = dynamoDbContext ?? throw new ArgumentNullException(nameof(dynamoDbContext));
        _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
    }

    public async Task<Order?> GetOrderByIdAsync(string orderId, string customerId)
    {
        return await _dynamoDbContext.LoadAsync<Order>(orderId, customerId, DynamoConfig.LoadConfig(OrdersTableName));
    }

    public async Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(string customerId)
    {
        var config = new QueryConfig
        {
            OverrideTableName = OrdersTableName,
            IndexName = CustomerIdGlobalIndex,
        };

        var query = _dynamoDbContext.QueryAsync<Order>(customerId, config);

        return await query.GetRemainingAsync();
    }

    public async Task<Order?> GetOrderByReservationIdAsync(string reservationId)
    {
        var config = new QueryConfig
        {
            OverrideTableName = OrdersTableName,
            IndexName = ReservationIdGlobalIndex,
        };

        var results = await _dynamoDbContext.QueryAsync<Order>(reservationId, config).GetNextSetAsync();

        return results.FirstOrDefault(o => o.Status != OrderStatus.Cancelled);
    }

    public async Task SaveOrderAsync(Order order)
    {
        await _dynamoDbContext.SaveAsync(order, DynamoConfig.SaveConfig(OrdersTableName));
    }

    public async Task SaveGroupOrderAtomicallyAsync(Order order, GroupReservation groupReservation)
    {
        var orderDocument = _dynamoDbContext.ToDocument(order);
        var request = new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = OrdersTableName,
                        Item = orderDocument.ToAttributeMap(),
                        ConditionExpression = "attribute_not_exists(orderId)"
                    }
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = ResourceNames.GroupReservationsTableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["groupReservationId"] = new AttributeValue { S = groupReservation.GroupReservationId }
                        },
                        UpdateExpression = "SET #status = :submitted",
                        ConditionExpression = "#status = :open",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":open"] = new AttributeValue { N = ((int)GroupReservationStatus.Open).ToString() },
                            [":submitted"] = new AttributeValue { N = ((int)GroupReservationStatus.Submitted).ToString() }
                        }
                    }
                }
            ]
        };

        try
        {
            await _dynamoDbClient.TransactWriteItemsAsync(request);
        }
        catch (TransactionCanceledException)
        {
            throw new ValidationException("This group pre-order has already been confirmed.");
        }
    }
}
