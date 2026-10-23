using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>What a deletion request, resume or sweep run ended with.</summary>
public enum AccountDeletionResult
{
    /// <summary>200: deleted now, or a replay of a finished deletion.</summary>
    Completed,

    /// <summary>409 ACCOUNT_002: active rows block it (also a step 9 abort).</summary>
    Blocked,

    /// <summary>503 ACCOUNT_005: started, not finished; a retry resumes.</summary>
    Incomplete,

    /// <summary>404 AUTH_013: no users row.</summary>
    NotFound,

    /// <summary>403 ACCOUNT_003.</summary>
    Suspended,

    /// <summary>400 ACCOUNT_001.</summary>
    InvalidBody,

    /// <summary>403 AUTH_040.</summary>
    StepUpRequired,

    /// <summary>500 AUTH_007: failed before anything was written.</summary>
    Failed
}

public sealed record AccountDeletionOutcome
{
    public required AccountDeletionResult Result { get; init; }
    public string? DeletionState { get; init; }
    public string? DeletedAt { get; init; }
    public IReadOnlyList<AccountDeletionBlocker> Blockers { get; init; } = Array.Empty<AccountDeletionBlocker>();

    /// <summary>Incomplete only because the invocation ran out of time (not a failed step).</summary>
    public bool BudgetCut { get; init; }
}

/// <summary>
/// The time an invocation may still spend. <see cref="ThrowIfLow"/> runs before each step and row,
/// so a run that is about to be cut off stops between writes and answers 503 (resumable) instead.
/// </summary>
public sealed class DeletionBudget
{
    private readonly Func<bool> _isLow;

    public DeletionBudget(Func<bool> isLow)
    {
        _isLow = isLow ?? throw new ArgumentNullException(nameof(isLow));
    }

    public static DeletionBudget Unlimited { get; } = new(() => false);

    public bool IsLow => _isLow();

    public void ThrowIfLow()
    {
        if (_isLow())
        {
            throw new DeletionBudgetExceededException();
        }
    }
}

/// <summary>
/// The invocation stopped between two writes because its time ran out (<see cref="DeletionBudget"/>).
/// Not a failure of the deletion: the sweep neither counts it nor alarms on it.
/// </summary>
public sealed class DeletionBudgetExceededException : Exception
{
    public DeletionBudgetExceededException()
        : base("The invocation is about to run out of time.")
    {
    }
}

/// <summary>
/// ACC-02: account deletion (account-deletion.md 1.2.0): the order of checks, the blocker check,
/// deletion steps 5 to 13, the resume of a started deletion, the step 9 abort, the completion sweep
/// and the <c>reapply</c> action. Every step is idempotent; a run that fails after step 5 answers
/// 503 and the next request or sweep resumes it.
/// </summary>
public sealed class AccountDeletionService
{
    /// <summary>Started deletions the sweep picks up once their request is this old.</summary>
    internal static readonly TimeSpan SweepMinAge = TimeSpan.FromHours(1);

    /// <summary>
    /// How often the sweep runs (the EventBridge rule in api-stack.ts). A deletion is first due at
    /// most <see cref="SweepMinAge"/> plus one interval after its request (7 hours), then again
    /// every interval, so three runs try it within the contract's 24 hours.
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// A row at least this old was due in the previous sweep run too, so failing on it now is its
    /// second failure: <c>ACCOUNT_DELETION_STUCK</c>.
    /// </summary>
    internal static readonly TimeSpan StuckAfter = SweepMinAge + SweepInterval;

    public const int RetryAfterSeconds = 2;

    /// <summary>Tries of the step 8 cancel of one order (each re-reads it after a lost condition).</summary>
    internal const int MaxCancelAttempts = 3;

    private readonly IAccountDeletionRepository _repo;
    private readonly ICognitoAuthService _cognito;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly ITransactionAuditRepository _audit;
    private readonly Func<DateTime> _clock;

    public AccountDeletionService(
        IAccountDeletionRepository repo,
        ICognitoAuthService cognito,
        IAmazonSimpleNotificationService sns,
        ITransactionAuditRepository audit,
        Func<DateTime>? clock = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _cognito = cognito ?? throw new ArgumentNullException(nameof(cognito));
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    // ──────────────────────────────────────────────
    // DELETE /user/account
    // ──────────────────────────────────────────────

    /// <summary>
    /// The request, in the contract's order of checks: row, finished or started deletion (no new
    /// step-up), suspension, body, blockers, step-up, then the deletion.
    /// </summary>
    public async Task<AccountDeletionOutcome> RequestAsync(
        string userId, bool confirmed, bool stepUpFresh, AuditContext ctx, DeletionBudget budget, ILambdaContext? log)
    {
        UserDto user;
        try
        {
            var row = await _repo.GetAccountAsync(userId);
            if (row == null)
            {
                return Outcome(AccountDeletionResult.NotFound);
            }

            if (!string.IsNullOrEmpty(row.DeletionState))
            {
                return await ResumeAsync(row, ctx, budget, log);
            }

            if (row.Suspended)
            {
                return Outcome(AccountDeletionResult.Suspended);
            }

            if (!confirmed)
            {
                return Outcome(AccountDeletionResult.InvalidBody);
            }

            var scan = await ScanAsync(row, budget);
            if (scan.Blockers.Count > 0)
            {
                return Blocked(scan.Blockers);
            }

            if (!stepUpFresh)
            {
                return Outcome(AccountDeletionResult.StepUpRequired);
            }

            var requestedAt = await _repo.TryMarkInProgressAsync(userId, Iso(_clock()));
            if (requestedAt == null)
            {
                // Lost a race with an admin suspension or another request: answer from the row.
                var fresh = await _repo.GetAccountAsync(userId);
                if (fresh == null)
                {
                    return Outcome(AccountDeletionResult.NotFound);
                }

                if (!string.IsNullOrEmpty(fresh.DeletionState))
                {
                    return await ResumeAsync(fresh, ctx, budget, log);
                }

                return Outcome(fresh.Suspended ? AccountDeletionResult.Suspended : AccountDeletionResult.Failed);
            }

            user = row with { DeletionState = AccountDeletionPolicy.InProgress, DeletionRequestedAt = requestedAt };
        }
        catch (Exception ex)
        {
            // Nothing was written: the client may simply try again later.
            log?.Logger.LogError($"Account deletion failed before any write for {MoneyAlarmLog.LogId(userId)}: [{ex.GetType().Name}]");
            return Outcome(AccountDeletionResult.Failed);
        }

        return await ContinueAsync(user, ctx, budget, log, resumed: false);
    }

    /// <summary>Answer a row that already carries a deletion state: replay, or resume where it stopped.</summary>
    private async Task<AccountDeletionOutcome> ResumeAsync(
        UserDto user, AuditContext ctx, DeletionBudget budget, ILambdaContext? log)
    {
        return user.DeletionState switch
        {
            AccountDeletionPolicy.Completed => Done(user.DeletedAt ?? user.UpdatedAt),
            AccountDeletionPolicy.CognitoPending => await FinishAsync(user, ctx, log, counts: null),
            AccountDeletionPolicy.InProgress => await ContinueAsync(user, ctx, budget, log, resumed: true),
            // Unknown value: never act on it.
            _ => Incomplete(user.UserId, user.DeletionState!, null, log)
        };
    }

    // ──────────────────────────────────────────────
    // Steps 5b to 11
    // ──────────────────────────────────────────────

    private async Task<AccountDeletionOutcome> ContinueAsync(
        UserDto user, AuditContext ctx, DeletionBudget budget, ILambdaContext? log, bool resumed)
    {
        var counts = new DeletionCounts { Resumed = resumed };
        try
        {
            // 5b. Checked: an existing row (a resume) counts as done.
            await TransactionAuditWriter.EmitOnceCheckedAsync(
                _audit, TransactionId(user.UserId), "account", user.UserId, "account_deletion_started",
                null, AccountDeletionPolicy.InProgress, AccountActor(user, ctx),
                user.DeletionRequestedAt ?? throw new InvalidOperationException("in_progress without deletionRequestedAt"));

            // 6. Revokes the refresh tokens on every device.
            budget.ThrowIfLow();
            EnsureDone(await _cognito.AdminGlobalSignOutAsync(user.UserId), "AdminUserGlobalSignOut");

            // 7. Listings off the market, settled history kept as scrubbed tombstones.
            await WithdrawListingsAsync(user.UserId, counts, budget);

            // 8. Unpaid orders cancelled; one that moved on to a paid status is a blocker.
            var raced = await CancelUnpaidOrdersAsync(user, counts, budget, log);

            // 9. The blockers again: a payment may have landed since step 3.
            var scan = await ScanAsync(user, budget);
            var blockers = Distinct(raced.Concat(scan.Blockers));
            if (blockers.Count > 0)
            {
                return await AbortAsync(user, blockers, ctx, log);
            }

            // 10. Per-table policy.
            await ApplyPolicyAsync(user.UserId, scan, counts, budget, includeListings: false);

            // 11. Tombstone: from here no personal data is left in the users row.
            budget.ThrowIfLow();
            var deletedAt = await _repo.TombstoneAsync(
                user.UserId, Iso(_clock()), AccountDeletionPolicy.CognitoPending, requireInProgress: true)
                ?? throw new InvalidOperationException("The users row left in_progress before the tombstone.");
            user = user with { DeletionState = AccountDeletionPolicy.CognitoPending, DeletedAt = deletedAt };
        }
        catch (Exception ex)
        {
            return Incomplete(user.UserId, AccountDeletionPolicy.InProgress, ex, log);
        }

        return await FinishAsync(user, ctx, log, counts);
    }

    /// <summary>Steps 12 and 13: delete the Cognito user, write <c>account_deleted</c>, mark completed.</summary>
    private async Task<AccountDeletionOutcome> FinishAsync(
        UserDto user, AuditContext ctx, ILambdaContext? log, DeletionCounts? counts)
    {
        try
        {
            EnsureDone(await _cognito.AdminDeleteUserAsync(user.UserId), "AdminDeleteUser");

            var deletedAt = user.DeletedAt
                ?? throw new InvalidOperationException("cognito_pending without deletedAt");
            await TransactionAuditWriter.EmitOnceCheckedAsync(
                _audit, TransactionId(user.UserId), "account", user.UserId, "account_deleted",
                AccountDeletionPolicy.CognitoPending, AccountDeletionPolicy.Completed, AccountActor(user, ctx),
                deletedAt, (counts ?? new DeletionCounts { Resumed = true }).ToMetadata());

            if (!await _repo.MarkCompletedAsync(user.UserId))
            {
                throw new InvalidOperationException("The users row left cognito_pending before completion.");
            }

            return Done(deletedAt);
        }
        catch (Exception ex)
        {
            return Incomplete(user.UserId, AccountDeletionPolicy.CognitoPending, ex, log);
        }
    }

    /// <summary>
    /// Step 9 abort: the account goes back to live (listings stay withdrawn, cancelled orders stay
    /// cancelled) and the request answers 409 with the blockers.
    /// </summary>
    private async Task<AccountDeletionOutcome> AbortAsync(
        UserDto user, List<AccountDeletionBlocker> blockers, AuditContext ctx, ILambdaContext? log)
    {
        if (!await _repo.TryClearInProgressAsync(user.UserId))
        {
            // Another run of this deletion is past the tombstone; that run checked the blockers itself.
            var fresh = await _repo.GetAccountAsync(user.UserId);
            return fresh?.DeletionState == AccountDeletionPolicy.Completed
                ? Done(fresh.DeletedAt ?? fresh.UpdatedAt)
                : Incomplete(user.UserId, fresh?.DeletionState ?? AccountDeletionPolicy.InProgress, null, log);
        }

        await TransactionAuditWriter.EmitAsync(
            _audit, log, TransactionId(user.UserId), "account", user.UserId, "account_deletion_aborted",
            AccountDeletionPolicy.InProgress, null, ctx, metadata: $"blockers={blockers.Count}");
        log?.Logger.LogWarning(
            $"Account deletion aborted for {MoneyAlarmLog.LogId(user.UserId)}: {blockers.Count} blocker(s) appeared after the first check.");
        return Blocked(blockers);
    }

    private async Task WithdrawListingsAsync(string userId, DeletionCounts counts, DeletionBudget budget)
    {
        var now = Iso(_clock());
        foreach (var crop in await _repo.QueryCropKeysAsync(userId))
        {
            budget.ThrowIfLow();
            if (await _repo.WithdrawCropAsync(crop, now))
            {
                counts.Crops++;
            }
        }

        foreach (var trip in await _repo.QueryTripKeysAsync(userId))
        {
            budget.ThrowIfLow();
            if (await _repo.WithdrawTripAsync(trip, now))
            {
                counts.Trips++;
            }
        }
    }

    /// <summary>
    /// Step 8. Each candidate is re-read strongly consistent and cancelled only while still unpaid
    /// at that version; a lost condition re-reads, and an order now in a blocking status is returned
    /// as a blocker for the step 9 abort.
    /// </summary>
    private async Task<List<AccountDeletionBlocker>> CancelUnpaidOrdersAsync(
        UserDto user, DeletionCounts counts, DeletionBudget budget, ILambdaContext? log)
    {
        var blockers = new List<AccountDeletionBlocker>();
        foreach (var row in await _repo.QueryOrdersAsync(user.UserId))
        {
            if (!AccountDeletionPolicy.IsUnpaidOrderStatus(AccountDeletionPolicy.Text(row.Item, "status")))
            {
                continue;
            }

            var orderId = AccountDeletionPolicy.Text(row.Item, "orderId");
            var createdAt = AccountDeletionPolicy.Text(row.Item, "createdAt");
            for (var attempt = 0; ; attempt++)
            {
                // Still unpaid after every lost race: neither cancelled nor a blocker, so the run
                // stops here (503) and the resume decides on it.
                if (attempt == MaxCancelAttempts)
                {
                    throw new InvalidOperationException($"Order {orderId} kept changing while it was being cancelled.");
                }

                budget.ThrowIfLow();
                var order = await _repo.GetOrderAsync(orderId, createdAt);
                if (order == null)
                {
                    break;
                }

                var status = AccountDeletionPolicy.Text(order, "status");
                var type = AccountDeletionPolicy.OrderBlockerType(
                    status, row.Role, await HasBuyerPaymentEvidenceAsync(order, row.Role));
                if (type != null)
                {
                    blockers.Add(Blocker(type, orderId, status, row.Role));
                    break;
                }

                if (!AccountDeletionPolicy.IsUnpaidOrderStatus(status))
                {
                    break;
                }

                if (await _repo.TryCancelUnpaidOrderAsync(order, Iso(_clock())))
                {
                    counts.OrdersCancelled++;
                    await TransactionAuditWriter.EmitAsync(
                        _audit, log, orderId, "order", orderId, "order_cancelled", status, "cancelled",
                        AuditContext.ForSystem(), metadata: $"cancelReason={AccountDeletionPolicy.CancelReason}");
                    break;
                }
            }
        }

        return blockers;
    }

    // ──────────────────────────────────────────────
    // Blocker check (steps 3 and 9)
    // ──────────────────────────────────────────────

    /// <summary>The rows the user is a party to and the blockers among them.</summary>
    internal sealed record DeletionScan(
        List<PartyRow> Orders, List<PartyRow> Escrows, List<PartyRow> Deliveries, List<AccountDeletionBlocker> Blockers);

    /// <summary>
    /// Find every row the user is a party to through the existing indexes and apply the blocker
    /// rules. A non-terminal order is re-read strongly consistent first: its index copy may lag.
    /// </summary>
    internal async Task<DeletionScan> ScanAsync(UserDto user, DeletionBudget budget)
    {
        var blockers = new List<AccountDeletionBlocker>();

        var orders = new List<PartyRow>();
        foreach (var row in await _repo.QueryOrdersAsync(user.UserId))
        {
            budget.ThrowIfLow();
            var order = row.Item;
            if (!AccountDeletionPolicy.IsTerminalOrderStatus(AccountDeletionPolicy.Text(order, "status")))
            {
                var fresh = await _repo.GetOrderAsync(
                    AccountDeletionPolicy.Text(order, "orderId"), AccountDeletionPolicy.Text(order, "createdAt"));
                if (fresh == null)
                {
                    continue;
                }

                order = fresh;
            }

            orders.Add(row with { Item = order });
            var orderId = AccountDeletionPolicy.Text(order, "orderId");
            var status = AccountDeletionPolicy.Text(order, "status");

            var hasSlot = row.Role == "buyer" && await _repo.HasCheckoutSlotAsync(orderId);
            var evidence = row.Role == "buyer" && (hasSlot || AccountDeletionPolicy.HasPaymentReference(order));
            var type = AccountDeletionPolicy.OrderBlockerType(status, row.Role, evidence);
            if (type != null)
            {
                blockers.Add(Blocker(type, orderId, status, row.Role));
            }

            // FUP-63: a captured payment the webhook did not apply blocks the buyer until its manual
            // refund is recorded, on an order of any status (UnappliedPayments, escrow-protocol 1.20.0).
            if (hasSlot)
            {
                var trail = await _repo.GetOrderPaymentTrailAsync(orderId);
                var payments = UnappliedPayments.Evaluate(
                    trail,
                    NullIfEmpty(AccountDeletionPolicy.Text(order, OrderFundingService.PaymentOrderIdField)),
                    NullIfEmpty(AccountDeletionPolicy.Text(order, OrderFundingService.MerchOrderIdField)));
                foreach (var payment in payments.Where(p => p.IsOpen))
                {
                    blockers.Add(Blocker(AccountDeletionPolicy.BlockerPaymentReview, orderId, payment.Actions[0], "buyer"));
                }
            }
        }

        budget.ThrowIfLow();
        var escrows = await _repo.QueryEscrowsAsync(user.UserId);
        foreach (var row in escrows)
        {
            var type = AccountDeletionPolicy.EscrowBlockerType(row.Item, row.Role);
            if (type != null)
            {
                blockers.Add(Blocker(type, AccountDeletionPolicy.Text(row.Item, "escrowId"),
                    AccountDeletionPolicy.Text(row.Item, "status"), row.Role));
            }
        }

        budget.ThrowIfLow();
        var buyerOrders = orders
            .Where(o => o.Role == "buyer" && AccountDeletionPolicy.MayHaveDelivery(AccountDeletionPolicy.Text(o.Item, "status")))
            .Select(o => new BuyerOrderRef(AccountDeletionPolicy.Text(o.Item, "orderId"), AccountDeletionPolicy.Text(o.Item, "listingId")))
            .ToList();
        var deliveries = await _repo.QueryDeliveriesAsync(user.UserId, buyerOrders);
        foreach (var row in deliveries)
        {
            var type = AccountDeletionPolicy.DeliveryBlockerType(row.Item);
            if (type != null)
            {
                blockers.Add(Blocker(type, AccountDeletionPolicy.Text(row.Item, "deliveryId"),
                    AccountDeletionPolicy.Text(row.Item, "status"), row.Role));
            }
        }

        return new DeletionScan(orders, escrows, deliveries, Distinct(blockers));
    }

    private async Task<bool> HasBuyerPaymentEvidenceAsync(Dictionary<string, object> order, string role)
        => role == "buyer"
           && (AccountDeletionPolicy.HasPaymentReference(order)
               || await _repo.HasCheckoutSlotAsync(AccountDeletionPolicy.Text(order, "orderId")));

    // ──────────────────────────────────────────────
    // Step 10: per-table policy
    // ──────────────────────────────────────────────

    private async Task ApplyPolicyAsync(
        string userId, DeletionScan scan, DeletionCounts counts, DeletionBudget budget, bool includeListings)
    {
        var now = Iso(_clock());

        if (includeListings)
        {
            await WithdrawListingsAsync(userId, counts, budget);
        }

        foreach (var group in GroupByKey(scan.Orders, "orderId"))
        {
            budget.ThrowIfLow();
            if (await _repo.ScrubOrderAsync(group.Item, group.Roles, now))
            {
                counts.Orders++;
            }
        }

        foreach (var group in GroupByKey(scan.Deliveries, "deliveryId"))
        {
            budget.ThrowIfLow();
            if (await _repo.ScrubDeliveryAsync(group.Item, group.Roles, now))
            {
                counts.Deliveries++;
            }
        }

        foreach (var group in GroupByKey(scan.Escrows, "escrowId"))
        {
            budget.ThrowIfLow();
            if (await _repo.ScrubEscrowAsync(group.Item, group.Roles))
            {
                counts.Escrows++;
            }
        }

        await ScrubRatingsAsync(userId, scan, counts, budget);

        foreach (var messageId in await _repo.QueryMessageIdsAsync(userId))
        {
            budget.ThrowIfLow();
            await _repo.DeleteMessageAsync(messageId);
            counts.Messages++;
        }

        // Match events first, then the intent, so a resume still finds the intent's events.
        foreach (var intentId in await _repo.QueryIntentIdsAsync(userId))
        {
            foreach (var eventId in await _repo.QueryMatchEventIdsAsync(intentId))
            {
                budget.ThrowIfLow();
                await _repo.DeleteMatchEventAsync(intentId, eventId);
                counts.MatchEvents++;
            }

            budget.ThrowIfLow();
            await _repo.DeleteIntentAsync(userId, intentId);
            counts.Intents++;
        }

        foreach (var endpointArn in await _repo.QueryDeviceEndpointsAsync(userId))
        {
            budget.ThrowIfLow();
            try
            {
                await _sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn });
            }
            catch (NotFoundException)
            {
                // Already gone: done.
            }

            await _repo.DeleteDeviceTokenAsync(userId, endpointArn);
            counts.DeviceTokens++;
        }
    }

    /// <summary>
    /// Rating comments go, stars stay: ratings about the user through the ratee index, and the
    /// ratings a buyer wrote by their deterministic ids (there is no rater index).
    /// </summary>
    private async Task ScrubRatingsAsync(string userId, DeletionScan scan, DeletionCounts counts, DeletionBudget budget)
    {
        var ratingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rating in await _repo.QueryRatingsReceivedAsync(userId))
        {
            if (rating.ContainsKey("comment"))
            {
                ratingIds.Add(AccountDeletionPolicy.Text(rating, "ratingId"));
            }
        }

        // A rating needs a delivered order, so orders that never had a delivery have none.
        foreach (var order in scan.Orders.Where(o =>
                     o.Role == "buyer" && AccountDeletionPolicy.MayHaveDelivery(AccountDeletionPolicy.Text(o.Item, "status"))))
        {
            var orderId = AccountDeletionPolicy.Text(order.Item, "orderId");
            foreach (var rateeId in new[] { AccountDeletionPolicy.Text(order.Item, "farmerId"), AccountDeletionPolicy.Text(order.Item, "truckerId") })
            {
                if (rateeId.Length == 0)
                {
                    continue;
                }

                budget.ThrowIfLow();
                var authored = await _repo.GetRatingAsync($"{orderId}:{rateeId}");
                if (authored != null && authored.ContainsKey("comment"))
                {
                    ratingIds.Add($"{orderId}:{rateeId}");
                }
            }
        }

        foreach (var ratingId in ratingIds.Where(id => id.Length > 0))
        {
            budget.ThrowIfLow();
            if (await _repo.RemoveRatingCommentAsync(ratingId))
            {
                counts.Ratings++;
            }
        }
    }

    // ──────────────────────────────────────────────
    // Completion sweep and reapply
    // ──────────────────────────────────────────────

    /// <summary>What a sweep run did.</summary>
    public sealed record SweepSummary(int Finished, int Aborted, int Failed, bool Truncated);

    /// <summary>
    /// Finish or abort every deletion started more than an hour ago (<c>in_progress</c> from step 5b,
    /// <c>cognito_pending</c> from step 12) as the system. Logs <c>ACCOUNT_DELETION_INCOMPLETE</c> per
    /// row it finishes, aborts or fails on, and <c>ACCOUNT_DELETION_STUCK</c> when it fails on a row
    /// for the second run. A row the run had no time left for is neither a failure nor stuck: the run
    /// stops there and the next one starts over.
    /// </summary>
    public async Task<SweepSummary> SweepAsync(DeletionBudget budget, ILambdaContext? log)
    {
        var now = _clock();
        var cutoff = Iso(now - SweepMinAge);
        int finished = 0, aborted = 0, failed = 0;
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
        do
        {
            if (budget.IsLow)
            {
                return new SweepSummary(finished, aborted, failed, Truncated: true);
            }

            var (userIds, next) = await _repo.ScanIncompleteAsync(cutoff, startKey);
            foreach (var userId in userIds)
            {
                if (budget.IsLow)
                {
                    return new SweepSummary(finished, aborted, failed, Truncated: true);
                }

                UserDto? user;
                try
                {
                    user = await _repo.GetAccountAsync(userId);
                }
                catch (Exception ex)
                {
                    failed++;
                    log?.Logger.LogWarning(
                        $"ACCOUNT_DELETION_INCOMPLETE userId={MoneyAlarmLog.LogId(userId)} state=- sweep=failed reason=[{ex.GetType().Name}]");
                    continue;
                }

                if (user == null || user.DeletionState is not (AccountDeletionPolicy.InProgress or AccountDeletionPolicy.CognitoPending))
                {
                    continue;
                }

                var outcome = await ResumeAsync(user, AuditContext.ForSystem(), budget, log);
                if (outcome.BudgetCut)
                {
                    log?.Logger.LogInformation(
                        $"Account deletion sweep stopped at {MoneyAlarmLog.LogId(userId)}: the run is out of time; the next run resumes it.");
                    return new SweepSummary(finished, aborted, failed, Truncated: true);
                }

                var result = outcome.Result switch
                {
                    AccountDeletionResult.Completed => "finished",
                    AccountDeletionResult.Blocked => "aborted",
                    _ => "failed"
                };
                log?.Logger.LogWarning(
                    $"ACCOUNT_DELETION_INCOMPLETE userId={MoneyAlarmLog.LogId(userId)} state={user.DeletionState} sweep={result}");

                switch (outcome.Result)
                {
                    case AccountDeletionResult.Completed:
                        finished++;
                        break;
                    case AccountDeletionResult.Blocked:
                        aborted++;
                        break;
                    default:
                        failed++;
                        if (IsStuck(user, now))
                        {
                            log?.Logger.LogError(
                                $"ACCOUNT_DELETION_STUCK userId={MoneyAlarmLog.LogId(userId)} state={outcome.DeletionState ?? user.DeletionState} requestedAt={user.DeletionRequestedAt}");
                        }

                        break;
                }
            }

            startKey = next;
        }
        while (startKey != null);

        return new SweepSummary(finished, aborted, failed, Truncated: false);
    }

    /// <summary>
    /// After a backup restore: rerun steps 10 and 11 for a deleted account, ending <c>completed</c>
    /// with the original <c>deletedAt</c> (no second <c>account_deleted</c> row). Only for a userId
    /// whose deletion finished: a <c>completed</c> row or an <c>account_deleted</c> audit row. A row
    /// still <c>in_progress</c> or <c>cognito_pending</c> without one is left to the sweep. Returns
    /// whether it reapplied.
    /// </summary>
    public async Task<bool> ReapplyAsync(string userId, DeletionBudget budget, ILambdaContext? log)
    {
        var user = await _repo.GetAccountAsync(userId);
        if (user == null)
        {
            return false;
        }

        var deletedRow = (await _audit.GetByTransactionAsync(TransactionId(userId)))
            .FirstOrDefault(r => AccountDeletionPolicy.Text(r, "action") == "account_deleted");
        if (deletedRow == null && user.DeletionState != AccountDeletionPolicy.Completed)
        {
            log?.Logger.LogWarning(
                $"Reapply skipped for {MoneyAlarmLog.LogId(userId)}: no finished deletion (state={user.DeletionState ?? "-"}).");
            return false;
        }

        var scan = await ScanAsync(user, budget);
        await ApplyPolicyAsync(userId, scan, new DeletionCounts(), budget, includeListings: true);

        var deletedAt = deletedRow != null ? AccountDeletionPolicy.Text(deletedRow, "timestamp") : user.DeletedAt;
        await _repo.TombstoneAsync(userId, string.IsNullOrEmpty(deletedAt) ? Iso(_clock()) : deletedAt,
            AccountDeletionPolicy.Completed, requireInProgress: false);
        return true;
    }

    // ──────────────────────────────────────────────
    // helpers
    // ──────────────────────────────────────────────

    /// <summary>Per-table counts of the rows this invocation wrote, for the <c>account_deleted</c> metadata.</summary>
    private sealed class DeletionCounts
    {
        public bool Resumed { get; init; }
        public int Crops { get; set; }
        public int Trips { get; set; }
        public int OrdersCancelled { get; set; }
        public int Orders { get; set; }
        public int Deliveries { get; set; }
        public int Escrows { get; set; }
        public int Ratings { get; set; }
        public int Messages { get; set; }
        public int Intents { get; set; }
        public int MatchEvents { get; set; }
        public int DeviceTokens { get; set; }

        public string ToMetadata()
            => FormattableString.Invariant(
                   $"crops={Crops};trips={Trips};ordersCancelled={OrdersCancelled};orders={Orders};deliveries={Deliveries};escrows={Escrows};ratings={Ratings};messages={Messages};intents={Intents};matchEvents={MatchEvents};deviceTokens={DeviceTokens}")
               + (Resumed ? ";resumed=true" : "");
    }

    private static bool IsStuck(UserDto user, DateTime now)
        => DateTime.TryParse(user.DeletionRequestedAt, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var requestedAt)
           && now - requestedAt >= StuckAfter;

    /// <summary>A Cognito admin call succeeded, or the user is already gone (NotFound counts as done).</summary>
    private static void EnsureDone(AuthResult<bool> result, string call)
    {
        if (!result.IsSuccess && result.Error?.HttpStatus != 404)
        {
            throw new InvalidOperationException($"{call} failed: {result.Error?.Code}");
        }
    }

    private static IEnumerable<(Dictionary<string, object> Item, List<string> Roles)> GroupByKey(
        List<PartyRow> rows, string keyField)
        => rows
            .GroupBy(r => AccountDeletionPolicy.Text(r.Item, keyField), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .Select(g => (g.First().Item, g.Select(r => r.Role).Distinct(StringComparer.Ordinal).ToList()));

    private static List<AccountDeletionBlocker> Distinct(IEnumerable<AccountDeletionBlocker> blockers)
        => blockers
            .GroupBy(b => (b.Type, b.Id, b.Role))
            .Select(g => g.First())
            .ToList();

    private static AccountDeletionBlocker Blocker(string type, string id, string status, string role)
        => new() { Type = type, Id = id, Status = status, Role = role };

    /// <summary>The account rows of the audit trail name the user as actor, as in step 5b.</summary>
    private static AuditContext AccountActor(UserDto user, AuditContext ctx) => ctx with
    {
        Actor = user.UserId,
        ActorRole = user.Role
    };

    internal static string TransactionId(string userId) => $"account-{userId}";

    private static AccountDeletionOutcome Outcome(AccountDeletionResult result) => new() { Result = result };

    private static AccountDeletionOutcome Done(string deletedAt) => new()
    {
        Result = AccountDeletionResult.Completed,
        DeletionState = AccountDeletionPolicy.Completed,
        DeletedAt = deletedAt
    };

    private static AccountDeletionOutcome Blocked(List<AccountDeletionBlocker> blockers) => new()
    {
        Result = AccountDeletionResult.Blocked,
        Blockers = blockers
    };

    private static AccountDeletionOutcome Incomplete(string userId, string state, Exception? ex, ILambdaContext? log)
    {
        log?.Logger.LogWarning(
            $"ACCOUNT_DELETION_INCOMPLETE userId={MoneyAlarmLog.LogId(userId)} state={state} reason=[{ex?.GetType().Name ?? "state"}]");
        return new AccountDeletionOutcome
        {
            Result = AccountDeletionResult.Incomplete,
            DeletionState = state,
            BudgetCut = ex is DeletionBudgetExceededException
        };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
