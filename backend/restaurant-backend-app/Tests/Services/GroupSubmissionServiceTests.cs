using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.Dishes;
using APIHandler.Services.Locations;
using APIHandler.Services.Notifications;
using APIHandler.Services.Orders;
using Moq;
using static APIHandler.Tests.GroupSubmissionTestData;

namespace APIHandler.Tests.Services;

public class GroupSubmissionServiceTests
{
    [Fact]
    public async Task SubmitGroupReservationAsync_MixedSessions_AggregatesAndMergesWithoutMutatingExistingOrder()
    {
        var fixture = new Fixture();
        fixture.Snapshot = Snapshot(Dish("dish1", 4), Dish("dish1", 2), Dish("unrelated", 7, OrderItemState.Confirmed));
        fixture.Sessions =
        [
            Session("owner", false, Dish("ignored-owner", 99)),
            Session("guest1", true, Dish("dish1", 1), Dish("dish2", 2)),
            Session("guest2", true, Dish("dish1", 2)),
            Session("guest3", false, Dish("ignored", 100))
        ];
        fixture.Sessions[0].IsOwnerSession = true;
        var original = fixture.Snapshot.Order;

        var result = await fixture.Submit();

        Assert.Equal(new[] { new ConsolidatedDishResponse("dish1", 3), new("dish2", 2) }, result);
        var merged = Assert.IsAssignableFrom<IReadOnlyList<OrderItem>>(fixture.MergedItems);
        Assert.Equal(3, merged.Count);
        Assert.Equal(9, Assert.Single(merged, item => item.DishId == "dish1").Quantity);
        Assert.Equal(2, Assert.Single(merged, item => item.DishId == "dish2").Quantity);
        var untouched = Assert.Single(merged, item => item.DishId == "unrelated");
        Assert.Equal(7, untouched.Quantity);
        Assert.Equal(OrderItemState.Confirmed, untouched.State);
        Assert.DoesNotContain(merged, item => item.DishId.StartsWith("ignored"));
        Assert.Equal(new[] { 4, 2, 7 }, original.Dishes.Select(item => item.Quantity));
        Assert.Equal(OrderStatus.Created, original.Status);
        Assert.Equal("existing-order", fixture.WrittenSnapshot!.Order.OrderId);
        Assert.Equal("Original address", fixture.WrittenSnapshot.Order.ReservationAddress);
        Assert.Equal("Original time", fixture.WrittenSnapshot.Order.ReservationTime);
        Assert.Same(fixture.Snapshot, fixture.WrittenSnapshot);
        fixture.VerifyOnlyAtomicWrite();
        fixture.Guests.Verify(value => value.GetForGroupSubmissionAsync(GroupId), Times.Once);
        fixture.Guests.Verify(value => value.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitGroupReservationAsync_NoSubmittedSessions_RejectsBeforeFindingOrWritingOrder(bool includeUnsubmitted)
    {
        var fixture = new Fixture { Sessions = includeUnsubmitted ? [Session("guest", false, Dish())] : [] };
        var error = await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        Assert.Contains("No submitted", error.Message);
        fixture.Orders.Verify(value => value.GetOrderForGroupSubmissionAsync(It.IsAny<string>()), Times.Never);
        fixture.VerifyNoWrites();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmittedSessionsEmpty_IgnoresDishesInUnsubmittedSessions()
    {
        var fixture = new Fixture { Sessions = [Session("submitted", true), Session("unsubmitted", false, Dish())] };
        var error = await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        Assert.Contains("contain no dishes", error.Message);
        fixture.VerifyNoWrites();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_MissingOrder_RejectsWithoutCreatingOne()
    {
        var fixture = new Fixture { Snapshot = null };
        var error = await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        Assert.Contains("No existing order", error.Message);
        fixture.VerifyNoWrites();
    }

    [Theory]
    [InlineData("missing-reservation", typeof(ReservationNotFoundException))]
    [InlineData("missing-group", typeof(GroupReservationNotFoundException))]
    [InlineData("non-owner", typeof(ActionForbiddenException))]
    [InlineData("unrelated-group", typeof(ValidationException))]
    [InlineData("cancelled-reservation", typeof(ValidationException))]
    [InlineData("past-cutoff", typeof(ValidationException))]
    [InlineData("submitted-group", typeof(ValidationException))]
    [InlineData("closed-group", typeof(ValidationException))]
    [InlineData("foreign-order", typeof(ActionForbiddenException))]
    [InlineData("unrelated-order", typeof(ValidationException))]
    [InlineData("submitted-order", typeof(ValidationException))]
    public async Task SubmitGroupReservationAsync_InvalidOwnershipRelationshipOrState_RejectsWithoutWrites(string scenario, Type expected)
    {
        var fixture = new Fixture();
        switch (scenario)
        {
            case "missing-reservation": fixture.Reservation = null; break;
            case "missing-group": fixture.Group = null; break;
            case "non-owner": fixture.Reservation!.CustomerId = "someone-else"; break;
            case "unrelated-group": fixture.Group!.ReservationId = "another-reservation"; break;
            case "cancelled-reservation": fixture.Reservation!.Status = ReservationStatus.Cancelled; break;
            case "past-cutoff": fixture.Reservation!.StartTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc); break;
            case "submitted-group": fixture.Group!.Status = GroupReservationStatus.Submitted; break;
            case "closed-group": fixture.Group!.Status = GroupReservationStatus.Closed; break;
            case "foreign-order": fixture.Snapshot!.Order.CustomerId = "someone-else"; break;
            case "unrelated-order": fixture.Snapshot!.Order.ReservationId = "another-reservation"; break;
            case "submitted-order": fixture.Snapshot!.Order.Status = OrderStatus.SubmittedByCustomer; break;
        }
        Assert.IsType(expected, await Record.ExceptionAsync(fixture.Submit));
        fixture.VerifyNoWrites();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SubmitGroupReservationAsync_NonpositiveSubmittedQuantity_Rejects(int quantity)
    {
        var fixture = new Fixture { Sessions = [Session("guest", true, Dish(quantity: quantity))] };
        await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        fixture.VerifyNoWrites();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmitGroupReservationAsync_QuantityOverflow_RejectsWithoutLosingExistingQuantities(bool overflowInSessions)
    {
        var fixture = new Fixture();
        if (overflowInSessions)
            fixture.Sessions = [Session("guest", true, Dish(quantity: int.MaxValue), Dish())];
        else
            fixture.Snapshot = Snapshot(Dish(quantity: int.MaxValue));
        await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        fixture.VerifyNoWrites();
    }

    [Theory]
    [InlineData(OrderItemState.Confirmed)]
    [InlineData(OrderItemState.Canceled)]
    public async Task SubmitGroupReservationAsync_ExistingDecidedDishOverlap_DoesNotApproveOrCancelNewQuantities(OrderItemState state)
    {
        var fixture = new Fixture { Snapshot = Snapshot(Dish(state: state)) };
        await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        Assert.Equal(1, fixture.Snapshot.Order.Dishes[0].Quantity);
        fixture.VerifyNoWrites();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ConflictingExistingDuplicateStates_RejectsWithoutDiscardingRows()
    {
        var fixture = new Fixture { Snapshot = Snapshot(Dish("existing"), Dish("existing", 2, OrderItemState.Confirmed)) };
        await Assert.ThrowsAsync<ValidationException>(fixture.Submit);
        Assert.Equal(2, fixture.Snapshot.Order.Dishes.Count);
        fixture.VerifyNoWrites();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_BlankCaller_IsUnauthorizedBeforeAnyRead()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SubmitGroupReservationAsync(" ", GroupId, ReservationId, fixture.Context));
        fixture.Reservations.VerifyNoOtherCalls();
        fixture.VerifyNoWrites();
    }

    private sealed class Fixture
    {
        internal Mock<IOrderRepository> Orders { get; } = new();
        internal Mock<IReservationRepository> Reservations { get; } = new();
        internal Mock<IGroupReservationRepository> Groups { get; } = new();
        internal Mock<IGuestSessionRepository> Guests { get; } = new();
        internal Reservation? Reservation { get; set; } = GroupSubmissionTestData.Reservation();
        internal GroupReservation? Group { get; set; } = GroupSubmissionTestData.Group();
        internal OrderSubmissionSnapshot? Snapshot { get; set; } = GroupSubmissionTestData.Snapshot(Dish("existing", 4));
        internal List<GuestSession> Sessions { get; set; } = [Session("guest", true, Dish())];
        internal OrderSubmissionSnapshot? WrittenSnapshot { get; private set; }
        internal IReadOnlyList<OrderItem>? MergedItems { get; private set; }
        internal OrderService Service { get; }
        internal ILambdaContext Context { get; }

        internal Fixture()
        {
            var context = new Mock<ILambdaContext>();
            context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
            Context = context.Object;
            Reservations.Setup(value => value.GetReservationByIdAsync(ReservationId)).Returns(() => Task.FromResult<Reservation?>(Reservation));
            Groups.Setup(value => value.GetGroupReservationByIdAsync(GroupId)).Returns(() => Task.FromResult<GroupReservation?>(Group));
            Guests.Setup(value => value.GetForGroupSubmissionAsync(GroupId)).Returns(() => Task.FromResult(Sessions));
            Orders.Setup(value => value.GetOrderForGroupSubmissionAsync(ReservationId)).Returns(() => Task.FromResult<OrderSubmissionSnapshot?>(Snapshot));
            Orders.Setup(value => value.SubmitExistingGroupOrderAtomicallyAsync(It.IsAny<OrderSubmissionSnapshot>(), It.IsAny<IReadOnlyList<OrderItem>>(), It.IsAny<GroupReservation>(), It.IsAny<Reservation>()))
                .Callback<OrderSubmissionSnapshot, IReadOnlyList<OrderItem>, GroupReservation, Reservation>((snapshot, items, _, _) =>
                {
                    WrittenSnapshot = snapshot;
                    MergedItems = items;
                }).Returns(Task.CompletedTask);
            Service = new(Orders.Object, Reservations.Object, Mock.Of<IDishService>(), Mock.Of<INotificationService>(), Groups.Object, Guests.Object, Mock.Of<ILocationService>());
        }

        internal Task<IReadOnlyList<ConsolidatedDishResponse>> Submit() => Service.SubmitGroupReservationAsync(OwnerId, GroupId, ReservationId, Context);

        internal void VerifyNoWrites()
        {
            Orders.Verify(value => value.SubmitExistingGroupOrderAtomicallyAsync(It.IsAny<OrderSubmissionSnapshot>(), It.IsAny<IReadOnlyList<OrderItem>>(), It.IsAny<GroupReservation>(), It.IsAny<Reservation>()), Times.Never);
            VerifyLegacyWritesNever();
        }

        internal void VerifyOnlyAtomicWrite()
        {
            Orders.Verify(value => value.SubmitExistingGroupOrderAtomicallyAsync(It.IsAny<OrderSubmissionSnapshot>(), It.IsAny<IReadOnlyList<OrderItem>>(), Group!, Reservation!), Times.Once);
            VerifyLegacyWritesNever();
        }

        private void VerifyLegacyWritesNever()
        {
            Orders.Verify(value => value.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
            Orders.Verify(value => value.SaveGroupOrderAtomicallyAsync(It.IsAny<Order>(), It.IsAny<GroupReservation>()), Times.Never);
        }
    }
}
