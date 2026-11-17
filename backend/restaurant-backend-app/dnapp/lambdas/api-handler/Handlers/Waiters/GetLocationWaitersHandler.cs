using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Helpers;
using APIHandler.Services.Waiters;
using System.Threading.Tasks;

namespace APIHandler.Handlers.Waiters;

public class GetLocationWaitersHandler(IWaiterService waiterService) : IApiRequestHandler
{
    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!RoleHelper.IsWaiter(request))
            return ResponseBuilder.CreateErrorResponse(403, "Forbidden", context.Logger);

        if (request.PathParameters == null ||
            !request.PathParameters.TryGetValue("id", out var locationId) ||
            string.IsNullOrWhiteSpace(locationId))
        {
            return ResponseBuilder.CreateErrorResponse(400, "Missing or invalid 'locationId'", context.Logger);
        }

        var waiters = await waiterService.GetWaitersByLocationIdAsync(locationId);
        return ResponseBuilder.CreateSuccessResponse(waiters);
    }
}
