using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.Locations;
using APIHandler.Services.Locations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class RemoveDishFromLocationHandlerTests
{
    private readonly Mock<ILocationService> _locationService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly RemoveDishFromLocationHandler _handler;

    public RemoveDishFromLocationHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new RemoveDishFromLocationHandler(_locationService.Object);
    }

    [Fact]
    public async Task HandleRequest_ShouldRemoveDishFromLocation_WhenWaiterProvidesLocationIdAndDishId()
    {
        const string locationId = "location-123";
        const string dishId = "dish-456";

        var response = await _handler.HandleRequest(CreateRequest(locationId, dishId), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(locationId, response.Body);
        Assert.Contains(dishId, response.Body);
        _locationService.Verify(service => service.RemoveDishFromLocationAsync(locationId, dishId), Times.Once);
    }

    [Theory]
    [InlineData(null, "dish-456")]
    [InlineData("location-123", null)]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenPathParameterIsMissing(string? locationId, string? dishId)
    {
        var response = await _handler.HandleRequest(CreateRequest(locationId, dishId), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        using var responseBody = JsonDocument.Parse(response.Body);
        Assert.Equal("Missing 'id' or 'dishId' in path parameters.", responseBody.RootElement.GetProperty("error").GetString());
        _locationService.Verify(service => service.RemoveDishFromLocationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenLocationServiceRejectsRemoval()
    {
        const string locationId = "location-123";
        const string dishId = "dish-456";
        _locationService.Setup(service => service.RemoveDishFromLocationAsync(locationId, dishId))
            .ThrowsAsync(new ValidationException("Dish is not associated with this location."));

        var response = await _handler.HandleRequest(CreateRequest(locationId, dishId), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Dish is not associated with this location.", response.Body);
        _locationService.Verify(service => service.RemoveDishFromLocationAsync(locationId, dishId), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnForbidden_WhenRequestIsNotFromWaiter()
    {
        var response = await _handler.HandleRequest(CreateRequest("location-123", "dish-456", "Customer"), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        _locationService.Verify(service => service.RemoveDishFromLocationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static APIGatewayProxyRequest CreateRequest(string? locationId, string? dishId, string role = "Waiter")
    {
        var pathParameters = new Dictionary<string, string>();
        if (locationId is not null)
            pathParameters["id"] = locationId;
        if (dishId is not null)
            pathParameters["dishId"] = dishId;

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