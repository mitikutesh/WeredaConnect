using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-44: <c>POST /escrow/dispute</c> reads the escrow (and, for an admin caller, the caller's
/// profile) to decide the step-up before the dispute runs. A failed read answers a typed
/// <c>503 ESCROW_012</c> with nothing written, instead of escaping as an unhandled Lambda error
/// (an API Gateway 502 without a code).
/// </summary>
public sealed class EscrowFunctionDisputeStepUpReadTests
{
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string EscrowId = "escrow-read-fail";
    private const string SecretText = "throttled for +251911111111";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly TestLambdaContext _context = new();

    public EscrowFunctionDisputeStepUpReadTests()
    {
        _escrowRepo
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _idempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
    }

    [Theory]
    [InlineData(BuyerId, "buyer")]
    [InlineData(FarmerId, "farmer")]
    [InlineData("trucker-789", "trucker")]
    public async Task Dispute_EscrowReadThrows_Returns503Escrow012_AndWritesNothing(string userId, string role)
    {
        _escrowRepo.GetByIdAsync(EscrowId).ThrowsAsync(new AmazonDynamoDBException(SecretText));

        var response = await Function().FunctionHandler(DisputeRequest(userId, role), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ESCROW_012");
        response.Body.Should().NotContain("+251911111111");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Dispute_AdminProfileReadThrows_Returns503Escrow012_AndWritesNothing()
    {
        // The escrow reads fine; the party check then reads the admin caller's profile, which is
        // part of the same step-up read and fails closed the same way.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(EscrowRow(5_000m));
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("admin-1").ThrowsAsync(new AmazonDynamoDBException(SecretText));

        var response = await Function(userRepo).FunctionHandler(DisputeRequest("admin-1", "admin"), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ESCROW_012");
        response.Body.Should().NotContain("+251911111111");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Dispute_EscrowReadFailsOnce_RetrySucceeds()
    {
        // Nothing the failed attempt did (no idempotency record, no status write) blocks the retry
        // with the same idempotency key.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(
            Task.FromException<Dictionary<string, object>?>(new AmazonDynamoDBException(SecretText)),
            Task.FromResult<Dictionary<string, object>?>(EscrowRow(5_000m)));
        var function = Function();

        var first = await function.FunctionHandler(DisputeRequest(BuyerId, "buyer"), _context);
        var retry = await function.FunctionHandler(DisputeRequest(BuyerId, "buyer"), _context);

        first.StatusCode.Should().Be(503);
        retry.StatusCode.Should().Be(200);
        retry.Body.Should().Contain("disputed");
    }

    private async Task AssertNothingWrittenAsync()
    {
        // The escrow repository saw the single step-up read and nothing else: no status write,
        // no lock. No escrow-audit row, no idempotency read or write, no gateway call.
        _escrowRepo.ReceivedCalls().Should().ContainSingle();
        await _escrowRepo.Received(1).GetByIdAsync(EscrowId);
        _auditRepo.ReceivedCalls().Should().BeEmpty();
        _idempotencyRepo.ReceivedCalls().Should().BeEmpty();
        _gateway.ReceivedCalls().Should().BeEmpty();
    }

    private EscrowFunction Function(IUserRepository? userRepo = null)
        => new(new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, userRepo));

    private static APIGatewayProxyRequest DisputeRequest(string userId, string role) => new()
    {
        HttpMethod = "POST",
        Path = "/escrow/dispute",
        Body = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = EscrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:{EscrowId}:dispute:1708171200999"
        }, AppJsonContext.Default.DisputeEscrowRequest),
        RequestContext = new ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId }, { "custom:role", role } }
        }
    };

    private static Dictionary<string, object> EscrowRow(decimal amount) => new()
    {
        { "escrowId", EscrowId },
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
    };
}
