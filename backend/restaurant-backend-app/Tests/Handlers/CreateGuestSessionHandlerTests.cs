using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Handlers;

public class CreateGuestSessionHandlerTests
{
    [Fact]
    public async Task ConflictingIdempotencyKey_Returns409()
    {
        var service = new Mock<IGuestSessionService>();
        service.Setup(value => value.CreateGuestSessionAsync(It.IsAny<CreateGuestSessionRequest>()))
            .ThrowsAsync(new GuestSessionCreationConflictException("Key conflict"));
        var context = new Mock<ILambdaContext>();
        context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
        var response = await new CreateGuestSessionHandler(service.Object).HandleRequest(new APIGatewayProxyRequest
        {
            Body = "{\"groupReservationId\":\"group-1\",\"guestName\":\"Guest\",\"idempotencyKey\":\"attempt-1\"}"
        }, context.Object);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("Key conflict", response.Body);
    }
}
