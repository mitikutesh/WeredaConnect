using System.Globalization;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-09 (fraud-detection-architecture.md 15.1) on delivery updates: the pickup keeps when the phone made the
/// scan (<c>pickupCapturedAt</c>); a confirm too fast for the distance between the two scans flags the delivery's
/// trucker; a delivery dispute the buyer raised feeds the buyer's dispute rate. Uses the delivery and dispute
/// rigs of <see cref="SyncFunctionTests"/>.
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

    private async Task<string> ConfirmDeliveryAsync(SyncFunction function, Dictionary<string, AttributeValue> written)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-frd09");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(new UpdateItemResponse { Attributes = written });
        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-frd09" }
        }) with
        {
            Timestamp = IsoUtc(DateTime.UtcNow.AddMinutes(-1))
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);
        return response.Body;
    }

    [Fact]
    public async Task DeliveryConfirmed_TooFastForTheDistance_FlagsTheTrucker_NotTheBuyerWhoConfirmed()
    {
        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(),
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)));

        Assert.Contains("\"status\":\"success\"", body);
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001", "delivery",
            Arg.Is<string>(d => d.Contains("258.9 km apart") && d.Contains("120 km/h")));
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync("buyer-001", Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string?>());
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
        _patternFraud.RecordFraudSignalAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("put failed")));

        var body = await ConfirmDeliveryAsync(function,
            DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)));

        Assert.Contains("\"status\":\"success\"", body);
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
