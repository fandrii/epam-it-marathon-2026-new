using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Entities;
using APIHandler.Handlers.Reservations;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetReservationsForUserHandlerTests
{
    private const string OwnerId = "owner-user-id";
    private const string ReservationId = "reservation-id";
    private const string GroupReservationId = "group-reservation-id";

    private readonly Mock<IReservationRepository> _reservationRepository = new();
    private readonly Mock<ILocationService> _locationService = new();
    private readonly Mock<IOrderService> _orderService = new();
    private readonly Mock<IGroupReservationRepository> _groupReservationRepository = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly GetReservationsForUserHandler _handler;

    public GetReservationsForUserHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _locationService
            .Setup(service => service.GetLocationByTableIdAsync("table-id"))
            .ReturnsAsync(new Location
            {
                Id = "location-id",
                Address = "1 Main Street",
                Description = "Restaurant",
                AverageOccupancy = "0"
            });
        _orderService
            .Setup(service => service.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync((Order?)null);
        _groupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = GroupReservationId,
                ReservationId = ReservationId,
                ReservationToken = "token",
                Status = GroupReservationStatus.Open
            });

        _handler = new GetReservationsForUserHandler(
            _reservationRepository.Object,
            _locationService.Object,
            _orderService.Object,
            _groupReservationRepository.Object);
    }

    [Fact]
    public async Task HandleRequest_CustomerOwnerReceivesGroupReservationIdOnOwnedReservation()
    {
        var reservation = CreateReservation(customerId: OwnerId);
        _reservationRepository
            .Setup(repository => repository.GetReservationsByCustomerIdAsync(OwnerId))
            .ReturnsAsync([reservation]);
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(reservation);

        var response = await _handler.HandleRequest(CreateRequest(OwnerId), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body!);
        var ownerReservation = document.RootElement[0];
        Assert.Equal(GroupReservationId, ownerReservation.GetProperty("groupReservationId").GetString());
        Assert.True(ownerReservation.GetProperty("guestInviteLinkAvailable").GetBoolean());
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(ReservationId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_DoesNotExposeGroupReservationIdWhenCallerIsNotReservationOwner()
    {
        var reservation = CreateReservation(customerId: "different-user-id");
        _reservationRepository
            .Setup(repository => repository.GetReservationsByCustomerIdAsync(OwnerId))
            .ReturnsAsync([reservation]);
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(reservation);

        var response = await _handler.HandleRequest(CreateRequest(OwnerId), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body!);
        Assert.False(document.RootElement[0].TryGetProperty("groupReservationId", out _));
    }

    [Fact]
    public async Task HandleRequest_DoesNotExposeGroupReservationIdInWaiterReservations()
    {
        var reservation = CreateReservation(customerId: OwnerId);
        _reservationRepository
            .Setup(repository => repository.GetReservationsByWaiterIdAsync("waiter-id"))
            .ReturnsAsync([reservation]);

        const string reservationsTableEnvironmentVariable = "reservationsTable";
        const string waiterIndexEnvironmentVariable = "reservationsWaiterIdGlobalIndex";
        var previousReservationsTable = Environment.GetEnvironmentVariable(reservationsTableEnvironmentVariable);
        var previousWaiterIndex = Environment.GetEnvironmentVariable(waiterIndexEnvironmentVariable);
        Environment.SetEnvironmentVariable(reservationsTableEnvironmentVariable, "reservations-table");
        Environment.SetEnvironmentVariable(waiterIndexEnvironmentVariable, "reservations-waiter-index");
        try
        {
            var response = await _handler.HandleRequest(CreateRequest("waiter-id", "Waiter"), _lambdaContext.Object);

            Assert.Equal(200, response.StatusCode);
            using var document = JsonDocument.Parse(response.Body!);
            Assert.False(document.RootElement[0].TryGetProperty("groupReservationId", out _));
            _groupReservationRepository.Verify(
                repository => repository.GetByReservationIdAsync(ReservationId),
                Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable(reservationsTableEnvironmentVariable, previousReservationsTable);
            Environment.SetEnvironmentVariable(waiterIndexEnvironmentVariable, previousWaiterIndex);
        }
    }

    private static Reservation CreateReservation(string customerId) => new()
    {
        Id = ReservationId,
        TableId = "table-id",
        CustomerId = customerId,
        WaiterId = "waiter-id",
        StartTime = DateTime.UtcNow.AddDays(1),
        EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
        GuestCount = 3,
        Status = APIHandler.Constants.ReservationStatus.Reserved
    };

    private static APIGatewayProxyRequest CreateRequest(string userId, string role = "Customer") => new()
    {
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = new Dictionary<string, string>
                {
                    ["sub"] = userId,
                    ["cognito:groups"] = $"[\"{role}\"]"
                }
            }
        }
    };
}
