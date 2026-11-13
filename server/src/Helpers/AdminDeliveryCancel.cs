namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-19 (spec E9, delivery-verification-api.md 2.10.0 "Admin cancel after pickup"): an admin closes a delivery
/// that was picked up and never reached the buyer, once the money behind it is decided. The rules are pure functions
/// of the rows read; <c>AdminFunction</c> does the reads and the one transaction.
/// <para>
/// The cancel moves the delivery only: <c>pickedUp</c> or <c>inTransit</c> to <c>cancelled</c> with
/// <c>cancelReason: admin_cancelled</c>. It moves no money, no order status, no trip booking and no listing stock,
/// so it is refused while any escrow of the order or of the delivery still holds or governs money. The money is
/// decided first through the paths that exist: a dispute verdict with a disposition (which closes the delivery
/// itself) or a refund. Then the delivery is closed so that no handoff, check-in or drop-off code can act on it.
/// </para>
/// </summary>
public static class AdminDeliveryCancel
{
    /// <summary>The <c>{action}</c> of <c>POST /admin/deliveries/{id}/{action}</c>.</summary>
    public const string Action = "cancel";

    /// <summary>The <c>cancelReason</c> written on the delivery (the parties' apps ignore it; the status says cancelled).</summary>
    public const string CancelReason = "admin_cancelled";

    /// <summary>The transaction-audit action (the admin Audit Log and the transaction timeline).</summary>
    public const string AuditAction = "admin_delivery_cancelled";

    /// <summary>The log token of an applied cancel.</summary>
    public const string LogToken = "DELIVERY_ADMIN_CANCELLED";

    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    /// <summary>400: a field of the request is missing or out of range (<c>field</c> names it).</summary>
    public const string InvalidCode = "DELIVERY_ADMIN_CANCEL_INVALID";

    /// <summary>409: the delivery or its order is not one this cancel is for (the message says why).</summary>
    public const string NotAllowedCode = "DELIVERY_ADMIN_CANCEL_NOT_ALLOWED";

    /// <summary>409: an escrow of the order or the delivery still holds or governs money; nothing was written.</summary>
    public const string MoneyHeldCode = "DELIVERY_ADMIN_CANCEL_MONEY_HELD";

    /// <summary>409: the delivery, its order or an escrow changed since the read; nothing was written.</summary>
    public const string ChangedCode = "DELIVERY_ADMIN_CANCEL_CHANGED";

    /// <summary>The goods left the farm and never reached the buyer.</summary>
    private static readonly HashSet<string> CancellableStatuses = new(StringComparer.Ordinal) { "pickedUp", "inTransit" };

    /// <summary>
    /// Order statuses whose goods may still be on the road or were taken back: logistics before delivery, a verdict
    /// recorded over goods that were moving, or an order closed with its refund. Anything else is refused: an order
    /// before its pickup has no picked-up delivery, and a delivered, disputed, disbursing or settled order says the
    /// goods arrived. An order still <c>accepted</c> lags its picked-up delivery: refused, because readers of an
    /// accepted order (the trucker drop-out, the cancelled trip's transport failure, the pickup judgement) would treat
    /// the cancelled delivery as never picked up, and nothing advances the order after the cancel.
    /// </summary>
    private static readonly HashSet<string> OrderStatusesAllowed = new(StringComparer.Ordinal)
    {
        "pickup_confirmed", "in_transit", "dispute_resolved", "cancelled", "rejected"
    };

    /// <summary>Escrow statuses that hold and govern no money any more (escrow-protocol.md, Status Types).</summary>
    private static readonly HashSet<string> SettledEscrowStatuses = new(StringComparer.Ordinal)
    {
        "completed", "cancelled", "refunded"
    };

    /// <summary>A delivery an admin cancelled: the order keeps it in its delivery slot (no new delivery).</summary>
    public static bool IsAdminCancelled(string? status, string? cancelReason)
        => status == "cancelled" && cancelReason == CancelReason;

    /// <summary>The same request again: the delivery is the one this admin cancel wrote from the version the page read.</summary>
    public static bool IsReplay(IReadOnlyDictionary<string, object> delivery, int expectedVersion)
        => IsAdminCancelled(Str(delivery, "status"), Str(delivery, "cancelReason")) && Int(delivery, "version") == expectedVersion + 1;

    /// <summary>Why this delivery cannot be cancelled by an admin (the 409's message), or null when it can.</summary>
    public static string? DeliveryRefusalOf(IReadOnlyDictionary<string, object> delivery)
    {
        var status = Str(delivery, "status");
        return status switch
        {
            _ when CancellableStatuses.Contains(status) => null,
            "pending" => "The delivery was not picked up yet: any party can cancel it before pickup, and a refund of the order cancels it.",
            "delivered" or "disputed" =>
                $"The buyer confirmed this handoff (the delivery is {status}): settle it through the dispute verdict, not a cancel.",
            "cancelled" => "The delivery is already cancelled.",
            _ => $"The delivery is {status}: only a delivery picked up and not delivered can be cancelled."
        };
    }

    /// <summary>
    /// Why the delivery's order does not allow the cancel (the 409's message), or null when it does. A delivery
    /// without an <c>orderId</c> (before 1.2.0) has no order to check; one whose order row is missing is refused.
    /// </summary>
    public static string? OrderRefusalOf(string? orderId, IReadOnlyDictionary<string, object>? order)
    {
        if (string.IsNullOrEmpty(orderId))
        {
            return null;
        }

        if (order == null)
        {
            return "The delivery's order cannot be found, so its money cannot be checked: nothing was written.";
        }

        var status = Str(order, "status");
        if (status == "accepted")
        {
            return "The order is accepted although its delivery was picked up: the order must catch up with its delivery "
                   + "first, or the cancelled delivery would read as never picked up. Nothing was written.";
        }

        return OrderStatusesAllowed.Contains(status)
            ? null
            : $"The order is {status}: only an order whose goods were picked up and never delivered can have its delivery cancelled.";
    }

    /// <summary>
    /// The first escrow (of the order or of the delivery) that still holds or governs money, or null when every one is
    /// settled or there is none. Read strongly consistent by the caller.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? FirstHeldEscrow(IEnumerable<IReadOnlyDictionary<string, object>> escrows)
        => escrows.FirstOrDefault(e => !SettledEscrowStatuses.Contains(Str(e, "status")));

    /// <summary>The 409's message for an escrow that still holds or governs money.</summary>
    public static string MoneyHeldMessage(IReadOnlyDictionary<string, object> escrow)
        => $"The payment is not decided yet (escrow {Str(escrow, "escrowId")} is {Str(escrow, "status")}). Decide the money first: "
           + "a dispute verdict with a disposition (which cancels the delivery itself) or the refund. Nothing was written.";

    /// <summary>The audit row's metadata: what was checked, then the reason (last, as it may hold any character).</summary>
    public static string AuditMetadata(string? orderStatus, IEnumerable<IReadOnlyDictionary<string, object>> escrows, string reason)
    {
        var money = string.Join(",", escrows.Select(e => $"{Str(e, "escrowId")}:{Str(e, "status")}"));
        return $"orderStatus={(string.IsNullOrEmpty(orderStatus) ? "none" : orderStatus)};escrows={(money.Length == 0 ? "none" : money)};reason={reason}";
    }

    private static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static int Int(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
}
