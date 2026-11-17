using APIHandler.Models.Orders;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GetAllSessionsResponse(IEnumerable<Participant> Participants, decimal Total);

public record GetGroupSessionsResponse(IEnumerable<GroupSessionParticipant> Participants, decimal Total);

public record GroupSessionOrderItemResponse(
    [property: JsonPropertyName("dish")] GroupSessionDishResponse Dish,
    [property: JsonPropertyName("quantity")] int Quantity);

public record GroupSessionDishResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] decimal Price);

public record GroupSessionParticipant(
    string? Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    string Name,
    IEnumerable<GroupSessionOrderItemResponse> Items,
    decimal Total,
    bool IsSubmitted);

public record Participant(
    string? Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    string Name,
    IEnumerable<OrderItemResponse> Items,
    decimal Total,
    bool IsSubmitted);

public enum ParticipantType
{
    Owner,
    Guest
}
