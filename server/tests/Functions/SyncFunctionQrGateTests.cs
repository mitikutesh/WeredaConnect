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
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-42: with no QR nonce on record, the drop-off confirm of a delivery that carries money is
/// deferred, not let through. "Carries money" is decided by the order's own escrow
/// (<c>escrow-order-{orderId}</c>, strongly consistent) before the <c>DeliveryIndex</c>, so an order
/// escrow whose link to the delivery failed or lags still needs the QR. A read failure defers.
/// FRD-11: a pickup at the farm (the trucker's <c>pickedUp</c>, the self-pickup buyer's
/// <c>delivered</c>) needs the farmer's nonce whatever the money, and no escrow is read for it.
/// </summary>
public sealed class SyncFunctionQrGateTests
{
    private const string DeliveriesTable = "test-deliveries-table";
    private const string DeliveryId = "del-fup42";
    private const string OrderId = "order-fup42";
    private const string OrderEscrowId = "escrow-order-" + OrderId;
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string NoncePending = "DELIVERY_NONCE_PENDING: ";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IEscrowRepository _escrow = Substitute.For<IEscrowRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryQrSecretRepository _qrSecrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly TestLambdaContext _context = new();

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    public SyncFunctionQrGateTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
    }

    // ---- an order escrow the DeliveryIndex does not know (link failed or lagging) ----

    [Fact]
    public async Task UnlinkedOrderEscrow_NoNonce_DropOff_IsDeferred_AndNothingIsWritten()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupUnlinkedOrderEscrow();

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        await _escrow.Received(1).GetByIdAsync(OrderEscrowId, true);
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        // Found by its key, so the index is not asked.
        await _escrow.DidNotReceive().GetByDeliveryIdAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("pending", "pickedUp", TruckerId, "trucker")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer")]
    public async Task UnlinkedOrderEscrow_SecretStoreOn_NoSecretRow_IsDeferred(
        string from, string to, string userId, string role)
    {
        // As production runs: the secret store is configured and has no row for this delivery.
        SetupDelivery(TruckDelivery(from));
        SetupUnlinkedOrderEscrow();
        _qrSecrets.GetAsync(DeliveryId).Returns((DeliveryQrSecret?)null);

        var result = await SendAsync(to, userId, role, true);

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        await _qrSecrets.Received(1).GetAsync(DeliveryId);
    }

    [Fact]
    public async Task UnlinkedOrderEscrow_SecretStoreOn_OnRecordNonceAndMatchingScan_IsStillDeferred()
    {
        // A pre-SIM-41 row may carry a nonce anyone could have written: with the store on it is not
        // trusted, so a scan that echoes it proves nothing and the money check decides.
        var item = TruckDelivery("pending");
        item["qrNonce"] = new AttributeValue { S = "on-record-nonce" };
        SetupDelivery(item);
        SetupUnlinkedOrderEscrow();

        var result = await SendAsync("pickedUp", TruckerId, "trucker", true, ("scannedQrNonce", "on-record-nonce"));

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task UnlinkedOrderEscrow_SelfPickupConfirm_NoNonce_IsDeferred()
    {
        SetupDelivery(SelfPickupDelivery("pending"));
        SetupUnlinkedOrderEscrow();

        var result = await SendAsync("delivered", BuyerId, "buyer",
            ("gpsDropoff", JsonSerializer.SerializeToElement(new { latitude = 9.0, longitude = 38.7 })));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task UnlinkedOrderEscrow_PastTheDeferWindow_FailsLikeALinkedOne()
    {
        SetupDelivery(TruckDelivery("pickedUp", createdAt: DateTime.UtcNow.AddDays(-20)));
        SetupUnlinkedOrderEscrow();

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .StartWith("DELIVERY_NONCE_MISSING: QR verification could not be completed for this payment.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task UnlinkedOrderEscrow_InAnyStatus_StillNeedsTheQr()
    {
        // Existence decides, as the DeliveryIndex check always did: fail closed on odd states.
        SetupDelivery(TruckDelivery("pending"));
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow("initiated"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker");

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
    }

    // ---- unchanged: a linked escrow, an escrow on the DeliveryIndex, and no escrow at all ----

    [Theory]
    [InlineData("pending", "pickedUp", TruckerId, "trucker")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer")]
    public async Task LinkedOrderEscrow_NoNonce_IsDeferredAsBefore(
        string from, string to, string userId, string role)
    {
        SetupDelivery(TruckDelivery(from));
        var linked = OrderEscrowRow("escrowed");
        linked["deliveryId"] = DeliveryId;
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(linked);
        _escrow.GetByDeliveryIdAsync(DeliveryId).Returns(linked);

        var result = await SendAsync(to, userId, role);

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task IndexedEscrow_WithoutAnOrderEscrow_NoNonce_DropOff_IsStillDeferred()
    {
        // An escrow paid through POST /escrow/initiate: on the DeliveryIndex, not at the order key.
        SetupDelivery(TruckDelivery("pickedUp"));
        _escrow.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-initiate-1" }, { "deliveryId", DeliveryId }, { "status", "escrowed" }
        });

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        await _escrow.Received(1).GetByIdAsync(OrderEscrowId, true);
    }

    [Fact]
    public async Task LegacyDeliveryWithoutAnOrder_IndexedEscrow_IsDeferred_AndNoOrderKeyIsRead()
    {
        SetupDelivery(TruckDelivery("pickedUp", orderId: null));
        _escrow.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-legacy-1" }, { "deliveryId", DeliveryId }, { "status", "escrowed" }
        });

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task NoEscrowAnywhere_NoNonce_DropOff_PassesAsBefore()
    {
        // A cash delivery picked up without a QR before FRD-11: neither the order key nor the
        // DeliveryIndex finds an escrow, so its drop-off is not stranded.
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("success");
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>());
        await _escrow.Received(1).GetByIdAsync(OrderEscrowId, true);
        Logs.Should().NotContain("QR_GATE_UNDECIDED");
    }

    // ---- FRD-11: a pickup at the farm needs the farmer's QR whatever the money ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pickup_NoEscrowAnywhere_NoNonce_IsDeferred_WithoutReadingTheEscrow(bool qrSecrets)
    {
        // The trucker path. Before FRD-11 a delivery without an escrow (cash) was let through.
        SetupDelivery(TruckDelivery("pending"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", qrSecrets);

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically.");
        await AssertNothingWrittenAsync();
        await AssertNoEscrowReadAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfPickup_NoEscrowAnywhere_NoNonce_IsDeferred_WithoutReadingTheEscrow(bool qrSecrets)
    {
        // The self-pickup path: the buyer's delivered is the pickup at the farm.
        SetupDelivery(SelfPickupDelivery("pending"));
        SetupListingAtTheFarm();

        var result = await SendAsync("delivered", BuyerId, "buyer", qrSecrets, GpsAtTheFarm("gpsDropoff"));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        await AssertNoEscrowReadAsync();
    }

    [Fact]
    public async Task Pickup_WithoutAnEscrowRepository_IsStillDeferred()
    {
        // No escrow store wired used to read as "cash" and let every pickup through.
        SetupDelivery(TruckDelivery("pending"));
        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(),
            orderRepo: _orders, escrowRepo: null, qrSecretRepo: _qrSecrets);

        var result = await SendWithAsync(function, "pickedUp", TruckerId, "trucker");

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pickup_NoNonce_PastTheDeferWindow_IsRefused_MoneyOrNot(bool paid)
    {
        SetupDelivery(TruckDelivery("pending", createdAt: DateTime.UtcNow.AddDays(-20)));
        if (paid)
        {
            SetupUnlinkedOrderEscrow();
        }

        var result = await SendAsync("pickedUp", TruckerId, "trucker", true);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_MISSING: The farmer's QR code never reached the server for this pickup. Contact support.");
        await AssertNothingWrittenAsync();
        await AssertNoEscrowReadAsync();
    }

    [Fact]
    public async Task SelfPickup_NoNonce_PastTheDeferWindow_IsRefused()
    {
        SetupDelivery(SelfPickupDelivery("pending", createdAt: DateTime.UtcNow.AddDays(-20)));
        SetupListingAtTheFarm();

        var result = await SendAsync("delivered", BuyerId, "buyer", true, GpsAtTheFarm("gpsDropoff"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .StartWith("DELIVERY_NONCE_MISSING: The farmer's QR code never reached the server for this pickup.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Pickup_ReadFailureCannotHappen_SoNoUndecidedAlarm()
    {
        // The pickup reads no escrow, so a failing escrow store neither defers it as undecided
        // nor alarms; it waits for the farmer's nonce like any pickup.
        SetupDelivery(TruckDelivery("pending"));
        _escrow.GetByIdAsync(OrderEscrowId, true)
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", true);

        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically.");
        Logs.Should().NotContain("QR_GATE_UNDECIDED");
    }

    [Fact]
    public async Task Pickup_NoEscrow_FarmerNonceScanned_Succeeds_AndConsumesThePickupLeg()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupFarmerNonce("farmer-nonce-1");

        var result = await SendAsync("pickedUp", TruckerId, "trucker", true,
            ("scannedQrNonce", "farmer-nonce-1"), GpsAtTheFarm("gpsPickup"));

        result.GetProperty("status").GetString().Should().Be("success");
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "pickedUp", "op-fup42");
        // The fix taken at the scan is stamped on the pickup.
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == DeliveriesTable
                && r.ExpressionAttributeNames.Values.Contains("gpsPickup")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelfPickup_NoEscrow_FarmerNonceScanned_Succeeds_AndConsumesTheDeliveredLeg()
    {
        SetupDelivery(SelfPickupDelivery("pending"));
        SetupListingAtTheFarm();
        SetupFarmerNonce("farmer-nonce-1");

        var result = await SendAsync("delivered", BuyerId, "buyer", true,
            ("scannedQrNonce", "farmer-nonce-1"), GpsAtTheFarm("gpsDropoff"));

        result.GetProperty("status").GetString().Should().Be("success");
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "delivered", "op-fup42");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pickup_FarmerNonceAlreadyUsed_IsRefusedAsAReplay(bool selfPickup)
    {
        SetupDelivery(selfPickup ? SelfPickupDelivery("pending") : TruckDelivery("pending"));
        SetupListingAtTheFarm();
        var leg = selfPickup ? "delivered" : "pickedUp";
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = "farmer-nonce-1",
            IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o"),
            ConsumedPickedUpOp = selfPickup ? null : "op-earlier",
            ConsumedDeliveredOp = selfPickup ? "op-earlier" : null
        });

        var result = selfPickup
            ? await SendAsync(leg, BuyerId, "buyer", true,
                ("scannedQrNonce", "farmer-nonce-1"), GpsAtTheFarm("gpsDropoff"))
            : await SendAsync(leg, TruckerId, "trucker", true, ("scannedQrNonce", "farmer-nonce-1"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_REPLAY: ");
        await AssertNothingWrittenAsync();
    }

    // ---- read failures fail closed ----

    [Fact]
    public async Task OrderEscrowReadFails_DropOff_IsDeferred_AndNothingIsWritten()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        _escrow.GetByIdAsync(OrderEscrowId, true)
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        // An undecided gate is never a pass through the index either.
        await _escrow.DidNotReceive().GetByDeliveryIdAsync(Arg.Any<string>());
        // Alarmed (order escrow problems), so a read that keeps failing does not defer unseen.
        Logs.Should().Contain(
            "QR_GATE_UNDECIDED deliveryId=del-fup42 orderId=order-fup42 reason=ProvisionedThroughputExceededException detail=");
    }

    [Fact]
    public async Task DeliveryIndexReadFails_IsDeferred_AndNothingIsWritten()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        _escrow.GetByDeliveryIdAsync(DeliveryId)
            .ThrowsAsync(new AmazonDynamoDBException("index unavailable"));

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
        Logs.Should().Contain("QR_GATE_UNDECIDED deliveryId=del-fup42 orderId=order-fup42 reason=AmazonDynamoDBException");
    }

    [Fact]
    public async Task ReadFails_ForALegacyDeliveryWithoutAnOrder_LogsADashForTheOrder()
    {
        SetupDelivery(TruckDelivery("pickedUp", orderId: null));
        _escrow.GetByDeliveryIdAsync(DeliveryId).ThrowsAsync(new TimeoutException("slow"));

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        Logs.Should().Contain("QR_GATE_UNDECIDED deliveryId=del-fup42 orderId=- reason=TimeoutException");
    }

    [Fact]
    public async Task ReadFails_ExceptionTextCannotSpellAnAlarmToken()
    {
        // The detail is free text (it may echo input): MoneyAlarmLog.LogText turns _ into -.
        SetupDelivery(TruckDelivery("pickedUp"));
        _escrow.GetByIdAsync(OrderEscrowId, true)
            .ThrowsAsync(new AmazonDynamoDBException("ORDER_ESCROW_LINK_FAILED spoof"));

        await SendAsync("delivered", BuyerId, "buyer");

        Logs.Should().Contain("detail=ORDER-ESCROW-LINK-FAILED spoof");
        Logs.Should().NotContain("ORDER_ESCROW_LINK_FAILED");
    }

    [Fact]
    public async Task ReadFails_PastTheDeferWindow_IsStillOnlyDeferred()
    {
        // The server does not know the delivery carries money, so no permanent "contact support".
        SetupDelivery(TruckDelivery("pickedUp", createdAt: DateTime.UtcNow.AddDays(-20)));
        _escrow.GetByIdAsync(OrderEscrowId, true)
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var result = await SendAsync("delivered", BuyerId, "buyer");

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith(NoncePending);
        await AssertNothingWrittenAsync();
    }

    // ---- the farmer's handoff: the nonce decides, the escrow is not consulted ----

    [Fact]
    public async Task FarmerNonceStored_TruckerScansIt_Succeeds_WithoutReadingTheEscrow()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupUnlinkedOrderEscrow();
        SetupFarmerNonce("farmer-nonce-1");

        var result = await SendAsync("pickedUp", TruckerId, "trucker", true, ("scannedQrNonce", "farmer-nonce-1"));

        result.GetProperty("status").GetString().Should().Be("success");
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _escrow.DidNotReceive().GetByDeliveryIdAsync(Arg.Any<string>());
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "pickedUp", "op-fup42");
    }

    [Fact]
    public async Task FarmerNonceStored_ScanMissing_IsRefusedAsBefore()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupFarmerNonce("farmer-nonce-1");

        var result = await SendAsync("pickedUp", TruckerId, "trucker", qrSecrets: true);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_MISSING: QR nonce verification required");
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("pending", "pickedUp")]
    [InlineData("pickedUp", "delivered")]
    public async Task Farmer_CannotDriveTheGatedTransitions_AndNoEscrowIsRead(string from, string to)
    {
        SetupDelivery(TruckDelivery(from));
        SetupUnlinkedOrderEscrow();

        var result = await SendAsync(to, FarmerId, "farmer");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_UNAUTHORIZED: ");
        await AssertNothingWrittenAsync();
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    // ---- helpers ----

    private void SetupUnlinkedOrderEscrow() =>
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow("escrowed"));

    /// <summary>The listing's registered place, which a self-pickup's GPS is checked against.</summary>
    private void SetupListingAtTheFarm() =>
        _crops.GetByIdAsync("listing-fup42").Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-fup42" }, { "farmerId", FarmerId }, { "latitude", "9.0" }, { "longitude", "38.7" }
        });

    /// <summary>A fix at the farm, as the scan screen takes it.</summary>
    private static (string Key, object Value) GpsAtTheFarm(string field) =>
        (field, JsonSerializer.SerializeToElement(new { latitude = 9.0, longitude = 38.7 }));

    private async Task AssertNoEscrowReadAsync()
    {
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _escrow.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await _escrow.DidNotReceive().GetByDeliveryIdAsync(Arg.Any<string>());
    }

    private void SetupFarmerNonce(string nonce) =>
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = nonce,
            IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o")
        });

    /// <summary>An order escrow as funding creates it, with no <c>deliveryId</c>: the link never landed.</summary>
    private static Dictionary<string, object> OrderEscrowRow(string status) => new()
    {
        { "escrowId", OrderEscrowId },
        { "orderId", OrderId },
        { "source", "order" },
        { "status", status },
        { "createdAt", "2026-10-20T08:00:00Z" }
    };

    private void SetupDelivery(Dictionary<string, AttributeValue> item)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = item });
    }

    private static Dictionary<string, AttributeValue> TruckDelivery(
        string status, string? orderId = OrderId, DateTime? createdAt = null)
    {
        var created = (createdAt ?? DateTime.UtcNow.AddDays(-1)).ToString("o");
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-fup42" } },
            { "listingId", new AttributeValue { S = "listing-fup42" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = "1" } }
        };
        if (orderId != null)
        {
            item["orderId"] = new AttributeValue { S = orderId };
        }

        return item;
    }

    private static Dictionary<string, AttributeValue> SelfPickupDelivery(string status, DateTime? createdAt = null)
    {
        var item = TruckDelivery(status, createdAt: createdAt);
        item["truckerId"] = new AttributeValue { S = "" };
        item["tripId"] = new AttributeValue { S = DeliveryId };
        item["deliveryType"] = new AttributeValue { S = "self_pickup" };
        return item;
    }

    private async Task AssertNothingWrittenAsync()
    {
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _escrow.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<Dictionary<string, object>?>());
        await _qrSecrets.DidNotReceive().MarkConsumedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        // Not recorded as processed, so the client's retry is evaluated again.
        await _syncMeta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    private Task<JsonElement> SendAsync(string status, string userId, string role,
        params (string Key, object Value)[] extra) => SendAsync(status, userId, role, qrSecrets: false, extra);

    private Task<JsonElement> SendAsync(string status, string userId, string role, bool qrSecrets,
        params (string Key, object Value)[] extra) =>
        SendWithAsync(new SyncFunction(_syncMeta, _crops, _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(),
            orderRepo: _orders, escrowRepo: _escrow, qrSecretRepo: qrSecrets ? _qrSecrets : null),
            status, userId, role, extra);

    private async Task<JsonElement> SendWithAsync(SyncFunction function, string status, string userId, string role,
        params (string Key, object Value)[] extra)
    {
        var data = new Dictionary<string, object> { { "status", status }, { "version", 1 } };
        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-42",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-fup42",
                        IdempotencyKey = $"{userId}:delivery:{DeliveryId}:update:12345",
                        EntityType = "delivery",
                        EntityId = DeliveryId,
                        Action = "update",
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId },
                    { "custom:role", role }
                }
            }
        };

        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
