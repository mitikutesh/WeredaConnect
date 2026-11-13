using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-137: the escrows whose clean-delivery credit escrow-cron still owes, so a run that dies between an
/// auto-release and its credit (FRD-10) does not lose the credit. One item in the escrow idempotency table
/// (key <see cref="Key"/>, like the cron's other markers there) holding a string set of escrow ids: the id
/// is added before the release is called and removed once the credit is settled, or once the release's answer
/// shows the cron owes none (FUP-155: blocked by the payout gate, or a 1.x escrow queued for a manual payout,
/// whose close credits). The next hourly run reads the item (one <c>GetItem</c>, no Query or Scan) and
/// decides each id left behind from its escrow row.
/// The journal only says which rows to read again: the credit key (<c>releasedAt</c> and the escrow id)
/// keeps every replay to one row per party, and the escrow row decides whether a credit is due at all.
/// </summary>
internal sealed class CleanDeliveryCreditJournal
{
    internal const string Key = "system:fraud:clean_delivery_credit:pending";
    internal const string PendingAttribute = "pendingEscrowIds";

    /// <summary>Ids the set holds at most; a full journal refuses the add and that release is not journaled.</summary>
    internal const int MaxPending = 500;

    private const int TtlDays = 30;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _idempotencyTable;

    public CleanDeliveryCreditJournal(IAmazonDynamoDB dynamoDb, string idempotencyTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        ArgumentException.ThrowIfNullOrEmpty(idempotencyTable);
        _idempotencyTable = idempotencyTable;
    }

    /// <summary>Adds the escrow to the set. False when the journal is full (nothing written).</summary>
    public async Task<bool> AddAsync(string escrowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(escrowId);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(TtlDays).ToUnixTimeSeconds();
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _idempotencyTable,
                Key = ItemKey(),
                UpdateExpression = "ADD #pending :ids SET #expires = :expires",
                ConditionExpression = "attribute_not_exists(#pending) OR size(#pending) < :max",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#pending", PendingAttribute },
                    { "#expires", "expiresAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":ids", new AttributeValue { SS = new List<string> { escrowId } } },
                    { ":expires", new AttributeValue { N = expiresAt.ToString(CultureInfo.InvariantCulture) } },
                    { ":max", new AttributeValue { N = MaxPending.ToString(CultureInfo.InvariantCulture) } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>Removes the escrow from the set (a no-op when it is not there).</summary>
    public async Task RemoveAsync(string escrowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(escrowId);
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _idempotencyTable,
            Key = ItemKey(),
            UpdateExpression = "DELETE #pending :ids",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#pending", PendingAttribute } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":ids", new AttributeValue { SS = new List<string> { escrowId } } }
            }
        });
    }

    /// <summary>The escrow ids in the set, in a stable order (strongly consistent read).</summary>
    public async Task<IReadOnlyList<string>> ReadAsync()
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _idempotencyTable,
            Key = ItemKey(),
            ConsistentRead = true
        });
        if (response.Item == null || !response.Item.TryGetValue(PendingAttribute, out var pending) || pending.SS == null)
        {
            return Array.Empty<string>();
        }

        return pending.SS.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, AttributeValue> ItemKey() => new()
    {
        { "idempotencyKey", new AttributeValue { S = Key } }
    };
}
