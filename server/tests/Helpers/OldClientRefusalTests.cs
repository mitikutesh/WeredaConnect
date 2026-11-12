using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-241 (sync-protocol.md 1.52.0): the codes clients know only since FUP-01 go out only to an app
/// that sends <c>X-App-Build</c>; an older app gets the sentence under a code it already treats as
/// permanent.
/// </summary>
public class OldClientRefusalTests
{
    private const string Booked =
        "TRIP_HAS_BOOKINGS: capacity cannot drop below the 20 quintals already booked.";

    private const string ActiveOrders =
        "CROP_HAS_ACTIVE_ORDERS: This listing has 2 order(s) in progress. It cannot be deleted until they are completed or cancelled.";

    [Fact]
    public void NoBuild_TripHasBookings_IsSyncValidation_WithTheWholeSentence()
        => OldClientRefusal.ForClient(Booked, null, "TRIP_HAS_BOOKINGS", "trip").Should().Be(
            "SYNC_VALIDATION: capacity cannot drop below the 20 quintals already booked.");

    [Fact]
    public void NoBuild_CropHasActiveOrders_IsSyncValidation_WithTheWholeSentence()
        => OldClientRefusal.ForClient(ActiveOrders, null, "CROP_HAS_ACTIVE_ORDERS", "crop_listing").Should().Be(
            "SYNC_VALIDATION: This listing has 2 order(s) in progress. It cannot be deleted until they are completed or cancelled.");

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(40)]
    public void ABuild_GetsTheCode_Unchanged(int build)
    {
        OldClientRefusal.ForClient(Booked, build, "TRIP_HAS_BOOKINGS", "trip").Should().Be(Booked);
        OldClientRefusal.ForClient(ActiveOrders, build, "CROP_HAS_ACTIVE_ORDERS", "crop_listing").Should().Be(ActiveOrders);
    }

    [Fact]
    public void NoBuild_AnOrder_GetsOrderValidation()
        => OldClientRefusal.ForClient(ReputationGate.CounterpartyMessage, null, ReputationGate.Code, "order").Should().Be(
            "ORDER_VALIDATION: This farmer or trucker can't take new orders right now.");

    [Theory]
    [InlineData("The trip's orders could not be checked. Please try again later.")] // unprefixed: retried
    [InlineData("TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.")]
    [InlineData("REPUTATION_RESTRICTED: counterparty: This farmer or trucker can't take new orders right now.")]
    [InlineData("TRIP_HAS_BOOKINGSX: not this code")]
    public void NoBuild_AnotherCode_IsLeftAlone(string message)
        => OldClientRefusal.ForClient(message, null, "TRIP_HAS_BOOKINGS", "trip").Should().Be(message);
}
