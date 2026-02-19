using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

public class EscrowServiceTests
{
    private readonly IEscrowRepository _mockEscrowRepo;
    private readonly IEscrowAuditRepository _mockAuditRepo;
    private readonly IEscrowIdempotencyRepository _mockIdempotencyRepo;
    private readonly ITelebirrGateway _mockTelebirrGateway;
    private readonly EscrowService _service;

    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string DeliveryId = "delivery-001";

    public EscrowServiceTests()
    {
        _mockEscrowRepo = Substitute.For<IEscrowRepository>();
        _mockAuditRepo = Substitute.For<IEscrowAuditRepository>();
        _mockIdempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
        _mockTelebirrGateway = Substitute.For<ITelebirrGateway>();

        // Default: Telebirr succeeds
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = true, TelebirrRef = "MOCK-HOLD-REF" });
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "MOCK-RELEASE-REF" });
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-REF" });

        // Default: idempotency store succeeds (no duplicate)
        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        _service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
    }

    private static InitiateEscrowRequest MakeInitiateRequest(
        decimal amount = 15_000m,
        string buyerId = BuyerId,
        string farmerId = FarmerId,
        string currency = "ETB",
        string? idempotencyKey = null)
    {
        return new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = buyerId,
            FarmerId = farmerId,
            Amount = amount,
            Currency = currency,
            IdempotencyKey = idempotencyKey ?? $"{buyerId}:{DeliveryId}:initiate:1708084800000"
        };
    }

    private static Dictionary<string, object> MakeEscrowItem(
        string escrowId = "escrow-001",
        string status = "escrowed",
        decimal amount = 15_000m,
        decimal commission = 375m,
        decimal farmerPayout = 14_625m,
        string buyerId = BuyerId,
        string farmerId = FarmerId)
    {
        return new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
            { "amount", amount },
            { "commission", commission },
            { "commissionRate", 0.025m },
            { "farmerPayout", farmerPayout },
            { "currency", "ETB" },
            { "status", status },
            { "telebirrRef", "MOCK-HOLD-REF" },
            { "telebirrHoldRef", "MOCK-HOLD-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00.0000000Z" },
            { "updatedAt", "2026-02-17T10:00:00.0000000Z" },
            { "expiresAt", "2026-02-24T10:00:00.0000000Z" },
            { "version", 1 }
        };
    }

    // ──────────────────────────────────────────────
    // InitiateAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_Success_ReturnsEscrowedResponse()
    {
        var request = MakeInitiateRequest();

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrowed", result.Value!.Status);
        Assert.Equal(15_000m, result.Value.Amount);
        Assert.Equal(375m, result.Value.Commission);
        Assert.Equal(14_625m, result.Value.FarmerPayout);
        Assert.Equal("ETB", result.Value.Currency);
        Assert.Equal("MOCK-HOLD-REF", result.Value.TelebirrRef);
        Assert.False(result.IsDuplicate);
    }

    [Fact]
    public async Task InitiateAsync_Success_StoresIdempotencyBeforeTelebirr()
    {
        var request = MakeInitiateRequest();

        await _service.InitiateAsync(request, BuyerId);

        await _mockIdempotencyRepo.Received(1).StoreAsync(
            request.IdempotencyKey, Arg.Any<string>(), "processing");
    }

    [Fact]
    public async Task InitiateAsync_Success_CreatesAuditEntries()
    {
        var request = MakeInitiateRequest();

        await _service.InitiateAsync(request, BuyerId);

        // initiate + hold_success = 2 audit entries
        await _mockAuditRepo.Received(2).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task InitiateAsync_DuplicateIdempotency_ReturnsCachedResult()
    {
        var request = MakeInitiateRequest();
        var existingEscrow = MakeEscrowItem();

        _mockIdempotencyRepo.GetAsync(request.IdempotencyKey)
            .Returns(new Dictionary<string, object>
            {
                { "escrowId", "escrow-001" },
                { "status", "completed" }
            });
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(existingEscrow);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsDuplicate);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
    }

    [Fact]
    public async Task InitiateAsync_AmountBelowMin_ReturnsEscrow001()
    {
        var request = MakeInitiateRequest(amount: 50m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_AmountAboveMax_ReturnsEscrow001()
    {
        var request = MakeInitiateRequest(amount: 600_000m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_WrongCurrency_ReturnsEscrow004()
    {
        var request = MakeInitiateRequest(currency: "USD");

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_004", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_SelfTrading_ReturnsError()
    {
        var request = MakeInitiateRequest(buyerId: "same-user", farmerId: "same-user");

        var result = await _service.InitiateAsync(request, "same-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_BuyerIdMismatch_ReturnsEscrow006()
    {
        var request = MakeInitiateRequest(buyerId: "buyer-456");

        var result = await _service.InitiateAsync(request, "different-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_006", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_InvalidIdempotencyKey_ReturnsEscrow003()
    {
        var request = MakeInitiateRequest(idempotencyKey: "bad-key-format");

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_003", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_TelebirrHoldFailure_ReturnsEscrow013()
    {
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_HOLD_FAILED",
                ErrorMessage = "Insufficient balance."
            });

        var request = MakeInitiateRequest();

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_013", result.Error!.Code);
        Assert.Equal(502, result.Error.HttpStatus);
    }

    [Theory]
    [InlineData(15_000, 375)]     // 15000 * 0.025 = 375
    [InlineData(1_000, 25)]       // 1000 * 0.025 = 25
    [InlineData(100_000, 2_500)]  // 100000 * 0.025 = 2500
    public async Task InitiateAsync_CommissionCalculation_Standard(decimal amount, decimal expectedCommission)
    {
        var request = MakeInitiateRequest(amount: amount);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedCommission, result.Value!.Commission);
        Assert.Equal(amount - expectedCommission, result.Value.FarmerPayout);
    }

    [Theory]
    [InlineData(100)]  // 100 * 0.025 = 2.5 → floor to 5
    [InlineData(150)]  // 150 * 0.025 = 3.75 → floor to 5
    [InlineData(199)]  // 199 * 0.025 = 4.975 → floor to 5
    public async Task InitiateAsync_CommissionFloor_FiveEtbMinimum(decimal amount)
    {
        var request = MakeInitiateRequest(amount: amount);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(5.0m, result.Value!.Commission);
    }

    // ──────────────────────────────────────────────
    // ReleaseAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_FromEscrowed_ReturnsCompleted()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.True(result.IsSuccess);
        Assert.Equal("completed", result.Value!.Status);
        Assert.Equal(14_625m, result.Value.FarmerPayout);
        Assert.Equal(375m, result.Value.Commission);
    }

    [Fact]
    public async Task ReleaseAsync_FromDisputed_ReturnsCompleted()
    {
        var escrowItem = MakeEscrowItem(status: "disputed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "dispute_resolved",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "admin:resolver");

        Assert.True(result.IsSuccess);
        Assert.Equal("completed", result.Value!.Status);
    }

    [Fact]
    public async Task ReleaseAsync_InvalidState_ReturnsInvalidTransition()
    {
        var escrowItem = MakeEscrowItem(status: "initiated");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error!.Code);
        Assert.Equal(422, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_NotFound_ReturnsEscrow016()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-999").Returns((Dictionary<string, object>?)null);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-999",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-999:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_016", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_DuplicateIdempotency_ReturnsCached()
    {
        var escrowItem = MakeEscrowItem(status: "completed");
        escrowItem["releasedAt"] = "2026-02-18T10:00:00Z";
        escrowItem["telebirrReleaseRef"] = "MOCK-RELEASE-REF";

        _mockIdempotencyRepo.GetAsync(Arg.Any<string>())
            .Returns(new Dictionary<string, object>
            {
                { "escrowId", "escrow-001" },
                { "status", "completed" }
            });
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.True(result.IsSuccess);
        Assert.True(result.IsDuplicate);
    }

    [Fact]
    public async Task ReleaseAsync_TelebirrFailure_ReturnsEscrow013()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse
            {
                Success = false,
                ErrorMessage = "Release failed."
            });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_013", result.Error!.Code);
        Assert.Equal(502, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // DisputeAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_SuccessAsBuyer_ReturnsDisputed()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            Description = "Teff bags were wet.",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("disputed", result.Value!.Status);
        Assert.Equal(BuyerId, result.Value.DisputedBy);
        Assert.Equal("damaged_goods", result.Value.Reason);
    }

    [Fact]
    public async Task DisputeAsync_SuccessAsFarmer_ReturnsDisputed()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = FarmerId,
            Reason = "wrong_quantity",
            IdempotencyKey = $"{FarmerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, FarmerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("disputed", result.Value!.Status);
    }

    [Fact]
    public async Task DisputeAsync_ExpiredWindow_ReturnsEscrow018()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        // Delivery confirmed 3 days ago — well past 48h window
        escrowItem["deliveryConfirmedAt"] = DateTime.UtcNow.AddDays(-3).ToString("o");

        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_018", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_InvalidReason_ReturnsEscrow017()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "invalid_reason_here",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_017", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_NotPartyToEscrow_ReturnsEscrow019()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = "random-user",
            Reason = "damaged_goods",
            IdempotencyKey = $"random-user:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, "random-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_019", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_DuplicateDispute_ReturnsEscrow020()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["disputeId"] = "dispute-existing";

        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_020", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_InvalidState_ReturnsInvalidTransition()
    {
        var escrowItem = MakeEscrowItem(status: "completed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error!.Code);
        Assert.Equal(422, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_DisputedByMismatchJwt_ReturnsEscrow019()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        // authenticatedUserId is different from disputedBy
        var result = await _service.DisputeAsync(request, "attacker-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_019", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // RefundAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RefundAsync_FromEscrowed_ReturnsRefunded()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            Reason = "Expired.",
            IdempotencyKey = $"system:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.True(result.IsSuccess);
        Assert.Equal("refunded", result.Value!.Status);
        Assert.Equal(15_000m, result.Value.RefundAmount);
    }

    [Fact]
    public async Task RefundAsync_FromDisputed_ReturnsRefunded()
    {
        var escrowItem = MakeEscrowItem(status: "disputed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "dispute_resolved",
            Reason = "Buyer wins.",
            IdempotencyKey = $"admin:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "admin:resolver");

        Assert.True(result.IsSuccess);
        Assert.Equal("refunded", result.Value!.Status);
    }

    [Fact]
    public async Task RefundAsync_InvalidState_ReturnsInvalidTransition()
    {
        var escrowItem = MakeEscrowItem(status: "completed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            IdempotencyKey = $"admin:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error!.Code);
        Assert.Equal(422, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RefundAsync_TelebirrFailure_ReturnsEscrow013()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse
            {
                Success = false,
                ErrorMessage = "Refund failed."
            });

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"system:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_013", result.Error!.Code);
        Assert.Equal(502, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // GetByIdAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_AsBuyer_ReturnsEscrowDto()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var result = await _service.GetByIdAsync("escrow-001", BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
        Assert.Equal(BuyerId, result.Value.BuyerId);
        Assert.Equal(FarmerId, result.Value.FarmerId);
        Assert.Equal(15_000m, result.Value.Amount);
    }

    [Fact]
    public async Task GetByIdAsync_AsFarmer_ReturnsEscrowDto()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var result = await _service.GetByIdAsync("escrow-001", FarmerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsEscrow016()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-999").Returns((Dictionary<string, object>?)null);

        var result = await _service.GetByIdAsync("escrow-999", BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_016", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    [Fact]
    public async Task GetByIdAsync_Unauthorized_ReturnsEscrow022()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var result = await _service.GetByIdAsync("escrow-001", "random-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_022", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // GetHistoryAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetHistoryAsync_AsBuyer_QueriesBuyerIndex()
    {
        var items = new List<Dictionary<string, object>> { MakeEscrowItem() };
        _mockEscrowRepo.QueryByBuyerAsync(BuyerId, null, 20, null)
            .Returns((items, (Dictionary<string, AttributeValue>?)null));

        var result = await _service.GetHistoryAsync(BuyerId, null, null, null, null, 20, null);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Escrows);
        Assert.False(result.Value.HasMore);
    }

    [Fact]
    public async Task GetHistoryAsync_AsFarmer_QueriesFarmerIndex()
    {
        var items = new List<Dictionary<string, object>> { MakeEscrowItem() };
        _mockEscrowRepo.QueryByFarmerAsync(FarmerId, null, 20, null)
            .Returns((items, (Dictionary<string, AttributeValue>?)null));

        var result = await _service.GetHistoryAsync(FarmerId, null, "farmer", null, null, 20, null);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Escrows);
        await _mockEscrowRepo.Received(1).QueryByFarmerAsync(FarmerId, null, 20, null);
        await _mockEscrowRepo.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ──────────────────────────────────────────────
    // State machine: IsValidTransition
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("initiated", "escrowed", true)]
    [InlineData("initiated", "cancelled", true)]
    [InlineData("escrowed", "released", true)]
    [InlineData("escrowed", "disputed", true)]
    [InlineData("escrowed", "refunded", true)]
    [InlineData("disputed", "released", true)]
    [InlineData("disputed", "refunded", true)]
    [InlineData("released", "completed", true)]
    [InlineData("initiated", "released", false)]
    [InlineData("escrowed", "completed", false)]
    [InlineData("completed", "released", false)]
    [InlineData("refunded", "escrowed", false)]
    [InlineData("cancelled", "escrowed", false)]
    public void IsValidTransition_ReturnsExpected(string from, string to, bool expected)
    {
        Assert.Equal(expected, EscrowService.IsValidTransition(from, to));
    }

    // ──────────────────────────────────────────────
    // MapToEscrowDto
    // ──────────────────────────────────────────────

    [Fact]
    public void MapToEscrowDto_BasicMapping_CorrectFields()
    {
        var item = MakeEscrowItem();

        var dto = EscrowService.MapToEscrowDto(item);

        Assert.Equal("escrow-001", dto.EscrowId);
        Assert.Equal(DeliveryId, dto.DeliveryId);
        Assert.Equal(BuyerId, dto.BuyerId);
        Assert.Equal(FarmerId, dto.FarmerId);
        Assert.Equal(15_000m, dto.Amount);
        Assert.Equal(375m, dto.Commission);
        Assert.Equal(0.025m, dto.CommissionRate);
        Assert.Equal(14_625m, dto.FarmerPayout);
        Assert.Equal("ETB", dto.Currency);
        Assert.Equal("escrowed", dto.Status);
        Assert.Null(dto.Dispute);
    }

    [Fact]
    public void MapToEscrowDto_WithDispute_MapsDisputeFields()
    {
        var item = MakeEscrowItem(status: "disputed");
        item["disputeId"] = "dispute-001";
        item["disputedBy"] = BuyerId;
        item["disputeReason"] = "damaged_goods";
        item["disputeDescription"] = "Bags were wet.";
        item["disputeCreatedAt"] = "2026-02-18T14:00:00Z";
        item["disputeEvidenceRefs"] = "photos/damage-1.jpg,photos/damage-2.jpg";

        var dto = EscrowService.MapToEscrowDto(item);

        Assert.NotNull(dto.Dispute);
        Assert.Equal("dispute-001", dto.Dispute!.DisputeId);
        Assert.Equal(BuyerId, dto.Dispute.DisputedBy);
        Assert.Equal("damaged_goods", dto.Dispute.Reason);
        Assert.Equal("Bags were wet.", dto.Dispute.Description);
        Assert.Equal(2, dto.Dispute.EvidenceRefs!.Count);
    }
}
