using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;
using R = WeredaConnect.Server.Helpers.ConductScoring.Results;
using S = WeredaConnect.Server.Helpers.ConductScoring.Steps;
using L = WeredaConnect.Server.Helpers.ConductScoring.LeftOutReasons;
using N = WeredaConnect.Server.Helpers.ConductScoring.NotCountedReasons;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// GCR-03 (good-conduct-rewards.md sections 4 and 5, reputation-api.md 1.9.0 section 1.8): one test per step per
/// role, each left-out case of section 5, and which orders count.
/// </summary>
public class ConductScoringTests
{
    private static readonly DateTime SettledAt = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    internal static ConductOrder Order(bool selfPickup = false) => new()
    {
        OrderId = "order-1",
        FarmerId = "farmer-1",
        BuyerId = "buyer-1",
        TruckerId = selfPickup ? null : "trucker-1",
        TripId = selfPickup ? null : "trip-1",
        DeliveryId = "del-1",
        OrderTotal = 1200m,
        SettledAt = SettledAt,
        // The trip leaves on 2026-11-07 (Ethiopia day), 09:00 local.
        PickupDate = new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc),
        HasDropoffPlace = !selfPickup,
        FarmerResponse = "accepted"
    };

    /// <summary>A delivery with every step taken on Android.</summary>
    internal static Dictionary<string, object> Delivery(params (string Key, object? Value)[] changes)
    {
        var row = new Dictionary<string, object>
        {
            ["deliveryId"] = "del-1",
            ["orderId"] = "order-1",
            ["status"] = "delivered",
            ["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "9.0", ["longitude"] = "38.7" },
            ["gpsDropoff"] = new Dictionary<string, object> { ["latitude"] = "9.1", ["longitude"] = "38.8" },
            ["pickupGpsStatus"] = "fix",
            ["dropoffGpsStatus"] = "fix",
            ["pickupGpsAccuracyM"] = "12",
            ["dropoffGpsAccuracyM"] = "20",
            ["pickupMethod"] = "qr",
            ["dropoffMethod"] = "qr",
            ["pickupPlatform"] = "android",
            ["dropoffPlatform"] = "android",
            ["pickedUpAt"] = "2026-11-07T07:00:00.000Z",
            ["pickupCapturedAt"] = "2026-11-07T06:59:00.000Z",
            // Asked for 5 minutes after the pickup reached the server.
            ["pickupPhotoRef"] = $"uploads/photos/del-1/pickup-{Ms(new DateTime(2026, 11, 7, 7, 5, 0, DateTimeKind.Utc))}.webp",
            ["dropoffPhotoRef"] = "uploads/photos/del-1/dropoff-1763550000000.webp",
            ["confirmedAt"] = "2026-11-07T15:00:00.000Z",
            ["dropoffCapturedAt"] = "2026-11-07T14:59:00.000Z"
        };
        foreach (var (key, value) in changes)
        {
            if (value == null)
            {
                row.Remove(key);
            }
            else
            {
                row[key] = value;
            }
        }

        return row;
    }

    internal static readonly ConductPings Shared = new(true, 9, false, true);

    private static readonly HashSet<string> BothRated = new() { "farmer-1", "trucker-1" };

    private static long Ms(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;

    private static ConductStep Step(ConductResult result, string id) => result.Steps.Single(s => s.Id == id);

    private static ConductResult Farmer(ConductOrder? order = null, Dictionary<string, object>? delivery = null)
        => ConductScoring.Evaluate(order ?? Order(), delivery ?? Delivery(), Shared, BothRated, "farmer");

    private static ConductResult Trucker(Dictionary<string, object>? delivery = null, ConductPings? pings = null, ConductOrder? order = null)
        => ConductScoring.Evaluate(order ?? Order(), delivery ?? Delivery(), pings ?? Shared, BothRated, "trucker");

    private static ConductResult Buyer(
        Dictionary<string, object>? delivery = null, ConductOrder? order = null, IReadOnlySet<string>? rated = null, bool noRatings = false)
        => ConductScoring.Evaluate(order ?? Order(), delivery ?? Delivery(), Shared, noRatings ? null : rated ?? BothRated, "buyer");

    // ===== full marks =====

    [Fact]
    public void FullMarks_ForAllThreeRoles()
    {
        Farmer().Should().Match<ConductResult>(r => r.Offered == 2 && r.Earned == 2);
        Trucker().Should().Match<ConductResult>(r => r.Offered == 5 && r.Earned == 5);
        Buyer().Should().Match<ConductResult>(r => r.Offered == 7 && r.Earned == 7);
        Buyer().Steps.Select(s => s.Id).Should().Equal(S.ConfirmedInPerson, S.DropoffScanned, S.DropoffFix, S.DropoffPhoto,
            S.DropoffPlaceSet, S.RatedParties, S.FairDisputes);
        Trucker().Steps.Select(s => s.Id).Should().Equal(S.PickupScanned, S.PickupFix, S.PickupPhoto, S.PickedUpOnDay, S.TripShared);
    }

    [Fact]
    public void SelfPickup_HasNoTruckerParty()
    {
        ConductScoring.Parties(Order(selfPickup: true)).Should().Equal(("farmer", "farmer-1"), ("buyer", "buyer-1"));
        ConductScoring.Parties(Order()).Should().Equal(("farmer", "farmer-1"), ("trucker", "trucker-1"), ("buyer", "buyer-1"));
    }

    [Fact]
    public void UnknownParty_Throws()
    {
        var act = () => ConductScoring.Evaluate(Order(), Delivery(), Shared, BothRated, "admin");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ===== farmer =====

    [Theory]
    [InlineData("wrong_quantity", R.Missed)]
    [InlineData("quality_mismatch", R.Missed)]
    [InlineData("not_delivered", R.Earned)]
    public void Farmer_AsDescribed_MissedOnlyWhenTheQuantityOrQualityDisputeWasUpheld(string reason, string expected)
    {
        Step(Farmer(delivery: Delivery(("disputeResolution", "buyer_wins"), ("disputeReason", reason))), S.AsDescribed)
            .Result.Should().Be(expected);
        Step(Farmer(delivery: Delivery(("disputeResolution", "farmer_wins"), ("disputeReason", reason))), S.AsDescribed)
            .Result.Should().Be(R.Earned);
        Step(Farmer(), S.AsDescribed).Result.Should().Be(R.Earned);
    }

    [Fact]
    public void Farmer_StockCurrent_MissedWhenDeclinedAsNotReady()
    {
        Step(Farmer(), S.StockCurrent).Result.Should().Be(R.Earned);
        Step(Farmer(Order() with { FarmerResponse = "declined", DeclineReason = "crop_not_ready" }), S.StockCurrent)
            .Result.Should().Be(R.Missed);
        Step(Farmer(Order() with { FarmerResponse = "declined", DeclineReason = "price" }), S.StockCurrent)
            .Result.Should().Be(R.Earned);
    }

    // ===== trucker =====

    [Theory]
    [InlineData("qr", R.Earned, null)]
    [InlineData("code", R.Missed, null)]
    [InlineData(null, R.LeftOut, L.NotRecorded)]
    public void Trucker_PickupScanned_FromTheStampedMethod(string? method, string result, string? reason)
        => Step(Trucker(Delivery(("pickupMethod", method))), S.PickupScanned).Should().Be(new ConductStep(S.PickupScanned, result, reason));

    [Fact]
    public void Trucker_PickupFix_EarnedWithAPoint_LeftOutWhenCoarse()
    {
        Step(Trucker(), S.PickupFix).Result.Should().Be(R.Earned);
        Step(Trucker(Delivery(("pickupGpsAccuracyM", "1500"))), S.PickupFix)
            .Should().Be(new ConductStep(S.PickupFix, R.LeftOut, L.CoarseFix));
        Step(Trucker(Delivery(("pickupGpsAccuracyM", "1000"))), S.PickupFix).Result.Should().Be(R.Earned);
    }

    [Theory]
    [InlineData("timeout", R.LeftOut, L.NoSignal)]
    [InlineData("denied", R.Missed, null)]
    [InlineData("denied_forever", R.Missed, null)]
    [InlineData("services_off", R.Missed, null)]
    [InlineData(null, R.LeftOut, L.OldClient)]
    public void Trucker_PickupFix_WithoutAPoint_TheStatusDecides(string? status, string result, string? reason)
        => Step(Trucker(Delivery(("gpsPickup", null), ("pickupGpsAccuracyM", null), ("pickupGpsStatus", status))), S.PickupFix)
            .Should().Be(new ConductStep(S.PickupFix, result, reason));

    [Fact]
    public void Trucker_PickupPhoto_EarnedWithin30MinutesOfThePickup_MissedLater()
    {
        Step(Trucker(), S.PickupPhoto).Result.Should().Be(R.Earned);
        var before = $"uploads/photos/del-1/pickup-{Ms(new DateTime(2026, 11, 7, 6, 31, 0, DateTimeKind.Utc))}.webp";
        Step(Trucker(Delivery(("pickupPhotoRef", before))), S.PickupPhoto).Result.Should().Be(R.Earned, "either side of the pickup");
        var late = $"uploads/photos/del-1/pickup-{Ms(new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc))}.jpg";
        Step(Trucker(Delivery(("pickupPhotoRef", late))), S.PickupPhoto).Result.Should().Be(R.Missed);
    }

    [Theory]
    [InlineData("android", R.Missed, null)]
    [InlineData("web", R.LeftOut, L.Web)]
    [InlineData(null, R.LeftOut, L.OldClient)]
    public void Trucker_PickupPhoto_WithoutAPhoto_ThePlatformDecides(string? platform, string result, string? reason)
        => Step(Trucker(Delivery(("pickupPhotoRef", null), ("pickupPlatform", platform))), S.PickupPhoto)
            .Should().Be(new ConductStep(S.PickupPhoto, result, reason));

    [Fact]
    public void Trucker_PickedUpOnDay_ByTheEthiopiaDayOfThePhoneTime()
    {
        Step(Trucker(), S.PickedUpOnDay).Result.Should().Be(R.Earned);
        // 23:30 in Addis on the departure day is still that day; 00:30 the next day is not.
        Step(Trucker(Delivery(("pickupCapturedAt", "2026-11-07T20:30:00.000Z"))), S.PickedUpOnDay).Result.Should().Be(R.Earned);
        Step(Trucker(Delivery(("pickupCapturedAt", "2026-11-07T21:30:00.000Z"))), S.PickedUpOnDay).Result.Should().Be(R.Missed);
        // An early pickup is on time; without the phone's time the server's counts.
        Step(Trucker(Delivery(("pickupCapturedAt", "2026-11-05T08:00:00.000Z"))), S.PickedUpOnDay).Result.Should().Be(R.Earned);
        Step(Trucker(Delivery(("pickupCapturedAt", null), ("pickedUpAt", "2026-11-08T08:00:00.000Z"))), S.PickedUpOnDay)
            .Result.Should().Be(R.Missed);
        Step(Trucker(order: Order() with { PickupDate = null }), S.PickedUpOnDay)
            .Should().Be(new ConductStep(S.PickedUpOnDay, R.LeftOut, L.NotRecorded));
    }

    [Fact]
    public void Trucker_TripShared_MobileOnly_CoveredAndUnmocked()
    {
        Step(Trucker(), S.TripShared).Result.Should().Be(R.Earned);
        Step(Trucker(pings: new ConductPings(true, 3, false, false)), S.TripShared).Result.Should().Be(R.Missed);
        Step(Trucker(pings: new ConductPings(true, 9, true, true)), S.TripShared).Result.Should().Be(R.Missed);
        Step(Trucker(pings: ConductPings.NotRead), S.TripShared).Should().Be(new ConductStep(S.TripShared, R.LeftOut, L.NotRecorded));
        Step(Trucker(Delivery(("pickupPlatform", "web"))), S.TripShared).Should().Be(new ConductStep(S.TripShared, R.LeftOut, L.Web));
        Step(Trucker(Delivery(("pickupPlatform", null))), S.TripShared)
            .Should().Be(new ConductStep(S.TripShared, R.LeftOut, L.OldClient));
    }

    [Fact]
    public void Trucker_WithoutADelivery_EveryStepIsLeftOut()
    {
        var result = ConductScoring.Evaluate(Order(), null, Shared, BothRated, "trucker");
        result.Offered.Should().Be(0);
        result.Steps.Should().OnlyContain(s => s.Result == R.LeftOut && s.Reason == L.NotRecorded).And.HaveCount(5);
    }

    [Fact]
    public void Web_LeavesOutTheStepsTheWebCannotTake_AndKeepsTheRest()
    {
        var web = Delivery(("pickupPhotoRef", null), ("dropoffPhotoRef", null), ("pickupPlatform", "web"), ("dropoffPlatform", "web"));
        var trucker = Trucker(web);
        trucker.Offered.Should().Be(3, "the scan, the fix and the day stay; the photo and sharing are left out");
        trucker.Earned.Should().Be(3);
        var buyer = Buyer(web);
        Step(buyer, S.DropoffPhoto).Should().Be(new ConductStep(S.DropoffPhoto, R.LeftOut, L.Web));
        buyer.Offered.Should().Be(6);

        // A photo the web did take still counts.
        Step(Buyer(Delivery(("dropoffPlatform", "web"))), S.DropoffPhoto).Result.Should().Be(R.Earned);
    }

    [Fact]
    public void OldClient_WithoutGpsStatusOrPlatform_LeavesOutTheStepsThatNeedThem()
    {
        var old = Delivery(("gpsPickup", null), ("gpsDropoff", null), ("pickupGpsStatus", null), ("dropoffGpsStatus", null),
            ("pickupPlatform", null), ("dropoffPlatform", null), ("pickupPhotoRef", null), ("dropoffPhotoRef", null));
        var trucker = Trucker(old);
        Step(trucker, S.PickupFix).Reason.Should().Be(L.OldClient);
        Step(trucker, S.PickupPhoto).Reason.Should().Be(L.OldClient);
        Step(trucker, S.TripShared).Reason.Should().Be(L.OldClient);
        trucker.Offered.Should().Be(2);
        var buyer = Buyer(old);
        Step(buyer, S.DropoffFix).Reason.Should().Be(L.OldClient);
        Step(buyer, S.DropoffPhoto).Reason.Should().Be(L.OldClient);
        buyer.Offered.Should().Be(5);
    }

    // ===== buyer =====

    [Fact]
    public void Buyer_ConfirmedInPerson_MissedOnASystemConfirm()
    {
        Step(Buyer(), S.ConfirmedInPerson).Result.Should().Be(R.Earned);
        Step(Buyer(Delivery(("autoConfirmedAt", "2026-11-09T15:00:00.000Z"))), S.ConfirmedInPerson).Result.Should().Be(R.Missed);
        Step(Buyer(Delivery(("confirmedAt", null))), S.ConfirmedInPerson)
            .Should().Be(new ConductStep(S.ConfirmedInPerson, R.LeftOut, L.NotRecorded));
    }

    [Theory]
    [InlineData("qr", R.Earned, null)]
    [InlineData("code", R.Missed, null)]
    [InlineData(null, R.LeftOut, L.NotRecorded)]
    public void Buyer_DropoffScanned_FromTheStampedMethod(string? method, string result, string? reason)
        => Step(Buyer(Delivery(("dropoffMethod", method))), S.DropoffScanned).Should().Be(new ConductStep(S.DropoffScanned, result, reason));

    [Theory]
    [InlineData("timeout", R.LeftOut, L.NoSignal)]
    [InlineData("denied", R.Missed, null)]
    [InlineData("denied_forever", R.Missed, null)]
    [InlineData("services_off", R.Missed, null)]
    [InlineData(null, R.LeftOut, L.OldClient)]
    public void Buyer_DropoffFix_WithoutAPoint_TheStatusDecides(string? status, string result, string? reason)
        => Step(Buyer(Delivery(("gpsDropoff", null), ("dropoffGpsAccuracyM", null), ("dropoffGpsStatus", status))), S.DropoffFix)
            .Should().Be(new ConductStep(S.DropoffFix, result, reason));

    [Fact]
    public void Buyer_DropoffFix_EarnedWithAPoint_LeftOutWhenCoarse()
    {
        Step(Buyer(), S.DropoffFix).Result.Should().Be(R.Earned);
        Step(Buyer(Delivery(("dropoffGpsAccuracyM", "2500"), ("dropoffGpsStatus", "approximate"))), S.DropoffFix)
            .Should().Be(new ConductStep(S.DropoffFix, R.LeftOut, L.CoarseFix));
    }

    [Theory]
    [InlineData("android", R.Missed, null)]
    [InlineData("web", R.LeftOut, L.Web)]
    [InlineData(null, R.LeftOut, L.OldClient)]
    public void Buyer_DropoffPhoto_AttachedEarns_WithoutOneThePlatformDecides(string? platform, string result, string? reason)
    {
        Step(Buyer(), S.DropoffPhoto).Result.Should().Be(R.Earned);
        Step(Buyer(Delivery(("dropoffPhotoRef", null), ("dropoffPlatform", platform))), S.DropoffPhoto)
            .Should().Be(new ConductStep(S.DropoffPhoto, result, reason));
    }

    [Fact]
    public void Buyer_DropoffPlaceSet_OnATruckedOrder_LeftOutOnASelfPickup()
    {
        Step(Buyer(), S.DropoffPlaceSet).Result.Should().Be(R.Earned);
        Step(Buyer(order: Order() with { HasDropoffPlace = false }), S.DropoffPlaceSet).Result.Should().Be(R.Missed);
        Step(Buyer(order: Order(selfPickup: true)), S.DropoffPlaceSet)
            .Should().Be(new ConductStep(S.DropoffPlaceSet, R.LeftOut, L.SelfPickup));
    }

    [Fact]
    public void Buyer_RatedParties_EveryRateeOfTheOrder()
    {
        Step(Buyer(), S.RatedParties).Result.Should().Be(R.Earned);
        Step(Buyer(rated: new HashSet<string> { "farmer-1" }), S.RatedParties).Result.Should().Be(R.Missed);
        Step(Buyer(order: Order(selfPickup: true), rated: new HashSet<string> { "farmer-1" }), S.RatedParties)
            .Result.Should().Be(R.Earned, "a self-pickup has only the farmer to rate");
        Step(Buyer(noRatings: true), S.RatedParties).Should().Be(new ConductStep(S.RatedParties, R.LeftOut, L.NotRecorded));
    }

    [Fact]
    public void Buyer_FairDisputes_MissedWhenADisputeWentAgainstTheBuyer()
    {
        Step(Buyer(), S.FairDisputes).Result.Should().Be(R.Earned);
        Step(Buyer(Delivery(("disputeResolution", "farmer_wins"))), S.FairDisputes).Result.Should().Be(R.Missed);
        Step(Buyer(Delivery(("disputeResolution", "buyer_wins"))), S.FairDisputes).Result.Should().Be(R.Earned);
    }

    [Fact]
    public void SelfPickup_Buyer_HasEveryStepButTheDropoffPlace()
    {
        var result = Buyer(order: Order(selfPickup: true), rated: new HashSet<string> { "farmer-1" });
        result.Offered.Should().Be(6);
        result.Earned.Should().Be(6);
    }

    // ===== which orders count =====

    private static string? Reason(
        string party = "farmer", string userId = "farmer-1", ConductOrder? order = null, Dictionary<string, object>? delivery = null,
        bool? fraudClean = true, IReadOnlyList<ConductStamp>? rows = null)
        => ConductScoring.NotCountedReason(order ?? Order(), delivery ?? Delivery(), party, userId, fraudClean, rows ?? Array.Empty<ConductStamp>());

    internal static ConductStamp Row(string orderId, string userId, DateTime settledAt, bool counted = true,
        string farmerId = "farmer-1", string? truckerId = "trucker-1", string buyerId = "buyer-1", string party = "farmer")
        => new()
        {
            UserId = userId,
            OrderId = orderId,
            Party = party,
            FarmerId = farmerId,
            TruckerId = truckerId,
            BuyerId = buyerId,
            Counted = counted,
            OccurredAt = settledAt,
            RecordedAt = settledAt
        };

    [Fact]
    public void Counts_ASettledUndisputedOrderOf500EtbOrMore()
    {
        Reason().Should().BeNull();
        Reason(order: Order() with { OrderTotal = 500m }).Should().BeNull();
        Reason(order: Order() with { OrderTotal = 499.99m }).Should().Be(N.BelowMinimum);
        Reason(order: Order() with { OrderTotal = null }).Should().Be(N.BelowMinimum);
    }

    [Fact]
    public void Disputed_CountsOnlyForThePartyItWasResolvedFor()
    {
        var open = Delivery(("status", "disputed"), ("disputedBy", "buyer-1"));
        foreach (var (party, user) in new[] { ("farmer", "farmer-1"), ("trucker", "trucker-1"), ("buyer", "buyer-1") })
        {
            Reason(party, user, delivery: open).Should().Be(N.Disputed);
        }

        var farmerWins = Delivery(("status", "disputed"), ("disputedBy", "buyer-1"), ("disputeResolution", "farmer_wins"));
        Reason("farmer", "farmer-1", delivery: farmerWins).Should().BeNull();
        Reason("trucker", "trucker-1", delivery: farmerWins).Should().BeNull();
        Reason("buyer", "buyer-1", delivery: farmerWins).Should().Be(N.Disputed);

        var buyerWins = Delivery(("disputeResolution", "buyer_wins"));
        Reason("buyer", "buyer-1", delivery: buyerWins).Should().BeNull();
        Reason("farmer", "farmer-1", delivery: buyerWins).Should().Be(N.Disputed);

        // FUP-64: buyer_wins settled as it is on a paid-out order.
        Reason("buyer", "buyer-1", order: Order() with { DisputeOverride = true }).Should().BeNull();
        Reason("trucker", "trucker-1", order: Order() with { DisputeOverride = true }).Should().Be(N.Disputed);
    }

    [Fact]
    public void FraudSignal_OnAnyParty_OrUnreadable_DoesNotCount()
    {
        Reason(fraudClean: false).Should().Be(N.FraudSignal);
        Reason(fraudClean: null).Should().Be(N.FraudUnchecked);
    }

    [Fact]
    public void PairCap_TheThirdCountedOrderOfAPairIn30Days_DoesNotCount()
    {
        var two = new[]
        {
            Row("order-a", "farmer-1", SettledAt.AddDays(-20)),
            Row("order-b", "farmer-1", SettledAt.AddDays(-3))
        };
        Reason(rows: two).Should().Be(N.PairCap);
        Reason("trucker", "trucker-1", rows: two.Select(r => r with { UserId = "trucker-1", Party = "trucker" }).ToList())
            .Should().Be(N.PairCap);

        // Both ways in time: a later settle within 30 days counts against an earlier one.
        Reason(rows: new[] { two[0], Row("order-c", "farmer-1", SettledAt.AddDays(10)) }).Should().Be(N.PairCap);

        // Outside 30 days, not counted, or this order's own row: no cap.
        Reason(rows: new[] { two[1], Row("order-a", "farmer-1", SettledAt.AddDays(-31)) }).Should().BeNull();
        Reason(rows: new[] { two[1], two[0] with { Counted = false } }).Should().BeNull();
        Reason(rows: new[] { two[1], Row("order-1", "farmer-1", SettledAt) }).Should().BeNull();
    }

    [Fact]
    public void PairCap_IsPerPair_AnotherBuyerAndTruckerDoNotCount()
    {
        var others = new[]
        {
            Row("order-a", "farmer-1", SettledAt.AddDays(-2), truckerId: "trucker-9", buyerId: "buyer-9"),
            Row("order-b", "farmer-1", SettledAt.AddDays(-1), truckerId: null, buyerId: "buyer-8")
        };
        Reason(rows: others).Should().BeNull();

        // Two with the same buyer but other truckers: the farmer and buyer pair is full.
        var sameBuyer = others.Select(r => r with { BuyerId = "buyer-1" }).ToList();
        Reason(rows: sameBuyer).Should().Be(N.PairCap);
    }

    [Fact]
    public void Reasons_AreCheckedInOrder()
    {
        Reason(order: Order() with { OrderTotal = 10m }, delivery: Delivery(("status", "disputed")), fraudClean: false)
            .Should().Be(N.Disputed);
        Reason(order: Order() with { OrderTotal = 10m }, fraudClean: false).Should().Be(N.BelowMinimum);
    }

    // ===== pings =====

    private static TripLocationRow Ping(DateTime at, string truckerId = "trucker-1", bool? mocked = false, string kind = "fix")
        => new("trip-1", $"{LocationPingRules.FormatUtc(at)}#{kind}", truckerId, 9.0, 38.7, 20, "hourly", mocked, null,
            LocationPingRules.FormatUtc(at), 0);

    private static readonly DateTime From = new(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SummarizePings_CoveredWhenNoStretchIsLongerThan90Minutes()
    {
        var to = From.AddHours(4);
        var rows = new[] { Ping(From.AddMinutes(30)), Ping(From.AddMinutes(110)), Ping(From.AddMinutes(190)) };
        ConductScoring.SummarizePings(rows, complete: true, "trucker-1", From, to)
            .Should().Be(new ConductPings(true, 3, false, true));

        // A gap of two hours in the middle.
        ConductScoring.SummarizePings(new[] { rows[0], rows[2] }, true, "trucker-1", From, to).Covered.Should().BeFalse();
        // The last fix is more than 90 minutes before the drop-off.
        ConductScoring.SummarizePings(new[] { rows[0], rows[1] }, true, "trucker-1", From, to).Covered.Should().BeFalse();
        // None at all, even on a short trip.
        ConductScoring.SummarizePings(Array.Empty<TripLocationRow>(), true, "trucker-1", From, From.AddMinutes(30))
            .Should().Be(new ConductPings(true, 0, false, false));
    }

    [Fact]
    public void SummarizePings_MockedFixesMark_OtherTruckersAndOtherKindsAreIgnored()
    {
        var to = From.AddHours(2);
        var rows = new[]
        {
            Ping(From.AddMinutes(30)), Ping(From.AddMinutes(60), mocked: true), Ping(From.AddMinutes(100)),
            Ping(From.AddMinutes(50), truckerId: "trucker-2", mocked: true), Ping(From.AddMinutes(70), kind: "paused")
        };
        var summary = ConductScoring.SummarizePings(rows, true, "trucker-1", From, to);
        summary.AnyMocked.Should().BeTrue();
        summary.Fixes.Should().Be(2);

        ConductScoring.SummarizePings(rows, complete: false, "trucker-1", From, to).Should().Be(ConductPings.NotRead);
        ConductScoring.SummarizePings(rows, true, "trucker-1", From, From).Should().Be(ConductPings.NotRead);
    }

    // ===== the order and the row =====

    [Fact]
    public void ConductOrder_FromRow_ReadsTheFacts_AndASelfPickupHasNoTrucker()
    {
        var row = new Dictionary<string, object>
        {
            ["orderId"] = "order-1", ["farmerId"] = "farmer-1", ["buyerId"] = "buyer-1", ["truckerId"] = "trucker-1",
            ["tripId"] = "trip-1", ["fulfillment"] = "trucker", ["activeDeliveryId"] = "del-1", ["orderTotal"] = "1250.5",
            ["updatedAt"] = "2026-11-20T09:00:00.000Z", ["pickupDate"] = "2026-11-07T06:00:00.000Z",
            ["deliveryLatitude"] = "9.03", ["deliveryLongitude"] = "38.74", ["farmerResponse"] = "accepted"
        };
        var order = ConductOrder.FromRow(row)!;
        order.TruckerId.Should().Be("trucker-1");
        order.OrderTotal.Should().Be(1250.5m);
        order.SettledAt.Should().Be(SettledAt);
        order.HasDropoffPlace.Should().BeTrue();
        order.DeliveryId.Should().Be("del-1");

        row["fulfillment"] = "self_pickup";
        row.Remove("deliveryLongitude");
        row["disputeOverrideAt"] = "2026-11-21T09:00:00.000Z";
        var selfPickup = ConductOrder.FromRow(row)!;
        selfPickup.SelfPickup.Should().BeTrue();
        selfPickup.HasDropoffPlace.Should().BeFalse();
        selfPickup.DisputeOverride.Should().BeTrue();

        row.Remove("buyerId");
        ConductOrder.FromRow(row).Should().BeNull();
    }

    [Fact]
    public void Stamp_CarriesTheResult_TheCount_AndA400DayLife()
    {
        var result = Buyer();
        var stamp = ConductScoring.Stamp(Order(), "buyer-1", result, N.PairCap, SettledAt.AddMinutes(1));
        stamp.EventId.Should().Be("HANDOFF#order-1");
        stamp.Party.Should().Be("buyer");
        stamp.Counted.Should().BeFalse();
        stamp.NotCountedReason.Should().Be(N.PairCap);
        stamp.Offered.Should().Be(7);
        stamp.Counterparties.Should().BeEquivalentTo("farmer-1", "trucker-1");
        stamp.PurgeAt.Should().Be(new DateTimeOffset(SettledAt.AddDays(400)).ToUnixTimeSeconds());
    }

    [Fact]
    public void CanReevaluate_OnceWithin14Days()
    {
        var stamp = Row("order-1", "buyer-1", SettledAt);
        ConductScoring.CanReevaluate(stamp, SettledAt.AddDays(14)).Should().BeTrue();
        ConductScoring.CanReevaluate(stamp, SettledAt.AddDays(14).AddSeconds(1)).Should().BeFalse();
        ConductScoring.CanReevaluate(stamp with { ReevaluatedAt = SettledAt.AddDays(1) }, SettledAt.AddDays(2)).Should().BeFalse();
        ConductScoring.CanReevaluate(null, SettledAt).Should().BeFalse();
    }

    [Theory]
    [InlineData("uploads/photos/del-1/pickup-1762498800000.webp", 1762498800000L)]
    [InlineData("uploads/photos/del-1-x/dropoff-1762498800000.jpg", 1762498800000L)]
    [InlineData("uploads/photos/del-1/pickup-abc.webp", null)]
    [InlineData("", null)]
    public void PhotoKeyTime_ReadsTheKeysMilliseconds(string key, long? ms)
        => ConductScoring.PhotoKeyTime(key).Should().Be(ms is { } value ? DateTime.UnixEpoch.AddMilliseconds(value) : null);

    // ===== GCR-04: the verified figure (spec 5, reputation-api.md 1.10.0 section 1.9) =====

    private static readonly DateTime ReadAt = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ConductStamp Scored(string orderId, int offered, int earned, DateTime settledAt, bool counted = true)
        => Row(orderId, "farmer-1", settledAt, counted) with { Offered = offered, Earned = earned };

    [Fact]
    public void Verified_IsTheShareOfStepsTaken_OverTheCountedOrders_AWholePercentHalfUp()
    {
        // 1 of 8 steps is 12.5%: half up gives 13 (banker's rounding would give 12).
        var figure = ConductScoring.Verified(new[]
        {
            Scored("o-1", 4, 1, ReadAt.AddDays(-1)),
            Scored("o-2", 2, 0, ReadAt.AddDays(-2)),
            Scored("o-3", 2, 0, ReadAt.AddDays(-3))
        }, ReadAt);
        figure.Should().Be(new ConductVerified(13, 3));

        // A sum of steps, not a share of orders fully earned: 13 of 17 steps is 76%, though only one order is full.
        ConductScoring.Verified(new[]
        {
            Scored("o-1", 6, 6, ReadAt.AddDays(-1)),
            Scored("o-2", 6, 4, ReadAt.AddDays(-2)),
            Scored("o-3", 5, 3, ReadAt.AddDays(-3))
        }, ReadAt).Should().Be(new ConductVerified(76, 3));
    }

    [Fact]
    public void Verified_IsAbsentBelow3CountedOrders_AndOrdersThatDoNotCountAreIgnored()
    {
        var two = new[] { Scored("o-1", 6, 6, ReadAt.AddDays(-1)), Scored("o-2", 6, 6, ReadAt.AddDays(-2)) };
        ConductScoring.Verified(two, ReadAt).Should().Be(new ConductVerified(null, 2));

        // Uncounted stamps (a pair cap, a fraud signal, below 500 ETB) neither reach the minimum nor move the figure.
        var withUncounted = two.Concat(new[]
        {
            Scored("o-3", 6, 0, ReadAt.AddDays(-3), counted: false),
            Scored("o-4", 6, 0, ReadAt.AddDays(-4), counted: false)
        }).ToList();
        ConductScoring.Verified(withUncounted, ReadAt).Should().Be(new ConductVerified(null, 2));

        ConductScoring.Verified(withUncounted.Append(Scored("o-5", 6, 3, ReadAt.AddDays(-5))), ReadAt)
            .Should().Be(new ConductVerified(83, 3), "15 of 18 steps");
        ConductScoring.Verified(Array.Empty<ConductStamp>(), ReadAt).Should().Be(new ConductVerified(null, 0));
    }

    [Fact]
    public void Verified_TheWindowIs90Days_ExactlyAsReliability()
    {
        var edge = ReadAt - TimeSpan.FromDays(90);
        var inside = new[]
        {
            Scored("o-1", 2, 2, ReadAt.AddDays(-1)),
            Scored("o-2", 2, 2, ReadAt.AddDays(-2)),
            Scored("o-3", 2, 0, edge.AddMilliseconds(1))
        };
        ConductScoring.Verified(inside, ReadAt).Should().Be(new ConductVerified(67, 3), "settled 1 ms inside the window");

        // Settled exactly 90 days ago is outside (the reliability bound is after the window's start), and so is a
        // settle stamped after the read's own time.
        var outside = new[]
        {
            Scored("o-1", 2, 2, ReadAt.AddDays(-1)),
            Scored("o-2", 2, 2, ReadAt.AddDays(-2)),
            Scored("o-3", 2, 0, edge),
            Scored("o-4", 2, 0, ReadAt.AddMilliseconds(1))
        };
        ConductScoring.Verified(outside, ReadAt).Should().Be(new ConductVerified(null, 2));
        ConductScoring.Verified(outside.Append(Scored("o-5", 2, 2, ReadAt)), ReadAt).Should().Be(new ConductVerified(100, 3), "a settle at the read's time is in");

        ConductScoring.VerifiedWindow.Should().Be(ReputationPolicy.ReliabilityWindow).And.Be(TimeSpan.FromDays(90));
    }

    [Fact]
    public void Verified_NoStepOffered_IsAbsent_AndEarnedNeverPassesOffered()
    {
        var nothingOffered = new[]
        {
            Scored("o-1", 0, 0, ReadAt.AddDays(-1)),
            Scored("o-2", 0, 0, ReadAt.AddDays(-2)),
            Scored("o-3", 0, 0, ReadAt.AddDays(-3))
        };
        ConductScoring.Verified(nothingOffered, ReadAt).Should().Be(new ConductVerified(null, 3));

        // A damaged row (earned above offered, a negative count) cannot push the figure past 100 or below 0.
        ConductScoring.Verified(new[]
        {
            Scored("o-1", 2, 5, ReadAt.AddDays(-1)),
            Scored("o-2", 2, 2, ReadAt.AddDays(-2)),
            Scored("o-3", 2, -1, ReadAt.AddDays(-3))
        }, ReadAt).Should().Be(new ConductVerified(67, 3), "4 of 6 steps");
    }
}
