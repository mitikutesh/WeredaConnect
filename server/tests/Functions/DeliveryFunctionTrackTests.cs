using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-06: GET /deliveries/{id}/track (live-trip-api.md section 2). Each sharing state, each party and
/// an outsider, the window edges (pickup and delivery), rounding, the unnamed other stops, the summary
/// after delivery, and that the read writes nothing. The arrival maths are in TripTrackTests.
/// </summary>
public class DeliveryFunctionTrackTests
{
    private const string DeliveryId = "del-101";
    private const string TripId = "trip-101";
    private const string ListingId = "listing-101";
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string TruckerId = "trucker-789";
    private const string OutsiderId = "someone-else";
    private const string PickedUpAt = "2026-10-27T05:05:00.0000000Z";
    private const string NotFoundBody = "{\"error\":{\"code\":\"DELIVERY_NOT_FOUND\",\"message\":\"Delivery not found.\"}}";

    private static readonly DateTime Now = new(2026, 10, 27, 9, 10, 0, DateTimeKind.Utc);
    private static readonly DateTime Pickup = new(2026, 10, 27, 5, 5, 0, DateTimeKind.Utc);

    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITripLocationRepository _locations = Substitute.For<ITripLocationRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly DeliveryFunction _function;

    public DeliveryFunctionTrackTests()
    {
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("inTransit"));
        TripDeliveries(Delivery("inTransit"));
        _locations.ListByTripAsync(TripId).Returns(new List<TripLocationRow>
        {
            Fix(At(6, 0), 8.3, 38.85),
            Fix(At(7, 0), 8.5, 38.9),
            Fix(At(8, 42), 8.7523, 38.9786)
        });
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object>
        {
            ["tripId"] = TripId,
            ["destinationLatitude"] = "9.03",
            ["destinationLongitude"] = "38.74"
        });
        _crops.GetByIdAsync(ListingId).Returns(Listing("8.1504", "38.8196"));
        _function = new DeliveryFunction(_deliveries, null, null, _locations, _trips, _crops) { Clock = () => Now };
    }

    // ===== Who may read it =====

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public async Task EachPartyOfTheDelivery_GetsTheTrack(string caller)
    {
        var (status, track) = await TrackAsync(caller);

        status.Should().Be(200);
        track.GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
        track.GetProperty("sharing").GetString().Should().Be("live");
        track.GetProperty("trail").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task AnOutsider_GetsTheSame404AsAnUnknownDelivery_AndNothingIsRead()
    {
        var outsider = await _function.FunctionHandler(Request(OutsiderId), _context);
        var unknown = await _function.FunctionHandler(Request(BuyerId, "del-unknown"), _context);

        outsider.StatusCode.Should().Be(404);
        outsider.Body.Should().Be(NotFoundBody);
        unknown.StatusCode.Should().Be(404);
        unknown.Body.Should().Be(NotFoundBody);
        await _locations.DidNotReceiveWithAnyArgs().ListByTripAsync(default!);
        await _trips.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await _crops.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
    }

    [Fact]
    public async Task ASoftDeletedDelivery_Is404()
    {
        var deleted = Delivery("inTransit");
        deleted["deletedAt"] = "2026-10-27T08:00:00Z";
        _deliveries.GetByIdAsync(DeliveryId).Returns(deleted);

        var response = await _function.FunctionHandler(Request(BuyerId), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Be(NotFoundBody);
    }

    [Fact]
    public async Task WithoutAToken_Is401()
    {
        var request = Request(BuyerId);
        request.RequestContext = null;

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(401);
    }

    // ===== Sharing states =====

    [Fact]
    public async Task NotStarted_BeforePickup_HasNoPosition_AndThePickupIsTheListing()
    {
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("pending", pickedUpAt: null));

        var (status, track) = await TrackAsync(BuyerId);

        status.Should().Be(200);
        track.GetProperty("sharing").GetString().Should().Be("not_started");
        IsNull(track, "lastFix").Should().BeTrue();
        track.GetProperty("trail").GetArrayLength().Should().Be(0);
        track.GetProperty("pauses").GetArrayLength().Should().Be(0);
        track.GetProperty("otherStops").GetArrayLength().Should().Be(0);
        IsNull(track, "eta").Should().BeTrue();
        IsNull(track, "remainingKm").Should().BeTrue();
        IsNull(track, "summary").Should().BeTrue();
        IsNull(track, "pausedUntil").Should().BeTrue();
        var pickup = track.GetProperty("pickup");
        pickup.GetProperty("latitude").GetDouble().Should().Be(8.15);
        pickup.GetProperty("longitude").GetDouble().Should().Be(38.82);
        IsNull(pickup, "at").Should().BeTrue();
        track.GetProperty("dropoff").GetProperty("latitude").GetDouble().Should().Be(9.03);
        track.GetProperty("dropoff").GetProperty("longitude").GetDouble().Should().Be(38.74);
        IsNull(track.GetProperty("dropoff"), "note").Should().BeTrue();
        // Nothing of the truck is read before pickup.
        await _locations.DidNotReceiveWithAnyArgs().ListByTripAsync(default!);
    }

    [Fact]
    public async Task PickedUpWithoutAPickupTime_IsNotStarted()
    {
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("pickedUp", pickedUpAt: null));

        var (_, track) = await TrackAsync(FarmerId);

        track.GetProperty("sharing").GetString().Should().Be("not_started");
        track.GetProperty("trail").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Live_ShowsTheWindow_TheLastFix_AndTheArrivalWindow()
    {
        var (status, track) = await TrackAsync(BuyerId);

        status.Should().Be(200);
        track.GetProperty("sharing").GetString().Should().Be("live");
        IsNull(track, "pausedUntil").Should().BeTrue();
        track.GetProperty("asOf").GetString().Should().Be("2026-10-27T09:10:00.000Z");
        var last = track.GetProperty("lastFix");
        last.GetProperty("latitude").GetDouble().Should().Be(8.752);
        last.GetProperty("longitude").GetDouble().Should().Be(38.979);
        last.GetProperty("capturedAt").GetString().Should().Be("2026-10-27T08:42:00.000Z");
        last.GetProperty("receivedAt").GetString().Should().Be("2026-10-27T08:42:03.000Z");
        last.GetProperty("accuracyM").GetDouble().Should().Be(35);
        var trail = track.GetProperty("trail");
        trail[0].GetProperty("capturedAt").GetString().Should().Be("2026-10-27T06:00:00.000Z");
        trail[0].GetProperty("source").GetString().Should().Be("hourly");
        trail[2].GetProperty("latitude").GetDouble().Should().Be(8.752);
        track.GetProperty("pickup").GetProperty("at").GetString().Should().Be("2026-10-27T05:05:00.000Z");
        IsNull(track, "summary").Should().BeTrue();

        // The same maths the helper tests pin down, from the same rows.
        var fixes = new List<TrackFix>
        {
            new(At(6, 0), 8.3, 38.85, "", 35, "hourly"),
            new(At(7, 0), 8.5, 38.9, "", 35, "hourly"),
            new(At(8, 42), 8.7523, 38.9786, "", 35, "hourly")
        };
        var expected = TripTrack.ArrivalWindow(fixes, new TrackPoint(9.03, 38.74), new List<TrackStop>(), Now, null)!;
        track.GetProperty("eta").GetProperty("from").GetString().Should().Be(LocationPingRules.FormatUtc(expected.FromUtc));
        track.GetProperty("eta").GetProperty("to").GetString().Should().Be(LocationPingRules.FormatUtc(expected.ToUtc));
        track.GetProperty("remainingKm").GetDouble().Should().Be(Math.Round(expected.RemainingKm, 1, MidpointRounding.AwayFromZero));
        DateTime.Parse(track.GetProperty("eta").GetProperty("from").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal).Should().BeOnOrAfter(Now);
    }

    [Fact]
    public async Task Paused_WhileThePauseRuns_SaysUntilWhen()
    {
        Rows(Fix(At(7, 50), 8.5, 38.9), Event("paused", At(7, 55), pausedUntil: At(9, 40)));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("paused");
        track.GetProperty("pausedUntil").GetString().Should().Be("2026-10-27T09:40:00.000Z");
        Pauses(track).Should().Equal(("2026-10-27T07:55:00.000Z", "2026-10-27T09:40:00.000Z"));
        // The truck stands still until the pause ends, so the window starts after it.
        var from = DateTime.Parse(track.GetProperty("eta").GetProperty("from").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        from.Should().BeAfter(At(9, 40));
    }

    [Fact]
    public async Task Paused_ThatRanOut_IsLive()
    {
        Rows(Fix(At(7, 50), 8.5, 38.9), Event("paused", At(7, 55), pausedUntil: At(8, 25)));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("live");
        IsNull(track, "pausedUntil").Should().BeTrue();
        Pauses(track).Should().Equal(("2026-10-27T07:55:00.000Z", "2026-10-27T08:25:00.000Z"));
    }

    [Fact]
    public async Task Resumed_EndsThePauseEarly_AndIsLive()
    {
        Rows(
            Fix(At(7, 50), 8.5, 38.9),
            Event("paused", At(7, 55), pausedUntil: At(9, 40)),
            Event("resumed", At(8, 10)));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("live");
        Pauses(track).Should().Equal(("2026-10-27T07:55:00.000Z", "2026-10-27T08:10:00.000Z"));
    }

    [Fact]
    public async Task Declined_WithNoFix_HasNoPositionAndNoArrival()
    {
        Rows(Event("declined", At(5, 6)));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("declined");
        IsNull(track, "lastFix").Should().BeTrue();
        IsNull(track, "eta").Should().BeTrue();
        IsNull(track, "remainingKm").Should().BeTrue();
        // The other fields of a running trip are still there.
        track.GetProperty("pickup").GetProperty("latitude").GetDouble().Should().Be(8.15);
    }

    [Fact]
    public async Task Declined_WithAScanFix_StillEstimates()
    {
        Rows(Event("declined", At(5, 6)), Fix(At(6, 30), 8.4, 38.88, source: "scan"));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("declined");
        track.GetProperty("lastFix").GetProperty("latitude").GetDouble().Should().Be(8.4);
        IsNull(track, "eta").Should().BeFalse();
        IsNull(track, "remainingKm").Should().BeFalse();
    }

    [Fact]
    public async Task EventsBeforeThisPickup_StillSetTheState_AndPausesAreCutAtPickup()
    {
        // The driver answers the consent once per trip, at the first pickup; this delivery came later.
        Rows(Event("paused", At(4, 50), pausedUntil: At(5, 35)), Event("declined", At(5, 40)));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("declined");
        Pauses(track).Should().Equal(("2026-10-27T05:05:00.000Z", "2026-10-27T05:35:00.000Z"));
    }

    [Fact]
    public async Task Ended_Delivered_GivesTheSummaryOnly_AndNoCoordinates()
    {
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("delivered", confirmedAt: "2026-10-27T10:35:00.0000000Z"));
        Rows(
            Fix(At(5, 0), 8.10, 38.80),
            Fix(At(5, 5), 8.15, 38.82),
            Fix(At(7, 5), 8.50, 38.90),
            Fix(At(10, 35), 9.02, 38.75),
            Fix(At(10, 35).AddMilliseconds(1), 9.03, 38.74),
            Fix(At(10, 50), 9.10, 38.70));

        var (status, track) = await TrackAsync(BuyerId);

        status.Should().Be(200);
        track.GetProperty("sharing").GetString().Should().Be("ended");
        IsNull(track, "lastFix").Should().BeTrue();
        track.GetProperty("trail").GetArrayLength().Should().Be(0);
        track.GetProperty("pauses").GetArrayLength().Should().Be(0);
        track.GetProperty("otherStops").GetArrayLength().Should().Be(0);
        IsNull(track, "eta").Should().BeTrue();
        IsNull(track, "remainingKm").Should().BeTrue();
        IsNull(track.GetProperty("pickup"), "latitude").Should().BeTrue();
        IsNull(track.GetProperty("pickup"), "longitude").Should().BeTrue();
        track.GetProperty("pickup").GetProperty("at").GetString().Should().Be("2026-10-27T05:05:00.000Z");
        IsNull(track.GetProperty("dropoff"), "latitude").Should().BeTrue();
        IsNull(track.GetProperty("dropoff"), "longitude").Should().BeTrue();

        // From pickup to delivery, both ends included: 05:05, 07:05 and 10:35.
        var summary = track.GetProperty("summary");
        summary.GetProperty("fixes").GetInt32().Should().Be(3);
        summary.GetProperty("durationMin").GetInt32().Should().Be(330);
        var km = GpsValidator.HaversineDistance(8.15, 38.82, 8.50, 38.90) + GpsValidator.HaversineDistance(8.50, 38.90, 9.02, 38.75);
        summary.GetProperty("distanceKm").GetDouble().Should().Be(Math.Round(km, 1, MidpointRounding.AwayFromZero));
        // No reads for points it would not show.
        await _trips.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await _crops.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await _deliveries.DidNotReceiveWithAnyArgs().QueryDeliveriesAsync();
    }

    [Theory]
    [InlineData("cancelled", "cancelledAt")]
    [InlineData("disputed", "confirmedAt")]
    public async Task Ended_CancelledOrDisputed_IsEndedWithASummary(string deliveryStatus, string endField)
    {
        var delivery = Delivery(deliveryStatus);
        delivery[endField] = "2026-10-27T07:05:00.0000000Z";
        _deliveries.GetByIdAsync(DeliveryId).Returns(delivery);

        var (_, track) = await TrackAsync(TruckerId);

        track.GetProperty("sharing").GetString().Should().Be("ended");
        IsNull(track, "lastFix").Should().BeTrue();
        track.GetProperty("summary").GetProperty("durationMin").GetInt32().Should().Be(120);
        // 06:00 and 07:00 are inside 05:05 to 07:05; 08:42 is after the delivery left the road.
        track.GetProperty("summary").GetProperty("fixes").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Ended_BeforeAnyPickup_HasAnEmptySummary()
    {
        var delivery = Delivery("cancelled", pickedUpAt: null);
        delivery["cancelledAt"] = "2026-10-27T04:00:00.0000000Z";
        _deliveries.GetByIdAsync(DeliveryId).Returns(delivery);

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("sharing").GetString().Should().Be("ended");
        var summary = track.GetProperty("summary");
        summary.GetProperty("distanceKm").GetDouble().Should().Be(0);
        summary.GetProperty("durationMin").GetInt32().Should().Be(0);
        summary.GetProperty("fixes").GetInt32().Should().Be(0);
        IsNull(track.GetProperty("pickup"), "at").Should().BeTrue();
    }

    // ===== The window =====

    [Fact]
    public async Task TheWindow_StartsAtPickup_ToTheMillisecond()
    {
        Rows(
            Fix(Pickup.AddMilliseconds(-1), 8.10, 38.80),
            Fix(Pickup, 8.15, 38.82),
            Fix(At(6, 0), 8.3, 38.85));

        var (_, track) = await TrackAsync(BuyerId);

        var trail = track.GetProperty("trail");
        trail.GetArrayLength().Should().Be(2);
        trail[0].GetProperty("capturedAt").GetString().Should().Be("2026-10-27T05:05:00.000Z");
        track.GetRawText().Should().NotContain("38.8,").And.NotContain("8.1,");
    }

    [Fact]
    public async Task TheWindow_EndsNow_WithTheTwoMinutePhoneClockSkew()
    {
        Rows(
            Fix(At(8, 42), 8.7523, 38.9786),
            Fix(Now.AddMinutes(2), 8.80, 38.95),
            Fix(Now.AddMinutes(2).AddMilliseconds(1), 8.81, 38.94));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("trail").GetArrayLength().Should().Be(2);
        track.GetProperty("lastFix").GetProperty("latitude").GetDouble().Should().Be(8.8);
    }

    // ===== Rounding and points =====

    [Fact]
    public async Task PartyCoordinates_AreRoundedTo3Decimals_HalfAwayFromZero()
    {
        Rows(Fix(At(8, 42), 8.7525, 38.97849));

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("lastFix").GetProperty("latitude").GetDouble().Should().Be(8.753);
        track.GetProperty("lastFix").GetProperty("longitude").GetDouble().Should().Be(38.978);
        track.GetProperty("trail")[0].GetProperty("latitude").GetDouble().Should().Be(8.753);
    }

    [Fact]
    public async Task GpsPickup_WinsOverTheListingLocation()
    {
        var delivery = Delivery("inTransit");
        delivery["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "8.16124", ["longitude"] = "38.83119" };
        _deliveries.GetByIdAsync(DeliveryId).Returns(delivery);
        TripDeliveries(delivery);

        var (_, track) = await TrackAsync(BuyerId);

        track.GetProperty("pickup").GetProperty("latitude").GetDouble().Should().Be(8.161);
        track.GetProperty("pickup").GetProperty("longitude").GetDouble().Should().Be(38.831);
        await _crops.DidNotReceive().GetByIdAsync(ListingId);
    }

    [Fact]
    public async Task AnUnknownDropoffPoint_HasNullCoordinates_AndNoArrival()
    {
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object> { ["tripId"] = TripId, ["toWoreda"] = "Addis Ababa" });

        var (_, track) = await TrackAsync(BuyerId);

        IsNull(track.GetProperty("dropoff"), "latitude").Should().BeTrue();
        IsNull(track.GetProperty("dropoff"), "longitude").Should().BeTrue();
        IsNull(track, "eta").Should().BeTrue();
        IsNull(track, "remainingKm").Should().BeTrue();
        IsNull(track, "lastFix").Should().BeFalse();
    }

    // ===== Other stops =====

    [Fact]
    public async Task OtherStops_AreTheOtherOrdersPlannedPoints_At2Decimals_WithDone_AndNeverNamed()
    {
        var pending = OtherDelivery("del-102", "pending", "listing-102");
        // Picked up before this pickup (05:05): its pickup is outside the window, its drop-off is not.
        var onTheRoad = OtherDelivery("del-103", "inTransit", "listing-103");
        onTheRoad["pickedUpAt"] = "2026-10-27T04:30:00.0000000Z";
        // Picked up and delivered inside the window, with real GPS that must never show.
        var delivered = OtherDelivery("del-105", "delivered", "listing-105");
        delivered["pickedUpAt"] = "2026-10-27T06:00:00.0000000Z";
        delivered["confirmedAt"] = "2026-10-27T07:30:00.0000000Z";
        delivered["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "8.4049", ["longitude"] = "38.8851" };
        delivered["gpsDropoff"] = new Dictionary<string, object> { ["latitude"] = "8.6012", ["longitude"] = "38.9149" };
        var cancelled = OtherDelivery("del-104", "cancelled", "listing-104");
        TripDeliveries(Delivery("inTransit"), pending, onTheRoad, delivered, cancelled);
        _crops.GetByIdAsync("listing-102").Returns(Listing("8.155", "38.825"));
        _crops.GetByIdAsync("listing-103").Returns(Listing("8.2", "38.83"));
        _crops.GetByIdAsync("listing-105").Returns(Listing("8.3549", "38.8751"));

        var (_, track) = await TrackAsync(FarmerId);

        var stops = track.GetProperty("otherStops").EnumerateArray()
            .Select(s => (
                s.GetProperty("kind").GetString(),
                s.GetProperty("latitude").GetDouble(),
                s.GetProperty("longitude").GetDouble(),
                s.GetProperty("done").GetBoolean()))
            .ToList();
        stops.Should().Equal(
            ("pickup", 8.16, 38.83, false),
            ("dropoff", 9.03, 38.74, false),
            ("dropoff", 9.03, 38.74, false),
            ("pickup", 8.35, 38.88, true),
            ("dropoff", 9.03, 38.74, true));
        foreach (var stop in track.GetProperty("otherStops").EnumerateArray())
        {
            stop.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("kind", "latitude", "longitude", "done");
        }

        var body = track.GetRawText();
        foreach (var secret in new[]
                 {
                     "del-102", "del-103", "del-104", "del-105", "listing-10", "order-", "Other Farmer", "Other Buyer",
                     "buyer-9", "quantity", "8.4", "38.89", "8.6", "38.91"
                 })
        {
            body.Should().NotContain(secret);
        }

        // The cancelled delivery's listing is never read; each listing is read once.
        await _crops.DidNotReceive().GetByIdAsync("listing-104");
        await _crops.Received(1).GetByIdAsync("listing-102");
    }

    // ===== Failures and side effects =====

    [Fact]
    public async Task AReadFailure_Is500TrackUnavailable()
    {
        _locations.ListByTripAsync(TripId).ThrowsAsync(new InvalidOperationException("boom"));

        var response = await _function.FunctionHandler(Request(BuyerId), _context);

        response.StatusCode.Should().Be(500);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("TRACK_UNAVAILABLE");
    }

    [Fact]
    public async Task WithoutTheTrackTables_Is500TrackUnavailable()
    {
        var function = new DeliveryFunction(_deliveries) { Clock = () => Now };

        var response = await function.FunctionHandler(Request(BuyerId), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("TRACK_UNAVAILABLE");
    }

    [Fact]
    public async Task TheTrack_WritesNothing()
    {
        await TrackAsync(BuyerId);

        await _locations.DidNotReceiveWithAnyArgs().TryPutAsync(default!);
        _deliveries.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Distinct()
            .Should().BeSubsetOf(new[] { "GetByIdAsync", "QueryDeliveriesAsync" });
        _trips.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Distinct().Should().Equal("GetByIdAsync");
        _crops.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Distinct().Should().Equal("GetByIdAsync");
    }

    [Fact]
    public async Task TheDeliveryRoute_WithoutTrack_StillReturnsTheDelivery()
    {
        var request = Request(BuyerId);
        request.Path = $"/deliveries/{DeliveryId}";

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("delivery").GetProperty("deliveryId").GetString()
            .Should().Be(DeliveryId);
        await _locations.DidNotReceiveWithAnyArgs().ListByTripAsync(default!);
    }

    // ===== Helpers =====

    private async Task<(int Status, JsonElement Track)> TrackAsync(string caller)
    {
        var response = await _function.FunctionHandler(Request(caller), _context);
        return (response.StatusCode, JsonDocument.Parse(response.Body).RootElement);
    }

    private static APIGatewayProxyRequest Request(string userId, string deliveryId = DeliveryId) => new()
    {
        HttpMethod = "GET",
        Path = $"/deliveries/{deliveryId}/track",
        Resource = "/deliveries/{id}/track",
        PathParameters = new Dictionary<string, string> { ["id"] = deliveryId },
        RequestContext = new ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
        }
    };

    private static DateTime At(int hour, int minute) => new(2026, 10, 27, hour, minute, 0, DateTimeKind.Utc);

    private static Dictionary<string, object> Delivery(
        string status, string? pickedUpAt = PickedUpAt, string? confirmedAt = null)
    {
        var item = new Dictionary<string, object>
        {
            ["deliveryId"] = DeliveryId,
            ["tripId"] = TripId,
            ["listingId"] = ListingId,
            ["orderId"] = "order-101",
            ["farmerId"] = FarmerId,
            ["farmerName"] = "Abebe Kebede",
            ["truckerId"] = TruckerId,
            ["truckerName"] = "Tadesse Worku",
            ["buyerId"] = BuyerId,
            ["buyerName"] = "Mekonnen Tadesse",
            ["qrToken"] = "qr-token-abc123",
            ["status"] = status,
            ["createdAt"] = "2026-10-26T10:30:00Z",
            ["updatedAt"] = "2026-10-27T05:05:00Z",
            ["version"] = "3"
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

    private static Dictionary<string, object> OtherDelivery(string deliveryId, string status, string listingId) => new()
    {
        ["deliveryId"] = deliveryId,
        ["tripId"] = TripId,
        ["listingId"] = listingId,
        ["orderId"] = "order-" + deliveryId,
        ["farmerId"] = "farmer-9",
        ["farmerName"] = "Other Farmer",
        ["truckerId"] = TruckerId,
        ["buyerId"] = "buyer-9",
        ["buyerName"] = "Other Buyer",
        ["quantity"] = "40",
        ["status"] = status,
        ["createdAt"] = "2026-10-26T11:00:00Z"
    };

    private static Dictionary<string, object> Listing(string latitude, string longitude) => new()
    {
        ["cropId"] = ListingId,
        ["latitude"] = latitude,
        ["longitude"] = longitude
    };

    private void TripDeliveries(params Dictionary<string, object>[] deliveries)
        => _deliveries.QueryDeliveriesAsync(null, TripId, null, 50, null)
            .Returns((deliveries.ToList(), (Dictionary<string, AttributeValue>?)null));

    private void Rows(params TripLocationRow[] rows) => _locations.ListByTripAsync(TripId).Returns(rows.ToList());

    private static TripLocationRow Fix(DateTime capturedAt, double latitude, double longitude, string source = "hourly") => new(
        TripId, $"{LocationPingRules.FormatUtc(capturedAt)}#fix", TruckerId, latitude, longitude, 35, source, false, null,
        LocationPingRules.FormatUtc(capturedAt.AddSeconds(3)), 0);

    private static TripLocationRow Event(string kind, DateTime capturedAt, DateTime? pausedUntil = null) => new(
        TripId, $"{LocationPingRules.FormatUtc(capturedAt)}#{kind}", TruckerId, null, null, null, null, null,
        pausedUntil is { } until ? LocationPingRules.FormatUtc(until) : null,
        LocationPingRules.FormatUtc(capturedAt.AddSeconds(3)), 0);

    private static bool IsNull(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Null;

    private static List<(string, string)> Pauses(JsonElement track)
        => track.GetProperty("pauses").EnumerateArray()
            .Select(p => (p.GetProperty("from").GetString()!, p.GetProperty("until").GetString()!))
            .ToList();
}
