using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using static WeredaConnect.Server.Services.OrderCommitment;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// GCR-03: the facts of a settled order the conduct stamp reads, from the orders stream's new image or from a
/// stored order row. Null when the order names no farmer or buyer, or has no time to stamp it at.
/// </summary>
public sealed record ConductOrder
{
    public required string OrderId { get; init; }
    public required string FarmerId { get; init; }
    public required string BuyerId { get; init; }

    /// <summary>The trucker who carried it; null on a self-pickup.</summary>
    public string? TruckerId { get; init; }

    public string? TripId { get; init; }

    /// <summary>The order's <c>activeDeliveryId</c>.</summary>
    public string? DeliveryId { get; init; }

    public decimal? OrderTotal { get; init; }

    /// <summary>The order's <c>updatedAt</c> at the settle (the record time when it has none).</summary>
    public DateTime SettledAt { get; init; }

    /// <summary>The trip's departure as stamped at funding (<c>pickupDate</c>).</summary>
    public DateTime? PickupDate { get; init; }

    /// <summary><c>deliveryLatitude</c> and <c>deliveryLongitude</c> both set.</summary>
    public bool HasDropoffPlace { get; init; }

    public string? FarmerResponse { get; init; }
    public string? DeclineReason { get; init; }

    /// <summary>FUP-64: a <c>buyer_wins</c> <c>settle_as_is</c> flagged this paid-out order (<c>disputeOverrideAt</c>).</summary>
    public bool DisputeOverride { get; init; }

    public bool SelfPickup => TruckerId == null;

    /// <summary>The order from any reader of its attributes as text.</summary>
    public static ConductOrder? From(Func<string, string?> text, DateTime? fallbackAt)
    {
        var orderId = NonEmpty(text("orderId"));
        var farmerId = NonEmpty(text("farmerId"));
        var buyerId = NonEmpty(text("buyerId"));
        if (orderId == null || farmerId == null || buyerId == null)
        {
            return null;
        }

        DateTime? settledAt = ReputationPolicy.TryParseUtc(text("updatedAt"), out var updated) ? updated : fallbackAt;
        if (settledAt is not { } at)
        {
            return null;
        }

        var truckerId = NonEmpty(text("truckerId"));
        var selfPickup = PickupJudgement.IsSelfPickup(text(FulfillmentField) ?? string.Empty,
            text("tripId") ?? string.Empty, truckerId ?? string.Empty);
        return new ConductOrder
        {
            OrderId = orderId,
            FarmerId = farmerId,
            BuyerId = buyerId,
            TruckerId = selfPickup ? null : truckerId,
            TripId = NonEmpty(text("tripId")),
            DeliveryId = NonEmpty(text("activeDeliveryId")),
            OrderTotal = decimal.TryParse(text("orderTotal"), NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
                ? total
                : null,
            SettledAt = at,
            PickupDate = ReputationPolicy.TryParseUtc(text(PickupDateField), out var pickup) ? pickup : null,
            HasDropoffPlace = IsNumber(text(OrderDeliveryLocation.LatitudeField)) && IsNumber(text("deliveryLongitude")),
            FarmerResponse = NonEmpty(text(FarmerResponseField)),
            DeclineReason = NonEmpty(text(DeclineReasonField)),
            DisputeOverride = NonEmpty(text(OrderMoneyFields.DisputeOverrideAt)) != null
        };
    }

    /// <summary>The order as the orders stream's image shows it.</summary>
    public static ConductOrder? FromImage(StreamImage image, DateTime? fallbackAt) => From(image.Text, fallbackAt);

    /// <summary>The order as <c>OrderRepository</c> reads it.</summary>
    public static ConductOrder? FromRow(IReadOnlyDictionary<string, object> row)
        => From(key => row.TryGetValue(key, out var value) && value != null ? value.ToString() : null, null);

    private static bool IsNumber(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// GCR-03: what the trucker's own pings say about sharing the trip, from the pickup to the drop-off.
/// <see cref="Read"/> false: not read (no trip, no table, a read that stopped at its page cap), so the step is left out.
/// </summary>
public sealed record ConductPings(bool Read, int Fixes, bool AnyMocked, bool Covered)
{
    public static readonly ConductPings NotRead = new(false, 0, false, false);
}

/// <summary>
/// GCR-03 (good-conduct-rewards.md sections 4 and 5, reputation-api.md 1.9.0 section 1.8): the conduct stamp of one
/// party of one settled order as pure functions. Each step is <c>earned</c>, <c>missed</c> or <c>left_out</c> (the
/// party could not take it, or the server has no record of it); <c>offered</c> counts the steps not left out,
/// <c>earned</c> the steps taken. Reward only: nothing here costs a point, restricts or hides anyone, and nothing
/// reads it back yet (GCR-04 does). Not scored here: the farm point (FUP-442), the check-ins (GCR-13), the
/// identity step (once per window, GCR-04), the listing kept current and the drop-off code made ready (FUP-550).
/// </summary>
public static class ConductScoring
{
    /// <summary>Spec 5: an order below this total (ETB) never counts (the earn-back minimum).</summary>
    public const decimal MinOrderEtb = 500m;

    /// <summary>Spec 5: counted orders per pair of parties in <see cref="PairWindow"/>, at most.</summary>
    public const int PairCapOrders = 2;

    public static readonly TimeSpan PairWindow = TimeSpan.FromDays(30);

    /// <summary>Spec 4.2: the pickup photo counts when it was asked for within this long of the pickup.</summary>
    public static readonly TimeSpan PickupPhotoWindow = TimeSpan.FromMinutes(30);

    /// <summary>Spec 8 item 8: a late rating or photo re-evaluates the stamp once within this long of it.</summary>
    public static readonly TimeSpan ReevaluationWindow = TimeSpan.FromDays(14);

    /// <summary>Stamp rows live as long as the penalty rows (architecture doc 3.1).</summary>
    public static TimeSpan Retention => ReputationPolicy.PenaltyRetention;

    /// <summary>Spec 4.4: a fix coarser than this is left out of the sum (the live ping's precision, live-trip-api.md invariant 6).</summary>
    public const double CoarseFixM = CheckInEffects.MaxAccuracyM;

    /// <summary>The longest stretch of the trip without a fix that still reads as shared: the hourly cadence plus half an hour.</summary>
    public static TimeSpan PingGap => TruckerGpsEvidence.Freshness;

    /// <summary>The two fraud signals that keep an order from counting (the earn-back rule of reputation-api.md 1.6).</summary>
    public static readonly IReadOnlyList<string> ExcludingSignals = new[]
    {
        FraudDetectionService.SelfTradeSignal, FraudDetectionService.SharedDeviceTradeSignal
    };

    public static class Results
    {
        public const string Earned = "earned";
        public const string Missed = "missed";
        public const string LeftOut = "left_out";
    }

    /// <summary>The step ids, per role (spec 4).</summary>
    public static class Steps
    {
        public const string AsDescribed = "as_described";
        public const string StockCurrent = "stock_current";

        public const string PickupScanned = "pickup_scanned";
        public const string PickupFix = "pickup_fix";
        public const string PickupPhoto = "pickup_photo";
        public const string PickedUpOnDay = "picked_up_on_day";
        public const string TripShared = "trip_shared";

        public const string ConfirmedInPerson = "confirmed_in_person";
        public const string DropoffScanned = "dropoff_scanned";
        public const string DropoffFix = "dropoff_fix";
        public const string DropoffPhoto = "dropoff_photo";
        public const string DropoffPlaceSet = "dropoff_place_set";
        public const string RatedParties = "rated_parties";
        public const string FairDisputes = "fair_disputes";
    }

    /// <summary>Why a step is left out of <c>offered</c> (spec 5).</summary>
    public static class LeftOutReasons
    {
        /// <summary>The phone found no GPS signal in time (<c>gpsStatus: timeout</c>).</summary>
        public const string NoSignal = "no_signal";

        /// <summary>The fix was coarser than <see cref="CoarseFixM"/>.</summary>
        public const string CoarseFix = "coarse_fix";

        /// <summary>The action came from the web app, which cannot take this step.</summary>
        public const string Web = "web";

        /// <summary>The app sent no <c>gpsStatus</c> or no <c>platform</c> (before GCR-02 and FUP-440).</summary>
        public const string OldClient = "old_client";

        /// <summary>The server has no record to judge the step by.</summary>
        public const string NotRecorded = "not_recorded";

        /// <summary>A self-pickup has no drop-off place to set.</summary>
        public const string SelfPickup = "self_pickup";
    }

    /// <summary>Why an order does not count for a party (spec 5, "Which orders count").</summary>
    public static class NotCountedReasons
    {
        public const string Disputed = "disputed";
        public const string BelowMinimum = "below_minimum";
        public const string FraudSignal = "fraud_signal";

        /// <summary>The fraud signals could not be read: no count rather than one a self-trade could buy.</summary>
        public const string FraudUnchecked = "fraud_unchecked";

        public const string PairCap = "pair_cap";
    }

    private const string Pickup = HandoffEvidence.PickupLeg;
    private const string Dropoff = HandoffEvidence.DropoffLeg;

    /// <summary>The parties of the order, farmer first: farmer, trucker (not on a self-pickup), buyer.</summary>
    public static IReadOnlyList<(string Party, string UserId)> Parties(ConductOrder order)
    {
        var parties = new List<(string, string)> { (Party.Farmer, order.FarmerId) };
        if (order.TruckerId is { } truckerId)
        {
            parties.Add((Party.Trucker, truckerId));
        }

        parties.Add((Party.Buyer, order.BuyerId));
        return parties;
    }

    /// <summary>
    /// One party's stamp. <paramref name="delivery"/> is the order's delivery as <c>DeliveryRepository</c> reads it
    /// (null when there is none); <paramref name="pings"/> the trucker's pings summary; <paramref name="ratedIds"/> the
    /// farmer and trucker the buyer rated on this order (null when the ratings could not be read).
    /// </summary>
    public static ConductResult Evaluate(
        ConductOrder order,
        IReadOnlyDictionary<string, object>? delivery,
        ConductPings pings,
        IReadOnlySet<string>? ratedIds,
        string party)
    {
        var steps = party switch
        {
            Party.Farmer => FarmerSteps(order, delivery),
            Party.Trucker => TruckerSteps(order, delivery, pings),
            Party.Buyer => BuyerSteps(order, delivery, ratedIds),
            _ => throw new ArgumentOutOfRangeException(nameof(party), party, "A conduct stamp is for a farmer, trucker or buyer.")
        };

        return new ConductResult(
            party,
            steps.Count(s => s.Result != Results.LeftOut),
            steps.Count(s => s.Result == Results.Earned),
            steps);
    }

    // ===== farmer (spec 4.1) =====

    private static List<ConductStep> FarmerSteps(ConductOrder order, IReadOnlyDictionary<string, object>? delivery)
    {
        // Goods as described: no dispute upheld for the quantity or the quality.
        var upheld = Text(delivery, DropoffJudgement.DisputeResolutionField) == "buyer_wins"
                     && Text(delivery, "disputeReason") is "wrong_quantity" or "quality_mismatch";
        // Stock was current: the order was not declined because the crop was not ready.
        var notReady = order.FarmerResponse == Response.Declined && order.DeclineReason == "crop_not_ready";
        return new List<ConductStep>
        {
            Taken(Steps.AsDescribed, !upheld),
            Taken(Steps.StockCurrent, !notReady)
        };
    }

    // ===== trucker (spec 4.2) =====

    private static List<ConductStep> TruckerSteps(ConductOrder order, IReadOnlyDictionary<string, object>? delivery, ConductPings pings)
    {
        var ids = new[] { Steps.PickupScanned, Steps.PickupFix, Steps.PickupPhoto, Steps.PickedUpOnDay, Steps.TripShared };
        if (delivery == null)
        {
            return ids.Select(id => Left(id, LeftOutReasons.NotRecorded)).ToList();
        }

        return new List<ConductStep>
        {
            Scan(Steps.PickupScanned, delivery, Pickup),
            Fix(Steps.PickupFix, delivery, Pickup, "gpsPickup"),
            Photo(Steps.PickupPhoto, delivery, Pickup, PickupPhotoInWindow(delivery)),
            OnDay(order, delivery),
            Shared(delivery, pings)
        };
    }

    /// <summary>Picked up on the planned day: the pickup's Ethiopia day is on or before the trip's departure day.</summary>
    private static ConductStep OnDay(ConductOrder order, IReadOnlyDictionary<string, object> delivery)
    {
        if (order.PickupDate is not { } departure || PickupTime(delivery) is not { } pickedUp)
        {
            return Left(Steps.PickedUpOnDay, LeftOutReasons.NotRecorded);
        }

        return Taken(Steps.PickedUpOnDay,
            TripDeparture.StartOfEthiopiaDayUtc(pickedUp) <= TripDeparture.StartOfEthiopiaDayUtc(departure));
    }

    /// <summary>Shared the trip: mobile only, pings cover the trip with no mocked fix.</summary>
    private static ConductStep Shared(IReadOnlyDictionary<string, object> delivery, ConductPings pings)
    {
        switch (HandoffEvidence.StoredText(Row(delivery), HandoffEvidence.PlatformField(Pickup)))
        {
            case "web":
                return Left(Steps.TripShared, LeftOutReasons.Web);
            case "android":
                return pings.Read
                    ? Taken(Steps.TripShared, pings.Covered && !pings.AnyMocked)
                    : Left(Steps.TripShared, LeftOutReasons.NotRecorded);
            default:
                return Left(Steps.TripShared, LeftOutReasons.OldClient);
        }
    }

    /// <summary>
    /// The pickup photo was asked for within <see cref="PickupPhotoWindow"/> of the pickup, either side. Both times are
    /// the server's (the key's presign time and <c>pickedUpAt</c>), so a pickup queued offline and sent with its photo
    /// reads as one made online. True when either time cannot be read.
    /// </summary>
    private static bool PickupPhotoInWindow(IReadOnlyDictionary<string, object> delivery)
    {
        if (PhotoKeyTime(Text(delivery, DeliveryPhotoKeys.FieldOf(DeliveryPhotoKeys.Pickup))) is not { } askedAt
            || !ReputationPolicy.TryParseUtc(Text(delivery, "pickedUpAt"), out var pickedUpAt))
        {
            return true;
        }

        return (askedAt - pickedUpAt).Duration() <= PickupPhotoWindow;
    }

    // ===== buyer (spec 4.3) =====

    private static List<ConductStep> BuyerSteps(
        ConductOrder order, IReadOnlyDictionary<string, object>? delivery, IReadOnlySet<string>? ratedIds)
    {
        var steps = new List<ConductStep>();
        if (delivery == null)
        {
            steps.AddRange(new[] { Steps.ConfirmedInPerson, Steps.DropoffScanned, Steps.DropoffFix, Steps.DropoffPhoto }
                .Select(id => Left(id, LeftOutReasons.NotRecorded)));
        }
        else
        {
            // Confirmed in person: the buyer's own confirm, not the order cron's for a silent buyer (T9).
            steps.Add(Text(delivery, DropoffJudgement.AutoConfirmedAtField) != null
                ? Taken(Steps.ConfirmedInPerson, false)
                : Text(delivery, "confirmedAt") != null
                    ? Taken(Steps.ConfirmedInPerson, true)
                    : Left(Steps.ConfirmedInPerson, LeftOutReasons.NotRecorded));
            steps.Add(Scan(Steps.DropoffScanned, delivery, Dropoff));
            steps.Add(Fix(Steps.DropoffFix, delivery, Dropoff, "gpsDropoff"));
            steps.Add(Photo(Steps.DropoffPhoto, delivery, Dropoff, inWindow: true));
        }

        steps.Add(order.SelfPickup
            ? Left(Steps.DropoffPlaceSet, LeftOutReasons.SelfPickup)
            : Taken(Steps.DropoffPlaceSet, order.HasDropoffPlace));

        var ratees = order.TruckerId is { } truckerId ? new[] { order.FarmerId, truckerId } : new[] { order.FarmerId };
        steps.Add(ratedIds == null
            ? Left(Steps.RatedParties, LeftOutReasons.NotRecorded)
            : Taken(Steps.RatedParties, ratees.All(ratedIds.Contains)));

        // Fair disputes: no dispute on the order resolved against the buyer.
        steps.Add(Taken(Steps.FairDisputes, Text(delivery, DropoffJudgement.DisputeResolutionField) != "farmer_wins"));
        return steps;
    }

    // ===== shared step rules =====

    /// <summary>Scanned, not typed: the leg's method as GCR-01 stamped it; none recorded (a handoff before 2.19.0) is left out.</summary>
    private static ConductStep Scan(string id, IReadOnlyDictionary<string, object> delivery, string leg)
        => HandoffEvidence.MethodOf(Row(delivery), leg) switch
        {
            "qr" => Taken(id, true),
            "code" => Taken(id, false),
            _ => Left(id, LeftOutReasons.NotRecorded)
        };

    /// <summary>
    /// A fix at the handoff. A stored point passed the handoff's 5 km check (a far one is refused,
    /// delivery-verification-api.md 2.15.0), so it is inside the radius. A fix coarser than <see cref="CoarseFixM"/>
    /// is left out. Without a point the leg's <c>gpsStatus</c> decides: <c>timeout</c> left out (no signal),
    /// <c>denied</c>, <c>denied_forever</c> and <c>services_off</c> missed (the party chose), none left out (an old app).
    /// </summary>
    private static ConductStep Fix(string id, IReadOnlyDictionary<string, object> delivery, string leg, string pointField)
    {
        if (delivery.TryGetValue(pointField, out var point) && point is IReadOnlyDictionary<string, object> { Count: > 0 })
        {
            return HandoffEvidence.AccuracyOf(Row(delivery), leg) is { } metres && metres > CoarseFixM
                ? Left(id, LeftOutReasons.CoarseFix)
                : Taken(id, true);
        }

        return HandoffEvidence.StoredText(Row(delivery), HandoffEvidence.GpsStatusField(leg)) switch
        {
            "timeout" => Left(id, LeftOutReasons.NoSignal),
            "denied" or "denied_forever" or "services_off" => Taken(id, false),
            null => Left(id, LeftOutReasons.OldClient),
            _ => Left(id, LeftOutReasons.NotRecorded)
        };
    }

    /// <summary>
    /// The handoff photo of <paramref name="leg"/>. A photo attached earns the step (the pickup's only when
    /// <paramref name="inWindow"/>). Without one the leg's platform decides: <c>android</c> missed; <c>web</c> left out
    /// (a desktop browser may have no camera, and the platform cannot tell a phone's browser apart); none left out.
    /// </summary>
    private static ConductStep Photo(string id, IReadOnlyDictionary<string, object> delivery, string leg, bool inWindow)
    {
        var type = leg == Pickup ? DeliveryPhotoKeys.Pickup : DeliveryPhotoKeys.Dropoff;
        if (Text(delivery, DeliveryPhotoKeys.FieldOf(type)) != null)
        {
            return Taken(id, inWindow);
        }

        return HandoffEvidence.StoredText(Row(delivery), HandoffEvidence.PlatformField(leg)) switch
        {
            "android" => Taken(id, false),
            "web" => Left(id, LeftOutReasons.Web),
            _ => Left(id, LeftOutReasons.OldClient)
        };
    }

    // ===== which orders count (spec 5) =====

    /// <summary>
    /// Why the order does not count for <paramref name="userId"/> as <paramref name="party"/>, or null when it counts,
    /// checked in this order: a dispute not resolved for the party, a total below <see cref="MinOrderEtb"/>, a fraud
    /// signal on any party of the order (<paramref name="fraudClean"/> false; null when it could not be read), and the
    /// pair cap over the party's own stamps (<paramref name="partyRows"/>; this order's own row is ignored).
    /// </summary>
    public static string? NotCountedReason(
        ConductOrder order,
        IReadOnlyDictionary<string, object>? delivery,
        string party,
        string userId,
        bool? fraudClean,
        IReadOnlyList<ConductStamp> partyRows)
    {
        if (DisputedAgainst(order, delivery, party))
        {
            return NotCountedReasons.Disputed;
        }

        if ((order.OrderTotal ?? 0m) < MinOrderEtb)
        {
            return NotCountedReasons.BelowMinimum;
        }

        if (fraudClean != true)
        {
            return fraudClean == null ? NotCountedReasons.FraudUnchecked : NotCountedReasons.FraudSignal;
        }

        return PairCapped(order, userId, partyRows) ? NotCountedReasons.PairCap : null;
    }

    /// <summary>
    /// Disputed and not resolved for the party: an open dispute counts for nobody, <c>farmer_wins</c> for the farmer
    /// and the trucker, <c>buyer_wins</c> (or the FUP-64 override on a paid-out order) for the buyer.
    /// </summary>
    private static bool DisputedAgainst(ConductOrder order, IReadOnlyDictionary<string, object>? delivery, string party)
    {
        var resolution = Text(delivery, DropoffJudgement.DisputeResolutionField) ?? (order.DisputeOverride ? "buyer_wins" : null);
        var disputed = resolution != null || Text(delivery, "disputedBy") != null || Text(delivery, "status") == "disputed";
        if (!disputed)
        {
            return false;
        }

        return resolution switch
        {
            "farmer_wins" => party == Party.Buyer,
            "buyer_wins" => party != Party.Buyer,
            _ => true
        };
    }

    /// <summary>
    /// The pair cap: with some other party of this order the user already has <see cref="PairCapOrders"/> counted
    /// stamps settled within <see cref="PairWindow"/> before or after this one (both ways, so arrival order does not
    /// matter).
    /// </summary>
    private static bool PairCapped(ConductOrder order, string userId, IReadOnlyList<ConductStamp> partyRows)
    {
        var counted = partyRows
            .Where(r => r.Counted && r.OrderId != order.OrderId && (r.OccurredAt - order.SettledAt).Duration() < PairWindow)
            .ToList();
        return new[] { order.FarmerId, order.TruckerId, order.BuyerId }
            .Where(id => !string.IsNullOrEmpty(id) && id != userId)
            .Distinct()
            .Any(other => counted.Count(r => r.Counterparties.Contains(other)) >= PairCapOrders);
    }

    // ===== pings (spec 4.2, "Shared the trip") =====

    /// <summary>
    /// The trucker's <c>fix</c> pings of the trip from <paramref name="fromUtc"/> (the pickup) to <paramref name="toUtc"/>
    /// (the drop-off, plus the ingest's clock skew), by their <c>capturedAt</c>: covered when there is at least one and
    /// no stretch of the window (from its start to the first, between two, from the last to its end) is longer than
    /// <see cref="PingGap"/>. A mocked fix fills no gap and marks the summary. A read that stopped at its page cap, or
    /// an empty window, is not read.
    /// </summary>
    public static ConductPings SummarizePings(
        IEnumerable<TripLocationRow> rows, bool complete, string truckerId, DateTime fromUtc, DateTime toUtc)
    {
        if (!complete || toUtc <= fromUtc)
        {
            return ConductPings.NotRead;
        }

        var until = toUtc + TruckerGpsEvidence.ClockSkew;
        var times = new List<DateTime>();
        var mocked = false;
        foreach (var row in rows)
        {
            if (!TripTrack.TryParseSortKey(row.SortKey, out var capturedAt, out var kind)
                || kind != LocationPingRules.KindFix
                || !string.Equals(row.TruckerId, truckerId, StringComparison.Ordinal)
                || capturedAt < fromUtc
                || capturedAt > until)
            {
                continue;
            }

            if (row.Mocked == true)
            {
                mocked = true;
                continue;
            }

            times.Add(capturedAt);
        }

        times.Sort();
        var covered = times.Count > 0 && times[0] - fromUtc <= PingGap && toUtc - times[^1] <= PingGap;
        for (var i = 1; covered && i < times.Count; i++)
        {
            covered = times[i] - times[i - 1] <= PingGap;
        }

        return new ConductPings(true, times.Count, mocked, covered);
    }

    /// <summary>The pickup as the phone made it (<c>pickupCapturedAt</c>, stored only when plausible), else <c>pickedUpAt</c>.</summary>
    public static DateTime? PickupTime(IReadOnlyDictionary<string, object> delivery)
        => ReputationPolicy.TryParseUtc(Text(delivery, TransactionPatternRules.PickupCapturedAtField), out var phone)
            ? phone
            : ReputationPolicy.TryParseUtc(Text(delivery, "pickedUpAt"), out var server) ? server : null;

    /// <summary>The drop-off as the phone made it (<c>dropoffCapturedAt</c>), else <c>confirmedAt</c>.</summary>
    public static DateTime? DropoffTime(IReadOnlyDictionary<string, object> delivery)
        => ReputationPolicy.TryParseUtc(Text(delivery, HandoffEvidence.DropoffCapturedAtField), out var phone)
            ? phone
            : ReputationPolicy.TryParseUtc(Text(delivery, "confirmedAt"), out var server) ? server : null;

    // ===== the row =====

    /// <summary>The stamp row of one party, first written at <paramref name="recordedAt"/>.</summary>
    public static ConductStamp Stamp(
        ConductOrder order, string userId, ConductResult result, string? notCountedReason, DateTime recordedAt)
        => new()
        {
            UserId = userId,
            OrderId = order.OrderId,
            Party = result.Party,
            FarmerId = order.FarmerId,
            BuyerId = order.BuyerId,
            TruckerId = order.TruckerId,
            Offered = result.Offered,
            Earned = result.Earned,
            Steps = result.Steps,
            Counted = notCountedReason == null,
            NotCountedReason = notCountedReason,
            OrderTotal = order.OrderTotal,
            OccurredAt = order.SettledAt,
            RecordedAt = recordedAt,
            PurgeAt = new DateTimeOffset(DateTime.SpecifyKind(order.SettledAt + Retention, DateTimeKind.Utc)).ToUnixTimeSeconds()
        };

    /// <summary>Whether a stamp may still take its one re-evaluation at <paramref name="now"/>.</summary>
    public static bool CanReevaluate(ConductStamp? stamp, DateTime now)
        => stamp != null && stamp.ReevaluatedAt == null && now - stamp.RecordedAt <= ReevaluationWindow;

    /// <summary>The server's time in a photo key (<c>.../{type}-{ms}.{ext}</c>), or null.</summary>
    internal static DateTime? PhotoKeyTime(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var dash = key.LastIndexOf('-');
        var dot = key.LastIndexOf('.');
        return dash >= 0 && dot > dash
               && long.TryParse(key.AsSpan(dash + 1, dot - dash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
               && ms is > 0 and < 253_402_300_799_999
            ? DateTime.UnixEpoch.AddMilliseconds(ms)
            : null;
    }

    private static ConductStep Taken(string id, bool taken) => new(id, taken ? Results.Earned : Results.Missed);

    private static ConductStep Left(string id, string reason) => new(id, Results.LeftOut, reason);

    private static string? Text(IReadOnlyDictionary<string, object>? row, string key)
        => row != null && row.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

    /// <summary><see cref="HandoffEvidence"/> reads a <see cref="Dictionary{TKey,TValue}"/>.</summary>
    private static Dictionary<string, object> Row(IReadOnlyDictionary<string, object> delivery)
        => delivery as Dictionary<string, object> ?? new Dictionary<string, object>(delivery);
}
