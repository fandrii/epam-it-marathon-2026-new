using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Repositories.GuestSessions;
using Moq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace APIHandler.Tests.Repositories;

public class GuestSessionCreationTests
{
    static GuestSessionCreationTests()
    {
        Environment.SetEnvironmentVariable("groupReservationsTable", "GroupReservations");
        Environment.SetEnvironmentVariable("guestSessionTable", "GuestSessions");
        Environment.SetEnvironmentVariable("reservationsTable", "Reservations");
    }
    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public async Task Capacity_IncludesOwnerOnce_AllowsExactlyCapacityParticipants(int capacity, bool ownerPersisted)
    {
        var database = new TestDatabase(capacity, ownerPersisted);
        for (int guest = 1; guest < capacity; guest++)
            await database.CreateAsync($"request-{guest}");

        Assert.Equal(capacity, await database.Repository.CountByGroupReservationId("group-1"));
        await Assert.ThrowsAsync<GuestLimitExceededException>(() => database.CreateAsync("excess-guest"));
        Assert.Equal(capacity - 1, database.Guests.Count);
        Assert.Equal(ownerPersisted ? 1 : 0, database.Owners.Count);
    }

    [Fact]
    public async Task SameKeyAndData_ReturnsOriginalSessionAndToken_EvenWhenFull()
    {
        var database = new TestDatabase(2);
        var first = await database.CreateAsync("same-key");
        var replay = await database.CreateAsync("same-key");

        Assert.Equal(first, replay);
        Assert.Single(database.Guests);
        Assert.Equal(1, database.SuccessfulTransactions);
        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Fact]
    public async Task SameKeyWithDifferentFingerprint_IsRejectedWithoutWriting()
    {
        var database = new TestDatabase(4);
        await database.CreateAsync("same-key", "original");
        await Assert.ThrowsAsync<GuestSessionCreationConflictException>(() => database.CreateAsync("same-key", "changed"));
        Assert.Single(database.Guests);
        Assert.Equal(1, database.SuccessfulTransactions);
    }

    [Fact]
    public async Task DifferentKeysWithIdenticalGuestNames_CreateDistinctParticipants()
    {
        var database = new TestDatabase(4);
        var first = await database.CreateAsync("first-key");
        var second = await database.CreateAsync("second-key");

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(2, database.Guests.Count);
        Assert.All(database.Guests, session => Assert.Equal("Same guest name", session["guestName"].S));
    }

    [Fact]
    public async Task ConcurrentSameKeyRequests_CreateOneSessionAndConsumeOneSeat()
    {
        var database = new TestDatabase(2);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => database.CreateAsync("same-key")));

        Assert.Single(responses.Select(response => response.SessionId).Distinct());
        Assert.Single(responses.Select(response => response.SessionToken).Distinct());
        Assert.Single(database.Guests);
        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ConcurrentDifferentKeys_CannotOversubscribeCapacity(int capacity)
    {
        var database = new TestDatabase(capacity);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try { await database.CreateAsync($"request-{index}"); return true; }
            catch (GuestLimitExceededException) { return false; }
        }));

        Assert.Equal(capacity - 1, outcomes.Count(success => success));
        Assert.Equal(capacity - 1, database.Guests.Count);
        Assert.Equal(capacity, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Fact]
    public async Task LegacySessionsArePreserved_SubmittedGuestsStillCount_ReadsNeverInitializeCounter()
    {
        var database = new TestDatabase(4);
        database.SeedGuest("existing-guest", submitted: true);

        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
        Assert.False(database.Group.ContainsKey("participantCount"));
        Assert.Equal(0, database.SuccessfulTransactions);

        await database.CreateAsync("new-guest");
        Assert.Equal(3, await database.Repository.CountByGroupReservationId("group-1"));
        Assert.Contains(database.Guests, item => item["guestSessionId"].S == "existing-guest" && item["isSubmitted"].BOOL == true);
    }

    [Fact]
    public async Task LegacyNumericOwnerFlagIsCountedOnce()
    {
        var database = new TestDatabase(2);
        database.Owners.Single()["isOwnerSession"] = N(1);

        Assert.Equal(1, await database.Repository.CountByGroupReservationId("group-1"));
        await database.CreateAsync("new-guest");
        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Fact]
    public async Task LegacyCapacityScanIncludesEveryPage()
    {
        var database = new TestDatabase(4) { ScanPageSize = 1 };
        database.SeedGuest("first-guest");
        database.SeedGuest("second-guest");

        Assert.Equal(3, await database.Repository.CountByGroupReservationId("group-1"));
        await database.CreateAsync("last-guest");
        await Assert.ThrowsAsync<GuestLimitExceededException>(() => database.CreateAsync("excess-guest"));
    }

    [Fact]
    public async Task RemovingGuestReleasesExactlyOneSeat_ReplayedCreationNeverResurrectsDeletedSession()
    {
        var database = new TestDatabase(2);
        var response = await database.CreateAsync("original-key");
        var deleted = new GuestSession { Id = response.SessionId, GroupReservationId = "group-1", GuestName = "Guest", Dishes = [] };

        await database.Repository.DeleteAsync(deleted);
        await database.Repository.DeleteAsync(deleted);
        Assert.Equal(1, await database.Repository.CountByGroupReservationId("group-1"));
        await Assert.ThrowsAsync<GuestSessionCreationConflictException>(() => database.CreateAsync("original-key"));
        await database.CreateAsync("replacement-key");
        Assert.Single(database.Guests);
    }

    [Fact]
    public async Task ReservationCapacityChangeCancelsAllTransactionMembers()
    {
        var database = new TestDatabase(2);
        database.ReservationItem["guestCount"] = N(1);

        await Assert.ThrowsAsync<GuestSessionCreationConflictException>(() => database.CreateAsync("new-key"));

        Assert.Empty(database.Guests);
        Assert.False(database.Group.ContainsKey("participantCount"));
        Assert.Equal(0, database.SuccessfulTransactions);
    }

    [Fact]
    public async Task ClosedGroupRejectsCreation_ReceiptCannotAppearInParticipantOrTokenIndexes()
    {
        var database = new TestDatabase(4);
        await database.CreateAsync("new-key");
        var receipt = Assert.Single(database.Items.Where(item => item.Key.Contains("creation-")).Select(item => item.Value));
        Assert.False(receipt.ContainsKey("groupReservationId"));
        Assert.False(receipt.ContainsKey("guestSessionTokenHash"));
        database.Group["status"] = N((int)GroupReservationStatus.Closed);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => database.CreateAsync("different-key"));
    }

    [Fact]
    public async Task OwnerInitializationRetriesNeverOverwriteAnExistingCart()
    {
        var database = new TestDatabase(2);
        var original = database.Owners.Single();
        original["cartSentinel"] = S("keep existing cart");
        var owner = new GuestSession
        {
            Id = "owner-group-1", GroupReservationId = "group-1", GuestName = "Owner",
            IsOwnerSession = true, OwnerUserId = "owner-1", Dishes = []
        };

        await Task.WhenAll(database.Repository.CreateOwnerSessionIfAbsentAsync(owner),
            database.Repository.CreateOwnerSessionIfAbsentAsync(owner));

        Assert.Equal("keep existing cart", database.Owners.Single()["cartSentinel"].S);
        Assert.Equal(1, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Fact]
    public async Task CartSaveAfterDeletionCannotResurrectParticipant()
    {
        var database = new TestDatabase(2);
        var response = await database.CreateAsync("request-key");
        var session = new GuestSession { Id = response.SessionId, GroupReservationId = "group-1", GuestName = "Guest", Dishes = [] };
        await database.Repository.DeleteAsync(session);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => database.Repository.SaveAsync(session));
        Assert.Empty(database.Guests);
        Assert.Equal(1, await database.Repository.CountByGroupReservationId("group-1"));
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted)]
    [InlineData(GroupReservationStatus.Closed)]
    public async Task DeleteTransactionRequiresOpenGroupAndPreservesCounter(GroupReservationStatus status)
    {
        var database = new TestDatabase(2);
        var response = await database.CreateAsync("request-key");
        var session = new GuestSession
        {
            Id = response.SessionId, GroupReservationId = "group-1", GuestName = "Guest", Dishes = []
        };
        database.Group["status"] = N((int)status);

        await Assert.ThrowsAsync<GuestSessionCreationConflictException>(() => database.Repository.DeleteAsync(session));

        Assert.Single(database.Guests);
        Assert.Equal(2, await database.Repository.CountByGroupReservationId("group-1"));
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(int value) => new() { N = value.ToString() };

    // This test double executes the actual DynamoDB transaction requests under a lock,
    // evaluates their conditional expressions, and commits all members or none.
    // Task.Yield forces requests to interleave reads before the conditional write.
    private sealed class TestDatabase
    {
        private readonly object _sync = new();
        public Dictionary<string, Dictionary<string, AttributeValue>> Items { get; } = [];
        public GuestSessionRepository Repository { get; }
        public Reservation Reservation { get; }
        public int SuccessfulTransactions { get; private set; }
        public int RejectedTransactions { get; private set; }
        public int ScanPageSize { get; set; } = int.MaxValue;
        public Dictionary<string, AttributeValue> Group => Items[$"{ResourceNames.GroupReservationsTableName}/group-1"];
        public Dictionary<string, AttributeValue> ReservationItem => Items[$"{ResourceNames.ReservationsTableName}/reservation-1"];
        public List<Dictionary<string, AttributeValue>> Guests => Items.Values.Where(item =>
            item.TryGetValue("groupReservationId", out var group) && group.S == "group-1" &&
            item.ContainsKey("guestSessionId") && item["isOwnerSession"].BOOL != true).ToList();
        public List<Dictionary<string, AttributeValue>> Owners => Items.Values.Where(item =>
            item.TryGetValue("isOwnerSession", out var owner) && owner.BOOL == true).ToList();

        public TestDatabase(int capacity, bool ownerPersisted = true)
        {
            Reservation = new Reservation { Id = "reservation-1", TableId = "table-1", WaiterId = "waiter-1", GuestCount = capacity };
            Items[$"{ResourceNames.GroupReservationsTableName}/group-1"] = new()
            { ["groupReservationId"] = S("group-1"), ["status"] = N((int)GroupReservationStatus.Open) };
            Items[$"{ResourceNames.ReservationsTableName}/reservation-1"] = new()
            { ["id"] = S(Reservation.Id), ["status"] = N((int)ReservationStatus.Reserved), ["guestCount"] = N(capacity) };
            if (ownerPersisted) SeedGuest("owner-group-1", owner: true);
            var client = new Mock<IAmazonDynamoDB>();
            client.Setup(value => value.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PutItemRequest request, CancellationToken _) =>
                {
                    lock (_sync)
                    {
                        if (!Condition(request.ConditionExpression, Read(request.TableName, Key(request.Item)), [], []))
                            throw new ConditionalCheckFailedException("Item already exists");
                        Items[Address(request.TableName, Key(request.Item))] = new(request.Item);
                        return Task.FromResult(new PutItemResponse());
                    }
                });
            client.Setup(value => value.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Returns((UpdateItemRequest request, CancellationToken _) =>
                {
                    lock (_sync)
                    {
                        if (!Condition(request.ConditionExpression, Read(request.TableName, request.Key), [], []))
                            throw new ConditionalCheckFailedException("Item no longer exists");
                        throw new InvalidOperationException("This fixture only exercises rejected cart saves.");
                    }
                });
            client.Setup(value => value.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (GetItemRequest request, CancellationToken _) =>
                {
                    await Task.Yield();
                    Assert.True(request.ConsistentRead);
                    lock (_sync) return new GetItemResponse { Item = Read(request.TableName, request.Key) };
                });
            client.Setup(value => value.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (ScanRequest request, CancellationToken _) =>
                {
                    await Task.Yield();
                    Assert.True(request.ConsistentRead);
                    lock (_sync)
                    {
                        string? after = request.ExclusiveStartKey?.GetValueOrDefault("guestSessionId")?.S;
                        var all = Items.Where(item => item.Key.StartsWith(request.TableName + "/"))
                            .Select(item => item.Value).OrderBy(item => item["guestSessionId"].S, StringComparer.Ordinal)
                            .Where(item => after is null || string.CompareOrdinal(item["guestSessionId"].S, after) > 0).ToList();
                        var page = all.Take(ScanPageSize).ToList();
                        return new ScanResponse
                        {
                            Items = page.Where(item => item.TryGetValue("groupReservationId", out var group) &&
                                group.S == request.ExpressionAttributeValues[":group"].S)
                                .Select(item => new Dictionary<string, AttributeValue>(item)).ToList(),
                            LastEvaluatedKey = all.Count > page.Count ? Key(page.Last()) : null
                        };
                    }
                });
            client.Setup(value => value.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (TransactWriteItemsRequest request, CancellationToken _) =>
                {
                    await Task.Yield();
                    lock (_sync)
                    {
                        bool valid = request.TransactItems.All(item => item.Update is { } update
                            ? Condition(update.ConditionExpression, Read(update.TableName, update.Key), update.ExpressionAttributeNames, update.ExpressionAttributeValues)
                            : item.Put is { } put
                                ? Condition(put.ConditionExpression, Read(put.TableName, Key(put.Item)), [], [])
                                : Condition(item.Delete.ConditionExpression, Read(item.Delete.TableName, item.Delete.Key), [], item.Delete.ExpressionAttributeValues));
                        if (!valid)
                        {
                            RejectedTransactions++;
                            throw new TransactionCanceledException("Conditional conflict")
                            { CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }] };
                        }
                        foreach (var member in request.TransactItems)
                        {
                            if (member.Put is { } put) Items[Address(put.TableName, Key(put.Item))] = new(put.Item);
                            else if (member.Delete is { } delete) Items.Remove(Address(delete.TableName, delete.Key));
                            else
                            {
                                var update = member.Update;
                                var item = Read(update.TableName, update.Key);
                                foreach (string assignment in update.UpdateExpression[4..].Split(','))
                                {
                                    var parts = assignment.Split('=', StringSplitOptions.TrimEntries);
                                    item[update.ExpressionAttributeNames[parts[0]]] = update.ExpressionAttributeValues[parts[1]];
                                }
                                Items[Address(update.TableName, update.Key)] = item;
                            }
                        }
                        SuccessfulTransactions++;
                        return new TransactWriteItemsResponse();
                    }
                });
            var context = new Mock<IDynamoDBContext>();
            context.Setup(value => value.ToDocument(It.IsAny<GuestSession>(), It.IsAny<ToDocumentConfig>()))
                .Returns((GuestSession session, ToDocumentConfig _) => Document.FromAttributeMap(new()
                {
                    ["guestSessionId"] = S(session.Id), ["groupReservationId"] = S(session.GroupReservationId),
                    ["isOwnerSession"] = new() { BOOL = session.IsOwnerSession }, ["guestName"] = S(session.GuestName)
                }));
            Repository = new GuestSessionRepository(context.Object, client.Object);
        }

        public Task<APIHandler.Models.GuestSessions.CreatGuestSessionResponse> CreateAsync(string key, string fingerprint = "same-data")
        {
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return Repository.CreateAtomicallyAsync(new GuestSession
            {
                Id = Guid.NewGuid().ToString(), GroupReservationId = "group-1", GuestName = "Same guest name", Dishes = [],
                SessionTokenHash = Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(token)))
            }, Reservation, key, fingerprint, token);
        }

        public void SeedGuest(string id, bool owner = false, bool submitted = false) =>
            Items[$"{ResourceNames.GuestSessionTableName}/{id}"] = new()
            {
                ["guestSessionId"] = S(id), ["groupReservationId"] = S("group-1"),
                ["isOwnerSession"] = new() { BOOL = owner }, ["isSubmitted"] = new() { BOOL = submitted }
            };

        private Dictionary<string, AttributeValue> Read(string table, Dictionary<string, AttributeValue> key) =>
            Items.TryGetValue(Address(table, key), out var item) ? new(item) : [];
        private static string Address(string table, Dictionary<string, AttributeValue> key) => $"{table}/{key.Single().Value.S}";
        private static Dictionary<string, AttributeValue> Key(Dictionary<string, AttributeValue> item) => new() { ["guestSessionId"] = item["guestSessionId"] };

        private static bool Condition(string expression, Dictionary<string, AttributeValue> item,
            Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
        {
            foreach (string condition in expression.Split(" AND "))
            {
                var exists = Regex.Match(condition, @"^attribute_(not_)?exists\(([#\w]+)\)$");
                string Resolve(string key) => names.TryGetValue(key, out var name) ? name : key;
                if (exists.Success)
                {
                    if (item.ContainsKey(Resolve(exists.Groups[2].Value)) == exists.Groups[1].Success) return false;
                }
                else
                {
                    var equal = Regex.Match(condition, @"^([#\w]+) = (:[\w]+)$");
                    Assert.True(equal.Success, $"Unsupported expression: {condition}");
                    if (!item.TryGetValue(Resolve(equal.Groups[1].Value), out var actual)) return false;
                    var expected = values[equal.Groups[2].Value];
                    if (actual.S != expected.S || actual.N != expected.N) return false;
                }
            }
            return true;
        }
    }
}
