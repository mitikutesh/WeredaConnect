using System.Text.Json;
using Amazon.DynamoDBv2;
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
/// ACC-02 (account-deletion.md, counterparty money entry points): the buyer's <c>payment_submitted</c>
/// is refused with <c>ORDER_VALIDATION</c> while the order's farmer or trucker is being or has been
/// deleted, after FRD-04's CRITICAL check and before KYC, read strongly consistent; nothing is written.
/// </summary>
public class SyncFunctionDeletedPartyTests
{
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string DeletedParty = "ORDER_VALIDATION: A party to this order has deleted their account.";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionDeletedPartyTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        foreach (var id in new[] { FarmerId, TruckerId, BuyerId })
        {
            _users.GetUserByIdAsync(id).Returns(Profile(id));
            _users.GetUserByIdAsync(id, true).Returns(Profile(id));
        }

        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        _orders.GetByIdAsync("order-pay", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-pay" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId },
            { "status", "created" }, { "orderTotal", 2_000m }, { "paymentMethod", "cbe_transfer" }, { "version", "1" }
        });
        _orders.UpdateAsync("order-pay", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-pay" }, { "status", "payment_submitted" }, { "version", "2" }
            }));
    }

    private SyncFunction Function() => new(
        _meta, _crops, _ddb, "trips", "deliveries", fraudService: _fraud, userRepo: _users, orderRepo: _orders);

    private static UserDto Profile(string id, string? deletionState = null) => new()
    {
        UserId = id,
        PhoneNumber = deletionState is null or "in_progress" ? "+251933333333" : "",
        DisplayName = "Abebe Kebede",
        Role = id.Split('-')[0],
        Woreda = deletionState is null or "in_progress" ? "Jimma" : "",
        IsPhoneVerified = deletionState is null or "in_progress",
        FaydaVerified = deletionState is null or "in_progress",
        DeletionState = deletionState,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    private static APIGatewayProxyRequest Payment() => new()
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
                    IdempotencyKey = "buyer-001:order:order-pay:update:12345",
                    EntityType = "order",
                    EntityId = "order-pay",
                    Action = "update",
                    Data = new Dictionary<string, object>
                    {
                        { "status", "payment_submitted" }, { "paymentMethod", "telebirr" }, { "version", 1 }
                    }
                }
            }
        }, Json),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                { "userId", BuyerId },
                { "custom:role", "buyer" },
                { "auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
            }
        }
    };

    private async Task<string> PayAsync()
    {
        var response = await Function().FunctionHandler(Payment(), _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    private async Task NothingWrittenAsync()
    {
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _meta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(FarmerId, "in_progress")]
    [InlineData(FarmerId, "cognito_pending")]
    [InlineData(FarmerId, "completed")]
    [InlineData(TruckerId, "in_progress")]
    [InlineData(TruckerId, "completed")]
    public async Task Payment_ForAnOrderOfADeletingFarmerOrTrucker_IsRefused_NothingWritten(string partyId, string state)
    {
        _users.GetUserByIdAsync(partyId, true).Returns(Profile(partyId, state));

        var body = await PayAsync();

        Assert.Contains(DeletedParty, body);
        // Before KYC: a deleted farmer is not reported as an unverified phone.
        Assert.DoesNotContain("ORDER_KYC_REQUIRED", body);
        await _users.DidNotReceive().GetUserByIdAsync(FarmerId);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task Payment_ByADeletingBuyer_IsRefusedForTheWholeBatch()
    {
        _users.GetUserByIdAsync(BuyerId, true).Returns(Profile(BuyerId, "in_progress"));

        var response = await Function().FunctionHandler(Payment(), _context);

        Assert.Equal(410, response.StatusCode);
        Assert.Contains("ACCOUNT_004", response.Body);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task Payment_WithLiveParties_ReadsEachStronglyConsistent_AndIsSubmitted()
    {
        var body = await PayAsync();

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).GetUserByIdAsync(FarmerId, true);
        await _users.Received(1).GetUserByIdAsync(TruckerId, true);
    }

    [Fact]
    public async Task Payment_CriticalBuyer_StaysFraudAccountHeld_TheRiskCheckComesFirst()
    {
        _fraud.GetUserRiskScoreAsync(BuyerId).Returns(new RiskScoreResult { Score = 100, EnforcementScore = 100, Tier = RiskTier.Critical });
        _users.GetUserByIdAsync(FarmerId, true).Returns(Profile(FarmerId, "completed"));

        var body = await PayAsync();

        Assert.Contains("FRAUD_ACCOUNT_HELD", body);
        await _users.DidNotReceive().GetUserByIdAsync(FarmerId, true);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task Payment_PartyReadFails_IsATransientError_NothingWritten()
    {
        _users.GetUserByIdAsync(TruckerId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var body = await PayAsync();

        Assert.Contains("Internal server error.", body);
        Assert.DoesNotContain("\"status\":\"success\"", body);
        await NothingWrittenAsync();
    }
}
