using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Entities;
using APIHandler.Repositories.GuestSessions;
using Moq;
using static APIHandler.Tests.GroupSubmissionTestData;

namespace APIHandler.Tests.Repositories;

public class GroupSubmissionSessionReadTests
{
    static GroupSubmissionSessionReadTests() => Environment.SetEnvironmentVariable("guestSessionTable", "GuestSessions");

    [Fact]
    public async Task GetForGroupSubmissionAsync_PaginatesStrongBaseReadsAndNormalizesLegacyFlags()
    {
        var client = new Mock<IAmazonDynamoDB>();
        var requests = new List<ScanRequest>();
        var cursor = new Dictionary<string, AttributeValue> { ["guestSessionId"] = S("cursor") };
        var legacy = Item("submitted", N(1), N(1));
        var unsubmitted = Item("unsubmitted", new() { BOOL = false }, new() { BOOL = false });
        client.Setup(value => value.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ScanRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return Task.FromResult(requests.Count == 1
                    ? new ScanResponse { Items = [legacy], LastEvaluatedKey = cursor }
                    : new ScanResponse { Items = [unsubmitted] });
            });
        var context = new Mock<IDynamoDBContext>();
        context.Setup(value => value.FromDocument<GuestSession>(It.IsAny<Document>())).Returns((Document document) =>
        {
            var map = document.ToAttributeMap();
            return new GuestSession
            {
                Id = map["guestSessionId"].S, GuestName = map["guestName"].S,
                GroupReservationId = map["groupReservationId"].S, Dishes = [],
                IsSubmitted = map["isSubmitted"].BOOL == true, IsOwnerSession = map["isOwnerSession"].BOOL == true
            };
        });

        var sessions = await new GuestSessionRepository(context.Object, client.Object).GetForGroupSubmissionAsync(GroupId);

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].IsSubmitted);
        Assert.True(sessions[0].IsOwnerSession);
        Assert.False(sessions[1].IsSubmitted);
        Assert.False(sessions[1].IsOwnerSession);
        Assert.Equal("1", legacy["isSubmitted"].N);
        Assert.Equal(2, requests.Count);
        Assert.Same(cursor, requests[1].ExclusiveStartKey);
        Assert.All(requests, request =>
        {
            Assert.Equal("GuestSessions", request.TableName);
            Assert.True(request.ConsistentRead == true);
            Assert.Equal("#group = :group", request.FilterExpression);
            Assert.Equal("groupReservationId", request.ExpressionAttributeNames["#group"]);
            Assert.Equal(GroupId, request.ExpressionAttributeValues[":group"].S);
        });
        client.Verify(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(value => value.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Dictionary<string, AttributeValue> Item(string id, AttributeValue submitted, AttributeValue owner) => new()
    {
        ["guestSessionId"] = S(id), ["guestName"] = S(id), ["groupReservationId"] = S(GroupId),
        ["isSubmitted"] = submitted, ["isOwnerSession"] = owner
    };
}
