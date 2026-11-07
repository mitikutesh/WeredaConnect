using System.Globalization;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-15 thresholds (fraud-detection-architecture.md section 15.2). Each environment variable overrides one
/// default and is read once per run. A value that is not an integer in its range keeps the default, so a typo
/// can neither make the scan flag every pair nor turn one run into a long read.
/// </summary>
internal sealed record RelationshipPatternSettings(
    int LookbackHours,
    int MaxOrders,
    int PairMinOrders,
    int PairWindowDays)
{
    /// <summary>Rows per page of one status partition of <c>StatusCreatedAtIndex</c>.</summary>
    public const int StatusPageSize = 100;

    /// <summary>Rows per page of a buyer's orders on <c>BuyerIdCreatedAtIndex</c>.</summary>
    public const int HistoryPageSize = 50;

    /// <summary>Pages of a buyer's orders read at most: 200 orders in the pair window, newest first.</summary>
    public const int HistoryMaxPages = 4;

    public static RelationshipPatternSettings Default { get; } = new(
        LookbackHours: 48,
        MaxOrders: 1000,
        PairMinOrders: 3,
        PairWindowDays: 7);

    public static RelationshipPatternSettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    internal static RelationshipPatternSettings From(Func<string, string?> env) => new(
        Read(env, "FRAUD_RELATIONSHIP_LOOKBACK_HOURS", Default.LookbackHours, 1, 168),
        Read(env, "FRAUD_RELATIONSHIP_MAX_ORDERS", Default.MaxOrders, 1, 10_000),
        Read(env, "FRAUD_PAIR_MIN_ORDERS", Default.PairMinOrders, 2, 100),
        Read(env, "FRAUD_PAIR_WINDOW_DAYS", Default.PairWindowDays, 1, 90));

    private static int Read(Func<string, string?> env, string name, int fallback, int min, int max) =>
        int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>
/// FRD-15: one pattern found on one funded order. The signal is written on each of <see cref="UserIds"/> (the
/// buyer and the farmer, once when they are the same id) with the order as the entity. The description names
/// user ids and the order, never a phone number, a Fayda ID or a device id.
/// </summary>
internal sealed record RelationshipHit(string SignalType, string OrderId, IReadOnlyList<string> UserIds, string Description);

/// <summary>
/// FRD-15 relationship rules as pure decisions over rows the caller read (no I/O), so every hit and miss is
/// table-tested. <see cref="RelationshipPatternService"/> does the bounded reads and the writes.
/// </summary>
internal static class RelationshipPatternRules
{
    /// <summary>The buyer id is the farmer id (roles are fixed, and the escrow paths refuse it: a tampered client or bad data).</summary>
    internal const string SameUser = "same_user";

    /// <summary>Both users rows carry the same phone number.</summary>
    internal const string SamePhone = "same_phone";

    /// <summary>Both users rows carry the same Fayda ID.</summary>
    internal const string SameFaydaId = "same_fayda_id";

    /// <summary>
    /// Order statuses the scan reads: every status an order with a verified payment can be in. The money
    /// statuses first, then the ends (a funded order cancelled or declined is still a trade that moved money).
    /// Not read: <c>created</c>, <c>pending_payment</c>, <c>payment_submitted</c> and <c>payment_failed</c>,
    /// which hold no verified payment.
    /// </summary>
    internal static readonly string[] ScannedOrderStatuses =
    {
        "funded", "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing", "settled",
        OrderCommitment.AwaitingTransportStatus, "disputed", "dispute_resolved", "cancelled", "rejected"
    };

    /// <summary>The order holds a verified payment (<c>paymentVerifiedAt</c>, the FRD-09 rate base).</summary>
    internal static bool IsFunded(IReadOnlyDictionary<string, object> order) => Str(order, "paymentVerifiedAt").Length > 0;

    /// <summary>
    /// Why the buyer and the farmer are one identity, or null: the same user id, else the same phone number,
    /// else the same Fayda ID on both users rows. A missing row or an empty value never matches. Phones and
    /// Fayda IDs are unique at registration, so a match is a data anomaly or a duplicated identity.
    /// </summary>
    public static string? SelfTradeReason(string buyerId, string farmerId, UserDto? buyer, UserDto? farmer)
    {
        if (string.Equals(buyerId, farmerId, StringComparison.Ordinal))
        {
            return SameUser;
        }

        if (buyer == null || farmer == null)
        {
            return null;
        }

        if (SameValue(buyer.PhoneNumber, farmer.PhoneNumber))
        {
            return SamePhone;
        }

        return SameValue(buyer.FaydaId, farmer.FaydaId) ? SameFaydaId : null;
    }

    /// <summary>
    /// How many installs (<c>knownDevices</c>, the X-Device-Id of each sign-in, at most 10 per account) both
    /// accounts have signed in on. 0 when either row is missing.
    /// </summary>
    public static int SharedDeviceCount(UserDto? buyer, UserDto? farmer)
    {
        if (buyer == null || farmer == null || buyer.KnownDevices.Count == 0 || farmer.KnownDevices.Count == 0)
        {
            return 0;
        }

        var farmerDevices = new HashSet<string>(
            farmer.KnownDevices.Where(d => !string.IsNullOrWhiteSpace(d)), StringComparer.Ordinal);
        return buyer.KnownDevices.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal)
            .Count(farmerDevices.Contains);
    }

    /// <summary>
    /// The funded orders between the buyer and <paramref name="farmerId"/> created in the pair window ending at
    /// <paramref name="nowUtc"/>, from the buyer's history and the examined order (counted once even when the
    /// lagging index already shows it). An order the farmer turned down (<c>rejected</c>, or a
    /// <c>farmerResponse</c> of <c>declined</c> or <c>expired</c>) does not count: the farmer took no part.
    /// </summary>
    public static int PairOrderCount(
        IReadOnlyList<Dictionary<string, object>> buyerHistory,
        IReadOnlyDictionary<string, object> examined,
        string farmerId,
        DateTime nowUtc,
        RelationshipPatternSettings settings)
    {
        var since = nowUtc.AddDays(-settings.PairWindowDays);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in buyerHistory.Append(examined))
        {
            var orderId = Str(order, "orderId");
            if (orderId.Length == 0
                || !string.Equals(Str(order, "farmerId"), farmerId, StringComparison.Ordinal)
                || !CountsForPair(order)
                || TransactionPatternRules.ParseUtc(Str(order, "createdAt")) is not { } createdAt
                || createdAt < since)
            {
                continue;
            }

            counted.Add(orderId);
        }

        return counted.Count;
    }

    /// <summary>
    /// The hits of one funded order: <c>SELF_TRADE</c> when the two parties are one identity, else
    /// <c>SHARED_DEVICE_TRADE</c> when their accounts share an install (one order, one identity reason), and
    /// <c>REPEATED_PAIR</c> when the pair holds at least <c>FRAUD_PAIR_MIN_ORDERS</c> funded orders in
    /// <c>FRAUD_PAIR_WINDOW_DAYS</c>. <paramref name="pairCount"/> is null when the history was not read.
    /// </summary>
    public static IReadOnlyList<RelationshipHit> Evaluate(
        string orderId,
        string buyerId,
        string farmerId,
        UserDto? buyer,
        UserDto? farmer,
        int? pairCount,
        RelationshipPatternSettings settings)
    {
        var hits = new List<RelationshipHit>();
        var parties = string.Equals(buyerId, farmerId, StringComparison.Ordinal)
            ? new[] { buyerId }
            : new[] { buyerId, farmerId };

        if (SelfTradeReason(buyerId, farmerId, buyer, farmer) is { } reason)
        {
            hits.Add(new RelationshipHit(FraudDetectionService.SelfTradeSignal, orderId, parties,
                $"Buyer {buyerId} and farmer {farmerId} of funded order {orderId} are one identity ({reason})."));
        }
        else if (SharedDeviceCount(buyer, farmer) is var shared and > 0)
        {
            hits.Add(new RelationshipHit(FraudDetectionService.SharedDeviceTradeSignal, orderId, parties,
                $"Buyer {buyerId} and farmer {farmerId} of funded order {orderId} have signed in on the same device ({shared} shared)."));
        }

        if (pairCount is { } count && count >= settings.PairMinOrders)
        {
            hits.Add(new RelationshipHit(FraudDetectionService.RepeatedPairSignal, orderId, parties,
                $"Buyer {buyerId} and farmer {farmerId}: {count} funded orders in {settings.PairWindowDays} days, this one {orderId}."));
        }

        return hits;
    }

    private static bool CountsForPair(IReadOnlyDictionary<string, object> order) =>
        IsFunded(order)
        && Str(order, "status") != "rejected"
        && Str(order, OrderCommitment.FarmerResponseField) is not (OrderCommitment.Response.Declined or OrderCommitment.Response.Expired);

    private static bool SameValue(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.Ordinal);

    internal static string Str(IReadOnlyDictionary<string, object> row, string name) =>
        row.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";
}
