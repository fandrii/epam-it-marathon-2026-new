using System.ComponentModel.DataAnnotations;

namespace APIHandler.Models.GuestSessions;

public record CreateGuestSessionRequest(
    [property: Required] string GroupReservationId, 
    [property: Required] string GuestName)
{
    [StringLength(128, MinimumLength = 8)]
    public string? IdempotencyKey { get; init; }
}
