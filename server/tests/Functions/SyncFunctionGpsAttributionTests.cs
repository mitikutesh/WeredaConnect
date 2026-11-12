using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-33 (fraud-detection-architecture.md sections 14.3 and 15.3): the <c>GPS_ANOMALY</c> of a confirmed
/// delivery. It always lands on the confirming buyer, whose phone sent the coordinates, as before ESC-33
/// (now after the delivery write succeeded). Only with <c>reputationPenalties</c> on does the delivery's
/// trucker (the stored row's, never an id from the request) get a second row, through the attribution guard.
/// Uses the delivery rig of the FRD-09 delivery pattern tests.
/// </summary>
public partial class SyncFunctionTests
{
    private const string GpsWhy = "Pickup and dropoff GPS coordinates are identical.";

    private SyncFunction GpsAnomalyFunction(bool? onTrucker = true)
    {
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Allowed());
        // The F-14 GPS check reads the delivery's trip for its woredas: none on record here.
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        var patterns = new TransactionPatternService(_mockOrderRepo, null, TransactionPatternSettings.Default);
        // null: the flag as the Lambda reads it from its environment (unset in tests, as in every environment).
        var function = onTrucker is { } flag
            ? new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
                fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo)
            { PatternRules = patterns, GpsAnomalyOnTrucker = flag }
            : new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
                fraudService: _patternFraud, userRepo: _mockUserRepo, orderRepo: _mockOrderRepo)
            { PatternRules = patterns };
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Flagged("GPS_ANOMALY", GpsWhy));
        _patternFraud.RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(AttributedSignalOutcome.Written);
        return function;
    }

    // ── the flag off: exactly the behaviour before ESC-33 ────────────

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GpsAnomaly_FlagOff_LandsOnTheConfirmingBuyerOnly_AsBefore(bool? flag)
    {
        var body = await ConfirmDeliveryAsync(GpsAnomalyFunction(onTrucker: flag), PlainDeliveredRow());

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "trucker-001", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public void GpsAnomaly_TheTruckerRow_IsOffByDefault()
    {
        Assert.False(new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table")
            .GpsAnomalyOnTrucker);
    }

    // ── the flag on: the same, plus the guarded row on the trucker ───

    /// <summary>A written row with no trip to judge, so only the GPS consistency flag speaks.</summary>
    private static Dictionary<string, AttributeValue> PlainDeliveredRow(string deliveryType = "trucker")
        => DeliveredRow(DateTime.UtcNow, TimeSpan.FromHours(6), TimeSpan.FromHours(6), deliveryType);

    [Fact]
    public async Task GpsAnomaly_OnAConfirmedDelivery_LandsOnTheTrucker_ThroughTheGuard()
    {
        var body = await ConfirmDeliveryAsync(GpsAnomalyFunction(), PlainDeliveredRow());

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordAttributedSignalAsync(
            "trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        // The buyer whose phone sent the coordinates keeps their own row, as before ESC-33.
        await _patternFraud.Received(1).RecordFraudSignalAsync("buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        // Nothing reaches the trucker past the guard.
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(
            "trucker-001", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GpsAnomaly_TwoDeliveriesOfOneTruckerInOneBatch_EachGetsItsOwnGuardedAttempt()
    {
        var function = GpsAnomalyFunction();
        _patternFraud.RecordAttributedSignalAsync("trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy)
            .Returns(AttributedSignalOutcome.AlreadyRecorded);
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(new UpdateItemResponse { Attributes = PlainDeliveredRow() });
        SyncOperationRequest Confirm(string deliveryId) => MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        }) with
        {
            OperationId = $"op-{deliveryId}", EntityId = deliveryId, IdempotencyKey = $"buyer-001:delivery:{deliveryId}:update:1"
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(Confirm("del-001"), Confirm("del-002")), "buyer-001", "buyer"), _context);

        Assert.DoesNotContain("\"status\":\"error\"", response.Body);
        // A skip for the first delivery does not stop the attempt for the second.
        await _patternFraud.Received(1).RecordAttributedSignalAsync("trucker-001", "buyer-001", "GPS_ANOMALY", "del-001", "delivery", GpsWhy);
        await _patternFraud.Received(1).RecordAttributedSignalAsync("trucker-001", "buyer-001", "GPS_ANOMALY", "del-002", "delivery", GpsWhy);
    }

    [Fact]
    public async Task GpsAnomaly_ATruckerIdInTheRequest_IsIgnored()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(new UpdateItemResponse { Attributes = PlainDeliveredRow() });
        var function = GpsAnomalyFunction();
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }, { "truckerId", "victim-trucker" }
        });

        await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        await _patternFraud.DidNotReceive().RecordAttributedSignalAsync(
            "victim-trucker", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GpsAnomaly_ByAUserWhoIsNotTheDeliverysBuyer_IsRefusedBeforeAnySignalOnTheTrucker()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09");
        var function = GpsAnomalyFunction();
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        });

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "stranger-001", "buyer"), _context);

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_WhenTheDeliveryWriteFails_WritesNoSignal()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09");
        var function = GpsAnomalyFunction();
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("stale"));
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        });

        var response = await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_OnASelfPickup_StaysOnTheBuyerWhoMovedTheGoods()
    {
        // The buyer collects at the farm: one scan, no trucker on the row (the rig of
        // SelfPickupDelivered_WithNonceAndListingGps_Succeeds).
        var cropRepo = Substitute.For<WeredaConnect.Server.Data.ICropRepository>();
        cropRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "farmerId", "farmer-001" }, { "latitude", "9.0" }, { "longitude", "38.7" },
            { "status", "available" }, { "version", "1" }
        });
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Flagged("GPS_ANOMALY", GpsWhy));
        var function = new SyncFunction(
            _mockSyncMeta, cropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            fraudService: _patternFraud, userRepo: _mockUserRepo)
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
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task GpsAnomaly_AGuardThatThrows_NeverFailsTheConfirm()
    {
        var function = GpsAnomalyFunction();
        _patternFraud.RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException<AttributedSignalOutcome>(new InvalidOperationException("query failed")));

        var body = await ConfirmDeliveryAsync(function, PlainDeliveredRow());

        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task NoGpsAnomaly_WritesNothingOnAnyone()
    {
        var function = GpsAnomalyFunction();
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Allowed());

        var body = await ConfirmDeliveryAsync(function, PlainDeliveredRow());

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }
}
