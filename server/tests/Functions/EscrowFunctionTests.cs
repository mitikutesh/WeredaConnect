using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

public class EscrowFunctionTests
{
    private readonly IEscrowRepository _mockEscrowRepo;
    private readonly IEscrowAuditRepository _mockAuditRepo;
    private readonly IEscrowIdempotencyRepository _mockIdempotencyRepo;
    private readonly ITelebirrGateway _mockTelebirrGateway;
    private readonly EscrowFunction _function;
    private readonly TestLambdaContext _context;

    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";

    public EscrowFunctionTests()
    {
        _mockEscrowRepo = Substitute.For<IEscrowRepository>();
        _mockAuditRepo = Substitute.For<IEscrowAuditRepository>();
        _mockIdempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
        _mockTelebirrGateway = Substitute.For<ITelebirrGateway>();

        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = true, TelebirrRef = "MOCK-HOLD-REF" });
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "MOCK-RELEASE-REF" });
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-REF" });

        // Default: idempotency store succeeds (no duplicate)
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        _function = new EscrowFunction(service);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakePostRequest(
        string path,
        string? body = null,
        string? userId = null,
        string? role = "buyer")
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = path,
            Body = body
        };

        if (userId != null)
        {
            var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
            if (role != null)
            {
                authorizer["custom:role"] = role;
            }

            request.RequestContext = new ProxyRequestContext { Authorizer = authorizer };
        }

        return request;
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path,
        string? userId = null,
        Dictionary<string, string>? pathParams = null,
        Dictionary<string, string>? queryParams = null,
        string? role = null)
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            PathParameters = pathParams,
            QueryStringParameters = queryParams
        };

        if (userId != null)
        {
            var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
            if (role != null)
            {
                authorizer["custom:role"] = role;
            }

            request.RequestContext = new ProxyRequestContext
            {
                Authorizer = authorizer
            };
        }

        return request;
    }

    // ──────────────────────────────────────────────
    // Routing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_PostInitiate_Routes()
    {
        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = "del-001",
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:del-001:initiate:1708084800000"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", body, BuyerId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_BoundsTelebirrCallsByTheRemainingTime()
    {
        // ESC-45 (D6): the gateway sees a deadline 5 s before the end of the run.
        TimeSpan? seen = null;
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>()).Returns(_ =>
        {
            seen = WeredaConnect.Server.Helpers.GatewayDeadline.Remaining;
            return new HoldFundsResponse { Success = true, TelebirrRef = "MOCK-HOLD-REF" };
        });
        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = "del-001",
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:del-001:initiate:1708084800001"
        }, AppJsonContext.Default.InitiateEscrowRequest);
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", body, BuyerId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await _function.FunctionHandler(request, new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(20) });

        Assert.NotNull(seen);
        Assert.InRange(seen!.Value, TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(15.5));
        Assert.Null(WeredaConnect.Server.Helpers.GatewayDeadline.Remaining);
    }

    [Fact]
    public async Task FunctionHandler_GetById_Routes()
    {
        var escrowItem = new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        };
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var request = MakeGetRequest("/escrow/escrow-001", BuyerId,
            pathParams: new Dictionary<string, string> { { "id", "escrow-001" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_GetByDelivery_Routes()
    {
        var escrowItem = new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        };
        _mockEscrowRepo.GetByDeliveryIdAsync("del-001").Returns(escrowItem);

        var request = MakeGetRequest("/escrow/by-delivery/del-001", BuyerId,
            pathParams: new Dictionary<string, string> { { "deliveryId", "del-001" } });

        var response = await _function.FunctionHandler(request, _context);

        // Routed to the by-delivery handler via the deliveryId path param, not GetById.
        Assert.Equal(200, response.StatusCode);
        await _mockEscrowRepo.Received(1).GetByDeliveryIdAsync("del-001");
        await _mockEscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_GetByDelivery_Unauthenticated_Returns401()
    {
        var request = MakeGetRequest(
            "/escrow/by-delivery/del-001",
            userId: null,
            pathParams: new Dictionary<string, string> { { "deliveryId", "del-001" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_MethodNotAllowed_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "DELETE",
            Path = "/escrow/escrow-001",
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", BuyerId } }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Authentication
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleInitiate_Unauthenticated_Returns401()
    {
        var request = MakePostRequest("/escrow/initiate", body: "{}", userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("ESCROW_005", response.Body);
    }

    [Fact]
    public async Task HandleRelease_Unauthenticated_Returns401()
    {
        var request = MakePostRequest("/escrow/release", body: "{}", userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task HandleDispute_Unauthenticated_Returns401()
    {
        var request = MakePostRequest("/escrow/dispute", body: "{}", userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task HandleRefund_Unauthenticated_Returns401()
    {
        var request = MakePostRequest("/escrow/refund", body: "{}", userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task HandleHistory_Unauthenticated_Returns401()
    {
        var request = MakeGetRequest("/escrow/history", userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetById_Unauthenticated_Returns401()
    {
        var request = MakeGetRequest("/escrow/escrow-001", userId: null,
            pathParams: new Dictionary<string, string> { { "id", "escrow-001" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Invalid JSON
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    [InlineData("admin")]
    public async Task HandleInitiate_NonBuyerRole_Returns403Escrow025(string role)
    {
        // SIM-04: only buyers may hold money; the service must not even be reached.
        var request = MakePostRequest("/escrow/initiate", ValidInitiateBody(), BuyerId, role: role);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ESCROW_025", response.Body);
        await _mockIdempotencyRepo.DidNotReceive().GetAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleInitiate_MissingRoleClaim_Returns403Escrow025()
    {
        // Fail closed: an access token (no custom:role) must not be able to hold money.
        var request = MakePostRequest("/escrow/initiate", ValidInitiateBody(), BuyerId, role: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ESCROW_025", response.Body);
        await _mockIdempotencyRepo.DidNotReceive().GetAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleInitiate_LiveTelebirr_Returns422Escrow039_NothingWrittenNothingCounted()
    {
        // FUP-36: the refusal is decided before any write, any gateway call and the velocity count.
        _mockTelebirrGateway.IsLiveTelebirr.Returns(true);
        var fraud = Substitute.For<IFraudDetectionService>();
        fraud.CheckPaymentVelocityAsync(BuyerId).Returns(new FraudCheckResult { IsAllowed = true });
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowFunction(service, fraud);
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", ValidInitiateBody(), BuyerId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(422, response.StatusCode);
        Assert.Contains("ESCROW_039", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _mockIdempotencyRepo.DidNotReceive().StoreAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _mockEscrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await fraud.DidNotReceive().RecordPaymentInitiatedAsync(Arg.Any<string>());
        await fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    private static string ValidInitiateBody() =>
        $$"""{"deliveryId":"del-001","buyerId":"{{BuyerId}}","farmerId":"{{FarmerId}}","amount":5000,"currency":"ETB","idempotencyKey":"{{BuyerId}}:del-001:initiate:1708084800000"}""";

    [Fact]
    public async Task HandleInitiate_InvalidJson_Returns400()
    {
        var request = MakePostRequest("/escrow/initiate", body: "{{bad json", userId: BuyerId);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleInitiate_NullBody_Returns400()
    {
        var request = MakePostRequest("/escrow/initiate", body: null, userId: BuyerId);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Route parsing for all endpoints
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_PostRelease_Routes()
    {
        var escrowItem = new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "telebirrHoldRef", "MOCK-HOLD-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        };
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var body = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = "del-001",
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var request = MakePostRequest("/escrow/release", body, "system");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_PostDispute_Routes()
    {
        var escrowItem = new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        };
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var body = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = "escrow-001",
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:escrow-001:dispute:1708171200000"
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var request = MakePostRequest("/escrow/dispute", body, BuyerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_PostRefund_Routes()
    {
        var escrowItem = new Dictionary<string, object>
        {
            { "escrowId", "escrow-001" },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 5000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "telebirrHoldRef", "MOCK-HOLD-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        };
        _mockEscrowRepo.GetByIdAsync("escrow-001").Returns(escrowItem);

        var body = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_timeout",
            IdempotencyKey = "system:escrow-001:refund:1708257600000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var request = MakePostRequest("/escrow/refund", body, "system");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_GetHistory_Routes()
    {
        _mockEscrowRepo.QueryByBuyerAsync(BuyerId, null, 20, null)
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest("/escrow/history", BuyerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_GetHistory_RoleTrucker_QueriesTruckerIndex()
    {
        // SIM-15: ?role=trucker reaches the trucker GSI, not the buyer default.
        _mockEscrowRepo.QueryByTruckerAsync("trucker-789", null, 20, null)
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest("/escrow/history", "trucker-789",
            queryParams: new Dictionary<string, string> { { "role", "trucker" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockEscrowRepo.Received(1).QueryByTruckerAsync("trucker-789", null, 20, null);
        await _mockEscrowRepo.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task FunctionHandler_GetHistory_NoRoleParam_ScopesByRoleClaim(string role)
    {
        // SIM-16: an unfiltered read used to default to the buyer GSI, so a farmer
        // or trucker read []. The token's role now picks the index.
        _mockEscrowRepo.QueryByFarmerAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _mockEscrowRepo.QueryByTruckerAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest("/escrow/history", "user-1", role: role);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        if (role == "farmer")
        {
            await _mockEscrowRepo.Received(1).QueryByFarmerAsync("user-1", null, 20, null);
        }
        else
        {
            await _mockEscrowRepo.Received(1).QueryByTruckerAsync("user-1", null, 20, null);
        }

        await _mockEscrowRepo.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ──────────────────────────────────────────────
    // Error response format (EscrowErrorResponse with code, message)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ErrorResponse_ContainsErrorCodeAndMessage()
    {
        var request = MakePostRequest("/escrow/initiate", body: "{{bad json", userId: BuyerId);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        // Verify the error body contains the expected structure
        Assert.Contains("\"code\":", response.Body);
        Assert.Contains("\"message\":", response.Body);
        Assert.Contains("ESCROW_001", response.Body);
    }

    [Fact]
    public async Task ErrorResponse_405_ContainsValidationCode()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "PATCH",
            Path = "/escrow/initiate",
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", BuyerId } }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("ESCROW_VALIDATION", response.Body);
    }

    // ──────────────────────────────────────────────
    // S4: Exception logs don't contain raw messages
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleInitiate_Exception_Returns500WithGenericMessage()
    {
        // Force an exception by having the service throw
        _mockIdempotencyRepo.GetAsync(Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("Sensitive DynamoDB error with phone +251912345678"));

        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = "del-001",
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:del-001:initiate:1708084800000"
        }, AppJsonContext.Default.InitiateEscrowRequest);

        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", body, BuyerId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("ESCROW_012", response.Body);
        // The response body should NOT contain the sensitive exception message
        Assert.DoesNotContain("phone", response.Body);
        Assert.DoesNotContain("+251912345678", response.Body);
        Assert.DoesNotContain("Sensitive DynamoDB error", response.Body);
    }

    [Fact]
    public async Task HandleRelease_Exception_DoesNotLeakSensitiveData()
    {
        _mockEscrowRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("Table not found: arn:aws:dynamodb:secret-info"));

        var body = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = "del-001",
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var request = MakePostRequest("/escrow/release", body, "system");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.DoesNotContain("arn:aws:dynamodb", response.Body);
        Assert.DoesNotContain("secret-info", response.Body);
    }

    // ──────────────────────────────────────────────
    // Invalid JSON for other endpoints
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleRelease_InvalidJson_Returns400()
    {
        var request = MakePostRequest("/escrow/release", body: "{{bad json", userId: "system");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleDispute_InvalidJson_Returns400()
    {
        var request = MakePostRequest("/escrow/dispute", body: "{{bad json", userId: BuyerId);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleRefund_InvalidJson_Returns400()
    {
        var request = MakePostRequest("/escrow/refund", body: "{{bad json", userId: "system");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // GET /escrow/{id} without path parameter
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleGetById_MissingPathParam_Returns405()
    {
        // GET /escrow/something without pathParameters containing "id"
        var request = MakeGetRequest("/escrow/some-id", BuyerId);
        // No pathParameters set — this won't match the GetById route
        var response = await _function.FunctionHandler(request, _context);

        // Without "id" in PathParameters, the route falls through to the default case
        Assert.Equal(405, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Step-up: initiate is tiered (password up to 10,000 ETB, AUTH_040; Fayda
    // above, AUTH_041); disputes on large escrows need a fresh password
    // ──────────────────────────────────────────────

    private static APIGatewayProxyRequest WithAuthTime(APIGatewayProxyRequest request, long authTime)
    {
        request.RequestContext!.Authorizer["auth_time"] = authTime.ToString();
        return request;
    }

    private static string InitiateBody(decimal amount) => JsonSerializer.Serialize(new InitiateEscrowRequest
    {
        DeliveryId = "del-001",
        BuyerId = BuyerId,
        FarmerId = FarmerId,
        Amount = amount,
        Currency = "ETB",
        IdempotencyKey = $"{BuyerId}:del-001:initiate:1708084800777"
    }, AppJsonContext.Default.InitiateEscrowRequest);

    private void SetupEscrow(string escrowId, decimal amount)
    {
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", "del-001" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", amount },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", amount - 125m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "buyerFaydaVerified", true },
            { "farmerFaydaVerified", true },
            { "createdAt", "2026-02-17T10:00:00Z" },
            { "updatedAt", "2026-02-17T10:00:00Z" },
            { "expiresAt", "2026-02-24T10:00:00Z" },
            { "version", 1 }
        });
    }

    private static string DisputeBody(string escrowId) => JsonSerializer.Serialize(new DisputeEscrowRequest
    {
        EscrowId = escrowId,
        DisputedBy = BuyerId,
        Reason = "damaged_goods",
        IdempotencyKey = $"{BuyerId}:{escrowId}:dispute:1708171200999"
    }, AppJsonContext.Default.DisputeEscrowRequest);

    /// <summary>The function with a buyer whose last Fayda login was at <paramref name="faydaAuthenticatedAt"/>.</summary>
    private EscrowFunction FunctionWithBuyerFayda(string? faydaAuthenticatedAt)
    {
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251900000003",
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            FaydaVerified = true,
            FaydaAuthenticatedAt = faydaAuthenticatedAt,
            CreatedAt = "2026-09-25T10:00:00Z",
            UpdatedAt = "2026-09-25T10:00:00Z",
            Version = 1
        });
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        return new EscrowFunction(service, userRepo: userRepo);
    }

    [Fact]
    public async Task Initiate_LargeAmount_WithoutFaydaLogin_Returns403FaydaStepUp()
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_041", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task Initiate_LargeAmount_FreshPasswordOnly_StillNeedsFayda()
    {
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await FunctionWithBuyerFayda(null).FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_041", response.Body);
    }

    [Fact]
    public async Task Initiate_LargeAmount_StaleFaydaLogin_Returns403FaydaStepUp()
    {
        var stale = DateTime.UtcNow.AddMinutes(-30).ToString("o");

        var response = await FunctionWithBuyerFayda(stale).FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_041", response.Body);
    }

    [Fact]
    public async Task Initiate_LargeAmount_RecentFaydaLogin_PassesStepUpWithoutPassword()
    {
        var recent = DateTime.UtcNow.AddMinutes(-1).ToString("o");

        // No auth_time at all: above 10,000 ETB the Fayda login replaces the password.
        var response = await FunctionWithBuyerFayda(recent).FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId), _context);

        Assert.DoesNotContain("AUTH_040", response.Body);
        Assert.DoesNotContain("AUTH_041", response.Body);
    }

    [Fact]
    public async Task Initiate_LargeAmount_ProfileReadFails_Returns503AndHoldsNothing()
    {
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync(BuyerId).ThrowsAsync(new InvalidOperationException("dynamo down +251911111111"));
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowFunction(service, userRepo: userRepo);

        var response = await function.FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId), _context);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("ESCROW_012", response.Body);
        Assert.DoesNotContain("+251911111111", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task Initiate_SmallAmount_WithoutFreshPassword_Returns403StepUp()
    {
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", InitiateBody(5_000m), BuyerId),
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_040", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task Initiate_SmallAmount_FreshPassword_Succeeds()
    {
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", InitiateBody(5_000m), BuyerId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public async Task Dispute_LargeEscrow_WithoutFreshAuth_Returns403StepUp()
    {
        SetupEscrow("escrow-big", 15_000m);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", DisputeBody("escrow-big"), BuyerId), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_040", response.Body);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "disputed", Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Dispute_LargeEscrow_FreshAuth_Succeeds()
    {
        SetupEscrow("escrow-big", 15_000m);
        var request = WithAuthTime(
            MakePostRequest("/escrow/dispute", DisputeBody("escrow-big"), BuyerId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Dispute_SmallEscrow_NeedsNoStepUp()
    {
        SetupEscrow("escrow-small", 5_000m);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", DisputeBody("escrow-small"), BuyerId), _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Dispute_LostToAnotherDispute_Returns409Escrow020_Not500()
    {
        // ESC-07: the other party's dispute (POST /escrow/dispute or a delivery dispute) froze the
        // escrow between the read and the write. Before, the failed condition was a 500.
        SetupEscrow("escrow-small", 5_000m);
        _mockEscrowRepo.GetByIdAsync("escrow-small", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-small" }, { "status", "disputed" }, { "createdAt", "2026-02-17T10:00:00Z" }
        });
        _mockEscrowRepo.UpdateStatusAsync("escrow-small", Arg.Any<string>(), "disputed", "escrowed",
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", DisputeBody("escrow-small"), BuyerId), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("ESCROW_020", response.Body);
    }

    [Fact]
    public async Task Dispute_NonParty_GetsServiceErrorNotStepUp()
    {
        SetupEscrow("escrow-big", 15_000m);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/dispute", DisputeBody("escrow-big"), "stranger-1"), _context);

        Assert.DoesNotContain("AUTH_040", response.Body);
        Assert.NotEqual(200, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Settlement routes: admin pool group, not marketplace roles
    // ──────────────────────────────────────────────

    private static APIGatewayProxyRequest AdminPoolRequest(string path, string body)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-pool-user" },
            { "cognito:groups", "admin" }
        };
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = path,
            Body = body,
            RequestContext = new ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static string ReleaseBody(string escrowId) => JsonSerializer.Serialize(new ReleaseEscrowRequest
    {
        EscrowId = escrowId,
        Trigger = "admin_override",
        DeliveryId = "del-001",
        IdempotencyKey = $"admin:{escrowId}:release:1708171200111"
    }, AppJsonContext.Default.ReleaseEscrowRequest);

    [Fact]
    public async Task Release_AdminPoolToken_IsAllowed()
    {
        SetupEscrow("escrow-adm", 5_000m);

        var response = await _function.FunctionHandler(
            AdminPoolRequest("/escrow/release", ReleaseBody("escrow-adm")), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task Release_MarketplaceBuyer_IsRefused()
    {
        SetupEscrow("escrow-adm", 5_000m);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/release", ReleaseBody("escrow-adm"), BuyerId, "buyer"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ESCROW_015", response.Body);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task Release_SelfClaimedAdminRoleWithoutGroup_IsRefused()
    {
        SetupEscrow("escrow-adm", 5_000m);

        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/release", ReleaseBody("escrow-adm"), "someone", "admin"), _context);

        Assert.NotEqual(200, response.StatusCode);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // ESC-01: POST /escrow/order-checkout
    // ──────────────────────────────────────────────

    [Fact]
    public async Task OrderCheckout_Buyer_RoutesToTheService()
    {
        // The test service has no order-checkout wiring, so reaching it answers 503 ESCROW_012.
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/order-checkout", """{"orderId":"order-1"}""", BuyerId), _context);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("ESCROW_012", response.Body);
    }

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    [InlineData(null)]
    public async Task OrderCheckout_NonBuyer_Returns403Escrow025(string? role)
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/order-checkout", """{"orderId":"order-1"}""", BuyerId, role), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ESCROW_025", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"orderId":""}""")]
    [InlineData("{{bad json")]
    public async Task OrderCheckout_WithoutAnOrderId_Returns400Escrow033(string body)
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/order-checkout", body, BuyerId), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_033", response.Body);
    }

    [Fact]
    public async Task OrderCheckout_Unauthenticated_Returns401()
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/order-checkout", """{"orderId":"order-1"}""", userId: null), _context);

        Assert.Equal(401, response.StatusCode);
    }

    private readonly IOrderRepository _checkoutOrderRepo = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkoutRepo = Substitute.For<IOrderCheckoutRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();

    /// <summary>An EscrowFunction whose service can open live order checkouts, with a fraud service.</summary>
    private EscrowFunction CheckoutFunction(string orderStatus = "payment_submitted")
    {
        _checkoutOrderRepo.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "orderTotal", "1500" }, { "status", orderStatus }, { "version", "2" }
        });
        _checkoutRepo.TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>()).Returns(true);
        _checkoutRepo.StoreMappingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(true);
        _mockTelebirrGateway.SupportsHostedCheckout.Returns(true);
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>()).Returns(new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = "PENDING-WEBHOOK-1",
            ToPayUrl = "https://telebirr.example/checkout?prepay_id=prepay-1",
            PrepayId = "prepay-1"
        });
        _fraud.CheckPaymentVelocityAsync(BuyerId).Returns(new FraudCheckResult { IsAllowed = true });

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway,
            orderRepo: _checkoutOrderRepo, orderCheckoutRepo: _checkoutRepo);
        return new EscrowFunction(service, _fraud);
    }

    private static APIGatewayProxyRequest CheckoutRequest()
        => MakePostRequest("/escrow/order-checkout", """{"orderId":"order-1"}""", BuyerId);

    [Fact]
    public async Task OrderCheckout_NewCheckout_CountsTowardThePaymentVelocity()
    {
        var sut = CheckoutFunction();

        var response = await sut.FunctionHandler(CheckoutRequest(), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("prepay_id=prepay-1", response.Body);
        await _fraud.Received(1).RecordPaymentInitiatedAsync(BuyerId);
    }

    [Fact]
    public async Task OrderCheckout_ReturnedOpenCheckout_DoesNotCountAgain()
    {
        var sut = CheckoutFunction();
        _checkoutRepo.GetSlotAsync("order-1").Returns(new OrderCheckoutSlot
        {
            OrderId = "order-1",
            BuyerId = BuyerId,
            Attempt = 1,
            MerchOrderId = EscrowService.BuildOrderMerchOrderId("order-1", 1),
            Amount = 1500m,
            Outcome = OrderCheckoutOutcome.UrlIssued,
            OpenedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            OpenUntilUtc = DateTime.UtcNow.AddMinutes(100),
            PrepayId = "prepay-1"
        });
        _mockTelebirrGateway.BuildCheckoutUrl("prepay-1").Returns("https://telebirr.example/checkout?prepay_id=prepay-1");

        var response = await sut.FunctionHandler(CheckoutRequest(), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _fraud.DidNotReceive().RecordPaymentInitiatedAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task OrderCheckout_TelebirrFailure_StillCountsTowardThePaymentVelocity()
    {
        var sut = CheckoutFunction();
        _mockTelebirrGateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>())
            .Returns(new HoldFundsResponse { Success = false, ErrorCode = "TELEBIRR_TIMEOUT" });

        var response = await sut.FunctionHandler(CheckoutRequest(), _context);

        Assert.Equal(502, response.StatusCode);
        Assert.Contains("ESCROW_013", response.Body);
        await _fraud.Received(1).RecordPaymentInitiatedAsync(BuyerId);
    }

    [Fact]
    public async Task OrderCheckout_VelocityLimitReached_Returns429WithoutCallingTelebirr()
    {
        var sut = CheckoutFunction();
        _fraud.CheckPaymentVelocityAsync(BuyerId)
            .Returns(new FraudCheckResult { IsAllowed = false, Message = "Too many payments." });

        var response = await sut.FunctionHandler(CheckoutRequest(), _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("ESCROW_011", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
        await _checkoutRepo.DidNotReceive().TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task OrderCheckout_RefusedBeforeTelebirr_DoesNotCount()
    {
        var sut = CheckoutFunction(orderStatus: "funded");

        var response = await sut.FunctionHandler(CheckoutRequest(), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("ESCROW_024", response.Body);
        await _fraud.DidNotReceive().RecordPaymentInitiatedAsync(Arg.Any<string>());
    }

    // ──────────────────────────────────────────────
    // ESC-13: money alarm lines from the admin settlement routes
    // ──────────────────────────────────────────────

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    [Fact]
    public async Task HandleRelease_ExceptionBeforeTheLock_LogsSettlementFailedFailed()
    {
        _mockEscrowRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("Table not found: arn:aws:dynamodb:secret-info"));
        var body = JsonSerializer.Serialize(new ReleaseEscrowRequest
        {
            EscrowId = "escrow-001",
            Trigger = "delivery_confirmed",
            DeliveryId = "del-001",
            IdempotencyKey = "system:escrow-001:release:1708171200000"
        }, AppJsonContext.Default.ReleaseEscrowRequest);

        var response = await _function.FunctionHandler(MakePostRequest("/escrow/release", body, "system"), _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=release_exception outcome=failed code=Exception", Logs);
        Assert.DoesNotContain("secret-info", Logs);
    }

    [Fact]
    public async Task HandleRefund_ExceptionBeforeTheLock_LogsSettlementFailedFailed()
    {
        _mockEscrowRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("Table not found: arn:aws:dynamodb:secret-info"));
        var body = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = "escrow-002",
            Trigger = "delivery_timeout",
            Reason = "No delivery within 7 days.",
            IdempotencyKey = "system:escrow-002:refund:1708171200000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var response = await _function.FunctionHandler(MakePostRequest("/escrow/refund", body, "system"), _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-002 action=refund_exception outcome=failed code=Exception", Logs);
        Assert.DoesNotContain("secret-info", Logs);
    }

    [Fact]
    public async Task HandleRefund_ExceptionAfterTheGatewayPaid_LogsSettlementFailedUnknown()
    {
        // ESC-05: the buyer was refunded but the status write never landed (4 attempts): the
        // exception escaped after the lock, so the outcome is unknown and the lock stays.
        _mockEscrowRepo.GetByIdAsync("escrow-003").Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-003" }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "amount", 5000m }, { "status", "escrowed" }, { "createdAt", "2026-02-17T12:00:00.000Z" }, { "version", 1 }
        });
        _mockEscrowRepo.UpdateStatusAsync("escrow-003", Arg.Any<string>(), "refunded", Arg.Any<string?>(),
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        var body = JsonSerializer.Serialize(new RefundEscrowRequest
        {
            EscrowId = "escrow-003",
            Trigger = "delivery_timeout",
            Reason = "No delivery within 7 days.",
            IdempotencyKey = "system:escrow-003:refund:1708171200000"
        }, AppJsonContext.Default.RefundEscrowRequest);

        var response = await _function.FunctionHandler(MakePostRequest("/escrow/refund", body, "system"), _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-003 action=refund_exception outcome=unknown code=AmazonDynamoDBException", Logs);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockEscrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("deliveryId")]
    public async Task HostilePathParameter_OnAnyUsersGet_CannotFireAMoneyAlarm(string pathParam)
    {
        // Any farmer, trucker or buyer can call these routes; a path that spells a token used
        // to reach the log through the invocation line and the error line.
        var hostile = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.Hostile;
        _mockEscrowRepo.GetByIdAsync(Arg.Any<string>()).ThrowsAsync(new Exception(hostile));
        _mockEscrowRepo.GetByDeliveryIdAsync(Arg.Any<string>()).ThrowsAsync(new Exception(hostile));
        var request = MakeGetRequest($"/escrow/{hostile}", "buyer-001",
            pathParams: new Dictionary<string, string> { { pathParam, hostile } }, role: "buyer");
        request.Resource = pathParam == "id" ? "/escrow/{id}" : "/escrow/by-delivery/{deliveryId}";
        request.Headers = new Dictionary<string, string> { { "X-Correlation-Id", "ESCROW_SETTLEMENT_FAILED" } };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains($"EscrowFunction invoked: GET {request.Resource}", Logs);
        Assert.DoesNotContain(WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens, Logs.Contains);
    }

    [Fact]
    public async Task ResourceThatIsTheRawPath_CannotFireAMoneyAlarm()
    {
        // AWS does not promise that a payload 1.0 resource is the route template; if it ever
        // carries the client path, the invocation line must still match no money filter.
        _mockEscrowRepo.GetByIdAsync(Arg.Any<string>()).Returns((Dictionary<string, object>?)null);
        var request = MakeGetRequest("/escrow/ESCROW_SETTLEMENT_FAILED", "buyer-001",
            pathParams: new Dictionary<string, string> { { "id", "ESCROW_SETTLEMENT_FAILED" } }, role: "buyer");
        request.Resource = "/escrow/ESCROW_SETTLEMENT_FAILED";

        await _function.FunctionHandler(request, _context);

        Assert.Contains("EscrowFunction invoked: GET /escrow/ESCROW-SETTLEMENT-FAILED", Logs);
        Assert.DoesNotContain(WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens, Logs.Contains);
    }
}
