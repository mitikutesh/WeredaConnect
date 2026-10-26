using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-48: when the parties of an order may reach each other (anti-bypass, order-lifecycle.md
/// 1.18.0). Contact opens once the farmer accepted the paid order and closes again when the money
/// goes back to the buyer: a cancel, a reject, a dispute verdict, or an admin refund that left the
/// order in its status (the override flags). Before that, a buyer and a farmer or trucker could
/// agree to trade off the platform and skip the commission. ESC-15 makes the contact per party: the
/// buyer and the farmer may talk from the farmer's accept on (<c>funded</c> with the farmer's
/// response <c>accepted</c>, or any later status), while the trucker joins only once they committed
/// to the transport and drops out again when the transport fails (<c>awaiting_transport</c>). The
/// same rule gates the phone numbers on order reads and new messages on order and delivery threads.
/// </summary>
public static class ContactReveal
{
    /// <summary>
    /// Order statuses (wire form) at which the money is held and has not gone back, so contact may be
    /// open: in <c>funded</c> and <c>awaiting_transport</c> the farmer's response decides; from
    /// <c>accepted</c> on the farmer has accepted. An override flag closes any of them.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "funded", OrderCommitment.AwaitingTransportStatus,
        "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing", "settled", "disputed"
    };

    /// <summary>Order statuses at which contact is always closed: before the money was held, or it went back.</summary>
    public static readonly IReadOnlySet<string> ClosedOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "created", "pending_payment", "payment_submitted", "payment_failed",
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
    public const string TransportCancelSignal = "CONTACT_THEN_TRANSPORT_CANCEL";

    /// <summary>
    /// Whether the buyer and the farmer of this order row may see each other's phone and message each
    /// other: the money is held, the farmer accepted (a row from before ESC-15 counts as accepted from
    /// <c>accepted</c> on and as pending while <c>funded</c>) and no admin refund flag is set.
    /// </summary>
    public static bool IsOpen(IReadOnlyDictionary<string, object> order)
    {
        var status = order.TryGetValue("status", out var s) ? s?.ToString() : null;
        return status != null
               && OpenOrderStatuses.Contains(status)
               && (!FarmerDecides(status)
                   || OrderCommitment.FarmerResponseOf(order) == OrderCommitment.Response.Accepted)
               && !HasFlag(order, OrderMoneyFields.RefundOverrideAt)
               && !HasFlag(order, OrderMoneyFields.DisputeOverrideAt);
    }

    /// <summary>
    /// Whether the trucker of this order row is in the contact too: the trucker committed to the
    /// transport (a trip order from before ESC-15 counts as committed from <c>accepted</c> on) and
    /// <see cref="IsOpen"/> holds. False after the trucker declined, expired or dropped out, and on a
    /// self-pickup order.
    /// </summary>
    public static bool IsTruckerOpen(IReadOnlyDictionary<string, object> order)
        => OrderCommitment.TruckerResponseOf(order) == OrderCommitment.Response.Accepted && IsOpen(order);

    /// <summary>
    /// Whether <paramref name="userId"/> may reach the other parties of this order: the buyer and the
    /// farmer follow <see cref="IsOpen"/>, the order's trucker <see cref="IsTruckerOpen"/>, anyone else
    /// never.
    /// </summary>
    public static bool IsOpenFor(IReadOnlyDictionary<string, object> order, string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (userId == Id(order, "buyerId") || userId == Id(order, "farmerId"))
        {
            return IsOpen(order);
        }

        return userId == Id(order, "truckerId") && IsTruckerOpen(order);
    }

    /// <summary>Statuses at which the farmer may still be deciding (ESC-15), so the farmer's response gates the contact.</summary>
    private static bool FarmerDecides(string status) => status is "funded" or OrderCommitment.AwaitingTransportStatus;

    private static string? Id(IReadOnlyDictionary<string, object> order, string key)
        => order.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool HasFlag(IReadOnlyDictionary<string, object> order, string key)
        => order.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value?.ToString());
}
