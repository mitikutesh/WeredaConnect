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
/// FRD-09 (fraud-detection-architecture.md 15.1) on delivery updates: the pickup keeps when the phone made the
/// scan (<c>pickupCapturedAt</c>); a confirm too fast for the distance between the two scans flags the delivery's
/// trucker, with the buyer's drop-off point bounded by a point the trucker controls (FUP-265); a delivery dispute
/// the buyer raised feeds the buyer's dispute rate. Uses the delivery and dispute rigs of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private readonly IFraudDetectionService _patternFraud = Substitute.For<IFraudDetectionService>();

    private SyncFunction DeliveryPatternFunction(IEscrowRepository? escrowRepo = null, EscrowService? escrowService = null)
    {
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Allowed());
        // The F-14 GPS check reads the delivery's trip for its woredas: none on record here.
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        return new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, escrowRepo: escrowRepo,
            orderEscrowService: escrowService)
        {
            PatternRules = new TransactionPatternService(_mockOrderRepo, escrowRepo, TransactionPatternSettings.Default)
        };
    }

    private static string IsoUtc(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static AttributeValue GpsPoint(double lat, double lng) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            { "latitude", new AttributeValue { N = lat.ToString(CultureInfo.InvariantCulture) } },
            { "longitude", new AttributeValue { N = lng.ToString(CultureInfo.InvariantCulture) } }
        }
    };

    /// <summary>The row the confirm wrote: picked up near Jimma, dropped off in Addis Ababa (about 259 km).</summary>
    private static Dictionary<string, AttributeValue> DeliveredRow(
        DateTime confirmedAt, TimeSpan serverTrip, TimeSpan captureTrip, string deliveryType = "trucker")
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "status", new AttributeValue { S = "delivered" } },
            { "gpsPickup", GpsPoint(7.67, 36.83) },
            { "gpsDropoff", GpsPoint(9.03, 38.74) },
            { "pickedUpAt", new AttributeValue { S = IsoUtc(confirmedAt - serverTrip) } },
            { "confirmedAt", new AttributeValue { S = IsoUtc(confirmedAt) } },
            { TransactionPatternRules.PickupCapturedAtField, new AttributeValue { S = IsoUtc(confirmedAt.AddMinutes(-1) - captureTrip) } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = "2" } }
        };
        if (deliveryType == "self_pickup")
        {
            row["deliveryType"] = new AttributeValue { S = "self_pickup" };
            row.Remove("truckerId");
        }

        return row;
    }

    /// <summary>The pickup side of a delivery row: what the stored row holds before the confirm (FUP-246).</summary>
    private static Dictionary<string, AttributeValue> PickupSide(Dictionary<string, AttributeValue> row)
        => row.Where(kv => kv.Key is "gpsPickup" or "pickedUpAt" or TransactionPatternRules.PickupCapturedAtField)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// The buyer's confirm of a picked-up delivery whose stored pickup is <paramref name="stored"/> (by default the
    /// pickup side of <paramref name="written"/>, the row the write answers with).
    /// </summary>
    private async Task<string> ConfirmDeliveryAsync(
        SyncFunction function, Dictionary<string, AttributeValue> written, TimeSpan? buyerClockOffset = null,
        Dictionary<string, AttributeValue>? stored = null, Dictionary<string, object>? extraData = null)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09",
            stored: stored ?? PickupSide(written));
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(new UpdateItemResponse { Attributes = written });
        var data = new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        };
        foreach (var (key, value) in extraData ?? new Dictionary<string, object>())
        {
            data[key] = value;
        }

        var op = MakeDeliveryUpdateOp(data) with
        {
            Timestamp = IsoUtc(DateTime.UtcNow.AddMinutes(-1) + (buyerClockOffset ?? TimeSpan.Zero))
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);
        return response.Body;
    }

    [Fact]
    public async Task DeliveryConfirmed_TooFastForTheDistance_FlagsTheTrucker_NotTheBuyerWhoConfirmed()
    {
        var function = DeliveryPatternFunction();
        // FUP-265: the trip's destination point, which the trucker set, bounds the buyer's drop-off point.
        GivenGpsTrip(GpsAddis);

        var body = await ConfirmDeliveryAsync(function,
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("258.9 km apart") && d.Contains("120 km/h")));
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync("buyer-001", Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData(-6)] // a buyer phone on a manually set local clock, six hours behind
    [InlineData(-12)] // or a day setting gone wrong
    public async Task DeliveryConfirmed_BuyerClockBehind_DoesNotFlagAnHonestOfflineTrucker(int buyerClockHours)
    {
        // The trucker was offline at the farm: the pickup reached the server 5 minutes before the confirm,
        // the trucker's phone made the scan 4 hours earlier. The buyer's clock is not part of the trip time.
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(5), TimeSpan.FromHours(4)),
            buyerClockOffset: TimeSpan.FromHours(buyerClockHours));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DeliveryConfirmed_NoGpsFixAtPickup_IsNotJudged()
    {
        var row = DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));
        row["gpsPickup"] = GpsPoint(0, 0);

        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(), row);

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DeliveryConfirmed_PickupSyncedLateFromAnOfflinePhone_IsNotFlagged()
    {
        // The pickup reached the server 10 minutes before the confirm, but the phone made the scan 4 hours earlier.
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(10), TimeSpan.FromHours(4)));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DeliveryConfirmed_SelfPickup_HasNoTripToJudge()
    {
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.Zero, TimeSpan.Zero, deliveryType: "self_pickup"));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DeliveryConfirmed_SignalWriteFails_TheConfirmStillSucceeds()
    {
        var function = DeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);
        _patternFraud.RecordFraudSignalAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("put failed")));

        var body = await ConfirmDeliveryAsync(function,
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001",
            "delivery", Arg.Any<string?>());
    }

    // ── FUP-265: never a row from a drop-off point the buyer alone controls ──────

    private const string BoundOrderId = "order-fup265";

    /// <summary>The confirmed row of a 20 minute trip from Jimma whose buyer sent <paramref name="dropoff"/>.</summary>
    private static Dictionary<string, AttributeValue> FastRowTo(TrackPoint dropoff, TimeSpan? serverTrip = null, TimeSpan? captureTrip = null)
    {
        var row = DeliveredRow(DateTime.UtcNow, serverTrip ?? TimeSpan.FromMinutes(20), captureTrip ?? TimeSpan.FromMinutes(25));
        row["gpsDropoff"] = GpsPoint(dropoff.Latitude, dropoff.Longitude);
        return row;
    }

    /// <summary>4.7 km past Addis Ababa seen from Jimma, inside the confirm's 5 km of the trip's destination.</summary>
    private static readonly TrackPoint StretchedPastAddis = new(9.06, 38.77);

    private Task NoDeliveryTimeRowAsync()
        => _patternFraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "IMPOSSIBLE_DELIVERY_TIME",
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());

    [Fact]
    public async Task FUP265_AConfirmOnATripWithoutADestinationPoint_WritesNoRow_HoweverFarTheBuyersPoint()
    {
        // 259 km in 20 minutes by the buyer's point alone: an older trip (no destination point) or none on record.
        var function = DeliveryPatternFunction();
        GivenGpsTrip(destination: null);

        var body = await ConfirmDeliveryAsync(function, FastRowTo(GpsAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await NoDeliveryTimeRowAsync();
    }

    [Fact]
    public async Task FUP265_NoTripOnRecord_WritesNoRow()
    {
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(), FastRowTo(GpsAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await NoDeliveryTimeRowAsync();
    }

    [Fact]
    public async Task FUP265_ABuyerPointStretchedPastTheDestination_CannotTurnAPossibleTripIntoARow()
    {
        // 130.5 minutes: 263.5 km to the buyer's point needs 131.8, the trip's destination (258.9 km) 129.4.
        var function = DeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);

        var body = await ConfirmDeliveryAsync(function,
            FastRowTo(StretchedPastAddis, TimeSpan.FromMinutes(130), TimeSpan.FromMinutes(129.5)));

        Assert.Contains("\"status\":\"success\"", body);
        await NoDeliveryTimeRowAsync();
    }

    [Fact]
    public async Task FUP265_AReallyImpossibleTrip_IsFlagged_WithTheDistanceToTheTripsDestination()
    {
        var function = DeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);

        var body = await ConfirmDeliveryAsync(function, FastRowTo(StretchedPastAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("with the pickup scan and the trip's destination 258.9 km apart")));
    }

    [Fact]
    public async Task FUP265_AnEnRouteDropoffNearerThanTheDestination_IsStillJudged_FromTheBuyersPoint()
    {
        // The buyer meets the truck 92.6 km from the farm, the trip going on to Addis Ababa.
        var function = DeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);

        var body = await ConfirmDeliveryAsync(function, FastRowTo(new TrackPoint(8.10, 37.55)));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("with the pickup scan and the drop-off scan 92.6 km apart")));
    }

    /// <summary>The FRD-09 rig with <c>dropoffClassification</c> (and the commitments it needs) on.</summary>
    private SyncFunction DropoffDeliveryPatternFunction()
    {
        DeliveryPatternFunction();
        return new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo)
        {
            PatternRules = new TransactionPatternService(_mockOrderRepo, null, TransactionPatternSettings.Default),
            CommitmentsEnabled = true,
            DropoffClassificationEnabled = true
        };
    }

    /// <summary>The delivery's order, stamped at pickup (ESC-36), with trucker-001's drop-off check-in at <paramref name="checkIn"/>.</summary>
    private void GivenBoundOrder(TrackPoint? checkIn)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", BoundOrderId }, { "buyerId", "buyer-001" }, { "truckerId", "trucker-001" }, { "tripId", "trip-001" },
            { "status", "in_transit" }, { "activeDeliveryId", "del-001" },
            { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending }
        };
        if (checkIn != null)
        {
            order[DropoffJudgement.CheckInField] = PickupJudgement.Entry(
                new ParsedCheckIn(BoundOrderId, PickupJudgement.Kind.ArrivedDropoff, null, checkIn.Latitude, checkIn.Longitude, 20),
                "trucker-001", DateTime.UtcNow.AddMinutes(-2), 0.8, late: false);
        }

        _mockOrderRepo.GetByIdAsync(BoundOrderId, consistentRead: true).Returns(order);
    }

    private Task<string> ConfirmOrderDeliveryAsync(SyncFunction function, Dictionary<string, AttributeValue> written)
    {
        var stored = PickupSide(written);
        stored["orderId"] = new AttributeValue { S = BoundOrderId };
        return ConfirmDeliveryAsync(function, written, stored: stored);
    }

    [Fact]
    public async Task FUP265_DropoffClassificationOn_TheTruckersOwnCheckIn_BoundsTheBuyersPoint_WithoutATripDestination()
    {
        var function = DropoffDeliveryPatternFunction();
        GivenGpsTrip(destination: null);
        GivenBoundOrder(checkIn: GpsAddis);

        var body = await ConfirmOrderDeliveryAsync(function, FastRowTo(StretchedPastAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("with the pickup scan and the trucker's drop-off check-in 258.9 km apart")));
    }

    [Fact]
    public async Task FUP265_DropoffClassificationOn_ACheckInNearTheFarm_ClearsWhateverTheBuyersPointSays()
    {
        // The trucker's own phone checked in 2 km from the pickup: the drop-off was there, not in Addis Ababa.
        var function = DropoffDeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);
        GivenBoundOrder(checkIn: new TrackPoint(7.685, 36.84));

        var body = await ConfirmOrderDeliveryAsync(function, FastRowTo(GpsAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await NoDeliveryTimeRowAsync();
    }

    [Fact]
    public async Task FUP265_DropoffClassificationOn_NoCheckIn_FallsBackToTheTripsDestination()
    {
        var function = DropoffDeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);
        GivenBoundOrder(checkIn: null);

        var body = await ConfirmOrderDeliveryAsync(function, FastRowTo(StretchedPastAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("the trip's destination 258.9 km apart")));
    }

    [Fact]
    public async Task FUP265_DropoffClassificationOn_AnOrderReadThatThrows_WritesNoRow_AndTheConfirmStands()
    {
        var function = DropoffDeliveryPatternFunction();
        GivenGpsTrip(GpsAddis);
        _mockOrderRepo.GetByIdAsync(BoundOrderId, consistentRead: true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var body = await ConfirmOrderDeliveryAsync(function, FastRowTo(GpsAddis));

        Assert.Contains("\"status\":\"success\"", body);
        await NoDeliveryTimeRowAsync();
        Assert.Contains("Pattern check delivery_time skipped for delivery", Logs);
    }

    [Fact]
    public async Task FUP265_DropoffClassificationOn_ATripTheBuyersPointDoesNotFlag_ReadsNoOrder()
    {
        // The bound only shortens the distance: a six hour trip is not judged further, so no read.
        var function = DropoffDeliveryPatternFunction();
        GivenBoundOrder(checkIn: GpsAddis);

        var body = await ConfirmOrderDeliveryAsync(function,
            FastRowTo(GpsAddis, TimeSpan.FromHours(6), TimeSpan.FromHours(6)));

        Assert.Contains("\"status\":\"success\"", body);
        await _mockOrderRepo.DidNotReceive().GetByIdAsync(BoundOrderId, consistentRead: true);
        await NoDeliveryTimeRowAsync();
    }

    private async Task<UpdateItemRequest?> PickUpAsync(string? clientTimestamp)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-pickup");
        UpdateItemRequest? write = null;
        _mockDynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => write = r))
            .Returns(new UpdateItemResponse { Attributes = DeliveredRow(DateTime.UtcNow, TimeSpan.Zero, TimeSpan.Zero) });
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "pickedUp" }, { "version", 1 }, { "scannedQrNonce", "nonce-pickup" }
        }) with
        {
            Timestamp = clientTimestamp
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker"), _context);
        Assert.Contains("\"status\":\"success\"", response.Body);
        return write;
    }

    [Fact]
    public async Task Pickup_KeepsWhenThePhoneMadeTheScan()
    {
        var captured = DateTime.UtcNow.AddHours(-3);

        var write = await PickUpAsync(IsoUtc(captured));

        Assert.NotNull(write);
        var alias = write!.ExpressionAttributeNames.Single(n => n.Value == TransactionPatternRules.PickupCapturedAtField).Key;
        Assert.Contains($"{alias} = ", write.UpdateExpression);
        var stored = DateTime.Parse(write.ExpressionAttributeValues[alias.Replace('#', ':')].S, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.Equal(captured, stored);
        // pickedUpAt stays the server's own time.
        Assert.Contains(write.ExpressionAttributeNames, n => n.Value == "pickedUpAt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unparsable")]
    [InlineData("future")]
    public async Task Pickup_MissingOrImplausibleTimestamp_StoresNoCaptureTime(string? kind)
    {
        var timestamp = kind switch
        {
            "unparsable" => "not-a-time",
            "future" => IsoUtc(DateTime.UtcNow.AddHours(2)),
            _ => null
        };

        var write = await PickUpAsync(timestamp);

        Assert.NotNull(write);
        Assert.DoesNotContain(write!.ExpressionAttributeNames, n => n.Value == TransactionPatternRules.PickupCapturedAtField);
    }

    // ── a delivery dispute: the buyer's dispute rate ─────

    private (SyncFunction Function, IEscrowRepository EscrowRepo) DisputePatternFunction()
    {
        var escrowRepo = Substitute.For<IEscrowRepository>();
        escrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow());
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync("del-001", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-001" }, { "orderId", "order-001" }, { "status", "disputed" }
        });
        var service = new EscrowService(escrowRepo, Substitute.For<IEscrowAuditRepository>(), idempotencyRepo: null,
            telebirrGateway: null, orderRepo: _mockOrderRepo, deliveryRepo: deliveryRepo,
            transactionAuditRepo: Substitute.For<ITransactionAuditRepository>());
        var function = DeliveryPatternFunction(escrowRepo, service);
        var order = SetupExistingOrder(status: "delivered");
        _mockOrderRepo.GetByIdAsync("order-001", true).Returns(order);
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "delivered", confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));
        return (function, escrowRepo);
    }

    private static Dictionary<string, object> PastEscrow(string id, string status, string? disputedBy = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", id }, { "buyerId", "buyer-001" }, { "status", status },
            { "createdAt", IsoUtc(DateTime.UtcNow.AddDays(-10)) }
        };
        if (disputedBy != null)
        {
            escrow["disputedBy"] = disputedBy;
        }

        return escrow;
    }

    [Fact]
    public async Task DeliveryDispute_BuyerWhoDisputesOften_IsFlaggedWithTheEscrow()
    {
        var (function, escrowRepo) = DisputePatternFunction();
        escrowRepo.QueryByBuyerAsync("buyer-001", null, 50, null).Returns((new List<Dictionary<string, object>>
        {
            PastEscrow("e-1", "dispute_resolved", disputedBy: "buyer-001"),
            PastEscrow("e-2", "completed"), PastEscrow("e-3", "completed"), PastEscrow("e-4", "refunded")
        }, (Dictionary<string, AttributeValue>?)null));

        var response = await SendDeliveryDisputeAsync(function);

        Assert.Contains("success", response.Body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "DISPUTE_RATE", OrderEscrowId, "escrow",
            Arg.Is<string>(d => d.StartsWith("The buyer disputed 2 of 5 paid orders")));
    }

    [Fact]
    public async Task DeliveryDispute_HistoryReadFails_TheDisputeStillStands()
    {
        var (function, escrowRepo) = DisputePatternFunction();
        escrowRepo.QueryByBuyerAsync("buyer-001", null, 50, null).ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var response = await SendDeliveryDisputeAsync(function);

        Assert.Contains("success", response.Body);
        await escrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DeliveryDispute_ByTheFarmer_IsNotTheBuyersRate()
    {
        var (function, escrowRepo) = DisputePatternFunction();

        await SendDeliveryDisputeAsync(function, userId: "farmer-001", role: "farmer");

        await escrowRepo.DidNotReceiveWithAnyArgs().QueryByBuyerAsync(default!, default, default, default);
    }
}
