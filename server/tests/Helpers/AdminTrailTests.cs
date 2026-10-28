using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// LT-13: the admin trail of a delivery (live-trip-api.md section 3): the window from pickup to
/// delivery (or now), the points at 4 decimals, the legs between precise fixes with the GPS_JUMP rule,
/// the pauses, the gaps and the summary. Points sit on one meridian, so each great circle distance is
/// the latitude difference x 111.195 km: 0.25 degrees = 27.799 km, 1 degree = 111.195 km.
/// </summary>
public class AdminTrailTests
{
    private const string DeliveryId = "del-101";
    private const string TripId = "trip-101";
    private const double Meridian = 38.74;

    private static readonly DateTime Pickup = At(5, 5);
    private static readonly DateTime Now = At(9, 10);

    // ===== Legs =====

    [Fact]
    public void Legs_RunBetweenConsecutiveFixes_WithTheirDistanceMinutesAndSpeed()
    {
        var trail = Build(Delivered(At(8, 0)), Fix(At(6, 0), 8.10), Fix(At(7, 0), 8.35), Fix(At(7, 30), 8.45));

        trail.Legs.Should().HaveCount(2);
        var first = trail.Legs[0];
        first.FromCapturedAt.Should().Be("2026-10-27T06:00:00.000Z");
        first.ToCapturedAt.Should().Be("2026-10-27T07:00:00.000Z");
        first.DistanceKm.Should().Be(27.80);
        first.Minutes.Should().Be(60.0);
        first.Kmh.Should().Be(27.8);
        first.Jump.Should().BeFalse();
        trail.Legs[1].DistanceKm.Should().Be(11.12);
        trail.Legs[1].Minutes.Should().Be(30.0);
        trail.Legs[1].Kmh.Should().Be(22.2);
        trail.Summary.DistanceKm.Should().Be(38.9);
        trail.Summary.MaxKmh.Should().Be(27.8);
        trail.Summary.Jumps.Should().Be(0);
    }

    [Fact]
    public void ALegOver130KmhAndOver1KmApart_IsAJump_AsTheGpsJumpSignalSeesIt()
    {
        var a = Fix(At(7, 0), 8.35);
        var b = Fix(At(7, 20), 9.35);

        var trail = Build(Delivered(At(8, 0)), a, b);

        var leg = trail.Legs.Should().ContainSingle().Subject;
        leg.DistanceKm.Should().Be(111.19);
        leg.Minutes.Should().Be(20.0);
        leg.Kmh.Should().Be(333.6);
        leg.Jump.Should().BeTrue();
        // The same maths as the check-in effect: no second copy of the rule.
        CheckInEffects.IsJump(Track(a), Track(b), out _, out _).Should().BeTrue();
        trail.Summary.Jumps.Should().Be(1);
        trail.Summary.MaxKmh.Should().Be(333.6);
    }

    [Fact]
    public void TwoFixesLessThan1KmApart_AreNoJump_HoweverFast()
    {
        // 0.005 degrees (0.556 km) in 10 seconds is about 200 km/h of GPS noise.
        var trail = Build(Delivered(At(8, 0)), Fix(At(7, 0), 8.35), Fix(At(7, 0).AddSeconds(10), 8.355));

        var leg = trail.Legs.Should().ContainSingle().Subject;
        leg.Kmh.Should().BeGreaterThan(130);
        leg.Jump.Should().BeFalse();
    }

    [Fact]
    public void ACoarseFix_IsShownAndMarked_ButIsNoEndOfALeg()
    {
        // The cell tower guess lies 100 km off: as a leg end it would be two impossible jumps.
        var trail = Build(
            Delivered(At(8, 0)),
            Fix(At(6, 0), 8.10),
            Fix(At(6, 30), 9.10, accuracyM: 1500),
            Fix(At(7, 0), 8.35));

        trail.Points.Should().HaveCount(3);
        trail.Points[1].Coarse.Should().BeTrue();
        trail.Points[1].AccuracyM.Should().Be(1500);
        trail.Points[0].Coarse.Should().BeFalse();
        var leg = trail.Legs.Should().ContainSingle().Subject;
        leg.FromCapturedAt.Should().Be("2026-10-27T06:00:00.000Z");
        leg.ToCapturedAt.Should().Be("2026-10-27T07:00:00.000Z");
        leg.Jump.Should().BeFalse();
        trail.Summary.CoarseFixes.Should().Be(1);
        trail.Summary.Jumps.Should().Be(0);
        trail.Summary.Fixes.Should().Be(3);
    }

    [Fact]
    public void AFixAtExactly1000Metres_IsPreciseEnough_ForALeg()
    {
        var trail = Build(Delivered(At(8, 0)), Fix(At(6, 0), 8.10), Fix(At(7, 0), 8.35, accuracyM: 1000));

        trail.Points.Should().OnlyContain(p => !p.Coarse);
        trail.Legs.Should().ContainSingle();
    }

    // ===== Window =====

    [Fact]
    public void TheWindow_IsPickupToDelivery_BothEndsIncluded()
    {
        var delivered = At(8, 0);
        var trail = Build(
            Delivered(delivered),
            Fix(Pickup.AddSeconds(-5), 8.0),
            Fix(Pickup, 8.1),
            Fix(delivered, 8.4),
            Fix(delivered.AddSeconds(1), 8.5));

        trail.Points.Select(p => p.CapturedAt).Should().Equal("2026-10-27T05:05:00.000Z", "2026-10-27T08:00:00.000Z");
        trail.From.Should().Be("2026-10-27T05:05:00.000Z");
        trail.Until.Should().Be("2026-10-27T08:00:00.000Z");
        trail.Open.Should().BeFalse();
        trail.Summary.DurationMin.Should().Be(175);
    }

    [Fact]
    public void OnTheRoad_TheWindowRunsToNow_WithThe2MinutePhoneClockSkew()
    {
        var trail = Build(
            Delivery("inTransit"),
            Fix(At(8, 30), 8.3),
            Fix(Now.AddMinutes(1), 8.4),
            Fix(Now.AddMinutes(3), 8.5));

        trail.Open.Should().BeTrue();
        trail.Until.Should().BeNull();
        trail.Points.Select(p => p.CapturedAt).Should().Equal("2026-10-27T08:30:00.000Z", "2026-10-27T09:11:00.000Z");
        trail.Summary.DurationMin.Should().Be(245);
        trail.AsOf.Should().Be("2026-10-27T09:10:00.000Z");
    }

    [Fact]
    public void ACancelledDeliveryAfterPickup_EndsAtItsCancelledAt()
    {
        var cancelled = Delivery("cancelled");
        cancelled["cancelledAt"] = "2026-10-27T07:00:00.000Z";

        var trail = Build(cancelled, Fix(At(6, 0), 8.1), Fix(At(7, 30), 8.3));

        trail.Until.Should().Be("2026-10-27T07:00:00.000Z");
        trail.Points.Should().ContainSingle();
        trail.Dropoff.At.Should().BeNull();
    }

    [Fact]
    public void BeforePickup_TheTrailIsEmpty_AndTheSummaryIsZero()
    {
        var trail = Build(Delivery("pending", pickedUpAt: null), Fix(At(6, 0), 8.1));

        trail.From.Should().BeNull();
        trail.Until.Should().BeNull();
        trail.Open.Should().BeFalse();
        trail.TripId.Should().Be(TripId);
        trail.Points.Should().BeEmpty();
        trail.Legs.Should().BeEmpty();
        trail.Pauses.Should().BeEmpty();
        trail.Gaps.Should().BeEmpty();
        trail.Summary.Should().BeEquivalentTo(new AdminTrailSummaryDto
        {
            SharedWholeWay = false,
            Declined = false,
            Fixes = 0,
            CoarseFixes = 0,
            MockedFixes = 0,
            LateFixes = 0,
            Jumps = 0,
            Gaps = 0,
            DistanceKm = 0,
            DurationMin = 0,
            MaxKmh = null
        });
    }

    [Fact]
    public void ASelfPickup_HasNoTrip_SoNoGapsAndNoClaimOfSharing()
    {
        var self = Delivered(At(8, 0));
        self["deliveryType"] = "self_pickup";
        self["tripId"] = DeliveryId;

        var trail = Build(self);

        AdminTrail.TripIdOf(self).Should().BeNull();
        trail.TripId.Should().BeNull();
        trail.Gaps.Should().BeEmpty();
        trail.Summary.SharedWholeWay.Should().BeFalse();
    }

    // ===== Points =====

    [Fact]
    public void EveryFixAndEvent_IsCarried_AtFourDecimals_InCaptureOrder()
    {
        var late = Fix(At(6, 0), 8.75234, longitude: 38.97856, source: "scan", mocked: true, receivedAt: At(6, 20));
        var trail = Build(
            Delivery("inTransit"),
            Event("resumed", At(7, 0)),
            late,
            Event("paused", At(6, 30), pausedUntil: At(6, 45)));

        trail.Points.Select(p => p.Kind).Should().Equal("fix", "paused", "resumed");
        var fix = trail.Points[0];
        fix.Latitude.Should().Be(8.7523);
        fix.Longitude.Should().Be(38.9786);
        fix.Source.Should().Be("scan");
        fix.AccuracyM.Should().Be(35);
        fix.Mocked.Should().BeTrue();
        fix.ReceivedAt.Should().Be("2026-10-27T06:20:00.000Z");
        fix.Late.Should().BeTrue();
        fix.PausedUntil.Should().BeNull();
        var paused = trail.Points[1];
        paused.PausedUntil.Should().Be("2026-10-27T06:45:00.000Z");
        paused.Latitude.Should().BeNull();
        paused.Source.Should().BeNull();
        paused.Mocked.Should().BeNull();
        paused.Coarse.Should().BeFalse();
        trail.Summary.MockedFixes.Should().Be(1);
        trail.Summary.LateFixes.Should().Be(1);
    }

    [Fact]
    public void ACheckInReceived15MinutesAfterCapture_IsNotLateYet()
    {
        var trail = Build(Delivery("inTransit"), Fix(At(6, 0), 8.1, receivedAt: At(6, 15)));

        trail.Points.Should().ContainSingle().Which.Late.Should().BeFalse();
    }

    [Fact]
    public void PickupAndDropoff_AreTheQrScanPoints_AtFourDecimals()
    {
        var delivered = Delivered(At(8, 0));
        delivered["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "8.15041", ["longitude"] = "38.81967" };
        delivered["gpsDropoff"] = new Dictionary<string, object> { ["latitude"] = "9.03", ["longitude"] = "38.74" };

        var trail = Build(delivered);

        trail.Pickup.Latitude.Should().Be(8.1504);
        trail.Pickup.Longitude.Should().Be(38.8197);
        trail.Pickup.At.Should().Be("2026-10-27T05:05:00.000Z");
        trail.Dropoff.Latitude.Should().Be(9.03);
        trail.Dropoff.At.Should().Be("2026-10-27T08:00:00.000Z");
    }

    // ===== Pauses and gaps =====

    [Fact]
    public void Pauses_AreTheTracksPauses_CutToStartAtPickup()
    {
        var trail = Build(
            Delivered(At(9, 0)),
            Event("paused", At(4, 50), pausedUntil: At(5, 30)),
            Fix(At(5, 40), 8.1),
            Event("paused", At(6, 0), pausedUntil: At(7, 0)),
            Event("resumed", At(6, 20)),
            Fix(At(6, 30), 8.2));

        trail.Pauses.Select(p => (p.From, p.Until)).Should().Equal(
            ("2026-10-27T05:05:00.000Z", "2026-10-27T05:30:00.000Z"),
            ("2026-10-27T06:00:00.000Z", "2026-10-27T06:20:00.000Z"));
    }

    [Fact]
    public void MoreThan90MinutesWithoutACheckIn_IsAGap_AndTheTrailIsNotSharedTheWholeWay()
    {
        var trail = Build(Delivered(At(9, 0)), Fix(At(5, 10), 8.1), Fix(At(6, 10), 8.2), Fix(At(8, 30), 8.4));

        var gap = trail.Gaps.Should().ContainSingle().Subject;
        gap.From.Should().Be("2026-10-27T06:10:00.000Z");
        gap.Until.Should().Be("2026-10-27T08:30:00.000Z");
        gap.Minutes.Should().Be(140);
        trail.Summary.Gaps.Should().Be(1);
        trail.Summary.SharedWholeWay.Should().BeFalse();
    }

    [Fact]
    public void APause_IsNoGap_ItsTimeIsLeftOut()
    {
        // 140 minutes between the check-ins, 60 of them paused: 80 minutes, no gap.
        var trail = Build(
            Delivered(At(9, 0)),
            Fix(At(5, 10), 8.1),
            Fix(At(6, 10), 8.2),
            Event("paused", At(6, 30), pausedUntil: At(7, 30)),
            Fix(At(8, 30), 8.4));

        trail.Gaps.Should().BeEmpty();
        trail.Summary.SharedWholeWay.Should().BeTrue();
    }

    [Fact]
    public void TheStretchesFromPickupAndToTheDelivery_CountToo()
    {
        var trail = Build(Delivered(At(10, 0)), Fix(At(7, 0), 8.1), Fix(At(8, 0), 8.2));

        trail.Gaps.Select(g => (g.From, g.Until, g.Minutes)).Should().Equal(
            ("2026-10-27T05:05:00.000Z", "2026-10-27T07:00:00.000Z", 115),
            ("2026-10-27T08:00:00.000Z", "2026-10-27T10:00:00.000Z", 120));
    }

    [Fact]
    public void OnTheRoad_TwoHoursSinceTheLastCheckIn_IsAGapToNow()
    {
        var trail = Build(Delivery("inTransit"), Fix(At(5, 10), 8.1), Fix(At(6, 10), 8.2), Fix(At(7, 0), 8.3));

        var gap = trail.Gaps.Should().ContainSingle().Subject;
        gap.From.Should().Be("2026-10-27T07:00:00.000Z");
        gap.Until.Should().Be("2026-10-27T09:10:00.000Z");
        gap.Minutes.Should().Be(130);
    }

    // ===== Summary =====

    [Fact]
    public void CheckInsAboutHourly_AreSharedTheWholeWay()
    {
        var trail = Build(Delivered(At(8, 0)), Fix(At(5, 10), 8.1), Fix(At(6, 10), 8.2), Fix(At(7, 10), 8.3), Fix(At(7, 55), 8.4));

        trail.Summary.SharedWholeWay.Should().BeTrue();
        trail.Summary.Declined.Should().BeFalse();
        trail.Summary.Fixes.Should().Be(4);
        trail.Summary.Gaps.Should().Be(0);
    }

    [Fact]
    public void ADeclineBeforePickup_IsDeclined_SoTheTrailIsNotSharedTheWholeWay()
    {
        // The driver said no at the trip's first pickup; this delivery has the scans only.
        var trail = Build(
            Delivered(At(6, 0)),
            Event("declined", At(4, 0)),
            Fix(At(5, 10), 8.1, source: "scan"),
            Fix(At(5, 59), 8.2, source: "scan"));

        trail.Summary.Declined.Should().BeTrue();
        trail.Summary.SharedWholeWay.Should().BeFalse();
        trail.Points.Should().HaveCount(2);
    }

    [Fact]
    public void AResumeAfterTheDecline_BeforePickup_IsNotDeclined()
    {
        var trail = Build(
            Delivered(At(6, 0)),
            Event("declined", At(4, 0)),
            Event("resumed", At(4, 30)),
            Fix(At(5, 10), 8.1),
            Fix(At(5, 59), 8.2));

        trail.Summary.Declined.Should().BeFalse();
        trail.Summary.SharedWholeWay.Should().BeTrue();
    }

    [Fact]
    public void ADeclineInsideTheWindow_IsDeclined()
    {
        var trail = Build(Delivered(At(6, 0)), Fix(At(5, 10), 8.1), Event("declined", At(5, 20)), Fix(At(5, 59), 8.2));

        trail.Summary.Declined.Should().BeTrue();
    }

    [Fact]
    public void NoCheckInAtAll_IsNotSharedTheWholeWay_EvenOnAShortTrip()
    {
        var trail = Build(Delivered(At(6, 0)));

        trail.Gaps.Should().BeEmpty();
        trail.Summary.Fixes.Should().Be(0);
        trail.Summary.SharedWholeWay.Should().BeFalse();
    }

    // ===== Helpers =====

    private static AdminDeliveryTrailResponse Build(Dictionary<string, object> delivery, params TripLocationRow[] rows)
        => AdminTrail.Build(delivery, rows, Now);

    private static DateTime At(int hour, int minute) => new(2026, 10, 27, hour, minute, 0, DateTimeKind.Utc);

    private static Dictionary<string, object> Delivered(DateTime confirmedAt)
        => Delivery("delivered", confirmedAt: LocationPingRules.FormatUtc(confirmedAt));

    private static Dictionary<string, object> Delivery(
        string status, string? pickedUpAt = "2026-10-27T05:05:00.000Z", string? confirmedAt = null)
    {
        var item = new Dictionary<string, object>
        {
            ["deliveryId"] = DeliveryId,
            ["tripId"] = TripId,
            ["status"] = status
        };
        if (pickedUpAt != null)
        {
            item["pickedUpAt"] = pickedUpAt;
        }

        if (confirmedAt != null)
        {
            item["confirmedAt"] = confirmedAt;
        }

        return item;
    }

    private static TripLocationRow Fix(
        DateTime capturedAt, double latitude, double longitude = Meridian, double accuracyM = 35, string source = "hourly",
        bool mocked = false, DateTime? receivedAt = null) => new(
        TripId, $"{LocationPingRules.FormatUtc(capturedAt)}#fix", "trucker-789", latitude, longitude, accuracyM, source, mocked,
        null, LocationPingRules.FormatUtc(receivedAt ?? capturedAt.AddSeconds(3)), 0);

    private static TripLocationRow Event(string kind, DateTime capturedAt, DateTime? pausedUntil = null) => new(
        TripId, $"{LocationPingRules.FormatUtc(capturedAt)}#{kind}", "trucker-789", null, null, null, null, null,
        pausedUntil is { } until ? LocationPingRules.FormatUtc(until) : null,
        LocationPingRules.FormatUtc(capturedAt.AddSeconds(3)), 0);

    private static TrackFix Track(TripLocationRow row)
        => TripTrack.FixesIn(new[] { row }, DateTime.MinValue, DateTime.MaxValue).Single();
}
