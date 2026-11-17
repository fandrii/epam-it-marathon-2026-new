using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Helpers;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Security.Claims;

namespace APIHandler.Tests.Services;

public class GuestAuthorizationServiceTests
{
    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ICognitoIdTokenValidator> _cognitoTokenValidator = new();
    private readonly Mock<ILambdaContext> _context = new();

    public GuestAuthorizationServiceTests()
    {
        _context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
    }

    [Fact]
    public async Task ValidateAsync_OwnerSessionWithMatchingCognitoOwner_AllowsRequest()
    {
        const string sessionId = "owner-session";
        const string token = "owner-jwt";
        _guestSessionService.Setup(service => service.IsOwnerSessionAsync(sessionId)).ReturnsAsync(true);
        _cognitoTokenValidator.Setup(validator => validator.ValidateAsync(token))
            .ReturnsAsync(new ClaimsIdentity([new Claim("sub", "owner-1")]));
        _guestSessionService.Setup(service => service.IsOwnerAuthorizedAsync("owner-1", sessionId)).ReturnsAsync(true);

        var result = await CreateService().ValidateAsync(CreateRequest(token), _context.Object, sessionId);

        Assert.Null(result);
        _guestSessionService.Verify(service => service.IsAuthorizedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_OwnerSessionWithDifferentCognitoUser_ReturnsForbidden()
    {
        const string sessionId = "owner-session";
        _guestSessionService.Setup(service => service.IsOwnerSessionAsync(sessionId)).ReturnsAsync(true);
        _cognitoTokenValidator.Setup(validator => validator.ValidateAsync("other-user-jwt"))
            .ReturnsAsync(new ClaimsIdentity([new Claim("sub", "other-user")]));
        _guestSessionService.Setup(service => service.IsOwnerAuthorizedAsync("other-user", sessionId)).ReturnsAsync(false);

        var result = await CreateService().ValidateAsync(CreateRequest("other-user-jwt"), _context.Object, sessionId);

        Assert.NotNull(result);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task ValidateAsync_OwnerSessionWithGuestToken_ReturnsUnauthorized()
    {
        const string sessionId = "owner-session";
        _guestSessionService.Setup(service => service.IsOwnerSessionAsync(sessionId)).ReturnsAsync(true);
        _cognitoTokenValidator.Setup(validator => validator.ValidateAsync("guest-token"))
            .ReturnsAsync((ClaimsIdentity?)null);

        var result = await CreateService().ValidateAsync(CreateRequest("guest-token"), _context.Object, sessionId);

        Assert.NotNull(result);
        Assert.Equal(401, result.StatusCode);
        _guestSessionService.Verify(service => service.IsAuthorizedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_RegularGuestSessionWithMatchingGuestToken_PreservesGuestAuthorization()
    {
        const string sessionId = "guest-session";
        const string token = "guest-token";
        _guestSessionService.Setup(service => service.IsOwnerSessionAsync(sessionId)).ReturnsAsync(false);
        _guestSessionService.Setup(service => service.IsAuthorizedAsync(token, sessionId)).ReturnsAsync(true);

        var result = await CreateService().ValidateAsync(CreateRequest(token), _context.Object, sessionId);

        Assert.Null(result);
        _cognitoTokenValidator.Verify(validator => validator.ValidateAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_CognitoOwnerCannotAccessParticipantSession()
    {
        const string sessionId = "participant-session";
        _guestSessionService.Setup(service => service.IsOwnerSessionAsync(sessionId)).ReturnsAsync(false);
        _guestSessionService.Setup(service => service.IsAuthorizedAsync("owner-jwt", sessionId)).ReturnsAsync(false);

        var result = await CreateService().ValidateAsync(CreateRequest("owner-jwt"), _context.Object, sessionId);

        Assert.NotNull(result);
        Assert.Equal(401, result.StatusCode);
        _cognitoTokenValidator.Verify(validator => validator.ValidateAsync(It.IsAny<string>()), Times.Never);
    }

    private GuestAuthorizationService CreateService() =>
        new(_guestSessionService.Object, _cognitoTokenValidator.Object);

    [Fact]
    public async Task ValidateAsync_LowercaseAuthorizationHeader_PreservesGuestTokenAuthentication()
    {
        _guestSessionService.Setup(service => service.IsAuthorizedAsync("guest-token", "guest-session")).ReturnsAsync(true);
        var request = new APIGatewayProxyRequest
        {
            Headers = new Dictionary<string, string> { ["authorization"] = "bearer guest-token" }
        };

        Assert.Null(await CreateService().ValidateAsync(request, _context.Object, "guest-session"));
    }

    [Fact]
    public async Task ValidateAsync_MissingHeaders_ReturnsUnauthorized()
    {
        var response = await CreateService().ValidateAsync(new APIGatewayProxyRequest { Headers = null }, _context.Object, "guest-session");
        Assert.Equal(401, response!.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(string token) => new()
    {
        Headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}"
        }
    };
}
