using System.Globalization;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ACC-02: the fixed values and the blocker rules of account deletion (account-deletion.md
/// "Blocked While Active"). Pure functions over stored rows, so every rule is tested on its own.
/// Any status the contract does not list blocks (fail closed).
/// </summary>
public static class AccountDeletionPolicy
{
    /// <summary>The display value written into a deleted user's copied name fields.</summary>
    public const string Sentinel = "Deleted user";

    public const string InProgress = "in_progress";
    public const string CognitoPending = "cognito_pending";
    public const string Completed = "completed";

    /// <summary>The <c>cancelReason</c> of an unpaid order the deletion cancels (step 8).</summary>
    public const string CancelReason = "account_deleted";

    /// <summary>The 409 lists at most this many blockers; <c>blockerCount</c> is the full count.</summary>
    public const int MaxListedBlockers = 20;

    public const string BlockerOrder = "order";
    public const string BlockerEscrow = "escrow";
    public const string BlockerDelivery = "delivery";
    public const string BlockerDispute = "dispute";
    public const string BlockerPaymentReview = "payment_review";

    /// <summary>The only attributes a users-row tombstone keeps.</summary>
    public static readonly IReadOnlySet<string> TombstoneAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "userId", "role", "deletionState", "deletionRequestedAt", "deletedAt", "createdAt", "updatedAt", "version"
    };

    private static readonly HashSet<string> BlockingOrderStatuses = new(StringComparer.Ordinal)
    {
        "payment_submitted", "funded", OrderCommitment.AwaitingTransportStatus, "accepted", "pickup_confirmed", "in_transit",
        "delivered", "disbursing"
    };

    private static readonly HashSet<string> TerminalOrderStatuses = new(StringComparer.Ordinal)
    {
        "settled", "cancelled", "rejected", "dispute_resolved"
    };

    private static readonly HashSet<string> TerminalEscrowStatuses = new(StringComparer.Ordinal)
    {
        "completed", "cancelled", "refunded"
    };

    /// <summary>
    /// Order statuses that can never have had a delivery (one is created only once the order is
    /// accepted), so the <c>ListingIndex</c> fallback skips them.
    /// </summary>
    private static readonly HashSet<string> OrderStatusesWithoutDelivery = new(StringComparer.Ordinal)
    {
        "created", "pending_payment", "payment_submitted", "payment_failed", "funded", "rejected"
    };

    /// <summary>True for a display name a live user may not take: the sentinel, trimmed, any case.</summary>
    public static bool IsSentinelName(string? name)
        => name != null && string.Equals(name.Trim(), Sentinel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The blocker type of an order for the user in <paramref name="role"/>, or null when it does not
    /// block. <paramref name="buyerPaymentEvidence"/>: the order has a checkout slot or a payment
    /// reference, which keeps a <c>payment_failed</c> order of its buyer blocking.
    /// </summary>
    public static string? OrderBlockerType(string status, string role, bool buyerPaymentEvidence)
    {
        if (BlockingOrderStatuses.Contains(status))
        {
            return BlockerOrder;
        }

        return status switch
        {
            "disputed" => BlockerDispute,
            "created" or "pending_payment" => null,
            // The buyer's phone is the refund contact for a late capture; for a farmer or trucker
            // the buyer's phone stays, so their deletion still cancels it.
            "payment_failed" => role == "buyer" && buyerPaymentEvidence ? BlockerOrder : null,
            _ when TerminalOrderStatuses.Contains(status) => null,
            _ => BlockerOrder
        };
    }

    /// <summary>True when the order status cannot change any more (a GSI copy of it is current).</summary>
    public static bool IsTerminalOrderStatus(string status) => TerminalOrderStatuses.Contains(status);

    /// <summary>True for the unpaid statuses step 8 cancels (when <see cref="OrderBlockerType"/> lets them through).</summary>
    public static bool IsUnpaidOrderStatus(string status)
        => status is "created" or "pending_payment" or "payment_failed";

    /// <summary>True when the order may have a delivery row (see <see cref="OrderStatusesWithoutDelivery"/>).</summary>
    public static bool MayHaveDelivery(string status) => !OrderStatusesWithoutDelivery.Contains(status);

    /// <summary>A payment reference on the order: <c>cbeTransactionId</c> or any <c>telebirr*</c> field.</summary>
    public static bool HasPaymentReference(Dictionary<string, object> order)
        => order.Any(kv => (kv.Key == "cbeTransactionId" || kv.Key.StartsWith("telebirr", StringComparison.Ordinal))
                           && !string.IsNullOrWhiteSpace(kv.Value?.ToString()));

    /// <summary>The blocker type of an escrow for the user in <paramref name="role"/>, or null.</summary>
    public static string? EscrowBlockerType(Dictionary<string, object> escrow, string role)
    {
        // The trucker leg is still owed and pays out to the trucker's phone, whatever the status.
        if (role == "trucker" && IsTrue(escrow, "truckerPayoutPending"))
        {
            return BlockerEscrow;
        }

        var status = Text(escrow, "status");
        if (status == "disputed")
        {
            return BlockerDispute;
        }

        return TerminalEscrowStatuses.Contains(status) ? null : BlockerEscrow;
    }

    /// <summary>The blocker type of a delivery, or null (soft-deleted, delivered or cancelled).</summary>
    public static string? DeliveryBlockerType(Dictionary<string, object> delivery)
    {
        if (!string.IsNullOrEmpty(Text(delivery, "deletedAt")))
        {
            return null;
        }

        return Text(delivery, "status") switch
        {
            "disputed" => BlockerDispute,
            "delivered" or "cancelled" => null,
            _ => BlockerDelivery
        };
    }

    /// <summary>The copied name field of a party role on orders, deliveries and escrows.</summary>
    public static string NameField(string role) => role + "Name";

    /// <summary>The copied phone field of a party role on deliveries.</summary>
    public static string PhoneField(string role) => role + "Phone";

    internal static string Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    internal static int? Number(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value)
           && int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static bool IsTrue(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value)
           && (value is true || string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
}
