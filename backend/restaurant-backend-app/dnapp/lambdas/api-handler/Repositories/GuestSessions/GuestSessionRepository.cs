using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public partial class GuestSessionRepository(IDynamoDBContext context, IAmazonDynamoDB client) : IGuestSessionRepository
{
    public async Task<GuestSession?> GetByGuestSessionId(string id) => await context.LoadAsync<GuestSession?>(id, new LoadConfig()
    {
        OverrideTableName = ResourceNames.GuestSessionTableName,
        ConsistentRead = true
    });

    public async Task<GuestSession?> GetByGroupReservationId(string groupReservationId)
    {
        List<GuestSession> result = await context.QueryAsync<GuestSession>(groupReservationId, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.GroupReservationId
        }).GetRemainingAsync();

        return result.FirstOrDefault();
    }

    public async Task<GuestSession?> GetByTokenHash(string tokenHash)
    {
        List<GuestSession> result = await context.QueryAsync<GuestSession>(tokenHash, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.SessionTokenHash
        }).GetRemainingAsync();

        return result.FirstOrDefault();
    }

    public async Task<List<GuestSession>> GetAllByGroupReservationIdAsync(string groupReservationId)
    {
        return await context.QueryAsync<GuestSession>(groupReservationId, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.GroupReservationId
        }).GetRemainingAsync();
    }

    public async Task SaveAsync(GuestSession guestSession)
    {
        try
        {
            var item = context.ToDocument(guestSession, new ToDocumentConfig { OverrideTableName = ResourceNames.GuestSessionTableName }).ToAttributeMap();
            var names = new Dictionary<string, string>();
            var values = new Dictionary<string, AttributeValue>();
            var sets = new List<string>();
            var removes = new List<string>();
            foreach (var attribute in item.Where(attribute => attribute.Key != "guestSessionId"))
            {
                string name = $"#field{names.Count}";
                names[name] = attribute.Key;
                if (attribute.Value.NULL == true)
                    removes.Add(name);
                else
                {
                    string value = $":value{values.Count}";
                    values[value] = attribute.Value;
                    sets.Add($"{name} = {value}");
                }
            }
            await client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = ResourceNames.GuestSessionTableName,
                Key = SessionKey(guestSession.Id),
                UpdateExpression = "SET " + string.Join(", ", sets) +
                    (removes.Count > 0 ? " REMOVE " + string.Join(", ", removes) : ""),
                ConditionExpression = "attribute_exists(guestSessionId)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            });
        }
        catch (ConditionalCheckFailedException)
        {
            throw new GuestSessionNotFoundException("The guest session has been deleted.");
        }
    }

    public async Task CreateOwnerSessionIfAbsentAsync(GuestSession guestSession)
    {
        try
        {
            var item = context.ToDocument(guestSession, new ToDocumentConfig { OverrideTableName = ResourceNames.GuestSessionTableName }).ToAttributeMap();
            item.Remove("guestSessionTokenHash");
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = ResourceNames.GuestSessionTableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(guestSessionId)"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // A concurrent initializer already created this deterministic owner item.
        }
    }

    public async Task<int> CountByGroupReservationId(string groupReservationId)
    {
        return (await ReadCapacityAsync(groupReservationId)).Count;
    }

    public async Task DeleteAsync(GuestSession session)
    {
        await DeleteWithCapacityAsync(session);
    }
}
