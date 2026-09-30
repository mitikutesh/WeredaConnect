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
/// while the order is still in that attempt.
/// </summary>
public sealed record OrderCheckoutMapping(
    string MerchOrderId, string OrderId, string BuyerId, decimal Amount, string? PaymentSubmittedAt = null);
