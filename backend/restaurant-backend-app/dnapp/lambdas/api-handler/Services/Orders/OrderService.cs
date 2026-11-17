using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Services.Dishes;
using APIHandler.Services.Notifications;
using APIHandler.Services.Locations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Services.Orders;

public partial class OrderService : IOrderService
{
    private readonly IOrderRepository _orderRepository;
    private readonly IReservationRepository _reservationRepository;
    private readonly IDishService _dishService;
    private readonly INotificationService _notificationService;
    private readonly IGroupReservationRepository _groupReservationRepository;
    private readonly IGuestSessionRepository _guestSessionRepository;
    private readonly ILocationService _locationService;

    public OrderService(
        IOrderRepository orderRepository,
        IReservationRepository reservationRepository,
        IDishService dishService,
        INotificationService notificationService,
        IGroupReservationRepository groupReservationRepository,
        IGuestSessionRepository guestSessionRepository,
        ILocationService locationService)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _reservationRepository = reservationRepository ?? throw new ArgumentNullException(nameof(reservationRepository));
        _dishService = dishService ?? throw new ArgumentNullException(nameof(dishService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _groupReservationRepository = groupReservationRepository ?? throw new ArgumentNullException(nameof(groupReservationRepository));
        _guestSessionRepository = guestSessionRepository ?? throw new ArgumentNullException(nameof(guestSessionRepository));
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
    }

    public async Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(string customerId)
    {
        return await _orderRepository.GetOrdersByCustomerIdAsync(customerId);
    }

    public async Task ConfirmPreOrderDishAsync(string callerId, string reservationId, string dishId, ILambdaLogger logger)
    {
        var reservation = await LoadReservationAsync(reservationId);

        EnsureCallerIsAssignedWaiter(callerId, reservation);
        EnsurePreOrderEditable(reservation.StartTime);

        var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId, "Order not found.");
        var order = snapshot.Order;
        bool shouldSubmit = ConfirmDishInOrder(order, dishId);

        if (shouldSubmit)
        {
            order.Status = OrderStatus.Submitted;
            logger.Log($"Order {order.OrderId} marked as Submitted (all dishes confirmed/canceled and at least one confirmed).");
        }

        await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
        logger.Log($"Confirmed dish {dishId} in order {order.OrderId}.");
    }

    public async Task CancelPreOrderDishAsync(string callerId, string reservationId, string dishId, ILambdaLogger logger)
    {
        var reservation = await LoadReservationAsync(reservationId);

        EnsureCallerIsAssignedWaiter(callerId, reservation);
        EnsurePreOrderEditable(reservation.StartTime);

        var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId, "Order not found.");
        var order = snapshot.Order;

        bool shouldSubmit = CancelDishInOrder(order, dishId);

        if (shouldSubmit)
        {
            order.Status = OrderStatus.Submitted;
            logger.Log($"Order {order.OrderId} marked as Submitted (all dishes confirmed/canceled and at least one confirmed).");
        }

        await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
        logger.Log($"Canceled dish {dishId} in order {order.OrderId}.");
    }

    public async Task<Order?> GetOrderByReservationIdAsync(string reservationId)
    {
        return await _orderRepository.GetOrderByReservationIdAsync(reservationId);
    }

    public async Task AddOrderAsync(Order order)
    {
        var reservation = await LoadReservationAsync(order.ReservationId);
        EnsurePreOrderEditable(reservation.StartTime);

        var existingActiveOrder = await GetActiveOrderByReservationIdAsync(order.ReservationId, throwIfNotFound: false);
        if (existingActiveOrder != null)
        {
            throw new ValidationException($"Active order already exists for reservation {order.ReservationId}.");
        }

        order.Status = OrderStatus.Created;
        await _orderRepository.SaveOrderAsync(order);

        if (!string.IsNullOrEmpty(reservation.WaiterId))
        {
            await _notificationService.CreateOrderNotificationAsync(
                reservation.WaiterId,
                order.OrderId,
                order.ReservationId);
        }
    }

    public async Task AddOrderForWaiterAsync(Order order)
    {
        _ = await LoadReservationAsync(order.ReservationId);

        var existingOrder = await GetOrderByReservationIdAsync(order.ReservationId);
        if (existingOrder != null)
        {
            throw new ValidationException($"Active order already exists for reservation {order.ReservationId}.");
        }

        order.Status = OrderStatus.Created;
        await _orderRepository.SaveOrderAsync(order);
    }

    public async Task EditPreOrderAsync(
        string customerId,
        string reservationId,
        List<OrderItem> newDishes,
        ILambdaContext context)
    {
        await EditPreOrderInternalAsync(
            reservationId,
            newDishes,
            context,
            reservation =>
            {
                if (!string.Equals(reservation.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                    throw new ActionForbiddenException($"User '{customerId}' is not authorized to edit pre-order for reservation '{reservation.Id}' owned by '{reservation.CustomerId}'.");

                if (reservation.Status != ReservationStatus.Reserved)
                    throw new ValidationException($"Pre-order can be edited only if reservation status is 'Reserved'. Current status: '{reservation.Status}'.");

                EnsurePreOrderEditable(reservation.StartTime);
            });
    }

    public async Task EditPreOrderForWaiterAsync(
        string waiterId,
        string reservationId,
        List<OrderItem> newDishes,
        ILambdaContext context)
    {
        await EditPreOrderInternalAsync(
            reservationId,
            newDishes,
            context,
            reservation =>
            {
                if (EqualityHelper.NotEqualsIgnoreCase(reservation.WaiterId, waiterId))
                    throw new ActionForbiddenException($"Waiter '{waiterId}' is not authorized to edit pre-order for reservation '{reservation.Id}' assigned to '{reservation.WaiterId}'.");

                if (reservation.Status != ReservationStatus.InProgress)
                    throw new ValidationException($"Pre-order can be edited only if reservation status is 'InProgress'. Current status: '{reservation.Status}'.");
            });
    }

    public async Task CancelPreOrderAsync(string customerId, string reservationId, ILambdaContext context)
    {
        var reservation = await LoadReservationAsync(reservationId);

        if (!string.Equals(reservation.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionForbiddenException("You are not authorized to cancel this pre-order.");
        }

        var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId);
        var order = snapshot.Order;
        order.Status = OrderStatus.Cancelled;

        await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
        context.Logger.LogLine($"Pre-order {order.OrderId} for reservation {reservationId} cancelled by customer {customerId}.");
    }

    public async Task SubmitPreOrderAsync(string customerId, string reservationId, ILambdaContext context)
    {
        var reservation = await LoadReservationAsync(reservationId);

        if (!string.Equals(reservation.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionForbiddenException("You are not authorized to submit this pre-order.");
        }

        var groupReservation = await _groupReservationRepository.GetByReservationIdAsync(reservationId);
        if (groupReservation is null)
        {
            var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId);
            var order = snapshot.Order;
            if (order.Status == OrderStatus.SubmittedByCustomer)
                throw new ValidationException("This pre-order has already been submitted.");

            order.Status = OrderStatus.SubmittedByCustomer;
            await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
            context.Logger.LogLine($"Pre-order {order.OrderId} for reservation {reservationId} submitted by customer {customerId}.");
            return;
        }

        await ConfirmGroupPreOrderAsync(customerId, reservation, groupReservation, context);
    }

    private async Task ConfirmGroupPreOrderAsync(
        string customerId,
        Reservation reservation,
        GroupReservation groupReservation,
        ILambdaContext context)
    {
        if (reservation.Status != ReservationStatus.Reserved)
            throw new ValidationException($"Group pre-order can be confirmed only for a reserved reservation. Current status: '{reservation.Status}'.");

        EnsurePreOrderEditable(reservation.StartTime);

        if (groupReservation.Status != GroupReservationStatus.Open)
            throw new ValidationException("This group pre-order has already been confirmed or is closed.");

        var existingOrder = await _orderRepository.GetOrderByReservationIdAsync(reservation.Id);
        if (existingOrder is not null)
            throw new ValidationException("An active pre-order already exists for this reservation.");

        var sessions = await _guestSessionRepository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId);
        if (sessions.Count == 0)
            throw new ValidationException("No participant pre-orders were found for this group reservation.");

        if (sessions.Any(session => !session.IsSubmitted))
            throw new ValidationException("All participant pre-orders must be submitted before group confirmation.");

        var consolidatedItems = sessions
            .SelectMany(session => session.Dishes ?? [])
            .Select(item => new OrderItem
            {
                DishId = item.DishId,
                Quantity = item.Quantity,
                State = item.State
            })
            .ToList();

        if (consolidatedItems.Count == 0)
            throw new ValidationException("The consolidated group pre-order is empty.");

        var location = await _locationService.GetLocationByTableIdAsync(reservation.TableId)
            ?? throw new ValidationException("Location not found for the reservation's table.");

        var order = new Order
        {
            OrderId = $"group-{groupReservation.GroupReservationId}",
            CustomerId = customerId,
            ReservationId = reservation.Id,
            ReservationAddress = location.Address,
            ReservationTime = $"{reservation.StartTime:MMM dd, yyyy, h:mm tt} - {reservation.EndTime:h:mm tt}",
            Dishes = consolidatedItems,
            Status = OrderStatus.SubmittedByCustomer
        };

        await _orderRepository.SaveGroupOrderAtomicallyAsync(order, groupReservation);
        context.Logger.LogLine($"Consolidated group pre-order {order.OrderId} for reservation {reservation.Id} confirmed by owner {customerId}.");
    }

    public async Task UnSubmitPreOrderAsync(string customerId, string reservationId, ILambdaContext context)
    {
        var reservation = await LoadReservationAsync(reservationId);

        if (!string.Equals(reservation.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionForbiddenException("You are not authorized to submit this pre-order.");
        }

        var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId, "No pre-order found for the given reservation.");
        var order = snapshot.Order;
        EnsurePreOrderEditable(reservation.StartTime);

        order.Status = OrderStatus.Created;

        await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
        context.Logger.LogLine($"Pre-order {order.OrderId} for reservation {reservationId} submitted by customer {customerId}.");
    }

    private async Task EditPreOrderInternalAsync(
    string reservationId,
    List<OrderItem> newDishes,
    ILambdaContext context,
    Action<Reservation> validateReservation)
    {
        var reservation = await LoadReservationAsync(reservationId);
        validateReservation(reservation);

        var orderItemsForValidation = newDishes.Select(d => new OrderItem
        {
            DishId = d.DishId,
            Quantity = d.Quantity
        }).ToList();

        var isValid = await ValidateDishes(orderItemsForValidation, context);
        if (!isValid)
            throw new ValidationException("One or more dishes in the request are invalid, unavailable, or have a quantity of 0.");

        var snapshot = await LoadOrderSnapshotByReservationIdAsync(reservationId);
        var order = snapshot.Order;
        order.Dishes.Clear();

        foreach (var dish in newDishes)
        {
            order.Dishes.Add(new OrderItem
            {
                DishId = dish.DishId,
                Quantity = dish.Quantity,
                State = OrderItemState.Pending,
            });
        }

        await _orderRepository.SaveOrderIfUnchangedAsync(order, snapshot);
    }

    private async Task<Order?> GetActiveOrderByReservationIdAsync(string reservationId, bool throwIfNotFound = true)
    {
        var order = await _orderRepository.GetOrderByReservationIdAsync(reservationId);

        if (order == null && throwIfNotFound)
        {
            throw new ValidationException($"Active pre-order for reservation '{reservationId}' not found.");
        }

        return order;
    }

    private async Task<Reservation> LoadReservationAsync(string reservationId)
    {
        var reservation = await _reservationRepository.GetReservationByIdAsync(reservationId);
        return reservation ?? throw new ReservationNotFoundException("Reservation not found.");
    }

    private async Task<OrderSubmissionSnapshot> LoadOrderSnapshotByReservationIdAsync(string reservationId, string? notFoundMessage = null)
    {
        var selected = await _orderRepository.GetOrderByReservationIdAsync(reservationId);
        var message = notFoundMessage ?? $"Active pre-order for reservation '{reservationId}' not found.";
        if (selected is null)
            throw new ValidationException(message);

        var snapshot = await _orderRepository.GetOrderSnapshotAsync(selected.OrderId, selected.CustomerId)
            ?? throw new ValidationException(message);
        if (!string.Equals(snapshot.Order.ReservationId, reservationId, StringComparison.Ordinal) ||
            snapshot.Order.Status == OrderStatus.Cancelled)
            throw new ValidationException("The order changed concurrently. Refresh and retry.");
        return snapshot;
    }

    private static void EnsurePreOrderEditable(DateTime reservationStartTime)
    {
        var cutoff = reservationStartTime.ToUniversalTime().AddMinutes(-30);
        if (DateTime.UtcNow > cutoff)
            throw new ValidationException($"Pre-orders can only be modified up to 30 minutes before the reservation start time ({reservationStartTime:HH:mm}).");
    }

    private static bool ConfirmDishInOrder(Order order, string dishId)
    {
        var dish = order.Dishes.FirstOrDefault(d => d.DishId == dishId)
            ?? throw new DishNotFoundException($"Dish {dishId} not found in order {order.OrderId}.");

        dish.State = OrderItemState.Confirmed;

        return ShouldMarkOrderAsSubmitted(order);
    }

    private static bool CancelDishInOrder(Order order, string dishId)
    {
        var dish = order.Dishes.FirstOrDefault(d => d.DishId == dishId)
            ?? throw new DishNotFoundException($"Dish {dishId} not found in order {order.OrderId}.");

        dish.State = OrderItemState.Canceled;

        return ShouldMarkOrderAsSubmitted(order);
    }

    private static void EnsureCallerIsAssignedWaiter(string callerId, Reservation reservation)
    {
        if (DataValidationHelper.IsMissing(reservation.WaiterId))
        {
            throw new ValidationException($"Reservation '{reservation.Id}' has no assigned waiter.");
        }

        if (!string.Equals(reservation.WaiterId, callerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionForbiddenException($"Action forbidden: You '{callerId}' are not the assigned waiter '{reservation.WaiterId}' for reservation '{reservation.Id}'.");
        }
    }

    private async Task<bool> ValidateDishes(List<OrderItem> orderItems, ILambdaContext context)
    {
        foreach (var item in orderItems)
        {
            if (string.IsNullOrEmpty(item.DishId) || item.Quantity <= 0)
            {
                context.Logger.LogLine($"Invalid dish ID or quantity for dish: {item.DishId}, quantity: {item.Quantity}");
                return false;
            }

            var dish = await _dishService.GetDishByIdAsync(item.DishId);
            if (dish is null || dish.State != DishState.Available)
            {
                context.Logger.LogLine($"Dish with ID {item.DishId} is not available.");
                return false;
            }
        }
        return orderItems.Count > 0;
    }

    private static bool ShouldMarkOrderAsSubmitted(Order order)
    {
        bool anyConfirmed = order.Dishes.Any(d => d.State == OrderItemState.Confirmed);
        bool anyPending = order.Dishes.Any(d => d.State == OrderItemState.Pending);

        return anyConfirmed && !anyPending;
    }
}
