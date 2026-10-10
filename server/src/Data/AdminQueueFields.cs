namespace WeredaConnect.Server.Data;

/// <summary>
/// Attribute names and reasons of the admin stuck-order queue (ESC-06). The queue lives on the
/// order row: an order whose money an admin must decide on is marked <c>adminQueue = open</c>
/// (the partition key of the sparse <c>AdminQueueIndex</c> on the orders table) and sorted by
/// <c>adminQueuedAt</c>, oldest first, the shape of the manual payout queue. The marker is written
/// and removed without bumping the order version, so a buyer's queued change never conflicts
/// with it. ESC-16 adds more reasons to the same index.
/// </summary>
internal static class AdminQueueFields
{
    public const string IndexName = "AdminQueueIndex";

    /// <summary>"open" while an admin must decide; REMOVEd when the escrow shows the money moved on.</summary>
    public const string Marker = "adminQueue";
    public const string MarkerOpen = "open";

    /// <summary>ISO time the order entered the queue; sort key of the index (oldest first).</summary>
    public const string QueuedAt = "adminQueuedAt";

    public const string Reason = "adminQueueReason";

    /// <summary>The escrow whose money the entry is about.</summary>
    public const string EscrowId = "adminQueueEscrowId";

    /// <summary>
    /// The order escrow was held 21 days after funding and its order is still not done; the
    /// escrow sweep no longer extends it (escrow-protocol.md 1.13.0).
    /// </summary>
    public const string ReasonHoldCap = "hold_cap";

    /// <summary>A capped order escrow names an order that does not exist; logged only (no row to mark).</summary>
    public const string ReasonOrderMissing = "order_missing";

    /// <summary>Escrow attribute stamped last when its order entered the queue at the hold cap.</summary>
    public const string HoldCapReachedAt = "holdCapReachedAt";

    public static readonly string[] All = { Marker, QueuedAt, Reason, EscrowId };
}
