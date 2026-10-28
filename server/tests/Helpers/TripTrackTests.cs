using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// LT-06: the arrival window maths (live-trip.md section 6.2), the speed, the rounding and the other
/// stops of <see cref="TripTrack"/>. Points sit on one meridian, so each great circle distance is the
/// latitude difference x 111.195 km: 0.9 degrees = 100.075 km, x 1.3 = 130.098 km, at 40 km/h
/// 195.147 minutes (3 h 15 min 8.8 s).
/// </summary>
public class TripTrackTests
{
    private const double Meridian = 38.74;

    private static readonly DateTime Now = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TrackPoint Dropoff = new(9.0, Meridian);
    private static readonly List<TrackStop> NoStops = new();

    // ===== Arrival window =====

    [Fact]
    public void OneFix_Drives40Kmh_TheRoadIs1Point3TimesTheGreatCircle_And10MinutesEachSide()
    {
        var arrival = TripTrack.ArrivalWindow(new[] { Fix(Now, 8.1) }, Dropoff, NoStops, Now, null)!;

        arrival.RemainingKm.Should().BeApproximately(GpsValidator.HaversineDistance(8.1, Meridian, 9.0, Meridian) * 1.3, 1e-9);
        arrival.RemainingKm.Should().BeApproximately(130.098, 0.001);
        // Centre 11:15:08.8; 11:05:08.8 and 11:25:08.8 land on the nearest 5 minute marks.
        arrival.FromUtc.Should().Be(At(11, 5));
        arrival.ToUtc.Should().Be(At(11, 25));
    }

    [Fact]
    public void EachOtherUndeliveredStopNearerThanTheDropoff_Adds20Minutes()
    {
        var stops = new List<TrackStop>
        {
            new("dropoff", 8.5, Meridian, false),   // nearer, to do: +20
            new("pickup", 8.6, Meridian, false),    // nearer, to do: +20
            new("dropoff", 8.4, Meridian, true),    // nearer but done
            new("dropoff", 9.5, Meridian, false)    // to do but farther than the drop-off
        };

        var arrival = TripTrack.ArrivalWindow(new[] { Fix(Now, 8.1) }, Dropoff, stops, Now, null)!;

        // Centre 11:15:08.8 + 40 minutes.
        arrival.FromUtc.Should().Be(At(11, 45));
        arrival.ToUtc.Should().Be(At(12, 5));
        arrival.RemainingKm.Should().BeApproximately(130.098, 0.001);
    }

    [Fact]
    public void TheHalfWidth_Is10MinutesPlusAQuarterOfTheLastFixsAge()
    {
        // The fix is at the drop-off (no travel) and the pause holds the centre at 10:00.
        var arrival = TripTrack.ArrivalWindow(new[] { Fix(At(6, 0), 9.0) }, Dropoff, NoStops, Now, At(10, 0))!;

        // Age 120 minutes: 10 + 30 = 40 minutes each side.
        arrival.FromUtc.Should().Be(At(9, 20));
        arrival.ToUtc.Should().Be(At(10, 40));
        arrival.RemainingKm.Should().Be(0);
    }

    [Fact]
    public void TheHalfWidth_IsAtMost90Minutes()
    {
        var arrival = TripTrack.ArrivalWindow(new[] { Fix(At(1, 20), 9.0) }, Dropoff, NoStops, Now, At(12, 0))!;

        // Age 400 minutes would give 110; capped at 90.
        arrival.FromUtc.Should().Be(At(10, 30));
        arrival.ToUtc.Should().Be(At(13, 30));
    }

    [Fact]
    public void TheStart_IsNeverBeforeNow_AndTheWindowStaysAtLeast10MinutesWide()
    {
        var now = Now.AddMinutes(2);

        // Centre 07:00 with 25.5 minutes each side: both ends are in the past.
        var arrival = TripTrack.ArrivalWindow(new[] { Fix(At(7, 0), 9.0) }, Dropoff, NoStops, now, null)!;

        arrival.FromUtc.Should().Be(At(8, 5));
        arrival.ToUtc.Should().Be(At(8, 15));
    }

    [Fact]
    public void BothEnds_AreOn5MinuteMarks()
    {
        var arrival = TripTrack.ArrivalWindow(
            new[] { Fix(Now.AddMinutes(-7), 8.13), Fix(Now.AddMinutes(-1), 8.17) }, Dropoff, NoStops, Now, null)!;

        foreach (var end in new[] { arrival.FromUtc, arrival.ToUtc })
        {
            (end.Minute % 5).Should().Be(0);
            end.Second.Should().Be(0);
            end.Millisecond.Should().Be(0);
        }
    }

    [Fact]
    public void WhilePaused_TheTravelCountsFromTheEndOfThePause()
    {
        var paused = TripTrack.ArrivalWindow(new[] { Fix(Now, 8.1) }, Dropoff, NoStops, Now, At(9, 0))!;
        var endedBeforeTheFix = TripTrack.ArrivalWindow(new[] { Fix(Now, 8.1) }, Dropoff, NoStops, Now, At(7, 30))!;

        // 09:00 + 3 h 15 min 8.8 s.
        paused.FromUtc.Should().Be(At(12, 5));
        paused.ToUtc.Should().Be(At(12, 25));
        endedBeforeTheFix.FromUtc.Should().Be(At(11, 5));
        endedBeforeTheFix.ToUtc.Should().Be(At(11, 25));
    }

    [Fact]
    public void WithoutAFixOrADropoffPoint_ThereIsNoWindow()
    {
        TripTrack.ArrivalWindow(Array.Empty<TrackFix>(), Dropoff, NoStops, Now, null).Should().BeNull();
        TripTrack.ArrivalWindow(new[] { Fix(Now, 8.1) }, null, NoStops, Now, null).Should().BeNull();
    }

    // ===== Speed =====

    [Fact]
    public void Speed_IsTheMedianOfTheLastThreeLegs()
    {
        var fixes = new[]
        {
            Fix(At(7, 0), 8.0),
            Fix(At(7, 10), 8.5),   // 333 km/h: the fourth leg from the end, not counted
            Fix(At(7, 20), 8.55),  // 33.4 km/h
            Fix(At(7, 30), 8.65),  // 66.7 km/h
            Fix(At(7, 40), 8.68)   // 20.0 km/h
        };

        TripTrack.SpeedKmh(fixes).Should().BeApproximately(GpsValidator.HaversineDistance(8.5, Meridian, 8.55, Meridian) * 6, 1e-9);
    }

    [Fact]
    public void Speed_WithTwoFixes_IsTheirLeg()
    {
        var fixes = new[] { Fix(At(7, 0), 8.0), Fix(At(7, 15), 8.1) };

        TripTrack.SpeedKmh(fixes).Should().BeApproximately(GpsValidator.HaversineDistance(8.0, Meridian, 8.1, Meridian) * 4, 1e-9);
    }

    [Fact]
    public void Speed_IsClampedTo20To60_And40WithOneFix()
    {
        var crawling = new[] { Fix(At(6, 0), 8.0), Fix(At(6, 30), 8.001), Fix(At(7, 0), 8.002) };
        var flying = new[] { Fix(At(6, 0), 8.0), Fix(At(6, 10), 8.5), Fix(At(6, 20), 9.0) };

        TripTrack.SpeedKmh(crawling).Should().Be(20);
        TripTrack.SpeedKmh(flying).Should().Be(60);
        TripTrack.SpeedKmh(new[] { Fix(At(6, 0), 8.0) }).Should().Be(40);
    }

    // ===== Rounding and stops =====

    [Theory]
    [InlineData(8.7525, 3, 8.753)]
    [InlineData(38.97849, 3, 38.978)]
    [InlineData(8.155, 2, 8.16)]
    [InlineData(8.1449, 2, 8.14)]
    public void Round_IsHalfAwayFromZeroOnTheDecimalValue(double value, int decimals, double expected)
    {
        TripTrack.Round(value, decimals).Should().Be(expected);
    }

    [Theory]
    [InlineData("pending", false, false)]
    [InlineData("pickedUp", true, false)]
    [InlineData("inTransit", true, false)]
    [InlineData("delivered", true, true)]
    [InlineData("disputed", true, true)]
    public void StopsOf_AnotherDelivery_SayWhetherEachIsDone(string status, bool pickupDone, bool dropoffDone)
    {
        var other = new Dictionary<string, object> { ["deliveryId"] = "del-102", ["status"] = status };

        var stops = TripTrack.StopsOf(other, new TrackPoint(8.2, 38.8), new TrackPoint(9.0, 38.7)).ToList();

        stops.Should().Equal(
            new TrackStop("pickup", 8.2, 38.8, pickupDone),
            new TrackStop("dropoff", 9.0, 38.7, dropoffDone));
    }

    [Fact]
    public void StopsOf_ACancelledDelivery_OrAPointNotKnown_AreLeftOut()
    {
        var cancelled = new Dictionary<string, object> { ["status"] = "cancelled", ["pickedUpAt"] = "2026-10-27T05:00:00Z" };
        var pending = new Dictionary<string, object> { ["status"] = "pending" };

        TripTrack.StopsOf(cancelled, new TrackPoint(8.2, 38.8), new TrackPoint(9.0, 38.7)).Should().BeEmpty();
        TripTrack.StopsOf(pending, null, new TrackPoint(9.0, 38.7)).Should().Equal(new TrackStop("dropoff", 9.0, 38.7, false));
    }

    [Fact]
    public void PointOf_ReadsStoredNumbers_AndRefusesMissingOrBadOnes()
    {
        TripTrack.PointOf(new Dictionary<string, object> { ["lat"] = "9.03", ["lng"] = "38.74" }, "lat", "lng")
            .Should().Be(new TrackPoint(9.03, 38.74));
        TripTrack.PointOf(new Dictionary<string, object> { ["lat"] = 9.03, ["lng"] = 38 }, "lat", "lng")
            .Should().Be(new TrackPoint(9.03, 38));
        TripTrack.PointOf(new Dictionary<string, object> { ["lat"] = "9.03" }, "lat", "lng").Should().BeNull();
        TripTrack.PointOf(new Dictionary<string, object> { ["lat"] = "north", ["lng"] = "38.74" }, "lat", "lng").Should().BeNull();
        TripTrack.PointOf(null, "lat", "lng").Should().BeNull();
    }

    private static DateTime At(int hour, int minute) => new(2026, 10, 27, hour, minute, 0, DateTimeKind.Utc);

    private static TrackFix Fix(DateTime capturedAt, double latitude) => new(capturedAt, latitude, Meridian, "", 20, "hourly");
}
