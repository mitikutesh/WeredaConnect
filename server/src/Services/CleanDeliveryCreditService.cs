using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-10: one trade the auto-release just paid. <see cref="PartyIds"/> are the buyer, the farmer and
/// the trucker when present (empty and repeated ids are ignored). <see cref="ReleasedAt"/> is the
/// escrow's stored release time, which the credit key is built from.
/// </summary>
internal sealed record CleanDeliveryTrade(
    string EscrowId, string? OrderId, string? DeliveryId, IReadOnlyList<string?> PartyIds, string? ReleasedAt);

/// <summary>
/// FRD-10: what <see cref="CleanDeliveryCreditService.CreditAsync"/> did for one trade.
/// <see cref="NotCleanReason"/> is set when no credit was written because the trade was not clean (or
/// could not be shown to be); otherwise <see cref="Credited"/> lists the users a row was written for,
/// <see cref="AlreadyCredited"/> counts the users whose row was already there (a replay), and
/// <see cref="Failed"/> the users whose write failed, with the exception type.
/// </summary>
internal sealed record CleanDeliveryCreditOutcome(
    string? NotCleanReason,
    IReadOnlyList<string> Credited,
    int AlreadyCredited,
    IReadOnlyList<(string UserId, string Error)> Failed)
{
    public static CleanDeliveryCreditOutcome NotClean(string reason) =>
        new(reason, Array.Empty<string>(), 0, Array.Empty<(string, string)>());
}

/// <summary>
/// FRD-10: the score decay of fraud-detection-architecture.md sections 15 and 20. When the auto-release
/// pays an escrow whose trade was clean (no dispute, no hold and no signal on its order, delivery or
/// escrow), each party gets one <c>CLEAN_DELIVERY</c> credit row of weight minus 5: the buyer, the
/// farmer, and the trucker when present. The score adds the credits in its window up to
/// <c>RISK_CREDIT_CAP</c> and never goes below 0 (section 14.2, <see cref="RiskScoreCalculator"/>).
/// Credit rows never go through <see cref="FraudDetectionService.RecordFraudSignalAsync"/>: its key is
/// the start of the hour, so two clean deliveries in one hour would fold into one credit. The key here
/// is the escrow's release time and its id, <c>{releasedAt}#CLEAN_DELIVERY#{escrowId}</c>, written with
/// <c>attribute_not_exists(signalKey)</c>, so one escrow credits each party once however often it is
/// replayed. No Scan: one Query per party on its own partition, then one put per party.
/// </summary>
internal sealed class CleanDeliveryCreditService
{
    internal const string SignalType = "CLEAN_DELIVERY";
    internal const int CreditWeight = -5;

    /// <summary>The escrow row records a dispute (<c>disputedBy</c> or <c>disputeResolution</c>).</summary>
    internal const string NotCleanDispute = "dispute";

    /// <summary>The escrow row records a fraud hold (<c>heldAt</c> or <c>holdReason</c>).</summary>
    internal const string NotCleanHold = "hold";

    /// <summary>A party's partition holds a signal on the order, the delivery or the escrow.</summary>
    internal const string NotCleanSignal = "signal";

    /// <summary>A party's partition had more rows than the page cap: fails closed, no credit.</summary>
    internal const string NotCleanUnread = "signals_unread";

    /// <summary>The release time is missing or does not parse, so no stable key can be built.</summary>
    internal const string NotCleanNoReleaseTime = "no_release_time";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _fraudSignalsTable;

    public CleanDeliveryCreditService(IAmazonDynamoDB dynamoDb, string fraudSignalsTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        ArgumentException.ThrowIfNullOrEmpty(fraudSignalsTable);
        _fraudSignalsTable = fraudSignalsTable;
    }

    /// <summary>Pages of one party's partition read at most before the trade counts as unread.</summary>
    internal int MaxPages { get; init; } = FraudDetectionService.DefaultRiskMaxPages;

    /// <summary>
    /// Why the escrow row itself rules a credit out, or null. The auto-release only pays an
    /// <c>escrowed</c> escrow, and nothing moves a disputed or held escrow back there, so this is a
    /// guard against rows written by older code: any dispute or hold field on the row means no credit.
    /// </summary>
    internal static string? EscrowNotCleanReason(Dictionary<string, object> escrow)
    {
        if (HasValue(escrow, "disputedBy") || HasValue(escrow, "disputeResolution"))
        {
            return NotCleanDispute;
        }

        return HasValue(escrow, EscrowHoldFields.HeldAt) || HasValue(escrow, EscrowHoldFields.HoldReason)
            ? NotCleanHold
            : null;
    }

    /// <summary>
    /// Credits each party of a clean trade. First reads every party's partition for a signal row on the
    /// order, the delivery or the escrow (one Query each over the table's 30 day life, at most
    /// <see cref="MaxPages"/> pages); a signal an admin approved (false positive) or whitelisted does not
    /// count, any other signal row does, whatever its weight. Then writes the credit rows. A read that
    /// throws propagates and nothing is written; a failed write is reported per user and the other
    /// parties are still written.
    /// </summary>
    public async Task<CleanDeliveryCreditOutcome> CreditAsync(CleanDeliveryTrade trade, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(trade.EscrowId);
        if (!DateTime.TryParse(trade.ReleasedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var releasedAt))
        {
            return CleanDeliveryCreditOutcome.NotClean(NotCleanNoReleaseTime);
        }

        var parties = trade.PartyIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var entities = new[] { trade.EscrowId, trade.OrderId, trade.DeliveryId }
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var utcNow = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        var sinceKey = utcNow.AddDays(-FraudDetectionService.SignalTtlDays).ToString("o", CultureInfo.InvariantCulture);

        foreach (var userId in parties)
        {
            if (await ReadTradeSignalAsync(userId, entities, sinceKey) is { } reason)
            {
                return CleanDeliveryCreditOutcome.NotClean(reason);
            }
        }

        var signalKey = CreditKey(releasedAt, trade.EscrowId);
        var expiresAt = new DateTimeOffset(releasedAt).AddDays(FraudDetectionService.SignalTtlDays).ToUnixTimeSeconds();
        var createdAt = utcNow.ToString("o", CultureInfo.InvariantCulture);
        var credited = new List<string>();
        var already = 0;
        var failed = new List<(string, string)>();
        foreach (var userId in parties)
        {
            try
            {
                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _fraudSignalsTable,
                    Item = CreditRow(userId, signalKey, trade.EscrowId, createdAt, expiresAt),
                    ConditionExpression = "attribute_not_exists(signalKey)"
                });
                credited.Add(userId);
            }
            catch (ConditionalCheckFailedException)
            {
                already++; // this escrow already credited this user: a replay, not an error
            }
            catch (Exception ex)
            {
                failed.Add((userId, ex.GetType().Name));
            }
        }

        return new CleanDeliveryCreditOutcome(null, credited, already, failed);
    }

    /// <summary>The credit row's sort key: the release time first, so the score's window applies to it.</summary>
    internal static string CreditKey(DateTime releasedAtUtc, string escrowId) =>
        $"{releasedAtUtc.ToString("o", CultureInfo.InvariantCulture)}#{SignalType}#{escrowId}";

    /// <summary>
    /// Section 20: a credit row carries no <c>status</c>, so the admin list, the dashboard count and the
    /// review action (which take signal rows only) never show or touch it.
    /// </summary>
    private static Dictionary<string, AttributeValue> CreditRow(
        string userId, string signalKey, string escrowId, string createdAt, long expiresAt) => new()
    {
        { "userId", new AttributeValue { S = userId } },
        { "signalKey", new AttributeValue { S = signalKey } },
        { "recordType", new AttributeValue { S = FraudDetectionService.RecordTypeCredit } },
        { "signalType", new AttributeValue { S = SignalType } },
        { "weight", new AttributeValue { N = CreditWeight.ToString(CultureInfo.InvariantCulture) } },
        { "entityType", new AttributeValue { S = "escrow" } },
        { "entityId", new AttributeValue { S = escrowId } },
        { "createdAt", new AttributeValue { S = createdAt } },
        { "expiresAt", new AttributeValue { N = expiresAt.ToString(CultureInfo.InvariantCulture) } }
    };

    /// <summary>
    /// Null when the user's partition holds no counting signal row on the trade's entities, else the
    /// reason. The filter runs after the read, so the pages are bounded by <see cref="MaxPages"/>, not by
    /// the matches; any match ends the read.
    /// </summary>
    private async Task<string?> ReadTradeSignalAsync(string userId, IReadOnlyList<string> entities, string sinceKey)
    {
        var names = new Dictionary<string, string>
        {
            { "#u", "userId" },
            { "#k", "signalKey" },
            { "#e", "entityId" },
            { "#rt", "recordType" },
            { "#r", "resolution" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":uid", new AttributeValue { S = userId } },
            { ":since", new AttributeValue { S = sinceKey } },
            { ":signal", new AttributeValue { S = FraudDetectionService.RecordTypeSignal } },
            { ":approve", new AttributeValue { S = "approve" } },
            { ":whitelist", new AttributeValue { S = "whitelist" } }
        };
        var placeholders = new List<string>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            placeholders.Add($":e{i}");
            values[$":e{i}"] = new AttributeValue { S = entities[i] };
        }

        Dictionary<string, AttributeValue>? startKey = null;
        var pages = 0;
        do
        {
            var request = new QueryRequest
            {
                TableName = _fraudSignalsTable,
                KeyConditionExpression = "#u = :uid AND #k >= :since",
                // Signal rows only (credit and whitelist rows are not signals), on this trade, not cleared
                // by an admin: the rows the score counts (section 14.2).
                FilterExpression = $"#e IN ({string.Join(", ", placeholders)})"
                    + " AND (attribute_not_exists(#rt) OR #rt = :signal)"
                    + " AND (attribute_not_exists(#r) OR NOT (#r IN (:approve, :whitelist)))",
                ProjectionExpression = "#k",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                ConsistentRead = false
            };
            if (startKey != null)
            {
                request.ExclusiveStartKey = startKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            pages++;
            if (response.Items is { Count: > 0 })
            {
                return NotCleanSignal;
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && pages < MaxPages);

        return startKey != null ? NotCleanUnread : null;
    }

    private static bool HasValue(Dictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value?.ToString());
}
