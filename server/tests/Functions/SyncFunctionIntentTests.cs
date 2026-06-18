using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>Tests for the buyer_intent entity handler in SyncFunction.</summary>
public class SyncFunctionIntentTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IIntentRepository _intents = Substitute.For<IIntentRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionIntentTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _intents.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "intentId", "intent-1" }, { "version", "1" } }));
        _intents.DeleteAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));
        _fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries", intentRepo: _intents);
    }

    private static APIGatewayProxyRequest Req(SyncOperationRequest op, string userId, string? role)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        var body = new SyncRequest
        {
            SyncBatchId = "b1",
            ClientId = "d1",
            Operations = new List<SyncOperationRequest> { op },
        };
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static SyncOperationRequest IntentOp(
        string action = "create",
        string intentId = "intent-1",
        string buyerId = "buyer-1",
        Dictionary<string, object>? data = null)
    {
        return new SyncOperationRequest
        {
            OperationId = $"op-{action}",
            IdempotencyKey = $"{buyerId}:buyer_intent:{intentId}:{action}:1",
            EntityType = "buyer_intent",
            EntityId = intentId,
            Action = action,
            Data = data ?? new Dictionary<string, object>
            {
                { "intentId", intentId },
                { "buyerId", buyerId },
                { "cropType", "wheat" },
                { "quantityNeeded", 10 },
                { "maxPricePerKg", 120 },
                { "deliveryWoreda", "Adama" },
            },
            Timestamp = "2026-06-19T10:00:00Z",
        };
    }

    [Fact]
    public async Task BuyerCreatesIntent_Succeeds()
    {
        var response = await _fn.FunctionHandler(Req(IntentOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _intents.Received(1).CreateAsync("intent-1", "buyer-1",
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task NonBuyerCannotCreateIntent()
    {
        var response = await _fn.FunctionHandler(Req(IntentOp(), "farmer-1", "farmer"), _ctx);

        Assert.Equal(200, response.StatusCode); // batch returns 200; per-op status is error
        Assert.Contains("INTENT_UNAUTHORIZED", response.Body);
        await _intents.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task BuyerIdMismatchIsRejected()
    {
        var op = IntentOp(buyerId: "buyer-2"); // payload buyerId differs from caller
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("INTENT_UNAUTHORIZED", response.Body);
        await _intents.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task UnknownFieldIsRejected()
    {
        var op = IntentOp(data: new Dictionary<string, object>
        {
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "cropType", "wheat" },
            { "quantityNeeded", 10 },
            { "maxPricePerKg", 120 },
            { "deliveryWoreda", "Adama" },
            { "evil", "x" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("INTENT_VALIDATION", response.Body);
        Assert.Contains("Unknown fields", response.Body);
    }

    [Fact]
    public async Task BuyerDeletesIntent_Succeeds()
    {
        var response = await _fn.FunctionHandler(
            Req(IntentOp(action: "delete"), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _intents.Received(1).DeleteAsync("intent-1", "buyer-1");
    }
}
