using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ACC-02 (account-deletion.md, counterparty money entry points): <c>POST /escrow/initiate</c> and
/// <c>POST /escrow/order-checkout</c> take no money for an order whose buyer, farmer or trucker is
/// being or has been deleted (<c>422 ESCROW_030</c>), and a refund to a deleted buyer never goes to
/// the tombstone's empty phone.
/// </summary>
public class EscrowServiceAccountDeletionTests
{
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string TruckerId = "trucker-789";
    private const string OrderId = "00000000-0000-0000-0000-00000000000a";
    private const string DeliveryId = "delivery-001";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idemRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkouts = Substitute.For<IOrderCheckoutRepository>();

    public EscrowServiceAccountDeletionTests()
    {
        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _users.GetUserByIdAsync(id).Returns(Live(id, role));
            _users.GetUserByIdAsync(id, true).Returns(Live(id, role));
        }

        _escrowRepo.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _idemRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _escrowRepo.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>()).Returns(new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = "PENDING-WEBHOOK-1",
            ToPayUrl = "https://telebirr.example/checkout?prepay_id=prepay-1",
            PrepayId = "prepay-1"
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-REF" });
        _checkouts.TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>()).Returns(true);
        _checkouts.StoreMappingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(true);
    }

    private static UserDto Live(string id, string role) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000000",
        Role = role,
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaId = $"FAYDA-{id}",
        CreatedAt = "2026-02-17T10:00:00.0000000Z",
        UpdatedAt = "2026-02-17T10:00:00.0000000Z",
        Version = 1
    };

    private static UserDto Tombstone(string id, string role, string state) => Live(id, role) with
    {
        PhoneNumber = "",
        Woreda = "",
        IsPhoneVerified = false,
        FaydaVerified = false,
        FaydaId = null,
        DeletionState = state
    };

    /// <summary>The party in <paramref name="role"/> is being (or has been) deleted.</summary>
    private void Deleting(string role, string state = "in_progress")
    {
        var id = role switch { "buyer" => BuyerId, "farmer" => FarmerId, _ => TruckerId };
        _users.GetUserByIdAsync(id, true).Returns(Tombstone(id, role, state));
    }

    private Dictionary<string, object> Order(string status) => new()
    {
        { "orderId", OrderId }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId },
        { "orderTotal", "15000" }, { "status", status }, { "version", "2" }
    };

    // ── POST /escrow/initiate ──

    private EscrowService InitiateService()
    {
        _orders.GetByIdAsync(OrderId).Returns(Order("created"));
        return new EscrowService(_escrowRepo, _auditRepo, _idemRepo, _gateway, _users, _orders);
    }

    private static InitiateEscrowRequest Initiate() => new()
    {
        DeliveryId = DeliveryId,
        OrderId = OrderId,
        BuyerId = BuyerId,
        FarmerId = FarmerId,
        Amount = 15_000m,
        Currency = "ETB",
        IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800000"
    };

    [Theory]
    [InlineData("buyer", "in_progress")]
    [InlineData("farmer", "in_progress")]
    [InlineData("farmer", "completed")]
    [InlineData("trucker", "cognito_pending")]
    public async Task Initiate_ForAnOrderOfADeletingParty_Is422Escrow030_AndHoldsNothing(string role, string state)
    {
        var service = InitiateService();
        Deleting(role, state);

        var result = await service.InitiateAsync(Initiate(), BuyerId);

        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_030");
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
        await _escrowRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        await _idemRepo.DidNotReceiveWithAnyArgs().StoreAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Initiate_ReadsEveryPartyStronglyConsistent_AndPassesLiveParties()
    {
        var service = InitiateService();

        var result = await service.InitiateAsync(Initiate(), BuyerId);

        result.Error?.Code.Should().NotBe("ESCROW_030");
        foreach (var id in new[] { BuyerId, FarmerId, TruckerId })
        {
            await _users.Received(1).GetUserByIdAsync(id, true);
        }
    }

    // ── POST /escrow/order-checkout ──

    private EscrowService CheckoutService()
    {
        _gateway.SupportsHostedCheckout.Returns(true);
        _orders.GetByIdAsync(OrderId, true).Returns(Order("payment_submitted"));
        return new EscrowService(
            _escrowRepo, _auditRepo, _idemRepo, _gateway, _users, _orders, orderCheckoutRepo: _checkouts);
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task Checkout_ForAnOrderOfADeletingParty_Is422Escrow030_BeforeAnySlotOrTelebirrCall(string role)
    {
        var service = CheckoutService();
        Deleting(role);

        var result = await service.CreateOrderCheckoutAsync(OrderId, BuyerId);

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_030");
        await _checkouts.DidNotReceiveWithAnyArgs().GetSlotAsync(default!);
        await _checkouts.DidNotReceiveWithAnyArgs().TryOpenSlotAsync(default!, default);
        _gateway.ReceivedCalls().Where(c => c.GetMethodInfo().Name != "get_SupportsHostedCheckout")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_ALiveOrder_StillOpensItsCheckout()
    {
        var service = CheckoutService();

        var result = await service.CreateOrderCheckoutAsync(OrderId, BuyerId);

        result.IsSuccess.Should().BeTrue();
        await _gateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task Checkout_UsersReadFails_FailsClosed()
    {
        var service = CheckoutService();
        _users.GetUserByIdAsync(FarmerId, true).Returns(
            Task.FromException<UserDto?>(new Amazon.DynamoDBv2.AmazonDynamoDBException("throttled")));

        await FluentActions.Awaiting(() => service.CreateOrderCheckoutAsync(OrderId, BuyerId))
            .Should().ThrowAsync<Amazon.DynamoDBv2.AmazonDynamoDBException>();
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
    }

    // ── refunds to a deleted buyer ──

    [Fact]
    public async Task Refund_ToABuyerWhoseRowIsATombstone_FallsBackToTheBuyerId_NotAnEmptyPhone()
    {
        _users.GetUserByIdAsync(BuyerId).Returns(Tombstone(BuyerId, "buyer", "completed"));
        _escrowRepo.GetByIdAsync("escrow-001").Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" }, { "deliveryId", DeliveryId }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "amount", 15_000m }, { "commission", 375m }, { "commissionRate", 0.025m }, { "farmerPayout", 14_625m },
            { "currency", "ETB" }, { "status", "escrowed" }, { "telebirrRef", "MOCK-HOLD-REF" },
            { "createdAt", "2026-02-17T10:00:00.0000000Z" }, { "updatedAt", "2026-02-17T10:00:00.0000000Z" },
            { "expiresAt", "2026-02-24T10:00:00.0000000Z" }, { "version", 1 }
        });
        var service = new EscrowService(_escrowRepo, _auditRepo, _idemRepo, _gateway, _users);

        var result = await service.RefundAsync(new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            Reason = "No delivery within 7 days.",
            IdempotencyKey = "system:escrow-001:refund:1708171200999"
        }, "system");

        result.IsSuccess.Should().BeTrue();
        await _gateway.Received(1).RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.BuyerPhone == BuyerId));
    }
}
