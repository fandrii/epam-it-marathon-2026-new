using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Entities;
using APIHandler.Handlers.Tables.Management;
using APIHandler.Services.Tables;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetLocationTablesHandlerTests
{
    private readonly Mock<ITableService> _tableService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly GetLocationTablesHandler _handler;

    public GetLocationTablesHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new GetLocationTablesHandler(_tableService.Object);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnTablesForLocation_WhenWaiterProvidesLocationId()
    {
        const string locationId = "location-123";
        var expectedTables = new List<Table>
        {
            new() { Id = "table-1", LocationId = locationId, Capacity = 4, TableNumber = 1 }
        };
        _tableService.Setup(service => service.GetAllTablesForLocationAsync(locationId))
            .ReturnsAsync(expectedTables);

        var response = await _handler.HandleRequest(CreateRequest(locationId), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        var actualTables = JsonSerializer.Deserialize<List<Table>>(response.Body!);
        Assert.NotNull(actualTables);
        Assert.Equal(expectedTables.Select(table => table.Id), actualTables.Select(table => table.Id));
        _tableService.Verify(service => service.GetAllTablesForLocationAsync(locationId), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnBadRequest_WhenLocationIdIsMissing()
    {
        var response = await _handler.HandleRequest(CreateRequest(null), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        _tableService.Verify(service => service.GetAllTablesForLocationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnForbidden_WhenRequestIsNotFromWaiter()
    {
        var response = await _handler.HandleRequest(CreateRequest("location-123", "Customer"), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        _tableService.Verify(service => service.GetAllTablesForLocationAsync(It.IsAny<string>()), Times.Never);
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