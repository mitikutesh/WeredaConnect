using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

        // Default: settlement lock is acquired (the release/refund happy path).
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
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
    public async Task ReleaseAsync_TransientReleasedWriteFailure_RetriesAndCompletes()
    {
        // By the time the 'released' CAS write runs, Telebirr has ALREADY disbursed
        // the funds — a transient DynamoDB failure must be retried, not surfaced,
        // or the escrow is stranded in 'escrowed' with the settlement lock held
        // (a later retry would 409 on the lock with no recovery path).
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var releasedWrites = 0;
        _mockEscrowRepo.UpdateStatusAsync("escrow-001", Arg.Any<string>(), "released",
                Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(_ =>
            {
                releasedWrites++;
                if (releasedWrites == 1)
                {
                    throw new InvalidOperationException("transient DynamoDB failure");
                }
                return Task.CompletedTask;
            });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        };

        var result = await _service.ReleaseAsync(request, "system");

        Assert.True(result.IsSuccess);
        Assert.Equal("completed", result.Value!.Status);
        Assert.Equal(2, releasedWrites); // first attempt failed, retry succeeded
        // The retry is a plain DB write — funds must NOT be disbursed again.
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task ReleaseAsync_ReleasedWriteCasFailure_SurfacesWithoutRetry()
    {
        // A CAS failure on the 'released' write means the escrow state genuinely
        // moved under us — retrying can never succeed and would only mask the
        // anomaly, so it must surface immediately (one attempt, no retry loop).
        var escrowItem = MakeEscrowItem(status: "escrowed");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var releasedWrites = 0;
        _mockEscrowRepo.UpdateStatusAsync("escrow-001", Arg.Any<string>(), "released",
                Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>())
            .Returns<Task>(_ =>
            {
                releasedWrites++;
                throw new ConditionalCheckFailedException("escrow state moved");
            });

        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        };

        await Assert.ThrowsAsync<ConditionalCheckFailedException>(
            () => _service.ReleaseAsync(request, "system"));
        Assert.Equal(1, releasedWrites);
    }

    private static Dictionary<string, object> MakeResolvedDispute(string resolution)
    {
        var item = MakeEscrowItem(status: "dispute_resolved");
        item["disputeId"] = "dispute-1";
        item["disputedBy"] = BuyerId;
        item["disputeResolution"] = resolution;
        item["disputeResolvedAt"] = "2026-02-19T10:00:00.0000000Z";
        return item;
    }

    private EscrowService CreateResolverService()
    {
        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("resolver").Returns(MakeAdminUser("resolver"));
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
        return CreateServiceWithUserRepo(mockUserRepo);
    }

    private static ReleaseEscrowRequest DisputeReleaseRequest() => new()
    {
        EscrowId = "escrow-001",
        Trigger = "dispute_resolved",
        DeliveryId = DeliveryId,
        IdempotencyKey = $"system:escrow-001:release:1708171200000"
    };

    private static RefundEscrowRequest DisputeRefundRequest() => new()
    {
        EscrowId = "escrow-001",
        Trigger = "dispute_resolved",
        Reason = "Buyer wins.",
        IdempotencyKey = $"admin:escrow-001:refund:1708257600000"
    };

    [Fact]
    public async Task ReleaseAsync_FromResolvedDispute_FarmerWins_ReturnsCompleted()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeResolvedDispute("farmer_wins"));

        var result = await CreateResolverService().ReleaseAsync(DisputeReleaseRequest(), "resolver", role: "admin");

        Assert.True(result.IsSuccess);
        Assert.Equal("completed", result.Value!.Status);
        // ESC-03: the lock is a CAS on the status the service validated.
        await _mockEscrowRepo.Received(1).TryAcquireSettlementLockAsync(
            "escrow-001", Arg.Any<string>(), "release", "dispute_resolved");
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-001", Arg.Any<string>(), "released", "dispute_resolved", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task ReleaseAsync_FromDisputed_IsRefusedUntilAVerdictIsRecorded()
    {
        // ESC-03 (D4): a dispute settles only through a recorded verdict.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "disputed"));

        var result = await CreateResolverService().ReleaseAsync(DisputeReleaseRequest(), "resolver", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal(422, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error.Code);
        Assert.Contains("Record the verdict first", result.Error.Message);
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task ReleaseAsync_BuyerWinsVerdict_IsRefused_NoLockNoGateway()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeResolvedDispute("buyer_wins"));

        var result = await CreateResolverService().ReleaseAsync(DisputeReleaseRequest(), "resolver", role: "admin");

        Assert.Equal(422, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error.Code);
        Assert.Contains("can only be refunded", result.Error.Message);
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockIdempotencyRepo.DidNotReceive().StoreAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task RefundAsync_FarmerWinsVerdict_IsRefused_NoLockNoGateway()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeResolvedDispute("farmer_wins"));

        var result = await CreateResolverService().RefundAsync(DisputeRefundRequest(), "resolver", role: "admin");

        Assert.Equal(422, result.Error!.HttpStatus);
        Assert.Contains("can only be released", result.Error.Message);
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Theory]
    [InlineData("release")]
    [InlineData("refund")]
    public async Task LegacyVerdictWithoutDirection_MayBeSettledEitherWay(string op)
    {
        // A partial_refund recorded before ESC-03 has no automatic leg; the admin settles it.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeResolvedDispute("partial_refund"));
        var service = CreateResolverService();

        var ok = op == "release"
            ? (await service.ReleaseAsync(DisputeReleaseRequest(), "resolver", role: "admin")).IsSuccess
            : (await service.RefundAsync(DisputeRefundRequest(), "resolver", role: "admin")).IsSuccess;

        Assert.True(ok);
        await _mockEscrowRepo.Received(1).TryAcquireSettlementLockAsync(
            "escrow-001", Arg.Any<string>(), op, "dispute_resolved");
    }

    [Fact]
    public async Task ReleaseAsync_ResolvedDispute_MirrorsTheOrderBeforeTheLock()
    {
        // ESC-03 (D5): the settlement repairs the order leg the verdict could not write.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "disputed" }, { "version", 4 }
        });
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-1" }, { "status", "dispute_resolved" }, { "version", 5 }
            }));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var txAudit = Substitute.For<ITransactionAuditRepository>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            userRepo, orderRepo, transactionAuditRepo: txAudit);
        var escrow = MakeResolvedDispute("farmer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var result = await service.ReleaseAsync(DisputeReleaseRequest(), "system");

        Assert.True(result.IsSuccess);
        Received.InOrder(() =>
        {
            orderRepo.UpdateAsync("order-1",
                Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "dispute_resolved"), 4);
            _mockEscrowRepo.TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "release", "dispute_resolved");
        });
        await txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_resolved" && e["entityType"].ToString() == "order"
            && e["actor"].ToString() == "system" && e["metadata"].ToString() == "resolution=farmer_wins"));
    }

    [Theory]
    [InlineData("farmer_wins", "release")]
    [InlineData("buyer_wins", "refund")]
    public async Task ResolvedDispute_OrderThatCannotFollow_IsNotSettled_AlarmedAndAuditedOnce(
        string resolution, string op)
    {
        // A dispute raised before delivery: the order is still in transit. Settling would pay
        // the farmer before delivery, or refund the buyer and leave the order live.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "in_transit" }, { "version", 4 }
        });
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var alarms = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            userRepo, orderRepo) { AlarmLog = alarms.Add };
        var escrow = MakeResolvedDispute(resolution);
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        // Two hourly sweeps on the same day.
        for (var run = 0; run < 2; run++)
        {
            var error = op == "release"
                ? (await service.ReleaseAsync(DisputeReleaseRequest(), "system")).Error
                : (await service.RefundAsync(DisputeRefundRequest(), "system")).Error;
            Assert.Equal(422, error!.HttpStatus);
            Assert.Equal("ESCROW_INVALID_TRANSITION", error.Code);
            Assert.Contains("order:mismatch:in_transit", error.Message);
        }

        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await orderRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        Assert.Equal(2, alarms.Count);
        Assert.All(alarms, line => Assert.Equal(
            $"DISPUTE_VERDICT_ORDER_MISMATCH escrowId=escrow-001 op={op} legs=order:mismatch:in-transit", line));
        // Both runs aim at the same deterministic key (the verdict time plus one tick), so the
        // conditional put keeps one row, sorted right after the verdict.
        var dayKey = "2026-02-19T10:00:00.0000001Z";
        await _mockAuditRepo.Received(2).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_verdict_mismatch"
            && e["timestamp"].ToString() == dayKey
            && e["auditId"].ToString() == EscrowAuditRepository.DeterministicAuditId("dispute_verdict_mismatch", dayKey)
            && e["metadata"].ToString()!.StartsWith($"legs=order:mismatch:in_transit;op={op};at=")));
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString()!.StartsWith("dispute_verdict")));
    }

    [Theory]
    [InlineData("farmer_wins", "release")]
    [InlineData("buyer_wins", "refund")]
    public async Task ResolvedDispute_OrderReadFails_FailsClosed_NoLockNoGateway(string resolution, string op)
    {
        // A throttled order read must not let the settlement through as "nothing to block".
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));
        var alarms = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo) { AlarmLog = alarms.Add };
        var escrow = MakeResolvedDispute(resolution);
        escrow["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var error = op == "release"
            ? (await service.ReleaseAsync(DisputeReleaseRequest(), "system")).Error
            : (await service.RefundAsync(DisputeRefundRequest(), "system")).Error;

        Assert.Equal(503, error!.HttpStatus);
        Assert.Equal("ESCROW_012", error.Code);
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        // Not the mismatch alarm: the blocked line, which alarms only when it persists.
        // (legs is defanged and cut at 64 characters like every MoneyAlarmLog field)
        Assert.StartsWith(
            $"DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=escrow-001 op={op} legs=order:read:ProvisionedThroughputExceededException.delivery:read:",
            Assert.Single(alarms));
        // One audit row for the admin timeline, at the verdict time plus three ticks.
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_settlement_blocked"
            && e["timestamp"].ToString() == "2026-02-19T10:00:00.0000003Z"
            && e["metadata"].ToString()!.StartsWith(
                $"legs=order:read:[ProvisionedThroughputExceededException],delivery:read:[ProvisionedThroughputExceededException];op={op};at=")));
    }

    private EscrowService MismatchedRefundService(IOrderRepository orderRepo, ITransactionAuditRepository txAudit)
    {
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "in_transit" }, { "version", 4 }
        });
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>()));
        var escrow = MakeResolvedDispute("buyer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);
        return new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo, transactionAuditRepo: txAudit);
    }

    private static RefundEscrowRequest OverrideRequest(string? reason) => new()
    {
        EscrowId = "escrow-001",
        Trigger = "dispute_resolved",
        Reason = reason,
        OverrideOrderMismatch = true,
        IdempotencyKey = $"admin:escrow-001:refund:1708257600001"
    };

    [Fact]
    public async Task RefundAsync_AdminOverride_RefundsAMismatchedBuyerWins_AuditedAndFlagged()
    {
        // The goods are in transit; the admin decides to refund the buyer anyway.
        var orderRepo = Substitute.For<IOrderRepository>();
        var txAudit = Substitute.For<ITransactionAuditRepository>();
        var service = MismatchedRefundService(orderRepo, txAudit);

        var result = await service.RefundAsync(OverrideRequest("Truck broke down, goods lost on the road."),
            "admin-7", role: "admin", adminGroupVerified: true);

        Assert.True(result.IsSuccess);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_order_mismatch_overridden" && e["actor"].ToString() == "admin-7"
            && e["metadata"].ToString() == "legs=order:mismatch:in_transit;reason=Truck broke down, goods lost on the road."));
        await txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_mismatch_override" && e["actor"].ToString() == "admin-7"
            && e["transactionId"].ToString() == "order-1"));
        // The order keeps its status and is flagged.
        await orderRepo.Received(1).UpdateAsync("order-1", Arg.Is<Dictionary<string, object>>(d =>
            !d.ContainsKey("status") && d["disputeOverrideBy"].ToString() == "admin-7"
            && d["disputeOverrideEscrowId"].ToString() == "escrow-001"), 4);
        await _mockAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_verdict_mismatch"));
    }

    [Theory]
    [InlineData(false, "Truck broke down, goods lost on the road.", 403, "ESCROW_021")]
    [InlineData(true, "too short", 400, "ESCROW_VALIDATION")]
    [InlineData(true, null, 400, "ESCROW_VALIDATION")]
    public async Task RefundAsync_Override_NeedsTheAdminGroupAndAReason(
        bool adminGroup, string? reason, int status, string code)
    {
        var service = MismatchedRefundService(Substitute.For<IOrderRepository>(), Substitute.For<ITransactionAuditRepository>());

        var result = await service.RefundAsync(OverrideRequest(reason), "admin-7", role: "admin", adminGroupVerified: adminGroup);

        Assert.Equal(status, result.Error!.HttpStatus);
        Assert.Equal(code, result.Error.Code);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task RefundAsync_Override_RoleAdminOutsideTheGroup_Returns403()
    {
        // The users-table admin role passes the refund's own admin check; the override still
        // needs the admin group.
        _mockUserRepo.GetUserByIdAsync("admin-7").Returns(MakeAdminUser("admin-7"));
        var service = MismatchedRefundService(Substitute.For<IOrderRepository>(), Substitute.For<ITransactionAuditRepository>());

        var result = await service.RefundAsync(OverrideRequest("Truck broke down, goods lost on the road."),
            "admin-7", role: "admin", adminGroupVerified: false);

        Assert.Equal(403, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_021", result.Error.Code);
        Assert.Contains("override", result.Error.Message);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_order_mismatch_overridden"));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("409")]
    [InlineData("throw")]
    [InlineData("restock")]
    public async Task RefundAsync_BuyerWinsBeforePickup_CancelOrRestockNotLanding_FailsClosed(string failure)
    {
        // Cancel-then-refund: the refund never runs ahead of an order cancel (or its stock
        // restore) that did not land; the next sweep retries.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "funded" }, { "version", 2 },
            { "listingId", "listing-1" }, { "quantity", "200" }
        });
        var cancel = orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        switch (failure)
        {
            case "throw":
                cancel.ThrowsAsync(new TimeoutException("boom"));
                break;
            case "restock":
                cancel.Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
                {
                    { "orderId", "order-1" }, { "status", "cancelled" }, { "version", 3 }
                }));
                break;
            default:
                cancel.Returns(AuthResult<Dictionary<string, object>>.Failure(int.Parse(failure), "SYNC_X", "nope"));
                break;
        }

        var crops = Substitute.For<ICropRepository>();
        crops.RestoreStockAsync("listing-1", "order-1", 2.0)
            .Returns(failure == "restock" ? StockRestoreResult.Failed : StockRestoreResult.Restored);
        var alarms = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo, cropRepo: crops) { AlarmLog = alarms.Add };
        var escrow = MakeResolvedDispute("buyer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var result = await service.RefundAsync(DisputeRefundRequest(), "system");

        Assert.Equal(503, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_012", result.Error.Code);
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        var line = Assert.Single(alarms);
        Assert.StartsWith("DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=escrow-001 op=refund legs=order:", line);
        Assert.Contains(failure == "restock" ? "restock:conflict" : "cancel:", line);
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_settlement_blocked"
            && e["timestamp"].ToString() == "2026-02-19T10:00:00.0000003Z"));
    }

    [Fact]
    public async Task RefundAsync_BuyerWinsBeforePickup_CancelsTheOrderThenRefunds()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "funded" }, { "version", 2 },
            { "listingId", "listing-1" }, { "quantity", "200" }
        });
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-1" }, { "status", "cancelled" }, { "version", 3 }
            }));
        var crops = Substitute.For<ICropRepository>();
        crops.RestoreStockAsync("listing-1", "order-1", 2.0).Returns(StockRestoreResult.Restored);
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo, cropRepo: crops);
        var escrow = MakeResolvedDispute("buyer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var result = await service.RefundAsync(DisputeRefundRequest(), "system");

        Assert.True(result.IsSuccess);
        Received.InOrder(() =>
        {
            orderRepo.UpdateAsync("order-1", Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "cancelled"), 2);
            crops.RestoreStockAsync("listing-1", "order-1", 2.0);
            _mockEscrowRepo.TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "refund", "dispute_resolved");
            _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        });
    }

    [Fact]
    public async Task RefundAsync_SweepWithoutOverride_StillRefusesTheMismatch()
    {
        var service = MismatchedRefundService(Substitute.For<IOrderRepository>(), Substitute.For<ITransactionAuditRepository>());

        var result = await service.RefundAsync(DisputeRefundRequest(), "system");

        Assert.Equal(422, result.Error!.HttpStatus);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task ReleaseAsync_ResolvedDispute_OrderWriteFailure_StillSettles_AndIsAudited()
    {
        // A transient write failure is not a mismatch: the money follows the verdict and a
        // later run (or an admin replay of the verdict) repairs the order.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "disputed" }, { "version", 4 }
        });
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "conflict"));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var alarms = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            userRepo, orderRepo) { AlarmLog = alarms.Add };
        var escrow = MakeResolvedDispute("farmer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var result = await service.ReleaseAsync(DisputeReleaseRequest(), "system");

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(alarms, line => line.StartsWith("DISPUTE_VERDICT_ORDER_MISMATCH"));
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_verdict_propagation_failed"
            && e["metadata"].ToString()!.StartsWith("legs=order:conflict;op=release;at=")));
    }

    [Theory]
    [InlineData("escrow-existing", null, "ESCROW_009")]
    [InlineData("escrow-order-order-001", "order", "ESCROW_024")]
    public async Task InitiateAsync_ResolvedButUnsettledEscrowOnTheDelivery_StillBlocks(
        string escrowId, string? source, string code)
    {
        // ESC-03: a dispute_resolved escrow still holds the money until its verdict settles.
        var existing = new Dictionary<string, object>
        {
            { "escrowId", escrowId }, { "status", "dispute_resolved" }, { "orderId", "order-001" }
        };
        if (source != null)
        {
            existing["source"] = source;
        }

        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(existing);

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public async Task InitiateAsync_OrderWithAResolvedButUnsettledEscrow_ReturnsEscrow024()
    {
        // The order-level double-charge guard (6f) counts dispute_resolved as active too.
        _mockEscrowRepo.QueryByOrderIdAsync("order-001", Arg.Any<int>()).Returns(new List<Dictionary<string, object>>
        {
            new() { { "escrowId", "escrow-order-order-001" }, { "orderId", "order-001" }, { "status", "dispute_resolved" } }
        });
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "status", "pending" }, { "orderId", "order-001" }
        });
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, deliveryRepo: deliveryRepo);

        var result = await service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.Equal("ESCROW_024", result.Error!.Code);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task RefundAsync_ResolvedDispute_MirrorsTheOrderBeforeTheLock()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", "delivered" }, { "version", 2 }
        });
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-1" },
                { "status", ((Dictionary<string, object>)call[1])["status"] },
                { "version", (int)call[2] + 1 }
            }));
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo);
        var escrow = MakeResolvedDispute("buyer_wins");
        escrow["orderId"] = "order-1";
        escrow.Remove("deliveryId");
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrow);

        var result = await service.RefundAsync(DisputeRefundRequest(), "system");

        Assert.True(result.IsSuccess);
        Received.InOrder(() =>
        {
            orderRepo.UpdateAsync("order-1",
                Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
            orderRepo.UpdateAsync("order-1",
                Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "dispute_resolved"), 3);
            _mockEscrowRepo.TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "refund", "dispute_resolved");
        });
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
                if (recordPresent)
                {
                    return false; // conditional write would fail
                }

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

    // --- SIM-12: dispute propagation ---

    private static DisputeEscrowRequest MakeDisputeRequest(string disputedBy = BuyerId) => new()
    {
        EscrowId = "escrow-001",
        DisputedBy = disputedBy,
        Reason = "damaged_goods",
        Description = "Teff bags were wet.",
        IdempotencyKey = $"{disputedBy}:escrow-001:dispute:1708171200000"
    };

    private static Dictionary<string, object> MakeOrder(string orderId, string status, int version) => new()
    {
        { "orderId", orderId }, { "status", status }, { "version", version.ToString(CultureInfo.InvariantCulture) }
    };

    private static AuthResult<Dictionary<string, object>> OrderWriteOk(string orderId) =>
        AuthResult<Dictionary<string, object>>.Success(MakeOrder(orderId, "disputed", 0));

    private static AuthResult<Dictionary<string, object>> OrderWriteConflict() =>
        AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict.");

    private Task AssertPropagationFailureAudited(string expectedLeg) =>
        _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_propagation_failed"
            && e.ContainsKey("metadata")
            && e["metadata"].ToString()!.Contains(expectedLeg)));

    private Task AssertNoPropagationFailureAudited() =>
        _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_propagation_failed"));

    [Fact]
    public async Task DisputeAsync_PropagatesToOrderAndDelivery()
    {
        // The fleet run: escrow disputed, but order and delivery stayed delivered and the
        // buyer could still rate the farmer.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods").Returns(true);
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteOk("order-1"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await deliveryRepo.Received(1).TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods");
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_OrderNotDelivered_IsLeftAlone()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "in_transit", 2));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_ResolvesOrderFromDeliveryWhenEscrowHasNoOrderId()
    {
        // Escrows created before the order linkage carry no orderId; the delivery does.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", "order-9" }, { "status", "delivered" }
        });
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-9", true).Returns(MakeOrder("order-9", "delivered", 3));
        orderRepo.UpdateAsync("order-9", Arg.Any<Dictionary<string, object>>(), 3).Returns(OrderWriteOk("order-9"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(1).UpdateAsync("order-9",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 3);
    }

    [Fact]
    public async Task DisputeAsync_PropagationFailure_DoesNotFailTheDispute()
    {
        // SIM-47: the delivery leg failing must not skip the order leg, and the failure
        // must name the leg instead of vanishing.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("deliveries table unavailable"));
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteOk("order-1"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("disputed", result.Value!.Status);
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
        await AssertPropagationFailureAudited("delivery:[InvalidOperationException]");
    }

    [Fact]
    public async Task DisputeAsync_OrderWriteDenied_IsAuditedWithLeg()
    {
        // SIM-47 production failure: the escrow Lambda had read-only grants on orders, so
        // the UpdateItem was denied and the error was swallowed without a trace.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods").Returns(true);
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2)
            .ThrowsAsync(new AmazonDynamoDBException("not authorized to perform: dynamodb:UpdateItem")
            {
                ErrorCode = "AccessDeniedException"
            });

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("disputed", result.Value!.Status);
        await deliveryRepo.Received(1).TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods");
        await AssertPropagationFailureAudited("order:[AmazonDynamoDBException]");
    }

    [Fact]
    public async Task DisputeAsync_OrderVersionConflict_RetriesWithFreshVersion()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(
            MakeOrder("order-1", "delivered", 2),
            MakeOrder("order-1", "delivered", 3));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteConflict());
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 3).Returns(OrderWriteOk("order-1"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 3);
        await orderRepo.Received(2).UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_OrderConflictPersists_IsAuditedAfterThreeAttempts()
    {
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(OrderWriteConflict());

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(3).UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertPropagationFailureAudited("order:conflict");
    }

    [Fact]
    public async Task DisputeAsync_OrderWriteRejected_IsAuditedWithCode()
    {
        // A non-conflict failure (order vanished between the reads) is not retried.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(404, "SYNC_VALIDATION", "Order not found."));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(1).UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertPropagationFailureAudited("order:SYNC_VALIDATION");
    }

    [Fact]
    public async Task DisputeAsync_OrderMovedOnDuringRetry_StopsWithoutFailure()
    {
        // A concurrent writer already disputed the order: the re-read ends the leg cleanly.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(
            MakeOrder("order-1", "delivered", 2),
            MakeOrder("order-1", "disputed", 3));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteConflict());

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(1).UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_PropagationAuditWriteFails_StillSucceeds()
    {
        // Losing the failure row must not turn a recorded dispute into a 500.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .ThrowsAsync(new AmazonDynamoDBException("denied"));
        _mockAuditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
                e["action"].ToString() == "dispute_propagation_failed"))
            .ThrowsAsync(new AmazonDynamoDBException("audit table unavailable"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal("disputed", result.Value!.Status);
        await _mockIdempotencyRepo.Received(1).UpdateAsync(MakeDisputeRequest().IdempotencyKey, "completed");
    }

    // ──────────────────────────────────────────────
    // ESC-07: the freeze a delivery dispute needs first
    // ──────────────────────────────────────────────

    private static Dictionary<string, object> MakeOrderEscrow(string status, string? deliveryId = DeliveryId)
    {
        var escrow = MakeEscrowItem(escrowId: "escrow-order-order-1", status: status);
        escrow["orderId"] = "order-1";
        if (deliveryId == null)
        {
            escrow.Remove("deliveryId");
        }
        else
        {
            escrow["deliveryId"] = deliveryId;
        }

        return escrow;
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_FreezesTheOrderEscrow_FromAConsistentRead()
    {
        // A delivery disputed through sync must freeze the escrow, or the 48-hour
        // auto-release would still pay the farmer.
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("escrowed"));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, "wet bags");

        Assert.Equal(DeliveryDisputeFreeze.Frozen, frozen.Outcome);
        Assert.True(frozen.MayMirror);
        Assert.Equal("escrow-order-order-1", frozen.EscrowId);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-order-order-1", "2026-02-17T10:00:00.0000000Z", "disputed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                f["disputeReason"].ToString() == "other"
                && f["disputeDescription"].ToString() == "wet bags"
                && f["disputedBy"].ToString() == BuyerId));
        await _mockEscrowRepo.DidNotReceive().GetByDeliveryIdAsync(Arg.Any<string>());
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute" && e["toState"].ToString() == "disputed"));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("refunded")]
    [InlineData("dispute_resolved")]
    [InlineData("initiated")]
    [InlineData("cancelled")]
    [InlineData("held")]
    [InlineData("something_new")]
    public async Task FreezeForDeliveryDispute_EscrowHoldingNoMoney_IsRefused(string status)
    {
        // ESC-07: every status but escrowed and disputed fails closed.
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow(status));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedNotHeld, frozen.Outcome);
        Assert.Equal(status, frozen.EscrowStatus);
        Assert.False(frozen.MayMirror);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_refused_not_held"
            && e["metadata"].ToString() == $"deliveryId={DeliveryId}"));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_UnlinkedRefundedOrderEscrow_IsRefusedNotMissing()
    {
        // The order escrow is read with no status filter: a refunded one never on the
        // DeliveryIndex is "holds no money", not "no escrow".
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("refunded", deliveryId: null));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedNotHeld, frozen.Outcome);
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_AlreadyDisputed_WritesNothing_AndMayMirror()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("disputed"));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.AlreadyDisputed, frozen.Outcome);
        Assert.True(frozen.MayMirror);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_AlreadyDisputed_EnsuresItsDisputeRow()
    {
        var escrow = MakeOrderEscrow("disputed");
        escrow["disputeCreatedAt"] = "2026-10-09T08:00:00.0000000Z";
        escrow["disputedBy"] = FarmerId;
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(escrow);

        await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["auditId"] == EscrowAuditRepository.DeterministicAuditId("dispute", "2026-10-09T08:00:00.0000000Z")
            && (string)e["timestamp"] == "2026-10-09T08:00:00.0000000Z"
            && (string)e["action"] == "dispute" && (string)e["actor"] == FarmerId));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_AuditFailsAfterTheFreeze_TheRetryWritesTheSameRow()
    {
        // The freeze landed, then its audit write threw: the sync answers a retry. The retry finds
        // the escrow disputed and writes the row at the same deterministic key, never a second one.
        Dictionary<string, object>? written = null;
        _mockEscrowRepo.UpdateStatusAsync("escrow-order-order-1", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Do<Dictionary<string, object>?>(f => written = f))
            .Returns(Task.CompletedTask);
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(_ =>
        {
            if (written == null)
            {
                return MakeOrderEscrow("escrowed");
            }

            var disputed = MakeOrderEscrow("disputed");
            disputed["disputeCreatedAt"] = written["disputeCreatedAt"];
            disputed["disputedBy"] = written["disputedBy"];
            return disputed;
        });
        var keys = new List<string>();
        var attempt = 0;
        _mockAuditRepo.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(e => keys.Add($"{e["auditId"]}@{e["timestamp"]}")))
            .Returns(_ => ++attempt == 1 ? throw new AmazonDynamoDBException("audit table down") : Task.FromResult(true));

        await Assert.ThrowsAsync<AmazonDynamoDBException>(() =>
            _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, "wet bags"));
        var retry = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, "wet bags");

        Assert.Equal(DeliveryDisputeFreeze.AlreadyDisputed, retry.Outcome);
        var createdAt = written!["disputeCreatedAt"].ToString()!;
        var key = $"{EscrowAuditRepository.DeterministicAuditId("dispute", createdAt)}@{createdAt}";
        Assert.Equal(new[] { key, key }, keys);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "disputed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_WritesNoVersion_TheRepositoryAddsIt()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("escrowed"));

        await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        await _mockEscrowRepo.Received(1).UpdateStatusAsync("escrow-order-order-1", Arg.Any<string>(), "disputed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => !f.ContainsKey("version")));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_RefusalAlreadyAuditedToday_IsNotAuditedAgain()
    {
        // Clients before sync-protocol 1.12.0 resend a refused dispute on every reconnect.
        var queued = MakeOrderEscrow("escrowed");
        queued["manualPayout"] = "open";
        queued["manualPayoutOp"] = "release";
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(queued);
        var auditId = $"audit-dispute_refused_manual_payout-{DeliveryId.Replace("-", "")}-{DateTime.UtcNow:yyyyMMdd}";
        _mockAuditRepo.GetAuditTrailAsync("escrow-order-order-1").Returns(new List<Dictionary<string, object>>
        {
            new() { { "auditId", auditId }, { "action", "dispute_refused_manual_payout" } }
        });

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedManualPayout, frozen.Outcome);
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_FirstRefusalOfTheDay_IsAuditedWithItsDailyKey()
    {
        var queued = MakeOrderEscrow("escrowed");
        queued["manualPayout"] = "open";
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(queued);
        _mockAuditRepo.GetAuditTrailAsync("escrow-order-order-1").Returns(new List<Dictionary<string, object>>
        {
            // Yesterday's refusal of the same dispute does not count.
            new() { { "auditId", $"audit-dispute_refused_manual_payout-{DeliveryId.Replace("-", "")}-{DateTime.UtcNow.AddDays(-1):yyyyMMdd}" } }
        });

        await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["auditId"] == $"audit-dispute_refused_manual_payout-{DeliveryId.Replace("-", "")}-{DateTime.UtcNow:yyyyMMdd}"
            && (string)e["action"] == "dispute_refused_manual_payout"));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_TrailUnreadable_StillAuditsTheRefusal()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("released"));
        _mockAuditRepo.GetAuditTrailAsync("escrow-order-order-1").ThrowsAsync(new AmazonDynamoDBException("denied"));

        await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "dispute_refused_not_held"));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_DisputedButLocked_IsRefused()
    {
        var escrow = MakeOrderEscrow("disputed");
        escrow["settlementLock"] = "release";
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(escrow);

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedSettlementInFlight, frozen.Outcome);
        Assert.False(frozen.MayMirror);
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_OrderEscrowOfAnotherDelivery_IsRefused()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("escrowed", deliveryId: "delivery-other"));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedOtherDelivery, frozen.Outcome);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _mockAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_refused_other_delivery"));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_NoEscrowAnywhere_ReturnsNoEscrow()
    {
        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.NoEscrow, frozen.Outcome);
        Assert.False(frozen.MayMirror);
        await _mockEscrowRepo.Received(1).GetByDeliveryIdAsync(DeliveryId);
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_IndexedEscrow_IsDecidedOnTheBaseRow()
    {
        // An orderless delivery (or an order paid through POST /escrow/initiate) finds its
        // escrow on the DeliveryIndex; the copy may lag, so the base row decides.
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(MakeEscrowItem(status: "escrowed"));
        _mockEscrowRepo.GetByIdAsync("escrow-001", true).Returns(MakeEscrowItem(status: "released"));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, orderId: null, BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.RefusedNotHeld, frozen.Outcome);
        Assert.Equal("released", frozen.EscrowStatus);
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_LostToAnotherDispute_IsAlreadyDisputed()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true)
            .Returns(MakeOrderEscrow("escrowed"), MakeOrderEscrow("disputed"));
        _mockEscrowRepo.UpdateStatusAsync("escrow-order-order-1", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var frozen = await _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null);

        Assert.Equal(DeliveryDisputeFreeze.AlreadyDisputed, frozen.Outcome);
        await _mockAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute"));
    }

    [Fact]
    public async Task FreezeForDeliveryDispute_ConditionFailsWithNothingInTheWay_Throws()
    {
        // Unknown outcome: the caller answers with a retry and writes nothing.
        _mockEscrowRepo.GetByIdAsync("escrow-order-order-1", true).Returns(MakeOrderEscrow("escrowed"));
        _mockEscrowRepo.UpdateStatusAsync("escrow-order-order-1", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("?"));

        await Assert.ThrowsAsync<ConditionalCheckFailedException>(() =>
            _service.FreezeForDeliveryDisputeAsync(DeliveryId, "order-1", BuyerId, null));
    }

    // ──────────────────────────────────────────────
    // ESC-07: DisputeAsync answers every lost race, never a 500
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("disputed", 409, "ESCROW_020")]
    [InlineData("released", 422, "ESCROW_INVALID_TRANSITION")]
    [InlineData("refunded", 422, "ESCROW_INVALID_TRANSITION")]
    [InlineData("escrowed", 409, "ESCROW_INVALID_TRANSITION")]
    public async Task DisputeAsync_LostRace_MapsTheLatestStatus_AndFreesTheKey(string latestStatus, int http, string code)
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockEscrowRepo.GetByIdAsync("escrow-001", true).Returns(MakeEscrowItem(status: latestStatus));
        _mockEscrowRepo.UpdateStatusAsync("escrow-001", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var result = await _service.DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal(http, result.Error!.HttpStatus);
        Assert.Equal(code, result.Error.Code);
        await _mockIdempotencyRepo.Received(1).DeleteAsync(MakeDisputeRequest().IdempotencyKey);
        await _mockIdempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), "completed");
    }

    [Fact]
    public async Task DisputeAsync_AuditsAtTheDeterministicKeyOfItsDisputeCreatedAt()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        Dictionary<string, object>? written = null;
        _mockEscrowRepo.UpdateStatusAsync("escrow-001", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Do<Dictionary<string, object>?>(f => written = f))
            .Returns(Task.CompletedTask);

        var result = await _service.DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.False(written!.ContainsKey("version"));
        var createdAt = written["disputeCreatedAt"].ToString()!;
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["auditId"] == EscrowAuditRepository.DeterministicAuditId("dispute", createdAt)
            && (string)e["action"] == "dispute"));
    }

    [Fact]
    public async Task DisputeAsync_RetryAfterTheFreeze_RepairsTheAuditRowAndTheMirror()
    {
        // The first request froze the escrow and then failed (its audit write or mirror never
        // landed). The party's retry is still refused (the escrow is disputed) but finishes both.
        var escrowItem = MakeEscrowItem(status: "disputed");
        escrowItem["orderId"] = "order-1";
        escrowItem["disputeCreatedAt"] = "2026-10-09T08:00:00.0000000Z";
        escrowItem["disputedBy"] = BuyerId;
        escrowItem["disputeReason"] = "damaged_goods";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods").Returns(true);
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteOk("order-1"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error!.Code);
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["auditId"] == EscrowAuditRepository.DeterministicAuditId("dispute", "2026-10-09T08:00:00.0000000Z")));
        await deliveryRepo.Received(1).TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods");
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
    }

    [Fact]
    public async Task DisputeAsync_RetryOnAQueuedDisputedEscrow_RepairsNothing()
    {
        var escrowItem = MakeEscrowItem(status: "disputed");
        escrowItem["disputeCreatedAt"] = "2026-10-09T08:00:00.0000000Z";
        escrowItem["manualPayout"] = "open";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo).DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        await _mockAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
        await deliveryRepo.DidNotReceive().TryMarkDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // ──────────────────────────────────────────────
    // ESC-07: the dispute mirror (order and delivery follow a frozen escrow)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DisputeAsync_MirrorAuditsEveryHop_AndAdvancesAnOrderLeftBehind()
    {
        // The order lagged at in_transit behind its delivered delivery: it is advanced to
        // delivered, then disputed, and each hop (and the delivery's) gets a tx-audit row.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync(DeliveryId, BuyerId, "damaged_goods").Returns(true);
        deliveryRepo.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "disputed" }
        });
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "in_transit", 4));
        orderRepo.UpdateAsync("order-1", Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "delivered"), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Success(MakeOrder("order-1", "delivered", 5)));
        orderRepo.UpdateAsync("order-1", Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 5)
            .Returns(OrderWriteOk("order-1"));
        var txRepo = Substitute.For<ITransactionAuditRepository>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo, deliveryRepo, transactionAuditRepo: txRepo);

        var result = await service.DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 5);
        await txRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "delivery" && e["action"].ToString() == "dispute"
            && e["transactionId"].ToString() == "order-1" && e["actor"].ToString() == "system"));
        await txRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "order" && e["action"].ToString() == "order_updated"
            && e["fromState"].ToString() == "in_transit" && e["toState"].ToString() == "delivered"));
        await txRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "order" && e["action"].ToString() == "dispute"
            && e["fromState"].ToString() == "delivered" && e["toState"].ToString() == "disputed"));
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_PreDeliveryOrder_IsNotAdvanced()
    {
        // FUP-16: a dispute before delivery freezes the money only; the order stays where it is.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "inTransit" }
        });
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "in_transit", 4));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await orderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoPropagationFailureAudited();
    }

    [Fact]
    public async Task DisputeAsync_EscrowWithoutDelivery_FindsItThroughTheOrder()
    {
        // An order escrow not linked yet carries no deliveryId: the order's active delivery is used.
        var escrowItem = MakeEscrowItem(status: "escrowed");
        escrowItem.Remove("deliveryId");
        escrowItem["orderId"] = "order-1";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.TryMarkDisputedAsync("delivery-active", BuyerId, "damaged_goods").Returns(true);
        var orderRepo = Substitute.For<IOrderRepository>();
        var order = MakeOrder("order-1", "delivered", 2);
        order["activeDeliveryId"] = "delivery-active";
        orderRepo.GetByIdAsync("order-1", true).Returns(order);
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteOk("order-1"));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .DisputeAsync(MakeDisputeRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        await deliveryRepo.Received(1).TryMarkDisputedAsync("delivery-active", BuyerId, "damaged_goods");
    }

    [Fact]
    public async Task MirrorDispute_WithoutMarkDelivery_LeavesTheDeliveryWrite_ToTheCaller()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "disputed" }
        });
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1", true).Returns(MakeOrder("order-1", "delivered", 2));
        orderRepo.UpdateAsync("order-1", Arg.Any<Dictionary<string, object>>(), 2).Returns(OrderWriteOk("order-1"));

        var legs = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .MirrorDisputeAsync("escrow-001", "order-1", DeliveryId, BuyerId, "wet bags", markDelivery: false);

        Assert.Null(legs);
        await deliveryRepo.DidNotReceive().TryMarkDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await orderRepo.Received(1).UpdateAsync("order-1",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
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
    public async Task RefundAsync_FromResolvedDispute_BuyerWins_ReturnsRefunded()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeResolvedDispute("buyer_wins"));

        var result = await CreateResolverService().RefundAsync(DisputeRefundRequest(), "resolver", role: "admin");

        Assert.True(result.IsSuccess);
        Assert.Equal("refunded", result.Value!.Status);
        await _mockEscrowRepo.Received(1).TryAcquireSettlementLockAsync(
            "escrow-001", Arg.Any<string>(), "refund", "dispute_resolved");
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-001", Arg.Any<string>(), "refunded", "dispute_resolved", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task RefundAsync_FromDisputed_IsRefusedUntilAVerdictIsRecorded()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "disputed"));

        var result = await CreateResolverService().RefundAsync(DisputeRefundRequest(), "resolver", role: "admin");

        Assert.Equal(422, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error.Code);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
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
    public async Task GetByDeliveryAsync_AsBoundTrucker_ReturnsEscrowDto()
    {
        // SIM-15: the trucker the order was bound to is a party (paid the transport leg).
        var escrowItem = MakeEscrowItem();
        escrowItem["truckerId"] = "trucker-789";
        escrowItem["truckerPayout"] = 1_000m;
        _mockEscrowRepo.GetByDeliveryIdAsync("delivery-001").Returns(escrowItem);

        var result = await _service.GetByDeliveryAsync("delivery-001", "trucker-789", "trucker");

        Assert.True(result.IsSuccess);
        Assert.Equal("trucker-789", result.Value!.TruckerId);
        Assert.Equal(1_000m, result.Value.TruckerPayout);
    }

    [Fact]
    public async Task GetByIdAsync_AsBoundTrucker_ReturnsEscrowDto()
    {
        var escrowItem = MakeEscrowItem();
        escrowItem["truckerId"] = "trucker-789";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var result = await _service.GetByIdAsync("escrow-001", "trucker-789", "trucker");

        Assert.True(result.IsSuccess);
        Assert.Equal("escrow-001", result.Value!.EscrowId);
    }

    [Fact]
    public async Task GetByIdAsync_OtherTrucker_ReturnsEscrow022()
    {
        var escrowItem = MakeEscrowItem();
        escrowItem["truckerId"] = "trucker-789";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var result = await _service.GetByIdAsync("escrow-001", "trucker-000", "trucker");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_022", result.Error!.Code);
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

    [Fact]
    public async Task GetHistoryAsync_AsTrucker_QueriesTruckerIndex()
    {
        // SIM-15: role=trucker reads the TruckerStatusIndex and maps the trucker leg.
        var item = MakeEscrowItem(amount: 16_000m, commission: 375m, farmerPayout: 14_625m);
        item["truckerId"] = "trucker-789";
        item["truckerPayout"] = 1_000m;
        item["buyerName"] = "Mekonnen";
        _mockEscrowRepo.QueryByTruckerAsync("trucker-789", "escrowed", 20, null)
            .Returns((new List<Dictionary<string, object>> { item }, (Dictionary<string, AttributeValue>?)null));

        var result = await _service.GetHistoryAsync("trucker-789", "escrowed", "trucker", null, null, 20, null);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value!.Escrows);
        Assert.Equal("trucker-789", summary.TruckerId);
        Assert.Equal(1_000m, summary.TruckerPayout);
        Assert.Equal(14_625m, summary.FarmerPayout);
        // The trucker faces the buyer, like the farmer does.
        Assert.Equal("Mekonnen", summary.CounterpartyName);
        // The view reconciles: amount = farmer + trucker + commission.
        Assert.Equal(summary.Amount, summary.FarmerPayout + summary.TruckerPayout!.Value + summary.Commission);
        await _mockEscrowRepo.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockEscrowRepo.DidNotReceive().QueryByFarmerAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task GetHistoryAsync_AsFarmer_IncludesOrderFundedSelfPickupEscrow()
    {
        // A self-pickup order's escrow is order-funded: no delivery yet, no trucker.
        // Nothing in the history path may drop it (SIM-16).
        var item = MakeEscrowItem();
        item.Remove("deliveryId");
        item["orderId"] = "order-sp-1";
        item["source"] = "order";
        _mockEscrowRepo.QueryByFarmerAsync(FarmerId, null, 20, null)
            .Returns((new List<Dictionary<string, object>> { item }, (Dictionary<string, AttributeValue>?)null));

        var result = await _service.GetHistoryAsync(FarmerId, null, "farmer", null, null, 20, null);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value!.Escrows);
        Assert.Equal("order-sp-1", summary.OrderId);
        Assert.Equal("", summary.DeliveryId);
        Assert.Null(summary.TruckerId);
        Assert.Equal(14_625m, summary.FarmerPayout);
    }

    [Theory]
    [InlineData("escrowed", "order_cancelled", "2099-01-01T00:00:00Z", true)]
    [InlineData("escrowed", null, "2020-01-01T00:00:00Z", true)]
    [InlineData("escrowed", null, "2099-01-01T00:00:00Z", false)]
    [InlineData("refunded", "order_cancelled", "2020-01-01T00:00:00Z", false)]
    [InlineData("released", null, "2020-01-01T00:00:00Z", false)]
    public void MapToEscrowSummaryDto_RefundPending_OnlyWhileHeldMoneyIsGoingBack(
        string status, string? refundReason, string expiresAt, bool expected)
    {
        // SIM-20: a cancelled order's escrow stays "escrowed" until the sweep runs;
        // it must not read as funds held safely.
        var item = MakeEscrowItem(status: status);
        item["expiresAt"] = expiresAt;
        if (refundReason != null)
        {
            item["refundReason"] = refundReason;
        }

        var summary = EscrowService.MapToEscrowSummaryDto(item, isBuyer: false);
        var dto = EscrowService.MapToEscrowDto(item);

        Assert.Equal(expected, summary.RefundPending);
        Assert.Equal(expected, dto.RefundPending);
        Assert.Equal(expiresAt, summary.ExpiresAt);
        Assert.Equal(refundReason, summary.RefundReason);
        Assert.Equal(refundReason, dto.RefundReason);
    }

    [Fact]
    public async Task GetHistoryAsync_Summary_CarriesFarmerLegAndNoTruckerLegWithoutSplit()
    {
        var items = new List<Dictionary<string, object>> { MakeEscrowItem() };
        _mockEscrowRepo.QueryByFarmerAsync(FarmerId, null, 20, null)
            .Returns((items, (Dictionary<string, AttributeValue>?)null));

        var result = await _service.GetHistoryAsync(FarmerId, null, "farmer", null, null, 20, null);

        var summary = Assert.Single(result.Value!.Escrows);
        Assert.Equal(14_625m, summary.FarmerPayout);
        Assert.Null(summary.TruckerPayout);
        Assert.Null(summary.TruckerId);
        Assert.Equal(summary.Amount, summary.FarmerPayout + summary.Commission);
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
        Assert.Null(dto.TruckerPayout);
        Assert.Null(dto.TruckerId);
    }

    [Fact]
    public void MapToEscrowDto_WithTruckerSplit_MapsTruckerLeg()
    {
        var item = MakeEscrowItem(amount: 16_000m, commission: 375m, farmerPayout: 14_625m);
        item["truckerId"] = "trucker-789";
        item["truckerPayout"] = 1_000m;

        var dto = EscrowService.MapToEscrowDto(item);

        Assert.Equal("trucker-789", dto.TruckerId);
        Assert.Equal(1_000m, dto.TruckerPayout);
        Assert.Equal(dto.Amount, dto.FarmerPayout + dto.TruckerPayout!.Value + dto.Commission);
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

    // NOTE: Release/Refund concurrency is now guarded by the settlement lock (see
    // ReleaseAsync_LosesSettlementLock_* tests), not the idempotency StoreAsync — so the
    // old ReleaseAsync/RefundAsync ConcurrentIdempotencyStore tests were removed. Dispute
    // still uses the StoreAsync guard, so its test remains below.

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
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
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

    private EscrowService CreateServiceWithDeliveryAndOrderRepo(
        IDeliveryRepository deliveryRepo, IOrderRepository orderRepo)
        => new(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            _mockUserRepo, orderRepo, deliveryRepo);

    /// <summary>A pending delivery whose parties are the standard test buyer and farmer.</summary>
    private static Dictionary<string, object> MakeBoundDelivery(
        string buyerId = BuyerId, string farmerId = FarmerId, string? orderId = null)
    {
        var delivery = new Dictionary<string, object>
        {
            { "buyerId", buyerId }, { "farmerId", farmerId }, { "status", "pending" }
        };
        if (orderId != null)
        {
            delivery["orderId"] = orderId;
        }

        return delivery;
    }

    /// <summary>An order whose split (11,500 + 3,000 + 500) reconciles to a 15,000 ETB total.</summary>
    private static Dictionary<string, object> MakeBoundOrder(
        string buyerId = BuyerId, decimal orderTotal = 15_000m) => new()
    {
        { "buyerId", buyerId }, { "deliveryId", DeliveryId }, { "orderTotal", orderTotal },
        { "cropCost", 11_500m }, { "transportCost", 3_000m },
        { "commissionAmount", 500m }, { "truckerId", "trucker-1" }
    };

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
    public async Task InitiateAsync_OrderFundedEscrowLinkedToDelivery_ReturnsEscrow024NotEscrow009()
    {
        // SIM-24: the buyer paid at order time; delivery linkage stamped the deliveryId
        // on the order-funded record, so the per-delivery lookup finds it first. The
        // double-charge code must win over the duplicate-delivery code.
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-order-001" }, { "orderId", "order-001" },
            { "deliveryId", DeliveryId }, { "status", "escrowed" }, { "source", "order" }
        });

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_024", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
        Assert.Equal("orderId", result.Error.Field);
    }

    [Fact]
    public async Task InitiateAsync_OrderFundedEscrowWithoutSourceAttr_ReturnsEscrow024ByDeterministicId()
    {
        // Older order-funded rows are still recognised by their deterministic id.
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-order-001" }, { "orderId", "order-001" }, { "status", "escrowed" }
        });

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.Equal("ESCROW_024", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_DeliveryEscrowCarryingOrderId_StillReturnsEscrow009()
    {
        // A second delivery-initiated escrow (source=delivery) is a duplicate for the
        // delivery, not a double charge of the order.
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-existing" }, { "orderId", "order-001" },
            { "status", "escrowed" }, { "source", "delivery" }
        });

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_009", result.Error!.Code);
        Assert.Equal("deliveryId", result.Error.Field);
    }

    [Fact]
    public async Task InitiateAsync_RefundedOrderFundedEscrowOnDelivery_DoesNotBlock()
    {
        // An inert (refunded) order-funded record must not raise either 409.
        _mockEscrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-order-001" }, { "status", "refunded" }, { "source", "order" }
        });

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.NotEqual("ESCROW_024", result.Error?.Code);
        Assert.NotEqual("ESCROW_009", result.Error?.Code);
    }

    [Fact]
    public async Task InitiateAsync_DuplicateKeyForAnotherBuyersEscrow_Returns403()
    {
        // Attacker presents a victim's idempotency key; the cached escrow belongs to a
        // different buyer and must NOT be disclosed (A01 IDOR).
        _mockIdempotencyRepo.GetAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-victim" }, { "status", "completed" }
        });
        _mockEscrowRepo.GetByIdAsync("escrow-victim")
            .Returns(MakeEscrowItem(escrowId: "escrow-victim", buyerId: "victim-buyer"));

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_006", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_DuplicateKeyForOwnEscrow_ReturnsCached()
    {
        // A genuine same-buyer retry still returns the cached escrow.
        _mockIdempotencyRepo.GetAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-own" }, { "status", "completed" }
        });
        _mockEscrowRepo.GetByIdAsync("escrow-own")
            .Returns(MakeEscrowItem(escrowId: "escrow-own", buyerId: BuyerId));

        var result = await _service.InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsDuplicate);
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
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", "delivered" }
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
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery());

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateAsync_CallerNotDeliveryBuyer_Returns403Escrow026()
    {
        // SIM-04: a stranger (or the farmer/trucker) holding money against someone
        // else's delivery used to succeed and then block the real buyer (ESCROW_009).
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(buyerId: "buyer-other"));

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_026", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task InitiateAsync_FarmerIdNotDeliveryFarmer_Returns400Escrow027()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(farmerId: "farmer-other"));

        var result = await CreateServiceWithDeliveryRepo(deliveryRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_027", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task InitiateAsync_AmountDiffersFromOrderTotal_Returns400Escrow028()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(orderId: "order-1"));
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(MakeBoundOrder(orderTotal: 1_128.74m));

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .InitiateAsync(MakeInitiateRequest(amount: 15_000m), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_028", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Theory]
    [InlineData("funded", null)]
    [InlineData("accepted", null)]
    [InlineData("cancelled", "TB-PAID-1")]
    public async Task InitiateAsync_LinkedOrderAlreadyPaid_Returns409Escrow024(string status, string? telebirrPaymentOrderId)
    {
        // ESC-01: the order's own state says the money is held even when its escrow record is
        // missing (the funding-time create and the delivery-link self-heal both failed).
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(orderId: "order-1"));
        var order = MakeBoundOrder();
        order["status"] = status;
        if (telebirrPaymentOrderId != null)
        {
            order["telebirrPaymentOrderId"] = telebirrPaymentOrderId;
        }

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(order);

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("ESCROW_024", result.Error.Code);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task InitiateAsync_LinkedOrderMissing_Returns422()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(orderId: "order-gone"));
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-gone").Returns((Dictionary<string, object>?)null);

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .InitiateAsync(MakeInitiateRequest(), BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_VALIDATION", result.Error!.Code);
        Assert.Equal(422, result.Error.HttpStatus);
    }

    [Fact]
    public async Task InitiateAsync_ClientOrderIdDiffersFromDeliveryOrder_Returns400()
    {
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(orderId: "order-1"));
        var orderRepo = Substitute.For<IOrderRepository>();
        var request = new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 15_000m,
            Currency = "ETB",
            OrderId = "order-2",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800000"
        };

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .InitiateAsync(request, BuyerId);

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_VALIDATION", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
        await orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task InitiateAsync_BoundPartiesAndOrderTotal_SucceedsWithOrderSplit()
    {
        // The legitimate buyer path: parties match, amount == orderTotal, and the payout
        // split comes from the delivery's order even though the client sent no orderId
        // (the mobile client never does).
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync(DeliveryId).Returns(MakeBoundDelivery(orderId: "order-1"));
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(MakeBoundOrder());

        var result = await CreateServiceWithDeliveryAndOrderRepo(deliveryRepo, orderRepo)
            .InitiateAsync(MakeInitiateRequest(amount: 15_000m), BuyerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(11_500m, result.Value!.FarmerPayout);
        Assert.Equal(500m, result.Value.Commission);
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
        Assert.Equal("ESCROW_VALIDATION", result.Error!.Code);
    }

    [Fact]
    public async Task InitiateAsync_OrderSplitDoesNotReconcile_ReturnsEscrow001()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-1").Returns(new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "deliveryId", DeliveryId }, { "orderTotal", 15_000m },
            { "cropCost", 14_000m }, { "transportCost", 3_000m },
            { "commissionAmount", 500m }, { "truckerId", "trucker-1" }
        });
        // Held amount 15,000 but split sums to 17,500 → must be rejected.
        var request = new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 15_000m,
            Currency = "ETB",
            OrderId = "order-1",
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
            { "buyerId", BuyerId }, { "deliveryId", DeliveryId }, { "orderTotal", 15_000m },
            { "cropCost", 11_500m }, { "transportCost", 3_000m },
            { "commissionAmount", 500m }, { "truckerId", "trucker-1" }
        });
        var request = new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 15_000m,
            Currency = "ETB",
            OrderId = "order-1",
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
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "bogus",
            DeliveryId = DeliveryId,
            IdempotencyKey = "admin:escrow-001:release:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .ReleaseAsync(request, "admin-user", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_VALIDATION", result.Error!.Code);
    }

    [Fact]
    public async Task ReleaseAsync_LosesSettlementLock_DoesNotCallTelebirr()
    {
        // A concurrent settlement already holds the lock. This release must NOT call Telebirr
        // (no double-payout) and must return a conflict, not a masked success.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "release", Arg.Any<string>())
            .Returns(false);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-user").Returns(MakeVerifiedUser("admin-user", "admin"));
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = "admin:escrow-001:release:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .ReleaseAsync(request, "admin-user", role: "admin");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_INVALID_TRANSITION", result.Error!.Code);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task RefundAsync_LosesSettlementLockToRelease_DoesNotRefund()
    {
        // Cross-operation race: a release already holds the lock; a concurrent refund must NOT
        // call Telebirr, so funds cannot be released AND refunded from the same hold.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "refund", Arg.Any<string>())
            .Returns(false);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-user").Returns(MakeVerifiedUser("admin-user", "admin"));
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var request = new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            IdempotencyKey = "admin:escrow-001:refund:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .RefundAsync(request, "admin-user", role: "admin");

        Assert.False(result.IsSuccess);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task ReleaseAsync_LockLostButAlreadyReleased_ReturnsDuplicate()
    {
        // Lock unavailable because a prior release already settled it → return the settled result.
        _mockEscrowRepo.GetByIdAsync("escrow-001")
            .Returns(MakeEscrowItem(status: "escrowed"), MakeEscrowItem(status: "completed"));
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync("escrow-001", Arg.Any<string>(), "release", Arg.Any<string>())
            .Returns(false);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-user").Returns(MakeVerifiedUser("admin-user", "admin"));
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        var request = new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "admin_override",
            DeliveryId = DeliveryId,
            IdempotencyKey = "admin:escrow-001:release:1708171200000"
        };

        var result = await CreateServiceWithUserRepo(userRepo)
            .ReleaseAsync(request, "admin-user", role: "admin");

        Assert.True(result.IsSuccess);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
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

    // ===== Payout destination and identity at release (ESCROW_029) =====

    private static ReleaseEscrowRequest SystemRelease(string escrowId = "escrow-001") => new()
    {
        EscrowId = escrowId,
        Trigger = "delivery_confirmed",
        DeliveryId = DeliveryId,
        IdempotencyKey = $"system:{escrowId}:release:1708171200999"
    };

    [Fact]
    public async Task ReleaseAsync_FarmerPhoneNotVerified_IsBlockedBeforeTelebirr()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed", amount: 5_000m,
            commission: 125m, farmerPayout: 4_875m));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer") with { IsPhoneVerified = false });

        var result = await CreateServiceWithUserRepo(userRepo).ReleaseAsync(SystemRelease(), "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_029", result.Error!.Code);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockAuditRepo.Received().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e["action"].ToString() == "release_blocked_kyc"));
    }

    [Fact]
    public async Task ReleaseAsync_LargeAmountFarmerWithoutFayda_IsBlocked()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeUnverifiedFaydaUser(FarmerId, "farmer"));

        var result = await CreateServiceWithUserRepo(userRepo).ReleaseAsync(SystemRelease(), "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_029", result.Error!.Code);
        Assert.Contains("Fayda", result.Error.Message);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task ReleaseAsync_PaysTheFarmersRegisteredPhone_NotTheUserId()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));

        var result = await CreateServiceWithUserRepo(userRepo).ReleaseAsync(SystemRelease(), "system");

        Assert.True(result.IsSuccess);
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(
            Arg.Is<ReleaseFundsRequest>(r => r.FarmerPhone == "+251900000000" && r.Amount == 14_625m));
    }

    [Fact]
    public async Task ReleaseAsync_UnverifiedTrucker_LeavesTheTruckerLegPending()
    {
        var item = MakeEscrowItem(status: "escrowed", amount: 16_000m, commission: 400m, farmerPayout: 14_600m);
        item["truckerPayout"] = 1_000m;
        item["truckerId"] = "trucker-unverified";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(item);
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        userRepo.GetUserByIdAsync("trucker-unverified").Returns((UserDto?)null);

        var result = await CreateServiceWithUserRepo(userRepo).ReleaseAsync(SystemRelease(), "system");

        Assert.True(result.IsSuccess);
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.Amount == 14_600m));
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.Amount == 1_000m));
    }

    [Fact]
    public async Task RefundAsync_RefundsTheBuyersRegisteredPhone()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer") with { PhoneNumber = "+251933333333" });

        var result = await CreateServiceWithUserRepo(userRepo).RefundAsync(new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            Reason = "No delivery within 7 days.",
            IdempotencyKey = "system:escrow-001:refund:1708171200999"
        }, "system");

        Assert.True(result.IsSuccess);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(
            Arg.Is<RefundFundsRequest>(r => r.BuyerPhone == "+251933333333"));
    }

    // ===== ESC-13: money alarm lines =====

    private EscrowService CreateServiceCapturingAlarms(List<string> lines, IUserRepository? userRepo = null)
        => new(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway, userRepo ?? _mockUserRepo)
        {
            AlarmLog = lines.Add
        };

    [Fact]
    public async Task ReleaseAsync_TelebirrFailure_LogsSettlementFailedToken()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines).ReleaseAsync(SystemRelease(), "system");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            new[] { "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=release_failed outcome=failed code=TELEBIRR_RELEASE_FAILED" },
            lines);
    }

    [Fact]
    public async Task ReleaseAsync_TelebirrTimeout_LogsUnknownOutcome()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_TIMEOUT" });
        QueueWritesSucceed();
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines).ReleaseAsync(SystemRelease(), "system");

        // After a timeout Telebirr may still have paid: the operator must check before paying by
        // hand, and (ESC-05) the escrow is parked for the admin with its lock, never retried.
        Assert.Equal(
            new[] { "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT" },
            lines);
        Assert.Equal(202, result.Error!.HttpStatus);
        await _mockEscrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    private void QueueWritesSucceed()
        => _mockEscrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(true);

    [Fact]
    public async Task ReleaseAsync_UnverifiedTrucker_AuditsTheBlockReason_WithoutAMoneyAlarm()
    {
        var item = MakeEscrowItem(status: "escrowed", amount: 16_000m, commission: 400m, farmerPayout: 14_600m);
        item["truckerPayout"] = 1_000m;
        item["truckerId"] = "trucker-unverified";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(item);
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        userRepo.GetUserByIdAsync("trucker-unverified").Returns((UserDto?)null);
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines, userRepo).ReleaseAsync(SystemRelease(), "system");

        // ESC-04: the farmer leg succeeds and the escrow stays released. A blocked trucker is a known
        // wait, like release_blocked_kyc: its reason is audited, and the overdue alarm (24 hours)
        // rather than a settlement failure says it is still owed.
        Assert.True(result.IsSuccess);
        Assert.Equal("released", result.Value!.Status);
        Assert.Empty(lines);
        // At a deterministic key (the time stored with the reason), so a later run re-ensures it.
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "trucker_release_blocked_kyc"
            && (string)e["metadata"] == "code=ESCROW_029;reason=profile_missing"));
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "completed",
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task ReleaseAsync_TruckerGatewayFailure_LogsTruckerReleaseFailed()
    {
        var item = MakeEscrowItem(status: "escrowed", amount: 16_000m, commission: 400m, farmerPayout: 14_600m);
        item["truckerPayout"] = 1_000m;
        item["truckerId"] = "trucker-001";
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(item);
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));
        userRepo.GetUserByIdAsync("trucker-001").Returns(MakeVerifiedUser("trucker-001", "trucker"));
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.Amount == 1_000m))
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_NETWORK" });
        QueueWritesSucceed();
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines, userRepo).ReleaseAsync(SystemRelease(), "system");

        // ESC-05: the trucker may be paid, so the leg waits for the admin instead of completing
        // with truckerPayoutPending.
        Assert.Equal(202, result.Error!.HttpStatus);
        Assert.Equal(
            new[] { "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=trucker_release_failed outcome=unknown code=TELEBIRR_NETWORK" },
            lines);
    }

    [Fact]
    public async Task RefundAsync_TelebirrFailure_LogsSettlementFailedToken()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = "TELEBIRR_REFUND_FAILED" });
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines).RefundAsync(new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            Reason = "No delivery within 7 days.",
            IdempotencyKey = "system:escrow-001:refund:1708171200999"
        }, "system");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            new[] { "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=refund_failed outcome=failed code=TELEBIRR_REFUND_FAILED" },
            lines);
    }

    [Fact]
    public async Task ReleaseAsync_Success_LogsNothing()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines).ReleaseAsync(SystemRelease(), "system");

        Assert.True(result.IsSuccess);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReleaseAsync_FarmerIdentityBlocked_LogsNothing()
    {
        // release_blocked_kyc is a known wait for the farmer to verify, not a settlement failure.
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer") with { IsPhoneVerified = false });
        var lines = new List<string>();

        var result = await CreateServiceCapturingAlarms(lines, userRepo).ReleaseAsync(SystemRelease(), "system");

        Assert.False(result.IsSuccess);
        Assert.Equal("ESCROW_029", result.Error!.Code);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReleaseAsync_AuditWriteThrows_StillLogsToken()
    {
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(MakeEscrowItem(status: "escrowed"));
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });
        _mockAuditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => e["action"].ToString() == "release_failed"))
            .ThrowsAsync(new Exception("audit table down"));
        var lines = new List<string>();

        await Assert.ThrowsAsync<Exception>(() => CreateServiceCapturingAlarms(lines).ReleaseAsync(SystemRelease(), "system"));

        // The line goes out before the audit write, so a dead audit table cannot hide the failure.
        Assert.Single(lines);
        Assert.StartsWith("ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=release_failed", lines[0]);
    }
}
