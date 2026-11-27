using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-439 (sync-protocol.md 1.62.0, delivery-verification-api.md 2.15.0): each handoff GPS refusal keeps
/// the <c>DELIVERY_GPS_PROXIMITY</c> code and names its reason right after it, so the clients tell the five
/// cases apart without reading the text. Nothing is written on any of them. The drop-off reason is checked
/// in <c>SyncFunctionDeliveredGpsTests</c>, the self-pickup without a fix in
/// <c>SelfPickupDelivered_WithoutGps_Rejected</c>. Uses the delivery rig of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    // The listing's point, and a place about 11 km north of it.
    private const double FarmLat = 9.0;
    private const double FarmLng = 38.7;
    private const double FarAwayLat = 9.1;

    [Fact]
    public async Task Pickup_MoreThan5KmFromTheListing_IsRefused_WithThePickupFarReason()
    {
        StubListingPoint("listing-001", FarmLat, FarmLng);
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-439");

        var message = await RefusalOfAsync(MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "pickedUp" },
            { "version", 1 },
            { "scannedQrNonce", "nonce-439" },
            { "gpsPickup", GpsAt(FarAwayLat, FarmLng) }
        }), "trucker-001", "trucker");

        message.Should().MatchRegex(@"^DELIVERY_GPS_PROXIMITY: pickup_far: Pickup GPS is \d+\.\dkm from listing location \(max 5km\)\.$");
        await DeliveryRowNotWrittenAsync();
    }

    [Fact]
    public async Task Pickup_NearTheListing_Passes()
    {
        StubListingPoint("listing-001", FarmLat, FarmLng);
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-439");

        var response = await _function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(
            new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "version", 1 },
                { "scannedQrNonce", "nonce-439" },
                { "gpsPickup", GpsAt(FarmLat + 0.01, FarmLng) }
            })), "trucker-001", "trucker"), _context);

        response.Body.Should().Contain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task SelfPickup_OnAListingWithoutAPoint_IsRefused_WithTheNoFarmPointReason()
    {
        // The fixture's default listing has no coordinates.
        SetupDeliveryQuery(SelfPickupDeliveryItem("pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));

        var message = await RefusalOfAsync(SelfPickupConfirmOp(GpsAt(FarmLat, FarmLng)), "buyer-001", "buyer");

        message.Should().Be(
            "DELIVERY_GPS_PROXIMITY: self_pickup_no_farm_point: This listing has no registered location, so self-pickup cannot be verified.");
        await DeliveryRowNotWrittenAsync();
    }

    [Fact]
    public async Task SelfPickup_MoreThan5KmFromTheFarm_IsRefused_WithTheSelfPickupFarReason()
    {
        StubListingPoint("listing-001", FarmLat, FarmLng);
        SetupDeliveryQuery(SelfPickupDeliveryItem("pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));

        var message = await RefusalOfAsync(SelfPickupConfirmOp(GpsAt(FarAwayLat, FarmLng)), "buyer-001", "buyer");

        message.Should().MatchRegex(@"^DELIVERY_GPS_PROXIMITY: self_pickup_far: You are \d+\.\dkm from the farm location \(max 5km\)\.$");
        await DeliveryRowNotWrittenAsync();
    }

    [Fact]
    public async Task SelfPickup_WithoutAFix_IsRefused_WithTheNoFixReason_BeforeTheListingIsRead()
    {
        SetupDeliveryQuery(SelfPickupDeliveryItem("pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));
        _mockCropRepo.ClearReceivedCalls();

        var message = await RefusalOfAsync(SelfPickupConfirmOp(gps: null), "buyer-001", "buyer");

        message.Should().Be(
            "DELIVERY_GPS_PROXIMITY: self_pickup_no_fix: Location is required to confirm self-pickup at the farm.");
        await _mockCropRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await DeliveryRowNotWrittenAsync();
    }

    [Fact]
    public void Message_PutsTheReasonRightAfterTheCode()
    {
        GpsProximityRefusal.Message(GpsProximityRefusal.ReasonDropoffFar, "Some text.")
            .Should().Be("DELIVERY_GPS_PROXIMITY: dropoff_far: Some text.");
        new[]
        {
            GpsProximityRefusal.ReasonPickupFar,
            GpsProximityRefusal.ReasonSelfPickupNoFix,
            GpsProximityRefusal.ReasonSelfPickupNoFarmPoint,
            GpsProximityRefusal.ReasonSelfPickupFar,
            GpsProximityRefusal.ReasonDropoffFar,
        }.Should().OnlyHaveUniqueItems().And.AllSatisfy(r => r.Should().MatchRegex("^[a-z]+(_[a-z]+)*$"));
    }

    private void StubListingPoint(string listingId, double latitude, double longitude) =>
        _mockCropRepo.GetByIdAsync(listingId).Returns(new Dictionary<string, object>
        {
            { "cropId", listingId },
            { "farmerId", "farmer-001" },
            { "latitude", latitude.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "longitude", longitude.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "status", "available" },
            { "version", "1" }
        });

    private static Dictionary<string, object> GpsAt(double latitude, double longitude) =>
        new() { { "latitude", latitude }, { "longitude", longitude } };

    /// <summary>The buyer's confirm of the self-pickup del-sp with the farmer's scanned nonce.</summary>
    private static SyncOperationRequest SelfPickupConfirmOp(Dictionary<string, object>? gps)
    {
        var data = new Dictionary<string, object>
        {
            { "status", "delivered" },
            { "version", 1 },
            { "scannedQrNonce", "sp-nonce" }
        };
        if (gps != null)
        {
            data["gpsDropoff"] = gps;
        }

        return new SyncOperationRequest
        {
            OperationId = "op-sp-439",
            IdempotencyKey = "buyer-001:delivery:del-sp:update:439",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "update",
            Data = data
        };
    }

    /// <summary>Sends <paramref name="op"/> and returns its error message (the status must be <c>error</c>).</summary>
    private async Task<string> RefusalOfAsync(SyncOperationRequest op, string userId, string role)
    {
        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), userId, role), _context);
        response.StatusCode.Should().Be(200, response.Body);
        var result = JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0];
        result.GetProperty("status").GetString().Should().Be("error", response.Body);
        return result.GetProperty("message").GetString()!;
    }

    private async Task DeliveryRowNotWrittenAsync() =>
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
}
