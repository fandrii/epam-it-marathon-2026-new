using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Services.GuestSessions;
using System;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GuestSessions;

public class GetOwnerGuestSessionIdHandler(IGuestSessionService guestSessionService) : IApiRequestHandler
{
    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (request.PathParameters is null ||
                !request.PathParameters.TryGetValue("reservationId", out var reservationId) ||
                string.IsNullOrWhiteSpace(reservationId))
            {
                throw new ValidationException("Reservation ID is required.");
            }

            var userId = UserContextHelper.GetAuthenticatedUserId(request)
                ?? throw new UnauthorizedAccessException("Unauthorized.");

            var response = await guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);
            return ResponseBuilder.CreateSuccessResponse(response, 200);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ResponseBuilder.CreateErrorResponse(401, exception.Message, context.Logger);
        }
        catch (ActionForbiddenException exception)
        {
            return ResponseBuilder.CreateErrorResponse(403, exception.Message, context.Logger);
        }
        catch (ReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (GuestSessionNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ValidationException exception)
        {
            return ResponseBuilder.CreateErrorResponse(400, exception.Message, context.Logger);
        }
    }
}
