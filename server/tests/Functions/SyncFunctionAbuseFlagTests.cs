using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// Persona simulation 2026-11-19 (fraud-detection-architecture.md 3.44.0, Rule 7): the review-only flags
/// written through the real sync handler. BUG-5: a trucker's pickup without a GPS fix is accepted and
/// flagged <c>PICKUP_NO_FIX</c>. BUG-7: an order on a trip that does not start where the listing is, is
/// accepted and flagged <c>ORDER_ROUTE_MISMATCH</c>. Each test has its negative side: a pickup with a fix,
/// the same route, and no trip write nothing. Uses the delivery and order rigs of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private SyncFunction AbuseFlagFunction()
    {
        _patternFraud.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        return new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo);
    }

    private Task NoFlagAsync(string signalType)
        => _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), signalType, Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());

    // ── BUG-5: a pickup without a GPS fix ────────────────────────────

    private async Task<string> PickUpAsync(SyncFunction function, string scannedNonce, bool withFix)
    {
        var data = new Dictionary<string, object>
        {
            { "status", "pickedUp" }, { "scannedQrNonce", scannedNonce }, { "version", 1 }
        };
        if (withFix)
        {
            data["gpsPickup"] = new Dictionary<string, object> { { "latitude", 9.68 }, { "longitude", 39.53 } };
        }

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(data)), "trucker-001", "trucker"), _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    [Fact]
    public async Task Pickup_WithoutAGpsFix_IsAccepted_AndFlaggedOnTheTruckerForReview()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var body = await PickUpAsync(AbuseFlagFunction(), "nonce-abc-123", withFix: false);

        // A rural phone without a fix still picks up: the flag is the only change.
        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync(
            "trucker-001", "PICKUP_NO_FIX", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("without a GPS fix") && d.Contains("handoff: qr")));
        Assert.Equal(0, FraudDetectionService.SignalWeight("PICKUP_NO_FIX"));
    }

    [Fact]
    public async Task Pickup_WithAGpsFix_WritesNoFlag()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var body = await PickUpAsync(AbuseFlagFunction(), "nonce-abc-123", withFix: true);

        Assert.Contains("\"status\":\"success\"", body);
        await NoFlagAsync("PICKUP_NO_FIX");
    }

    [Fact]
    public async Task Pickup_ThatIsRefused_WritesNoPickupFlag()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var body = await PickUpAsync(AbuseFlagFunction(), "wrong-nonce", withFix: false);

        Assert.Contains("DELIVERY_NONCE_MISMATCH", body);
        await NoFlagAsync("PICKUP_NO_FIX");
    }

    [Fact]
    public async Task Pickup_WithoutAFix_WhenTheFlagCannotBeWritten_IsStillAccepted()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");
        _patternFraud.RecordFraudSignalAsync("trucker-001", "PICKUP_NO_FIX", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("signals down"));

        var body = await PickUpAsync(AbuseFlagFunction(), "nonce-abc-123", withFix: false);

        Assert.Contains("\"status\":\"success\"", body);
    }

    // ── BUG-7: an order on a trip that does not pass the farm ────────

    /// <summary>farmer-001's teff listing at a named place, and trip-001 leaving from <paramref name="tripFrom"/>.</summary>
    private void GivenListingAndTrip(string? listingWoreda, string? listingPlace, string tripFrom)
    {
        var listing = new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "listingId", "listing-001" }, { "farmerId", "farmer-001" },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" }, { "quantity", 10.0 },
            { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        };
        if (listingWoreda != null)
        {
            listing["woreda"] = listingWoreda;
        }

        if (listingPlace != null)
        {
            listing["locationName"] = listingPlace;
        }

        _mockCropRepo.GetByIdAsync("listing-001").Returns(listing);
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(new QueryResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                new()
                {
                    { "tripId", new AttributeValue { S = "trip-001" } },
                    { "truckerId", new AttributeValue { S = "trucker-001" } },
                    { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
                    { "fromWoreda", new AttributeValue { S = tripFrom } },
                    { "toWoreda", new AttributeValue { S = "Mekelle" } },
                    { "status", new AttributeValue { S = "open" } },
                    { "pricePerQuintal", new AttributeValue { N = "120" } },
                    { "version", new AttributeValue { N = "1" } }
                }
            }
        });
        SetupOrderCreateSucceeds();
    }

    private async Task<string> CreateOrderOnTripAsync(SyncFunction function)
    {
        var op = MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }
        });
        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    [Fact]
    public async Task OrderCreate_OnATripThatDoesNotStartWhereTheListingIs_IsAccepted_AndFlaggedOnTheBuyer()
    {
        // BUG-7: a Debre Birhan crop booked on a Dire Dawa to Mekelle trip.
        GivenListingAndTrip("Debre Birhan", "Debre Birhan market", tripFrom: "Dire Dawa");

        var body = await CreateOrderOnTripAsync(AbuseFlagFunction());

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync(
            "buyer-001", "ORDER_ROUTE_MISMATCH", "order-001", "order",
            Arg.Is<string>(d => d.Contains("Dire Dawa") && d.Contains("Debre Birhan")));
        Assert.Equal(0, FraudDetectionService.SignalWeight("ORDER_ROUTE_MISMATCH"));
    }

    [Theory]
    [InlineData("Debre Birhan", null, "Debre Birhan")]
    [InlineData("Debre Birhan", null, "  debre   BIRHAN ")] // typed differently, the same place
    [InlineData("Basona Werana", "Debre Birhan", "Debre Birhan")] // the trip names the listing's place
    [InlineData(null, null, "Dire Dawa")] // a listing without a place name is never judged
    public async Task OrderCreate_OnATripFromTheListingsPlace_WritesNoFlag(string? woreda, string? place, string tripFrom)
    {
        GivenListingAndTrip(woreda, place, tripFrom);

        var body = await CreateOrderOnTripAsync(AbuseFlagFunction());

        Assert.Contains("\"status\":\"success\"", body);
        await NoFlagAsync("ORDER_ROUTE_MISMATCH");
    }

    [Fact]
    public async Task OrderCreate_WithoutATrip_WritesNoRouteFlag()
    {
        // A self-pickup order has no trip to compare.
        GivenListingAndTrip("Debre Birhan", null, tripFrom: "Dire Dawa");

        var response = await AbuseFlagFunction().FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderCreateOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await NoFlagAsync("ORDER_ROUTE_MISMATCH");
    }

    [Fact]
    public async Task OrderCreate_ThatIsRefused_WritesNoRouteFlag()
    {
        GivenListingAndTrip("Debre Birhan", null, tripFrom: "Dire Dawa");
        _mockCropRepo.GetByIdAsync("listing-001").Returns((Dictionary<string, object>?)null);

        var body = await CreateOrderOnTripAsync(AbuseFlagFunction());

        Assert.Contains("Listing not found.", body);
        await NoFlagAsync("ORDER_ROUTE_MISMATCH");
    }

    [Theory]
    [InlineData("Dire Dawa", "Debre Birhan", null, true)]
    [InlineData("Dire Dawa", "Debre Birhan", "Debre Birhan market", true)]
    [InlineData("Dire Dawa", null, "Debre Birhan", true)]
    [InlineData("debre birhan", "Debre Birhan", null, false)]
    [InlineData("Debre  Birhan", " Debre Birhan ", null, false)]
    [InlineData("Debre Birhan", "Basona Werana", "DEBRE BIRHAN", false)]
    [InlineData("", "Debre Birhan", null, false)]
    [InlineData("Dire Dawa", "", " ", false)]
    [InlineData("Dire Dawa", null, null, false)]
    public void RouteMismatch_ComparesNamesAsPeopleTypeThem(string? tripFrom, string? woreda, string? place, bool mismatch)
        => Assert.Equal(mismatch, SyncFunction.IsRouteMismatch(tripFrom, woreda, place));
}
