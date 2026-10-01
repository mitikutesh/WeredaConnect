using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-01: POST /escrow/order-checkout (EscrowService.CreateOrderCheckoutAsync). An order has
/// at most one open Telebirr checkout, the amount and merch_order_id come from the server,
/// and the merch_order_id mapping is never removed when Telebirr's answer is unknown.
/// </summary>
public class EscrowServiceOrderCheckoutTests
{
    private const string OrderId = "00000000-0000-0000-0000-00000000000a";
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string PrepayId = "prepay-1";

    private static readonly DateTime FixedNow = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idemRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkoutRepo = Substitute.For<IOrderCheckoutRepository>();
    private readonly EscrowService _sut;

    public EscrowServiceOrderCheckoutTests()
    {
        _gateway.SupportsHostedCheckout.Returns(true);
        _checkoutRepo.TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>()).Returns(true);
        _checkoutRepo.StoreMappingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(true);
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>()).Returns(new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = "PENDING-WEBHOOK-1",
            ToPayUrl = "https://telebirr.example/checkout?prepay_id=prepay-1",
            PrepayId = PrepayId
        });
        StubOrder("payment_submitted");

        _sut = new EscrowService(
            _escrowRepo, _auditRepo, _idemRepo, _gateway,
            orderRepo: _orderRepo, orderCheckoutRepo: _checkoutRepo);
    }

    private void StubOrder(
        string status, string buyerId = BuyerId, string farmerId = FarmerId, string orderTotal = "15000",
        DateTime? paymentSubmittedAt = null, DateTime? createdAt = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "buyerId", buyerId }, { "farmerId", farmerId },
            { "orderTotal", orderTotal }, { "status", status }, { "version", "2" }
        };
        if (paymentSubmittedAt != null)
        {
            order["paymentSubmittedAt"] = paymentSubmittedAt.Value.ToString("o");
        }

        if (createdAt != null)
        {
            order["createdAt"] = createdAt.Value.ToString("o");
        }

        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);
    }

    /// <summary>A checkout's openUntil once Telebirr answered between the two instants: window plus margin, 130 minutes.</summary>
    private static bool IsFreshOpenUntil(DateTime? openUntil, DateTime before, DateTime after)
        => openUntil != null && openUntil >= before.AddMinutes(130) && openUntil <= after.AddMinutes(130);

    private static OrderCheckoutSlot Slot(string outcome, int attempt = 1, string? prepayId = null, bool expired = false)
        => new()
        {
            OrderId = OrderId,
            BuyerId = BuyerId,
            Attempt = attempt,
            MerchOrderId = EscrowService.BuildOrderMerchOrderId(OrderId, attempt),
            Amount = 15000m,
            Outcome = outcome,
            OpenedAtUtc = expired ? FixedNow.AddDays(-2) : DateTime.UtcNow.AddMinutes(-5),
            OpenUntilUtc = expired ? FixedNow.AddDays(-2).AddHours(2) : DateTime.UtcNow.AddMinutes(115),
            PrepayId = prepayId
        };

    [Fact]
    public async Task CreateOrderCheckout_PaymentSubmittedOrder_OpensACheckoutWithServerAmount()
    {
        var merchOrderId = EscrowService.BuildOrderMerchOrderId(OrderId, 1);
        var before = DateTime.UtcNow;

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        var after = DateTime.UtcNow;
        Assert.True(actual.IsSuccess);
        Assert.Equal(15000m, actual.Value!.Amount);
        Assert.Equal("ETB", actual.Value.Currency);
        Assert.False(actual.Value.IsExisting);
        Assert.Contains("prepay_id=prepay-1", actual.Value.ToPayUrl);
        await _checkoutRepo.Received(1).TryOpenSlotAsync(
            Arg.Is<OrderCheckoutSlot>(s => s.Attempt == 1 && s.MerchOrderId == merchOrderId
                && s.Outcome == OrderCheckoutOutcome.Opening && s.Amount == 15000m),
            null);
        await _checkoutRepo.Received(1).StoreMappingAsync(merchOrderId, OrderId, BuyerId, 15000m, null);
        await _gateway.Received(1).HoldFundsAsync(Arg.Is<HoldFundsRequest>(r =>
            r.TransactionRef == merchOrderId && r.Amount == 15000m));
        await _checkoutRepo.Received(1).UpdateSlotOutcomeAsync(
            OrderId, 1, OrderCheckoutOutcome.UrlIssued, PrepayId,
            Arg.Is<DateTime?>(d => IsFreshOpenUntil(d, before, after)));
        Assert.True(IsFreshOpenUntil(DateTime.Parse(actual.Value.OpenUntil, null,
            System.Globalization.DateTimeStyles.RoundtripKind), before, after));
    }

    [Fact]
    public async Task CreateOrderCheckout_OpenCheckoutWithPrepayId_ReturnsTheSamePayment()
    {
        _checkoutRepo.GetSlotAsync(OrderId).Returns(Slot(OrderCheckoutOutcome.UrlIssued, prepayId: PrepayId));
        _gateway.BuildCheckoutUrl(PrepayId).Returns("https://telebirr.example/checkout?prepay_id=prepay-1&resigned=1");

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        Assert.True(actual.Value!.IsExisting);
        Assert.EndsWith("resigned=1", actual.Value.ToPayUrl);
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task CreateOrderCheckout_OpenCheckoutWithUnknownOutcome_RefusesUntilTheWindowEnds()
    {
        _checkoutRepo.GetSlotAsync(OrderId).Returns(Slot(OrderCheckoutOutcome.Unknown));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.False(actual.IsSuccess);
        Assert.Equal(409, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_032", actual.Error.Code);
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_AttemptThatNeverReachedTelebirr_IsFinished()
    {
        var slot = Slot(OrderCheckoutOutcome.Opening);
        _checkoutRepo.GetSlotAsync(OrderId).Returns(slot);
        _checkoutRepo.GetMappingAsync(slot.MerchOrderId).Returns((OrderCheckoutMapping?)null);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        await _checkoutRepo.Received(1).StoreMappingAsync(slot.MerchOrderId, OrderId, BuyerId, 15000m, null);
        await _gateway.Received(1).HoldFundsAsync(Arg.Is<HoldFundsRequest>(r => r.TransactionRef == slot.MerchOrderId));
    }

    [Fact]
    public async Task CreateOrderCheckout_AttemptResumedAfterThePaymentTime_NeverReachesTelebirr()
    {
        StubOrder("payment_submitted", paymentSubmittedAt: DateTime.UtcNow.AddHours(-21));
        var slot = Slot(OrderCheckoutOutcome.Opening);
        _checkoutRepo.GetSlotAsync(OrderId).Returns(slot);
        _checkoutRepo.GetMappingAsync(slot.MerchOrderId).Returns((OrderCheckoutMapping?)null);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(409, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_030", actual.Error.Code);
        await _checkoutRepo.DidNotReceive().StoreMappingAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>());
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_BindsTheMappingToTheOrdersPaymentAttempt()
    {
        var submittedAt = DateTime.UtcNow.AddHours(-1);
        StubOrder("payment_submitted", paymentSubmittedAt: submittedAt);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        await _checkoutRepo.Received(1).StoreMappingAsync(
            EscrowService.BuildOrderMerchOrderId(OrderId, 1), OrderId, BuyerId, 15000m, submittedAt.ToString("o"));
    }

    [Theory]
    [InlineData(-21, false)]
    [InlineData(-19, true)]
    public async Task CreateOrderCheckout_OrderWithoutAPaymentClock_CountsFromItsCreation(int createdHoursAgo, bool opens)
    {
        StubOrder("payment_submitted", createdAt: DateTime.UtcNow.AddHours(createdHoursAgo));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(opens, actual.IsSuccess);
        if (!opens)
        {
            Assert.Equal("ESCROW_030", actual.Error!.Code);
            await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
        }
    }

    [Fact]
    public async Task CreateOrderCheckout_FinishedAttempt_MeasuresItsWindowFromTelebirrsAnswer()
    {
        var slot = Slot(OrderCheckoutOutcome.Opening) with { OpenUntilUtc = DateTime.UtcNow.AddMinutes(1) };
        _checkoutRepo.GetSlotAsync(OrderId).Returns(slot);
        _checkoutRepo.GetMappingAsync(slot.MerchOrderId).Returns((OrderCheckoutMapping?)null);
        var before = DateTime.UtcNow;

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        var after = DateTime.UtcNow;
        await _checkoutRepo.Received(1).UpdateSlotOutcomeAsync(
            OrderId, 1, OrderCheckoutOutcome.UrlIssued, PrepayId,
            Arg.Is<DateTime?>(d => IsFreshOpenUntil(d, before, after)));
        Assert.True(IsFreshOpenUntil(DateTime.Parse(actual.Value!.OpenUntil, null,
            System.Globalization.DateTimeStyles.RoundtripKind), before, after));
    }

    [Fact]
    public async Task CreateOrderCheckout_PaymentTimeRunOut_RefusesANewAttempt()
    {
        StubOrder("payment_submitted", paymentSubmittedAt: DateTime.UtcNow.AddHours(-21));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(409, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_030", actual.Error.Code);
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_PaymentTimeRunOut_StillReturnsTheOpenCheckout()
    {
        StubOrder("payment_submitted", paymentSubmittedAt: DateTime.UtcNow.AddHours(-21));
        _checkoutRepo.GetSlotAsync(OrderId).Returns(Slot(OrderCheckoutOutcome.UrlIssued, prepayId: PrepayId));
        _gateway.BuildCheckoutUrl(PrepayId).Returns("https://telebirr.example/checkout?prepay_id=prepay-1");

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        Assert.True(actual.Value!.IsExisting);
    }

    [Fact]
    public async Task CreateOrderCheckout_WithinThePaymentTime_OpensACheckout()
    {
        StubOrder("payment_submitted", paymentSubmittedAt: DateTime.UtcNow.AddHours(-19));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        await _gateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_OpeningAttemptAlreadyMapped_IsTreatedAsUnknown()
    {
        var slot = Slot(OrderCheckoutOutcome.Opening);
        _checkoutRepo.GetSlotAsync(OrderId).Returns(slot);
        _checkoutRepo.GetMappingAsync(slot.MerchOrderId)
            .Returns(new OrderCheckoutMapping(slot.MerchOrderId, OrderId, BuyerId, 15000m));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_032", actual.Error!.Code);
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_AfterTheWindowEnds_OpensTheNextAttempt()
    {
        _checkoutRepo.GetSlotAsync(OrderId).Returns(Slot(OrderCheckoutOutcome.UrlIssued, prepayId: PrepayId, expired: true));
        var nextMerchOrderId = EscrowService.BuildOrderMerchOrderId(OrderId, 2);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.True(actual.IsSuccess);
        await _checkoutRepo.Received(1).TryOpenSlotAsync(
            Arg.Is<OrderCheckoutSlot>(s => s.Attempt == 2 && s.MerchOrderId == nextMerchOrderId), 1);
        await _gateway.Received(1).HoldFundsAsync(Arg.Is<HoldFundsRequest>(r => r.TransactionRef == nextMerchOrderId));
    }

    [Fact]
    public async Task CreateOrderCheckout_ConcurrentOpener_LosesWithoutCallingTelebirr()
    {
        _checkoutRepo.TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>()).Returns(false);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_032", actual.Error!.Code);
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task CreateOrderCheckout_SomeoneElsesOrder_IsForbidden()
    {
        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, "buyer-other");

        Assert.Equal(403, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_026", actual.Error.Code);
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
    }

    [Theory]
    [InlineData("funded")]
    [InlineData("accepted")]
    public async Task CreateOrderCheckout_AlreadyFundedOrder_IsRefused(string status)
    {
        StubOrder(status);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_024", actual.Error!.Code);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("cancelled")]
    public async Task CreateOrderCheckout_OrderNotWaitingForPayment_IsRefused(string status)
    {
        StubOrder(status);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(409, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_030", actual.Error.Code);
    }

    [Fact]
    public async Task CreateOrderCheckout_SelfTradeOrder_IsRefused()
    {
        StubOrder("payment_submitted", farmerId: BuyerId);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(422, actual.Error!.HttpStatus);
        await _gateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Theory]
    [InlineData("99.99")]
    [InlineData("500000.01")]
    public async Task CreateOrderCheckout_AmountOutsideTelebirrLimits_IsRefused(string orderTotal)
    {
        StubOrder("payment_submitted", orderTotal: orderTotal);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_001", actual.Error!.Code);
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task CreateOrderCheckout_MockGateway_RefusesBeforeAnyWrite()
    {
        _gateway.SupportsHostedCheckout.Returns(false);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_031", actual.Error!.Code);
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task CreateOrderCheckout_OrderMissing_ReturnsNotFound()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal(404, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_033", actual.Error.Code);
    }

    [Theory]
    [InlineData("TELEBIRR_TIMEOUT")]
    [InlineData("TELEBIRR_NETWORK")]
    [InlineData("TELEBIRR_PREORDER")]
    public async Task CreateOrderCheckout_UncertainGatewayFailure_KeepsTheMappingAndBlocksTheSlot(string errorCode)
    {
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = false, ErrorCode = errorCode });
        var merchOrderId = EscrowService.BuildOrderMerchOrderId(OrderId, 1);
        var before = DateTime.UtcNow;

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        var after = DateTime.UtcNow;
        Assert.Equal(502, actual.Error!.HttpStatus);
        Assert.Equal("ESCROW_013", actual.Error.Code);
        await _checkoutRepo.Received(1).UpdateSlotOutcomeAsync(
            OrderId, 1, OrderCheckoutOutcome.Unknown, null,
            Arg.Is<DateTime?>(d => IsFreshOpenUntil(d, before, after)));
        await _idemRepo.DidNotReceive().UpdateAsync(merchOrderId, "failed", Arg.Any<string?>());
        await _idemRepo.DidNotReceive().DeleteAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CreateOrderCheckout_GatewayThrows_IsTreatedAsUncertain()
    {
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns<HoldFundsResponse>(_ => throw new InvalidOperationException("boom"));

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_013", actual.Error!.Code);
        await _checkoutRepo.Received(1).UpdateSlotOutcomeAsync(
            OrderId, 1, OrderCheckoutOutcome.Unknown, null, Arg.Is<DateTime?>(d => d != null));
    }

    [Fact]
    public async Task CreateOrderCheckout_DefiniteGatewayRefusal_FreesTheSlot()
    {
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = false, ErrorCode = "TELEBIRR_ERR_AMOUNT" });
        var merchOrderId = EscrowService.BuildOrderMerchOrderId(OrderId, 1);

        var actual = await _sut.CreateOrderCheckoutAsync(OrderId, BuyerId);

        Assert.Equal("ESCROW_013", actual.Error!.Code);
        await _checkoutRepo.Received(1).UpdateSlotOutcomeAsync(OrderId, 1, OrderCheckoutOutcome.Failed, null, null);
        await _idemRepo.Received(1).UpdateAsync(merchOrderId, "failed", Arg.Any<string?>());
    }

    [Fact]
    public void BuildOrderMerchOrderId_IsAHashOfTheOrderIdPlusTheAttempt()
    {
        var first = EscrowService.BuildOrderMerchOrderId(OrderId, 1);
        var second = EscrowService.BuildOrderMerchOrderId(OrderId, 2);
        var longId = EscrowService.BuildOrderMerchOrderId(new string('a', 80), 1);

        Assert.Equal("WCO39D3159FE171BAD33894DB64001", first);
        Assert.Equal("WCO39D3159FE171BAD33894DB64002", second);
        Assert.All(new[] { first, second, longId }, id =>
        {
            Assert.Equal(30, id.Length);
            Assert.True(id.All(char.IsAsciiLetterOrDigit));
        });
    }

    [Theory]
    [InlineData("order-1", "order_1")]
    [InlineData("order-1", "order1")]
    [InlineData("order_1", "order1")]
    public void BuildOrderMerchOrderId_OrderIdsThatDifferOnlyInPunctuation_NeverShareAnId(string one, string other)
    {
        var actual = EscrowService.BuildOrderMerchOrderId(one, 1);

        Assert.NotEqual(EscrowService.BuildOrderMerchOrderId(other, 1), actual);
    }

    [Fact]
    public async Task CreateFromFundedOrder_PrefersTheServerTelebirrReferenceOverTheClientOne()
    {
        _escrowRepo.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "orderTotal", "15000" }, { "cropCost", "14300" }, { "status", "funded" },
            { "cbeTransactionId", "CLIENT-TYPED" }, { "telebirrPaymentOrderId", "TB-REAL" },
            { "paymentVerifiedAt", "2026-09-30T09:00:00.0000000Z" }
        };

        await _sut.CreateFromFundedOrderAsync(order, "telebirr");

        await _escrowRepo.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(r =>
            (string)r["telebirrRef"] == "TB-REAL" && (string)r["telebirrHoldRef"] == "TB-REAL"));
    }
}
