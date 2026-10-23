using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB escrow audit repository using low-level API for AOT compatibility.
/// APPEND-ONLY: Only PutItem and Query — NO UpdateItem, NO DeleteItem.
///
/// Table schema (EscrowAudit):
///   PK: escrowId (STRING)
///   SK: timestamp (STRING)
/// </summary>
public sealed class EscrowAuditRepository : IEscrowAuditRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public EscrowAuditRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task AppendAsync(Dictionary<string, object> auditEntry)
    {
        var item = new Dictionary<string, AttributeValue>();

        foreach (var kvp in auditEntry)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        };

        await _dynamoDb.PutItemAsync(request);
    }

    /// <summary>
    /// The auditId of a row written at a deterministic key (ESC-02). Every writer of such a
    /// row (the escrow service and the escrow-cron crash recovery) uses it, so the same
    /// event is never stored twice.
    /// </summary>
    public static string DeterministicAuditId(string action, string timestamp)
        => $"audit-{action}-{new string(timestamp.Where(char.IsAsciiLetterOrDigit).ToArray())}";

    /// <inheritdoc />
    public async Task<bool> AppendIfAbsentAsync(Dictionary<string, object> auditEntry)
    {
        var item = new Dictionary<string, AttributeValue>();
        foreach (var kvp in auditEntry)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            // Still append-only: an existing row with this key is never overwritten.
            ConditionExpression = "attribute_not_exists(escrowId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId)
        => GetAuditTrailAsync(escrowId, consistentRead: false);

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId, bool consistentRead)
    {
        var allItems = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "escrowId = :escrowId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":escrowId", new AttributeValue { S = escrowId } }
                },
                ScanIndexForward = true, // oldest first for audit trail
                ConsistentRead = consistentRead
            };

            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            allItems.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return allItems;
    }

    /// <summary>
    /// Upper bound on items read by the cross-escrow recent-audit Scan. Bounds
    /// the cost at MVP scale; if the table grows past this, migrate the
    /// admin audit feed to a GSI (constant-partition or date-bucketed PK with a
    /// timestamp sort key) + a dedicated query so reads stay cheap and ordered.
    /// </summary>
    private const int MaxScanItems = 2000;

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetRecentAcrossEscrowsAsync(int limit)
    {
        // MVP-scale implementation: the audit table has no GSI for a global
        // feed, so Scan the table, then sort newest-first in memory. Bounded by
        // MaxScanItems to cap cost; see the const above for the scale path.
        var items = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        var capped = false;

        do
        {
            var request = new ScanRequest { TableName = _tableName };
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);
            items.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey is { Count: > 0 }
                ? response.LastEvaluatedKey
                : null;

            if (items.Count >= MaxScanItems)
            {
                capped = true;
                break;
            }
        } while (lastKey != null);

        // timestamp is an ISO-8601 string, so ordinal string compare == chronological.
        items.Sort((a, b) => string.CompareOrdinal(GetTimestamp(b), GetTimestamp(a)));

        if (capped)
        {
            // Surface the cap rather than silently truncating older history.
            Console.WriteLine(
                $"[EscrowAuditRepository] Recent-audit Scan hit the {MaxScanItems}-item cap; " +
                "older entries may be omitted. Migrate the global audit feed to a GSI.");
        }

        return limit < items.Count ? items.GetRange(0, limit) : items;
    }

    /// <inheritdoc />
    public async Task<EscrowAuditKeyPage> ScanKeysWithActionsAsync(
        IReadOnlyCollection<string> actions,
        Dictionary<string, AttributeValue>? exclusiveStartKey,
        int pageSize)
    {
        var values = new Dictionary<string, AttributeValue>();
        var placeholders = new List<string>();
        foreach (var action in actions)
        {
            var placeholder = $":a{placeholders.Count}";
            placeholders.Add(placeholder);
            values[placeholder] = new AttributeValue { S = action };
        }

        var request = new ScanRequest
        {
            TableName = _tableName,
            // Limit counts rows evaluated before the filter, so it bounds the read cost of a page.
            Limit = pageSize,
            FilterExpression = $"#action IN ({string.Join(", ", placeholders)})",
            ProjectionExpression = "escrowId, #ts, #action",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#action", "action" },
                { "#ts", "timestamp" }
            },
            ExpressionAttributeValues = values
        };
        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.ScanAsync(request);
        // Rows of one escrow id are contiguous in scan order, so its last row on the page is the
        // key a caller can resume after.
        var hits = new List<EscrowAuditKeyHit>();
        foreach (var item in response.Items ?? new List<Dictionary<string, AttributeValue>>())
        {
            if (!item.TryGetValue("escrowId", out var id) || string.IsNullOrEmpty(id.S))
            {
                continue;
            }

            var timestamp = item.TryGetValue("timestamp", out var ts) ? ts.S ?? "" : "";
            var index = hits.FindIndex(h => h.EscrowId == id.S);
            if (index < 0)
            {
                hits.Add(new EscrowAuditKeyHit(id.S, timestamp));
            }
            else
            {
                hits[index] = hits[index] with { LastTimestamp = timestamp };
            }
        }

        return new EscrowAuditKeyPage(
            hits,
            response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<AuditClaimWriteResult> AppendWithClaimAsync(
        Dictionary<string, object> auditEntry, string claimTable, Dictionary<string, object> claimItem)
    {
        var request = new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new()
                {
                    Put = new Put
                    {
                        TableName = _tableName,
                        Item = ToItem(auditEntry),
                        // Still append-only: an existing row with this key is never overwritten.
                        ConditionExpression = "attribute_not_exists(escrowId)"
                    }
                },
                new()
                {
                    Put = new Put
                    {
                        TableName = claimTable,
                        Item = ToItem(claimItem),
                        ConditionExpression = "attribute_not_exists(idempotencyKey)"
                    }
                }
            }
        };

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request);
            return AuditClaimWriteResult.Written;
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order: 0 the audit row, 1 the claim.
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            string? Code(int index) => reasons.Count > index ? reasons[index]?.Code : null;
            if (Code(1) == "ConditionalCheckFailed")
            {
                return AuditClaimWriteResult.ClaimTaken;
            }

            if (Code(0) == "ConditionalCheckFailed")
            {
                return AuditClaimWriteResult.EntryExists;
            }

            if (reasons.Any(r => r?.Code == "TransactionConflict"))
            {
                return AuditClaimWriteResult.Contended;
            }

            // Throttling or a validation error: nothing was written, so a retry is safe.
            throw;
        }
    }

    private static Dictionary<string, AttributeValue> ToItem(Dictionary<string, object> entry)
    {
        var item = new Dictionary<string, AttributeValue>();
        foreach (var kvp in entry)
        {
            if (kvp.Value != null)
            {
                item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            }
        }

        return item;
    }

    private static string GetTimestamp(Dictionary<string, object> item) =>
        item.TryGetValue("timestamp", out var v) ? v?.ToString() ?? string.Empty : string.Empty;
}
