using Amazon.DynamoDBv2.Model;
using APIHandler.Entities;
using System.Collections.Generic;

namespace APIHandler.Repositories.Orders;

// The exact persisted values are retained for compare-and-swap, including legacy
// items with omitted fields. Re-serializing the entity would normalize those fields.
public sealed record OrderSubmissionSnapshot(Order Order, Dictionary<string, AttributeValue> Attributes);
