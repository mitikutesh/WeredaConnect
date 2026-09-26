using System.Text.Json;
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
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
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

        var request = MakePostRequest("/escrow/initiate", body, BuyerId);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
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

        var request = MakePostRequest("/escrow/initiate", body, BuyerId);
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
    // Step-up (AUTH_040) on large initiate / dispute
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

    [Fact]
    public async Task Initiate_LargeAmount_WithoutFreshAuth_Returns403StepUp()
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_040", response.Body);
        await _mockTelebirrGateway.DidNotReceive().HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    [Fact]
    public async Task Initiate_LargeAmount_StaleAuth_Returns403StepUp()
    {
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId),
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_040", response.Body);
    }

    [Fact]
    public async Task Initiate_LargeAmount_FreshAuth_PassesStepUp()
    {
        var request = WithAuthTime(
            MakePostRequest("/escrow/initiate", InitiateBody(15_000m), BuyerId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var response = await _function.FunctionHandler(request, _context);

        Assert.DoesNotContain("AUTH_040", response.Body);
    }

    [Fact]
    public async Task Initiate_SmallAmount_NeedsNoStepUp()
    {
        var response = await _function.FunctionHandler(
            MakePostRequest("/escrow/initiate", InitiateBody(5_000m), BuyerId), _context);

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
}
