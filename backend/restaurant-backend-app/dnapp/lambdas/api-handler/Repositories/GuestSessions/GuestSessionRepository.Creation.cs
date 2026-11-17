using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public partial class GuestSessionRepository
{
    private const int MembershipAttempts = 8;
    private sealed record Capacity(int Count, long Revision, bool Initialized, GroupReservationStatus Status);
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> SessionKey(string id) => new() { ["guestSessionId"] = S(id) };

    public async Task<CreatGuestSessionResponse?> GetCreationResultAsync(string requestKey, string fingerprint)
    {
        var item = await ReadItemAsync(ResourceNames.GuestSessionTableName, SessionKey($"creation-{requestKey}"));
        if (item.Count == 0) return null;
        if (item["fingerprint"].S != fingerprint)
            throw new GuestSessionCreationConflictException("This idempotency key has already been used with different request data.");

        var session = await ReadItemAsync(ResourceNames.GuestSessionTableName, SessionKey(item["responseSessionId"].S));
        if (session.Count == 0)
            throw new GuestSessionCreationConflictException("The session created by this request has been deleted. Use a new idempotency key for a new participant.");

        return new CreatGuestSessionResponse(item["responseSessionId"].S, item["responseToken"].S);
    }

    public async Task<CreatGuestSessionResponse> CreateAtomicallyAsync(
        GuestSession session, Reservation reservation, string requestKey, string fingerprint, string sessionToken)
    {
        for (int attempt = 0; attempt < MembershipAttempts; attempt++)
        {
            var previous = await GetCreationResultAsync(requestKey, fingerprint);
            if (previous is not null) return previous;

            var capacity = await ReadCapacityAsync(session.GroupReservationId);
            if (capacity.Status != GroupReservationStatus.Open)
                throw new GroupReservationExpiredException();
            if (capacity.Count >= reservation.GuestCount)
            {
                // Another request with this same key may have just taken the last seat.
                previous = await GetCreationResultAsync(requestKey, fingerprint);
                return previous ?? throw new GuestLimitExceededException("Guest limit exceeded. The reservation is full.");
            }

            var transaction = new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    CapacityWrite(session.GroupReservationId, capacity, capacity.Count + 1, requireOpen: true),
                    // A conditional no-op update validates the current reservation using
                    // existing UpdateItem permissions and serializes with reservation edits.
                    new() { Update = new Update
                    {
                        TableName = ResourceNames.ReservationsTableName,
                        Key = new() { ["id"] = S(reservation.Id) },
                        UpdateExpression = "SET #capacity = :capacity",
                        ConditionExpression = "#status = :reserved AND #capacity = :capacity",
                        ExpressionAttributeNames = new() { ["#capacity"] = "guestCount", ["#status"] = "status" },
                        ExpressionAttributeValues = new() { [":capacity"] = N(reservation.GuestCount), [":reserved"] = N((int)ReservationStatus.Reserved) }
                    } },
                    new() { Put = new Put
                    {
                        TableName = ResourceNames.GuestSessionTableName,
                        ConditionExpression = "attribute_not_exists(guestSessionId)",
                        Item = new()
                        {
                            ["guestSessionId"] = S(session.Id),
                            ["groupReservationId"] = S(session.GroupReservationId),
                            ["guestName"] = S(session.GuestName),
                            ["guestSessionTokenHash"] = S(session.SessionTokenHash!),
                            ["isOwnerSession"] = new() { BOOL = false },
                            ["isSubmitted"] = new() { BOOL = false },
                            ["dishes"] = new() { L = [] }
                        }
                    } },
                    new() { Put = new Put
                    {
                        TableName = ResourceNames.GuestSessionTableName,
                        ConditionExpression = "attribute_not_exists(guestSessionId)",
                        // No groupReservationId or token-hash index keys: creation receipts
                        // must never appear as participants or authorize cart requests.
                        Item = new()
                        {
                            ["guestSessionId"] = S($"creation-{requestKey}"),
                            ["fingerprint"] = S(fingerprint),
                            ["responseSessionId"] = S(session.Id),
                            ["responseToken"] = S(sessionToken)
                        }
                    } }
                ]
            };

            try
            {
                await client.TransactWriteItemsAsync(transaction);
                return new CreatGuestSessionResponse(session.Id, sessionToken);
            }
            catch (TransactionCanceledException exception) when (IsMembershipConflict(exception)) { }
            catch (TransactionConflictException) { }
        }

        return await GetCreationResultAsync(requestKey, fingerprint) ??
            throw new GuestSessionCreationConflictException("The reservation changed concurrently. Retry with the same idempotency key.");
    }

    private async Task<Capacity> ReadCapacityAsync(string groupId)
    {
        var group = await ReadItemAsync(ResourceNames.GroupReservationsTableName,
            new() { ["groupReservationId"] = S(groupId) });
        if (group.Count == 0) throw new GroupReservationNotFoundException();
        var status = (GroupReservationStatus)int.Parse(group["status"].N, CultureInfo.InvariantCulture);
        if (group.TryGetValue("participantCount", out var count))
        {
            if (!int.TryParse(count.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out int participants) || participants < 1 ||
                !group.TryGetValue("participantRevision", out var revision) ||
                !long.TryParse(revision.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out long version) || version < 1)
                throw new GuestSessionCreationConflictException("Participant capacity metadata is inconsistent.");
            return new Capacity(participants, version, true, status);
        }

        // Legacy groups are initialized only by a membership write. A strongly
        // consistent base-table scan avoids missing sessions in the eventual GSI.
        // All membership writers condition on the absent counter, so a competing
        // initialization invalidates this snapshot and the entire transaction retries.
        int guests = 0;
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var response = await client.ScanAsync(new ScanRequest
            {
                TableName = ResourceNames.GuestSessionTableName,
                ConsistentRead = true,
                FilterExpression = "#group = :group",
                ProjectionExpression = "guestSessionId, isOwnerSession",
                ExpressionAttributeNames = new() { ["#group"] = "groupReservationId" },
                ExpressionAttributeValues = new() { [":group"] = S(groupId) },
                ExclusiveStartKey = cursor
            });
            guests += (response.Items ?? []).Count(item =>
                !item.TryGetValue("isOwnerSession", out var owner) || (owner.BOOL != true && owner.N != "1"));
            cursor = response.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });

        return new Capacity(guests + 1, 0, false, status);
    }

    private static TransactWriteItem CapacityWrite(string groupId, Capacity capacity, int next, bool requireOpen)
    {
        var names = new Dictionary<string, string> { ["#count"] = "participantCount", ["#revision"] = "participantRevision" };
        var values = new Dictionary<string, AttributeValue> { [":next"] = N(next), [":nextRevision"] = N(capacity.Revision + 1) };
        string condition = "attribute_exists(groupReservationId) AND attribute_not_exists(#count)";
        if (capacity.Initialized)
        {
            condition = "#count = :old AND #revision = :revision";
            values[":old"] = N(capacity.Count);
            values[":revision"] = N(capacity.Revision);
        }
        if (requireOpen)
        {
            condition += " AND #status = :open";
            names["#status"] = "status";
            values[":open"] = N((int)GroupReservationStatus.Open);
        }
        return new TransactWriteItem { Update = new Update
        {
            TableName = ResourceNames.GroupReservationsTableName,
            Key = new() { ["groupReservationId"] = S(groupId) },
            UpdateExpression = "SET #count = :next, #revision = :nextRevision",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        } };
    }

    private async Task DeleteWithCapacityAsync(GuestSession session)
    {
        for (int attempt = 0; attempt < MembershipAttempts; attempt++)
        {
            var existing = await ReadItemAsync(ResourceNames.GuestSessionTableName, SessionKey(session.Id));
            if (existing.Count == 0) return;
            var capacity = await ReadCapacityAsync(session.GroupReservationId);
            int next = capacity.Count - (session.IsOwnerSession ? 0 : 1);
            if (next < 1) throw new GuestSessionCreationConflictException("Participant capacity is inconsistent.");
            try
            {
                await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems =
                [
                    CapacityWrite(session.GroupReservationId, capacity, next, requireOpen: true),
                    new() { Delete = new Delete
                    {
                        TableName = ResourceNames.GuestSessionTableName,
                        Key = SessionKey(session.Id),
                        ConditionExpression = "attribute_exists(guestSessionId) AND groupReservationId = :group",
                        ExpressionAttributeValues = new() { [":group"] = S(session.GroupReservationId) }
                    } }
                ] });
                return;
            }
            catch (TransactionCanceledException exception) when (IsMembershipConflict(exception)) { }
            catch (TransactionConflictException) { }
        }
        throw new GuestSessionCreationConflictException("The group changed concurrently. Retry removing the session.");
    }

    private async Task<Dictionary<string, AttributeValue>> ReadItemAsync(string table, Dictionary<string, AttributeValue> key)
    {
        var response = await client.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true });
        return response.Item ?? [];
    }

    private static bool IsMembershipConflict(TransactionCanceledException exception) =>
        exception.CancellationReasons is { Count: > 0 } reasons &&
        reasons.All(reason => reason.Code is "None" or "ConditionalCheckFailed" or "TransactionConflict");
}
