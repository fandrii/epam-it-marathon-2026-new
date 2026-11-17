using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public partial class GuestSessionRepository
{
    public async Task<List<GuestSession>> GetForGroupSubmissionAsync(string groupReservationId)
    {
        // A GSI can still contain an old isSubmitted value immediately after a
        // guest submits. This terminal operation reads the base table instead.
        var sessions = new List<GuestSession>();
        Dictionary<string, AttributeValue>? nextKey = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest
            {
                TableName = ResourceNames.GuestSessionTableName,
                ConsistentRead = true,
                FilterExpression = "#group = :group",
                ExpressionAttributeNames = new() { ["#group"] = "groupReservationId" },
                ExpressionAttributeValues = new() { [":group"] = new() { S = groupReservationId } },
                ExclusiveStartKey = nextKey
            });
            foreach (var item in page.Items ?? [])
            {
                var normalized = new Dictionary<string, AttributeValue>(item);
                foreach (var flag in new[] { "isSubmitted", "isOwnerSession" })
                    if (item.TryGetValue(flag, out var value) && value.N is "0" or "1")
                        normalized[flag] = new AttributeValue { BOOL = value.N == "1" };
                sessions.Add(context.FromDocument<GuestSession>(Document.FromAttributeMap(normalized)));
            }
            nextKey = page.LastEvaluatedKey;
        } while (nextKey is { Count: > 0 });
        return sessions;
    }
}
