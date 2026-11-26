using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// The negotiated commission of one order (order-lifecycle.md 2.26.0, "Admin commission change"): before the buyer
/// pays, an admin may agree another commission rate for one order. The rules are pure functions of the order as read;
/// <c>AdminFunction</c> does the read and the one guarded write.
/// <para>
/// Only an order with no money yet (<c>created</c> or <c>pending_payment</c>, no verified payment, no payment hold
/// from the webhook) takes another rate. The crop cost and the transport cost never change: the commission is
/// <c>round2(cropCost x rate)</c> and the total is <c>cropCost + transportCost + commission</c>, as at create. A
/// checkout is only opened from <c>payment_submitted</c> and reads the total then, so the buyer pays the new total;
/// a payment for an older total is refused by the webhook (<c>order_changed</c>) into the unapplied payments queue.
/// </para>
/// </summary>
public static class OrderCommissionChange
{
    /// <summary>The <c>{action}</c> of <c>POST /admin/orders/{orderId}/{action}</c>.</summary>
    public const string Action = "commission";

    /// <summary>
    /// Server-only order map: the last change (<see cref="StampAt"/>, <see cref="StampFromRate"/>,
    /// <see cref="StampToRate"/>, <see cref="StampFromTotal"/>, <see cref="StampFromVersion"/>). No admin id and no
    /// reason: those are in the audit trail only.
    /// </summary>
    public const string Field = "commissionChange";

    public const string StampAt = "at";
    public const string StampFromRate = "fromRate";
    public const string StampToRate = "toRate";
    public const string StampFromTotal = "fromTotal";
    public const string StampFromVersion = "fromVersion";

    /// <summary>The transaction-audit action of a change (the admin Audit Log and the transaction timeline).</summary>
    public const string AuditAction = "admin_commission_changed";

    /// <summary>The log token of an applied change.</summary>
    public const string LogToken = "COMMISSION_CHANGED";

    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    /// <summary>The highest rate an admin may agree: 20 %.</summary>
    public const decimal MaxRate = 0.20m;

    /// <summary>A rate has at most 4 decimals (0.01 % steps).</summary>
    public const int RateDecimals = 4;

    /// <summary>400: a field of the request is missing or out of range (<c>field</c> names it).</summary>
    public const string InvalidCode = "COMMISSION_CHANGE_INVALID";

    /// <summary>409: the order cannot take another commission now (the message says why).</summary>
    public const string NotAllowedCode = "COMMISSION_CHANGE_NOT_ALLOWED";

    /// <summary>409: the order changed since it was read; nothing was written.</summary>
    public const string ChangedCode = "COMMISSION_CHANGE_CHANGED";

    /// <summary>The order statuses before any money moved.</summary>
    public static readonly IReadOnlySet<string> EditableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "created", "pending_payment"
    };

    /// <summary>The order's money as the change leaves it.</summary>
    public readonly record struct Money(decimal CommissionAmount, decimal OrderTotal);

    /// <summary>Whether <paramref name="rate"/> is a rate an admin may set: 0 to 20 %, at most 4 decimals.</summary>
    public static bool IsValidRate(decimal rate)
        => rate >= 0m && rate <= MaxRate && decimal.Round(rate, RateDecimals) == rate;

    /// <summary>Whether <paramref name="reason"/> is 10 to 500 characters once trimmed.</summary>
    public static bool IsValidReason(string? reason)
        => (reason ?? "").Trim().Length is >= ReasonMinLength and <= ReasonMaxLength;

    /// <summary>ETB to the santim, halves away from zero, as the order create and the escrow round.</summary>
    public static decimal RoundEtb(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>The commission and total of an order at <paramref name="rate"/>; the crop and transport costs stay.</summary>
    public static Money Compute(decimal cropCost, decimal transportCost, decimal rate)
    {
        var commission = RoundEtb(RoundEtb(cropCost) * rate);
        return new Money(commission, RoundEtb(cropCost) + RoundEtb(transportCost) + commission);
    }

    /// <summary>
    /// Why the order cannot take another commission now, or null. <paramref name="holdUntil"/> is the webhook's
    /// <c>paymentHoldUntil</c> as read (a verified payment of the order waits in the webhook inbox).
    /// </summary>
    public static string? RefusalOf(string status, string? paymentVerifiedAt, string? holdUntil, decimal cropCost, DateTime nowUtc)
    {
        if (!EditableStatuses.Contains(status))
        {
            return $"Only an unpaid order (created or pending_payment) can get another commission; this one is {status}.";
        }

        if (!string.IsNullOrEmpty(paymentVerifiedAt))
        {
            return "This order's payment is already verified.";
        }

        if (HoldsPayment(holdUntil, nowUtc))
        {
            return "A payment for this order is on its way. Wait until it is settled, then reload.";
        }

        return cropCost > 0m ? null : "This order has no crop cost to take a commission from.";
    }

    /// <summary>Whether the webhook's payment hold is still in the future.</summary>
    public static bool HoldsPayment(string? holdUntil, DateTime nowUtc)
        => DateTime.TryParse(holdUntil, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
           && until > nowUtc;

    /// <summary>The stamp of a change from <paramref name="fromVersion"/>.</summary>
    public static Dictionary<string, object> Stamp(decimal fromRate, decimal toRate, decimal fromTotal, int fromVersion, string at)
        => new()
        {
            { StampAt, at },
            { StampFromRate, fromRate },
            { StampToRate, toRate },
            { StampFromTotal, fromTotal },
            { StampFromVersion, fromVersion }
        };

    /// <summary>The same change (to the same rate from the same version) is already on the order.</summary>
    public static bool IsReplay(IReadOnlyDictionary<string, object> order, int expectedVersion, decimal rate)
        => StampOf(order) is { } stamp
           && Number(stamp, StampFromVersion) == expectedVersion
           && Number(stamp, StampToRate) == rate;

    /// <summary>The rate and total before the last change, or null without one.</summary>
    public static (decimal Rate, decimal Total)? BeforeOf(IReadOnlyDictionary<string, object> order)
        => StampOf(order) is { } stamp && Number(stamp, StampFromRate) is { } rate && Number(stamp, StampFromTotal) is { } total
            ? (rate, total)
            : null;

    private static IReadOnlyDictionary<string, object>? StampOf(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue(Field, out var raw) && raw is IReadOnlyDictionary<string, object> stamp ? stamp : null;

    private static decimal? Number(IReadOnlyDictionary<string, object> map, string key)
        => map.TryGetValue(key, out var raw) && raw != null
           && decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number | NumberStyles.AllowExponent,
               CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
