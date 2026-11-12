using System.Globalization;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-203 (fraud-detection-architecture.md Rule 4 and 15.3): the trucker's <c>GPS_ANOMALY</c> row is judged
/// from the trucker's own check-ins only. Picked up near Jimma, drop-off point in Addis Ababa (about 259 km);
/// one degree of latitude is 111.19 km here.
/// </summary>
public class TruckerGpsEvidenceTests
{
    private const string Trucker = "trucker-001";
    private const string From = "Jimma";
    private const string To = "Addis Ketema";
    private const double Kmh = 120;

    private static readonly DateTime PickedUpAt = new(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ConfirmedAt = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TrackPoint Pickup = new(7.6700, 36.8300);
    private static readonly TrackPoint Addis = new(9.0300, 38.7400);

    private static TripLocationRow Row(
        DateTime capturedAt, double lat, double lng, string kind = "fix", string? source = "hourly",
        double? accuracyM = 35, bool? mocked = false, string truckerId = Trucker)
        => new("trip-001", $"{LocationPingRules.FormatUtc(capturedAt)}#{kind}", truckerId,
            kind == "fix" ? lat : null, kind == "fix" ? lng : null, kind == "fix" ? accuracyM : null,
            kind == "fix" ? source : null, kind == "fix" ? mocked : null, null,
            LocationPingRules.FormatUtc(capturedAt.AddSeconds(3)), 1_800_000_000);

    private static TruckerCheckIn At(DateTime capturedAt, TrackPoint point) => new(capturedAt, point.Latitude, point.Longitude);

    /// <summary>The verdict with the trip's destination in Addis Ababa unless a test says else.</summary>
    private static TruckerGpsVerdict Judge(
        IReadOnlyList<TruckerCheckIn> checkIns, TrackPoint? destination = null, TrackPoint? orderPoint = null,
        bool withoutDestination = false, string from = From, string to = To)
        => TruckerGpsEvidence.Judge(
            checkIns, Pickup, ConfirmedAt, from, to, withoutDestination ? null : destination ?? Addis, orderPoint, Kmh);

    // ── the window and the check-ins that count ─────────────────────

    [Fact]
    public void WindowKeys_RunFromThePickupToTheConfirmationPlusTheClockSkew_EveryKindIncluded()
    {
        var (from, to) = TruckerGpsEvidence.WindowKeys(PickedUpAt, ConfirmedAt);

        from.Should().Be("2026-11-12T05:00:00.000Z");
        to.Should().Be("2026-11-12T09:02:00.000Z#~");
        // The key condition is BETWEEN: rows at either end, of any kind, are inside.
        string.CompareOrdinal("2026-11-12T05:00:00.000Z#fix", from).Should().BePositive();
        string.CompareOrdinal("2026-11-12T09:02:00.000Z#resumed", to).Should().BeNegative();
        string.CompareOrdinal("2026-11-12T09:02:00.001Z#fix", to).Should().BePositive();
    }

    [Fact]
    public void RoadCheckIns_KeepOnlyTheTruckersPreciseRoadFixes_InTheWindow_InCaptureOrder()
    {
        var rows = new[]
        {
            Row(ConfirmedAt.AddMinutes(-20), 8.0, 37.0, source: "near_stop"),
            Row(ConfirmedAt.AddMinutes(-60), 7.9, 36.9, source: "hourly"),
            Row(ConfirmedAt.AddMinutes(-10), 8.1, 37.1, source: "manual"),
            Row(ConfirmedAt.AddMinutes(1), 8.2, 37.2, source: "arrived"), // after the confirmation, inside the skew
            Row(PickedUpAt, 7.67, 36.83, source: "hourly"), // at the window's start
            // Not evidence of where the truck was at the confirmation:
            Row(PickedUpAt.AddSeconds(20), 7.67, 36.83, source: "scan"), // the pickup scan
            Row(PickedUpAt.AddSeconds(30), 7.67, 36.83, source: "start"), // the start of sharing, at the scan
            Row(ConfirmedAt.AddMinutes(-5), 8.3, 37.3, kind: "paused"),
            Row(ConfirmedAt.AddMinutes(-15), 8.4, 37.4, accuracyM: 1500), // a cell tower guess
            Row(ConfirmedAt.AddMinutes(-16), 8.4, 37.4, accuracyM: null),
            Row(ConfirmedAt.AddMinutes(-17), 8.4, 37.4, mocked: true), // a location-faking app
            Row(ConfirmedAt.AddMinutes(-18), 8.4, 37.4, truckerId: "someone-else"),
            Row(PickedUpAt.AddMinutes(-1), 7.67, 36.83), // before this delivery's pickup
            Row(ConfirmedAt.AddMinutes(3), 9.03, 38.74) // after the confirmation and the skew
        };

        var checkIns = TruckerGpsEvidence.RoadCheckIns(rows, Trucker, PickedUpAt, ConfirmedAt);

        checkIns.Select(c => c.CapturedAtUtc).Should().Equal(
            PickedUpAt, ConfirmedAt.AddMinutes(-60), ConfirmedAt.AddMinutes(-20), ConfirmedAt.AddMinutes(-10),
            ConfirmedAt.AddMinutes(1));
        checkIns[1].Should().Be(new TruckerCheckIn(ConfirmedAt.AddMinutes(-60), 7.9, 36.9));
    }

    [Fact]
    public void RoadCheckIns_OfAScansOnlyTrip_AreNone()
    {
        // A driver who declined sharing (D2) still records the scan fix at each pickup.
        var rows = new[]
        {
            Row(PickedUpAt.AddMinutes(-1), 0, 0, kind: "declined"),
            Row(PickedUpAt.AddSeconds(5), 7.67, 36.83, source: "scan")
        };

        TruckerGpsEvidence.RoadCheckIns(rows, Trucker, PickedUpAt, ConfirmedAt).Should().BeEmpty();
    }

    // ── the verdict ─────────────────────────────────────────────────

    [Fact]
    public void Judge_TheTruckersOwnCheckInAtThePickup_TooSoonToReachTheDropoff_IsAnAnomaly()
    {
        var atTheFarm = new TrackPoint(7.6710, 36.8300); // 0.11 km from the pickup scan

        var verdict = Judge(new[] { At(ConfirmedAt.AddMinutes(-10), atTheFarm) });

        verdict.Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
        verdict.IsAnomaly.Should().BeTrue();
        var toAddis = GpsValidator.HaversineDistance(atTheFarm.Latitude, atTheFarm.Longitude, Addis.Latitude, Addis.Longitude);
        toAddis.Should().BeApproximately(258.8, 0.2);
        verdict.Description.Should().Be(string.Create(CultureInfo.InvariantCulture,
            $"Trucker check-in 10 min before the confirmation: 0.11 km from the pickup point, {toAddis:0.0} km from the drop-off point."));
    }

    [Fact]
    public void Judge_TheTruckersOwnCheckInAtTheDropoff_IsConsistent_WhateverTheBuyerSent()
    {
        // The buyer's coordinates are not an input at all: only the trucker's check-in is compared with the pickup.
        Judge(new[] { At(ConfirmedAt.AddMinutes(-5), Addis) }).Outcome.Should().Be(TruckerGpsOutcome.Consistent);
    }

    [Fact]
    public void Judge_NoCheckIn_IsNoEvidence()
    {
        Judge(Array.Empty<TruckerCheckIn>()).Outcome.Should().Be(TruckerGpsOutcome.NoEvidence);
    }

    [Fact]
    public void Judge_AStaleCheckIn_IsNoEvidence()
    {
        Judge(new[] { At(ConfirmedAt.AddMinutes(-91), Pickup) }).Outcome.Should().Be(TruckerGpsOutcome.NoEvidence);
        // At the freshness edge it counts.
        Judge(new[] { At(ConfirmedAt.AddMinutes(-90), Pickup) }).Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
    }

    [Fact]
    public void Judge_ACheckInTakenJustAfterTheConfirmation_CountsAsTheLast()
    {
        // A phone clock a minute ahead: the time left is the clock skew alone.
        Judge(new[] { At(ConfirmedAt.AddMinutes(1), Pickup) }).Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
    }

    [Fact]
    public void Judge_EveryCheckInAtTheFarm_IsAnAnomaly()
    {
        var checkIns = new[]
        {
            At(ConfirmedAt.AddMinutes(-70), new TrackPoint(7.6702, 36.8301)),
            At(ConfirmedAt.AddMinutes(-10), new TrackPoint(7.6705, 36.8302))
        };

        Judge(checkIns).Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
    }

    [Fact]
    public void Judge_ACheckInAwayFromThePickup_ClearsTheTrucker_WhereverTheLastOneIs()
    {
        // One check-in 1.5 km from the farm on an inter-woreda trip: the truck left the pickup.
        var checkIns = new[]
        {
            At(ConfirmedAt.AddMinutes(-70), Pickup),
            At(ConfirmedAt.AddMinutes(-40), new TrackPoint(7.6700 + 1.5 / 111.19, 36.8300)),
            At(ConfirmedAt.AddMinutes(-10), Pickup)
        };

        Judge(checkIns).Outcome.Should().Be(TruckerGpsOutcome.Consistent);
    }

    [Fact]
    public void Judge_ALateConfirm_WithTheTruckBackAtTheFarm_IsNotAnAnomaly()
    {
        // Jimma to Addis Ababa with a stop: hourly check-ins on the road, none within 5 km of the drop-off (the
        // phone checks in every 15 minutes only once its last fix is within 10 km of a drop-off). The phone keeps
        // sharing until the buyer's confirm syncs; the truck is back at the same farm for its next load when the
        // buyer comes online (or syncs on purpose) ten minutes after the last check-in there.
        var checkIns = new[]
        {
            At(ConfirmedAt.AddMinutes(-230), new TrackPoint(8.2000, 37.5000)),
            At(ConfirmedAt.AddMinutes(-170), new TrackPoint(8.9000, 38.6000)), // about 21 km from the drop-off
            At(ConfirmedAt.AddMinutes(-110), new TrackPoint(8.5000, 38.0000)),
            At(ConfirmedAt.AddMinutes(-50), new TrackPoint(7.9000, 37.1000)),
            At(ConfirmedAt.AddMinutes(-10), new TrackPoint(7.6705, 36.8302))
        };

        Judge(checkIns).Outcome.Should().Be(TruckerGpsOutcome.Consistent);
    }

    [Fact]
    public void Judge_ATripWithoutADestinationPoint_WritesNothing()
    {
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, withoutDestination: true)
            .Outcome.Should().Be(TruckerGpsOutcome.NoDropoffPoint);
    }

    [Fact]
    public void Judge_TheBuyersOrderPointAlone_NeverFlagsTheTrucker()
    {
        // A trip from before trip-listing-api 2.1.0: only the order's point (the buyer's choice) is far away.
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, orderPoint: Addis, withoutDestination: true)
            .Outcome.Should().Be(TruckerGpsOutcome.NoDropoffPoint);
    }

    [Fact]
    public void Judge_ADestinationAFewKmFromTheFarm_WithTheTruckThere_ReachedTheDropoff()
    {
        var destination = new TrackPoint(7.6700 + 4 / 111.19, 36.8300);

        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, destination)
            .Outcome.Should().Be(TruckerGpsOutcome.ReachedDropoff);
    }

    [Fact]
    public void Judge_AtTheFarmButWithTimeToDriveToTheDropoff_IsNotAnAnomaly()
    {
        // Loaded for an hour after the scan, then a 25 km drive: the last check-in is at the farm 35 minutes
        // before the confirmation, and 20 km (beyond the 5 km radius) at 120 km/h takes 10 minutes.
        var destination = new TrackPoint(7.6700 + 25 / 111.19, 36.8300);

        Judge(new[] { At(ConfirmedAt.AddMinutes(-35), Pickup) }, destination)
            .Outcome.Should().Be(TruckerGpsOutcome.CouldReachDropoff);
    }

    [Fact]
    public void Judge_TheOrdersOwnPoint_OnlyClears()
    {
        // The order's own point (the buyer's choice, locked at pickup) 3 km from the farm, the trip's destination
        // in Addis: the truck at the farm is at that drop-off, so no row.
        var orderPoint = new TrackPoint(7.6700 + 3 / 111.19, 36.8300);

        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, orderPoint: orderPoint)
            .Outcome.Should().Be(TruckerGpsOutcome.ReachedDropoff);
        // Far from the farm it changes nothing: the trip's destination decides.
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, orderPoint: new TrackPoint(12.0, 40.0))
            .Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
    }

    [Fact]
    public void Judge_UsesRule4sThresholds_InterWoredaUnder1Km_OrIdenticalPoints()
    {
        var halfKm = new TrackPoint(7.6700 + 0.5 / 111.19, 36.8300);
        var oneAndAHalfKm = new TrackPoint(7.6700 + 1.5 / 111.19, 36.8300);

        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), halfKm) }).Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), oneAndAHalfKm) }).Outcome.Should().Be(TruckerGpsOutcome.Consistent);
        // Within one woreda only identical points flag.
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), halfKm) }, from: From, to: From)
            .Outcome.Should().Be(TruckerGpsOutcome.Consistent);
        Judge(new[] { At(ConfirmedAt.AddMinutes(-10), Pickup) }, from: From, to: From)
            .Outcome.Should().Be(TruckerGpsOutcome.Anomaly);
    }

    [Fact]
    public void Judge_AtTheFarmEarlyAndAtTheDropoffAtTheEnd_IsConsistent()
    {
        var checkIns = new[] { At(ConfirmedAt.AddMinutes(-85), Pickup), At(ConfirmedAt.AddMinutes(-3), Addis) };

        Judge(checkIns).Outcome.Should().Be(TruckerGpsOutcome.Consistent);
    }
}
