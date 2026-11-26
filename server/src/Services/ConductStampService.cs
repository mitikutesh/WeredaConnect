using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;

namespace WeredaConnect.Server.Services;

/// <summary>
/// GCR-03 (reputation-api.md 1.9.0 section 1.8): writes the conduct stamp of a settled order, one
/// <c>HANDOFF_QUALITY</c> row per party (<see cref="ConductScoring"/>), and re-evaluates it once when a rating or a
/// handoff photo arrives late. Not flagged (stamping runs before <c>conductRewards</c> shows anything). Best effort:
/// <see cref="TryStampAsync"/> and <see cref="TryReevaluateAsync"/> never throw, so a failed stamp never blocks,
/// fails or delays a settlement, a rating or a photo; it is logged (<see cref="FailedToken"/>) and not retried later.
/// </summary>
public sealed class ConductStampService
{
    public const string StampedToken = "CONDUCT_STAMPED";
    public const string ReevaluatedToken = "CONDUCT_REEVALUATED";
    public const string SkippedToken = "CONDUCT_STAMP_SKIPPED";
    public const string FailedToken = "CONDUCT_STAMP_FAILED";

    /// <summary>Attempts of a settle stamp before it is given up (a replayed attempt finds the rows already written).</summary>
    public const int MaxAttempts = 3;

    /// <summary>A party's stamps read for the pair cap, at most.</summary>
    public const int StampsReadCap = 1000;

    private readonly IConductStampRepository _stamps;
    private readonly IDeliveryRepository _deliveries;
    private readonly IOrderRepository? _orders;
    private readonly IRatingRepository? _ratings;
    private readonly ITripLocationRepository? _pings;
    private readonly IFraudDetectionService? _fraud;
    private readonly TimeProvider _clock;
    private readonly Func<int, Task> _backoff;

    /// <param name="orders">Read by the re-evaluation only; without it nothing is re-evaluated.</param>
    /// <param name="ratings">Without it the buyer's rating step is left out.</param>
    /// <param name="pings">Without it the trucker's trip sharing step is left out.</param>
    /// <param name="fraud">Without it no order counts (<c>fraud_unchecked</c>).</param>
    public ConductStampService(
        IConductStampRepository stamps,
        IDeliveryRepository deliveries,
        IOrderRepository? orders,
        IRatingRepository? ratings,
        ITripLocationRepository? pings,
        IFraudDetectionService? fraud,
        TimeProvider? clock = null,
        Func<int, Task>? backoff = null)
    {
        _stamps = stamps ?? throw new ArgumentNullException(nameof(stamps));
        _deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
        _orders = orders;
        _ratings = ratings;
        _pings = pings;
        _fraud = fraud;
        _clock = clock ?? TimeProvider.System;
        _backoff = backoff ?? (attempt => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 150) * attempt)));
    }

    /// <summary>
    /// The reputation-stream's service from its environment: <c>REPUTATION_EVENTS_TABLE</c>, <c>USERS_TABLE</c> and
    /// <c>DELIVERIES_TABLE</c> are needed (null without one: a stack from before GCR-03 stamps nothing);
    /// <c>RATINGS_TABLE</c>, <c>TRIP_LOCATIONS_TABLE</c> and <c>FRAUD_SIGNALS_TABLE</c> are read when set.
    /// </summary>
    public static ConductStampService? FromEnvironment(IAmazonDynamoDB dynamoDb)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        if (Env("REPUTATION_EVENTS_TABLE") is not { } eventsTable || Env("USERS_TABLE") is not { } usersTable
            || Env("DELIVERIES_TABLE") is not { } deliveriesTable)
        {
            return null;
        }

        var fraudTable = Env("FRAUD_SIGNALS_TABLE");
        return new ConductStampService(
            new ReputationEventRepository(dynamoDb, eventsTable, usersTable),
            new DeliveryRepository(dynamoDb, deliveriesTable),
            orders: null,
            Env("RATINGS_TABLE") is { } ratingsTable ? new RatingRepository(dynamoDb, ratingsTable) : null,
            Env("TRIP_LOCATIONS_TABLE") is { } pingsTable ? new TripLocationRepository(dynamoDb, pingsTable) : null,
            fraudTable != null ? new FraudDetectionService(dynamoDb, "", fraudTable, new RateLimitRepository(dynamoDb, "")) : null);
    }

    // ===== on settle =====

    /// <summary>
    /// Stamp <paramref name="order"/> (just settled), up to <see cref="MaxAttempts"/> times on a failure. Never
    /// throws; returns the rows written (0 on a replay or a failure).
    /// </summary>
    public async Task<int> TryStampAsync(ConductOrder order, ILambdaLogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StampAsync(order, logger);
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning($"{FailedToken} order={MoneyAlarmLog.LogId(order.OrderId)} attempt={attempt} [{ex.GetType().Name}]");
                await _backoff(attempt);
            }
            catch (Exception ex)
            {
                logger.LogError($"{FailedToken} order={MoneyAlarmLog.LogId(order.OrderId)} attempt={attempt} gaveUp=true [{ex.GetType().Name}]");
                return 0;
            }
        }
    }

    /// <summary>
    /// One row per party of the settled order, each written only into a free slot, so settling twice (a stream
    /// replay, a retry) writes one row per party. A party with no users row or being deleted gets none.
    /// </summary>
    public async Task<int> StampAsync(ConductOrder order, ILambdaLogger logger)
    {
        var inputs = await ReadInputsAsync(order);
        var now = Now;
        var written = 0;
        foreach (var (party, userId) in ConductScoring.Parties(order))
        {
            var stamp = await BuildAsync(order, inputs, party, userId, now);
            var outcome = await _stamps.PutNewAsync(stamp);
            Log(logger, outcome, StampedToken, stamp);
            written += outcome == ConductWriteOutcome.Written ? 1 : 0;
        }

        return written;
    }

    // ===== late evidence (spec 8 item 8) =====

    /// <summary>
    /// A rating or a handoff photo arrived for <paramref name="orderId"/>: every stamp of <paramref name="partyIds"/>
    /// still within <see cref="ConductScoring.ReevaluationWindow"/> of its first write and not re-evaluated yet is
    /// evaluated again from the rows as they are now, and marked re-evaluated, so a second late arrival does nothing.
    /// An order not stamped yet (not settled) needs nothing: its stamp will see the evidence. The first id is the
    /// party whose evidence arrived: without a stamp of theirs nothing more is read (one read on the common path,
    /// evidence before the settle). Never throws.
    /// </summary>
    public async Task<int> TryReevaluateAsync(string orderId, IEnumerable<string?> partyIds, ILambdaLogger logger)
    {
        try
        {
            return await ReevaluateAsync(orderId, partyIds, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"{FailedToken} order={MoneyAlarmLog.LogId(orderId)} step=reevaluate [{ex.GetType().Name}]");
            return 0;
        }
    }

    public async Task<int> ReevaluateAsync(string orderId, IEnumerable<string?> partyIds, ILambdaLogger logger)
    {
        var now = Now;
        var eligible = new Dictionary<string, ConductStamp>(StringComparer.Ordinal);
        var first = true;
        foreach (var userId in partyIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal))
        {
            var stamp = await _stamps.GetStampAsync(userId, orderId);
            if (first && stamp == null)
            {
                return 0;
            }

            first = false;
            if (ConductScoring.CanReevaluate(stamp, now))
            {
                eligible[userId] = stamp!;
            }
        }

        if (eligible.Count == 0 || _orders == null)
        {
            return 0;
        }

        var row = await _orders.GetByIdAsync(orderId, consistentRead: true);
        if (row == null || ConductOrder.FromRow(row) is not { } current)
        {
            return 0;
        }

        // The order settled when it was first stamped; later edits of the row do not move that.
        var order = current with { SettledAt = eligible.Values.First().OccurredAt };
        var inputs = await ReadInputsAsync(order);
        var written = 0;
        foreach (var (party, userId) in ConductScoring.Parties(order))
        {
            if (!eligible.TryGetValue(userId, out var existing) || existing.Party != party)
            {
                continue;
            }

            var stamp = await BuildAsync(order, inputs, party, userId, existing.RecordedAt) with
            {
                ReevaluatedAt = now,
                Version = existing.Version + 1
            };
            var outcome = await _stamps.ReplaceOnceAsync(stamp);
            Log(logger, outcome, ReevaluatedToken, stamp);
            written += outcome == ConductWriteOutcome.Written ? 1 : 0;
        }

        return written;
    }

    // ===== inputs =====

    private sealed record ConductInputs(
        IReadOnlyDictionary<string, object>? Delivery, ConductPings Pings, IReadOnlySet<string>? RatedIds, bool? FraudClean);

    private async Task<ConductInputs> ReadInputsAsync(ConductOrder order)
    {
        var delivery = order.DeliveryId != null ? await _deliveries.GetByIdAsync(order.DeliveryId, consistentRead: true) : null;
        if (delivery != null && (delivery.TryGetValue("orderId", out var owner) ? owner?.ToString() : null) != order.OrderId)
        {
            delivery = null; // another order's row: nothing of it is this order's evidence
        }

        return new ConductInputs(delivery, await ReadPingsAsync(order, delivery), await ReadRatedAsync(order), await ReadFraudCleanAsync(order));
    }

    /// <summary>The trucker's pings summary, read only when the trip sharing step can be offered (an Android pickup).</summary>
    private async Task<ConductPings> ReadPingsAsync(ConductOrder order, IReadOnlyDictionary<string, object>? delivery)
    {
        if (_pings == null || order.TruckerId is not { } truckerId || order.TripId is not { } tripId || delivery == null
            || !(delivery.TryGetValue(HandoffEvidence.PlatformField(HandoffEvidence.PickupLeg), out var platform)
                 && platform?.ToString() == "android")
            || ConductScoring.PickupTime(delivery) is not { } from
            || ConductScoring.DropoffTime(delivery) is not { } to
            || to <= from)
        {
            return ConductPings.NotRead;
        }

        var (fromKey, toKey) = TruckerGpsEvidence.WindowKeys(from, to);
        var window = await _pings.ListBetweenAsync(tripId, fromKey, toKey, TruckerGpsEvidence.MaxWindowPages);
        return ConductScoring.SummarizePings(window.Rows, window.Complete, truckerId, from, to);
    }

    /// <summary>The order's farmer and trucker the buyer rated (rating ids <c>{orderId}:{rateeId}</c>); null without the table.</summary>
    private async Task<IReadOnlySet<string>?> ReadRatedAsync(ConductOrder order)
    {
        if (_ratings == null)
        {
            return null;
        }

        var rated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rateeId in new[] { order.FarmerId, order.TruckerId })
        {
            if (!string.IsNullOrEmpty(rateeId) && await _ratings.GetByIdAsync($"{order.OrderId}:{rateeId}", consistentRead: true) != null)
            {
                rated.Add(rateeId);
            }
        }

        return rated;
    }

    /// <summary>No <c>SELF_TRADE</c> or <c>SHARED_DEVICE_TRADE</c> signal on the order for any of its parties; null without the table.</summary>
    private async Task<bool?> ReadFraudCleanAsync(ConductOrder order)
    {
        if (_fraud == null)
        {
            return null;
        }

        foreach (var (_, userId) in ConductScoring.Parties(order))
        {
            foreach (var signal in ConductScoring.ExcludingSignals)
            {
                if (await _fraud.HasSignalAsync(userId, signal, order.OrderId))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<ConductStamp> BuildAsync(
        ConductOrder order, ConductInputs inputs, string party, string userId, DateTime recordedAt)
    {
        var result = ConductScoring.Evaluate(order, inputs.Delivery, inputs.Pings, inputs.RatedIds, party);
        var rows = await _stamps.QueryStampsAsync(userId, StampsReadCap);
        var reason = ConductScoring.NotCountedReason(order, inputs.Delivery, party, userId, inputs.FraudClean, rows);
        return ConductScoring.Stamp(order, userId, result, reason, recordedAt);
    }

    private static void Log(ILambdaLogger logger, ConductWriteOutcome outcome, string token, ConductStamp stamp)
    {
        var line = $"order={MoneyAlarmLog.LogId(stamp.OrderId)} party={stamp.Party} offered={stamp.Offered} earned={stamp.Earned} "
                   + $"counted={(stamp.Counted ? "true" : "false")}"
                   + (stamp.NotCountedReason != null ? $" notCounted={stamp.NotCountedReason}" : string.Empty);
        switch (outcome)
        {
            case ConductWriteOutcome.Written:
                logger.LogInformation($"{token} {line}");
                break;
            case ConductWriteOutcome.Skipped:
                logger.LogInformation($"{SkippedToken} reason=no_user_or_deleting {line}");
                break;
        }
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
