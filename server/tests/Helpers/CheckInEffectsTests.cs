using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// LT-07: the decisions behind the side effects of an accepted check-in (live-trip.md section 6.3):
/// left the pickup (2 km), near the drop-off (5 km, once), the minutes of the arrival window, and the
/// impossible jump (over 130 km/h, more than 1 km apart). One degree of latitude is 111.19 km here.
/// </summary>
public class CheckInEffectsTests
{
    private static readonly DateTime PickedUpAt = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Captured = new(2026, 10, 27, 8, 42, 0, DateTimeKind.Utc);
    private static readonly TrackPoint Pickup = new(8.1500, 38.8200);

    private static Dictionary<string, object> Delivery(string status, string? pickedUpAt = "2026-10-27T05:00:00.0000000Z")
    {
        var row = new Dictionary<string, object> { ["deliveryId"] = "del-1", ["status"] = status };
        if (pickedUpAt != null)
        {
            row["pickedUpAt"] = pickedUpAt;
        }

        return row;
    }

    private static TrackPoint North(TrackPoint from, double degrees) => new(from.Latitude + degrees, from.Longitude);

    // ===== left the pickup =====

    [Theory]
    [InlineData(0.0180, true)]   // 2.0 km
    [InlineData(0.5000, true)]   // 55.6 km
    [InlineData(0.0179, false)]  // 1.99 km
    [InlineData(0.0, false)]
    public void Departed_From2KmOfThePickup(double degreesNorth, bool departed)
    {
        var km = CheckInEffects.DepartedKm(Delivery("pickedUp"), Pickup, North(Pickup, degreesNorth), Captured);

        (km != null).Should().Be(departed);
        if (departed)
        {
            km!.Value.Should().BeGreaterThanOrEqualTo(CheckInEffects.DepartureKm);
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("cancelled")]
    public void Departed_OnlyFromPickedUp(string status)
        => CheckInEffects.DepartedKm(Delivery(status), Pickup, North(Pickup, 0.5), Captured).Should().BeNull();

    [Fact]
    public void Departed_NeedsAPickupPoint()
        => CheckInEffects.DepartedKm(Delivery("pickedUp"), null, North(Pickup, 0.5), Captured).Should().BeNull();

    [Fact]
    public void ACheckInFromBeforeThePickupSynced_SaysNothingAboutTheDelivery()
    {
        // Waited offline from 04:59; the pickup synced at 05:00.
        var early = PickedUpAt.AddMinutes(-1);

        CheckInEffects.DepartedKm(Delivery("pickedUp"), Pickup, North(Pickup, 0.5), early).Should().BeNull();
        CheckInEffects.NearbyKmOf(Delivery("pickedUp"), Pickup, Pickup, early).Should().BeNull();
        CheckInEffects.DepartedKm(Delivery("pickedUp"), Pickup, North(Pickup, 0.5), PickedUpAt).Should().NotBeNull();
    }

    [Fact]
    public void WithoutAReadablePickedUpAt_NothingMoves()
    {
        CheckInEffects.DepartedKm(Delivery("pickedUp", pickedUpAt: null), Pickup, North(Pickup, 0.5), Captured).Should().BeNull();
        CheckInEffects.DepartedKm(Delivery("pickedUp", pickedUpAt: "soon"), Pickup, North(Pickup, 0.5), Captured).Should().BeNull();
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1000.0, true)]
    [InlineData(1000.1, false)] // a cell tower guess: its error alone crosses the 2 km, 5 km and 1 km lines
    [InlineData(5000.0, false)]
    [InlineData(null, false)]
    public void PreciseEnough_UpTo1KmOfAccuracy(double? accuracyM, bool precise)
        => CheckInEffects.PreciseEnough(accuracyM).Should().Be(precise);

    // ===== near the drop-off =====

    [Theory]
    [InlineData(0.0449, true)]   // 4.99 km
    [InlineData(0.0, true)]
    [InlineData(0.0450, false)]  // 5.0038 km
    public void Nearby_Within5KmOfTheDropoff(double degreesNorth, bool nearby)
    {
        var dropoff = new TrackPoint(9.0300, 38.7400);

        var km = CheckInEffects.NearbyKmOf(Delivery("inTransit"), dropoff, North(dropoff, degreesNorth), Captured);

        (km != null).Should().Be(nearby);
    }

    [Theory]
    [InlineData("pickedUp", true)]
    [InlineData("inTransit", true)]
    [InlineData("pending", false)]
    [InlineData("delivered", false)]
    [InlineData("disputed", false)]
    [InlineData("cancelled", false)]
    public void Nearby_OnlyOnTheRoad(string status, bool nearby)
        => (CheckInEffects.NearbyKmOf(Delivery(status), Pickup, Pickup, Captured) != null).Should().Be(nearby);

    [Fact]
    public void Nearby_Once_AStampedDeliveryIsNotNearbyAgain()
    {
        var delivery = Delivery("inTransit");
        delivery[CheckInEffects.NearbyNotifiedAtField] = "2026-10-27T08:00:00.000Z";

        CheckInEffects.NearbyKmOf(delivery, Pickup, Pickup, Captured).Should().BeNull();
    }

    [Fact]
    public void Nearby_NeedsADropoffPoint()
        => CheckInEffects.NearbyKmOf(Delivery("inTransit"), null, Pickup, Captured).Should().BeNull();

    // ===== minutes =====

    [Theory]
    [InlineData("2026-10-27T09:25:00.000Z", 25)]
    [InlineData("2026-10-27T09:02:00.000Z", 0)]   // 2 minutes rounds to 0
    [InlineData("2026-10-27T09:03:00.000Z", 5)]   // 3 minutes rounds to 5
    [InlineData("2026-10-27T08:50:00.000Z", 0)]   // never negative
    [InlineData("2026-10-27T11:12:30.000Z", 135)] // 132.5 minutes, half away from zero
    public void Minutes_ToTheWindowStart_RoundedTo5(string from, int minutes)
        => CheckInEffects.MinutesUntil(from, new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc)).Should().Be(minutes);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void Minutes_WithoutAWindow_AreUnknown(string? from)
        => CheckInEffects.MinutesUntil(from, Captured).Should().BeNull();

    // ===== impossible jump =====

    private static TrackFix Fix(DateTime at, TrackPoint point) => new(at, point.Latitude, point.Longitude, "x", 20, "hourly");

    [Fact]
    public void Jump_Over130KmhAndMoreThan1KmApart()
    {
        // 55.6 km in 10 minutes: 334 km/h.
        var a = Fix(Captured, Pickup);
        var b = Fix(Captured.AddMinutes(10), North(Pickup, 0.5));

        CheckInEffects.IsJump(a, b, out var km, out var kmh).Should().BeTrue();
        km.Should().BeApproximately(55.6, 0.1);
        kmh.Should().BeApproximately(333.6, 0.5);
        // The order of the two does not matter.
        CheckInEffects.IsJump(b, a, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Jump_NotAtARealTruckSpeed()
    {
        // 55.6 km in an hour.
        CheckInEffects.IsJump(Fix(Captured, Pickup), Fix(Captured.AddHours(1), North(Pickup, 0.5)), out _, out var kmh)
            .Should().BeFalse();
        kmh.Should().BeLessThan(CheckInEffects.JumpKmh);
    }

    [Fact]
    public void Jump_NeverWithin1Km_HoweverFast()
    {
        // 0.9 km in 10 seconds is 400 km/h of GPS noise.
        CheckInEffects.IsJump(Fix(Captured, Pickup), Fix(Captured.AddSeconds(10), North(Pickup, 0.0081)), out var km, out var kmh)
            .Should().BeFalse();
        km.Should().BeLessThan(CheckInEffects.JumpMinKm);
        kmh.Should().BeGreaterThan(CheckInEffects.JumpKmh);
    }

    [Fact]
    public void Jump_TwoPlacesAtTheSameMoment()
        => CheckInEffects.IsJump(Fix(Captured, Pickup), Fix(Captured, North(Pickup, 0.1)), out _, out _).Should().BeTrue();

    [Fact]
    public void Neighbours_AreTheCheckInsJustBeforeAndJustAfter()
    {
        var fixes = new[]
        {
            Fix(Captured.AddHours(-2), Pickup), Fix(Captured.AddHours(-1), Pickup),
            Fix(Captured, Pickup), Fix(Captured.AddHours(1), Pickup), Fix(Captured.AddHours(2), Pickup)
        };

        var (before, after) = CheckInEffects.Neighbours(fixes, Captured);

        before!.CapturedAtUtc.Should().Be(Captured.AddHours(-1));
        after!.CapturedAtUtc.Should().Be(Captured.AddHours(1));
        CheckInEffects.Neighbours(fixes[..1], Captured.AddHours(-2)).Should().Be(((TrackFix?)null, (TrackFix?)null));
    }

    [Theory]
    [InlineData(2.04, "2.0")]
    [InlineData(2.05, "2.1")]
    [InlineData(4.99, "5.0")]
    public void Distances_AreOneDecimal(double km, string text)
        => CheckInEffects.FormatKm(km).Should().Be(text);
}
