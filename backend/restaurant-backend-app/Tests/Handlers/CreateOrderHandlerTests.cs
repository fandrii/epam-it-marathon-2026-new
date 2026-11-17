using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Dishes;
using APIHandler.Entities;
using APIHandler.Handlers.Order;
using APIHandler.Services.Dishes;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using APIHandler.Services.Reservations;
using Moq;

namespace APIHandler.Tests.Handlers;

public class CreateOrderHandlerTests
{
    private const string OwnerId = "owner-user-id";
    private const string ReservationId = "reservation-id";
    private const string DishId = "dish-id";

    private readonly Mock<IDishService> _dishService = new();
    private readonly Mock<IReservationService> _reservationService = new();
    private readonly Mock<ILocationService> _locationService = new();
    private readonly Mock<IOrderService> _orderService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly CreateOrderHandler _handler;

    public CreateOrderHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _reservationService
            .Setup(service => service.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-id",
                CustomerId = OwnerId,
                WaiterId = "waiter-id",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
                GuestCount = 3,
                Status = ReservationStatus.Reserved
            });
        _locationService
            .Setup(service => service.GetLocationByTableIdAsync("table-id"))
            .ReturnsAsync(new Location
            {
                Id = "location-id",
                Address = "1 Main Street",
                Description = "Restaurant",
                AverageOccupancy = "0"
            });
        _dishService
            .Setup(service => service.GetDishByIdAsync(DishId))
            .ReturnsAsync(new Dish
            {
                Id = DishId,
                Name = "Soup",
                State = DishState.Available,
                Price = 4.50m
            });
        _orderService
            .Setup(service => service.AddOrderAsync(It.IsAny<Order>()))
            .Returns(Task.CompletedTask);

        _handler = new CreateOrderHandler(
            _dishService.Object,
            _reservationService.Object,
            _locationService.Object,
            _orderService.Object);
    }

    [Fact]
    public async Task HandleRequest_OwnerCanCreatePreOrderWithFlatDishItems()
    {
        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{DishId}}","quantity":2}]}"""),
            _lambdaContext.Object);

        Assert.Equal(201, response.StatusCode);
        _dishService.Verify(service => service.GetDishByIdAsync(DishId), Times.AtLeastOnce);
        _orderService.Verify(
            service => service.AddOrderAsync(It.Is<Order>(order =>
                order.CustomerId == OwnerId &&
                order.ReservationId == ReservationId &&
                order.Dishes.Count == 1 &&
                order.Dishes[0].DishId == DishId &&
                order.Dishes[0].Quantity == 2)),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_RejectsUnavailableDishWithFlatDishItems()
    {
        _dishService
            .Setup(service => service.GetDishByIdAsync(DishId))
            .ReturnsAsync(new Dish
            {
                Id = DishId,
                Name = "Soup",
                State = DishState.OutOfStock,
                Price = 4.50m
            });

        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{DishId}}","quantity":1}]}"""),
            _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _orderService.Verify(service => service.AddOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_RejectsOrderForAnotherUsersReservation()
    {
        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{DishId}}","quantity":1}]}""",
                userId: "different-user-id"),
            _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        _orderService.Verify(service => service.AddOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_RejectsPreOrderForReservationOutsideReservedStatus()
    {
        _reservationService
            .Setup(service => service.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-id",
                CustomerId = OwnerId,
                WaiterId = "waiter-id",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
                GuestCount = 3,
                Status = ReservationStatus.InProgress
            });

        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{DishId}}","quantity":1}]}"""),
            _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _orderService.Verify(service => service.AddOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_RejectsWaiterRoleWhenCreatingAnOwnerPreOrder()
    {
        _reservationService
            .Setup(service => service.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-id",
                CustomerId = OwnerId,
                WaiterId = "waiter-id",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
                GuestCount = 3,
                Status = ReservationStatus.InProgress
            });

        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{DishId}}","quantity":1}]}""",
                role: "Waiter"),
            _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        _dishService.Verify(service => service.GetDishByIdAsync(DishId), Times.Once);
        _orderService.Verify(service => service.AddOrderAsync(It.IsAny<Order>()), Times.Never);
        _orderService.Verify(service => service.AddOrderForWaiterAsync(It.IsAny<Order>()), Times.Never);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData(DishId, 0)]
    public async Task HandleRequest_RejectsFlatOrderItemsWithInvalidDishIdOrQuantity(string dishId, int quantity)
    {
        var response = await _handler.HandleRequest(
            CreateOwnerRequest(
                $$"""{"reservationId":"{{ReservationId}}","orderItems":[{"dishId":"{{dishId}}","quantity":{{quantity}}}]}"""),
            _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _dishService.Verify(service => service.GetDishByIdAsync(It.IsAny<string>()), Times.Never);
        _orderService.Verify(service => service.AddOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    private static APIGatewayProxyRequest CreateOwnerRequest(
        string body,
        string userId = OwnerId,
        string role = "Customer") => new()
    {
        Body = body,
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
