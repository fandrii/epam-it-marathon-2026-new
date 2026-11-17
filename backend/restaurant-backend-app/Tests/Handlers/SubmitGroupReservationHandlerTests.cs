using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Handlers.Order;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.Orders;
using Moq;
using System.Text.Json;
using static APIHandler.Tests.GroupSubmissionTestData;

namespace APIHandler.Tests.Handlers;

public class SubmitGroupReservationHandlerTests
{
    private readonly Mock<IOrderService> _service = new();
    private readonly ILambdaContext _context;
    private readonly SubmitGroupReservationHandler _handler;

    public SubmitGroupReservationHandlerTests()
    {
        var context = new Mock<ILambdaContext>();
        context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
        _context = context.Object;
        _handler = new(_service.Object);
    }

    [Fact]
    public async Task HandleRequest_OwnerReceivesExactConsolidatedArrayAndCors()
    {
        _service.Setup(value => value.SubmitGroupReservationAsync(OwnerId, GroupId, ReservationId, _context))
            .ReturnsAsync(new ConsolidatedDishResponse[] { new("dish1", 3), new("dish2", 2) });

        var response = await _handler.HandleRequest(Request(), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("[{\"dishId\":\"dish1\",\"quantity\":3},{\"dishId\":\"dish2\",\"quantity\":2}]", response.Body);
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        Assert.Equal("*", response.Headers["Access-Control-Allow-Origin"]);
        using var json = JsonDocument.Parse(response.Body);
        Assert.All(json.RootElement.EnumerateArray(), item =>
            Assert.Equal(new[] { "dishId", "quantity" }, item.EnumerateObject().Select(property => property.Name)));
        _service.Verify(value => value.SubmitGroupReservationAsync(OwnerId, GroupId, ReservationId, _context), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"reservationId\":null}")]
    [InlineData("{\"reservationId\":\" \"}")]
    public async Task HandleRequest_InvalidBody_Returns400WithoutSubmitting(string? body)
    {
        var request = Request();
        request.Body = body;
        var response = await _handler.HandleRequest(request, _context);
        Assert.Equal(400, response.StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingGroupPath_Returns400(string? groupId)
    {
        var request = Request();
        request.PathParameters = groupId is null ? null : new Dictionary<string, string> { ["groupReservationId"] = groupId };
        Assert.Equal(400, (await _handler.HandleRequest(request, _context)).StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("no-context")]
    [InlineData("no-authorizer")]
    [InlineData("no-claims")]
    [InlineData("blank-sub")]
    public async Task HandleRequest_MissingCognitoIdentity_Returns401(string scenario)
    {
        var request = Request();
        switch (scenario)
        {
            case "no-context": request.RequestContext = null; break;
            case "no-authorizer": request.RequestContext.Authorizer = null; break;
            case "no-claims": request.RequestContext.Authorizer.Claims = new(); break;
            case "blank-sub": request.RequestContext.Authorizer.Claims["sub"] = " "; break;
        }
        Assert.Equal(401, (await _handler.HandleRequest(request, _context)).StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("forbidden", 403)]
    [InlineData("reservation", 404)]
    [InlineData("group", 404)]
    [InlineData("validation", 400)]
    public async Task HandleRequest_DomainFailure_ReturnsExpectedHttpError(string scenario, int status)
    {
        Exception exception = scenario switch
        {
            "forbidden" => new ActionForbiddenException("Not the owner"),
            "reservation" => new ReservationNotFoundException("Missing"),
            "group" => new GroupReservationNotFoundException(),
            _ => new ValidationException("Invalid submission")
        };
        _service.Setup(value => value.SubmitGroupReservationAsync(OwnerId, GroupId, ReservationId, _context)).ThrowsAsync(exception);
        var response = await _handler.HandleRequest(Request(), _context);
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(exception.Message, json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LegacySubmitHandler_UsesLegacyServiceAndKeepsOriginalResponse()
    {
        var request = Request();
        request.PathParameters = new Dictionary<string, string> { ["reservationId"] = ReservationId };
        var response = await new SubmitOrderHandler(_service.Object).HandleRequest(request, _context);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("\"Pre-order submitted successfully.\"", response.Body);
        _service.Verify(value => value.SubmitPreOrderAsync(OwnerId, ReservationId, _context), Times.Once);
        _service.Verify(value => value.SubmitGroupReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILambdaContext>()), Times.Never);
    }

    private static APIGatewayProxyRequest Request() => new()
    {
        Body = "{\"reservationId\":\"reservation-1\"}",
        PathParameters = new Dictionary<string, string> { ["groupReservationId"] = GroupId },
        RequestContext = new() { Authorizer = new() { Claims = new() { ["sub"] = OwnerId } } }
    };
}
