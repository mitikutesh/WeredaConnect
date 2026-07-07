using System.Globalization;
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
    private readonly IUserRepository _mockUserRepo;
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

        // Default users: valid, phone-verified, Fayda-verified buyer + farmer so the standard
        // (high-value) initiate flow passes user + Fayda validation. Individual tests override
        // by constructing their own service via CreateServiceWithUserRepo.
        _mockUserRepo = Substitute.For<IUserRepository>();
        _mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
        _mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));

        _service = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway, _mockUserRepo);
    }

    /// <summary>Build a phone- and Fayda-verified user with the given role.</summary>
    private static UserDto MakeVerifiedUser(string userId, string role) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900000000",
        Role = role,
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaId = $"FAYDA-{userId}",
        CreatedAt = "2026-02-17T10:00:00.0000000Z",
        UpdatedAt = "2026-02-17T10:00:00.0000000Z",
        Version = 1
    };

    /// <summary>Build a phone-verified but NOT Fayda-verified user with the given role.</summary>
    private static UserDto MakeUnverifiedFaydaUser(string userId, string role) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900000000",
        Role = role,
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        FaydaVerified = false,
        CreatedAt = "2026-02-17T10:00:00.0000000Z",
        UpdatedAt = "2026-02-17T10:00:00.0000000Z",
        Version = 1
    };

    /// <summary>
    /// Helper to create an EscrowService with IUserRepository for Fayda and role-verification tests.
    /// </summary>
    private EscrowService CreateServiceWithUserRepo(IUserRepository userRepo)
    {
        return new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway, userRepo);
    }

    /// <summary>
    /// Helper to create a UserDto with the admin role for role-verification tests.
    /// </summary>
    private static UserDto MakeAdminUser(string userId) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900000000",
        Role = "admin",
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-17T10:00:00.0000000Z",
        UpdatedAt = "2026-02-17T10:00:00.0000000Z",
        Version = 1
    };

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

        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("resolver").Returns(MakeAdminUser("resolver"));
        var service = CreateServiceWithUserRepo(mockUserRepo);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "dispute_resolved",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await service.ReleaseAsync(request, "resolver", role: "admin");

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

    [Fact]
    public async Task ReleaseAsync_TelebirrFailure_DeletesIdempotencyKeyNotMarkFailed()
    {
        // C-1: a transient Telebirr failure must DELETE the idempotency record,
        // not mark it "failed". A lingering record makes the retry's conditional
        // StoreAsync return false → permanent ESCROW_003 (409).
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorMessage = "Release failed." });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.False(result.IsSuccess);
        await _mockIdempotencyRepo.Received(1).DeleteAsync(request.IdempotencyKey);
        await _mockIdempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), "failed");
    }

    [Fact]
    public async Task ReleaseAsync_RetryAfterTransientFailure_IsNotBlocked()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        // Model the conditional idempotency store: it only succeeds when no
        // record is currently present. DeleteAsync clears it; had the code left
        // a "failed" record instead, the retry's StoreAsync would return false.
        var recordPresent = false;
        _mockIdempotencyRepo
            .StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                if (recordPresent) return false; // conditional write would fail
                recordPresent = true;
                return true;
            });
        _mockIdempotencyRepo.When(r => r.DeleteAsync(Arg.Any<string>()))
            .Do(_ => recordPresent = false);

        // Telebirr fails on the first attempt, then succeeds on the retry.
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(
                new ReleaseFundsResponse { Success = false, ErrorMessage = "Transient" },
                new ReleaseFundsResponse { Success = true, TelebirrRef = "MOCK-RELEASE-REF" });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        };

        var first = await _service.ReleaseAsync(request, "system");
        Assert.False(first.IsSuccess);
        Assert.Equal("ESCROW_013", first.Error!.Code);

        // Same idempotency key — must NOT be permanently blocked with ESCROW_003.
        var second = await _service.ReleaseAsync(request, "system");
        Assert.True(second.IsSuccess);
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

        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("resolver").Returns(MakeAdminUser("resolver"));
        var service = CreateServiceWithUserRepo(mockUserRepo);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "dispute_resolved",
            Reason = "Buyer wins.",
            IdempotencyKey = $"admin:escrow-001:refund:1708257600000"
        };

        var result = await service.RefundAsync(request, "resolver", role: "admin");

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

    [Fact]
    public async Task RefundAsync_TelebirrFailure_DeletesIdempotencyKeyNotMarkFailed()
    {
        // C-1: same retryability guarantee as release — a transient refund
        // failure deletes the idempotency record so the retry isn't 409'd.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorMessage = "Refund failed." });

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = "system:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_013", result.Error!.Code);
        await _mockIdempotencyRepo.Received(1).DeleteAsync(request.IdempotencyKey);
        await _mockIdempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), "failed");
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
    // GetByDeliveryAsync (F-33)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetByDeliveryAsync_AsBuyer_ReturnsEscrowDto()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByDeliveryIdAsync("delivery-001").Returns(escrowItem);

        var result = await _service.GetByDeliveryAsync("delivery-001", BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
        Assert.Equal(BuyerId, result.Value.BuyerId);
    }

    [Fact]
    public async Task GetByDeliveryAsync_AsFarmer_ReturnsEscrowDto()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByDeliveryIdAsync("delivery-001").Returns(escrowItem);

        var result = await _service.GetByDeliveryAsync("delivery-001", FarmerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
    }

    [Fact]
    public async Task GetByDeliveryAsync_NotFound_ReturnsEscrow016()
    {
        _mockEscrowRepo.GetByDeliveryIdAsync("delivery-999")
            .Returns((Dictionary<string, object>?)null);

        var result = await _service.GetByDeliveryAsync("delivery-999", BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_016", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    [Fact]
    public async Task GetByDeliveryAsync_NonParty_ReturnsEscrow022()
    {
        var escrowItem = MakeEscrowItem();
        _mockEscrowRepo.GetByDeliveryIdAsync("delivery-001").Returns(escrowItem);

        // A trucker (or any non-party) must not read a delivery's escrow.
        var result = await _service.GetByDeliveryAsync("delivery-001", "trucker-xyz");

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

    // ──────────────────────────────────────────────
    // Security: M1 — Concurrent idempotency key writes
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_ConcurrentIdempotencyStore_Returns409()
    {
        // Simulate: StoreAsync returns false (another request already claimed the key)
        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var request = MakeInitiateRequest();

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_003", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_ConcurrentIdempotencyStore_Returns409()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_003", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task DisputeAsync_ConcurrentIdempotencyStore_Returns409()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_003", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RefundAsync_ConcurrentIdempotencyStore_Returns409()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"system:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_003", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // Security: M2 — Authorization checks on release/refund
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_RegularUser_Returns403()
    {
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"{BuyerId}:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_015", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_AsAdmin_Succeeds()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("admin-user").Returns(MakeAdminUser("admin-user"));
        var service = CreateServiceWithUserRepo(mockUserRepo);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"admin:escrow-001:release:1708171200000"
        };

        var result = await service.ReleaseAsync(request, "admin-user", role: "admin");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RefundAsync_RegularUser_Returns403()
    {
        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"{BuyerId}:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_021", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RefundAsync_FarmerCannotRefund_Returns403()
    {
        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"{FarmerId}:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, FarmerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_021", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_JwtAdminButDbFarmer_Returns403()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("fake-admin").Returns(new UserDto
        {
            UserId = "fake-admin",
            PhoneNumber = "+251900000001",
            Role = "Farmer",
            Woreda = "test-woreda",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-17T10:00:00.0000000Z",
            UpdatedAt = "2026-02-17T10:00:00.0000000Z",
            Version = 1
        });
        var service = CreateServiceWithUserRepo(mockUserRepo);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"fake-admin:escrow-001:release:1708171200000"
        };

        var result = await service.ReleaseAsync(request, "fake-admin", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_015", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_JwtAdminButUserNotFound_Returns403()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("ghost-admin").Returns((UserDto?)null);
        var service = CreateServiceWithUserRepo(mockUserRepo);

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"ghost-admin:escrow-001:release:1708171200000"
        };

        var result = await service.ReleaseAsync(request, "ghost-admin", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_015", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ReleaseAsync_NoUserRepoInjected_AdminReturns403()
    {
        // A service with no IUserRepository (null) must fail-closed on admin verification.
        var noRepoService = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"admin:escrow-001:release:1708171200000"
        };

        var result = await noRepoService.ReleaseAsync(request, "admin-user", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_015", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // Security: M4 — Fayda ID enforcement for high-value transactions
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_HighValueWithoutBuyerFaydaId_Returns403()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251912345678",
            Role = "Buyer",
            Woreda = "test-woreda",
            FaydaVerified = false, // Not Fayda-verified
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251922222222",
            Role = "Farmer",
            Woreda = "test-woreda",
            FaydaVerified = true,
            FaydaId = "FAYDA-FARMER-001",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var serviceWithUserRepo = CreateServiceWithUserRepo(mockUserRepo);
        var request = MakeInitiateRequest(amount: 15_000m); // Above 10,000 threshold

        var result = await serviceWithUserRepo.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_007", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
        Assert.Contains("Buyer", result.Error.Message);
    }

    [Fact]
    public async Task InitiateAsync_HighValueWithoutFarmerFaydaId_Returns403()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251912345678",
            Role = "Buyer",
            Woreda = "test-woreda",
            FaydaVerified = true,
            FaydaId = "FAYDA-BUYER-001",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251922222222",
            Role = "Farmer",
            Woreda = "test-woreda",
            FaydaVerified = false, // Not Fayda-verified
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var serviceWithUserRepo = CreateServiceWithUserRepo(mockUserRepo);
        var request = MakeInitiateRequest(amount: 15_000m);

        var result = await serviceWithUserRepo.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_007", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
        Assert.Contains("Farmer", result.Error.Message);
    }

    [Fact]
    public async Task InitiateAsync_HighValueWithBothFaydaIds_Succeeds()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251912345678",
            Role = "Buyer",
            Woreda = "test-woreda",
            FaydaVerified = true,
            FaydaId = "FAYDA-BUYER-001",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251922222222",
            Role = "Farmer",
            Woreda = "test-woreda",
            FaydaVerified = true,
            FaydaId = "FAYDA-FARMER-001",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var serviceWithUserRepo = CreateServiceWithUserRepo(mockUserRepo);
        var request = MakeInitiateRequest(amount: 15_000m);

        var result = await serviceWithUserRepo.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("escrowed", result.Value!.Status);
    }

    [Fact]
    public async Task InitiateAsync_BelowFaydaThreshold_DoesNotRequireFayda()
    {
        // Below the 10,000 ETB threshold, an unverified (non-Fayda) buyer is still allowed.
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeUnverifiedFaydaUser(BuyerId, "buyer"));
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));

        var serviceWithUserRepo = CreateServiceWithUserRepo(mockUserRepo);
        var request = MakeInitiateRequest(amount: 5_000m); // Below 10,000 threshold

        var result = await serviceWithUserRepo.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateAsync_ExactFaydaThreshold_DoesNotRequireFayda()
    {
        // amount == 10,000 should NOT trigger Fayda check (it's > 10,000, not >=)
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeUnverifiedFaydaUser(BuyerId, "buyer"));
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));

        var serviceWithUserRepo = CreateServiceWithUserRepo(mockUserRepo);
        var request = MakeInitiateRequest(amount: 10_000m);

        var result = await serviceWithUserRepo.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    // ──────────────────────────────────────────────
    // Security: S3 — Number formatting is locale-invariant
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(1.5)]
    [InlineData(0.001)]
    [InlineData(1234.56)]
    public void ToAttributeValue_DecimalValues_UseInvariantCulture(double value)
    {
        var decimalValue = (decimal)value;
        var result = CropRepository.ToAttributeValue(decimalValue);

        Assert.NotNull(result.N);
        // Must use '.' as decimal separator regardless of culture
        Assert.DoesNotContain(",", result.N);
        Assert.Equal(decimalValue.ToString(CultureInfo.InvariantCulture), result.N);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(0.001)]
    [InlineData(1234.56)]
    public void ToAttributeValue_DoubleValues_UseInvariantCulture(double value)
    {
        var result = CropRepository.ToAttributeValue(value);

        Assert.NotNull(result.N);
        Assert.DoesNotContain(",", result.N);
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), result.N);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-100)]
    [InlineData(0)]
    public void ToAttributeValue_IntValues_UseInvariantCulture(int value)
    {
        var result = CropRepository.ToAttributeValue(value);

        Assert.NotNull(result.N);
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), result.N);
    }

    [Theory]
    [InlineData(9999999999L)]
    [InlineData(-1L)]
    public void ToAttributeValue_LongValues_UseInvariantCulture(long value)
    {
        var result = CropRepository.ToAttributeValue(value);

        Assert.NotNull(result.N);
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), result.N);
    }

    // ──────────────────────────────────────────────
    // Dispute input validation (S2 security fix)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_DescriptionTooLong_ReturnsEscrow017()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            Description = new string('A', 1001), // Exceeds 1000 char limit
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_017", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
        Assert.Equal("description", result.Error.Field);
    }

    [Fact]
    public async Task DisputeAsync_Description1000Chars_Succeeds()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            Description = new string('A', 1000), // Exactly 1000 — allowed
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DisputeAsync_TooManyEvidenceRefs_ReturnsEscrow017()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            EvidenceRefs = new List<string> { "a", "b", "c", "d", "e", "f" }, // 6 > max 5
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_017", result.Error!.Code);
        Assert.Equal("evidenceRefs", result.Error.Field);
    }

    [Fact]
    public async Task DisputeAsync_EvidenceRefTooLong_ReturnsEscrow017()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            EvidenceRefs = new List<string> { new string('X', 201) }, // Exceeds 200 char limit
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_017", result.Error!.Code);
        Assert.Equal("evidenceRefs", result.Error.Field);
    }

    [Fact]
    public async Task DisputeAsync_EmptyEvidenceRef_ReturnsEscrow017()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            EvidenceRefs = new List<string> { "valid-ref.jpg", "" }, // Empty ref not allowed
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_017", result.Error!.Code);
        Assert.Equal("evidenceRefs", result.Error.Field);
    }

    [Fact]
    public async Task DisputeAsync_ValidEvidenceRefs_Succeeds()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            EvidenceRefs = new List<string> { "photo1.jpg", "photo2.jpg", "photo3.jpg" },
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    // ──────────────────────────────────────────────
    // Dispute from "released" state — invalid
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_FromReleasedState_ReturnsInvalidTransition()
    {
        var escrowItem = MakeEscrowItem(status: "released");
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

    // ──────────────────────────────────────────────
    // Release from "released" state — invalid
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_FromReleasedState_ReturnsInvalidTransition()
    {
        var escrowItem = MakeEscrowItem(status: "released");
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

    // ──────────────────────────────────────────────
    // Commission edge case: 200 ETB → 5 ETB min
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_CommissionEdge_200ETB_MinCommission()
    {
        // 200 * 0.025 = 5.0 — exactly at the minimum, should be 5 ETB
        var request = MakeInitiateRequest(amount: 200m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(5.0m, result.Value!.Commission);
        Assert.Equal(195.0m, result.Value.FarmerPayout);
    }

    // ──────────────────────────────────────────────
    // Amount boundary tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_Amount99ETB_ReturnsEscrow001()
    {
        var request = MakeInitiateRequest(amount: 99m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_Amount500001ETB_ReturnsEscrow001()
    {
        var request = MakeInitiateRequest(amount: 500_001m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_Amount100ETB_Succeeds()
    {
        var request = MakeInitiateRequest(amount: 100m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateAsync_Amount500000ETB_Succeeds()
    {
        // High-value txn with a fully verified buyer + farmer (default _service user repo).
        var request = MakeInitiateRequest(amount: 500_000m);

        var result = await _service.InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateAsync_HighValue_NoUserRepo_FailsClosed()
    {
        // Fayda is mandatory above 10,000 ETB. With no verification source available, the
        // service MUST fail closed rather than silently skip the regulatory check (B4/A6).
        var noRepoService = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var request = MakeInitiateRequest(amount: 500_000m);

        var result = await noRepoService.InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_007", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // Review fixes: duplicate-delivery, precision, rounding, delivery/farmer
    // validation, order-split reconciliation, triggers, concurrency
    // ──────────────────────────────────────────────

    private EscrowService CreateServiceWithDeliveryRepo(IDeliveryRepository deliveryRepo)
        => new(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, null, deliveryRepo);

    private EscrowService CreateServiceWithOrderRepo(IOrderRepository orderRepo)
        => new(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo);

    [Fact]
    public async Task InitiateAsync_ActiveEscrowExistsForDelivery_ReturnsEscrow009()
    {
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-existing" }, { "status", "escrowed" }
        });

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_009", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_AmountWithThreeDecimals_ReturnsEscrow001()
    {
        var result = await _service.InitiateAsync(MakeInitiateRequest(amount: 100.999m), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_OddAmount_RoundsCommissionToSantim()
    {
        // 10,001 * 0.025 = 250.025 → rounds away-from-zero to 250.03 (santim precision).
        var result = await _service.InitiateAsync(MakeInitiateRequest(amount: 10_001m), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(250.03m, result.Value!.Commission);
        Assert.Equal(10_001m - 250.03m, result.Value.FarmerPayout);
    }

    [Fact]
    public async Task InitiateAsync_DeliveryNotFound_ReturnsEscrow008()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns((Dictionary<string, object>?)null);

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_008", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_DeliveryWrongState_ReturnsEscrow010()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "status", "delivered" }
        });

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_010", result.Error!.Code);
        Assert.Equal(422, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_DeliveryPending_Succeeds()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "status", "pending" }
        });

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateAsync_RecipientNotFarmer_ReturnsEscrow006()
    {
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "buyer")); // wrong role

        var result = await CreateServiceWithUserRepo(userRepo)
            .InitiateAsync(MakeInitiateRequest(amount: 5_000m), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_006", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_OrderSplitDoesNotReconcile_ReturnsEscrow001()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "deliveryId", DeliveryId },
            { "cropCost", 14_000m }, { "transportCost", 3_000m },
            { "commissionAmount", 500m }, { "truckerId", "trucker-1" }
        });
        // Held amount 15,000 but split sums to 17,500 → must be rejected.
        var request = new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId, BuyerId = BuyerId, FarmerId = FarmerId, Amount = 15_000m,
            Currency = "ETB", OrderId = "order-1",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800000"
        };

        var result = await CreateServiceWithOrderRepo(orderRepo).InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_001", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_OrderSplitReconciles_Succeeds()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "deliveryId", DeliveryId },
            { "cropCost", 11_500m }, { "transportCost", 3_000m },
            { "commissionAmount", 500m }, { "truckerId", "trucker-1" }
        });
        var request = new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId, BuyerId = BuyerId, FarmerId = FarmerId, Amount = 15_000m,
            Currency = "ETB", OrderId = "order-1",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800000"
        };

        var result = await CreateServiceWithOrderRepo(orderRepo).InitiateAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(11_500m, result.Value!.FarmerPayout);
    }

    [Fact]
    public async Task ReleaseAsync_InvalidTrigger_ReturnsValidationError()
    {
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-user").Returns(MakeVerifiedUser("admin-user", "admin"));
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001", Trigger = "bogus", DeliveryId = DeliveryId,
            IdempotencyKey = "admin:escrow-001:release:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .ReleaseAsync(request, "admin-user", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_VALIDATION", result.Error!.Code);
    }

    [Fact]
    public async Task ReleaseAsync_ConcurrentTransition_ReturnsSettledWithoutError()
    {
        // Simulate a lost optimistic-concurrency race: the escrowed→released write throws
        // ConditionalCheckFailedException because another release already advanced the state.
        // Telebirr dedupes on the stable transactionRef, so this must resolve to success, not 500.
        _mockEscrowRepo.GetByIdAsync("escrow-001")
            .Returns(MakeEscrowItem(status: "escrowed"), MakeEscrowItem(status: "completed"));
        _mockEscrowRepo.When(x => x.UpdateStatusAsync(
                "escrow-001", Arg.Any<string>(), "released", Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>?>()))
            .Do(_ => throw new ConditionalCheckFailedException("lost race"));

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-user").Returns(MakeVerifiedUser("admin-user", "admin"));
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001", Trigger = "admin_override", DeliveryId = DeliveryId,
            IdempotencyKey = "admin:escrow-001:release:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .ReleaseAsync(request, "admin-user", role: "admin");

        Assert.True(result.IsSuccess);
    }

    // ──────────────────────────────────────────────
    // Refund not found
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RefundAsync_NotFound_ReturnsEscrow016()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-999").Returns((Dictionary<string, object>?)null);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-999",
            Trigger = "admin_override",
            IdempotencyKey = $"system:escrow-999:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_016", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // Refund duplicate idempotency
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RefundAsync_DuplicateIdempotency_ReturnsCached()
    {
        var escrowItem = MakeEscrowItem(status: "refunded");
        escrowItem["refundedAt"] = "2026-02-18T10:00:00Z";
        escrowItem["telebirrRefundRef"] = "MOCK-REFUND-REF";

        _mockIdempotencyRepo.GetAsync(Arg.Any<string>())
            .Returns(new Dictionary<string, object>
            {
                { "escrowId", "escrow-001" },
                { "status", "completed" }
            });
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"system:escrow-001:refund:1708257600000"
        };

        var result = await _service.RefundAsync(request, "system");

        Assert.True(result.IsSuccess);
        Assert.True(result.IsDuplicate);
    }

    // ──────────────────────────────────────────────
    // Dispute duplicate idempotency
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_DuplicateIdempotency_ReturnsCached()
    {
        var escrowItem = MakeEscrowItem(status: "disputed");
        escrowItem["disputeId"] = "dispute-001";
        escrowItem["disputedBy"] = BuyerId;
        escrowItem["disputeReason"] = "damaged_goods";
        escrowItem["disputeCreatedAt"] = "2026-02-18T14:00:00Z";
        escrowItem["disputeWindowEndsAt"] = "2026-02-20T14:00:00Z";

        _mockIdempotencyRepo.GetAsync(Arg.Any<string>())
            .Returns(new Dictionary<string, object>
            {
                { "escrowId", "escrow-001" },
                { "status", "completed" }
            });
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsDuplicate);
    }

    // ──────────────────────────────────────────────
    // Dispute not found
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_NotFound_ReturnsEscrow016()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-999").Returns((Dictionary<string, object>?)null);

        var request = new DisputeEscrowRequest
        {
            EscrowId = "escrow-999",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-999:dispute:1708171200000"
        };

        var result = await _service.DisputeAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_016", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    // ──────────────────────────────────────────────
    // Audit trail for Telebirr failures (S1)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_TelebirrFailure_CreatesAuditEntry()
    {
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_HOLD_FAILED",
                ErrorMessage = "Insufficient balance."
            });

        var request = MakeInitiateRequest();
        await _service.InitiateAsync(request, BuyerId);

        // initiate audit + hold_failed audit = 2 entries
        await _mockAuditRepo.Received(2).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ReleaseAsync_TelebirrFailure_CreatesAuditEntry()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_RELEASE_FAILED"
            });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:escrow-001:release:1708171200000"
        };

        await _service.ReleaseAsync(request, "system");

        // release_failed audit entry
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RefundAsync_TelebirrFailure_CreatesAuditEntry()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_REFUND_FAILED"
            });

        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = $"system:escrow-001:refund:1708257600000"
        };

        await _service.RefundAsync(request, "system");

        // refund_failed audit entry
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }
}
