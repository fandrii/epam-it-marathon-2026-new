using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Services.GuestSessions;

public class GuestAuthorizationService(
    IGuestSessionService guestSessionService,
    ICognitoIdTokenValidator cognitoIdTokenValidator) : IGuestAuthorizationService
{
    public async ValueTask<APIGatewayProxyResponse?> ValidateAsync(APIGatewayProxyRequest request, ILambdaContext context, string guestSessionid)
    {
        var authorizationValue = request.Headers?.FirstOrDefault(header =>
            string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(authorizationValue))
            return CreateErrorResponse(context);

        const string BearerString = "Bearer ";
        if (!authorizationValue.StartsWith(BearerString, StringComparison.OrdinalIgnoreCase) || authorizationValue.Length <= BearerString.Length)
            return CreateErrorResponse(context);

        string token = authorizationValue[BearerString.Length..];

        if (await guestSessionService.IsOwnerSessionAsync(guestSessionid))
        {
            var identity = await cognitoIdTokenValidator.ValidateAsync(token);
            var userId = identity?.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
                return CreateOwnerAuthenticationErrorResponse(context);

            bool ownerAuthorized = await guestSessionService.IsOwnerAuthorizedAsync(userId, guestSessionid);
            return !ownerAuthorized ? CreateOwnerAuthorizationErrorResponse(context) : null;
        }

        bool authorized = await guestSessionService.IsAuthorizedAsync(token, guestSessionid);

        return !authorized ? CreateErrorResponse(context) : null;
    }

    private static APIGatewayProxyResponse CreateErrorResponse(ILambdaContext context)
        => ResponseBuilder.CreateErrorResponse(401, "Invalid guest session token", context.Logger);

    private static APIGatewayProxyResponse CreateOwnerAuthenticationErrorResponse(ILambdaContext context)
        => ResponseBuilder.CreateErrorResponse(401, "A valid Cognito owner token is required.", context.Logger);

    private static APIGatewayProxyResponse CreateOwnerAuthorizationErrorResponse(ILambdaContext context)
        => ResponseBuilder.CreateErrorResponse(403, "Only the reservation owner can access this owner session.", context.Logger);
}
