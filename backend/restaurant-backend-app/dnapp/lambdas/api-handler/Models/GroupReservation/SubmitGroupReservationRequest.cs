using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public record SubmitGroupReservationRequest(
    [property: Required, JsonPropertyName("reservationId")] string ReservationId);
