using FluentAssertions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-444 (delivery-verification-api.md 2.15.1): a handoff point outside the Ethiopia box (latitude 3 to 15,
/// longitude 33 to 48, the bounds of a listing's point and of a live ping) is refused with
/// <c>DELIVERY_VALIDATION</c> and nothing is written. Before, <c>gpsPickup</c> and <c>gpsDropoff</c> were
/// bounded only by the 5 km checks, which run only when a reference point exists, so a crafted client could
/// store (0, 0) or a point abroad on a delivery whose listing or drop-off had none. Where a reference point
/// exists the 5 km refusal still answers first. Uses the delivery rigs of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    public static TheoryData<double, double> PointsOutsideEthiopia => new()
    {
        { 0.0, 0.0 },
        { 40.71, -74.0 },
        { 2.99, 38.7 },
        { 15.01, 38.7 },
        { 9.0, 32.99 },
        { 9.0, 48.01 },
    };

    [Theory]
    [MemberData(nameof(PointsOutsideEthiopia))]
    public async Task Pickup_OutsideEthiopia_OnAListingWithoutAPoint_IsRefused_NothingWritten(double lat, double lng)
    {
        // The listing has no point, so no 5 km check runs.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-444");

        var message = await RefusalOfAsync(MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "pickedUp" },
            { "version", 1 },
            { "scannedQrNonce", "nonce-444" },
            { "gpsPickup", GpsAt(lat, lng) }
        }), "trucker-001", "trucker");

        message.Should().Be(
            "DELIVERY_VALIDATION: gpsPickup must be inside Ethiopia (latitude 3 to 15, longitude 33 to 48).");
        await DeliveryRowNotWrittenAsync();
    }

    [Theory]
    [InlineData(3.0, 33.0)]
    [InlineData(15.0, 48.0)]
    [InlineData(9.0, 38.7)]
    public async Task Pickup_InsideEthiopia_OnAListingWithoutAPoint_Passes(double lat, double lng)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-444");

        var response = await _function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(
            new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "version", 1 },
                { "scannedQrNonce", "nonce-444" },
                { "gpsPickup", GpsAt(lat, lng) }
            })), "trucker-001", "trucker"), _context);

        response.Body.Should().Contain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task Pickup_AtZeroZero_OnAListingWithAPoint_KeepsThePickupFarRefusal()
    {
        // The 5 km check runs first: the phone keeps its permanent, reverted refusal with its own text.
        StubListingPoint("listing-001", FarmLat, FarmLng);
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-444");

        var message = await RefusalOfAsync(MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "pickedUp" },
            { "version", 1 },
            { "scannedQrNonce", "nonce-444" },
            { "gpsPickup", GpsAt(0.0, 0.0) }
        }), "trucker-001", "trucker");

        message.Should().StartWith("DELIVERY_GPS_PROXIMITY: pickup_far: ");
        await DeliveryRowNotWrittenAsync();
    }

    [Theory]
    [MemberData(nameof(PointsOutsideEthiopia))]
    public async Task BuyerConfirm_DropoffOutsideEthiopia_WithNoDropoffPoint_IsRefused(double lat, double lng)
    {
        // A trip created before trip-listing-api 2.1.0 has no destination point and the order none either, so
        // no 5 km check runs.
        GivenGpsTrip(null);
        var write = CaptureDeliveryWrite();

        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)),
            extraData: new() { { "gpsDropoff", GpsData(lat, lng) } });

        body.Should().Contain(
            "DELIVERY_VALIDATION: gpsDropoff must be inside Ethiopia (latitude 3 to 15, longitude 33 to 48).");
        body.Should().NotContain("\"status\":\"success\"");
        write().Should().BeNull();
    }

    [Fact]
    public async Task BuyerConfirm_DropoffInsideEthiopia_WithNoDropoffPoint_StillPasses()
    {
        GivenGpsTrip(null);

        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)),
            extraData: new() { { "gpsDropoff", GpsData(9.03, 38.74) } });

        body.Should().Contain("\"status\":\"success\"");
    }
}
