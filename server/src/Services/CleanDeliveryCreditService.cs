using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

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
/// FUP-137: the credit follows the write of the escrow's <c>releasedAt</c>. Where the gateway pays, that is
/// the auto-release itself (<see cref="CreditAsync"/>, called by escrow-cron). Where an admin pays the legs
/// by hand, it is the manual payout close that writes <c>releasedAt</c>, and the crash recovery of
/// escrow-cron reads a released row again: both go through <see cref="CreditReleasedAsync"/>, which
/// credits only a row the auto-release marked (<see cref="OriginField"/>).
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

    /// <summary>FUP-137: the row was not released by the auto-release (no <see cref="OriginField"/> marker).</summary>
    internal const string NotCleanNotAutoRelease = "not_auto_release";

    /// <summary>FUP-137: the row is not <c>released</c> or <c>completed</c>, so no money is on record as paid.</summary>
    internal const string NotCleanNotReleased = "not_released";

    /// <summary>
    /// FUP-137: the escrow attribute that says who started the release this row records or waits for.
    /// <see cref="OriginAutoRelease"/> is written by the release itself, in the write that settles the row
    /// (the gateway paid) or queues it for a manual payout (the gateway could not), and only for the hourly
    /// auto-release: the system actor, trigger <c>delivery_confirmed</c>, from <c>escrowed</c>. An admin
    /// release, a dispute verdict, a held exit and a refund never write it. A ledger escrow can be decided
    /// more than once, so every release decision writes it (<see cref="OriginOther"/> when not the
    /// auto-release). A stale settlement the recovery step parks has no origin and earns no credit.
    /// </summary>
    internal const string OriginField = ReleaseOriginFields.Field;
    internal const string OriginAutoRelease = ReleaseOriginFields.AutoRelease;
    internal const string OriginOther = ReleaseOriginFields.Other;

    /// <summary>FUP-137: log lines of the credit (not alarmed; a lost credit only means a higher score).</summary>
    internal const string CreditedToken = "CLEAN_DELIVERY_CREDITED";
    internal const string NotCreditedToken = "CLEAN_DELIVERY_NOT_CREDITED";
    internal const string CreditFailedToken = "CLEAN_DELIVERY_CREDIT_FAILED";

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
    /// FUP-137: <see cref="OriginAutoRelease"/> when a release by <paramref name="actor"/> with
    /// <paramref name="trigger"/> that settles from <paramref name="settledFrom"/> is the hourly
    /// auto-release of a confirmed delivery, else null.
    /// </summary>
    internal static string? OriginOf(string actor, string trigger, string settledFrom) =>
        actor == EscrowService.SystemActor && trigger == "delivery_confirmed" && settledFrom == "escrowed"
            ? OriginAutoRelease
            : null;

    /// <summary>
    /// FUP-137: credits the parties of an escrow row read after its release was recorded: the manual
    /// payout close that wrote <c>releasedAt</c> (live mode), or a row escrow-cron reads again after a run
    /// that died between the release and the credit. Only a <c>released</c> or <c>completed</c> row the
    /// auto-release marked (<see cref="OriginField"/>) with no dispute or hold field is credited, with the
    /// key of <see cref="CreditAsync"/> (the stored <c>releasedAt</c> and the escrow id), so the close, its
    /// replay, a second admin and the recovery write each party's row once between them.
    /// <paramref name="resolveTruckerId"/> finds the trucker of a row that pays one and names none.
    /// </summary>
    public async Task<CleanDeliveryCreditOutcome> CreditReleasedAsync(
        Dictionary<string, object> escrow, Func<Dictionary<string, object>, Task<string?>> resolveTruckerId,
        DateTime nowUtc)
    {
        if (Value(escrow, "status") is not ("released" or "completed"))
        {
            return CleanDeliveryCreditOutcome.NotClean(NotCleanNotReleased);
        }

        if (Value(escrow, OriginField) != OriginAutoRelease)
        {
            return CleanDeliveryCreditOutcome.NotClean(NotCleanNotAutoRelease);
        }

        if (EscrowNotCleanReason(escrow) is { } reason)
        {
            return CleanDeliveryCreditOutcome.NotClean(reason);
        }

        var truckerId = Value(escrow, "truckerId");
        if (truckerId == null && PartySuspensionGate.HasTruckerLeg(escrow))
        {
            truckerId = await resolveTruckerId(escrow);
        }

        return await CreditAsync(
            new CleanDeliveryTrade(
                Value(escrow, "escrowId") ?? "",
                Value(escrow, "orderId"),
                Value(escrow, "deliveryId"),
                new[] { Value(escrow, "buyerId"), Value(escrow, "farmerId"), truckerId },
                Value(escrow, "releasedAt")),
            nowUtc);
    }

    /// <summary>
    /// FUP-137: the log lines of one outcome, shared by escrow-cron and the admin's manual payout close.
    /// Returns true when the credit is settled (written, already there, or ruled out) and false when a
    /// write failed, so the caller may try the escrow again.
    /// </summary>
    internal static bool LogOutcome(
        string escrowId, CleanDeliveryCreditOutcome outcome, Action<string> info, Action<string> error)
    {
        if (outcome.NotCleanReason != null)
        {
            info($"{NotCreditedToken} escrowId={MoneyAlarmLog.LogId(escrowId)} reason={MoneyAlarmLog.LogId(outcome.NotCleanReason)}");
            return true;
        }

        foreach (var (userId, failure) in outcome.Failed)
        {
            error($"{CreditFailedToken} escrowId={MoneyAlarmLog.LogId(escrowId)} userId={MoneyAlarmLog.LogId(userId)} error={MoneyAlarmLog.LogId(failure)}");
        }

        info($"{CreditedToken} escrowId={MoneyAlarmLog.LogId(escrowId)} credited={outcome.Credited.Count} alreadyCredited={outcome.AlreadyCredited} failed={outcome.Failed.Count}");
        return outcome.Failed.Count == 0;
    }

    /// <summary>
    /// Credits each party of a clean trade. First reads every party's partition for a signal row on the
    /// order, the delivery or the escrow (one Query each over the table's 30 day life, at most
    /// <see cref="MaxPages"/> pages); a signal an admin approved (false positive) or whitelisted does not
    /// count, any other signal row does, whatever its weight. A row is a signal unless it is a credit or
    /// the whitelist row, as the score reads it (FUP-137: a <c>recordType</c> nobody knows blocks the
    /// credit, it never lowers a score unseen). Then writes the credit rows. A read that
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
            { ":credit", new AttributeValue { S = FraudDetectionService.RecordTypeCredit } },
            { ":whitelistRow", new AttributeValue { S = FraudDetectionService.RecordTypeWhitelist } },
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
                // Every row but a credit or the whitelist row, on this trade, not cleared by an admin: the
                // rows the score counts (section 14.2). FUP-137: as RiskScoreCalculator.Add, a recordType
                // other than those two is a signal, whatever its value (fails closed).
                FilterExpression = $"#e IN ({string.Join(", ", placeholders)})"
                    + " AND (attribute_not_exists(#rt) OR NOT (#rt IN (:credit, :whitelistRow)))"
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

    private static bool HasValue(Dictionary<string, object> row, string key) => Value(row, key) != null;

    /// <summary>The attribute as a string, or null when it is missing or empty.</summary>
    private static string? Value(Dictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;
}
