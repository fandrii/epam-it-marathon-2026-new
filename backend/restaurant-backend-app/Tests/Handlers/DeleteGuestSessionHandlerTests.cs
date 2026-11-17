using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class DeleteGuestSessionHandlerTests
{
    private readonly Mock<IGuestSessionService> _service = new();
    private readonly ILambdaContext _context;

    public DeleteGuestSessionHandlerTests()
    {
        var context = new Mock<ILambdaContext>();
        context.SetupGet(x => x.Logger).Returns(Mock.Of<ILambdaLogger>());
        _context = context.Object;
    }

    [Fact]
    public async Task OwnerDeletion_ReturnsExactGroupSnapshotShape()
    {
        _service.Setup(x => x.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1"))
            .ReturnsAsync(new GetGroupSessionsResponse(
            [
                new GroupSessionParticipant("session-2", ParticipantType.Guest, "Olena",
                [new GroupSessionOrderItemResponse(new GroupSessionDishResponse("dish-2", "Tom Yum", 220), 1)],
                220, false)
            ], 220));

        var response = await new DeleteGuestSessionHandler(_service.Object).HandleRequest(Request(), _context);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(220m, json.RootElement.GetProperty("total").GetDecimal());
        var participant = json.RootElement.GetProperty("participants")[0];
        Assert.Equal("Guest", participant.GetProperty("type").GetString());
        Assert.Equal("Tom Yum", participant.GetProperty("items")[0].GetProperty("dish").GetProperty("name").GetString());
    }

    [Fact]
    public async Task MissingIdentity_Returns401WithoutCallingService()
    {
        var request = Request();
        request.RequestContext.Authorizer = null;
        Assert.Equal(401, (await new DeleteGuestSessionHandler(_service.Object).HandleRequest(request, _context)).StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("forbidden", 403)]
    [InlineData("missing", 404)]
    [InlineData("invalid", 400)]
    public async Task DomainErrors_MapToRequiredStatus(string scenario, int status)
    {
        Exception error = scenario switch
        {
            "forbidden" => new ActionForbiddenException("Owner only"),
            "missing" => new GuestSessionNotFoundException("Missing"),
            _ => new ValidationException("Wrong group")
        };
        _service.Setup(x => x.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1")).ThrowsAsync(error);
        Assert.Equal(status, (await new DeleteGuestSessionHandler(_service.Object).HandleRequest(Request(), _context)).StatusCode);
    }

    private static APIGatewayProxyRequest Request() => new()
    {
        PathParameters = new Dictionary<string, string> { ["groupReservationId"] = "group-1", ["guestSessionId"] = "guest-1" },
        RequestContext = new() { Authorizer = new() { Claims = new Dictionary<string, string> { ["sub"] = "owner-1" } } }
    };
}
