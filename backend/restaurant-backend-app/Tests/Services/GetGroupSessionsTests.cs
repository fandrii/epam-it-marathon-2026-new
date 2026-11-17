using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Services;

public class GetGroupSessionsTests
{
    private const string GroupReservationId = "group-id";
    private const string ReservationId = "reservation-id";
    private const string OwnerUserId = "owner-user-id";

    private readonly Mock<IGroupReservationRepository> _groupReservationRepository = new();
    private readonly Mock<IReservationRepository> _reservationRepository = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepository = new();
    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly GuestSessionService _service;

    public GetGroupSessionsTests()
    {
        _service = new GuestSessionService(
            _groupReservationRepository.Object,
            _guestSessionRepository.Object,
            _dishRepository.Object,
            _reservationRepository.Object,
            Mock.Of<APIHandler.Repositories.Auth.IUserRepository>(),
            Mock.Of<APIHandler.Repositories.Tables.ITableRepository>());
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ReservationOwner_ReturnsParticipantsAndTotals()
    {
        SetupOwnerAuthorization(OwnerUserId);

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = "owner-session",
                GroupReservationId = GroupReservationId,
                GuestName = "Owner",
                IsOwnerSession = true,
                IsSubmitted = true,
                Dishes =
                [
                    new OrderItem { DishId = "dish-a", Quantity = 2 },
                    new OrderItem { DishId = "dish-b", Quantity = 1 }
                ]
            },
            new()
            {
                Id = "guest-session",
                GroupReservationId = GroupReservationId,
                GuestName = "Guest",
                IsOwnerSession = false,
                IsSubmitted = false,
                Dishes = [new OrderItem { DishId = "dish-a", Quantity = 3 }]
            }
        };
        var dishes = new[]
        {
            new Dish { Id = "dish-a", Name = "Soup", Price = 4.50m },
            new Dish { Id = "dish-b", Name = "Pasta", Price = 12m }
        };

        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync(sessions);
        _dishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        var response = await _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId);

        var participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal(21m, participants[0].Total);
        Assert.Equal(13.50m, participants[1].Total);
        Assert.Equal(34.50m, response.Total);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.True(participants[0].IsSubmitted);
        Assert.False(participants[1].IsSubmitted);
        Assert.Equal(new[] { 2, 1 }, participants[0].Items.Select(item => item.Quantity));
        _dishRepository.Verify(repository => repository.GetDishesByIdsAsync(
            It.Is<IEnumerable<string>>(ids => ids.OrderBy(id => id).SequenceEqual(new[] { "dish-a", "dish-b" }))), Times.Once);
        _guestSessionRepository.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
        _guestSessionRepository.Verify(repository => repository.CreateOwnerSessionIfAbsentAsync(It.IsAny<GuestSession>()), Times.Never);
        _guestSessionRepository.Verify(repository => repository.CreateAtomicallyAsync(It.IsAny<GuestSession>(),
            It.IsAny<Reservation>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _guestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerResponse_UsesDedicatedJsonShapeForParticipantItemsAndDishes()
    {
        SetupOwnerAuthorization(OwnerUserId);
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "owner-session",
                    GroupReservationId = GroupReservationId,
                    GuestName = "Owner",
                    IsOwnerSession = true,
                    IsSubmitted = true,
                    Dishes = [new OrderItem { DishId = "dish-a", Quantity = 2 }]
                },
                new GuestSession
                {
                    Id = "guest-session",
                    GroupReservationId = GroupReservationId,
                    GuestName = "Guest",
                    IsOwnerSession = false,
                    IsSubmitted = false,
                    Dishes = [new OrderItem { DishId = "dish-b", Quantity = 3 }]
                }
            ]);
        _dishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(
            [
                new Dish { Id = "dish-a", Name = "Soup", Price = 4.50m },
                new Dish { Id = "dish-b", Name = "Pasta", Price = 12m }
            ]);

        var result = await _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId);
        var response = ResponseBuilder.CreateJsonResponse(200, result);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        var participants = root.GetProperty("participants");
        var participant = participants[0];
        var item = participant.GetProperty("items")[0];
        var dish = item.GetProperty("dish");
        var guestParticipant = participants[1];
        var guestItem = guestParticipant.GetProperty("items")[0];
        var guestDish = guestItem.GetProperty("dish");

        AssertPropertyNames(root, "participants", "total");
        AssertPropertyNames(participant, "id", "type", "name", "items", "total", "isSubmitted");
        AssertPropertyNames(item, "dish", "quantity");
        AssertPropertyNames(dish, "id", "name", "price");
        AssertPropertyNames(guestParticipant, "id", "type", "name", "items", "total", "isSubmitted");
        AssertPropertyNames(guestItem, "dish", "quantity");
        AssertPropertyNames(guestDish, "id", "name", "price");
        Assert.Equal("owner-session", participant.GetProperty("id").GetString());
        Assert.Equal("Owner", participant.GetProperty("type").GetString());
        Assert.Equal("Owner", participant.GetProperty("name").GetString());
        Assert.True(participant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(9m, participant.GetProperty("total").GetDecimal());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal("dish-a", dish.GetProperty("id").GetString());
        Assert.Equal("Soup", dish.GetProperty("name").GetString());
        Assert.Equal(4.50m, dish.GetProperty("price").GetDecimal());
        Assert.Equal("guest-session", guestParticipant.GetProperty("id").GetString());
        Assert.Equal("Guest", guestParticipant.GetProperty("type").GetString());
        Assert.Equal("Guest", guestParticipant.GetProperty("name").GetString());
        Assert.False(guestParticipant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(36m, guestParticipant.GetProperty("total").GetDecimal());
        Assert.Equal(3, guestItem.GetProperty("quantity").GetInt32());
        Assert.Equal("dish-b", guestDish.GetProperty("id").GetString());
        Assert.Equal("Pasta", guestDish.GetProperty("name").GetString());
        Assert.Equal(12m, guestDish.GetProperty("price").GetDecimal());
        Assert.Equal(45m, root.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NonOwner_ThrowsForbiddenWithoutLoadingSessions()
    {
        SetupOwnerAuthorization("different-user-id");

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId));

        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _dishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupDoesNotExist_ThrowsNotFoundWithoutLoadingReservation()
    {
        _groupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId));

        _reservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerHasNoParticipants_ReturnsEmptyResponseWithoutLoadingDishes()
    {
        SetupOwnerAuthorization(OwnerUserId);
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync([]);

        var response = await _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId);

        Assert.Empty(response.Participants);
        Assert.Equal(0m, response.Total);
        _dishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_SessionReferencesMissingDish_ThrowsDishNotFound()
    {
        SetupOwnerAuthorization(OwnerUserId);
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "guest-session",
                    GroupReservationId = GroupReservationId,
                    GuestName = "Guest",
                    IsOwnerSession = false,
                    IsSubmitted = true,
                    Dishes = [new OrderItem { DishId = "deleted-dish", Quantity = 2 }]
                }
            ]);
        _dishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(
            () => _service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId));

        Assert.Contains("deleted-dish", exception.Message);
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expectedNames)
    {
        Assert.Equal(
            expectedNames.OrderBy(name => name),
            element.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
    }

    private void SetupOwnerAuthorization(string reservationOwnerId)
    {
        _groupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(GroupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = GroupReservationId,
                ReservationId = ReservationId,
                ReservationToken = "reservation-token"
            });
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-id",
                WaiterId = "waiter-id",
                CustomerId = reservationOwnerId
            });
    }
}
