using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>FRD-03: the escrow row an order write is fenced on: its table and key.</summary>
public sealed record EscrowRowKey(string TableName, string EscrowId, string CreatedAt);

/// <summary>
/// Repository interface for order persistence in DynamoDB.
/// Supports sync operations with version-based conflict detection.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// Create a new order. Fails if orderId already exists.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string orderId,
        Dictionary<string, object> data);

    /// <summary>
    /// Update an existing order with optimistic concurrency.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion);

    /// <summary>
    /// FUP-32: update the order like <see cref="UpdateAsync"/> (<paramref name="data"/> set, version
    /// checked and incremented, new <c>updatedAt</c>), also only while it is still
    /// <paramref name="expectedStatus"/>, and write <paramref name="linkedItems"/> (conditional writes
    /// to other tables) in the same DynamoDB transaction: all of it lands or none of it. A linked
    /// item's failed condition is reported over the order's. Returns the order as written: the row
    /// read strongly consistent before the write, with the update applied (a transaction returns no
    /// item, and a read after the commit could fail after the money moved). Unlike
    /// <see cref="UpdateAsync"/>'s <c>ALL_NEW</c>, it can miss a field written without a version bump
    /// between that read and the commit, such as a delivery slot claim (<c>activeDeliveryId</c>);
    /// callers that act on such fields read the row again. A transaction cancelled for any other
    /// reason (a conflicting transaction, throttling) throws, with nothing written.
    /// </summary>
    Task<OrderLinkedWriteResult> TryUpdateWithAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion,
        string expectedStatus,
        IReadOnlyList<TransactWriteItem> linkedItems);

    /// <summary>
    /// FRD-03: <see cref="UpdateAsync"/> in one <c>TransactWriteItems</c> with a <c>ConditionCheck</c> that
    /// the order's escrow (<paramref name="escrow"/>) is not <c>held</c>, so a settlement sweep that holds it
    /// between the caller's read and this write wins. Fails with <c>409</c>
    /// <see cref="OrderRepository.EscrowHeldCode"/> when the escrow is held (nothing written), and like
    /// <see cref="UpdateAsync"/> on a version conflict. On success it returns the order as written (a
    /// strongly consistent read).
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> UpdateUnlessEscrowHeldAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion,
        EscrowRowKey escrow);

    /// <summary>
    /// Get an order by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string orderId);

    /// <summary>
    /// Get an order by ID with a strongly consistent read when
    /// <paramref name="consistentRead"/> is true. Money paths that classify a payment
    /// against the order's current state use it so they never act on a stale copy.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string orderId, bool consistentRead);

    /// <summary>
    /// ESC-01: fund an order in one conditional write. The write only succeeds while the
    /// order is still <c>payment_submitted</c>; it sets <c>status = funded</c> plus
    /// <paramref name="fields"/> and increments the version. Unlike
    /// <see cref="UpdateAsync"/> it does not depend on the caller's version, so an
    /// unrelated buyer update (payment method, reference) cannot make a verified payment
    /// fail. Returns the updated order, or null when the order is missing or no longer
    /// <c>payment_submitted</c> (the caller re-reads and classifies).
    /// </summary>
    Task<Dictionary<string, object>?> TryMarkFundedAsync(string orderId, Dictionary<string, object> fields);

    /// <summary>
    /// SIM-09, ESC-08: claim the order's single active-delivery slot and write the delivery row in
    /// one transaction, so a slot is never held by a delivery that was not written. The claim
    /// succeeds when the slot is free or already held by this delivery (idempotent retry); with
    /// <paramref name="replacing"/> it only takes the slot from that stale holder, and only while
    /// the holder's claim time is still the one that was read (a live retry of the holder keeps
    /// it). The row write requires that no row with its <c>deliveryId</c> exists.
    /// </summary>
    /// <param name="deliveryItem">The full delivery row, including <c>deliveryId</c>.</param>
    Task<DeliverySlotWriteResult> TryCreateDeliveryWithSlotAsync(
        string orderId,
        string deliveriesTable,
        Dictionary<string, AttributeValue> deliveryItem,
        DeliverySlotHolder? replacing = null);

    /// <summary>
    /// SIM-09: free the order's delivery slot when <paramref name="deliveryId"/> is cancelled
    /// so a replacement delivery can be created. No-op when another delivery holds it.
    /// </summary>
    Task ReleaseDeliverySlotAsync(string orderId, string deliveryId);

    /// <summary>
    /// Query orders with optional filters using GSIs.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOrdersAsync(
        string? buyerId = null,
        string? farmerId = null,
        string? truckerId = null,
        string? status = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query orders by status for cron processing (e.g. auto-cancel expired).
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(string status, string? createdBefore = null);

    /// <summary>
    /// ESC-06: put the order in the admin stuck-order queue (<see cref="AdminQueueFields"/>) while
    /// it is still <paramref name="expectedStatus"/> and not queued yet. Written without bumping
    /// <c>version</c> or <c>updatedAt</c>, like the delivery slot claim. Returns false when the
    /// order is missing, already queued or moved on.
    /// </summary>
    Task<bool> TryEnterAdminQueueAsync(
        string orderId, string reason, string escrowId, string queuedAt, string expectedStatus);

    /// <summary>
    /// ESC-06: take the order out of the admin queue, only while the entry is the one queued at
    /// <paramref name="seenQueuedAt"/> (a stale reader never removes a newer entry). Not versioned.
    /// Returns false when the entry is gone or different.
    /// </summary>
    Task<bool> TryLeaveAdminQueueAsync(string orderId, string seenQueuedAt);

    /// <summary>ESC-06: one page of the admin queue (<c>AdminQueueIndex</c>), oldest first.</summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryAdminQueueAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// ESC-45 (D8): hold a <c>payment_submitted</c> order out of the order cron's
    /// <c>payment_failed</c> until <paramref name="holdUntil"/> (ISO-8601 UTC), because a verified
    /// Telebirr payment for it is waiting in the webhook inbox. Extend-only; when
    /// <paramref name="paymentSubmittedAt"/> is given, only for that payment attempt. Written without
    /// bumping <c>version</c> or <c>updatedAt</c> (a server bookkeeping field must not make a client
    /// conflict). False when the order is missing, not <c>payment_submitted</c>, in another attempt,
    /// or already held as long.
    /// </summary>
    Task<bool> TryHoldForPaymentAsync(string orderId, string holdUntil, string? paymentSubmittedAt);

    /// <summary>
    /// ESC-45 (D8): the order cron's <c>payment_submitted -> payment_failed</c>, version-checked like
    /// <see cref="UpdateAsync"/> and also conditional on no payment hold beyond
    /// <paramref name="nowIso"/>, so a hold written after the cron read the order still wins. False
    /// when any condition failed or the order is missing.
    /// </summary>
    Task<bool> TryMarkPaymentFailedAsync(string orderId, int expectedVersion, string nowIso);

    /// <summary>
    /// ESC-09: move the order from <paramref name="fromStatus"/> to <paramref name="toStatus"/> in one
    /// write conditioned on the status (not on the caller's version), with <c>ADD version 1</c> and a
    /// new <c>updatedAt</c>, like <see cref="TryMarkFundedAsync"/>. ESC-15: <paramref name="extraFields"/>
    /// are set in the same write (the keys, <c>version</c>, <c>updatedAt</c> and <c>status</c> are
    /// skipped). Returns the updated order, or null when the order is missing or no longer
    /// <paramref name="fromStatus"/> (never creates a row).
    /// </summary>
    Task<Dictionary<string, object>?> TryAdvanceStatusAsync(
        string orderId, string fromStatus, string toStatus, Dictionary<string, object>? extraFields = null);

    /// <summary>
    /// ESC-15: every order of a trip (<c>TripIdIndex</c>, all pages, oldest first), for the trip's
    /// cancellation and the capacity bookkeeping. Deleted and cancelled orders are included; the
    /// caller filters by status.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByTripAsync(string tripId);

    /// <summary>
    /// ESC-15: set <paramref name="fields"/> on the order WITHOUT bumping <c>version</c> or touching
    /// <c>updatedAt</c> (server bookkeeping such as reminder markers must never make a client
    /// conflict), only while the order exists and each entry of <paramref name="expectedStrings"/>
    /// still equals the row (string equality; an empty map checks existence only). The keys,
    /// <c>version</c>, <c>updatedAt</c> and <c>status</c> are skipped from <paramref name="fields"/>.
    /// Returns false when the order is missing or any expectation failed.
    /// </summary>
    Task<bool> TryStampAsync(
        string orderId, Dictionary<string, object> fields, IReadOnlyDictionary<string, string> expectedStrings);

    /// <summary>
    /// ESC-09: one page of the <c>StatusCreatedAtIndex</c> partition for <paramref name="status"/>,
    /// oldest first, without the rows the money sweep marked as decided at that status
    /// (<see cref="OrderMoneyFields.SweepSkip"/>). <paramref name="limit"/> is the evaluation limit.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryMoneySweepPageAsync(
        string status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// ESC-09: mark the order as decided by the money sweep while it is still <paramref name="status"/>
    /// (<see cref="OrderMoneyFields.SweepSkip"/>), without bumping <c>version</c> or <c>updatedAt</c>.
    /// Returns false when the order is missing or its status moved on.
    /// </summary>
    Task<bool> TryMarkMoneySweepSkipAsync(string orderId, string status, string markedAt);
}

/// <summary>ESC-45 (D8): the server-only order field that holds an order for a pending payment.</summary>
public static class PaymentHoldFields
{
    public const string HoldUntil = "paymentHoldUntil";
}

/// <summary>
/// Outcome of <see cref="IOrderRepository.TryCreateDeliveryWithSlotAsync"/>. Only
/// <see cref="Written"/> wrote anything. <see cref="SlotTaken"/> is the zero value so an unset
/// result never reads as success.
/// </summary>
public enum DeliverySlotWriteResult
{
    /// <summary>Another delivery holds the slot, or the stale holder being replaced changed.</summary>
    SlotTaken = 0,

    /// <summary>The slot is held by the delivery and its row was written.</summary>
    Written,

    /// <summary>A delivery row with this <c>deliveryId</c> already exists.</summary>
    DeliveryExists,

    /// <summary>The order does not exist.</summary>
    OrderNotFound,

    /// <summary>ESC-06: the order no longer takes a delivery (it is not accepted, pickup_confirmed or in_transit).</summary>
    OrderNotActive,
}

/// <summary>
/// FUP-32: what <see cref="IOrderRepository.TryUpdateWithAsync"/> did. Only <see cref="Written"/>
/// wrote anything. <see cref="OrderChanged"/> is the zero value so an unset result never reads as
/// success.
/// </summary>
public enum OrderLinkedWriteOutcome
{
    /// <summary>The order's version or status is no longer the expected one (re-read and decide).</summary>
    OrderChanged = 0,

    /// <summary>The order and every linked item were written.</summary>
    Written,

    /// <summary>The order does not exist.</summary>
    OrderNotFound,

    /// <summary>A linked item's condition failed (for example the escrow is no longer a plain hold).</summary>
    LinkedItemRefused,
}

/// <summary>FUP-32: the outcome of <see cref="IOrderRepository.TryUpdateWithAsync"/>, with the order as written.</summary>
public readonly record struct OrderLinkedWriteResult(OrderLinkedWriteOutcome Outcome, Dictionary<string, object>? Order = null);

/// <summary>
/// ESC-08: the delivery an order's slot names (<c>activeDeliveryId</c>) and when it claimed the
/// slot (<c>activeDeliveryClaimedAt</c>, null for claims written before ESC-08).
/// </summary>
public sealed record DeliverySlotHolder(string DeliveryId, string? ClaimedAt);
