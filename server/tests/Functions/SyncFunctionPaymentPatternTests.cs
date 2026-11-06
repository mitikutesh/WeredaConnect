using System.Globalization;
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

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-09 (fraud-detection-architecture.md 15.1): the buyer payment rules on <c>/sync</c> order
/// <c>payment_submitted</c>. They run only after the gates let the payment through and the write succeeded,
/// write their signals on the paying buyer with the order as the entity, and never change the answer: a
/// failed history read or signal write is logged and the payment goes on.
/// </summary>
public class SyncFunctionPaymentPatternTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string OrderId = "order-pay";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionPaymentPatternTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, "2025-01-01T00:00:00Z"));
        _users.GetUserByIdAsync(FarmerId).Returns(Profile(FarmerId, "2025-01-01T00:00:00Z"));
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", OrderId }, { "status", "payment_submitted" }, { "version", "2" }
            }));
        OrderOf(9_800m);
        History(Prior("o-1", 9_700m, 3), Prior("o-2", 9_900m, 12));
    }

    // The buyer has no Fayda ID (none is needed up to 10,000 ETB): the structuring rule applies to them.
    private static UserDto Profile(string id, string createdAt, bool faydaVerified = false) => new()
    {
        UserId = id,
        PhoneNumber = "+251933333333",
        Role = id == BuyerId ? "buyer" : "farmer",
        Woreda = "Jimma",
        IsPhoneVerified = true,
        FaydaVerified = faydaVerified || id != BuyerId,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Version = 1
    };

    private void OrderOf(decimal total) => _orders.GetByIdAsync(OrderId, true).Returns(new Dictionary<string, object>
    {
        { "orderId", OrderId }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", "created" },
        { "orderTotal", total }, { "version", "1" }
    });

    private static Dictionary<string, object> Prior(string id, decimal total, int daysAgo) => new()
    {
        { "orderId", id },
        { "createdAt", DateTime.UtcNow.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture) },
        { "orderTotal", total.ToString(CultureInfo.InvariantCulture) },
        { "paymentSubmittedAt", DateTime.UtcNow.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture) },
        { "paymentVerifiedAt", DateTime.UtcNow.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture) }
    };

    private void History(params Dictionary<string, object>[] rows) =>
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null)
            .Returns((rows.ToList(), (Dictionary<string, AttributeValue>?)null));

    private SyncFunction Function(bool withRules = true) => new(
        _meta, _crops, _ddb, "trips", "deliveries", fraudService: _fraud, userRepo: _users, orderRepo: _orders)
    {
        PatternRules = withRules ? new TransactionPatternService(_orders, null, TransactionPatternSettings.Default) : null
    };

    private async Task<string> PayAsync(SyncFunction function, long? authTime = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "userId", BuyerId }, { "custom:role", "buyer" },
            { "auth_time", (authTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture) }
        };
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-1",
                ClientId = "device-1",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-pay",
                        IdempotencyKey = $"{BuyerId}:order:{OrderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = OrderId,
                        Action = "update",
                        Data = new Dictionary<string, object>
                        {
                            { "status", "payment_submitted" }, { "paymentMethod", "telebirr" }, { "version", 1 }
                        }
                    }
                }
            }, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };

        var response = await function.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    private Task NoSignal() =>
        _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);

    [Fact]
    public async Task ThirdPaymentJustUnderTheFaydaThreshold_RecordsStructuringOnTheBuyer()
    {
        var body = await PayAsync(Function());

        Assert.Contains("\"status\":\"success\"", body);
        await _fraud.Received(1).RecordFraudSignalAsync(BuyerId, "KYC_THRESHOLD_STRUCTURING", OrderId, "order",
            Arg.Is<string>(d => d.Contains("9,800 ETB") && d.Contains("3 payments")));
    }

    [Fact]
    public async Task OrdinaryPayment_RecordsNothing()
    {
        OrderOf(2_000m);

        var body = await PayAsync(Function());

        Assert.Contains("\"status\":\"success\"", body);
        await NoSignal();
    }

    [Fact]
    public async Task FaydaVerifiedBuyer_InTheBand_RecordsNoStructuring()
    {
        // Verified already: there is no Fayda gate under 10,000 ETB to stay away from.
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, "2025-01-01T00:00:00Z", faydaVerified: true));

        var body = await PayAsync(Function());

        Assert.Contains("\"status\":\"success\"", body);
        await NoSignal();
    }

    [Fact]
    public async Task NewAccountStructuring_IsOneSignal_NotTwo()
    {
        // A 2 day old account's third payment in the band: structuring only, the same payment is not also
        // written as a new account with a large amount.
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, DateTime.UtcNow.AddDays(-2).ToString("o", CultureInfo.InvariantCulture)));

        await PayAsync(Function());

        await _fraud.Received(1).RecordFraudSignalAsync(BuyerId, "KYC_THRESHOLD_STRUCTURING", OrderId, "order", Arg.Any<string?>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(BuyerId, "NEW_ACCOUNT_LARGE_AMOUNT", Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task NewAccountPayingALot_RecordsTheNewAccountSignal()
    {
        OrderOf(6_000m);
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, DateTime.UtcNow.AddDays(-2).ToString("o", CultureInfo.InvariantCulture)));

        await PayAsync(Function());

        await _fraud.Received(1).RecordFraudSignalAsync(BuyerId, "NEW_ACCOUNT_LARGE_AMOUNT", OrderId, "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task HistoryReadFails_PaymentStillGoesThrough_AndTheSkipIsLogged()
    {
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null).ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var body = await PayAsync(Function());

        Assert.Contains("\"status\":\"success\"", body);
        await NoSignal();
        var logs = ((TestLambdaLogger)_context.Logger).Buffer.ToString();
        Assert.Contains("Pattern check payment skipped for order", logs);
        Assert.Contains("[ProvisionedThroughputExceededException]", logs);
    }

    [Fact]
    public async Task SignalWriteFails_PaymentStillGoesThrough()
    {
        _fraud.RecordFraudSignalAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("put failed")));

        var body = await PayAsync(Function());

        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task RefusedPayment_RunsNoPatternCheck()
    {
        // A stale password at 9,800 ETB is AUTH_040: nothing was paid, so nothing is observed.
        var body = await PayAsync(Function(), authTime: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());

        Assert.Contains("AUTH_040", body);
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
        await NoSignal();
    }

    [Fact]
    public async Task WithoutRulesWired_NoHistoryIsRead()
    {
        var body = await PayAsync(Function(withRules: false));

        Assert.Contains("\"status\":\"success\"", body);
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
        await NoSignal();
    }
}
