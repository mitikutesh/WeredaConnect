using System.Text.Json;
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
        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        _function = new EscrowFunction(service);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakePostRequest(
        string path,
        string? body = null,
        string? userId = null)
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = path,
            Body = body
        };

        if (userId != null)
        {
            request.RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } }
            };
        }

        return request;
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path,
        string? userId = null,
        Dictionary<string, string>? pathParams = null,
        Dictionary<string, string>? queryParams = null)
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
            request.RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } }
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
}
