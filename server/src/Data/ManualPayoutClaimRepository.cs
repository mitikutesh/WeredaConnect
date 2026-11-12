using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-05: the claim writes of the manual payout queue and the read of a reference item. Every claim write
/// is one conditional UpdateItem on the row that holds the leg (the escrow row for a 1.x leg, the ledger row
/// for a ledger leg); the leg's money attributes are never touched here. Each write returns false when its
/// condition failed or when the row stayed busy with a transaction (a record in flight) after one retry; the
/// caller reads the row again and decides.
/// </summary>
internal interface IManualPayoutClaimRepository
{
    /// <summary>
    /// Claim or renew a pending 1.x leg: written only while the queue is open for <paramref name="op"/>, the
    /// leg is pending and no other admin holds a live claim. False when the condition failed.
    /// </summary>
    Task<bool> TryClaimLegAsync(string escrowId, string createdAt, string op, string leg, string actor, string now, string expiresAt);

    /// <summary>Remove the holder's claim on a 1.x leg (expired or not). False when the holder is someone else or nobody.</summary>
    Task<bool> TryReleaseLegAsync(string escrowId, string createdAt, string leg, string actor);

    /// <summary>Claim or renew a ledger row while it is <c>manual</c> and no other admin holds a live claim.</summary>
    Task<bool> TryClaimLedgerRowAsync(string escrowId, string reference, string actor, string now, string expiresAt);

    /// <summary>Remove the holder's claim on a ledger row.</summary>
    Task<bool> TryReleaseLedgerRowAsync(string escrowId, string reference, string actor);

    /// <summary>Where a reference is recorded (a strongly consistent read of its item), or null.</summary>
    Task<PayoutReferenceHolder?> GetReferenceHolderAsync(string externalRef);
}

internal sealed class ManualPayoutClaimRepository : IManualPayoutClaimRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _escrowsTable;
    private readonly string? _ledgerTable;
    private readonly string _referenceTable;

    /// <summary>The wait before the one retry of a write that met a transaction in flight on the row; tests set 0.</summary>
    internal TimeSpan ConflictRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public ManualPayoutClaimRepository(IAmazonDynamoDB dynamoDb, string escrowsTable, string? ledgerTable, string referenceTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _escrowsTable = escrowsTable;
        _ledgerTable = string.IsNullOrEmpty(ledgerTable) ? null : ledgerTable;
        _referenceTable = referenceTable;
    }

    /// <inheritdoc />
    public Task<bool> TryClaimLegAsync(
        string escrowId, string createdAt, string op, string leg, string actor, string now, string expiresAt)
        => TryUpdateAsync(new UpdateItemRequest
        {
            TableName = _escrowsTable,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #claimBy = :actor, #claimAt = :now, #claimExp = :exp",
            ConditionExpression = "#marker = :open AND #op = :op AND #legStatus = :pending"
                                  + " AND (attribute_not_exists(#claimBy) OR #claimBy = :actor OR #claimExp < :now)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#marker"] = ManualPayoutFields.Marker,
                ["#op"] = ManualPayoutFields.Op,
                ["#legStatus"] = ManualPayoutFields.StatusAttr(leg),
                ["#claimBy"] = ManualPayoutClaimFields.ClaimedByAttr(leg),
                ["#claimAt"] = ManualPayoutClaimFields.ClaimedAtAttr(leg),
                ["#claimExp"] = ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":open"] = S(ManualPayoutFields.MarkerOpen),
                [":op"] = S(op),
                [":pending"] = S(ManualPayoutFields.LegPending),
                [":actor"] = S(actor),
                [":now"] = S(now),
                [":exp"] = S(expiresAt)
            }
        });

    /// <inheritdoc />
    public Task<bool> TryReleaseLegAsync(string escrowId, string createdAt, string leg, string actor)
        => TryUpdateAsync(new UpdateItemRequest
        {
            TableName = _escrowsTable,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "REMOVE #claimBy, #claimAt, #claimExp",
            ConditionExpression = "#claimBy = :actor",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#claimBy"] = ManualPayoutClaimFields.ClaimedByAttr(leg),
                ["#claimAt"] = ManualPayoutClaimFields.ClaimedAtAttr(leg),
                ["#claimExp"] = ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":actor"] = S(actor) }
        });

    /// <inheritdoc />
    public Task<bool> TryClaimLedgerRowAsync(string escrowId, string reference, string actor, string now, string expiresAt)
        => TryUpdateAsync(new UpdateItemRequest
        {
            TableName = LedgerTable(),
            Key = LedgerKey(escrowId, reference),
            UpdateExpression = "SET #claimBy = :actor, #claimAt = :now, #claimExp = :exp",
            ConditionExpression = "#status = :manual"
                                  + " AND (attribute_not_exists(#claimBy) OR #claimBy = :actor OR #claimExp < :now)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = LedgerFields.Status,
                ["#claimBy"] = ManualPayoutClaimFields.LedgerClaimedBy,
                ["#claimAt"] = ManualPayoutClaimFields.LedgerClaimedAt,
                ["#claimExp"] = ManualPayoutClaimFields.LedgerClaimExpiresAt
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":manual"] = S(LedgerFields.StatusManual),
                [":actor"] = S(actor),
                [":now"] = S(now),
                [":exp"] = S(expiresAt)
            }
        });

    /// <inheritdoc />
    public Task<bool> TryReleaseLedgerRowAsync(string escrowId, string reference, string actor)
        => TryUpdateAsync(new UpdateItemRequest
        {
            TableName = LedgerTable(),
            Key = LedgerKey(escrowId, reference),
            UpdateExpression = "REMOVE #claimBy, #claimAt, #claimExp",
            ConditionExpression = "#claimBy = :actor",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#claimBy"] = ManualPayoutClaimFields.LedgerClaimedBy,
                ["#claimAt"] = ManualPayoutClaimFields.LedgerClaimedAt,
                ["#claimExp"] = ManualPayoutClaimFields.LedgerClaimExpiresAt
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":actor"] = S(actor) }
        });

    /// <inheritdoc />
    public async Task<PayoutReferenceHolder?> GetReferenceHolderAsync(string externalRef)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _referenceTable,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(PayoutReferenceFields.Key(externalRef)) },
            ConsistentRead = true
        });
        if (response.Item is not { Count: > 0 } item)
        {
            return null;
        }

        string? Str(string name) => item.TryGetValue(name, out var value) && value.S != null ? value.S : null;
        return new PayoutReferenceHolder(
            Str(PayoutReferenceFields.EscrowId) ?? "", Str(PayoutReferenceFields.Ref) ?? "",
            Str(PayoutReferenceFields.RecordedBy), Str(PayoutReferenceFields.RecordedAt));
    }

    private async Task<bool> TryUpdateAsync(UpdateItemRequest request)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _dynamoDb.UpdateItemAsync(request);
                return true;
            }
            catch (ConditionalCheckFailedException)
            {
                return false;
            }
            catch (TransactionConflictException) when (attempt < 2)
            {
                // A record's transaction is in flight on the same row: wait once and try again.
                await Task.Delay(ConflictRetryDelay);
            }
            catch (TransactionConflictException)
            {
                return false;
            }
        }
    }

    private string LedgerTable()
        => _ledgerTable ?? throw new InvalidOperationException("The escrow ledger table is not configured.");

    private static Dictionary<string, AttributeValue> EscrowKey(string escrowId, string createdAt) => new()
    {
        ["escrowId"] = S(escrowId),
        ["createdAt"] = S(createdAt)
    };

    private static Dictionary<string, AttributeValue> LedgerKey(string escrowId, string reference) => new()
    {
        [LedgerFields.EscrowId] = S(escrowId),
        [LedgerFields.Ref] = S(reference)
    };

    private static AttributeValue S(string value) => new() { S = value };
}
