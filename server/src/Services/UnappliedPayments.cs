using System.Globalization;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-02: which captured Telebirr payments of an order still wait for a manual refund. The
/// webhook records a payment it did not apply as an escrow-audit row under
/// <c>escrow-order-{orderId}</c> (<see cref="Unmatched"/>, <see cref="Duplicate"/>,
/// <see cref="Unprocessed"/>); an admin resolves it with an <see cref="RefundedManual"/> row
/// under the same key. This is the one rule for that question: the admin queue and the record
/// call use it, and the account-deletion <c>payment_review</c> blocker (ACC-02) must use it too.
/// Contract: escrow-protocol.md "Additions in 1.20.0", account-deletion.md 1.1.0.
/// </summary>
/// <remarks>
/// <para>
/// A payment is identified by its Telebirr payment_order_id, the <c>telebirrRef</c> of its rows.
/// Rows the webhook wrote without one (a <c>Completed</c> notice with no payment_order_id, an
/// inbox row given up before its body was read) are identified by the checkout's merch_order_id
/// in their metadata. A refund row resolves the payment whose payment_order_id it names, and a
/// nameless payment whose merch_order_id it names, whenever it was written.
/// </para>
/// <para>
/// A payment that funded the order (its payment_order_id, or for a nameless one its
/// merch_order_id, is on the order) is the order's own money in its escrow, never a refund case.
/// </para>
/// </remarks>
public static class UnappliedPayments
{
    public const string Unmatched = "order_payment_unmatched";
    public const string Duplicate = "order_payment_duplicate";
    public const string Unprocessed = "order_payment_unprocessed";
    public const string RefundedManual = "order_payment_refunded_manual";

    /// <summary>Written by the webhook just before an <c>unmatched</c> row with <c>reason=amount</c>.</summary>
    public const string AmountMismatch = "webhook_amount_mismatch";

    public const string OrderEscrowPrefix = "escrow-order-";

    /// <summary>The escrow-audit actions of a captured payment that was not applied.</summary>
    public static readonly IReadOnlyList<string> UnappliedActions = new[] { Unmatched, Duplicate, Unprocessed };

    public static string EscrowKey(string orderId) => OrderEscrowPrefix + orderId;

    /// <summary>
    /// Every captured payment of one order that the webhook did not apply, with its manual refund
    /// row if one exists. <paramref name="trail"/> is the whole escrow-audit trail of
    /// <c>escrow-order-{orderId}</c>; the two funded ids are the order's
    /// <c>telebirrPaymentOrderId</c> and <c>telebirrMerchOrderId</c> (null when the order is
    /// missing or unfunded).
    /// </summary>
    public static List<UnappliedPayment> Evaluate(
        IReadOnlyList<Dictionary<string, object>> trail,
        string? fundedPaymentOrderId,
        string? fundedMerchOrderId)
    {
        var rows = trail
            .OrderBy(r => Get(r, "timestamp"), StringComparer.Ordinal)
            .ToList();
        var refunds = rows
            .Where(r => Get(r, "action") == RefundedManual)
            .Select(ToRefund)
            .ToList();

        // One group per payment, in the order its first row was written.
        var groups = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var row in rows)
        {
            if (!UnappliedActions.Contains(Get(row, "action")))
            {
                continue;
            }

            var key = IdentityKey(row);
            if (!groups.TryGetValue(key, out var groupRows))
            {
                groupRows = new List<Dictionary<string, object>>();
                groups[key] = groupRows;
                groupOrder.Add(key);
            }

            groupRows.Add(row);
        }

        var result = new List<UnappliedPayment>(groupOrder.Count);
        foreach (var groupRows in groupOrder.Select(key => groups[key]))
        {
            var paymentOrderId = NullIfEmpty(Get(groupRows[0], "telebirrRef"));
            var merchOrderId = groupRows
                .Select(r => NullIfEmpty(Meta(r).GetValueOrDefault("merchOrderId")))
                .FirstOrDefault(m => m != null);

            var refund = paymentOrderId != null
                ? refunds.FirstOrDefault(r => r.PaymentOrderId == paymentOrderId)
                : merchOrderId != null
                    ? refunds.FirstOrDefault(r => r.MerchOrderId == merchOrderId)
                    : null;

            var funded = paymentOrderId != null
                ? paymentOrderId == NullIfEmpty(fundedPaymentOrderId)
                : merchOrderId != null && merchOrderId == NullIfEmpty(fundedMerchOrderId);

            result.Add(new UnappliedPayment
            {
                PaymentOrderId = paymentOrderId,
                MerchOrderId = merchOrderId,
                Actions = groupRows.Select(r => Get(r, "action")).Distinct().ToList(),
                Reasons = groupRows
                    .Select(r => Meta(r).GetValueOrDefault("reason") ?? "")
                    .Where(r => r.Length > 0)
                    .Distinct()
                    .ToList(),
                FirstSeenAt = Get(groupRows[0], "timestamp"),
                LastSeenAt = Get(groupRows[^1], "timestamp"),
                CapturedAmount = CapturedAmount(groupRows, rows),
                // Only a named payment can be final: the inbox keeps a verdict per payment_order_id,
                // so a later notice for the same checkout that carries one is a new inbox row and
                // may still fund the order.
                HasFinalVerdict = paymentOrderId != null
                                  && groupRows.Any(r => Get(r, "action") is Unmatched or Duplicate),
                FundedOrder = funded,
                Refund = refund
            });
        }

        return result;
    }

    /// <summary>True when the order has a captured payment that still waits for a manual refund.</summary>
    public static bool HasOpen(
        IReadOnlyList<Dictionary<string, object>> trail, string? fundedPaymentOrderId, string? fundedMerchOrderId)
        => Evaluate(trail, fundedPaymentOrderId, fundedMerchOrderId).Any(p => p.IsOpen);

    /// <summary>
    /// The refund row's metadata: the reserved <c>paymentOrderId=..;amount=..</c>, then
    /// <c>;merchOrderId=..</c> when the payment's checkout is known.
    /// </summary>
    public static string RefundMetadata(string paymentOrderId, decimal amount, string? merchOrderId)
        => $"paymentOrderId={paymentOrderId};amount={FormatAmount(amount)}"
           + (string.IsNullOrEmpty(merchOrderId) ? "" : $";merchOrderId={merchOrderId}");

    /// <summary>An amount as it is written in audit metadata: invariant, at most two decimals.</summary>
    public static string FormatAmount(decimal amount)
        => Round(amount).ToString("0.##", CultureInfo.InvariantCulture);

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What was captured, as far as the rows say. The rows carry the checkout amount, which is
    /// what Telebirr charges; but for <c>reason=amount</c> the notice said otherwise, and its
    /// amount is on the <c>webhook_amount_mismatch</c> row written just before. Null when the rows
    /// do not tell (a <c>currency</c> refusal, a give-up row with no amount, conflicting values).
    /// </summary>
    private static decimal? CapturedAmount(
        List<Dictionary<string, object>> groupRows, List<Dictionary<string, object>> allRows)
    {
        var fromNotice = new HashSet<decimal>();
        var fromCheckout = new HashSet<decimal>();
        foreach (var row in groupRows)
        {
            var meta = Meta(row);
            var reason = meta.GetValueOrDefault("reason");
            if (reason == "currency")
            {
                return null;
            }

            if (Get(row, "action") == Unmatched && reason == "amount")
            {
                var timestamp = Get(row, "timestamp");
                var notice = allRows.LastOrDefault(r =>
                    Get(r, "action") == AmountMismatch
                    && string.CompareOrdinal(Get(r, "timestamp"), timestamp) <= 0);
                var noticed = notice == null ? null : ParseAmount(Meta(notice).GetValueOrDefault("webhook"));
                if (noticed == null)
                {
                    return null;
                }

                fromNotice.Add(noticed.Value);
                continue;
            }

            if (ParseAmount(meta.GetValueOrDefault("amount")) is { } checkout)
            {
                fromCheckout.Add(checkout);
            }
        }

        var known = fromNotice.Count > 0 ? fromNotice : fromCheckout;
        return known.Count == 1 ? known.Single() : null;
    }

    private static string IdentityKey(Dictionary<string, object> row)
    {
        var paymentOrderId = NullIfEmpty(Get(row, "telebirrRef"));
        if (paymentOrderId != null)
        {
            return "p:" + paymentOrderId;
        }

        var merchOrderId = NullIfEmpty(Meta(row).GetValueOrDefault("merchOrderId"));
        // A row with neither id cannot be matched to a refund; the current webhook always
        // writes the merch_order_id.
        return merchOrderId != null ? "m:" + merchOrderId : "row:" + Get(row, "timestamp");
    }

    private static ManualRefundRecord ToRefund(Dictionary<string, object> row)
    {
        var meta = Meta(row);
        return new ManualRefundRecord
        {
            PaymentOrderId = NullIfEmpty(meta.GetValueOrDefault("paymentOrderId")),
            MerchOrderId = NullIfEmpty(meta.GetValueOrDefault("merchOrderId")),
            Amount = ParseAmount(meta.GetValueOrDefault("amount")),
            TelebirrRef = Get(row, "telebirrRef"),
            RecordedBy = Get(row, "actor"),
            RecordedAt = Get(row, "timestamp")
        };
    }

    /// <summary>
    /// <c>key=value</c> pairs of an audit row's metadata. The webhook separates them with
    /// <c>,</c>, the refund row with <c>;</c>; no value written there contains either.
    /// </summary>
    private static Dictionary<string, string> Meta(Dictionary<string, object> row)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in Get(row, "metadata").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                result.TryAdd(part[..eq].Trim(), part[(eq + 1)..].Trim());
            }
        }

        return result;
    }

    private static decimal? ParseAmount(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0
            ? Round(amount)
            : null;

    private static string Get(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>FUP-02: one captured payment of an order that the webhook did not apply.</summary>
public sealed record UnappliedPayment
{
    /// <summary>Telebirr payment_order_id; null when the webhook never learned it.</summary>
    public string? PaymentOrderId { get; init; }

    /// <summary>The checkout's merch_order_id, the portal search key of a nameless payment.</summary>
    public string? MerchOrderId { get; init; }

    public required List<string> Actions { get; init; }
    public required List<string> Reasons { get; init; }
    public required string FirstSeenAt { get; init; }
    public required string LastSeenAt { get; init; }

    /// <summary>What Telebirr captured, when the rows tell; null when only the portal knows.</summary>
    public decimal? CapturedAmount { get; init; }

    /// <summary>
    /// The webhook decided this payment for good: a named payment with an <c>unmatched</c> or
    /// <c>duplicate</c> row. With only <c>unprocessed</c> rows a redelivery or the inbox replay may
    /// still apply it, and a nameless payment may still arrive again as a notice that names it.
    /// </summary>
    public required bool HasFinalVerdict { get; init; }

    /// <summary>The payment funded the order: its money is in the order's escrow.</summary>
    public required bool FundedOrder { get; init; }

    public ManualRefundRecord? Refund { get; init; }

    /// <summary>Still waits for a manual refund.</summary>
    public bool IsOpen => Refund == null && !FundedOrder;
}

/// <summary>FUP-02: an <c>order_payment_refunded_manual</c> row.</summary>
public sealed record ManualRefundRecord
{
    public string? PaymentOrderId { get; init; }
    public string? MerchOrderId { get; init; }
    public decimal? Amount { get; init; }

    /// <summary>The merchant-portal transaction id of the refund.</summary>
    public required string TelebirrRef { get; init; }
    public required string RecordedBy { get; init; }
    public required string RecordedAt { get; init; }
}
