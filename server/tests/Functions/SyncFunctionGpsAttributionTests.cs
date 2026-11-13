using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-33 and FUP-203 (fraud-detection-architecture.md Rule 4 and sections 14.3 and 15.3): the <c>GPS_ANOMALY</c>
/// of a confirmed delivery. The confirming buyer's own row is judged from the coordinates their phone sends, as
/// before ESC-33 (written after the delivery write succeeded). The trucker's row, only with
/// <c>reputationPenalties</c> on, is judged from the trucker's own check-ins (the trip-locations window from the
/// pickup to the confirmation), never from the buyer's coordinates, and goes through the attribution guard; the
/// trucker is the stored row's, never an id from the request. Uses the delivery rig of the FRD-09 delivery
/// pattern tests. Picked up near Jimma, trip destination in Addis Ababa (about 259 km) unless a test says else.
/// </summary>
public partial class SyncFunctionTests
{
    private const string GpsWhy = "Pickup and dropoff GPS coordinates are identical.";

    private static readonly TrackPoint GpsJimma = new(7.67, 36.83);
    private static readonly TrackPoint GpsAddis = new(9.03, 38.74);

    private readonly ITripLocationRepository _truckerCheckIns = Substitute.For<ITripLocationRepository>();

    private SyncFunction GpsAnomalyFunction(bool? onTrucker = true)
    {
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Allowed());
        // The F-14 GPS check reads the delivery's trip for its woredas: none on record here.
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        // No check-in of the trucker unless a test says else.
        GivenTruckerCheckIns();
        var patterns = new TransactionPatternService(_mockOrderRepo, null, TransactionPatternSettings.Default);
        // null: the flag as the Lambda reads it from its environment (unset in tests, as in every environment).
        var function = onTrucker is { } flag
            ? new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
                fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo,
                tripLocationRepo: _truckerCheckIns)
            { PatternRules = patterns, GpsAnomalyOnTrucker = flag }
            : new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
                fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo,
                tripLocationRepo: _truckerCheckIns)
            { PatternRules = patterns };
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Flagged("GPS_ANOMALY", GpsWhy));
        _patternFraud.RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(AttributedSignalOutcome.Written);
        return function;
    }

    /// <summary>The buyer's Rule 4 as built (the fraud service is a substitute here), not a canned answer.</summary>
    private void WithTheRealBuyerRule4()
        => _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(ci => FraudDetectionService.GpsConsistency(
                ci.ArgAt<double?>(0), ci.ArgAt<double?>(1), ci.ArgAt<double?>(2), ci.ArgAt<double?>(3),
                ci.ArgAt<string>(4), ci.ArgAt<string>(5)));

    /// <summary>The delivery's trip: an inter-woreda trip with its destination point.</summary>
    private void GivenGpsTrip(TrackPoint? destination)
    {
        var trip = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "fromWoreda", new AttributeValue { S = "Jimma" } },
            { "toWoreda", new AttributeValue { S = "Addis Ketema" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:00:00Z" } }
        };
        // A trip created before trip-listing-api 2.1.0 has no destination point.
        if (destination != null)
        {
            trip["destinationLatitude"] = new AttributeValue { N = destination.Latitude.ToString(CultureInfo.InvariantCulture) };
            trip["destinationLongitude"] = new AttributeValue { N = destination.Longitude.ToString(CultureInfo.InvariantCulture) };
        }

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { trip } });
    }

    /// <summary>
    /// The stored delivery before the confirm: picked up four hours ago by trucker-001 at <paramref name="pickup"/>
    /// (the trucker's scan point), the rig of <c>SetupExistingDeliveryWithQrNonce</c> plus the pickup fields.
    /// </summary>
    private DateTime GivenPickedUpDelivery(TrackPoint pickup, string? orderId = null)
    {
        var pickedUpAt = DateTime.UtcNow.AddHours(-4);
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "qrNonce", new AttributeValue { S = "nonce-frd09" } },
            { "pickupPhotoVersionId", new AttributeValue { S = "photo-version-pickup-1" } },
            { "dropoffPhotoVersionId", new AttributeValue { S = "photo-version-dropoff-1" } },
            { "status", new AttributeValue { S = "pickedUp" } },
            { "pickedUpAt", new AttributeValue { S = pickedUpAt.ToString("o", CultureInfo.InvariantCulture) } },
            { "gpsPickup", GpsPoint(pickup.Latitude, pickup.Longitude) },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = "1" } }
        };
        if (orderId != null)
        {
            item["orderId"] = new AttributeValue { S = orderId };
        }

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(new UpdateItemResponse { Attributes = PlainDeliveredRow() });
        return pickedUpAt;
    }

    /// <summary>The row the confirm wrote (the FRD-09 rig's; a six hour trip, so no other pattern speaks).</summary>
    private static Dictionary<string, AttributeValue> PlainDeliveredRow()
        => DeliveredRow(DateTime.UtcNow, TimeSpan.FromHours(6), TimeSpan.FromHours(6));

    /// <summary>A road check-in of trucker-001 on trip-001, taken <paramref name="ago"/> before now.</summary>
    private static TripLocationRow CheckInRow(TimeSpan ago, TrackPoint at, string source = "hourly", string truckerId = "trucker-001")
    {
        var captured = DateTime.UtcNow - ago;
        return new TripLocationRow("trip-001", $"{LocationPingRules.FormatUtc(captured)}#fix", truckerId,
            at.Latitude, at.Longitude, 35, source, false, null, LocationPingRules.FormatUtc(captured.AddSeconds(3)), 1_800_000_000);
    }

    private void GivenTruckerCheckIns(params TripLocationRow[] rows)
        => _truckerCheckIns.ListBetweenAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new TripLocationWindow(rows.ToList(), Complete: true));

    /// <summary>The buyer's confirm, with the drop-off coordinates their phone sends (or none).</summary>
    private async Task<string> ConfirmWithDropoffAsync(SyncFunction function, TrackPoint? gpsDropoff)
    {
        var data = new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        };
        if (gpsDropoff != null)
        {
            data["gpsDropoff"] = new Dictionary<string, object>
            {
                { "latitude", gpsDropoff.Latitude }, { "longitude", gpsDropoff.Longitude }
            };
        }

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(data)), "buyer-001", "buyer"), _context);
        return response.Body;
    }

    private Task NoTruckerRowAsync()
        => _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);

    // ── the flag off: exactly the behaviour before ESC-33 ────────────

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GpsAnomaly_FlagOff_LandsOnTheConfirmingBuyerOnly_AsBefore(bool? flag)
    {
        var function = GpsAnomalyFunction(onTrucker: flag);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "trucker-001", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        await NoTruckerRowAsync();
        // Not even read: the trucker's check-ins stay untouched with the flag off.
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public void GpsAnomaly_TheTruckerRow_IsOffByDefault()
    {
        Assert.False(new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table")
            .GpsAnomalyOnTrucker);
    }

    // ── the flag on: the trucker's row from the trucker's own evidence ─

    [Fact]
    public async Task GpsAnomaly_ABuyerSendingAFabricatedDropoff_FlagsOnlyTheBuyer_WhileTheTruckersCheckInIsAtTheDropoff()
    {
        // FUP-203's case: an inter-woreda trip whose destination is 3.3 km from the farm. The buyer's phone sends the
        // pickup point itself as the drop-off (the track shows it to the buyer): within 5 km of the destination,
        // so the confirm is accepted, and Rule 4 flags the identical points. The trucker's own check-in five
        // minutes before the confirmation is at the destination.
        var farm = new TrackPoint(8.0000, 38.0000);
        var destination = new TrackPoint(8.0300, 38.0000);
        var function = GpsAnomalyFunction();
        WithTheRealBuyerRule4();
        GivenGpsTrip(destination);
        GivenPickedUpDelivery(farm);
        GivenTruckerCheckIns(
            CheckInRow(TimeSpan.FromMinutes(40), new TrackPoint(8.0200, 38.0000), "near_stop"),
            CheckInRow(TimeSpan.FromMinutes(5), destination, "arrived"));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: farm);

        Assert.Contains("\"status\":\"success\"", body);
        // The buyer whose phone sent the coordinates keeps their own row.
        await _patternFraud.Received(1).RecordFraudSignalAsync(
            "buyer-001", "GPS_ANOMALY", "del-001", "delivery", Arg.Is<string?>(d => d!.Contains("0.00km apart")));
        // The trucker is judged by the trucker's own check-in, which is 3.3 km from the pickup: no row.
        await NoTruckerRowAsync();
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "trucker-001", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GpsAnomaly_TheBuyersFlaggedCoordinatesAlone_NeverReachTheTrucker()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(5), GpsAddis, "arrived"));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_TheTruckersOwnCheckInAtThePickup_WritesTheTruckerRow_ThroughTheGuard()
    {
        // An honest drop-off from the buyer's phone (no buyer row); the trucker's own phone says the truck was at the
        // farm ten minutes before the confirmation, 259 km from the drop-off point.
        var function = GpsAnomalyFunction();
        WithTheRealBuyerRule4();
        GivenGpsTrip(GpsAddis);
        var pickedUpAt = GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), new TrackPoint(7.6705, 36.8302)));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis);

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery",
            Arg.Is<string?>(d => d!.StartsWith("Trucker check-in 10 min before the confirmation: 0.06 km from the pickup point")));
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "buyer-001", "GPS_ANOMALY", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        // Nothing reaches the trucker past the guard.
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "trucker-001", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        // One bounded read of the delivery's window: from its pickup to the confirmation plus the clock skew.
        await _truckerCheckIns.Received(1).ListBetweenAsync(
            "trip-001", LocationPingRules.FormatUtc(pickedUpAt), Arg.Is<string>(to => to.EndsWith("#~", StringComparison.Ordinal)),
            TruckerGpsEvidence.MaxWindowPages);
    }

    [Fact]
    public async Task GpsAnomaly_NoCheckInOfTheTrucker_NoTruckerRow()
    {
        // Sharing declined (D2): only the pickup scan fix, which is not road evidence.
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma, "scan"));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_AStaleCheckIn_NoTruckerRow()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromHours(2), GpsJimma));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_ALateConfirm_WithTheTruckBackAtTheFarm_NoTruckerRow()
    {
        // The buyer holds the confirm offline (or on purpose) until the truck is back at the same farm for its next
        // load. Hourly check-ins on the road, none within 5 km of the drop-off; the last one at the farm ten minutes
        // before the confirm reaches the server. The road check-ins clear the trucker.
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(
            CheckInRow(TimeSpan.FromMinutes(220), new TrackPoint(8.2000, 37.5000)),
            CheckInRow(TimeSpan.FromMinutes(170), new TrackPoint(8.9000, 38.6000)),
            CheckInRow(TimeSpan.FromMinutes(110), new TrackPoint(8.5000, 38.0000)),
            CheckInRow(TimeSpan.FromMinutes(50), new TrackPoint(7.9000, 37.1000)),
            CheckInRow(TimeSpan.FromMinutes(10), new TrackPoint(7.6705, 36.8302)));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_ATripWithoutADestinationPoint_NoTruckerRow_WhateverTheOrdersPoint()
    {
        // A trip from before trip-listing-api 2.1.0: the only drop-off point left is the buyer's order point, far
        // from the farm. It may only clear the trucker, so nothing is written.
        var function = GpsAnomalyFunction();
        GivenGpsTrip(destination: null);
        GivenPickedUpDelivery(GpsJimma, orderId: "order-gps");
        _mockOrderRepo.GetByIdAsync("order-gps", consistentRead: true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-gps" }, { "buyerId", "buyer-001" }, { "tripId", "trip-001" },
            { OrderDeliveryLocation.LatitudeField, GpsAddis.Latitude }, { OrderDeliveryLocation.LongitudeField, GpsAddis.Longitude }
        });
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_AWindowReadThatStoppedAtItsCap_NoTruckerRow()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        _truckerCheckIns.ListBetweenAsync(default!, default!, default!, default).ReturnsForAnyArgs(
            new TripLocationWindow(new List<TripLocationRow> { CheckInRow(TimeSpan.FromMinutes(10), GpsJimma) }, Complete: false));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_TheOrdersOwnDropoffPoint_CanClearTheTrucker()
    {
        // The order's point (read strongly consistent) is 3 km from the farm: a truck at the farm is at the drop-off.
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma, orderId: "order-gps");
        _mockOrderRepo.GetByIdAsync("order-gps", consistentRead: true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-gps" }, { "buyerId", "buyer-001" }, { "tripId", "trip-001" },
            { OrderDeliveryLocation.LatitudeField, 7.67 + 3 / 111.19 }, { OrderDeliveryLocation.LongitudeField, 36.83 }
        });
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
        await _mockOrderRepo.Received().GetByIdAsync("order-gps", consistentRead: true);
    }

    [Fact]
    public async Task GpsAnomaly_TwoDeliveriesOfOneTruckerInOneBatch_EachGetsItsOwnGuardedAttempt()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));
        _patternFraud.RecordAttributedSignalAsync("trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", Arg.Any<string?>())
            .Returns(AttributedSignalOutcome.AlreadyRecorded);
        SyncOperationRequest Confirm(string deliveryId) => MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        }) with
        {
            OperationId = $"op-{deliveryId}",
            EntityId = deliveryId,
            IdempotencyKey = $"buyer-001:delivery:{deliveryId}:update:1"
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(Confirm("del-001"), Confirm("del-002")), "buyer-001", "buyer"), _context);

        Assert.DoesNotContain("\"status\":\"error\"", response.Body);
        // A skip for the first delivery does not stop the attempt for the second.
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", Arg.Any<string?>());
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-002", "delivery", Arg.Any<string?>());
    }

    [Fact]
    public async Task GpsAnomaly_ATruckerIdInTheRequest_IsIgnored()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }, { "truckerId", "victim-trucker" }
        });

        await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        await _patternFraud.DidNotReceive().RecordAttributedSignalAsync(
            "victim-trucker", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GpsAnomaly_ACheckInOfAnotherTrucker_IsNotTheTruckersEvidence()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma, truckerId: "victim-trucker"));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task GpsAnomaly_ByAUserWhoIsNotTheDeliverysBuyer_IsRefusedBeforeAnySignalOnTheTrucker()
    {
        var function = GpsAnomalyFunction();
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        });

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "stranger-001", "buyer"), _context);

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await NoTruckerRowAsync();
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_WhenTheDeliveryWriteFails_WritesNoSignal()
    {
        var function = GpsAnomalyFunction();
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("stale"));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.DoesNotContain("\"status\":\"success\"", body);
        await NoTruckerRowAsync();
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_OnASelfPickup_StaysOnTheBuyerWhoMovedTheGoods()
    {
        // The buyer collects at the farm: one scan, no trucker on the row (the rig of
        // SelfPickupDelivered_WithNonceAndListingGps_Succeeds).
        var cropRepo = Substitute.For<ICropRepository>();
        cropRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "farmerId", "farmer-001" }, { "latitude", "9.0" }, { "longitude", "38.7" },
            { "status", "available" }, { "version", "1" }
        });
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Flagged("GPS_ANOMALY", GpsWhy));
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), new TrackPoint(9.0, 38.7)));
        var function = new SyncFunction(
            _mockSyncMeta, cropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo, tripLocationRepo: _truckerCheckIns)
        {
            GpsAnomalyOnTrucker = true
        };
        SetupDeliveryQuery(SelfPickupDeliveryItem("pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));
        // The F-14 GPS check reads the delivery's trip for its woredas: a self-pickup has none.
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-deliver",
            IdempotencyKey = "buyer-001:delivery:del-sp:update:deliver",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "sp-nonce" },
                { "gpsDropoff", new Dictionary<string, object> { { "latitude", 9.0 }, { "longitude", 38.7 } } }
            }
        };

        var response = await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-sp", "delivery", GpsWhy);
        await NoTruckerRowAsync();
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_AGuardThatThrows_NeverFailsTheConfirm()
    {
        var function = GpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(10), GpsJimma));
        _patternFraud.RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException<AttributedSignalOutcome>(new InvalidOperationException("query failed")));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.ReceivedWithAnyArgs(1).RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_ACheckInReadThatThrows_NeverFailsTheConfirm()
    {
        var function = GpsAnomalyFunction();
        GivenPickedUpDelivery(GpsJimma);
        _truckerCheckIns.ListBetweenAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new AmazonDynamoDBException("throttled"));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.Contains("\"status\":\"success\"", body);
        await NoTruckerRowAsync();
        // The buyer's own row is written before the trucker's evidence is read.
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
    }

    // ── FUP-245: the trucker's drop-off check-in (ESC-36) first ──────

    private const string DropoffOrderId = "order-fup245";

    /// <summary>The rig above with <c>dropoffClassification</c> (and the commitments it needs) set as given.</summary>
    private SyncFunction DropoffGpsAnomalyFunction(bool dropoffOn = true)
    {
        GpsAnomalyFunction();
        return new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, tripLocationRepo: _truckerCheckIns)
        {
            PatternRules = new TransactionPatternService(_mockOrderRepo, null, TransactionPatternSettings.Default),
            GpsAnomalyOnTrucker = true,
            CommitmentsEnabled = dropoffOn,
            DropoffClassificationEnabled = dropoffOn
        };
    }

    /// <summary>
    /// The delivery's order, stamped at pickup (ESC-36), with the trucker's drop-off check-in received
    /// <paramref name="checkInAgo"/> before now (<paramref name="distanceKm"/>: measured, null: no point to measure
    /// against) and an out-of-range attempt (<paramref name="attemptAgo"/>), as the check-in handler keeps them.
    /// </summary>
    private void GivenDropoffOrder(
        TimeSpan? checkInAgo = null, double? distanceKm = 0.8, TimeSpan? attemptAgo = null, string by = "trucker-001")
    {
        Dictionary<string, object> Entry(TimeSpan ago, double? km) => PickupJudgement.Entry(
            new ParsedCheckIn(DropoffOrderId, PickupJudgement.Kind.ArrivedDropoff, null, GpsJimma.Latitude, GpsJimma.Longitude, 20),
            by, DateTime.UtcNow - ago, km, late: false);
        var order = new Dictionary<string, object>
        {
            { "orderId", DropoffOrderId }, { "buyerId", "buyer-001" }, { "truckerId", "trucker-001" }, { "tripId", "trip-001" },
            { "status", "in_transit" }, { "activeDeliveryId", "del-001" },
            { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending }
        };
        if (checkInAgo is { } checkIn)
        {
            order[DropoffJudgement.CheckInField] = Entry(checkIn, distanceKm);
        }

        if (attemptAgo is { } attempt)
        {
            order[DropoffJudgement.AttemptField] = Entry(attempt, 14.2);
        }

        _mockOrderRepo.GetByIdAsync(DropoffOrderId, consistentRead: true).Returns(order);
    }

    /// <summary>
    /// The FUP-203 anomaly on the road check-ins: every one at the farm, the last ten minutes before the
    /// confirmation, the trip's destination 259 km away. Without drop-off evidence this writes the trucker's row.
    /// </summary>
    private void GivenEveryRoadCheckInAtTheFarm()
    {
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma, orderId: DropoffOrderId);
        GivenTruckerCheckIns(
            CheckInRow(TimeSpan.FromMinutes(70), new TrackPoint(7.6702, 36.8301)),
            CheckInRow(TimeSpan.FromMinutes(10), new TrackPoint(7.6705, 36.8302)));
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_TheTruckersDropoffCheckIn_IsReadFirst_AndClears_WhateverTheRoadCheckInsSay()
    {
        var function = DropoffGpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        GivenDropoffOrder(checkInAgo: TimeSpan.FromMinutes(30));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        await NoTruckerRowAsync();
        // The drop-off check-in decides: the road check-ins are not read.
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
        await _mockOrderRepo.Received(1).GetByIdAsync(DropoffOrderId, consistentRead: true);
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_ATruckerWhoDeclinedSharing_IsJudgedFromTheDropoffCheckIn()
    {
        // D2: no road check-in at all, only the pickup scan fix. The drop-off check-in is the evidence.
        var function = DropoffGpsAnomalyFunction();
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma, orderId: DropoffOrderId);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromHours(4), GpsJimma, "scan"));
        GivenDropoffOrder(checkInAgo: TimeSpan.FromMinutes(20), distanceKm: null);

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: null));

        await NoTruckerRowAsync();
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_AnOutOfRangeAttemptAlone_WritesNoRow_EvenWithEveryRoadCheckInAtTheFarm()
    {
        // FUP-204: the point the attempt missed may be the buyer's own pin; the buyer's choice never decides the row.
        var function = DropoffGpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        GivenDropoffOrder(attemptAgo: TimeSpan.FromMinutes(15));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        await NoTruckerRowAsync();
        await _truckerCheckIns.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_NoDropoffCheckIn_FallsBackToTheRoadCheckIns_WithOneOrderRead()
    {
        var function = DropoffGpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        GivenDropoffOrder();

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery",
            Arg.Is<string?>(d => d!.StartsWith("Trucker check-in 10 min before the confirmation: 0.06 km from the pickup point")));
        await _truckerCheckIns.ReceivedWithAnyArgs(1).ListBetweenAsync(default!, default!, default!, default);
        // The read for the drop-off check-in also serves the order's own point.
        await _mockOrderRepo.Received(1).GetByIdAsync(DropoffOrderId, consistentRead: true);
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_ADropoffCheckInOfAnotherTrucker_IsNotTheTruckersEvidence()
    {
        var function = DropoffGpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        GivenDropoffOrder(checkInAgo: TimeSpan.FromMinutes(30), by: "victim-trucker");

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        // The road check-ins of the delivery's own trucker decide.
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", Arg.Any<string?>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GpsAnomaly_FUP245_DropoffClassificationOff_TheDropoffCheckInIsNotRead_AsBefore(bool? dropoffOn)
    {
        // null: the flags as the Lambda reads them from its environment (unset in tests, as in every environment).
        var function = dropoffOn is { } on ? DropoffGpsAnomalyFunction(on) : GpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        GivenDropoffOrder(checkInAgo: TimeSpan.FromMinutes(30));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        // FUP-203 exactly: the road check-ins write the row; the order is read only for its own point.
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", Arg.Any<string?>());
        await _truckerCheckIns.ReceivedWithAnyArgs(1).ListBetweenAsync(default!, default!, default!, default);
        await _mockOrderRepo.Received(1).GetByIdAsync(DropoffOrderId, consistentRead: true);
    }

    [Fact]
    public async Task GpsAnomaly_FUP245_AnOrderReadThatThrows_NeverFailsTheConfirm()
    {
        var function = DropoffGpsAnomalyFunction();
        GivenEveryRoadCheckInAtTheFarm();
        _mockOrderRepo.GetByIdAsync(DropoffOrderId, consistentRead: true)
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        Assert.Contains("\"status\":\"success\"", await ConfirmWithDropoffAsync(function, gpsDropoff: GpsAddis));

        await NoTruckerRowAsync();
    }

    [Fact]
    public async Task NoGpsAnomaly_WritesNothingOnAnyone()
    {
        var function = GpsAnomalyFunction();
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Allowed());
        GivenGpsTrip(GpsAddis);
        GivenPickedUpDelivery(GpsJimma);
        GivenTruckerCheckIns(CheckInRow(TimeSpan.FromMinutes(5), GpsAddis, "arrived"));

        var body = await ConfirmWithDropoffAsync(function, gpsDropoff: null);

        Assert.Contains("\"status\":\"success\"", body);
        await NoTruckerRowAsync();
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }
}
