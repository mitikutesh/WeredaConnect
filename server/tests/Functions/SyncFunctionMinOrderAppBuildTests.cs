using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-25 (sync-protocol.md 1.41.0, spec K1): every <c>order</c> operation, create and update, from an app
/// below <c>MIN_ORDER_APP_BUILD</c> is refused before any read or write: <c>APP_UPDATE_REQUIRED</c> to an
/// app that sends <c>X-App-Build</c>, <c>ORDER_VALIDATION</c> with the same text to one that sends none.
/// Other entities are never gated, the rest of the batch goes on, and the gate does not depend on
/// <c>COMMITMENTS_ENABLED</c>.
/// </summary>
public class SyncFunctionMinOrderAppBuildTests
{
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const int Minimum = 3;

    private const string TooOld =
        "APP_UPDATE_REQUIRED: This version of the app is too old for orders. Update WeredaConnect to continue.";
    private const string NoBuild =
        "ORDER_VALIDATION: This version of the app is too old for orders. Update WeredaConnect to continue.";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionMinOrderAppBuildTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        foreach (var id in new[] { FarmerId, TruckerId, BuyerId })
        {
            _users.GetUserByIdAsync(id).Returns(Profile(id));
            _users.GetUserByIdAsync(id, true).Returns(Profile(id));
        }

        _fraud.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
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

    private SyncFunction Function(int minimum = Minimum, bool commitments = true) => new(
        _meta, _crops, _ddb, "trips", "deliveries", fraudService: _fraud, userRepo: _users, orderRepo: _orders)
    {
        MinOrderAppBuild = minimum,
        CommitmentsEnabled = commitments
    };

    private static UserDto Profile(string id) => new()
    {
        UserId = id,
        PhoneNumber = "+251933333333",
        DisplayName = "Abebe Kebede",
        Role = id.Split('-')[0],
        Woreda = "Jimma",
        IsPhoneVerified = true,
        FaydaVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    /// <summary>The buyer's payment of a created order: succeeds with the mocks above when the gate lets it in.</summary>
    private static SyncOperationRequest Payment() => new()
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
    };

    private static SyncOperationRequest OrderCreate() => new()
    {
        OperationId = "op-create",
        IdempotencyKey = "buyer-001:order:order-new:create:12345",
        EntityType = "order",
        EntityId = "order-new",
        Action = "create",
        Data = new Dictionary<string, object>
        {
            { "listingId", "crop-1" }, { "quantity", 100 }, { "paymentMethod", "telebirr" }
        }
    };

    private static SyncOperationRequest Op(string entityType, string action, string id) => new()
    {
        OperationId = $"op-{entityType}",
        IdempotencyKey = $"buyer-001:{entityType}:{id}:{action}:12345",
        EntityType = entityType,
        EntityId = id,
        Action = action,
        Data = new Dictionary<string, object> { { "version", 1 } }
    };

    /// <summary>A batch from the buyer; <paramref name="build"/> null sends no <c>X-App-Build</c>.</summary>
    private static APIGatewayProxyRequest Batch(string? build, params SyncOperationRequest[] operations) => new()
    {
        Body = JsonSerializer.Serialize(new SyncRequest
        {
            SyncBatchId = "batch-1",
            ClientId = "device-1",
            Operations = operations.ToList()
        }, Json),
        // HTTP API delivers header names lowercased.
        Headers = build == null ? null : new Dictionary<string, string> { { "x-app-build", build } },
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

    private async Task<List<SyncOperationResult>> SyncAsync(SyncFunction function, APIGatewayProxyRequest request)
    {
        var response = await function.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        var body = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.SyncResponse);
        return body!.Results;
    }

    /// <summary>The refusal came before any read or write: no order, listing, idempotency or fraud call.</summary>
    private void NothingReadOrWritten()
    {
        Assert.Empty(_orders.ReceivedCalls());
        Assert.Empty(_crops.ReceivedCalls());
        Assert.Empty(_ddb.ReceivedCalls());
        Assert.Empty(_meta.ReceivedCalls());
        Assert.Empty(_fraud.ReceivedCalls());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task OrderUpdate_FromABuildBelowTheMinimum_IsAppUpdateRequired_NothingReadOrWritten(string build)
    {
        var results = await SyncAsync(Function(), Batch(build, Payment()));

        var result = Assert.Single(results);
        Assert.Equal("op-pay", result.OperationId);
        Assert.Equal("error", result.Status);
        Assert.Equal(TooOld, result.Message);
        NothingReadOrWritten();
    }

    [Fact]
    public async Task OrderCreate_FromABuildBelowTheMinimum_IsAppUpdateRequired_NothingReadOrWritten()
    {
        var results = await SyncAsync(Function(), Batch("2", OrderCreate()));

        var result = Assert.Single(results);
        Assert.Equal("error", result.Status);
        Assert.Equal(TooOld, result.Message);
        NothingReadOrWritten();
    }

    [Fact]
    public async Task OrderDelete_FromABuildBelowTheMinimum_IsAppUpdateRequired()
    {
        var results = await SyncAsync(Function(), Batch("2", Op("order", "delete", "order-pay")));

        Assert.Equal(TooOld, Assert.Single(results).Message);
        NothingReadOrWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("3.0")]
    public async Task OrderOperations_WithNoReadableBuild_AreOrderValidation_WithTheSameText(string? build)
    {
        var results = await SyncAsync(Function(), Batch(build, Payment(), OrderCreate()));

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal("error", r.Status);
            Assert.Equal(NoBuild, r.Message);
        });
        NothingReadOrWritten();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData(" 17 ")]
    public async Task OrderUpdate_AtOrAboveTheMinimum_GoesThrough(string build)
    {
        var results = await SyncAsync(Function(), Batch(build, Payment()));

        var result = Assert.Single(results);
        Assert.Equal("success", result.Status);
        await _orders.Received().UpdateAsync("order-pay", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task TheHeaderName_IsReadCaseInsensitively()
    {
        var request = Batch(null, Payment());
        request.Headers = new Dictionary<string, string> { { "X-App-Build", "2" } };

        var results = await SyncAsync(Function(), request);

        Assert.Equal(TooOld, Assert.Single(results).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public async Task GateOff_MinimumZero_LetsEveryBuildThrough(string? build)
    {
        var results = await SyncAsync(Function(minimum: 0), Batch(build, Payment()));

        Assert.Equal("success", Assert.Single(results).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheGate_DoesNotDependOnTheCommitmentsFlag(bool commitments)
    {
        var refused = await SyncAsync(Function(commitments: commitments), Batch("2", Payment()));
        Assert.Equal(TooOld, Assert.Single(refused).Message);

        var passed = await SyncAsync(Function(commitments: commitments), Batch("3", Payment()));
        Assert.Equal("success", Assert.Single(passed).Status);
    }

    [Theory]
    [InlineData("crop_listing", "create")]
    [InlineData("trip", "update")]
    [InlineData("delivery", "update")]
    [InlineData("rating", "create")]
    [InlineData("message", "create")]
    [InlineData("buyer_intent", "create")]
    [InlineData("match_event", "create")]
    [InlineData("location_ping", "create")]
    [InlineData("delivery_photo", "create")]
    [InlineData("user", "update")]
    public async Task OtherEntities_FromAnOldBuildOrNoBuild_AreNotGated(string entityType, string action)
    {
        foreach (var build in new[] { null, "1" })
        {
            _meta.ClearReceivedCalls();

            var results = await SyncAsync(Function(), Batch(build, Op(entityType, action, $"{entityType}-1")));

            var result = Assert.Single(results);
            Assert.DoesNotContain("too old for orders", result.Message ?? "");
            // The operation reached the idempotency read, which the gate skips for an order.
            await _meta.Received(1).IdempotencyKeyExistsAsync(Arg.Any<string>());
        }
    }

    [Fact]
    public async Task ARefusedOrder_LeavesTheRestOfTheBatchGoingOn()
    {
        var results = await SyncAsync(
            Function(), Batch("2", OrderCreate(), Op("rating", "create", "order-pay:farmer-001"), Payment()));

        Assert.Equal(3, results.Count);
        Assert.Equal(TooOld, results[0].Message);
        // The rating reached its handler (no ratings table in this test).
        Assert.StartsWith("RATING_UNAVAILABLE:", results[1].Message);
        Assert.Equal(TooOld, results[2].Message);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _orders.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
    }
}
