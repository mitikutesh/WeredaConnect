using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-32 (reputation-api.md section 9, reputation-architecture.md 6.5): the admin actions. Each
/// one is the same recompute as an event, with a row update or a system row in place of a
/// candidate: read the users row and the user's rows, plan the change, and commit it with the
/// aggregate in one transaction under the map's version. Two admins acting on one user are
/// serialized by that version: the loser re-reads, sees the winner's change and answers from it
/// (a replay, a refusal or a new plan), so nothing is ever applied twice.
///
/// After the commit the owner fields copied onto the user's listings are refreshed at once when a
/// public field changed (the transaction also left the day's heal marker for a copy that fails).
/// </summary>
public sealed partial class ReputationService
{
    public const string DecisionExcuse = "excuse";
    public const string DecisionUphold = "uphold";

    public const string ActionTag = "tag";
    public const string ActionUntag = "untag";
    public const string ActionRestrict = "restrict";
    public const string ActionLift = "lift";

    /// <summary>The stored value of <c>appealDecision</c> after an excuse and after an uphold.</summary>
    public const string DecisionExcused = "excused";

    public const string DecisionUpheld = "upheld";

    public const string AdminAppliedToken = "REPUTATION_ADMIN_APPLIED";

    // ===== 9.1: the admin read =====

    /// <summary>
    /// The full view of one user at read time: the users row, every row of the partition (one
    /// Query, strongly consistent) and the aggregate computed from them, so an admin never sees an
    /// expired point or an ended restriction. Null when the user has no row. No write.
    /// </summary>
    public async Task<ReputationAdminRead?> ReadAdminAsync(string userId)
    {
        var user = await _repo.GetUserStateAsync(userId);
        if (user == null)
        {
            return null;
        }

        var now = Now;
        var (rows, _) = await _repo.QueryEventsAsync(userId, _settings.EventsReadCap);
        return new ReputationAdminRead(ToRead(user, rows, now), rows);
    }

    private ReputationRead ToRead(ReputationUserState user, IReadOnlyList<ReputationEvent> rows, DateTime now)
    {
        var ratings = ReputationGate.StoredRatings(user.RatingSum, user.RatingCount, user.RatingsAggregatedAt);
        var nextExpiry = rows
            .Where(e => ReputationCodes.Penalties.Contains(e.Code) && e.Status == ReputationStatuses.Active
                        && e.Points - e.EarnedBack > 0 && e.PointsExpireAt is { } expires && expires > now)
            .Select(e => e.PointsExpireAt)
            .Min();
        return new ReputationRead(user, Aggregate(rows, user, now), ratings, nextExpiry, now);
    }

    // ===== 9.3: excuse or uphold one event =====

    /// <summary>
    /// Excuse or uphold one event of <paramref name="userId"/>.
    ///
    /// <c>excuse</c>: any penalty event whose points still count (stored <c>active</c>, not expired),
    /// appealed or not. The row becomes <c>excused</c>, its appeal (if any) closes, a points
    /// restriction lifts when the points fall below <c>restrictAt</c>, and the restriction of a
    /// <c>DELIVERY_OVERDUE</c> review lifts.
    ///
    /// <c>uphold</c>: only an event with an open appeal or review. The appeal closes as upheld; the
    /// restriction of a <c>DELIVERY_OVERDUE</c> review gets its end (<paramref name="until"/>, or now
    /// plus <c>restrictionDays</c>).
    ///
    /// The same decision again is <see cref="ReputationAdminOutcome.AlreadyApplied"/> and writes
    /// nothing; the other decision on a decided event is <see cref="ReputationAdminOutcome.EventDecided"/>.
    /// An event that was upheld with no party text can be appealed once by the party (it then has
    /// an open appeal again and takes a new decision); an event that has the party's text never
    /// reopens.
    /// </summary>
    public async Task<ReputationAdminResult> DecideAsync(
        string userId, string eventId, string decision, string note, DateTime? until, string adminId, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(userId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                return ReputationAdminResult.Refused(ReputationAdminOutcome.UserNotFound);
            }

            var rows = await ReadEventsAsync(user, logger);
            var now = Now;
            var row = rows.FirstOrDefault(r => r.EventId == eventId);
            if (row == null || !ReputationCodes.OrderEvents.Contains(row.Code))
            {
                // A system row is not an event an admin decides.
                return ReputationAdminResult.Refused(ReputationAdminOutcome.EventNotFound);
            }

            var before = ToRead(user, rows, now);
            var open = row.AppealStatus is "pending" or "review";
            var updates = new List<ReputationRowUpdate>();
            var nowIso = ReputationPolicy.Iso(now);

            if (decision == DecisionExcuse)
            {
                if (row.Status == ReputationStatuses.Excused)
                {
                    return ReputationAdminResult.Replay(before, rows, row);
                }

                if (!open && row.AppealDecision == DecisionUpheld)
                {
                    return ReputationAdminResult.Refused(ReputationAdminOutcome.EventDecided);
                }

                if (!ReputationCodes.Penalties.Contains(row.Code)
                    || ReputationReadModels.StatusAt(row, now) != ReputationStatuses.Active)
                {
                    return ReputationAdminResult.Refused(ReputationAdminOutcome.AppealClosed);
                }

                updates.Add(new ReputationRowUpdate
                {
                    EventId = row.EventId,
                    FromStatus = ReputationStatuses.Active,
                    SeenVersion = row.Version,
                    ToStatus = ReputationStatuses.Excused,
                    AppealDecision = DecisionExcused,
                    DecidedAt = nowIso,
                    DecidedBy = adminId,
                    DecisionNote = note,
                    CloseAppeal = true,
                    LeaveDueIndex = true
                });
            }
            else
            {
                if (!open)
                {
                    return row.Status == ReputationStatuses.Excused
                        ? ReputationAdminResult.Refused(ReputationAdminOutcome.EventDecided)
                        : row.AppealDecision == DecisionUpheld
                            ? ReputationAdminResult.Replay(before, rows, row)
                            : ReputationAdminResult.Refused(ReputationAdminOutcome.AppealClosed);
                }

                updates.Add(new ReputationRowUpdate
                {
                    EventId = row.EventId,
                    FromStatus = row.Status,
                    SeenVersion = row.Version,
                    RequiredAppealStatus = row.AppealStatus,
                    CheckAppealTextAt = true,
                    SeenAppealTextAt = row.AppealTextAt,
                    AppealDecision = DecisionUpheld,
                    DecidedAt = nowIso,
                    DecidedBy = adminId,
                    DecisionNote = note,
                    CloseAppeal = true
                });
            }

            var rowsAfter = rows.Select(r => r.EventId == row.EventId ? updates[0].Apply(r) : r).ToList();
            var inForce = InForceRestrictions(rowsAfter, now);

            if (decision == DecisionExcuse)
            {
                var pointsAfter = ReputationPolicy.ActivePoints(rowsAfter, now);
                foreach (var restriction in inForce)
                {
                    var lifts = (restriction.Cause == ReputationCauses.Points && pointsAfter < _settings.RestrictAt)
                                || (restriction.Cause == ReputationCauses.DeliveryOverdue
                                    && row.Code == ReputationCodes.DeliveryOverdue && restriction.Since == row.RecordedAt);
                    if (lifts)
                    {
                        updates.Add(Lift(restriction, nowIso, adminId));
                    }
                }
            }
            else if (row.Code == ReputationCodes.DeliveryOverdue)
            {
                // The review's restriction has no end until an admin decides (5.4).
                var reviewed = inForce.FirstOrDefault(r =>
                    r.Cause == ReputationCauses.DeliveryOverdue && r.Until == null && r.Since == row.RecordedAt);
                if (reviewed != null)
                {
                    var end = until ?? now.AddDays(_settings.RestrictionDays);
                    if (end <= now || end > now.AddDays(_settings.AdminRestrictMaxDays))
                    {
                        return ReputationAdminResult.Refused(ReputationAdminOutcome.InvalidUntil);
                    }

                    updates.Add(new ReputationRowUpdate
                    {
                        EventId = reviewed.EventId,
                        FromStatus = ReputationStatuses.Active,
                        RequireNoUntil = true,
                        SetUntil = end
                    });
                }
            }

            rowsAfter = ApplyUpdates(rows, updates);
            var result = await CommitAdminAsync(user, before, rowsAfter, updates, Array.Empty<ReputationEvent>(), now, decision, logger);
            if (result != null)
            {
                return result with { Event = rowsAfter.First(r => r.EventId == row.EventId) };
            }

            await _backoff(attempt);
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(userId)} kept changing; the decision gave up.");
    }

    // ===== 9.4: tag, untag, restrict, lift =====

    /// <summary>
    /// Tag or untag "Low reputation", restrict, or lift the restriction of a farmer or trucker.
    ///
    /// <c>tag</c> and <c>untag</c> write an <c>OVERRIDE#</c> row in force until
    /// <paramref name="until"/> (at most <c>tagMaxDays</c> ahead) and mark every earlier override
    /// <c>replaced</c>. <c>restrict</c> writes a <c>RESTRICTION#</c> row with cause <c>admin</c> (at
    /// most <c>adminRestrictMaxDays</c> ahead) and is refused while any restriction is in force
    /// (lift it first). <c>lift</c> ends every restriction in force, whatever its cause.
    ///
    /// The same tag, untag or restrict again (same end, same reason) is
    /// <see cref="ReputationAdminOutcome.AlreadyApplied"/> and writes nothing.
    /// </summary>
    public async Task<ReputationAdminResult> ApplyAdminActionAsync(
        string userId, string action, string reason, DateTime? until, string adminId, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(userId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                return ReputationAdminResult.Refused(ReputationAdminOutcome.UserNotFound);
            }

            if (user.Role is not ("farmer" or "trucker"))
            {
                return ReputationAdminResult.Refused(ReputationAdminOutcome.NotParty);
            }

            var rows = await ReadEventsAsync(user, logger);
            var now = Now;
            var before = ToRead(user, rows, now);
            var nowIso = ReputationPolicy.Iso(now);
            var updates = new List<ReputationRowUpdate>();
            var newRows = new List<ReputationEvent>();

            // Stored at millisecond precision, so compare at that precision.
            DateTime? end = until is { } u && ReputationPolicy.TryParseUtc(ReputationPolicy.Iso(u), out var rounded) ? rounded : null;

            switch (action)
            {
                case ActionTag:
                case ActionUntag:
                    {
                        if (end is not { } tagEnd || tagEnd <= now || tagEnd > now.AddDays(_settings.TagMaxDays))
                        {
                            return ReputationAdminResult.Refused(ReputationAdminOutcome.InvalidUntil);
                        }

                        var tag = action == ActionTag ? ReputationCauses.TagLow : ReputationCauses.TagClear;
                        var current = ReputationPolicy.ActiveOverride(rows, now);
                        if (current != null && current.Tag == tag && current.Until == tagEnd && current.Reason == reason)
                        {
                            return ReputationAdminResult.Replay(before, rows, null);
                        }

                        // The latest tag or untag replaces every earlier one.
                        foreach (var old in rows.Where(r =>
                                     r.Code is ReputationCodes.AdminTag or ReputationCodes.AdminUntag && r.Status == ReputationStatuses.Active))
                        {
                            updates.Add(new ReputationRowUpdate
                            {
                                EventId = old.EventId,
                                FromStatus = ReputationStatuses.Active,
                                ToStatus = ReputationStatuses.Replaced,
                                LeaveDueIndex = true
                            });
                        }

                        newRows.Add(new ReputationEvent
                        {
                            UserId = userId,
                            EventId = ReputationSlots.Override + nowIso,
                            Code = action == ActionTag ? ReputationCodes.AdminTag : ReputationCodes.AdminUntag,
                            Tag = tag,
                            Until = tagEnd,
                            Reason = reason,
                            By = adminId,
                            Status = ReputationStatuses.Active,
                            OccurredAt = now,
                            RecordedAt = now,
                            DueDay = ReputationPolicy.Day(tagEnd),
                            DueAt = ReputationPolicy.Iso(tagEnd),
                            PurgeAt = Epoch(now + ReputationPolicy.PenaltyRetention)
                        });
                        break;
                    }

                case ActionRestrict:
                    {
                        if (end is not { } restrictEnd || restrictEnd <= now || restrictEnd > now.AddDays(_settings.AdminRestrictMaxDays))
                        {
                            return ReputationAdminResult.Refused(ReputationAdminOutcome.InvalidUntil);
                        }

                        var inForce = InForceRestrictions(rows, now);
                        if (inForce.Count > 0)
                        {
                            return inForce.Any(r => r.Cause == ReputationCauses.Admin && r.Until == restrictEnd && r.Reason == reason)
                                ? ReputationAdminResult.Replay(before, rows, null)
                                : ReputationAdminResult.Refused(ReputationAdminOutcome.AlreadyRestricted);
                        }

                        newRows.Add(RestrictionRow(userId, ReputationCauses.Admin, now, restrictEnd) with { Reason = reason, By = adminId });
                        break;
                    }

                case ActionLift:
                    {
                        var inForce = InForceRestrictions(rows, now);
                        if (inForce.Count == 0)
                        {
                            return ReputationAdminResult.Refused(ReputationAdminOutcome.NotRestricted);
                        }

                        updates.AddRange(inForce.Select(r => Lift(r, nowIso, adminId)));
                        break;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown reputation action.");
            }

            var rowsAfter = ApplyUpdates(rows, updates);
            rowsAfter.AddRange(newRows);
            var result = await CommitAdminAsync(user, before, rowsAfter, updates, newRows, now, action, logger);
            if (result != null)
            {
                return result;
            }

            await _backoff(attempt);
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(userId)} kept changing; the admin action gave up.");
    }

    // ===== FUP-163: the copies follow a suspend or unsuspend at once =====

    /// <summary>
    /// After an admin suspend or unsuspend: recompute the aggregate (a suspended account stores and
    /// copies as <c>restricted</c>), write the map when the tier changed and rewrite the owner fields
    /// on the user's active listings in the same request. Returns how many rows were rewritten. The
    /// caller left the day's heal marker first, so the daily job retries whatever fails here.
    /// </summary>
    public async Task<int> RefreshAfterSuspensionAsync(string userId, ILambdaLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var user = await _repo.GetUserStateAsync(userId);
            if (user == null || !string.IsNullOrEmpty(user.DeletionState))
            {
                return 0;
            }

            var rows = await ReadEventsAsync(user, logger);
            var now = Now;
            var after = Aggregate(rows, user, now);
            var tierRow = TierRow(userId, user, after, now, "suspension");
            if (user.MapVersion == null || tierRow != null || PublicFieldsChanged(user, after))
            {
                var outcome = await _repo.CommitAsync(new ReputationWrite
                {
                    UserId = userId,
                    ExpectedMapVersion = user.MapVersion,
                    Map = CarryOver(user, after, now),
                    NewSystemRows = tierRow != null ? new[] { tierRow } : Array.Empty<ReputationEvent>()
                });
                if (outcome != ReputationCommitOutcome.Committed)
                {
                    await _backoff(attempt);
                    continue;
                }
            }

            // Throws on a failed copy: the caller logs it and the heal marker keeps the retry.
            return _copies == null ? 0 : await _copies.RefreshOwnerRowsAsync(userId, user.Role, OwnerFields(user, after, now));
        }

        throw new ReputationConflictException(
            $"The reputation of user {MoneyAlarmLog.LogId(userId)} kept changing; the suspension refresh gave up.");
    }

    // ===== shared steps =====

    /// <summary>
    /// Commit one admin change with the recomputed aggregate, a <c>TIER#</c> row when the tier
    /// changed and the heal marker when a copied field changed; then refresh the copies. Null on a
    /// conflict (the caller re-reads and plans again).
    /// </summary>
    private async Task<ReputationAdminResult?> CommitAdminAsync(
        ReputationUserState user,
        ReputationRead before,
        List<ReputationEvent> rowsAfter,
        List<ReputationRowUpdate> updates,
        IReadOnlyList<ReputationEvent> newRows,
        DateTime now,
        string step,
        ILambdaLogger logger)
    {
        var after = Aggregate(rowsAfter, user, now);
        var systemRows = new List<ReputationEvent>(newRows);
        if (TierRow(user.UserId, user, after, now, "admin") is { } tierRow)
        {
            systemRows.Add(tierRow);
        }

        var publicChanged = PublicFieldsChanged(user, after);
        var outcome = await _repo.CommitAsync(new ReputationWrite
        {
            UserId = user.UserId,
            ExpectedMapVersion = user.MapVersion,
            Map = CarryOver(user, after, now),
            NewSystemRows = systemRows,
            RowUpdates = updates,
            HealAt = publicChanged ? now : null
        });
        if (outcome != ReputationCommitOutcome.Committed)
        {
            return null;
        }

        logger.LogInformation(
            $"{AdminAppliedToken} step={step} user={MoneyAlarmLog.LogId(user.UserId)} tier={after.Tier} points={after.ActivePoints}");
        if (publicChanged)
        {
            await TryCopyAsync(user, after, now, "admin_" + step, logger);
        }

        return new ReputationAdminResult(
            ReputationAdminOutcome.Applied, before, ToRead(user, rowsAfter, now), rowsAfter, null);
    }

    /// <summary>Every restriction row in force at <paramref name="now"/> (status active, started, not ended), any cause.</summary>
    internal static List<ReputationEvent> InForceRestrictions(IReadOnlyList<ReputationEvent> rows, DateTime now)
        => rows
            .Where(e => e.Code == ReputationCodes.Restriction && e.Status == ReputationStatuses.Active
                        && e.Since is { } since && since <= now && (e.Until == null || e.Until > now))
            .ToList();

    private static ReputationRowUpdate Lift(ReputationEvent restriction, string nowIso, string adminId) => new()
    {
        EventId = restriction.EventId,
        FromStatus = ReputationStatuses.Active,
        ToStatus = ReputationStatuses.Lifted,
        LiftedAt = nowIso,
        LiftedBy = adminId,
        LeaveDueIndex = true
    };

    private static List<ReputationEvent> ApplyUpdates(IReadOnlyList<ReputationEvent> rows, List<ReputationRowUpdate> updates)
    {
        var byId = updates.ToDictionary(u => u.EventId, StringComparer.Ordinal);
        return rows.Select(r => byId.TryGetValue(r.EventId, out var update) ? update.Apply(r) : r).ToList();
    }
}

/// <summary>ESC-32: a user's reputation as an admin reads it: the aggregate at read time and every row.</summary>
public sealed record ReputationAdminRead(ReputationRead Read, IReadOnlyList<ReputationEvent> Rows);

/// <summary>How an admin action ended.</summary>
public enum ReputationAdminOutcome
{
    /// <summary>Written.</summary>
    Applied,

    /// <summary>The same change is already stored: nothing was written.</summary>
    AlreadyApplied,

    /// <summary>No users row, or a deleting or deleted account.</summary>
    UserNotFound,

    /// <summary>The target is not a farmer or trucker.</summary>
    NotParty,

    /// <summary>No such event under the user (or a system row).</summary>
    EventNotFound,

    /// <summary>The event was already decided the other way.</summary>
    EventDecided,

    /// <summary>Nothing to decide: no open appeal to uphold, or the event no longer counts.</summary>
    AppealClosed,

    /// <summary><c>lift</c> with no restriction in force.</summary>
    NotRestricted,

    /// <summary><c>restrict</c> while another restriction is in force.</summary>
    AlreadyRestricted,

    /// <summary><c>until</c> is missing, in the past or beyond the policy's limit.</summary>
    InvalidUntil
}

/// <summary>
/// What an admin action did: the aggregate before and after (the audit row's from and to), the
/// rows after it, and for a decision the event as it reads now.
/// </summary>
public sealed record ReputationAdminResult(
    ReputationAdminOutcome Outcome,
    ReputationRead? Before,
    ReputationRead? After,
    IReadOnlyList<ReputationEvent>? Rows,
    ReputationEvent? Event)
{
    public bool Succeeded => Outcome is ReputationAdminOutcome.Applied or ReputationAdminOutcome.AlreadyApplied;

    public static ReputationAdminResult Refused(ReputationAdminOutcome outcome) => new(outcome, null, null, null, null);

    public static ReputationAdminResult Replay(ReputationRead current, IReadOnlyList<ReputationEvent> rows, ReputationEvent? row)
        => new(ReputationAdminOutcome.AlreadyApplied, current, current, rows, row);
}
