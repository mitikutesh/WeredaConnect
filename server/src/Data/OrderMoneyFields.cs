namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-09: server-only order attributes of the order-follows-the-money mirror
/// (escrow-protocol.md 1.15.0, order-lifecycle.md 1.17.0). None of them is in a DTO, a client
/// may set none of them, and none is written with a <c>version</c> or <c>updatedAt</c> bump.
/// </summary>
internal static class OrderMoneyFields
{
    /// <summary>
    /// The order status at which the hourly money sweep found nothing more to do for this order (a
    /// mismatch it alarmed once, a flagged override, no escrow at all). The sweep's page query skips
    /// the row while its status is still this one, so it comes back by itself once the status moves.
    /// </summary>
    public const string SweepSkip = "moneySweepSkip";

    /// <summary>ISO time the skip was written.</summary>
    public const string SweepSkipAt = "moneySweepSkipAt";

    /// <summary>Part B: an admin refunded the escrow over an order that could not follow (goods moving).</summary>
    public const string RefundOverrideAt = "refundOverrideAt";
    public const string RefundOverrideBy = "refundOverrideBy";
    public const string RefundOverrideReason = "refundOverrideReason";
    public const string RefundOverrideEscrowId = "refundOverrideEscrowId";

    /// <summary>ESC-03: an admin refunded a buyer_wins verdict over an order mismatch.</summary>
    public const string DisputeOverrideAt = "disputeOverrideAt";
}
