using APIHandler.Entities;
using APIHandler.Models.GuestSessions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public interface IGuestSessionRepository
{
    // Total participants: one reservation owner plus every persisted non-owner session.
    Task<int> CountByGroupReservationId(string groupReservationId);

    Task<GuestSession?> GetByGroupReservationId(string groupReservationId);

    Task<GuestSession?> GetByGuestSessionId(string id);

    Task<GuestSession?> GetByTokenHash(string tokenHash);

    Task<List<GuestSession>> GetAllByGroupReservationIdAsync(string groupReservationId);

    Task<List<GuestSession>> GetForGroupSubmissionAsync(string groupReservationId);

    Task SaveAsync(GuestSession guestSession);
    Task CreateOwnerSessionIfAbsentAsync(GuestSession guestSession);

    Task<CreatGuestSessionResponse?> GetCreationResultAsync(string requestKey, string fingerprint);

    Task<CreatGuestSessionResponse> CreateAtomicallyAsync(
        GuestSession session, Reservation reservation, string requestKey, string fingerprint, string sessionToken);

    Task DeleteAsync(GuestSession session);
}
