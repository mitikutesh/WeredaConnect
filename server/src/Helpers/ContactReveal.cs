using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-48: when the parties of an order may reach each other (anti-bypass, order-lifecycle.md
/// 1.18.0). Contact opens once the farmer accepted the paid order and closes again when the money
/// goes back to the buyer: a cancel, a reject, a dispute verdict, or an admin refund that left the
/// order in its status (the override flags). Before that, a buyer and a farmer or trucker could
/// agree to trade off the platform and skip the commission. The same rule gates the phone numbers
/// on order reads and new messages on order and delivery threads.
/// </summary>
public static class ContactReveal
{
    /// <summary>Order statuses (wire form) at which contact is open, unless an override flag is set.</summary>
    public static readonly IReadOnlySet<string> OpenOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing", "settled", "disputed"
    };

    /// <summary>Order statuses at which contact is closed: before the farmer accepted, or the money went back.</summary>
    public static readonly IReadOnlySet<string> ClosedOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "created", "pending_payment", "payment_submitted", "payment_failed", "funded",
        "cancelled", "rejected", "dispute_resolved"
    };

    /// <summary>Message refusal prefix for a context whose contact is not open (sync-protocol.md 1.15.0).</summary>
    public const string LockedCode = "MESSAGE_CONTACT_LOCKED";

    public const string LockedMessage =
        LockedCode + ": Contact opens after payment is held in escrow and the farmer accepts the order.";

    /// <summary>
    /// Fraud signal written when a buyer cancels after a transport failure an order whose contact
    /// had opened (the parties could have met and agreed to trade off the platform).
    /// </summary>
    public const string TransportCancelSignal = "contact_then_transport_cancel";

    /// <summary>Whether the parties of this order row may see each other's phone and message each other.</summary>
    public static bool IsOpen(IReadOnlyDictionary<string, object> order)
    {
        var status = order.TryGetValue("status", out var s) ? s?.ToString() : null;
        return status != null
               && OpenOrderStatuses.Contains(status)
               && !HasFlag(order, OrderMoneyFields.RefundOverrideAt)
               && !HasFlag(order, OrderMoneyFields.DisputeOverrideAt);
    }

    private static bool HasFlag(IReadOnlyDictionary<string, object> order, string key)
        => order.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value?.ToString());
}
