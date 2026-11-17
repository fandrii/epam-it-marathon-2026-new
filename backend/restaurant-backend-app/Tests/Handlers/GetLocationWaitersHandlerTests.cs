using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Handlers.Waiters;
using APIHandler.Models.WaiterList;
using APIHandler.Services.Waiters;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetLocationWaitersHandlerTests
{
    private readonly Mock<IWaiterService> _waiterService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly GetLocationWaitersHandler _handler;

    public GetLocationWaitersHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new GetLocationWaitersHandler(_waiterService.Object);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnWaitersForLocation_WhenWaiterRoleAndLocationIdAreProvided()
    {
        const string locationId = "location-123";
        var expectedWaiters = new List<WaiterListItem>
        {
            new()
            {
                UserId = "waiter-1",
                LocationId = locationId,
                CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Reservations = ["reservation-1"]
            },
            new()
            {
                UserId = "waiter-2",
                LocationId = locationId,
                CreatedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                Reservations = []
            }
        };
        _waiterService.Setup(service => service.GetWaitersByLocationIdAsync(locationId))
            .ReturnsAsync(expectedWaiters);

        var response = await _handler.HandleRequest(CreateRequest(locationId), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        var actualWaiters = JsonSerializer.Deserialize<List<WaiterListItem>>(
            response.Body!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(actualWaiters);
        Assert.Equal(expectedWaiters.Count, actualWaiters.Count);
        Assert.Equal(expectedWaiters[0].UserId, actualWaiters[0].UserId);
        Assert.Equal(expectedWaiters[0].LocationId, actualWaiters[0].LocationId);
        Assert.Equal(expectedWaiters[0].CreatedAt, actualWaiters[0].CreatedAt);
        Assert.Equal(expectedWaiters[0].Reservations, actualWaiters[0].Reservations);
        Assert.Equal(expectedWaiters[1].UserId, actualWaiters[1].UserId);
        Assert.Equal(expectedWaiters[1].LocationId, actualWaiters[1].LocationId);
        Assert.Equal(expectedWaiters[1].CreatedAt, actualWaiters[1].CreatedAt);
        Assert.Equal(expectedWaiters[1].Reservations, actualWaiters[1].Reservations);
        _waiterService.Verify(service => service.GetWaitersByLocationIdAsync(locationId), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenPathParametersAreMissing()
    {
        var request = CreateRequest(locationId: null);
        request.PathParameters = null;

        var response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _waiterService.Verify(service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenLocationIdIsMissing()
    {
        var request = CreateRequest(locationId: null);

        var response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _waiterService.Verify(service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenLocationIdIsBlank(string locationId)
    {
        var response = await _handler.HandleRequest(CreateRequest(locationId), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _waiterService.Verify(service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnForbidden_WhenRequestIsNotFromWaiter()
    {
        var response = await _handler.HandleRequest(CreateRequest("location-123", "Customer"), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        _waiterService.Verify(service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()), Times.Never);
    }

    private static APIGatewayProxyRequest CreateRequest(string? locationId, string role = "Waiter")
    {
        var pathParameters = locationId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["id"] = locationId };

        return new APIGatewayProxyRequest
        {
            PathParameters = pathParameters,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["cognito:groups"] = $"[{JsonSerializer.Serialize(role)}]" }
                }
            }
        };
    }
}
