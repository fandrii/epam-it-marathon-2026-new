using APIHandler.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Repositories.Orders;

public interface IOrderRepository
{
    Task<Order?> GetOrderByIdAsync(string orderId, string customerId);
    Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(string customerId);
    Task<Order?> GetOrderByReservationIdAsync(string reservationId);
    Task SaveOrderAsync(Order order);
    Task<OrderSubmissionSnapshot?> GetOrderSnapshotAsync(string orderId, string customerId);
    Task SaveOrderIfUnchangedAsync(Order order, OrderSubmissionSnapshot original);
    Task SaveGroupOrderAtomicallyAsync(Order order, GroupReservation groupReservation);
    Task<OrderSubmissionSnapshot?> GetOrderForGroupSubmissionAsync(string reservationId);
    Task SubmitExistingGroupOrderAtomicallyAsync(OrderSubmissionSnapshot snapshot,
        IReadOnlyList<OrderItem> mergedItems, GroupReservation groupReservation, Reservation reservation);
}

