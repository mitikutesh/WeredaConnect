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

/// <summary>ACC-02: <c>POST /escrow/*</c> answers <c>410 ACCOUNT_004</c> to an account that is being or has been deleted.</summary>
public sealed class EscrowFunctionAccountDeletionTests
{
    private const string UserId = "buyer-1";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly TestLambdaContext _context = new();

    private EscrowFunction Function()
        => new(new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway), userRepo: _users);

    private void CallerIs(string? deletionState) => _users.GetUserByIdAsync(UserId, true).Returns(new UserDto
    {
        UserId = UserId,
        PhoneNumber = "+251911000003",
        Role = "buyer",
        Woreda = "Adama",
        IsPhoneVerified = true,
        DeletionState = deletionState,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    });

    private static APIGatewayProxyRequest Request(string method, string path) => new()
    {
        HttpMethod = method,
        Path = path,
        Body = "{}",
        RequestContext = new ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", UserId }, { "custom:role", "buyer" } }
        }
    };

    [Theory]
    [InlineData("/escrow/initiate", "in_progress")]
    [InlineData("/escrow/order-checkout", "cognito_pending")]
    [InlineData("/escrow/dispute", "completed")]
    public async Task MoneyAndDisputeWrites_ByADeletingAccount_Return410_AndTouchNothing(string path, string state)
    {
        CallerIs(state);

        var response = await Function().FunctionHandler(Request("POST", path), _context);

        response.StatusCode.Should().Be(410);
        using var body = JsonDocument.Parse(response.Body);
        body.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("ACCOUNT_004");
        body.RootElement.GetProperty("error").GetProperty("deletionState").GetString().Should().Be(state);
        _escrowRepo.ReceivedCalls().Should().BeEmpty();
        _gateway.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TheAccountCannotBeRead_Returns503Escrow012()
    {
        _users.GetUserByIdAsync(UserId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var response = await Function().FunctionHandler(Request("POST", "/escrow/initiate"), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ESCROW_012");
        _escrowRepo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ALiveAccount_IsNotRefused()
    {
        CallerIs(null);

        var response = await Function().FunctionHandler(Request("POST", "/escrow/initiate"), _context);

        response.StatusCode.Should().NotBe(410);
    }

    [Fact]
    public async Task Reads_StillWork_ForADeletingAccount()
    {
        CallerIs("in_progress");

        var response = await Function().FunctionHandler(Request("GET", "/escrow/history"), _context);

        response.StatusCode.Should().NotBe(410);
        await _users.DidNotReceive().GetUserByIdAsync(UserId, true);
    }
}
