using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// End-to-end integration tests for the escrow payment flow.
/// Each test chains multiple HTTP-level calls through the Lambda handler and
/// threads escrow state between steps to validate full lifecycle transitions.
///
/// Escrow lifecycle paths tested:
///   - Initiate → Release → Completed (happy path)
///   - Initiate → Dispute (buyer) → Refund (buyer wins)
///   - Initiate → Dispute (farmer) → Release (farmer wins)
///   - Idempotency: 3G retry returns cached result
///   - Authorization: buyer/farmer cannot self-release or refund
///   - Telebirr failure: hold fails → 502, audit trail written
///   - State machine enforcement: invalid transitions return 422
///   - Cron auto-release: delivery confirmed + dispute window expired
///   - Cron auto-refund: expired escrow, no delivery confirmed
///   - Response body: all required fields present and correct
///
/// DynamoDB repositories and Telebirr gateway are substituted.
/// All EscrowService business logic and EscrowFunction routing run real code.
/// </summary>
public class EscrowFlowIntegrationTests
{
    private readonly IEscrowRepository _mockEscrowRepo;
    private readonly IEscrowAuditRepository _mockAuditRepo;
    private readonly IEscrowIdempotencyRepository _mockIdempotencyRepo;
    private readonly ITelebirrGateway _mockTelebirrGateway;
    private readonly EscrowFunction _function;
    private readonly EscrowCronFunction _cronFunction;
    private readonly TestLambdaContext _context;

    private const string BuyerId = "buyer-int-456";
    private const string FarmerId = "farmer-int-123";
    private const string DeliveryId = "delivery-int-001";

    public EscrowFlowIntegrationTests()
    {
        _mockEscrowRepo = Substitute.For<IEscrowRepository>();
        _mockAuditRepo = Substitute.For<IEscrowAuditRepository>();
        _mockIdempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
        _mockTelebirrGateway = Substitute.For<ITelebirrGateway>();

        // Default: Telebirr succeeds
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = true, TelebirrRef = "MOCK-HOLD-INT-REF" });
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "MOCK-RELEASE-INT-REF" });
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-INT-REF" });

        // Default: idempotency store succeeds (no duplicate)
        _mockIdempotencyRepo.StoreAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var mockUserRepo = Substitute.For<IUserRepository>();
        mockUserRepo.GetUserByIdAsync("resolver").Returns(new UserDto
        {
            UserId = "resolver",
            PhoneNumber = "+251900000099",
            Role = "admin",
            Woreda = "test-woreda",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-17T10:00:00.0000000Z",
            UpdatedAt = "2026-02-17T10:00:00.0000000Z",
            Version = 1
        });
        // Verified, phone- and Fayda-verified buyer + farmer so initiate passes user validation.
        mockUserRepo.GetUserByIdAsync(BuyerId).Returns(MakeVerifiedUser(BuyerId, "buyer"));
        mockUserRepo.GetUserByIdAsync(FarmerId).Returns(MakeVerifiedUser(FarmerId, "farmer"));

        var service = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway, mockUserRepo);
        _function = new EscrowFunction(service);
        _cronFunction = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway);
        _context = new TestLambdaContext();
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

    // ──────────────────────────────────────────────
    // Request / response helpers
    // ──────────────────────────────────────────────

    private static APIGatewayProxyRequest MakePostRequest(
        string path, string? body = null, string? userId = null, string? role = null)
    {
        var req = new APIGatewayProxyRequest { HttpMethod = "POST", Path = path, Body = body };
        if (userId != null)
        {
            var context = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
            if (role != null)
            {
                context["role"] = role;
            }

            req.RequestContext = new ProxyRequestContext { Authorizer = context };
        }

        return req;
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path,
        string? userId = null,
        Dictionary<string, string>? pathParams = null,
        Dictionary<string, string>? queryParams = null)
    {
        var req = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            PathParameters = pathParams,
            QueryStringParameters = queryParams
        };
        if (userId != null)
        {
            req.RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } }
            };
        }

        return req;
    }

    private static APIGatewayProxyRequest MakeCronRequest(string action)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", action } })
        };
    }

    // ──────────────────────────────────────────────
    // JSON parsing helpers
    // ──────────────────────────────────────────────

    private static string GetStr(string body, string field)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(field).GetString()!;
    }

    private static decimal GetDec(string body, string field)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(field).GetDecimal();
    }

    private static bool GetBool(string body, string field)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(field).GetBoolean();
    }

    // ──────────────────────────────────────────────
    // Escrow state helpers
    // ──────────────────────────────────────────────

    private Dictionary<string, object> MakeEscrowedItem(
        string escrowId,
        decimal amount = 5_000m,
        decimal commission = 125m,
        decimal farmerPayout = 4_875m,
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
            { "status", "escrowed" },
            { "telebirrRef", "MOCK-HOLD-INT-REF" },
            { "telebirrHoldRef", "MOCK-HOLD-INT-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-20T10:00:00.0000000Z" },
            { "updatedAt", "2026-02-20T10:00:00.0000000Z" },
            { "expiresAt", "2026-02-27T10:00:00.0000000Z" },
            { "version", 2 }
        };
    }

    // ──────────────────────────────────────────────
    // Flow 1: Initiate → Release → Completed (happy path)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_InitiateReleaseCompleted_EndToEnd()
    {
        // Step 1 — Buyer initiates escrow (5,000 ETB)
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800000"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");
        Assert.StartsWith("escrow-", escrowId);

        // Verify initiate response fields
        Assert.Equal("escrowed", GetStr(initiateResponse.Body, "status"));
        Assert.Equal(125m, GetDec(initiateResponse.Body, "commission"));       // 5000 * 0.025
        Assert.Equal(4_875m, GetDec(initiateResponse.Body, "farmerPayout"));
        Assert.Equal("MOCK-HOLD-INT-REF", GetStr(initiateResponse.Body, "telebirrRef"));
        Assert.False(GetBool(initiateResponse.Body, "isDuplicate"));

        // Step 2 — System releases funds after delivery confirmation
        var escrowedItem = MakeEscrowedItem(escrowId);
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        var releaseBody = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:{escrowId}:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var releaseResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/release", releaseBody, "system"), _context);

        Assert.Equal(200, releaseResponse.StatusCode);
        Assert.Equal("completed", GetStr(releaseResponse.Body, "status"));
        Assert.Equal(4_875m, GetDec(releaseResponse.Body, "farmerPayout"));
        Assert.Equal("MOCK-RELEASE-INT-REF", GetStr(releaseResponse.Body, "telebirrRef"));

        // Telebirr: one hold (initiate) + one release (release)
        await _mockTelebirrGateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 2: Initiate → Dispute (buyer) → Refund (buyer wins)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_InitiateDisputeRefund_BuyerWins()
    {
        // Step 1 — Initiate (8,000 ETB)
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 8_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800001"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Step 2 — Buyer raises dispute (damaged goods)
        var escrowedItem = MakeEscrowedItem(escrowId, amount: 8_000m, commission: 200m, farmerPayout: 7_800m);
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        var disputeBody = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = escrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            Description = "Teff bags arrived wet and damaged.",
            IdempotencyKey = $"{BuyerId}:{escrowId}:dispute:1708171200000"
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var disputeResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", disputeBody, BuyerId), _context);

        Assert.Equal(200, disputeResponse.StatusCode);
        Assert.Equal("disputed", GetStr(disputeResponse.Body, "status"));
        Assert.Equal(BuyerId, GetStr(disputeResponse.Body, "disputedBy"));
        Assert.Equal("damaged_goods", GetStr(disputeResponse.Body, "reason"));
        var disputeId = GetStr(disputeResponse.Body, "disputeId");
        Assert.StartsWith("dispute-", disputeId);

        // Step 3 — Admin resolves in buyer's favor: refund full amount
        var disputedItem = new Dictionary<string, object>(escrowedItem)
        {
            ["status"] = "disputed",
            ["disputeId"] = disputeId,
            ["disputedBy"] = BuyerId,
            ["disputeReason"] = "damaged_goods",
            ["disputeCreatedAt"] = DateTime.UtcNow.AddHours(-2).ToString("o"),
            ["disputeWindowEndsAt"] = DateTime.UtcNow.AddHours(46).ToString("o"),
            ["version"] = 3
        };
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(disputedItem);

        var refundBody = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "dispute_resolved",
            Reason = "Admin ruled in buyer's favour: damaged goods confirmed.",
            IdempotencyKey = $"admin:{escrowId}:refund:1708257600000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var refundResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/refund", refundBody, "resolver", role: "admin"), _context);

        Assert.Equal(200, refundResponse.StatusCode);
        Assert.Equal("refunded", GetStr(refundResponse.Body, "status"));
        Assert.Equal(8_000m, GetDec(refundResponse.Body, "refundAmount")); // Full refund, no commission
        Assert.Equal("MOCK-REFUND-INT-REF", GetStr(refundResponse.Body, "telebirrRef"));

        // Telebirr: hold (initiate) + refund (refund). No release.
        await _mockTelebirrGateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 3: Initiate → Dispute (farmer) → Release (farmer wins)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_InitiateDisputeRelease_FarmerWins()
    {
        // Step 1 — Initiate
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 3_500m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800002"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Step 2 — Farmer raises dispute
        var escrowedItem = MakeEscrowedItem(escrowId, amount: 3_500m, commission: 87.5m, farmerPayout: 3_412.5m);
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        var disputeBody = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = escrowId,
            DisputedBy = FarmerId,
            Reason = "wrong_quantity",
            Description = "Buyer claims 500 kg but we delivered 600 kg.",
            IdempotencyKey = $"{FarmerId}:{escrowId}:dispute:1708171200001"
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var disputeResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", disputeBody, FarmerId), _context);

        Assert.Equal(200, disputeResponse.StatusCode);
        Assert.Equal("disputed", GetStr(disputeResponse.Body, "status"));
        Assert.Equal(FarmerId, GetStr(disputeResponse.Body, "disputedBy"));
        Assert.Equal("wrong_quantity", GetStr(disputeResponse.Body, "reason"));

        // Step 3 — Admin resolves in farmer's favour: release funds
        var disputedItem = new Dictionary<string, object>(escrowedItem)
        {
            ["status"] = "disputed",
            ["disputeId"] = "dispute-farmer-test-001",
            ["disputedBy"] = FarmerId,
            ["disputeReason"] = "wrong_quantity",
            ["disputeCreatedAt"] = DateTime.UtcNow.AddHours(-1).ToString("o"),
            ["version"] = 3
        };
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(disputedItem);

        var releaseBody = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "dispute_resolved",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"admin:{escrowId}:release:1708171200001"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var releaseResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/release", releaseBody, "resolver", role: "admin"), _context);

        Assert.Equal(200, releaseResponse.StatusCode);
        Assert.Equal("completed", GetStr(releaseResponse.Body, "status"));
        Assert.Equal(3_412.5m, GetDec(releaseResponse.Body, "farmerPayout"));
        Assert.Equal(87.5m, GetDec(releaseResponse.Body, "commission"));
    }

    // ──────────────────────────────────────────────
    // Flow 4: Complete response body field validation on initiate
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_Initiate_ResponseBodyContainsAllRequiredFields()
    {
        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 10_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800003"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", body, BuyerId), _context);

        Assert.Equal(201, response.StatusCode);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        // Presence checks
        foreach (var field in new[]
        {
            "escrowId", "deliveryId", "buyerId", "farmerId",
            "amount", "commission", "farmerPayout", "currency",
            "status", "telebirrRef", "createdAt", "expiresAt", "isDuplicate"
        })
        {
            Assert.True(root.TryGetProperty(field, out _), $"Missing field: {field}");
        }

        // Value checks
        Assert.Equal(DeliveryId, root.GetProperty("deliveryId").GetString());
        Assert.Equal(BuyerId, root.GetProperty("buyerId").GetString());
        Assert.Equal(FarmerId, root.GetProperty("farmerId").GetString());
        Assert.Equal(10_000m, root.GetProperty("amount").GetDecimal());
        Assert.Equal(250m, root.GetProperty("commission").GetDecimal());     // 10000 * 0.025
        Assert.Equal(9_750m, root.GetProperty("farmerPayout").GetDecimal());
        Assert.Equal("ETB", root.GetProperty("currency").GetString());
        Assert.Equal("escrowed", root.GetProperty("status").GetString());
        Assert.Equal("MOCK-HOLD-INT-REF", root.GetProperty("telebirrRef").GetString());
        Assert.False(root.GetProperty("isDuplicate").GetBoolean());
    }

    // ──────────────────────────────────────────────
    // Flow 5: Idempotency — 3G double-submit returns cached result
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_Idempotency_3GRetry_ReturnsCachedResult()
    {
        var idempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800004";
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = idempotencyKey
        }, AppJsonContext.Default.InitiateEscrowRequest);

        // First request: succeeds (no prior idempotency record)
        var firstResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(201, firstResponse.StatusCode);
        var escrowId = GetStr(firstResponse.Body, "escrowId");
        Assert.False(GetBool(firstResponse.Body, "isDuplicate"));

        // Simulate: the idempotency record now exists (3G retry)
        var escrowedItem = MakeEscrowedItem(escrowId);
        _mockIdempotencyRepo.GetAsync(idempotencyKey)
            .Returns(new Dictionary<string, object>
            {
                { "escrowId", escrowId },
                { "status", "completed" }
            });
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        // Second request: same key → returns cached result with isDuplicate=true
        var secondResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(200, secondResponse.StatusCode);
        Assert.Equal(escrowId, GetStr(secondResponse.Body, "escrowId"));
        Assert.True(GetBool(secondResponse.Body, "isDuplicate"));

        // Telebirr HoldFunds called only once — not again on retry
        await _mockTelebirrGateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 6: Authorization — buyer cannot self-release
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_BuyerCannotRelease_Returns403()
    {
        // Initiate
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800005"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);
        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Buyer attempts self-release — must be blocked (only system/admin allowed)
        var releaseBody = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"{BuyerId}:{escrowId}:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var releaseResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/release", releaseBody, BuyerId), _context);

        Assert.Equal(403, releaseResponse.StatusCode);
        Assert.Contains("ESCROW_015", releaseResponse.Body);

        // Telebirr release must NOT have been called
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 7: Authorization — farmer cannot refund
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_FarmerCannotRefund_Returns403()
    {
        // Initiate
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800006"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);
        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Farmer attempts refund — must be blocked
        var refundBody = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_timeout",
            IdempotencyKey = $"{FarmerId}:{escrowId}:refund:1708257600000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var refundResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/refund", refundBody, FarmerId), _context);

        Assert.Equal(403, refundResponse.StatusCode);
        Assert.Contains("ESCROW_021", refundResponse.Body);

        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 8: Telebirr hold failure — 502, audit trail written, idempotency marked failed
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_TelebirrHoldFailure_Returns502_AuditWritten_IdempotencyFailed()
    {
        var idempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800007";

        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_HOLD_FAILED",
                ErrorMessage = "Insufficient balance."
            });

        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = idempotencyKey
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(502, response.StatusCode);
        Assert.Contains("ESCROW_013", response.Body);

        // Idempotency key must be deleted so retries can attempt a fresh hold
        await _mockIdempotencyRepo.Received(1).DeleteAsync(idempotencyKey);

        // 2 audit entries: "initiate" (→ initiated) + "hold_failed" (initiated → initiated)
        await _mockAuditRepo.Received(2).AppendAsync(Arg.Any<Dictionary<string, object>>());

        // Telebirr release/refund must NOT have been called
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 9: State machine — cannot dispute a completed escrow
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CannotDisputeCompletedEscrow_Returns422()
    {
        var escrowId = "escrow-completed-int-001";
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "completed" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-20T10:00:00Z" },
            { "updatedAt", "2026-02-20T10:00:00Z" },
            { "expiresAt", "2026-02-27T10:00:00Z" },
            { "version", 4 }
        });

        var disputeBody = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = escrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:{escrowId}:dispute:1708171200000"
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", disputeBody, BuyerId), _context);

        Assert.Equal(422, response.StatusCode);
        Assert.Contains("ESCROW_INVALID_TRANSITION", response.Body);
        Assert.Contains("completed", response.Body);
    }

    // ──────────────────────────────────────────────
    // Flow 10: State machine — cannot refund a released escrow
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CannotRefundReleasedEscrow_Returns422()
    {
        var escrowId = "escrow-released-int-001";
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "released" },
            { "telebirrHoldRef", "MOCK-HOLD-INT-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-20T10:00:00Z" },
            { "updatedAt", "2026-02-20T10:00:00Z" },
            { "expiresAt", "2026-02-27T10:00:00Z" },
            { "version", 3 }
        });

        var refundBody = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            IdempotencyKey = $"admin:{escrowId}:refund:1708257600000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/refund", refundBody, "resolver", role: "admin"), _context);

        Assert.Equal(422, response.StatusCode);
        Assert.Contains("ESCROW_INVALID_TRANSITION", response.Body);
        Assert.Contains("released", response.Body);

        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 11: Cron auto-release — delivery confirmed + dispute window expired
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CronAutoRelease_DeliveryConfirmedWindowExpired()
    {
        var pastWindow = DateTime.UtcNow.AddHours(-49).ToString("o");
        var deliveryConfirmed = DateTime.UtcNow.AddHours(-50).ToString("o");

        var escrowId = "escrow-cron-release-int-001";
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", $"del-{escrowId}" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 15_000m },
            { "commission", 375m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 14_625m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "telebirrHoldRef", "MOCK-HOLD-INT-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-10T10:00:00.0000000Z" },
            { "updatedAt", "2026-02-10T10:00:00.0000000Z" },
            { "expiresAt", "2026-02-27T10:00:00.0000000Z" },
            { "deliveryConfirmedAt", deliveryConfirmed },
            { "disputeWindowEndsAt", pastWindow },
            { "version", 2 }
        };

        _mockEscrowRepo
            .QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrow);

        var response = await _cronFunction.FunctionHandler(
            MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"released\":\"1\"", response.Body);
        Assert.Contains("\"refunded\":\"0\"", response.Body);

        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 12: Cron auto-refund — expired escrow, no delivery confirmed
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CronAutoRefund_ExpiredEscrowNoDelivery()
    {
        var escrowId = "escrow-cron-refund-int-001";
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", $"del-{escrowId}" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "telebirrHoldRef", "MOCK-HOLD-INT-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-12T10:00:00.0000000Z" },
            { "updatedAt", "2026-02-12T10:00:00.0000000Z" },
            { "expiresAt", expiredAt },
            // No deliveryConfirmedAt — delivery was never made
            { "version", 2 }
        };

        // Auto-release query: no eligible escrows (no confirmed delivery + expired window)
        _mockEscrowRepo
            .QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        // Auto-refund query (expiresAtBefore): returns the expired escrow
        _mockEscrowRepo
            .QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrow);

        var response = await _cronFunction.FunctionHandler(
            MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"refunded\":\"1\"", response.Body);
        Assert.Contains("\"released\":\"0\"", response.Body);

        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 13: Initiate → GET /escrow/{id} — full EscrowDto validated
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_InitiateAndGetById_ReturnsCompleteEscrowDto()
    {
        // Step 1 — Initiate
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800008"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);

        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Step 2 — GET /escrow/{id} as buyer
        var escrowedItem = MakeEscrowedItem(escrowId);
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        var getResponse = await _function.FunctionHandler(
            MakeGetRequest($"/escrow/{escrowId}", BuyerId,
                pathParams: new Dictionary<string, string> { { "id", escrowId } }),
            _context);

        Assert.Equal(200, getResponse.StatusCode);

        using var doc = JsonDocument.Parse(getResponse.Body);
        var root = doc.RootElement;
        Assert.Equal(escrowId, root.GetProperty("escrowId").GetString());
        Assert.Equal(DeliveryId, root.GetProperty("deliveryId").GetString());
        Assert.Equal(BuyerId, root.GetProperty("buyerId").GetString());
        Assert.Equal(FarmerId, root.GetProperty("farmerId").GetString());
        Assert.Equal(5_000m, root.GetProperty("amount").GetDecimal());
        Assert.Equal(125m, root.GetProperty("commission").GetDecimal());
        Assert.Equal(4_875m, root.GetProperty("farmerPayout").GetDecimal());
        Assert.Equal("escrowed", root.GetProperty("status").GetString());
        Assert.Equal("MOCK-HOLD-INT-REF", root.GetProperty("telebirrRef").GetString());
    }

    // ──────────────────────────────────────────────
    // Flow 14: Initiate → GET /escrow/history — entry appears with correct counterpartyName
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_InitiateAndHistory_EntryAppearsAsBuyer()
    {
        // Step 1 — Initiate
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800009"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);
        Assert.Equal(201, initiateResponse.StatusCode);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Step 2 — GET /escrow/history as buyer
        var historyItems = new List<Dictionary<string, object>>
        {
            new()
            {
                { "escrowId", escrowId },
                { "deliveryId", DeliveryId },
                { "buyerId", BuyerId },
                { "farmerId", FarmerId },
                { "farmerName", "Abebe Bekele" }, // counterpartyName for buyer perspective
                { "amount", 5_000m },
                { "commission", 125m },
                { "currency", "ETB" },
                { "status", "escrowed" },
                { "createdAt", "2026-02-20T10:00:00Z" }
            }
        };
        _mockEscrowRepo.QueryByBuyerAsync(BuyerId, null, 20, null)
            .Returns((historyItems, (Dictionary<string, AttributeValue>?)null));

        var historyResponse = await _function.FunctionHandler(
            MakeGetRequest("/escrow/history", BuyerId), _context);

        Assert.Equal(200, historyResponse.StatusCode);

        using var doc = JsonDocument.Parse(historyResponse.Body);
        var escrows = doc.RootElement.GetProperty("escrows");
        Assert.Equal(1, escrows.GetArrayLength());
        Assert.Equal(escrowId, escrows[0].GetProperty("escrowId").GetString());
        Assert.Equal("Abebe Bekele", escrows[0].GetProperty("counterpartyName").GetString());
        Assert.Equal(5_000m, escrows[0].GetProperty("amount").GetDecimal());
        Assert.False(doc.RootElement.GetProperty("hasMore").GetBoolean());
    }

    // ──────────────────────────────────────────────
    // Flow 15: Commission floor — 5 ETB minimum across amount boundary values
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(100, 5.0, 95.0)]      // 100 * 0.025 = 2.5 → floor to 5
    [InlineData(150, 5.0, 145.0)]     // 150 * 0.025 = 3.75 → floor to 5
    [InlineData(200, 5.0, 195.0)]     // 200 * 0.025 = 5.0 → exactly at minimum
    [InlineData(1_000, 25.0, 975.0)]  // 1000 * 0.025 = 25 → no floor needed
    [InlineData(5_000, 125.0, 4_875.0)]
    public async Task Flow_CommissionFloor_ValidatedInInitiateResponse(
        double amountDouble, double expectedCommission, double expectedPayout)
    {
        var amount = (decimal)amountDouble;
        var commission = (decimal)expectedCommission;
        var payout = (decimal)expectedPayout;

        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = amount,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:{(long)amountDouble * 1000}"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", body, BuyerId), _context);

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(commission, GetDec(response.Body, "commission"));
        Assert.Equal(payout, GetDec(response.Body, "farmerPayout"));
    }

    // ──────────────────────────────────────────────
    // Flow 16: Dispute window edge — expired by 1 second
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_DisputeWindowExpiredByOneSecond_Returns400()
    {
        // Delivery confirmed 48 hours and 1 second ago → window just closed
        var confirmedAt = DateTime.UtcNow.AddHours(-48).AddSeconds(-1);

        var escrowId = "escrow-window-edge-int";
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "deliveryConfirmedAt", confirmedAt.ToString("o") },
            { "telebirrHoldRef", "MOCK-HOLD-INT-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 2 }
        });

        var disputeBody = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = escrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:{escrowId}:dispute:1708171200000"
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", disputeBody, BuyerId), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_018", response.Body);
    }

    // ──────────────────────────────────────────────
    // Flow 17: Audit trail — entries written at each lifecycle step
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_Initiate_WritesTwoAuditEntries()
    {
        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800010"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", body, BuyerId), _context);

        Assert.Equal(201, response.StatusCode);

        // Expect 2 entries: "initiate" (→ initiated) and "hold_success" (initiated → escrowed)
        await _mockAuditRepo.Received(2).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Flow_InitiateThenRelease_WritesFourAuditEntries()
    {
        // Step 1 — Initiate (2 audit entries)
        var initiateBody = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800011"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var initiateResponse = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", initiateBody, BuyerId), _context);
        var escrowId = GetStr(initiateResponse.Body, "escrowId");

        // Step 2 — Release (2 more audit entries: "release" + "complete")
        var escrowedItem = MakeEscrowedItem(escrowId);
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrowedItem);

        var releaseBody = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = DeliveryId,
            IdempotencyKey = $"system:{escrowId}:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        await _function.FunctionHandler(
            MakePostRequest("/escrow/release", releaseBody, "system"), _context);

        // Total: 2 (initiate) + 2 (release + complete) = 4
        await _mockAuditRepo.Received(4).AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ──────────────────────────────────────────────
    // Flow 18: Idempotency — concurrent store rejected (ESCROW_003)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_ConcurrentIdempotencyStore_Returns409()
    {
        // Simulate: another concurrent request already claimed this key
        _mockIdempotencyRepo.StoreAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:1708084800012"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", body, BuyerId), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("ESCROW_003", response.Body);

        // Telebirr must NOT have been called — request aborted before gateway call
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Flow 19: Cron reconciliation — stuck "initiated" escrow with Telebirr ref confirmed
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CronReconciliation_ConfirmedTelebirrRef_TransitionsToEscrowed()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrowId = "escrow-cron-recon-int-001";

        var stuckEscrow = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "initiated" },
            { "telebirrRef", "TBR-INT-REF-001" }, // Telebirr ref exists — hold call did go through
            { "createdAt", staleCreatedAt },
            { "expiresAt", "2026-02-27T10:00:00Z" },
            { "version", 1 }
        };

        _mockEscrowRepo
            .QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { stuckEscrow });

        _mockTelebirrGateway.GetTransactionStatusAsync("TBR-INT-REF-001")
            .Returns(new TransactionStatusResponse
            {
                TransactionRef = "TBR-INT-REF-001",
                Status = "completed",
                TelebirrRef = "TBR-INT-CONFIRMED-001",
                Amount = 5_000m,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });

        var response = await _cronFunction.FunctionHandler(
            MakeCronRequest("reconciliation"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"reconciledToEscrowed\":\"1\"", response.Body);
        Assert.Contains("\"reconciledToCancelled\":\"0\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Flow 20: Cron reconciliation — stuck "initiated" with no Telebirr ref → cancelled
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Flow_CronReconciliation_NoTelebirrRef_CancelsEscrow()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrowId = "escrow-cron-recon-int-002";

        var stuckEscrow = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "initiated" },
            // No telebirrRef — hold call never completed
            { "createdAt", staleCreatedAt },
            { "expiresAt", "2026-02-27T10:00:00Z" },
            { "version", 1 }
        };

        _mockEscrowRepo
            .QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { stuckEscrow });

        var response = await _cronFunction.FunctionHandler(
            MakeCronRequest("reconciliation"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"reconciledToCancelled\":\"1\"", response.Body);

        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            escrowId, staleCreatedAt, "cancelled", "initiated",
            Arg.Any<Dictionary<string, object>>());
    }
}
