using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
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
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_OwnerAndExistingOwnerSession_ReturnsExistingSessionIdWithoutCreatingSession()
    {
        const string userId = "owner-1";
        const string reservationId = "reservation-1";
        var reservation = CreateReservation(reservationId, userId);
        var group = CreateGroup("group-1", reservationId);
        var ownerSession = CreateOwnerSession("owner-session-123", group.GroupReservationId, userId);
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGroupReservationRepository.Setup(repository => repository.GetByReservationIdAsync(reservationId)).ReturnsAsync(group);
        _mockGuestSessionRepository.Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([ownerSession]);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);

        Assert.Equal(ownerSession.Id, result.GuestSessionId);
        _mockGuestSessionRepository.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_DifferentUser_ThrowsForbiddenWithoutReadingSessions()
    {
        const string reservationId = "reservation-1";
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(CreateReservation(reservationId, "actual-owner"));

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync("other-user", reservationId));

        _mockGroupReservationRepository.Verify(repository => repository.GetByReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MissingReservation_ThrowsReservationNotFound()
    {
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync("missing"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync("owner-1", "missing"));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NoMatchingOwnerSession_ThrowsGuestSessionNotFound()
    {
        const string userId = "owner-1";
        const string reservationId = "reservation-1";
        var reservation = CreateReservation(reservationId, userId);
        var group = CreateGroup("group-1", reservationId);
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGroupReservationRepository.Setup(repository => repository.GetByReservationIdAsync(reservationId)).ReturnsAsync(group);
        _mockGuestSessionRepository.Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([CreateOwnerSession("someone-elses-session", group.GroupReservationId, "different-owner")]);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId));
    }

    [Fact]
    public async Task CreateGroupOwnerSessionAsync_FirstAttempt_SavesDeterministicOwnerSession()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        _mockGuestSessionRepository.Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);
        GuestSession? savedSession = null;
        _mockGuestSessionRepository.Setup(repository => repository.CreateOwnerSessionIfAbsentAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(session => savedSession = session)
            .Returns(Task.CompletedTask);

        await _guestSessionService.CreateGroupOwnerSessionAsync(userId, groupId);

        Assert.NotNull(savedSession);
        Assert.Equal($"owner-{groupId}", savedSession.Id);
        Assert.Equal(groupId, savedSession.GroupReservationId);
        Assert.Equal(userId, savedSession.OwnerUserId);
        Assert.True(savedSession.IsOwnerSession);
        Assert.Empty(savedSession.Dishes);
    }

    [Fact]
    public async Task CreateGroupOwnerSessionAsync_RetryAfterOwnerExists_DoesNotSaveDuplicate()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        _mockGuestSessionRepository.Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([CreateOwnerSession($"owner-{groupId}", groupId, userId)]);

        await _guestSessionService.CreateGroupOwnerSessionAsync(userId, groupId);

        _mockGuestSessionRepository.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(repository => repository.CreateOwnerSessionIfAbsentAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task CreateGroupOwnerSessionAsync_ConcurrentAttempts_UseSameDeterministicSessionKey()
    {
        const string userId = "owner-1";
        const string groupId = "group-1";
        _mockGuestSessionRepository.Setup(repository => repository.GetAllByGroupReservationIdAsync(groupId))
            .ReturnsAsync([]);
        var savedIds = new List<string>();
        _mockGuestSessionRepository.Setup(repository => repository.CreateOwnerSessionIfAbsentAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(session => savedIds.Add(session.Id))
            .Returns(Task.CompletedTask);

        await Task.WhenAll(
            _guestSessionService.CreateGroupOwnerSessionAsync(userId, groupId),
            _guestSessionService.CreateGroupOwnerSessionAsync(userId, groupId));

        Assert.Equal(2, savedIds.Count);
        Assert.All(savedIds, id => Assert.Equal($"owner-{groupId}", id));
        Assert.Single(savedIds.Distinct());
    }
    [Theory]
    [InlineAutoData(1)]
    [InlineAutoData(4)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.CreateAtomicallyAsync(It.IsAny<GuestSession>(), parentReservation,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((GuestSession created, Reservation _, string key, string fingerprint, string token) =>
            {
                session = created;
                return Task.FromResult(new CreatGuestSessionResponse(created.Id, token));
            });

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Fact]
    public async Task CreateGuestSessionAsync_ReplayedKey_ReturnsOriginalResponseWithoutCreatingOrCheckingCapacity()
    {
        var request = new CreateGuestSessionRequest("group-1", "Guest") { IdempotencyKey = "attempt-123" };
        var response = new CreatGuestSessionResponse("existing-session", "original-token");
        _mockGuestSessionRepository.Setup(repository => repository.GetCreationResultAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(response);

        Assert.Equal(response, await _guestSessionService.CreateGuestSessionAsync(request));
        _mockGroupReservationRepository.Verify(repository => repository.GetGroupReservationByIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(repository => repository.CreateAtomicallyAsync(It.IsAny<GuestSession>(),
            It.IsAny<Reservation>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateGuestSessionAsync_FingerprintIncludesGroupAndName_WhileKeyIsIndependent()
    {
        var inputs = new List<(string key, string fingerprint)>();
        _mockGuestSessionRepository.Setup(repository => repository.GetCreationResultAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((key, fingerprint) => inputs.Add((key, fingerprint)))
            .ReturnsAsync(new CreatGuestSessionResponse("session", "token"));

        await _guestSessionService.CreateGuestSessionAsync(new("group-1", "Guest") { IdempotencyKey = "same-attempt" });
        await _guestSessionService.CreateGuestSessionAsync(new("group-2", "Guest") { IdempotencyKey = "same-attempt" });
        await _guestSessionService.CreateGuestSessionAsync(new("group-1", "Other guest") { IdempotencyKey = "same-attempt" });
        await _guestSessionService.CreateGuestSessionAsync(new("group-1", "Guest") { IdempotencyKey = "other-attempt" });

        Assert.Equal(inputs[0].key, inputs[1].key);
        Assert.NotEqual(inputs[0].fingerprint, inputs[1].fingerprint);
        Assert.NotEqual(inputs[0].fingerprint, inputs[2].fingerprint);
        Assert.NotEqual(inputs[0].key, inputs[3].key);
        Assert.Equal(inputs[0].fingerprint, inputs[3].fingerprint);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 1)]
    [InlineAutoData(1, 2)]
    [InlineAutoData(2, 2)]
    [InlineAutoData(2, 3)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        _mockGuestSessionRepository
            .Setup(r => r.CreateAtomicallyAsync(It.IsAny<GuestSession>(), parentReservation,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new GuestLimitExceededException());

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.IsOwnerSession = false;
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAuthorizedAsync_CognitoJwtLikeCredential_ReturnsFalseWithoutThrowing()
    {
        bool result = await _guestSessionService.IsAuthorizedAsync("header.payload.signature", "participant-session");

        Assert.False(result);
        _mockGuestSessionRepository.Verify(repository => repository.GetByTokenHash(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IsOwnerAuthorizedAsync_MatchingSessionAndReservationOwner_ReturnsTrue()
    {
        const string userId = "owner-1";
        var session = CreateOwnerSession("owner-session", "group-1", userId);
        var group = CreateGroup("group-1", "reservation-1");
        var reservation = CreateReservation("reservation-1", userId);
        _mockGuestSessionRepository.Setup(repository => repository.GetByGuestSessionId(session.Id)).ReturnsAsync(session);
        _mockGroupReservationRepository.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);

        bool result = await _guestSessionService.IsOwnerAuthorizedAsync(userId, session.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task IsOwnerAuthorizedAsync_SessionBelongsToDifferentOwner_ReturnsFalseBeforeReadingReservation()
    {
        var session = CreateOwnerSession("owner-session", "group-1", "actual-owner");
        _mockGuestSessionRepository.Setup(repository => repository.GetByGuestSessionId(session.Id)).ReturnsAsync(session);

        bool result = await _guestSessionService.IsOwnerAuthorizedAsync("other-user", session.Id);

        Assert.False(result);
        _mockGroupReservationRepository.Verify(
            repository => repository.GetGroupReservationByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IsOwnerAuthorizedAsync_ReservationBelongsToDifferentOwner_ReturnsFalse()
    {
        const string userId = "owner-1";
        var session = CreateOwnerSession("owner-session", "group-1", userId);
        var group = CreateGroup("group-1", "reservation-1");
        _mockGuestSessionRepository.Setup(repository => repository.GetByGuestSessionId(session.Id)).ReturnsAsync(session);
        _mockGroupReservationRepository.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(CreateReservation(group.ReservationId, "actual-reservation-owner"));

        bool result = await _guestSessionService.IsOwnerAuthorizedAsync(userId, session.Id);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Fact]
    public async Task OwnerSession_CartLifecycle_AddUpdateRemoveAndSubmit_PreservesQuantityAndSubmission()
    {
        const string ownerId = "owner-1";
        var session = CreateOwnerSession("owner-session", "group-1", ownerId);
        var group = CreateGroup("group-1", "reservation-1");
        var reservation = CreateReservation("reservation-1", ownerId);
        var table = new Table
        {
            Id = reservation.TableId,
            LocationId = "location-1",
            TableNumber = 1,
            Capacity = 4
        };
        var dish = new Dish
        {
            Id = "dish-1",
            LocationId = table.LocationId,
            State = DishState.Available,
            Price = 12.50m
        };
        _mockGuestSessionRepository.Setup(repository => repository.GetByGuestSessionId(session.Id)).ReturnsAsync(session);
        _mockGroupReservationRepository.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _mockTableRepository.Setup(repository => repository.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(repository => repository.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);
        _mockDishRepository.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(session.Id, dish.Id, 2);
        await _guestSessionService.AddDishAsync(session.Id, dish.Id, 1);
        await _guestSessionService.RemoveDishAsync(session.Id, dish.Id, 1);
        await _guestSessionService.SubmitCartAsync(session.Id);

        Assert.True(session.IsOwnerSession);
        Assert.True(session.IsSubmitted);
        var item = Assert.Single(session.Dishes);
        Assert.Equal(dish.Id, item.DishId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(OrderItemState.Pending, item.State);
        _mockGuestSessionRepository.Verify(repository => repository.SaveAsync(session), Times.Exactly(4));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    private static Reservation CreateReservation(string reservationId, string ownerId) => new()
    {
        Id = reservationId,
        CustomerId = ownerId,
        TableId = "table-1",
        WaiterId = "waiter-1",
        StartTime = DateTime.UtcNow.AddHours(2),
        EndTime = DateTime.UtcNow.AddHours(3),
        GuestCount = 2
    };

    private static GroupReservation CreateGroup(string groupId, string reservationId) => new()
    {
        GroupReservationId = groupId,
        ReservationId = reservationId,
        ReservationToken = "token-1",
        Status = GroupReservationStatus.Open
    };

    private static GuestSession CreateOwnerSession(string sessionId, string groupId, string ownerId) => new()
    {
        Id = sessionId,
        GroupReservationId = groupId,
        GuestName = "Owner",
        OwnerUserId = ownerId,
        IsOwnerSession = true,
        Dishes = []
    };

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }
}





