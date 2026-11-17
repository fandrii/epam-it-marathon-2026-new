using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Handlers.Order;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.Dishes;
using APIHandler.Services.Locations;
using APIHandler.Services.Notifications;
using APIHandler.Services.Orders;
using Moq;
using System.Text.RegularExpressions;
using static APIHandler.Tests.GroupSubmissionTestData;

namespace APIHandler.Tests.Repositories;

public class GroupSubmissionRepositoryTests
{
    static GroupSubmissionRepositoryTests()
    {
        Environment.SetEnvironmentVariable("ordersTable", "Orders");
        Environment.SetEnvironmentVariable("groupReservationsTable", "GroupReservations");
        Environment.SetEnvironmentVariable("reservationsTable", "Reservations");
    }

    [Fact]
    public async Task GetOrderForGroupSubmissionAsync_PaginatesAndConsistentlyReloadsBeforeSelectingActiveOrder()
    {
        var client = new Mock<IAmazonDynamoDB>();
        var queries = new List<QueryRequest>();
        var reads = new List<GetItemRequest>();
        var next = new Dictionary<string, AttributeValue> { ["orderId"] = S("cursor") };
        client.Setup(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns((QueryRequest request, CancellationToken _) =>
            {
                queries.Add(request);
                return Task.FromResult(queries.Count == 1
                    ? new QueryResponse { Items = [Candidate("cancelled"), Candidate("moved")], LastEvaluatedKey = next }
                    : new QueryResponse { Items = [Candidate("active"), Candidate("active")] });
            });
        var active = Attributes(Order(Dish("dish1", 4)));
        active["orderId"] = S("active");
        active["unmappedMetadata"] = S("preserved");
        client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetItemRequest request, CancellationToken _) =>
            {
                reads.Add(request);
                var attributes = Attributes(Order(Dish()));
                attributes["orderId"] = request.Key["orderId"];
                if (request.Key["orderId"].S == "cancelled") attributes["status"] = N((int)OrderStatus.Cancelled);
                if (request.Key["orderId"].S == "moved") attributes["reservationId"] = S("different-reservation");
                return Task.FromResult(new GetItemResponse { Item = request.Key["orderId"].S == "active" ? active : attributes });
            });

        var snapshot = await LookupRepository(client).GetOrderForGroupSubmissionAsync(ReservationId);

        Assert.NotNull(snapshot);
        Assert.Equal("active", snapshot.Order.OrderId);
        Assert.Same(active, snapshot.Attributes);
        Assert.Equal(2, queries.Count);
        Assert.Same(next, queries[1].ExclusiveStartKey);
        Assert.All(queries, request =>
        {
            Assert.Equal("GSI_ReservationId", request.IndexName);
            Assert.Equal(ReservationId, request.ExpressionAttributeValues[":reservation"].S);
        });
        Assert.Equal(3, reads.Count);
        Assert.All(reads, request => Assert.True(request.ConsistentRead == true));
    }

    [Fact]
    public async Task GetOrderForGroupSubmissionAsync_DeletedIndexCandidate_ReturnsMissingWithoutWriting()
    {
        var client = new Mock<IAmazonDynamoDB>();
        client.Setup(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Candidate("deleted")] });
        client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse());
        Assert.Null(await LookupRepository(client).GetOrderForGroupSubmissionAsync(ReservationId));
        client.Verify(value => value.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOrderForGroupSubmissionAsync_MultipleActiveOrders_RejectsAmbiguousMerge()
    {
        var client = new Mock<IAmazonDynamoDB>();
        client.Setup(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Candidate("one"), Candidate("two")] });
        client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetItemRequest request, CancellationToken _) =>
            {
                var item = Attributes(Order(Dish()));
                item["orderId"] = request.Key["orderId"];
                return Task.FromResult(new GetItemResponse { Item = item });
            });
        await Assert.ThrowsAsync<ValidationException>(() => LookupRepository(client).GetOrderForGroupSubmissionAsync(ReservationId));
    }

    [Fact]
    public async Task SubmitExistingGroupOrderAtomicallyAsync_PreservesKeysMetadataAndUpdatesBothStatuses()
    {
        var database = new AtomicDatabase();
        database.CurrentOrder["unmappedMetadata"] = S("keep this");
        database.CurrentOrder["dishes"].L[0].M["price"] = new() { N = "12.50" };
        var original = database.Snapshot();

        await database.Submit(original);

        Assert.Equal(1, database.Commits);
        Assert.Equal("existing-order", database.CurrentOrder["orderId"].S);
        Assert.Equal("Original address", database.CurrentOrder["reservationAddress"].S);
        Assert.Equal("keep this", database.CurrentOrder["unmappedMetadata"].S);
        Assert.Equal("12.50", database.CurrentOrder["dishes"].L[0].M["price"].N);
        Assert.Equal("4", database.CurrentOrder["dishes"].L[0].M["quantity"].N);
        Assert.Equal(((int)OrderStatus.SubmittedByCustomer).ToString(), database.CurrentOrder["status"].N);
        Assert.Equal(((int)GroupReservationStatus.Submitted).ToString(), database.CurrentGroup["status"].N);
        var transaction = Assert.Single(database.Requests);
        Assert.Equal(3, transaction.TransactItems.Count);
        Assert.All(transaction.TransactItems, item =>
        {
            Assert.NotNull(item.Update);
            Assert.Null(item.Put);
            Assert.Null(item.Delete);
        });
        Assert.Equal("1", original.Attributes["dishes"].L[0].M["quantity"].N);
    }

    [Fact]
    public async Task SubmitExistingGroupOrderAtomicallyAsync_ConcurrentDuplicateRequests_CommitQuantitiesOnce()
    {
        var database = new AtomicDatabase { ConcurrentRequests = 2 };
        var snapshot = database.Snapshot();
        async Task<bool> Confirm()
        {
            try { await database.Submit(snapshot); return true; }
            catch (ValidationException) { return false; }
        }

        var outcomes = await Task.WhenAll(Confirm(), Confirm());

        Assert.Single(outcomes, success => success);
        Assert.Equal(1, database.Commits);
        Assert.Equal("4", database.CurrentOrder["dishes"].L[0].M["quantity"].N);
        Assert.Equal(((int)GroupReservationStatus.Submitted).ToString(), database.CurrentGroup["status"].N);
    }

    [Fact]
    public async Task LegacyEdit_LoadedBeforeGroupConfirmation_Returns400WithoutOverwritingConsolidatedOrder()
    {
        var database = new AtomicDatabase();
        var editReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowEditSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orders = new Mock<IOrderRepository>();
        orders.Setup(value => value.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(Order(Dish()));
        orders.Setup(value => value.GetOrderSnapshotAsync("existing-order", OwnerId))
            .Returns(() => database.Repository.GetOrderSnapshotAsync("existing-order", OwnerId));
        orders.Setup(value => value.SaveOrderIfUnchangedAsync(It.IsAny<Order>(), It.IsAny<OrderSubmissionSnapshot>()))
            .Returns(async (Order order, OrderSubmissionSnapshot snapshot) =>
            {
                editReady.SetResult();
                await allowEditSave.Task;
                await database.Repository.SaveOrderIfUnchangedAsync(order, snapshot);
            });
        var reservations = new Mock<IReservationRepository>();
        reservations.Setup(value => value.GetReservationByIdAsync(ReservationId)).ReturnsAsync(Reservation());
        var dishes = new Mock<IDishService>();
        dishes.Setup(value => value.GetDishByIdAsync("replacement"))
            .ReturnsAsync(new Dish { Id = "replacement", Name = "Replacement", Price = 5m, State = DishState.Available });
        var service = new OrderService(orders.Object, reservations.Object, dishes.Object,
            Mock.Of<INotificationService>(), Mock.Of<IGroupReservationRepository>(), Mock.Of<IGuestSessionRepository>(), Mock.Of<ILocationService>());
        var context = new Mock<ILambdaContext>();
        context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
        var edit = new EditPreOrderHandler(service).HandleRequest(new APIGatewayProxyRequest
        {
            Body = "{\"orderItems\":[{\"dishId\":\"replacement\",\"quantity\":2}]}",
            PathParameters = new Dictionary<string, string> { ["reservationId"] = ReservationId },
            RequestContext = new() { Authorizer = new() { Claims = new() { ["sub"] = OwnerId, ["cognito:groups"] = "Customer" } } }
        }, context.Object);
        Assert.Same(editReady.Task, await Task.WhenAny(editReady.Task, edit));

        await database.Submit(database.Snapshot());
        allowEditSave.SetResult();
        var response = await edit;

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("changed concurrently", response.Body);
        Assert.Equal("dish1", database.CurrentOrder["dishes"].L[0].M["dishId"].S);
        Assert.Equal("4", database.CurrentOrder["dishes"].L[0].M["quantity"].N);
        Assert.Equal(((int)OrderStatus.SubmittedByCustomer).ToString(), database.CurrentOrder["status"].N);
        Assert.Equal(((int)GroupReservationStatus.Submitted).ToString(), database.CurrentGroup["status"].N);
        Assert.Equal(0, database.ConditionalCommits);
        orders.Verify(value => value.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task SaveOrderIfUnchangedAsync_ConcurrentOrdinaryEdits_OnlyOneSnapshotCanCommit()
    {
        var database = new AtomicDatabase { ConcurrentUpdates = 2 };
        var first = await database.Repository.GetOrderSnapshotAsync("existing-order", OwnerId);
        var second = await database.Repository.GetOrderSnapshotAsync("existing-order", OwnerId);
        Assert.NotNull(first);
        Assert.NotNull(second);
        first.Order.Dishes[0].Quantity = 5;
        second.Order.Dishes[0].Quantity = 9;
        async Task<(bool Success, int Quantity)> Save(OrderSubmissionSnapshot snapshot)
        {
            try { await database.Repository.SaveOrderIfUnchangedAsync(snapshot.Order, snapshot); return (true, snapshot.Order.Dishes[0].Quantity); }
            catch (ValidationException) { return (false, snapshot.Order.Dishes[0].Quantity); }
        }

        var outcomes = await Task.WhenAll(Save(first), Save(second));

        var committed = Assert.Single(outcomes, result => result.Success);
        Assert.Equal(1, database.ConditionalCommits);
        Assert.Equal(committed.Quantity.ToString(), database.CurrentOrder["dishes"].L[0].M["quantity"].N);
        Assert.Equal(((int)GroupReservationStatus.Open).ToString(), database.CurrentGroup["status"].N);
    }

    [Theory]
    [InlineData("order-dishes")]
    [InlineData("order-status")]
    [InlineData("order-deleted")]
    [InlineData("group-closed")]
    [InlineData("group-moved")]
    [InlineData("reservation-owner")]
    [InlineData("reservation-status")]
    public async Task SubmitExistingGroupOrderAtomicallyAsync_ConcurrentChange_RejectsAllWrites(string mutation)
    {
        var database = new AtomicDatabase();
        var snapshot = database.Snapshot();
        switch (mutation)
        {
            case "order-dishes": database.CurrentOrder["dishes"] = DishList([Dish(quantity: 9)]); break;
            case "order-status": database.CurrentOrder["status"] = N((int)OrderStatus.Cancelled); break;
            case "order-deleted": database.CurrentOrder.Clear(); break;
            case "group-closed": database.CurrentGroup["status"] = N((int)GroupReservationStatus.Closed); break;
            case "group-moved": database.CurrentGroup["reservationId"] = S("other-reservation"); break;
            case "reservation-owner": database.CurrentReservation["customerId"] = S("other-owner"); break;
            case "reservation-status": database.CurrentReservation["status"] = N((int)ReservationStatus.Cancelled); break;
        }
        var before = database.StateJson();

        await Assert.ThrowsAsync<ValidationException>(() => database.Submit(snapshot));

        Assert.Equal(0, database.Commits);
        Assert.Equal(before, database.StateJson());
    }

    [Fact]
    public async Task SubmitExistingGroupOrderAtomicallyAsync_OmittedLegacyFields_UsesAbsentFieldGuards()
    {
        var database = new AtomicDatabase();
        database.CurrentOrder.Remove("dishes");
        database.CurrentOrder.Remove("status");
        var snapshot = new OrderSubmissionSnapshot(Order(), new(database.CurrentOrder));
        await database.Submit(snapshot);
        var update = database.Requests[0].TransactItems[0].Update;
        Assert.Contains("attribute_not_exists(#dishes)", update.ConditionExpression);
        Assert.Contains("attribute_not_exists(#status)", update.ConditionExpression);
        Assert.Equal(1, database.Commits);
        Assert.Equal("4", database.CurrentOrder["dishes"].L[0].M["quantity"].N);
    }

    [Theory]
    [InlineData("ConditionalCheckFailed", true)]
    [InlineData("TransactionConflict", true)]
    [InlineData("ProvisionedThroughputExceeded", false)]
    [InlineData("InternalServerError", false)]
    [InlineData("ValidationError", false)]
    [InlineData(null, false)]
    public async Task SubmitExistingGroupOrderAtomicallyAsync_TransactionFailure_OnlyMapsConditionalConflicts(string? reason, bool validation)
    {
        var client = new Mock<IAmazonDynamoDB>();
        var failure = new TransactionCanceledException("transaction failed")
        {
            CancellationReasons = reason is null ? [] : [new() { Code = reason }]
        };
        client.Setup(value => value.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var repository = new OrderRepository(Mock.Of<IDynamoDBContext>(), client.Object);
        var task = repository.SubmitExistingGroupOrderAtomicallyAsync(Snapshot(Dish()), [Dish(quantity: 4)], Group(), Reservation());
        if (validation) await Assert.ThrowsAsync<ValidationException>(() => task);
        else Assert.Same(failure, await Assert.ThrowsAsync<TransactionCanceledException>(() => task));
    }

    private static Dictionary<string, AttributeValue> Candidate(string id) => new() { ["orderId"] = S(id), ["customerId"] = S(OwnerId) };

    private static OrderRepository LookupRepository(Mock<IAmazonDynamoDB> client)
    {
        var context = new Mock<IDynamoDBContext>();
        context.Setup(value => value.FromDocument<Order>(It.IsAny<Document>())).Returns((Document document) =>
        {
            var item = document.ToAttributeMap();
            var order = Order();
            order.OrderId = item["orderId"].S;
            order.CustomerId = item["customerId"].S;
            order.ReservationId = item["reservationId"].S;
            order.Status = (OrderStatus)int.Parse(item["status"].N);
            return order;
        });
        return new(context.Object, client.Object);
    }

    // This double evaluates the actual conditional transaction request before applying
    // any writes. The gate makes competing confirmations use the same old snapshot.
    private sealed class AtomicDatabase
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updatesArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        private int _updateArrivals;
        internal int ConcurrentRequests { get; init; } = 1;
        internal int ConcurrentUpdates { get; init; } = 1;
        internal int Commits { get; private set; }
        internal int ConditionalCommits { get; private set; }
        internal List<TransactWriteItemsRequest> Requests { get; } = [];
        internal Dictionary<string, AttributeValue> CurrentOrder { get; } = Attributes(Order(Dish()));
        internal Dictionary<string, AttributeValue> CurrentGroup { get; } = new()
        {
            ["groupReservationId"] = S(GroupId), ["reservationId"] = S(ReservationId), ["status"] = N((int)GroupReservationStatus.Open)
        };
        internal Dictionary<string, AttributeValue> CurrentReservation { get; } = new()
        {
            ["id"] = S(ReservationId), ["customerId"] = S(OwnerId), ["status"] = N((int)ReservationStatus.Reserved)
        };
        private readonly OrderRepository _repository;
        internal OrderRepository Repository => _repository;

        internal AtomicDatabase()
        {
            var client = new Mock<IAmazonDynamoDB>();
            client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .Returns((GetItemRequest request, CancellationToken _) =>
                {
                    Assert.True(request.ConsistentRead == true);
                    lock (_sync) return Task.FromResult(new GetItemResponse { Item = new(CurrentOrder) });
                });
            client.Setup(value => value.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (UpdateItemRequest request, CancellationToken _) =>
                {
                    if (ConcurrentUpdates > 1)
                    {
                        if (Interlocked.Increment(ref _updateArrivals) == ConcurrentUpdates) _updatesArrived.SetResult();
                        await _updatesArrived.Task;
                    }
                    var update = new Update
                    {
                        TableName = request.TableName, Key = request.Key, UpdateExpression = request.UpdateExpression,
                        ConditionExpression = request.ConditionExpression, ExpressionAttributeNames = request.ExpressionAttributeNames,
                        ExpressionAttributeValues = request.ExpressionAttributeValues
                    };
                    lock (_sync)
                    {
                        if (!Matches(update, Row(update))) throw new ConditionalCheckFailedException("Stale order snapshot");
                        Apply(update, Row(update));
                        ConditionalCommits++;
                        return new UpdateItemResponse();
                    }
                });
            client.Setup(value => value.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (TransactWriteItemsRequest request, CancellationToken _) =>
                {
                    if (ConcurrentRequests > 1)
                    {
                        if (Interlocked.Increment(ref _arrivals) == ConcurrentRequests) _arrived.SetResult();
                        await _arrived.Task;
                    }
                    lock (_sync)
                    {
                        Requests.Add(request);
                        var updates = request.TransactItems.Select(item => item.Update ?? throw new InvalidOperationException("Unexpected insert/delete")).ToArray();
                        var reasons = updates.Select(update => new CancellationReason
                        {
                            Code = Matches(update, Row(update)) ? "None" : "ConditionalCheckFailed"
                        }).ToList();
                        if (reasons.Any(reason => reason.Code != "None"))
                            throw new TransactionCanceledException("Conditional conflict") { CancellationReasons = reasons };
                        foreach (var update in updates)
                            Apply(update, Row(update));
                        Commits++;
                        return new TransactWriteItemsResponse();
                    }
                });
            var context = new DynamoDBContextBuilder().WithDynamoDBClient(() => client.Object).Build();
            _repository = new(context, client.Object);
        }

        internal OrderSubmissionSnapshot Snapshot() => new(Order(Dish()), new(CurrentOrder));
        internal Task Submit(OrderSubmissionSnapshot snapshot) => _repository.SubmitExistingGroupOrderAtomicallyAsync(snapshot, [Dish(quantity: 4)], Group(), Reservation());
        internal string StateJson() => string.Join("|", new[] { CurrentOrder, CurrentGroup, CurrentReservation }.Select(row => Document.FromAttributeMap(row).ToJson()));

        private Dictionary<string, AttributeValue> Row(Update update)
        {
            var row = update.TableName switch
            {
                "Orders" => CurrentOrder, "GroupReservations" => CurrentGroup, "Reservations" => CurrentReservation,
                _ => throw new InvalidOperationException("Unexpected table")
            };
            foreach (var key in update.Key)
                if (row.Count > 0) Assert.Equal(key.Value.S, row[key.Key].S);
            return row;
        }

        private static bool Matches(Update update, Dictionary<string, AttributeValue> row)
        {
            foreach (var condition in update.ConditionExpression.Split(" AND "))
            {
                var existence = Regex.Match(condition, @"^attribute_(exists|not_exists)\(([^)]+)\)$");
                if (existence.Success)
                {
                    bool exists = row.ContainsKey(Resolve(update, existence.Groups[2].Value));
                    if (exists != (existence.Groups[1].Value == "exists")) return false;
                }
                else
                {
                    var equality = condition.Split(" = ");
                    Assert.Equal(2, equality.Length);
                    if (!row.TryGetValue(Resolve(update, equality[0]), out var actual)) return false;
                    var expected = update.ExpressionAttributeValues[equality[1]];
                    if (ValueJson(actual) != ValueJson(expected)) return false;
                }
            }
            return true;
        }

        private static void Apply(Update update, Dictionary<string, AttributeValue> row)
        {
            foreach (var assignment in update.UpdateExpression[4..].Split(','))
            {
                var sides = assignment.Trim().Split(" = ");
                row[Resolve(update, sides[0])] = update.ExpressionAttributeValues[sides[1]];
            }
        }

        private static string Resolve(Update update, string name) => name.StartsWith('#') ? update.ExpressionAttributeNames[name] : name;
        private static string ValueJson(AttributeValue value) => Document.FromAttributeMap(new() { ["value"] = value }).ToJson();
    }
}
