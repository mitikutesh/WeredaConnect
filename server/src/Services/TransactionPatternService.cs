using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-09 (fraud-detection-architecture.md section 15.1): the bounded history reads behind the
/// transaction pattern rules of <see cref="TransactionPatternRules"/>. Each check reads one user's
/// newest rows on an existing GSI (orders by buyer, farmer or trucker; escrows by buyer), at most
/// <c>FRAUD_PATTERN_MAX_PAGES</c> pages of <see cref="TransactionPatternSettings.PageSize"/> rows, and stops at
/// the first row older than <c>FRAUD_PATTERN_WINDOW_DAYS</c>. No Scan. A check returns the hits; the caller
/// writes them as signals on the subject, best effort, after the business write it observes succeeded.
/// A check may throw on a failed read: the caller logs it and writes nothing.
/// </summary>
internal sealed class TransactionPatternService
{
    /// <summary>The party a cancel-after-fund check is about.</summary>
    internal const string Farmer = OrderCommitment.Party.Farmer;

    /// <summary>The party a cancel-after-fund check is about.</summary>
    internal const string Trucker = OrderCommitment.Party.Trucker;

    /// <summary>Escrow statuses that mean the money was held at some point (the dispute rate's base).</summary>
    private static readonly HashSet<string> HeldEscrowStatuses = new(StringComparer.Ordinal)
    {
        "escrowed", "released", "completed", "disputed", "dispute_resolved", "held", "refunded"
    };

    private readonly IOrderRepository? _orders;
    private readonly IEscrowRepository? _escrows;
    private readonly TransactionPatternSettings? _settings;

    /// <param name="orders">Reads order history; without it the payment and cancel rules skip.</param>
    /// <param name="escrows">Reads escrow history; without it the dispute rate skips.</param>
    /// <param name="settings">Fixed settings for tests; null reads the environment on every check.</param>
    public TransactionPatternService(
        IOrderRepository? orders, IEscrowRepository? escrows, TransactionPatternSettings? settings = null)
    {
        _orders = orders;
        _escrows = escrows;
        _settings = settings;
    }

    internal TransactionPatternSettings Settings => _settings ?? TransactionPatternSettings.FromEnvironment();

    /// <summary>
    /// The three buyer payment rules on a submitted payment of <paramref name="orderId"/>: amount above 3
    /// sigma, new account with a large amount, repeated amounts just under the Fayda threshold. One read of
    /// the buyer's orders (the current order left out of the history); the account age comes from the
    /// caller, which read the buyer's row for the KYC gate.
    /// </summary>
    public async Task<IReadOnlyList<PatternHit>> CheckPaymentAsync(
        string buyerId, string orderId, decimal amountEtb, string? accountCreatedAt, DateTime nowUtc)
    {
        var settings = Settings;
        var hits = new List<PatternHit>();
        if (amountEtb <= 0)
        {
            return hits;
        }

        var history = new List<PaymentSample>();
        if (_orders != null)
        {
            foreach (var order in await ReadRecentOrdersAsync(OrderCommitment.Party.Buyer, buyerId, nowUtc, settings))
            {
                if (Str(order, "orderId") == orderId || ReadDecimal(order, "orderTotal") is not { } amount || amount <= 0m)
                {
                    continue;
                }

                var verifiedAt = TransactionPatternRules.ParseUtc(Str(order, "paymentVerifiedAt"));
                var submittedAt = TransactionPatternRules.ParseUtc(Str(order, "paymentSubmittedAt"));
                if ((submittedAt ?? verifiedAt) is { } paidAt)
                {
                    history.Add(new PaymentSample(amount, paidAt, verifiedAt != null));
                }
            }
        }

        Add(hits, TransactionPatternRules.AmountAnomaly(amountEtb, history, settings));
        Add(hits, TransactionPatternRules.NewAccountLargeAmount(
            amountEtb, TransactionPatternRules.ParseUtc(accountCreatedAt), nowUtc, settings));
        Add(hits, TransactionPatternRules.KycThresholdStructuring(amountEtb, history, nowUtc, settings));
        return hits;
    }

    /// <summary>
    /// The cancel-after-fund rate of a farmer or a trucker who just ended the funded orders
    /// <paramref name="cancelledOrderIds"/> (counted as funded and cancelled whatever the lagging index says).
    /// Base: the party's orders in the window with <c>paymentVerifiedAt</c>. Counted: a farmer's own cancel
    /// (<c>cancelled</c>, <c>cancelledBy</c> and <c>faultParty</c> farmer; a decline before accepting is an
    /// answer, not a cancel), a trucker's drop-out or trip cancel (<c>truckerResponse: cancelled</c>).
    /// </summary>
    public async Task<IReadOnlyList<PatternHit>> CheckCancelAfterFundAsync(
        string userId, string party, IReadOnlyCollection<string> cancelledOrderIds, DateTime nowUtc)
    {
        var settings = Settings;
        if (_orders == null || cancelledOrderIds.Count == 0 || party is not (Farmer or Trucker))
        {
            return Array.Empty<PatternHit>();
        }

        var funded = cancelledOrderIds.Count;
        var cancelled = cancelledOrderIds.Count;
        foreach (var order in await ReadRecentOrdersAsync(party, userId, nowUtc, settings))
        {
            if (cancelledOrderIds.Contains(Str(order, "orderId") ?? "") || string.IsNullOrEmpty(Str(order, "paymentVerifiedAt")))
            {
                continue;
            }

            funded++;
            if (IsCancelAfterFundBy(order, party))
            {
                cancelled++;
            }
        }

        return One(TransactionPatternRules.CancelAfterFundRate(party, funded, cancelled, settings));
    }

    /// <summary>
    /// The dispute rate of a buyer who just disputed <paramref name="escrowId"/> (counted as held and
    /// disputed). Base: the buyer's escrows in the window that held money; counted: those with
    /// <c>disputedBy</c> the buyer (a farmer's dispute is not the buyer's).
    /// </summary>
    public async Task<IReadOnlyList<PatternHit>> CheckDisputeRateAsync(string buyerId, string escrowId, DateTime nowUtc)
    {
        var settings = Settings;
        if (_escrows == null)
        {
            return Array.Empty<PatternHit>();
        }

        var since = nowUtc.AddDays(-settings.WindowDays);
        var held = 1;
        var disputed = 1;
        Dictionary<string, AttributeValue>? start = null;
        for (var page = 0; page < settings.MaxPages; page++)
        {
            var (items, next) = await _escrows.QueryByBuyerAsync(buyerId, null, TransactionPatternSettings.PageSize, start);
            foreach (var escrow in items)
            {
                if (IsBefore(escrow, since))
                {
                    return One(TransactionPatternRules.DisputeRate(held, disputed, settings));
                }

                if (Str(escrow, "escrowId") == escrowId || !HeldEscrowStatuses.Contains(Str(escrow, "status") ?? ""))
                {
                    continue;
                }

                held++;
                if (Str(escrow, "disputedBy") == buyerId)
                {
                    disputed++;
                }
            }

            if (next == null)
            {
                break;
            }

            start = next;
        }

        return One(TransactionPatternRules.DisputeRate(held, disputed, settings));
    }

    /// <summary>The impossible delivery time rule (no read): the facts come from the delivery's written row.</summary>
    public IReadOnlyList<PatternHit> CheckDeliveryTime(DeliveryTimeFacts facts) =>
        One(TransactionPatternRules.ImpossibleDeliveryTime(facts, Settings));

    internal static bool IsCancelAfterFundBy(Dictionary<string, object> order, string party) => party switch
    {
        Farmer => Str(order, "status") == "cancelled"
                  && Str(order, OrderCommitment.CancelledByField) == OrderCommitment.Party.Farmer
                  && Str(order, OrderCommitment.FaultPartyField) == OrderCommitment.Party.Farmer,
        Trucker => Str(order, OrderCommitment.TruckerResponseField) == OrderCommitment.Response.Cancelled,
        _ => false
    };

    /// <summary>
    /// The party's newest orders back to the window's start, newest first on the party's GSI
    /// (<c>BuyerIdCreatedAtIndex</c>, <c>FarmerIdCreatedAtIndex</c> or <c>TruckerIdCreatedAtIndex</c>), at most
    /// the page cap. Rows are read whole (the indexes project all attributes).
    /// </summary>
    private async Task<List<Dictionary<string, object>>> ReadRecentOrdersAsync(
        string party, string userId, DateTime nowUtc, TransactionPatternSettings settings)
    {
        var since = nowUtc.AddDays(-settings.WindowDays);
        var rows = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? start = null;
        for (var page = 0; page < settings.MaxPages; page++)
        {
            var (items, next) = party switch
            {
                Farmer => await _orders!.QueryOrdersAsync(farmerId: userId, limit: TransactionPatternSettings.PageSize, exclusiveStartKey: start),
                Trucker => await _orders!.QueryOrdersAsync(truckerId: userId, limit: TransactionPatternSettings.PageSize, exclusiveStartKey: start),
                _ => await _orders!.QueryOrdersAsync(buyerId: userId, limit: TransactionPatternSettings.PageSize, exclusiveStartKey: start)
            };

            foreach (var item in items)
            {
                if (IsBefore(item, since))
                {
                    return rows;
                }

                rows.Add(item);
            }

            if (next == null)
            {
                break;
            }

            start = next;
        }

        return rows;
    }

    /// <summary>The row was created before <paramref name="since"/>; a row without a readable time is kept.</summary>
    private static bool IsBefore(Dictionary<string, object> row, DateTime since) =>
        TransactionPatternRules.ParseUtc(Str(row, "createdAt")) is { } createdAt && createdAt < since;

    private static void Add(List<PatternHit> hits, PatternHit? hit)
    {
        if (hit is { } found)
        {
            hits.Add(found);
        }
    }

    private static IReadOnlyList<PatternHit> One(PatternHit? hit) =>
        hit is { } found ? new[] { found } : Array.Empty<PatternHit>();

    private static string? Str(Dictionary<string, object> row, string name) =>
        row.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static decimal? ReadDecimal(Dictionary<string, object> row, string name) =>
        row.TryGetValue(name, out var value) && value != null
        && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
