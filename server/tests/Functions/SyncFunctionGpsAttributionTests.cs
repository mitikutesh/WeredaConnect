using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-33 (fraud-detection-architecture.md sections 14.3 and 15.3): the <c>GPS_ANOMALY</c> of a confirmed
/// delivery lands on the delivery's trucker (the stored row's, never an id from the request) through the
/// attribution guard, not on the buyer who sent the confirm; only after the delivery write succeeded; a
/// self-pickup keeps it on the buyer. Uses the delivery rig of <see cref="SyncFunctionDeliveryPatternTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private const string GpsWhy = "Pickup and dropoff GPS coordinates are identical.";

    private SyncFunction GpsAnomalyFunction()
    {
        var function = DeliveryPatternFunction();
        _patternFraud.CheckGpsConsistency(default, default, default, default, default!, default!)
            .ReturnsForAnyArgs(FraudCheckResult.Flagged("GPS_ANOMALY", GpsWhy));
        _patternFraud.RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(AttributedSignalOutcome.Written);
        return function;
    }

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
        // The buyer who confirmed gets nothing, and nothing bypasses the guard.
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
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
            fraudService: _patternFraud, userRepo: _mockUserRepo);
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
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(), PlainDeliveredRow());

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
        await _patternFraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }
}
