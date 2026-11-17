using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using Moq;
using static APIHandler.Tests.GroupSubmissionTestData;

namespace APIHandler.Tests.Repositories;

public class GroupSubmissionSerializationTests
{
    static GroupSubmissionSerializationTests()
    {
        Environment.SetEnvironmentVariable("ordersTable", "Orders");
        Environment.SetEnvironmentVariable("guestSessionTable", "GuestSessions");
    }

    [Fact]
    public async Task GetOrderSnapshotAsync_ActualMapper_LoadsOrderWithoutMutatingRawSnapshot()
    {
        var attributes = Attributes(Order(Dish()));
        var client = new Mock<IAmazonDynamoDB>();
        client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = attributes });
        using var context = new DynamoDBContextBuilder().WithDynamoDBClient(() => client.Object).Build();
        var repository = new OrderRepository(context, client.Object);

        var snapshot = await repository.GetOrderSnapshotAsync("existing-order", OwnerId);

        Assert.NotNull(snapshot);
        Assert.Equal("existing-order", snapshot.Order.OrderId);
        Assert.Equal(ReservationId, snapshot.Order.ReservationId);
        Assert.Equal(OrderStatus.Created, snapshot.Order.Status);
        snapshot.Order.Dishes[0].Quantity = 7;
        snapshot.Order.Dishes[0].State = OrderItemState.Confirmed;
        snapshot.Order.Status = OrderStatus.Submitted;
        Assert.Equal("1", snapshot.Attributes["dishes"].L[0].M["quantity"].N);
        Assert.Equal("0", snapshot.Attributes["dishes"].L[0].M["state"].N);
        Assert.Equal("0", snapshot.Attributes["status"].N);
        client.Verify(value => value.GetItemAsync(It.Is<GetItemRequest>(request => request.ConsistentRead == true), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveOrderIfUnchangedAsync_ActualMapper_PersistsWaiterDecisionAndOriginalCasValues()
    {
        var original = Snapshot(Dish());
        var client = new Mock<IAmazonDynamoDB>();
        UpdateItemRequest? update = null;
        client.Setup(value => value.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((request, _) => update = request)
            .ReturnsAsync(new UpdateItemResponse());
        using var context = new DynamoDBContextBuilder().WithDynamoDBClient(() => client.Object).Build();
        var repository = new OrderRepository(context, client.Object);
        original.Order.Dishes[0].State = OrderItemState.Confirmed;
        original.Order.Status = OrderStatus.Submitted;

        await repository.SaveOrderIfUnchangedAsync(original.Order, original);

        Assert.NotNull(update);
        Assert.Equal("1", update.ExpressionAttributeValues[":dishes"].L[0].M["state"].N);
        Assert.Equal("0", update.ExpressionAttributeValues[":originaldishes"].L[0].M["state"].N);
        Assert.Equal("1", update.ExpressionAttributeValues[":status"].N);
        Assert.Equal("0", update.ExpressionAttributeValues[":originalstatus"].N);
    }

    [Fact]
    public async Task GetForGroupSubmissionAsync_ActualMapper_ReadsNumericAndBoolGuestFlags()
    {
        Dictionary<string, AttributeValue> Item(string id, AttributeValue submitted, AttributeValue owner) => new()
        {
            ["guestSessionId"] = S(id), ["guestName"] = S(id), ["groupReservationId"] = S(GroupId),
            ["isSubmitted"] = submitted, ["isOwnerSession"] = owner, ["dishes"] = DishList([Dish()])
        };
        var client = new Mock<IAmazonDynamoDB>();
        client.Setup(value => value.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResponse { Items =
            [
                Item("legacy", N(1), N(0)),
                Item("current", new() { BOOL = true }, new() { BOOL = true })
            ] });
        using var context = new DynamoDBContextBuilder().WithDynamoDBClient(() => client.Object).Build();

        var sessions = await new GuestSessionRepository(context, client.Object).GetForGroupSubmissionAsync(GroupId);

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session => Assert.True(session.IsSubmitted));
        Assert.False(sessions[0].IsOwnerSession);
        Assert.True(sessions[1].IsOwnerSession);
        Assert.All(sessions, session => Assert.Equal(1, Assert.Single(session.Dishes).Quantity));
    }
}
