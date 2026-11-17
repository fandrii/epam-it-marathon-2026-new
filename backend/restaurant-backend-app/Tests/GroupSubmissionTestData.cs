using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Repositories.Orders;

namespace APIHandler.Tests;

internal static class GroupSubmissionTestData
{
    internal const string OwnerId = "owner-1";
    internal const string ReservationId = "reservation-1";
    internal const string GroupId = "group-1";

    internal static Reservation Reservation() => new()
    {
        Id = ReservationId, CustomerId = OwnerId, TableId = "table-1", WaiterId = "waiter-1",
        StartTime = new DateTime(9998, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(9998, 1, 1, 14, 0, 0, DateTimeKind.Utc),
        GuestCount = 4, Status = ReservationStatus.Reserved
    };

    internal static GroupReservation Group() => new()
    {
        GroupReservationId = GroupId, ReservationId = ReservationId,
        ReservationToken = "invitation", Status = GroupReservationStatus.Open
    };

    internal static OrderItem Dish(string id = "dish1", int quantity = 1,
        OrderItemState state = OrderItemState.Pending) => new() { DishId = id, Quantity = quantity, State = state };

    internal static GuestSession Session(string id, bool submitted, params OrderItem[] dishes) => new()
    {
        Id = id, GroupReservationId = GroupId, GuestName = id, IsSubmitted = submitted, Dishes = dishes.ToList()
    };

    internal static Order Order(params OrderItem[] dishes) => new()
    {
        OrderId = "existing-order", CustomerId = OwnerId, ReservationId = ReservationId,
        ReservationAddress = "Original address", ReservationTime = "Original time",
        Dishes = dishes.ToList(), Status = OrderStatus.Created
    };

    internal static OrderSubmissionSnapshot Snapshot(params OrderItem[] dishes)
    {
        var order = Order(dishes);
        return new(order, Attributes(order));
    }

    internal static Dictionary<string, AttributeValue> Attributes(Order order) => new()
    {
        ["orderId"] = S(order.OrderId), ["customerId"] = S(order.CustomerId),
        ["reservationId"] = S(order.ReservationId), ["reservationAddress"] = S(order.ReservationAddress),
        ["reservationTime"] = S(order.ReservationTime), ["status"] = N((int)order.Status),
        ["dishes"] = DishList(order.Dishes)
    };

    internal static AttributeValue DishList(IEnumerable<OrderItem> dishes) => new()
    {
        L = dishes.Select(dish => new AttributeValue { M = new()
        {
            ["dishId"] = S(dish.DishId), ["quantity"] = N(dish.Quantity), ["state"] = N((int)dish.State)
        } }).ToList()
    };

    internal static AttributeValue S(string value) => new() { S = value };
    internal static AttributeValue N(int value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}
