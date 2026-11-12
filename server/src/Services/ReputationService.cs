using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-28: records reputation events and keeps the <c>reputation</c> aggregate on the users row
/// (reputation-architecture.md section 6). Every write recomputes the aggregate from all of the
/// user's rows (read strongly consistent) and commits it in the same transaction as the rows it
/// changes, conditioned on the map's own version, so the event rows and the aggregate never drift
/// and two writers for one user are serialized: the loser re-reads and sees the winner.
///
/// ESC-29: a write that changes a field copied onto the user's crop or trip rows (the tier, the
/// effective rating, the reliability) refreshes those copies after the commit (6.3), best effort,
/// and upserts the day's <c>HEAL#</c> marker, so the daily job retries a copy that failed.
/// </summary>
public sealed partial class ReputationService
{
    /// <summary>Attempts of one write before it throws (and the stream parks the record).</summary>
    public const int MaxAttempts = 5;

    /// <summary>Re-reads of the events when the Query lags the users row (6.1, read-lag check).</summary>
    public const int MaxLagReads = 3;

    /// <summary>The lag check only looks for a last event this recent (older rows may have been purged by TTL).</summary>
    public static readonly TimeSpan LagCheckWindow = TimeSpan.FromDays(30);

    public const string SkippedToken = "REPUTATION_SKIPPED";
    public const string TruncatedToken = "REPUTATION_EVENTS_TRUNCATED";
    public const string RecordedToken = "REPUTATION_RECORDED";

    /// <summary>A copy onto crop or trip rows failed (the monitoring stack counts it; the daily job retries).</summary>
    public const string CopyFailedToken = "REPUTATION_COPY_FAILED";

    /// <summary>Most items in one transaction (DynamoDB allows 100; one stays free).</summary>
    public const int MaxTransactionItems = 99;

    private readonly IReputationEventRepository _repo;
    private readonly IFraudDetectionService? _fraud;
    private readonly ReputationPolicySettings _settings;
    private readonly TimeProvider _clock;
    private readonly Func<int, Task> _backoff;
    private readonly IReputationCopyWriter? _copies;

    public ReputationService(
        IReputationEventRepository repo,
        ReputationPolicySettings settings,
        IFraudDetectionService? fraud = null,
        TimeProvider? clock = null,
        Func<int, Task>? backoff = null,
        IReputationCopyWriter? copies = null)
    {
        _copies = copies;
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fraud = fraud;
        _clock = clock ?? TimeProvider.System;
        _backoff = backoff ?? DefaultBackoff;
    }

    public ReputationPolicySettings Settings => _settings;

    // ===== record an event (6.1, 6.2) =====

    /// <summary>
    /// Record one candidate: read the users row and the user's rows, score the candidate, and commit
    /// the event, the aggregate and any restriction, earn-back, tier row and heal marker in one
    /// transaction under the map's version. A slot that already exists is a replay and writes
    /// nothing. A version conflict re-reads and tries again, up to <see cref="MaxAttempts"/>, then
    /// throws so the caller parks the record.
    /// </summary>
    public async Task<ReputationRecordResult> RecordAsync(ReputationCandidate candidate, bool timeoutPenaltiesOn, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(candidate.UserId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                logger.LogInformation(
                    $"{SkippedToken} reason={(user == null ? "no_user" : "deleting")} user={MoneyAlarmLog.LogId(candidate.UserId)} slot={SlotKind(candidate.EventId)}");
                return ReputationRecordResult.Skipped;
            }

            var rows = await ReadEventsAsync(user, logger);
            if (rows.Any(r => r.EventId == candidate.EventId))
            {
                return ReputationRecordResult.Replay;
            }

            var now = Now;
            var plan = await PlanAsync(candidate, user, rows, timeoutPenaltiesOn, now);
            var outcome = await _repo.CommitAsync(plan.Write);
            switch (outcome)
            {
                case ReputationCommitOutcome.Committed:
                    logger.LogInformation(
                        $"{RecordedToken} user={MoneyAlarmLog.LogId(candidate.UserId)} code={plan.Event.Code} points={plan.Event.Points}"
                        + (plan.Event.ZeroReason != null ? $" zeroReason={plan.Event.ZeroReason}" : string.Empty)
                        + $" tier={plan.Write.Map.Aggregate.Tier}");
                    if (plan.Write.HealAt != null)
                    {
                        await TryCopyAsync(user, plan.Write.Map.Aggregate, now, "record", logger);
                    }

                    return ReputationRecordResult.Recorded;
                case ReputationCommitOutcome.EventExists:
                    return ReputationRecordResult.Replay;
                default:
                    await _backoff(attempt);
                    break;
            }
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(candidate.UserId)} kept changing; {MaxAttempts} attempts.");
    }

    /// <summary>The transaction one candidate leads to, given what was read.</summary>
    internal async Task<RecordPlan> PlanAsync(
        ReputationCandidate candidate, ReputationUserState user, IReadOnlyList<ReputationEvent> rows, bool timeoutPenaltiesOn, DateTime now)
    {
        var s = _settings;
        var score = ReputationPolicy.Score(candidate, rows, user.Suspended, s, timeoutPenaltiesOn);
        var newEvent = EventRow(candidate, score, now);
        var rowsAfter = new List<ReputationEvent>(rows) { newEvent };

        ReputationEarnBackUpdate? earnBack = null;
        if (score.Code == ReputationCodes.OrderCompleted
            && ReputationPolicy.EarnBackTarget(candidate, rows, now, s) is { } target
            && await IsCleanOrderAsync(candidate))
        {
            earnBack = new ReputationEarnBackUpdate(target.EventId, target.Version);
            newEvent = newEvent with { EarnBackEventId = target.EventId };
            rowsAfter[^1] = newEvent;
            var index = rowsAfter.FindIndex(r => r.EventId == target.EventId);
            rowsAfter[index] = target with { EarnedBack = target.EarnedBack + 1, Version = target.Version + 1 };
        }

        var after = Aggregate(rowsAfter, user, now);
        var systemRows = new List<ReputationEvent>();

        // 5.4: a DELIVERY_OVERDUE opens a review with a restriction that has no end; any other
        // penalty that crosses restrictAt with no restriction active starts a 14 day one.
        ReputationEvent? restriction = null;
        if (score.Code == ReputationCodes.DeliveryOverdue)
        {
            restriction = RestrictionRow(candidate.UserId, ReputationCauses.DeliveryOverdue, now, until: null);
            newEvent = newEvent with { AppealStatus = "review", AppealedAt = ReputationPolicy.Iso(now) };
            rowsAfter[rowsAfter.FindIndex(r => r.EventId == newEvent.EventId)] = newEvent;
        }
        else if (ReputationPolicy.StartsPointsRestriction(score.Code, score.Points, after.ActivePoints, rows, now, s))
        {
            restriction = RestrictionRow(candidate.UserId, ReputationCauses.Points, now, now.AddDays(s.RestrictionDays));
        }

        if (restriction != null)
        {
            systemRows.Add(restriction);
            rowsAfter.Add(restriction);
            after = Aggregate(rowsAfter, user, now);
        }

        if (ReputationCodes.Penalties.Contains(score.Code))
        {
            newEvent = newEvent with { ActivePointsAfter = after.ActivePoints };
        }

        if (TierRow(candidate.UserId, user, after, now, candidate.EventId) is { } tierRow)
        {
            systemRows.Add(tierRow);
        }

        var write = new ReputationWrite
        {
            UserId = candidate.UserId,
            ExpectedMapVersion = user.MapVersion,
            Map = new ReputationMapWrite
            {
                Aggregate = after,
                Version = (user.MapVersion ?? 0) + 1,
                UpdatedAt = now,
                LastEventId = newEvent.EventId,
                LastEventRecordedAt = now
            },
            NewEvent = newEvent,
            NewSystemRows = systemRows,
            EarnBack = earnBack,
            HealAt = PublicFieldsChanged(user, after) ? now : null
        };

        return new RecordPlan(newEvent, write);
    }

    // ===== refresh after a rating create (6.4) =====

    /// <summary>
    /// rating-api.md 1.2.0 "Stored aggregate" step 3, best effort: recompute the ratee's aggregate
    /// with the new rating sum and count and write the map when a public field changed (version
    /// checked, retried on a conflict). Returns false when nothing was written.
    /// </summary>
    public async Task<bool> RefreshAsync(string userId, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(userId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                return false;
            }

            var rows = await ReadEventsAsync(user, logger);
            var now = Now;
            var after = Aggregate(rows, user, now);
            var tierRow = TierRow(userId, user, after, now, "refresh");
            if (attempt == 1)
            {
                // The caller is the rating create: the rating count changed even when no stored
                // field did. The rating transaction left the HEAL# marker for a failure here.
                await TryCopyAsync(user, after, now, "refresh", logger);
            }

            if (user.MapVersion != null && tierRow == null && !PublicFieldsChanged(user, after))
            {
                return false;
            }

            var outcome = await _repo.CommitAsync(new ReputationWrite
            {
                UserId = userId,
                ExpectedMapVersion = user.MapVersion,
                Map = CarryOver(user, after, now),
                NewSystemRows = tierRow != null ? new[] { tierRow } : Array.Empty<ReputationEvent>()
            });
            if (outcome == ReputationCommitOutcome.Committed)
            {
                return true;
            }

            await _backoff(attempt);
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(userId)} kept changing; refresh gave up.");
    }

    // ===== reads (6.6, ESC-29) =====

    /// <summary>
    /// A user's reputation at read time, with no write: the stored aggregate under the live policy,
    /// or, once <c>now</c> has passed the map's <c>nextChangeAt</c> (an expiry or an end the daily
    /// job has not written yet), a recompute from the events (one Query). So a read never shows an
    /// expired point, an ended restriction or an ended override. <paramref name="withEvents"/> also
    /// reads the events while points are active (the party's own view needs the next expiry).
    /// Null when the user has no row.
    /// </summary>
    public async Task<ReputationRead?> ReadAsync(string userId, bool withEvents)
    {
        var user = await _repo.GetUserStateAsync(userId);
        if (user == null)
        {
            return null;
        }

        var now = Now;
        var ratings = ReputationGate.StoredRatings(user.RatingSum, user.RatingCount, user.RatingsAggregatedAt);
        var snapshot = user.Snapshot;
        var stale = snapshot?.NextChangeAt is { } next && next <= now;
        if (snapshot == null || !(stale || (withEvents && snapshot.ActivePoints > 0)))
        {
            return new ReputationRead(user, ReputationPolicy.FromSnapshot(snapshot, ratings, user.Suspended, now, _settings), ratings, null, now);
        }

        var (rows, _) = await _repo.QueryEventsAsync(userId, _settings.EventsReadCap);
        var aggregate = Aggregate(rows, user, now);
        var nextExpiry = rows
            .Where(e => ReputationCodes.Penalties.Contains(e.Code) && e.Status == ReputationStatuses.Active
                        && e.Points - e.EarnedBack > 0 && e.PointsExpireAt is { } expires && expires > now)
            .Select(e => e.PointsExpireAt)
            .Min();
        return new ReputationRead(user, aggregate, ratings, nextExpiry, now);
    }

    /// <summary>The user's rows for the party's event list (one Query, up to <c>eventsReadCap</c>).</summary>
    public async Task<IReadOnlyList<ReputationEvent>> ReadEventsAsync(string userId)
        => (await _repo.QueryEventsAsync(userId, _settings.EventsReadCap)).Rows;

    // ===== the daily job's per-user step (8.2) =====

    /// <summary>
    /// Settle a user's due rows at <paramref name="now"/>: expired penalties and completions become
    /// <c>expired</c>, restrictions and overrides past their end become <c>ended</c>, each leaving the
    /// <c>DueIndex</c>; <c>HEAL#</c> markers are deleted; the aggregate is recomputed and written with
    /// a <c>TIER#</c> row when the tier changed. Chunks of at most <see cref="MaxTransactionItems"/>
    /// items, each with the map under its version. <paramref name="force"/> writes the map even when
    /// nothing was due (the manual recompute after a policy change). Never starts a restriction.
    /// </summary>
    public async Task<ReputationSettleResult> SettleDueAsync(string userId, DateTime now, bool force, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(userId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                logger.LogInformation(
                    $"{SkippedToken} reason={(user == null ? "no_user" : "deleting")} user={MoneyAlarmLog.LogId(userId)} step=daily");
                return ReputationSettleResult.Skipped;
            }

            var rows = await ReadEventsAsync(user, logger);
            var (transitions, deletes, rowsAfter) = DueChanges(rows, now);
            var after = Aggregate(rowsAfter, user, now);
            var tierRow = TierRow(userId, user, after, now, "daily");

            if (!force && transitions.Count == 0 && deletes.Count == 0 && tierRow == null && user.MapVersion != null
                && !PublicFieldsChanged(user, after))
            {
                return new ReputationSettleResult(true, 0, 0, 0, Written: false);
            }

            // 6.3 through the heal marker: the copies are refreshed before the marker is deleted, so
            // a failed copy throws, the marker stays and the next run tries again. Idempotent (rows
            // that already hold the values are not written), so a version conflict below repeats it
            // for free.
            var copied = 0;
            if (_copies != null && (force || deletes.Count > 0 || tierRow != null || PublicFieldsChanged(user, after)))
            {
                copied = await _copies.RefreshOwnerRowsAsync(userId, user.Role, OwnerFields(user, after, now));
            }

            var changes = transitions.Select(t => new DueChange(t, null))
                .Concat(deletes.Select(d => new DueChange(null, d)))
                .ToList();
            var perChunk = MaxTransactionItems - 2; // the users map and room for the tier row
            var chunks = changes.Count == 0
                ? new List<List<DueChange>> { new() }
                : changes.Chunk(perChunk).Select(c => c.ToList()).ToList();

            var expected = user.MapVersion;
            var conflict = false;
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var write = new ReputationWrite
                {
                    UserId = userId,
                    ExpectedMapVersion = expected,
                    Map = CarryOver(user, after, now) with { Version = (expected ?? 0) + 1 },
                    NewSystemRows = i == 0 && tierRow != null ? new[] { tierRow } : Array.Empty<ReputationEvent>(),
                    Transitions = chunk.Where(c => c.Transition != null).Select(c => c.Transition!).ToList(),
                    Deletes = chunk.Where(c => c.Delete != null).Select(c => c.Delete!).ToList()
                };

                if (await _repo.CommitAsync(write) != ReputationCommitOutcome.Committed)
                {
                    conflict = true;
                    break;
                }

                expected = (expected ?? 0) + 1;
            }

            if (conflict)
            {
                await _backoff(attempt);
                continue;
            }

            return new ReputationSettleResult(
                true,
                Expired: transitions.Count(t => t.ToStatus == ReputationStatuses.Expired),
                Ended: transitions.Count(t => t.ToStatus == ReputationStatuses.Ended),
                Healed: deletes.Count,
                Written: true)
            {
                Copied = copied
            };
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(userId)} kept changing; the daily step gave up.");
    }

    /// <summary>The due rows' changes at <paramref name="now"/> and the rows as they will read after them.</summary>
    internal static (List<ReputationRowTransition> Transitions, List<string> Deletes, List<ReputationEvent> RowsAfter) DueChanges(
        IReadOnlyList<ReputationEvent> rows, DateTime now)
    {
        var transitions = new List<ReputationRowTransition>();
        var deletes = new List<string>();
        var after = new List<ReputationEvent>(rows.Count);

        foreach (var row in rows)
        {
            var isDue = !string.IsNullOrEmpty(row.DueDay)
                        && ReputationPolicy.TryParseUtc(row.DueAt, out var dueAt) && dueAt <= now;
            if (!isDue)
            {
                after.Add(row);
                continue;
            }

            if (row.Code == ReputationCodes.Heal || row.EventId.StartsWith(ReputationSlots.Heal, StringComparison.Ordinal))
            {
                deletes.Add(row.EventId);
                continue;
            }

            string? to = null;
            if (row.Status == ReputationStatuses.Active)
            {
                if (ReputationCodes.OrderEvents.Contains(row.Code))
                {
                    to = row.PointsExpireAt is { } expires && expires <= now ? ReputationStatuses.Expired : null;
                }
                else if (row.Code is ReputationCodes.Restriction or ReputationCodes.AdminTag or ReputationCodes.AdminUntag)
                {
                    to = row.Until is { } until && until <= now ? ReputationStatuses.Ended : null;
                }

                if (to == null)
                {
                    // Due but not yet changing (a moved end): leave it on the index for its time.
                    after.Add(row);
                    continue;
                }
            }

            transitions.Add(new ReputationRowTransition(row.EventId, row.Status, to));
            after.Add(to == null
                ? row with { DueDay = null, DueAt = null }
                : row with { Status = to, DueDay = null, DueAt = null, Version = row.Version + 1 });
        }

        return (transitions, deletes, after);
    }

    // ===== shared steps =====

    /// <summary>
    /// The user's rows, strongly consistent (6.1). Past <c>eventsReadCap</c> it logs
    /// <c>REPUTATION_EVENTS_TRUNCATED</c> and goes on. When the users map names a recent last event
    /// the Query does not return yet, it reads again (the two reads are separate, so a just
    /// committed transaction can show on the users row first); still missing, it throws so the
    /// caller retries later.
    /// </summary>
    internal async Task<IReadOnlyList<ReputationEvent>> ReadEventsAsync(ReputationUserState user, ILambdaLogger logger)
    {
        for (var read = 1; ; read++)
        {
            var (rows, truncated) = await _repo.QueryEventsAsync(user.UserId, _settings.EventsReadCap);
            if (truncated)
            {
                logger.LogWarning(
                    $"{TruncatedToken} user={MoneyAlarmLog.LogId(user.UserId)} cap={_settings.EventsReadCap}");
                return rows;
            }

            var mustSee = user.LastEventId != null && user.LastEventRecordedAt is { } recorded
                          && Now - recorded < LagCheckWindow;
            if (!mustSee || rows.Any(r => r.EventId == user.LastEventId))
            {
                return rows;
            }

            if (read >= MaxLagReads)
            {
                throw new ReputationConflictException(
                    $"The events of user {MoneyAlarmLog.LogId(user.UserId)} lag their aggregate.");
            }

            await _backoff(read);
        }
    }

    /// <summary>
    /// The aggregate of the rows read. The rating sum and count are used only once the row carries
    /// <c>ratingsAggregatedAt</c>: an unstamped row would give an effective rating over the ratings
    /// created since the deploy only (<see cref="ReputationGate.StoredRatings"/>).
    /// </summary>
    private ReputationAggregate Aggregate(IReadOnlyList<ReputationEvent> rows, ReputationUserState user, DateTime now)
    {
        var (sum, count) = ReputationGate.StoredRatings(user.RatingSum, user.RatingCount, user.RatingsAggregatedAt);
        return ReputationPolicy.Aggregate(rows, sum, count, user.Suspended, now, _settings);
    }

    /// <summary>5.8 rule 4: no <c>SELF_TRADE</c> or <c>SHARED_DEVICE_TRADE</c> signal on the order for the party or the buyer.</summary>
    private async Task<bool> IsCleanOrderAsync(ReputationCandidate completion)
    {
        if (_fraud == null || string.IsNullOrEmpty(completion.OrderId))
        {
            return false; // cannot check: no earn-back rather than one a self-trade could buy
        }

        foreach (var userId in new[] { completion.UserId, completion.BuyerId })
        {
            if (string.IsNullOrEmpty(userId))
            {
                continue;
            }

            foreach (var signal in new[] { FraudDetectionService.SelfTradeSignal, FraudDetectionService.SharedDeviceTradeSignal })
            {
                if (await _fraud.HasSignalAsync(userId, signal, completion.OrderId))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private ReputationEvent EventRow(ReputationCandidate c, ReputationScore score, DateTime now)
    {
        var expires = c.OccurredAt + _settings.PointsLife;
        var isCompletion = score.Code == ReputationCodes.OrderCompleted;
        return new ReputationEvent
        {
            UserId = c.UserId,
            EventId = c.EventId,
            Code = score.Code,
            Party = c.Party,
            BasePoints = score.BasePoints,
            Points = score.Points,
            ZeroReason = score.Points > 0 ? null : score.ZeroReason,
            Status = ReputationStatuses.Active,
            OrderId = c.OrderId,
            TripId = c.TripId,
            BuyerId = c.BuyerId,
            OrderTotal = isCompletion ? c.OrderTotal : null,
            PickupDate = c.PickupDate,
            OccurredAt = c.OccurredAt,
            RecordedAt = now,
            PointsExpireAt = expires,
            DueDay = ReputationPolicy.Day(expires),
            DueAt = ReputationPolicy.Iso(expires),
            SourceSeq = c.SourceSeq,
            PurgeAt = Epoch(isCompletion ? c.OccurredAt + ReputationPolicy.CompletionRetention : now + ReputationPolicy.PenaltyRetention)
        };
    }

    private static ReputationEvent RestrictionRow(string userId, string cause, DateTime since, DateTime? until) => new()
    {
        UserId = userId,
        EventId = ReputationSlots.Restriction + ReputationPolicy.Iso(since),
        Code = ReputationCodes.Restriction,
        Cause = cause,
        Since = since,
        Until = until,
        Status = ReputationStatuses.Active,
        OccurredAt = since,
        RecordedAt = since,
        DueDay = until is { } end ? ReputationPolicy.Day(end) : null,
        DueAt = until is { } at ? ReputationPolicy.Iso(at) : null,
        PurgeAt = Epoch(since + ReputationPolicy.PenaltyRetention)
    };

    /// <summary>
    /// A <c>TIER#</c> row when the tier about to be stored differs from the stored one (an absent map
    /// reads as <c>good</c>): it feeds the <c>tier_changed</c> notification.
    /// </summary>
    private static ReputationEvent? TierRow(string userId, ReputationUserState user, ReputationAggregate after, DateTime now, string cause)
    {
        var from = user.StoredTier ?? ReputationTiers.Good;
        if (from == after.Tier)
        {
            return null;
        }

        return new ReputationEvent
        {
            UserId = userId,
            EventId = $"{ReputationSlots.Tier}{ReputationPolicy.Iso(now)}#{cause}",
            Code = ReputationCodes.TierChanged,
            FromTier = from,
            ToTier = after.Tier,
            RestrictedUntil = after.Tier == ReputationTiers.Restricted ? after.Restriction?.Until : null,
            Status = ReputationStatuses.Active,
            OccurredAt = now,
            RecordedAt = now,
            PurgeAt = Epoch(now + ReputationPolicy.PenaltyRetention)
        };
    }

    /// <summary>
    /// Whether a field copied onto the user's rows differs from the stored map: the tier (the
    /// internal one, since browse hides <c>restricted</c> owners while buyers read it as <c>low</c>),
    /// the effective rating or the reliability.
    /// </summary>
    internal static bool PublicFieldsChanged(ReputationUserState user, ReputationAggregate after)
        => (user.StoredTier ?? ReputationTiers.Good) != after.Tier
           || user.StoredEffectiveRating != after.EffectiveRating
           || user.StoredReliabilityPct != after.ReliabilityPct;

    private static OwnerReputationFields OwnerFields(ReputationUserState user, ReputationAggregate aggregate, DateTime now)
        => ReputationGate.OwnerFields(
            aggregate, ReputationGate.StoredRatings(user.RatingSum, user.RatingCount, user.RatingsAggregatedAt).Count, now);

    /// <summary>The copy refresh after a commit (6.3), best effort: a failure is logged and left to the daily job.</summary>
    private async Task TryCopyAsync(ReputationUserState user, ReputationAggregate aggregate, DateTime now, string step, ILambdaLogger logger)
    {
        if (_copies == null)
        {
            return;
        }

        try
        {
            await _copies.RefreshOwnerRowsAsync(user.UserId, user.Role, OwnerFields(user, aggregate, now));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                $"{CopyFailedToken} step={step} user={MoneyAlarmLog.LogId(user.UserId)} [{ex.GetType().Name}]");
        }
    }

    /// <summary>The map to write with no new event: the last event fields carry over.</summary>
    private static ReputationMapWrite CarryOver(ReputationUserState user, ReputationAggregate after, DateTime now) => new()
    {
        Aggregate = after,
        Version = (user.MapVersion ?? 0) + 1,
        UpdatedAt = now,
        LastEventId = user.LastEventId,
        LastEventRecordedAt = user.LastEventRecordedAt
    };

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static long Epoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>The slot's kind for a log line (the order id stays out).</summary>
    private static string SlotKind(string eventId)
    {
        var hash = eventId.IndexOf('#');
        return hash > 0 ? eventId[..hash] : "unknown";
    }

    private static Task DefaultBackoff(int attempt)
        => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt));

    /// <summary>One candidate's planned write, for the tests.</summary>
    internal sealed record RecordPlan(ReputationEvent Event, ReputationWrite Write);

    /// <summary>One due row's change in the daily step: a transition or a delete.</summary>
    private sealed record DueChange(ReputationRowTransition? Transition, string? Delete);
}

/// <summary>ESC-29: a user's reputation as read (<see cref="ReputationService.ReadAsync"/>).</summary>
public sealed record ReputationRead(
    ReputationUserState User, ReputationAggregate Aggregate, (long Sum, long Count) Ratings, DateTime? NextPointExpiryAt, DateTime AsOf);

/// <summary>How <see cref="ReputationService.RecordAsync"/> ended.</summary>
public enum ReputationRecordResult
{
    Recorded,
    Replay,
    Skipped
}

/// <summary>What <see cref="ReputationService.SettleDueAsync"/> did for one user.</summary>
public sealed record ReputationSettleResult(bool Processed, int Expired, int Ended, int Healed, bool Written)
{
    /// <summary>ESC-29: crop or trip rows whose owner fields were rewritten.</summary>
    public int Copied { get; init; }

    public static ReputationSettleResult Skipped { get; } = new(false, 0, 0, 0, false);
}

/// <summary>A reputation write that could not settle: the stream parks the record, the job leaves the user due.</summary>
public sealed class ReputationConflictException : Exception
{
    public ReputationConflictException(string message) : base(message)
    {
    }
}
