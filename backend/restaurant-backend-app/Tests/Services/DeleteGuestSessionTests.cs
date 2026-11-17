using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public class DeleteGuestSessionTests
{
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";
    private const string OwnerId = "owner-1";
    private readonly Mock<IGroupReservationRepository> _groups = new();
    private readonly Mock<IGuestSessionRepository> _sessions = new();
    private readonly Mock<IDishRepository> _dishes = new();
    private readonly Mock<IReservationRepository> _reservations = new();
    private readonly GuestSessionService _service;

    public DeleteGuestSessionTests()
    {
        _service = new(_groups.Object, _sessions.Object, _dishes.Object, _reservations.Object,
            Mock.Of<IUserRepository>(), Mock.Of<ITableRepository>());
        SetGroup(GroupReservationStatus.Open);
        _reservations.Setup(x => x.GetReservationByIdAsync(ReservationId)).ReturnsAsync(new Reservation
        {
            Id = ReservationId, CustomerId = OwnerId, TableId = "table-1", WaiterId = "waiter-1"
        });
    }

    [Fact]
    public async Task OwnerDeletesGuest_ReturnsUpdatedSnapshotWithExactTypesAndTotals()
    {
        var deleted = Session("delete-me", false, new OrderItem { DishId = "deleted-dish", Quantity = 9 });
        var remaining = new[]
        {
            Session("owner-session", true, new OrderItem { DishId = "dish-1", Quantity = 2 }),
            Session("session-2", false,
                new OrderItem { DishId = "dish-2", Quantity = 1 },
                new OrderItem { DishId = "dish-3", Quantity = 2 })
        };
        _sessions.Setup(x => x.GetByGuestSessionId(deleted.Id)).ReturnsAsync(deleted);
        _sessions.Setup(x => x.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync(remaining.ToList());
        _dishes.Setup(x => x.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(
        [
            new Dish { Id = "dish-1", Name = "Soup", Price = 10m },
            new Dish { Id = "dish-2", Name = "Tom Yum", Price = 220m },
            new Dish { Id = "dish-3", Name = "Tea", Price = 5m }
        ]);

        var result = await _service.DeleteGuestSessionAsync(OwnerId, GroupId, deleted.Id);

        _sessions.Verify(x => x.DeleteAsync(deleted), Times.Once);
        Assert.Equal(250m, result.Total);
        var participants = result.Participants.ToArray();
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal(230m, participants[1].Total);
        Assert.DoesNotContain(participants, x => x.Id == deleted.Id);
    }

    [Fact]
    public async Task NonOwner_IsForbiddenWithoutReadingOrDeletingSession()
    {
        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.DeleteGuestSessionAsync("other-user", GroupId, "guest-1"));
        _sessions.Verify(x => x.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        _sessions.Verify(x => x.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task MissingSession_IsNotFound()
    {
        _sessions.Setup(x => x.GetByGuestSessionId("missing")).ReturnsAsync((GuestSession?)null);
        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, "missing"));
    }

    [Fact]
    public async Task SessionFromAnotherGroup_IsRejectedWithoutDelete()
    {
        var session = Session("guest-1", false);
        session.GroupReservationId = "other-group";
        _sessions.Setup(x => x.GetByGuestSessionId("guest-1")).ReturnsAsync(session);
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, "guest-1"));
        _sessions.Verify(x => x.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task OwnerSession_IsRejectedWithoutDelete()
    {
        _sessions.Setup(x => x.GetByGuestSessionId("owner-session")).ReturnsAsync(Session("owner-session", true));
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, "owner-session"));
        _sessions.Verify(x => x.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task NonOpenGroup_IsRejectedBeforeReadingSession(GroupReservationStatus status, Type exceptionType)
    {
        SetGroup(status);
        Assert.IsType(exceptionType, await Record.ExceptionAsync(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, "guest-1")));
        _sessions.Verify(x => x.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
    }

    private void SetGroup(GroupReservationStatus status) =>
        _groups.Setup(x => x.GetGroupReservationByIdAsync(GroupId)).ReturnsAsync(new GroupReservation
        {
            GroupReservationId = GroupId, ReservationId = ReservationId,
            ReservationToken = "token", Status = status
        });

    private static GuestSession Session(string id, bool owner, params OrderItem[] dishes) => new()
    {
        Id = id, GroupReservationId = GroupId, GuestName = owner ? "Owner" : "Guest",
        IsOwnerSession = owner, IsSubmitted = false, Dishes = dishes.ToList()
    };
}
