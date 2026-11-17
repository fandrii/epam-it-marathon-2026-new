using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.Orders;
using System;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GroupReservations;

public class SubmitGroupReservationHandler(IOrderService orderService) : IApiRequestHandler
{
    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (request?.RequestContext is null)
                throw new UnauthorizedAccessException("Unauthorized.");

            var customerId = UserContextHelper.GetAuthenticatedUserId(request)
                ?? throw new UnauthorizedAccessException("Unauthorized.");

            if (request.PathParameters is null ||
                !request.PathParameters.TryGetValue("groupReservationId", out var groupReservationId) ||
                string.IsNullOrWhiteSpace(groupReservationId))
                throw new ValidationException("Group reservation ID is required.");

            if (!RequestBodyHelper.TryParseAndValidate<SubmitGroupReservationRequest>(
                request, context, out var body, out var errorResponse))
                return errorResponse;

            var response = await orderService.SubmitGroupReservationAsync(
                customerId, groupReservationId, body.ReservationId, context);
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
        catch (GroupReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ValidationException exception)
        {
            return ResponseBuilder.CreateErrorResponse(400, exception.Message, context.Logger);
        }
    }
}
