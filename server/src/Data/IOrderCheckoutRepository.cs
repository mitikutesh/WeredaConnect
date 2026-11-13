namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-01: persistence for live Telebirr order checkouts, stored in the escrow idempotency
/// table (PK <c>idempotencyKey</c>; these rows get a 400-day TTL, far past any checkout
/// window, so a merch_order_id is never reused) as two kinds of rows:
/// <list type="bullet">
/// <item>A slot per order (<c>order-checkout:{orderId}</c>) so an order has at most one open
/// checkout: a retry gets the same payment back instead of a second payable one.</item>
/// <item>A mapping per attempt (key = the server-generated merch_order_id) that the Telebirr
/// webhook resolves to the order, buyer and exact amount.</item>
/// </list>
/// </summary>
public interface IOrderCheckoutRepository
{
    /// <summary>The order's checkout slot, or null when none was ever opened.</summary>
    Task<OrderCheckoutSlot?> GetSlotAsync(string orderId);

    /// <summary>
    /// Write <paramref name="slot"/> as the order's current checkout. With
    /// <paramref name="previousAttempt"/> null it succeeds only when no slot exists; otherwise
    /// only while the stored slot is still that attempt, so two concurrent openers cannot both
    /// win. False when the condition failed.
    /// </summary>
    Task<bool> TryOpenSlotAsync(OrderCheckoutSlot slot, int? previousAttempt);

    /// <summary>
    /// Record the outcome (and the prepay id) of the slot's attempt, and move its
    /// <c>openUntil</c> when <paramref name="openUntilUtc"/> is given (the window starts when
    /// Telebirr answers, not when the slot was written). No-op when the slot has moved on to
    /// another attempt.
    /// </summary>
    Task UpdateSlotOutcomeAsync(
        string orderId, int attempt, string outcome, string? prepayId = null, DateTime? openUntilUtc = null);

    /// <summary>
    /// Record a final outcome for the checkout identified by <paramref name="merchOrderId"/>
    /// (paid). No-op when the slot now belongs to another attempt.
    /// </summary>
    Task CloseSlotForMerchOrderAsync(string orderId, string merchOrderId, string outcome);

    /// <summary>
    /// FUP-37: record what the queryOrder reconciliation concluded for the order's payment attempt
    /// <paramref name="cycle"/> (its <c>paymentSubmittedAt</c>, empty for an order without one), up to
    /// this slot's <paramref name="attempt"/> (<see cref="OrderCheckoutQuery"/>). Only while the slot is
    /// still that attempt: a new attempt replaces the whole slot, and the verdict with it. False when the
    /// slot has moved on (or is gone).
    /// </summary>
    Task<bool> RecordQueryVerdictAsync(string orderId, int attempt, string verdict, string cycle, DateTime atUtc);

    /// <summary>
    /// Write the merch_order_id mapping before Telebirr is called, bound to the order's payment
    /// attempt (its <c>paymentSubmittedAt</c>, null for orders from before it was stamped).
    /// False when a mapping with that merch_order_id already exists.
    /// </summary>
    Task<bool> StoreMappingAsync(
        string merchOrderId, string orderId, string buyerId, decimal amount, string? paymentSubmittedAt);

    /// <summary>
    /// The mapping for a merch_order_id, or null when it is not an order checkout. Throws
    /// <see cref="InvalidDataException"/> for an order checkout row that lacks its order, buyer
    /// or amount, so the payment is alarmed instead of taken for another kind of payment.
    /// </summary>
    Task<OrderCheckoutMapping?> GetMappingAsync(string merchOrderId);
}

/// <summary>Where an order checkout attempt stands.</summary>
public static class OrderCheckoutOutcome
{
    /// <summary>The slot is written; Telebirr has not answered yet.</summary>
    public const string Opening = "opening";

    /// <summary>Telebirr created the payment and the buyer has a checkout URL.</summary>
    public const string UrlIssued = "url_issued";

    /// <summary>Telebirr may or may not have created the payment (timeout or network error).</summary>
    public const string Unknown = "unknown";

    /// <summary>Telebirr refused to create the payment.</summary>
    public const string Failed = "failed";

    /// <summary>The webhook confirmed the payment and the order was funded.</summary>
    public const string Paid = "paid";
}

/// <summary>FUP-37: the queryOrder reconciliation's verdicts, stored on the slot (<c>paymentQueryVerdict</c>).</summary>
public static class OrderCheckoutQuery
{
    /// <summary>Every checkout of the payment attempt is closed or failed at Telebirr: no money was taken.</summary>
    public const string NotPaid = "not_paid";

    /// <summary>A paid checkout was recorded in the webhook inbox, which funds the order like a notification.</summary>
    public const string Paid = "paid";

    /// <summary>An answer decided nothing (alarmed); asked again on the next run.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>Still unresolved a day after the payment deadline: alarmed for a person, and the order may fail.</summary>
    public const string GaveUp = "gave_up";
}

/// <summary>The order's current checkout attempt.</summary>
public sealed record OrderCheckoutSlot
{
    public required string OrderId { get; init; }
    public required string BuyerId { get; init; }
    public required int Attempt { get; init; }
    public required string MerchOrderId { get; init; }
    public required decimal Amount { get; init; }
    public required string Outcome { get; init; }
    public required DateTime OpenedAtUtc { get; init; }
    public required DateTime OpenUntilUtc { get; init; }
    public string? PrepayId { get; init; }

    /// <summary>FUP-37: the reconciliation's verdict on this attempt (<see cref="OrderCheckoutQuery"/>), or null.</summary>
    public string? QueryVerdict { get; init; }

    /// <summary>FUP-37: the payment attempt (<c>paymentSubmittedAt</c>) <see cref="QueryVerdict"/> is about.</summary>
    public string? QueryCycle { get; init; }

    /// <summary>FUP-37: when <see cref="QueryVerdict"/> was written.</summary>
    public DateTime? QueriedAtUtc { get; init; }

    /// <summary>
    /// FUP-37: true when the reconciliation has settled payment attempt <paramref name="cycle"/> for
    /// good, so the order cron may fail an unpaid order: every checkout of the attempt was found not
    /// paid, a paid one was handed to the webhook inbox (which holds the order while it is pending), or
    /// the reconciliation gave up after alarming. Unresolved or no verdict: the order waits.
    /// </summary>
    public bool QuerySettles(string cycle)
        => QueryVerdict is OrderCheckoutQuery.NotPaid or OrderCheckoutQuery.Paid or OrderCheckoutQuery.GaveUp
           && string.Equals(QueryCycle ?? "", cycle, StringComparison.Ordinal);

    /// <summary>
    /// True while the attempt can still be paid: Telebirr has (or may have) a live payment
    /// for it and its checkout window has not ended.
    /// </summary>
    public bool IsOpen(DateTime nowUtc)
        => Outcome is OrderCheckoutOutcome.Opening or OrderCheckoutOutcome.UrlIssued or OrderCheckoutOutcome.Unknown
           && nowUtc < OpenUntilUtc;
}

/// <summary>
/// What the webhook needs to bind a payment to its order. <see cref="PaymentSubmittedAt"/> is
/// the order's payment attempt when the checkout was opened; a payment only funds the order
/// while the order is still in that attempt. <see cref="Status"/> (FUP-37) is the row's own status:
/// <c>processing</c>, <c>failed</c> (Telebirr refused the preOrder: nothing exists there) or
/// <c>completed</c>.
/// </summary>
public sealed record OrderCheckoutMapping(
    string MerchOrderId, string OrderId, string BuyerId, decimal Amount, string? PaymentSubmittedAt = null,
    string? Status = null);
