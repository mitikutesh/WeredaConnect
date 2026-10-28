using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-07: the side effects of an accepted check-in (live-trip.md section 6.3, live-trip-api.md
/// sections 4 and 5). Each effect, its idempotency and its failure isolation: the system move to
/// <c>inTransit</c> with the order coupling and the audit row, the <c>nearbyNotifiedAt</c> stamp that
/// sends <c>truck_nearby</c>, and the <c>GPS_JUMP</c> and <c>GPS_MOCKED</c> signals. None of them ever
/// changes the ingest's answer.
/// </summary>
public class SyncFunctionCheckInEffectsTests
{
    private const string TruckerId = "00000000-0000-0000-0000-000000000001";
    private const string BuyerId = "00000000-0000-0000-0000-000000000003";
    private const string TripId = "00000000-0000-0000-0000-000000000005";
    private const string DeliveryId = "del-1";
    private const string OrderId = "order-1";
    private const string ListingId = "listing-1";
    private const string TripsTable = "test-trips";
    private const string DeliveriesTable = "test-deliveries";

    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Captured = new(2026, 10, 27, 8, 42, 0, DateTimeKind.Utc);

    // The farm near Meki, the buyer in Addis Ababa (the trip's destination point). The default fix is
    // on the road between them: 67 km from the farm, 33 km from the drop-off.
    private const double FarmLat = 8.1500, FarmLng = 38.8200;
    private const double DestLat = 9.0300, DestLng = 38.7400;
    private const double RoadLat = 8.7523, RoadLng = 38.9785;

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITripLocationRepository _locations = Substitute.For<ITripLocationRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly List<TripLocationRow> _stored = new();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    public SyncFunctionCheckInEffectsTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        TripWithDestination(DestLat, DestLng);
        OnTrip(Delivery("pickedUp"));
        _locations.ListSortKeysAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new List<string>());
        _locations.TryPutAsync(Arg.Do<TripLocationRow>(r => _stored.Add(r))).Returns(true);
        _locations.ListByTripAsync(TripId).Returns(_ => new List<TripLocationRow>(_stored));
        _deliveries.TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(true);
        _deliveries.TryStampNearbyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>())
            .Returns(true);
        _orders.GetByIdAsync(OrderId).Returns(new Dictionary<string, object>
        {
            ["orderId"] = OrderId,
            ["status"] = "pickup_confirmed",
            ["version"] = "3"
        });
        _orders.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>()));
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(e => _auditRows.Add(e))).Returns(Task.CompletedTask);
        _function = new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            fraudService: _fraud, orderRepo: _orders, transactionAuditRepo: _audit, deliveryRepo: _deliveries,
            tripLocationRepo: _locations)
        {
            LocationClock = () => Now
        };
    }

    // ===== auto in transit =====

    [Fact]
    public async Task AFix2KmPastThePickup_MovesTheDeliveryInTransit_WithTheOrderCouplingAndTheAudit()
    {
        var result = await SendAsync(Fix(RoadLat, RoadLng));

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
        // The order follows, exactly as for the trucker's own inTransit: pickup_confirmed -> in_transit.
        await _orders.Received(1).UpdateAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(d => d.Count == 1 && (string)d["status"] == "in_transit"), 3);

        var deliveryLeg = _auditRows.Should().ContainSingle(e => (string)e["entityType"] == "delivery").Subject;
        deliveryLeg["transactionId"].Should().Be(OrderId);
        deliveryLeg["entityId"].Should().Be(DeliveryId);
        deliveryLeg["action"].Should().Be("delivery_updated");
        deliveryLeg["fromState"].Should().Be("pickedUp");
        deliveryLeg["toState"].Should().Be("inTransit");
        deliveryLeg["actor"].Should().Be("system");
        deliveryLeg.Should().NotContainKey("ipAddress");
        ((string)deliveryLeg["metadata"]).Should().MatchRegex(@"^source=check_in;distanceKm=69\.\d$");
        var orderLeg = _auditRows.Should().ContainSingle(e => (string)e["entityType"] == "order").Subject;
        orderLeg["actor"].Should().Be("system");
        orderLeg["fromState"].Should().Be("pickup_confirmed");
        orderLeg["toState"].Should().Be("in_transit");
    }

    [Theory]
    [InlineData(FarmLat, FarmLng, false)]          // at the listing
    [InlineData(FarmLat + 0.0179, FarmLng, false)] // 1.99 km
    [InlineData(FarmLat + 0.0180, FarmLng, true)]  // 2.0 km
    public async Task WithoutAGpsPickup_TheListingLocationIsThePickup(double lat, double lng, bool moves)
    {
        OnTrip(Delivery("pickedUp", gpsPickup: false));
        _crops.GetByIdAsync(ListingId).Returns(new Dictionary<string, object>
        {
            ["cropId"] = ListingId,
            ["latitude"] = FarmLat.ToString(CultureInfo.InvariantCulture),
            ["longitude"] = FarmLng.ToString(CultureInfo.InvariantCulture)
        });

        await SendAsync(Fix(lat, lng));

        await _deliveries.Received(moves ? 1 : 0).TryMarkInTransitAsync(DeliveryId, TripId, 4);
    }

    [Fact]
    public async Task WithoutAnyPickupPoint_NothingMoves()
    {
        OnTrip(Delivery("pickedUp", gpsPickup: false));

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.DidNotReceive().TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ACheckInCapturedBeforeThePickupSynced_MovesNothing()
    {
        // Picked up (synced) at 08:50; the 08:42 check-in waited offline and is far from the farm.
        OnTrip(Delivery("pickedUp", pickedUpAt: "2026-10-27T08:50:00.0000000Z"));

        var result = await SendAsync(Fix(RoadLat, RoadLng));

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.DidNotReceive().TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("inTransit")]
    [InlineData("pending")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task OnlyAPickedUpDelivery_IsMoved(string status)
    {
        // Another delivery of the trip is on the road, so the check-in is accepted.
        OnTrip(Delivery(status), Delivery("inTransit", deliveryId: "del-2"));

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.DidNotReceive().TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task TheMoveIsConditionalOnTheFreshRead_NotTheIndexRow()
    {
        // The trip's deliveries come from an index that can lag: version 3 there, 4 on the table.
        var stale = Delivery("pickedUp");
        stale["version"] = "3";
        _deliveries.QueryDeliveriesAsync(
                Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { stale }, (Dictionary<string, AttributeValue>?)null));

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
    }

    [Fact]
    public async Task AFreshReadShowingTheDeliveryMovedOn_IsLeftAlone()
    {
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("inTransit"));

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.DidNotReceive().TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Idempotent_AMoveThatLostItsCondition_TouchesNeitherTheOrderNorTheAudit()
    {
        // The trucker's own button (or another check-in) moved it between the read and the write.
        _deliveries.TryMarkInTransitAsync(DeliveryId, TripId, 4).Returns(false);

        var result = await SendAsync(Fix(RoadLat, RoadLng));

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderAlreadyInTransit_IsNeverMovedBack()
    {
        _orders.GetByIdAsync(OrderId).Returns(new Dictionary<string, object>
        {
            ["orderId"] = OrderId,
            ["status"] = "in_transit",
            ["version"] = "5"
        });

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ===== truck nearby =====

    [Fact]
    public async Task AFixWithin5KmOfTheDropoff_StampsTheDelivery_WithTheDistanceAndTheMinutes()
    {
        OnTrip(Delivery("inTransit"));
        // An hour earlier at the same spot: the truck crawls, so the speed is the 20 km/h floor.
        StoredFix(Now.AddHours(-1), DestLat + 0.0441, DestLng);

        var result = await SendAsync(Fix(DestLat + 0.0441, DestLng, capturedAt: Now));

        result.GetProperty("status").GetString().Should().Be("success");
        // 4.9 km, 6.4 km of road at 20 km/h: the window opens at 09:10, 10 minutes from now.
        await _deliveries.Received(1).TryStampNearbyAsync(DeliveryId, TripId, "2026-10-27T09:00:00.000Z", 4.9, 10);
    }

    [Fact]
    public async Task AWindowThatOpensNow_IsStampedWithZeroMinutes()
    {
        OnTrip(Delivery("inTransit"));

        await SendAsync(Fix(DestLat + 0.02, DestLng));

        await _deliveries.Received(1).TryStampNearbyAsync(DeliveryId, TripId, "2026-10-27T09:00:00.000Z", 2.2, 0);
    }

    [Fact]
    public async Task APickedUpDeliveryNearItsDropoff_IsMovedAndStamped()
    {
        // A short trip: the farm is 1 km from the drop-off, the truck 3 km past both.
        var delivery = Delivery("pickedUp");
        delivery["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "9.0210", ["longitude"] = "38.7400" };
        OnTrip(delivery);

        await SendAsync(Fix(DestLat + 0.0270, DestLng));

        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
        await _deliveries.Received(1).TryStampNearbyAsync(DeliveryId, TripId, Arg.Any<string>(), 3.0, Arg.Any<int?>());
    }

    [Fact]
    public async Task FartherThan5Km_NoStamp()
    {
        OnTrip(Delivery("inTransit"));

        await SendAsync(Fix(DestLat + 0.0450, DestLng));

        await NoStamp();
    }

    [Fact]
    public async Task Idempotent_AnAlreadyStampedDelivery_IsNotStampedAgain()
    {
        var delivery = Delivery("inTransit");
        delivery["nearbyNotifiedAt"] = "2026-10-27T08:30:00.000Z";
        OnTrip(delivery);

        await SendAsync(Fix(DestLat + 0.01, DestLng));

        await NoStamp();
    }

    [Fact]
    public async Task Idempotent_AStampThatLostItsCondition_IsQuiet()
    {
        // Another check-in stamped it first: the condition fails and nothing else happens.
        OnTrip(Delivery("inTransit"));
        _deliveries.TryStampNearbyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>())
            .Returns(false);

        var result = await SendAsync(Fix(DestLat + 0.01, DestLng));

        result.GetProperty("status").GetString().Should().Be("success");
        Log().Should().NotContain("CHECKIN_TRUCK_NEARBY");
        Log().Should().NotContain("CHECKIN_EFFECT_FAILED");
    }

    [Fact]
    public async Task WithoutADropoffPoint_NoStamp()
    {
        OnTrip(Delivery("inTransit"));
        TripWithDestination(null, null);

        await SendAsync(Fix(DestLat, DestLng));

        await NoStamp();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("cancelled")]
    public async Task OnlyADeliveryOnTheRoad_IsStamped(string status)
    {
        // The check-in is accepted for another delivery on the road, which was told already.
        var told = Delivery("inTransit", deliveryId: "del-2");
        told["nearbyNotifiedAt"] = "2026-10-27T08:30:00.000Z";
        OnTrip(Delivery(status), told);

        await SendAsync(Fix(DestLat, DestLng));

        await NoStamp();
    }

    // ===== fraud signals =====

    [Fact]
    public async Task AMockedFix_RaisesGpsMocked_OnTheTrucker_WithTheTripAsTheEntity()
    {
        await SendAsync(Fix(RoadLat, RoadLng, mocked: true));

        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_MOCKED", TripId, "trip", Arg.Any<string?>());
    }

    [Fact]
    public async Task AFixNotFlaggedAsMocked_RaisesNoGpsMocked()
    {
        await SendAsync(Fix(RoadLat, RoadLng, mocked: false));

        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "GPS_MOCKED", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnImpossibleJumpFromThePreviousFix_RaisesGpsJump()
    {
        // 55.6 km in 12 minutes: 278 km/h.
        StoredFix(Captured.AddMinutes(-12), RoadLat - 0.5, RoadLng);

        var result = await SendAsync(Fix(RoadLat, RoadLng));

        result.GetProperty("status").GetString().Should().Be("success");
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_JUMP", TripId, "trip",
            Arg.Is<string?>(d => d!.Contains("55.6 km") && d.Contains("278 km/h")));
    }

    [Fact]
    public async Task ALateFixBetweenTwoStoredOnes_IsCheckedAgainstTheOneAfterIt()
    {
        StoredFix(Captured.AddHours(-1), RoadLat - 0.3, RoadLng); // 33 km an hour before: fine
        StoredFix(Captured.AddMinutes(5), RoadLat + 0.3, RoadLng); // 33 km five minutes after: a jump

        await SendAsync(Fix(RoadLat, RoadLng));

        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_JUMP", TripId, "trip", Arg.Any<string?>());
    }

    [Theory]
    [InlineData(3600, 0.5)] // 55.6 km in an hour
    [InlineData(10, 0.008)] // 0.9 km in 10 seconds (320 km/h): GPS noise, under 1 km
    public async Task NoJump_AtATruckSpeedOrWithin1Km(int secondsBefore, double degreesSouth)
    {
        StoredFix(Captured.AddSeconds(-secondsBefore), RoadLat - degreesSouth, RoadLng);

        await SendAsync(Fix(RoadLat, RoadLng));

        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "GPS_JUMP", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // ===== accuracy =====

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    [InlineData(5000, false)]
    public async Task OnlyAFixWithin1KmOfAccuracy_MovesStampsOrJumps_AMockFlagCountsAnyway(double accuracyM, bool effects)
    {
        // 1.1 km from the drop-off, 99 km past the farm, 41 km from a check-in 12 minutes earlier (over 200 km/h).
        StoredFix(Captured.AddMinutes(-12), RoadLat, RoadLng);

        var result = await SendAsync(Fix(DestLat + 0.01, DestLng, mocked: true, accuracyM: accuracyM));

        result.GetProperty("status").GetString().Should().Be("success");
        var times = effects ? 1 : 0;
        await _deliveries.Received(times).TryMarkInTransitAsync(DeliveryId, TripId, 4);
        await _deliveries.Received(times).TryStampNearbyAsync(
            DeliveryId, TripId, Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>());
        await _fraud.Received(times).RecordFraudSignalAsync(TruckerId, "GPS_JUMP", TripId, "trip", Arg.Any<string?>());
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_MOCKED", TripId, "trip", Arg.Any<string?>());
    }

    [Fact]
    public async Task ACoarseStoredFix_IsNoJumpNeighbour_ThePreciseOneBeforeItIs()
    {
        StoredFix(Captured.AddHours(-1), RoadLat - 0.3, RoadLng);                         // 33 km an hour before: fine
        StoredFix(Captured.AddMinutes(-1), RoadLat - 0.03, RoadLng, accuracyM: 3000);     // 3.3 km a minute before, a cell tower guess

        await SendAsync(Fix(RoadLat, RoadLng));

        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "GPS_JUMP", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // ===== scope =====

    [Theory]
    [InlineData("paused")]
    [InlineData("resumed")]
    [InlineData("declined")]
    public async Task ASharingEvent_HasNoEffects(string kind)
    {
        OnTrip(Delivery("pickedUp"));
        var op = Ping(kind, data: d =>
        {
            if (kind == "paused")
            {
                d["pausedUntil"] = "2026-10-27T09:30:00Z";
            }
        });

        var result = await SendAsync(op);

        result.GetProperty("status").GetString().Should().Be("success");
        await NoEffectsAtAll();
    }

    [Fact]
    public async Task AnIdempotentRepeat_HasNoEffects()
    {
        _locations.TryPutAsync(Arg.Any<TripLocationRow>()).Returns(false);

        var result = await SendAsync(Fix(RoadLat, RoadLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("message").GetString().Should().Be("Location already stored.");
        await NoEffectsAtAll();
    }

    [Fact]
    public async Task ARefusedCheckIn_HasNoEffects()
    {
        var result = await SendAsync(Fix(RoadLat, RoadLng, mocked: true), userId: BuyerId);

        result.GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_TRUCKER");
        await NoEffectsAtAll();
    }

    [Fact]
    public async Task ACheckIn_NeverWritesTheTripRow_OrAnyMoney()
    {
        await SendAsync(Fix(RoadLat, RoadLng, mocked: true));

        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
        await _dynamoDb.DidNotReceive().TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>());
    }

    // ===== failure isolation =====

    [Fact]
    public async Task AFailingMove_FailsNeitherTheIngestNorTheOtherEffects()
    {
        var delivery = Delivery("pickedUp");
        delivery["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "8.9000", ["longitude"] = "38.7400" };
        OnTrip(delivery);
        _deliveries.GetByIdAsync(DeliveryId, true).ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var result = await SendAsync(Fix(DestLat + 0.01, DestLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(1);
        await _deliveries.Received(1).TryStampNearbyAsync(DeliveryId, TripId, Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>());
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_MOCKED", TripId, "trip", Arg.Any<string?>());
        Log().Should().Contain("CHECKIN_EFFECT_FAILED effect=auto_in_transit");
    }

    [Fact]
    public async Task AFailingStamp_FailsNeitherTheIngestNorTheSignals()
    {
        OnTrip(Delivery("inTransit"));
        _deliveries.TryStampNearbyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var result = await SendAsync(Fix(DestLat + 0.01, DestLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_MOCKED", TripId, "trip", Arg.Any<string?>());
        Log().Should().Contain("CHECKIN_EFFECT_FAILED effect=truck_nearby");
    }

    [Fact]
    public async Task AFailingTrailRead_StillMovesAndStamps_WithoutMinutes()
    {
        var delivery = Delivery("pickedUp");
        delivery["gpsPickup"] = new Dictionary<string, object> { ["latitude"] = "8.9000", ["longitude"] = "38.7400" };
        OnTrip(delivery);
        _locations.ListByTripAsync(TripId).ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var result = await SendAsync(Fix(DestLat + 0.01, DestLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
        await _deliveries.Received(1).TryStampNearbyAsync(DeliveryId, TripId, Arg.Any<string>(), 1.1, null);
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "GPS_MOCKED", TripId, "trip", Arg.Any<string?>());
        Log().Should().Contain("CHECKIN_EFFECT_FAILED effect=fraud_signals");
    }

    [Fact]
    public async Task AFailingSignalWrite_FailsNothing()
    {
        _fraud.RecordFraudSignalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var result = await SendAsync(Fix(RoadLat, RoadLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.Received(1).TryMarkInTransitAsync(DeliveryId, TripId, 4);
    }

    [Fact]
    public async Task OneDeliveryFailing_DoesNotStopTheNextOne()
    {
        var first = Delivery("pickedUp", deliveryId: "del-a");
        var second = Delivery("pickedUp", deliveryId: "del-b");
        OnTrip(first, second);
        _deliveries.GetByIdAsync("del-a", true).ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        await SendAsync(Fix(RoadLat, RoadLng));

        await _deliveries.Received(1).TryMarkInTransitAsync("del-b", TripId, 4);
    }

    [Fact]
    public async Task EverythingFailing_TheAnswerIsStillTheStoredCheckIn()
    {
        OnTrip(Delivery("pickedUp"));
        _deliveries.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>()).ThrowsAsync(new InvalidOperationException("x"));
        _deliveries.TryStampNearbyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>())
            .ThrowsAsync(new InvalidOperationException("x"));
        _locations.ListByTripAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("x"));
        _crops.GetByIdAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("x"));

        var result = await SendAsync(Fix(DestLat, DestLng, mocked: true));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(1);
        _stored.Should().ContainSingle();
        await _syncMeta.Received(1).StoreIdempotencyKeyAsync(Arg.Any<string>(), "op-ping", "location_ping", "success");
    }

    // ===== the trucker's own button after the move =====

    [Fact]
    public async Task TheTruckersOwnInTransit_AfterACheckInMovedIt_IsAnsweredWithTheRow()
    {
        InTransitDeliveryOnTable();

        var result = await SendAsync(TruckerInTransitOp());

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("inTransit");
        result.GetProperty("serverData").TryGetProperty("truckerPhone", out _).Should().BeFalse();
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SomeoneElsesInTransit_IsStillAnInvalidTransition()
    {
        InTransitDeliveryOnTable();

        var result = await SendAsync(TruckerInTransitOp(), userId: BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_INVALID_TRANSITION");
        result.TryGetProperty("serverData", out var data).Should().BeFalse(data.ToString());
    }

    private void InTransitDeliveryOnTable()
        => _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable
                && r.ExpressionAttributeValues.ContainsKey(":id")
                && r.ExpressionAttributeValues[":id"].S == DeliveryId))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["deliveryId"] = new() { S = DeliveryId },
                        ["tripId"] = new() { S = TripId },
                        ["orderId"] = new() { S = OrderId },
                        ["truckerId"] = new() { S = TruckerId },
                        ["truckerPhone"] = new() { S = "+251911000002" },
                        ["buyerId"] = new() { S = BuyerId },
                        ["status"] = new() { S = "inTransit" },
                        ["createdAt"] = new() { S = "2026-10-27T04:00:00Z" },
                        ["version"] = new() { N = "5" }
                    }
                }
            });

    private static SyncOperationRequest TruckerInTransitOp() => new()
    {
        OperationId = "op-transit",
        IdempotencyKey = $"{TruckerId}:delivery:{DeliveryId}:update:1793090520000",
        EntityType = "delivery",
        EntityId = DeliveryId,
        Action = "update",
        ClientVersion = 4,
        Data = new Dictionary<string, object> { ["status"] = "inTransit", ["version"] = 4 },
        Timestamp = "2026-10-27T08:45:00.000Z"
    };

    // ===== helpers =====

    private Task NoStamp()
        => _deliveries.DidNotReceive().TryStampNearbyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<int?>());

    private async Task NoEffectsAtAll()
    {
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _deliveries.DidNotReceive().TryMarkInTransitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
        await NoStamp();
        await _locations.DidNotReceive().ListByTripAsync(Arg.Any<string>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        _auditRows.Should().BeEmpty();
    }

    private string Log() => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private void TripWithDestination(double? lat, double? lng)
    {
        var trip = new Dictionary<string, AttributeValue>
        {
            ["tripId"] = new() { S = TripId },
            ["truckerId"] = new() { S = TruckerId },
            ["status"] = new() { S = "inTransit" }
        };
        if (lat is { } la && lng is { } ln)
        {
            trip["destinationLatitude"] = new() { N = la.ToString(CultureInfo.InvariantCulture) };
            trip["destinationLongitude"] = new() { N = ln.ToString(CultureInfo.InvariantCulture) };
        }

        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { trip } });
    }

    /// <summary>The trip's deliveries (index rows, version 4) and the same rows on a fresh read.</summary>
    private void OnTrip(params Dictionary<string, object>[] rows)
    {
        _deliveries.QueryDeliveriesAsync(
                Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((rows.ToList(), (Dictionary<string, AttributeValue>?)null));
        foreach (var row in rows)
        {
            _deliveries.GetByIdAsync((string)row["deliveryId"], true).Returns(new Dictionary<string, object>(row));
        }
    }

    /// <summary>A delivery row as the repository returns it (strings, gpsPickup as a map).</summary>
    private static Dictionary<string, object> Delivery(
        string status, string pickedUpAt = "2026-10-27T05:00:00.0000000Z", bool gpsPickup = true, string deliveryId = DeliveryId)
    {
        var row = new Dictionary<string, object>
        {
            ["deliveryId"] = deliveryId,
            ["tripId"] = TripId,
            ["orderId"] = OrderId,
            ["listingId"] = ListingId,
            ["truckerId"] = TruckerId,
            ["buyerId"] = BuyerId,
            ["status"] = status,
            ["version"] = "4",
            ["createdAt"] = "2026-10-27T04:00:00Z"
        };
        if (status is not ("pending" or "cancelled"))
        {
            row["pickedUpAt"] = pickedUpAt;
        }

        if (status == "delivered")
        {
            row["confirmedAt"] = "2026-10-27T09:30:00Z";
        }

        if (gpsPickup)
        {
            row["gpsPickup"] = new Dictionary<string, object>
            {
                ["latitude"] = FarmLat.ToString(CultureInfo.InvariantCulture),
                ["longitude"] = FarmLng.ToString(CultureInfo.InvariantCulture)
            };
        }

        return row;
    }

    private void StoredFix(DateTime capturedAt, double lat, double lng, double accuracyM = 20)
        => _stored.Add(new TripLocationRow(
            TripId, $"{capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}#fix", TruckerId,
            Math.Round(lat, 4), Math.Round(lng, 4), accuracyM, "hourly", false, null, "2026-10-27T08:00:00.000Z", 0));

    private static SyncOperationRequest Fix(
        double lat, double lng, bool mocked = false, DateTime? capturedAt = null, double accuracyM = 20)
        => Ping("fix", capturedAt, data: d =>
        {
            d["latitude"] = lat;
            d["longitude"] = lng;
            d["mocked"] = mocked;
            d["accuracyM"] = accuracyM;
        });

    private static SyncOperationRequest Ping(string kind, DateTime? capturedAt = null, Action<Dictionary<string, object>>? data = null)
    {
        var captured = capturedAt ?? Captured;
        var ms = new DateTimeOffset(captured).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var payload = new Dictionary<string, object>
        {
            ["tripId"] = TripId,
            ["kind"] = kind,
            ["capturedAt"] = captured.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        };
        if (kind == "fix")
        {
            payload["source"] = "hourly";
            payload["accuracyM"] = 20;
        }

        data?.Invoke(payload);
        var entityId = $"{TripId}#{ms}";
        return new SyncOperationRequest
        {
            OperationId = "op-ping",
            IdempotencyKey = $"{TruckerId}:location_ping:{entityId}:create:{ms}",
            EntityType = "location_ping",
            EntityId = entityId,
            Action = "create",
            ClientVersion = 1,
            Data = payload,
            Timestamp = "2026-10-27T08:42:00.000Z"
        };
    }

    private async Task<JsonElement> SendAsync(SyncOperationRequest operation, string userId = TruckerId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-lt-07",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", "trucker" } }
            }
        };

        var response = await _function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
