using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for escrow persistence in DynamoDB.
/// Supports escrow lifecycle operations: create, get, update status, and queries by various indexes.
/// </summary>
public interface IEscrowRepository
{
    /// <summary>
    /// Create a new escrow record.
    /// </summary>
    Task CreateAsync(Dictionary<string, object> escrow);

    /// <summary>
    /// Get an escrow by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string escrowId);

    /// <summary>
    /// Get an escrow by ID with a strongly consistent read when
    /// <paramref name="consistentRead"/> is true. The manual payout queue (ESC-02) uses it
    /// to decide on the row's current state after a conditional write failed or threw.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string escrowId, bool consistentRead);

    /// <summary>
    /// Update escrow status with optimistic concurrency check on the expected current status.
    /// Throws ConditionalCheckFailedException if the current status doesn't match expectedCurrentStatus.
    /// A move to <c>released</c>, <c>refunded</c> or <c>disputed</c> also requires that the escrow
    /// has no open manual payout (ESC-02); a queued escrow settles only through TryCloseManualPayoutAsync.
    /// A move to <c>disputed</c> also requires that no settlement holds the lock (ESC-03) and that no
    /// refund is scheduled (<c>refundReason</c>, FUP-32).
    /// ESC-04: a move to <c>completed</c> requires no open manual payout and no pending trucker leg
    /// (<c>truckerPayoutPending</c>): an escrow is completed only once every leg is paid.
    /// </summary>
    /// <param name="escrowId">The escrow partition key.</param>
    /// <param name="createdAt">The escrow sort key.</param>
    /// <param name="newStatus">The new status value.</param>
    /// <param name="expectedCurrentStatus">The expected current status for optimistic concurrency. If null, no status check is performed.</param>
    /// <param name="additionalFields">Optional extra fields to update.</param>
    Task UpdateStatusAsync(string escrowId, string createdAt, string newStatus, string? expectedCurrentStatus = null, Dictionary<string, object>? additionalFields = null);

    /// <summary>
    /// FUP-32: the escrow half of an order cancel that refunds, for
    /// <see cref="IOrderRepository.TryUpdateWithAsync"/>. The item schedules the refund for the hourly
    /// sweep (<c>expiresAt</c> and <c>updatedAt</c> set to <paramref name="scheduledAt"/>,
    /// <c>refundReason</c>) only while the escrow is a plain hold: <c>escrowed</c>, no settlement
    /// lock, no open manual payout, no confirmed delivery. No I/O.
    /// </summary>
    TransactWriteItem BuildRefundArm(string escrowId, string createdAt, string reason, string scheduledAt);

    /// <summary>
    /// ESC-06: push an order escrow's <c>expiresAt</c> forward while it is still a plain
    /// <c>escrowed</c> hold: no refund scheduled (<c>refundReason</c>), no settlement lock, no open
    /// manual payout. Returns false when that no longer holds (a refund scheduled in the meantime
    /// keeps its <c>expiresAt</c>, so the sweep refunds it).
    /// </summary>
    Task<bool> TryExtendExpiryAsync(string escrowId, string createdAt, string expiresAt);

    /// <summary>
    /// Atomically claim settlement (release or refund) for an escrow BEFORE any
    /// irreversible Telebirr call. Sets a <c>settlementLock</c> attribute conditional
    /// on no lock already existing, no open manual payout (ESC-02), and the escrow still
    /// being in <paramref name="expectedStatus"/>, which must be settleable (<c>escrowed</c>
    /// or <c>dispute_resolved</c>, ESC-03). Returns false if another settlement already holds the
    /// lock or the escrow is no longer in that status; the caller then MUST NOT call Telebirr.
    /// This is the mutual-exclusion guard that prevents a release and a refund (or two
    /// releases) from both moving funds for the same escrow. ESC-05: the write also stamps
    /// <c>settlementLockAt</c> and a random <c>settlementLockId</c>; when it fails its condition,
    /// a strongly consistent re-read that shows this very lock (same op and lock id, written by an
    /// earlier attempt whose answer was lost) counts as acquired.
    /// </summary>
    /// <param name="op">The operation claiming the lock ("release" or "refund").</param>
    /// <param name="expectedStatus">The status the caller validated the operation against.</param>
    /// <param name="allowHeld">
    /// FRD-03: only the admin release and refund set it, so <c>held</c> joins the settleable statuses for
    /// them alone; the sweeps can never lock a held escrow.
    /// </param>
    Task<bool> TryAcquireSettlementLockAsync(string escrowId, string createdAt, string op, string expectedStatus, bool allowHeld = false);

    /// <summary>
    /// FRD-03: freeze an escrow for fraud review (<c>status = held</c> with <c>holdReason</c>, <c>heldAt</c>,
    /// <c>heldBy = system</c>, <c>heldFromStatus</c>, <c>heldFor</c>; the version is added to), conditional
    /// on the status still being <see cref="EscrowHoldRequest.FromStatus"/> with no settlement lock and no
    /// open manual payout, so a settlement already in progress wins. With <paramref name="fence"/> the
    /// write is one <c>TransactWriteItems</c> with a <c>ConditionCheck</c> that the order's version and
    /// status are still what the sweep read. A failed condition whose strongly consistent re-read shows
    /// this very hold (same <c>heldAt</c>, an earlier attempt whose answer was lost) counts as held.
    /// </summary>
    Task<EscrowHoldWrite> TryHoldAsync(string escrowId, string createdAt, EscrowHoldRequest hold, OrderFence? fence = null);

    /// <summary>
    /// Release a previously-acquired settlement lock (with its time and id) after a definite
    /// gateway refusal, so the next sweep can retry. No-op if the lock is absent, and on a row
    /// with an open manual payout (the queue owns the lock, ESC-05). Never called once the
    /// gateway may have moved money.
    /// </summary>
    Task ReleaseSettlementLockAsync(string escrowId, string createdAt);

    /// <summary>
    /// ESC-05: settleable rows (<paramref name="status"/>) whose settlement lock was taken before
    /// <paramref name="lockedBefore"/> and that are not queued, from <c>StatusExpiresIndex</c>.
    /// A lock without <c>settlementLockAt</c> (taken before ESC-05) is aged by <c>updatedAt</c>.
    /// Pages through filtered-empty pages up to <paramref name="limit"/>.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryStaleSettlementLocksAsync(string status, string lockedBefore, int limit = 50);

    /// <summary>
    /// ESC-05: park a stale settlement in the manual payout queue with reason
    /// <c>GATEWAY_OUTCOME_UNKNOWN</c>. Same write as <see cref="TryQueueManualPayoutAsync"/>,
    /// also conditional on the status the recovery step read (<paramref name="fromStatus"/>) and
    /// on the same lock: <c>settlementLockId = </c><paramref name="lockId"/>, or no lock id at
    /// all (a lock from before ESC-05) when <paramref name="lockId"/> is null. Also stamps
    /// <c>manualPayoutParkId = </c><paramref name="parkId"/>. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryQueueStaleSettlementAsync(
        string escrowId, string createdAt, string op, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt, string? lockId, string parkId);

    /// <summary>
    /// ESC-02: put a settlement the gateway could not make into the manual payout queue.
    /// Marks the row <c>manualPayout = open</c> with the given legs, conditional on the
    /// caller holding the settlement lock for <paramref name="op"/> and no queue entry
    /// existing yet. The lock is deliberately KEPT: nothing else may settle this escrow
    /// until an admin has paid every leg. <paramref name="queuedAt"/> becomes the index sort
    /// key and the timestamp of the queued audit row. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt);

    /// <summary>
    /// ESC-02: record an admin-paid leg that is NOT the last pending one. Conditional on
    /// the queue being open for <paramref name="op"/>, this leg still pending and
    /// <paramref name="otherLegMustBePending"/> (when given) still pending, which proves
    /// another leg remains and the escrow must stay in its current status. Returns false
    /// when the condition fails (the caller re-reads and decides again).
    /// </summary>
    Task<bool> TryMarkManualLegPaidAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, string? otherLegMustBePending);

    /// <summary>
    /// ESC-02: record the LAST pending leg and close the queue in one write: the leg
    /// becomes paid, <paramref name="otherLegsMustBePaid"/> are checked paid, the escrow
    /// moves to <paramref name="newStatus"/> (<c>released</c> or <c>refunded</c>) with the
    /// given settlement fields, and the <c>manualPayout</c> marker is removed. Conditional
    /// on the queue being open for <paramref name="op"/> and the status still settleable.
    /// ESC-04: a trucker leg queued on a <c>released</c> escrow (from status <c>released</c>)
    /// closes too, to <c>completed</c>; a release close also removes the trucker leg's pending
    /// flag, block reason and lock. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryCloseManualPayoutAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, IReadOnlyList<string> otherLegsMustBePaid,
        string newStatus, Dictionary<string, object> settlementFields);

    /// <summary>
    /// ESC-02: open manual payouts, oldest first, from the sparse <c>ManualPayoutIndex</c>.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOpenManualPayoutsAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// ESC-04: <c>released</c> escrows whose trucker leg is still owed and not queued, from
    /// <c>StatusExpiresIndex</c> (oldest <c>expiresAt</c> first), paging through filtered-empty
    /// pages up to <paramref name="limit"/>.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryPendingTruckerLegsAsync(int limit);

    /// <summary>
    /// ESC-04: <c>completed</c> escrows still flagged <c>truckerPayoutPending</c> (written before
    /// ESC-04, when a release completed without its trucker leg) and not queued, up to
    /// <paramref name="limit"/>. Only the one-off legacy action reads them.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryLegacyTruckerLegsAsync(int limit);

    /// <summary>
    /// ESC-04: record why the payout gate blocks the trucker, as a compare-and-set: only when the
    /// stored reason is missing or different, the escrow is still <c>released</c> with the leg
    /// pending, unqueued and unlocked. Also stores <paramref name="reasonAt"/>, the key of the
    /// reason's audit row. Returns false when the condition fails, so a reason is audited once
    /// however many runs see it.
    /// </summary>
    Task<bool> TrySetTruckerBlockReasonAsync(string escrowId, string createdAt, string reason, string reasonAt);

    /// <summary>
    /// ESC-04: copy the trucker the escrow pays from its order, only when the escrow has none.
    /// Returns false when the condition fails (a truckerId is already there).
    /// </summary>
    Task<bool> TryBackfillTruckerIdAsync(string escrowId, string createdAt, string truckerId);

    /// <summary>
    /// ESC-04: take the trucker leg's lock (<c>truckerPayoutLockId</c>, <c>truckerPayoutLockAt</c>)
    /// before the gateway call, conditional on the escrow being <c>released</c> with the leg
    /// pending, unlocked and unqueued; clears the block reason and its time. Returns the lock id, or null when
    /// the condition fails. A failed condition whose strongly consistent re-read shows this very
    /// lock id (an earlier attempt whose answer was lost) counts as acquired.
    /// </summary>
    Task<string?> TryAcquireTruckerPayoutLockAsync(string escrowId, string createdAt);

    /// <summary>
    /// ESC-04: release the trucker leg's lock after a definite refusal, only when it is still
    /// <paramref name="lockId"/> and the escrow is not queued. Returns false when the condition fails.
    /// </summary>
    Task<bool> ReleaseTruckerPayoutLockAsync(string escrowId, string createdAt, string lockId);

    /// <summary>
    /// ESC-04: the gateway paid the trucker. One write moves the escrow from <c>released</c> to
    /// <c>completed</c> with the trucker's reference, conditional on the lock still being
    /// <paramref name="lockId"/> and no queue entry, and removes the pending flag, the block reason
    /// and the lock. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryRecordTruckerPayoutAsync(
        string escrowId, string createdAt, string lockId, string truckerRef, string paidAt, string completedAt);

    /// <summary>
    /// ESC-04: put the trucker leg of a <c>released</c> escrow into the manual payout queue (op
    /// <c>release</c>, from status <c>released</c>, the farmer leg seeded paid), conditional on the
    /// escrow still being <c>released</c>, the trucker lock still being <paramref name="lockId"/>
    /// and no queue entry. The lock and the pending flag stay. With <paramref name="parkId"/> (the
    /// recovery of a stale trucker lock) also stamps <c>manualPayoutParkId</c>. Returns false when
    /// the condition fails.
    /// </summary>
    Task<bool> TryQueueTruckerLegAsync(
        string escrowId, string createdAt, string reason, IReadOnlyList<ManualPayoutLegSeed> legs,
        string queuedAt, string lockId, string? parkId);

    /// <summary>
    /// ESC-04 legacy: an escrow whose trucker leg was left pending before ESC-04 (no
    /// <c>truckerPayoutPendingSince</c>): <paramref name="currentStatus"/> <c>completed</c> (a release
    /// completed without it) or <c>released</c> (a crash between the two writes). In one write the
    /// escrow becomes <c>released</c> (a completed one loses <c>completedAt</c>) and the leg is queued
    /// as <c>GATEWAY_OUTCOME_UNKNOWN</c>, conditional on the same status, the leg pending without a
    /// pending time or trucker lock, and no queue entry. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryParkLegacyTruckerLegAsync(
        string escrowId, string createdAt, string currentStatus, IReadOnlyList<ManualPayoutLegSeed> legs,
        string queuedAt, string pendingSince);

    /// <summary>
    /// Get an escrow by its associated delivery ID using the DeliveryIndex GSI.
    /// </summary>
    Task<Dictionary<string, object>?> GetByDeliveryIdAsync(string deliveryId);

    /// <summary>
    /// Query escrows by their associated order ID using the sparse OrderIndex GSI,
    /// newest first. An order can carry several rows over its life (an inert
    /// refunded/cancelled row next to an active one), so this returns a small list
    /// for the caller to filter by status.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByOrderIdAsync(string orderId, int limit = 5);

    /// <summary>
    /// Query escrows by buyer ID using the BuyerStatusIndex GSI.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByBuyerAsync(
        string buyerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by farmer ID using the FarmerStatusIndex GSI.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by trucker ID using the TruckerStatusIndex GSI (sparse: only
    /// escrows whose order was bound to a truck carry truckerId). SIM-15.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by status for cron/background processing using the StatusExpiresIndex GSI.
    /// Escrows with an open manual payout (ESC-02) or a pending trucker leg (ESC-04, owned by
    /// the trucker retry) are filtered out, and paging continues through filtered-empty pages,
    /// so such rows can never fill the window.
    /// </summary>
    /// <param name="status">Status to query (e.g. "held", "pending_release").</param>
    /// <param name="expiresAtBefore">Optional upper bound for expiresAt (ISO 8601).</param>
    /// <param name="deliveryConfirmedBefore">Optional upper bound for deliveryConfirmedAt (post-query filter).</param>
    /// <param name="limit">Maximum results to return.</param>
    Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100);

    /// <summary>
    /// ESC-10: one page of a status sweep on <c>StatusExpiresIndex</c>, oldest <c>expiresAt</c> first:
    /// a single Query of <see cref="CronSweepQuery.PageSize"/> evaluated rows with the filters of
    /// <see cref="QueryByStatusForCronAsync"/> plus the query's own. A filtered page can be empty with a
    /// <c>LastEvaluatedKey</c>; the caller pages on, so rows it skips never hide the rows behind them.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCronPageAsync(
        CronSweepQuery query, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// ESC-10: record why a sweep or the payout gate blocks this escrow's settlement, as a
    /// compare-and-set: only when the stored reason is missing or different, and the escrow is still
    /// <paramref name="expectedStatus"/>, unlocked and unqueued. Also stores <paramref name="reasonAt"/>,
    /// the key of the reason's audit row. Returns false when the condition fails, so a reason is
    /// audited once however many runs see it.
    /// </summary>
    Task<bool> TrySetSettlementBlockReasonAsync(
        string escrowId, string createdAt, string expectedStatus, string reason, string reasonAt);

    /// <summary>
    /// ESC-10: remove the stored block reason once its gate passes, only when it is still
    /// <paramref name="storedReason"/>. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryClearSettlementBlockReasonAsync(string escrowId, string createdAt, string storedReason);
}

/// <summary>FRD-03: what <see cref="IEscrowRepository.TryHoldAsync"/> found.</summary>
public enum EscrowHoldWrite
{
    /// <summary>The escrow is held now (by this write).</summary>
    Held,

    /// <summary>The escrow moved on, or a settlement lock or queue entry appeared: nothing held.</summary>
    EscrowChanged,

    /// <summary>The fenced order's version or status changed since the sweep read it: nothing held.</summary>
    OrderChanged,

    /// <summary>The write met a concurrent transaction or a throttle the SDK retries did not clear: nothing held, the next run retries.</summary>
    Conflict
}

/// <summary>
/// FRD-03: one hold (fraud-detection-architecture.md section 18.2). <paramref name="FromStatus"/> is
/// <c>escrowed</c> or <c>dispute_resolved</c>, <paramref name="HeldFor"/> <c>release</c> or <c>refund</c>,
/// <paramref name="HeldAt"/> ISO 8601 (also the key of the hold's audit row).
/// </summary>
public sealed record EscrowHoldRequest(string FromStatus, string HeldFor, string HoldReason, string HeldAt);

/// <summary>
/// FRD-03: the order a hold is fenced on (the auto-refund sweep, like its cancel-then-refund): the orders
/// table, the order's key, and the version and status the sweep read.
/// </summary>
public sealed record OrderFence(string TableName, string OrderId, string CreatedAt, int Version, string Status);

/// <summary>
/// ESC-10: what one page of a status sweep reads. <paramref name="DeliveryConfirmed"/> true keeps only
/// rows with <c>deliveryConfirmedAt</c>, false only rows without it; <paramref name="ExcludeLocked"/>
/// drops rows a settlement holds (the stale lock recovery owns those).
/// </summary>
public sealed record CronSweepQuery(
    string Status,
    string? ExpiresAtBefore = null,
    bool? DeliveryConfirmed = null,
    bool ExcludeLocked = false,
    int PageSize = 100);
