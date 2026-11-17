using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Services.Orders;

public partial class OrderService
{
    public async Task<IReadOnlyList<ConsolidatedDishResponse>> SubmitGroupReservationAsync(
        string customerId, string groupReservationId, string reservationId, ILambdaContext context)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new UnauthorizedAccessException("Unauthorized.");
        if (string.IsNullOrWhiteSpace(groupReservationId) || string.IsNullOrWhiteSpace(reservationId))
            throw new ValidationException("Group reservation ID and reservation ID are required.");

        var reservation = await LoadReservationAsync(reservationId);
        if (!string.Equals(reservation.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
            throw new ActionForbiddenException("You are not authorized to submit this group pre-order.");

        var group = await _groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId)
            ?? throw new GroupReservationNotFoundException();
        if (!string.Equals(group.ReservationId, reservation.Id, StringComparison.Ordinal))
            throw new ValidationException("The group reservation does not belong to the specified reservation.");
        if (reservation.Status != ReservationStatus.Reserved)
            throw new ValidationException("Group pre-orders can be submitted only for reserved reservations.");
        EnsurePreOrderEditable(reservation.StartTime);
        if (group.Status != GroupReservationStatus.Open)
            throw new ValidationException("This group pre-order has already been submitted or is closed.");

        var sessions = await _guestSessionRepository.GetForGroupSubmissionAsync(groupReservationId);
        var submitted = sessions.Where(session => session.IsSubmitted).ToList();
        if (submitted.Count == 0)
            throw new ValidationException("No submitted guest sessions were found.");

        var consolidated = MergeDishQuantities(submitted.SelectMany(session => session.Dishes ?? []), preserveState: false);
        if (consolidated.Count == 0)
            throw new ValidationException("Submitted guest sessions contain no dishes.");

        var snapshot = await _orderRepository.GetOrderForGroupSubmissionAsync(reservation.Id)
            ?? throw new ValidationException("No existing order was found for this reservation.");
        var order = snapshot.Order;
        if (!string.Equals(order.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
            throw new ActionForbiddenException("The existing order does not belong to the reservation owner.");
        if (!string.Equals(order.ReservationId, reservation.Id, StringComparison.Ordinal))
            throw new ValidationException("The existing order does not belong to this reservation.");
        if (order.Status != OrderStatus.Created)
            throw new ValidationException("Only an existing order in Created status can receive the group pre-order.");

        var merged = MergeDishQuantities(order.Dishes ?? [], preserveState: true);
        foreach (var item in consolidated)
        {
            if (merged.TryGetValue(item.Key, out var existing))
            {
                if (existing.State != OrderItemState.Pending)
                    throw new ValidationException("Consolidated quantities cannot be added to a dish that has already been confirmed or canceled.");
                existing.Quantity = AddQuantities(existing.Quantity, item.Value.Quantity);
            }
            else
                merged.Add(item.Key, new OrderItem
                {
                    DishId = item.Key,
                    Quantity = item.Value.Quantity,
                    State = OrderItemState.Pending
                });
        }

        await _orderRepository.SubmitExistingGroupOrderAtomicallyAsync(
            snapshot, merged.Values.ToList(), group, reservation);
        context.Logger.LogLine($"Submitted group pre-order {groupReservationId} into existing order {order.OrderId}.");
        return consolidated.Values.Select(item => new ConsolidatedDishResponse(item.DishId, item.Quantity)).ToArray();
    }

    private static Dictionary<string, OrderItem> MergeDishQuantities(IEnumerable<OrderItem> items, bool preserveState)
    {
        var result = new Dictionary<string, OrderItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.DishId) || item.Quantity <= 0)
                throw new ValidationException("Order dishes must have a dish ID and a positive quantity.");
            if (result.TryGetValue(item.DishId, out var existing))
            {
                if (preserveState && existing.State != item.State)
                    throw new ValidationException("Existing duplicate dish entries have conflicting states.");
                existing.Quantity = AddQuantities(existing.Quantity, item.Quantity);
            }
            else
            {
                result.Add(item.DishId, new OrderItem
                {
                    DishId = item.DishId,
                    Quantity = item.Quantity,
                    State = preserveState ? item.State : OrderItemState.Pending
                });
            }
        }
        return result;
    }

    private static int AddQuantities(int left, int right)
    {
        try { return checked(left + right); }
        catch (OverflowException) { throw new ValidationException("The consolidated dish quantity is too large."); }
    }
}
