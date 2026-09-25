using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// The order-funded escrow leg (escrow-protocol.md §Order-funded escrow):
/// CreateFromFundedOrderAsync / LinkDeliveryToOrderEscrowAsync /
/// ScheduleRefundForOrderAsync, plus the ESCROW_024 double-charge guard.
/// The service under test is constructed WITHOUT gateway/idempotency wiring,
/// exactly as SyncFunction builds it.
/// </summary>
public class EscrowServiceOrderFundedTests
{
    private readonly IEscrowRepository _mockEscrowRepo;
    private readonly IEscrowAuditRepository _mockAuditRepo;
    private readonly IDeliveryRepository _mockDeliveryRepo;
    private readonly EscrowService _sut;

    private const string OrderId = "00000000-0000-0000-0000-00000000000a";
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string TruckerId = "trucker-789";
    private const string DeliveryId = "del-001";
    private const string PaymentVerifiedAt = "2026-03-01T10:00:00.0000000Z";

    public EscrowServiceOrderFundedTests()
    {
        _mockEscrowRepo = Substitute.For<IEscrowRepository>();
        _mockAuditRepo = Substitute.For<IEscrowAuditRepository>();
        _mockDeliveryRepo = Substitute.For<IDeliveryRepository>();

        _mockEscrowRepo.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        _sut = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo,
            idempotencyRepo: null, telebirrGateway: null,
            deliveryRepo: _mockDeliveryRepo);
    }

    private static Dictionary<string, object> MakeFundedOrder(
        string buyerId = BuyerId,
        string farmerId = FarmerId,
        decimal? orderTotal = 1128.74m,
        decimal? cropCost = 950.50m,
        decimal? transportCost = 150.00m,
        string? truckerId = TruckerId,
        string status = "funded")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
            { "buyerName", "Mekonnen" },
            { "farmerName", "Abebe" },
            { "status", status },
            { "quantity", 100m },
            { "pricePerUnit", 9.505m },
            { "paymentVerifiedAt", PaymentVerifiedAt },
            { "cbeTransactionId", "CBE-TX-1" },
            { "version", 3 }
        };

        if (orderTotal.HasValue)
        {
            order["orderTotal"] = orderTotal.Value;
        }

        if (cropCost.HasValue)
        {
            order["cropCost"] = cropCost.Value;
        }

        if (transportCost.HasValue)
        {
            order["transportCost"] = transportCost.Value;
        }

        if (truckerId != null)
        {
            order["truckerId"] = truckerId;
        }

        return order;
    }

    private static Dictionary<string, object> MakeOrderEscrow(
        string status = "escrowed", string? deliveryId = null, string? deliveryConfirmedAt = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", $"escrow-order-{OrderId}" },
            { "orderId", OrderId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 1128.74m },
            { "commission", 28.24m },
            { "farmerPayout", 950.50m },
            { "truckerPayout", 150.00m },
            { "currency", "ETB" },
            { "status", status },
            { "source", "order" },
            { "createdAt", PaymentVerifiedAt },
            { "expiresAt", "2026-03-08T10:00:00.0000000Z" },
            { "version", 1 }
        };

        if (deliveryId != null)
        {
            escrow["deliveryId"] = deliveryId;
        }

        if (deliveryConfirmedAt != null)
        {
            escrow["deliveryConfirmedAt"] = deliveryConfirmedAt;
        }

        return escrow;
    }

    // ──────────────────────────────────────────────
    // CreateFromFundedOrderAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CreateFromFundedOrder_WithFullSplit_WritesEscrowedRecordWithExactSplit()
    {
        Dictionary<string, object>? written = null;
        await _mockEscrowRepo.CreateAsync(Arg.Do<Dictionary<string, object>>(r => written = r));

        var actual = await _sut.CreateFromFundedOrderAsync(MakeFundedOrder(), "system");

        Assert.True(actual);
        Assert.NotNull(written);
        Assert.Equal($"escrow-order-{OrderId}", written!["escrowId"]);
        Assert.Equal(PaymentVerifiedAt, written["createdAt"]);
        Assert.Equal("2026-03-08T10:00:00.0000000Z", written["expiresAt"]);
        Assert.Equal("escrowed", written["status"]);
        Assert.Equal("order", written["source"]);
        Assert.Equal(OrderId, written["orderId"]);
        Assert.Equal(1128.74m, written["amount"]);
        Assert.Equal(950.50m, written["farmerPayout"]);
        Assert.Equal(150.00m, written["truckerPayout"]);
        // Commission is the exact remainder: split always reconciles to the amount.
        Assert.Equal(28.24m, written["commission"]);
        Assert.Equal(TruckerId, written["truckerId"]);
        Assert.Equal("CBE-TX-1", written["telebirrRef"]);
        Assert.Equal("CBE-TX-1", written["telebirrHoldRef"]);
        // No deliveryId until a delivery is linked (sparse DeliveryIndex).
        Assert.False(written.ContainsKey("deliveryId"));

        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            (string)a["action"] == "order_funded"
            && (string)a["fromState"] == ""
            && (string)a["toState"] == "escrowed"));
    }

    [Fact]
    public async Task CreateFromFundedOrder_NoCostBreakdown_FallsBackToDefaultCommission()
    {
        Dictionary<string, object>? written = null;
        await _mockEscrowRepo.CreateAsync(Arg.Do<Dictionary<string, object>>(r => written = r));

        var order = MakeFundedOrder(
            orderTotal: 1000m, cropCost: null, transportCost: null, truckerId: null);
        var actual = await _sut.CreateFromFundedOrderAsync(order, "system");

        Assert.True(actual);
        // max(1000 * 0.025, 5) = 25 commission; farmer takes the remainder.
        Assert.Equal(975m, written!["farmerPayout"]);
        Assert.Equal(25m, written["commission"]);
        Assert.False(written.ContainsKey("truckerPayout"));
    }

    [Fact]
    public async Task CreateFromFundedOrder_NoOrderTotal_UsesQuantityTimesPrice()
    {
        Dictionary<string, object>? written = null;
        await _mockEscrowRepo.CreateAsync(Arg.Do<Dictionary<string, object>>(r => written = r));

        var order = MakeFundedOrder(
            orderTotal: null, cropCost: null, transportCost: null, truckerId: null);
        var actual = await _sut.CreateFromFundedOrderAsync(order, "system");

        Assert.True(actual);
        // 100 × 9.505 = 950.50 (legacy fallback).
        Assert.Equal(950.50m, written!["amount"]);
    }

    [Fact]
    public async Task CreateFromFundedOrder_ActiveEscrowExists_ReturnsTrueWithoutCreating()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var actual = await _sut.CreateFromFundedOrderAsync(MakeFundedOrder(), "system");

        Assert.True(actual);
        await _mockEscrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CreateFromFundedOrder_ConditionalCheckFailed_ReturnsTrue()
    {
        _mockEscrowRepo.CreateAsync(Arg.Any<Dictionary<string, object>>())
            .ThrowsAsync(new ConditionalCheckFailedException("duplicate"));

        var actual = await _sut.CreateFromFundedOrderAsync(MakeFundedOrder(), "system");

        Assert.True(actual);
    }

    [Fact]
    public async Task CreateFromFundedOrder_SelfTrade_ReturnsFalseWithoutCreating()
    {
        var actual = await _sut.CreateFromFundedOrderAsync(
            MakeFundedOrder(buyerId: FarmerId, farmerId: FarmerId), "system");

        Assert.False(actual);
        await _mockEscrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CreateFromFundedOrder_MissingParties_ReturnsFalse()
    {
        var order = MakeFundedOrder();
        order.Remove("buyerId");

        var actual = await _sut.CreateFromFundedOrderAsync(order, "system");

        Assert.False(actual);
        await _mockEscrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ──────────────────────────────────────────────
    // LinkDeliveryToOrderEscrowAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task LinkDelivery_UnlinkedEscrow_SetsDeliveryId()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryId"] == DeliveryId));
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            (string)a["action"] == "delivery_linked"));
    }

    [Fact]
    public async Task LinkDelivery_EscrowWithoutTrucker_BackfillsTruckerIdFromOrder()
    {
        // SIM-15: the fixture escrow predates the truck binding (no truckerId), the
        // order carries one. Linking stamps it so the trucker index sees the row.
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["deliveryId"] == DeliveryId && (string)f["truckerId"] == TruckerId));
    }

    [Fact]
    public async Task LinkDelivery_EscrowWithTrucker_KeepsItsTruckerId()
    {
        var escrow = MakeOrderEscrow();
        escrow["truckerId"] = "trucker-original";
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => !f.ContainsKey("truckerId")));
    }

    [Fact]
    public async Task LinkDelivery_OrderWithoutTrucker_StampsNoTruckerId()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted", truckerId: null), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => !f.ContainsKey("truckerId")));
    }

    [Fact]
    public async Task LinkDelivery_AlreadyLinkedToSameDelivery_ReturnsTrueWithoutUpdate()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow(deliveryId: DeliveryId) });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task LinkDelivery_LinkedToActiveDelivery_ReturnsFalse()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow(deliveryId: "del-000") });
        _mockDeliveryRepo.GetByIdAsync("del-000").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-000" }, { "status", "pending" }
        });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.False(actual);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task LinkDelivery_LinkedToCancelledDelivery_Relinks()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow(deliveryId: "del-000") });
        _mockDeliveryRepo.GetByIdAsync("del-000").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-000" }, { "status", "cancelled" }
        });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryId"] == DeliveryId));
    }

    [Fact]
    public async Task LinkDelivery_TerminalOrder_ReturnsFalse()
    {
        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "rejected"), "system");

        Assert.False(actual);
        await _mockEscrowRepo.DidNotReceive().QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task LinkDelivery_NoEscrow_SelfHealsThenLinks()
    {
        // First lookup: nothing (funding-time create failed). After the self-heal
        // create, the lookup returns the fresh record.
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(
                new List<Dictionary<string, object>>(),
                new List<Dictionary<string, object>>(),
                new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(), "system");

        Assert.True(actual);
        await _mockEscrowRepo.Received(1).CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryId"] == DeliveryId));
    }

    [Fact]
    public async Task LinkDelivery_GsiLagsButDeterministicIdResolves_Links()
    {
        // The OrderIndex GSI is eventually consistent — a just-created escrow can
        // be missing from it. The deterministic base-table id must close the gap,
        // otherwise the delivery stays permanently unlinked (money never released).
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.GetByIdAsync($"escrow-order-{OrderId}")
            .Returns(MakeOrderEscrow());

        var actual = await _sut.LinkDeliveryToOrderEscrowAsync(
            OrderId, DeliveryId, MakeFundedOrder(status: "accepted"), "system");

        Assert.True(actual);
        await _mockEscrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            $"escrow-order-{OrderId}", PaymentVerifiedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryId"] == DeliveryId));
    }

    // ──────────────────────────────────────────────
    // ScheduleRefundForOrderAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ScheduleRefund_EscrowedUnconfirmed_ExpiresNowWithReason()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        Dictionary<string, object>? fields = null;
        await _mockEscrowRepo.UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Do<Dictionary<string, object>?>(f => fields = f));

        var actual = await _sut.ScheduleRefundForOrderAsync(OrderId, "order_rejected", "system");

        Assert.True(actual);
        Assert.NotNull(fields);
        Assert.Equal("order_rejected", fields!["refundReason"]);
        var expiresAt = DateTime.Parse((string)fields["expiresAt"], null,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.True(expiresAt <= DateTime.UtcNow.AddMinutes(1));
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            (string)a["action"] == "refund_scheduled"));
    }

    [Fact]
    public async Task ScheduleRefund_DeliveryConfirmed_ReturnsFalse()
    {
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>
            {
                MakeOrderEscrow(deliveryConfirmedAt: "2026-03-02T10:00:00.0000000Z")
            });

        var actual = await _sut.ScheduleRefundForOrderAsync(OrderId, "order_cancelled", "system");

        Assert.False(actual);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task ScheduleRefund_NoActiveEscrow_ReturnsFalse()
    {
        var actual = await _sut.ScheduleRefundForOrderAsync(OrderId, "order_rejected", "system");

        Assert.False(actual);
    }

    // ──────────────────────────────────────────────
    // InitiateAsync double-charge guard (ESCROW_024)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_OrderAlreadyEscrowed_Returns409Escrow024()
    {
        var idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
        var gateway = Substitute.For<ITelebirrGateway>();
        var fullService = new EscrowService(_mockEscrowRepo, _mockAuditRepo, idempotencyRepo, gateway);

        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId)
            .Returns((Dictionary<string, object>?)null);
        _mockEscrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { MakeOrderEscrow() });

        var result = await fullService.InitiateAsync(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            OrderId = OrderId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 500m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:12345"
        }, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_024", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }
}
