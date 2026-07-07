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

/// <summary>Tests for the match_event entity handler in SyncFunction.</summary>
public class SyncFunctionMatchEventTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IMatchEventRepository _events = Substitute.For<IMatchEventRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionMatchEventTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _events.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<bool>.Success(true));
        _fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries", matchEventRepo: _events);
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

    private static SyncOperationRequest EventOp(
        string action = "create",
        string eventId = "ev-1",
        string buyerId = "buyer-1",
        Dictionary<string, object>? data = null)
    {
        return new SyncOperationRequest
        {
            OperationId = $"op-{action}",
            IdempotencyKey = $"{buyerId}:match_event:{eventId}:{action}:1",
            EntityType = "match_event",
            EntityId = eventId,
            Action = action,
            Data = data ?? new Dictionary<string, object>
            {
                { "eventId", eventId },
                { "intentId", "intent-1" },
                { "buyerId", buyerId },
                { "eventType", "opened" },
                { "listingId", "crop-1" },
                { "score", 0.87 },
                { "rank", 2 },
            },
            Timestamp = "2026-07-07T10:00:00Z",
        };
    }

    [Fact]
    public async Task BuyerLogsOpenedEvent_Succeeds()
    {
        var response = await _fn.FunctionHandler(Req(EventOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _events.Received(1).CreateAsync("ev-1", "intent-1", "buyer-1",
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ShownEvent_WithImpressionsPayload_Succeeds()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "shown" },
            { "matchCount", 12 },
            { "impressions", "[{\"listingId\":\"crop-1\",\"score\":0.9,\"rank\":0}]" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task NonBuyerCannotLogEvents()
    {
        var response = await _fn.FunctionHandler(Req(EventOp(), "farmer-1", "farmer"), _ctx);

        Assert.Contains("MATCH_EVENT_UNAUTHORIZED", response.Body);
        await _events.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task BuyerIdMismatchIsRejected()
    {
        var op = EventOp(buyerId: "buyer-2");
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("MATCH_EVENT_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task UpdateAction_IsRejected_AppendOnly()
    {
        var response = await _fn.FunctionHandler(
            Req(EventOp(action: "update"), "buyer-1", "buyer"), _ctx);

        Assert.Contains("append-only", response.Body);
        await _events.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task UnknownEventType_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "hovered" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("MATCH_EVENT_VALIDATION", response.Body);
    }

    [Fact]
    public async Task OpenedEvent_WithoutListingId_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "opened" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("listingId is required", response.Body);
    }

    [Fact]
    public async Task UnknownField_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "shown" },
            { "evil", "x" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("Unknown fields", response.Body);
    }

    [Fact]
    public async Task OutOfRangeScore_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "opened" },
            { "listingId", "crop-1" },
            { "score", 1.5 },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("score must be a number in [0, 1]", response.Body);
    }

    [Fact]
    public async Task NegativeRank_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "opened" },
            { "listingId", "crop-1" },
            { "rank", -1 },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("rank must be a non-negative integer", response.Body);
    }

    [Fact]
    public async Task OversizedImpressions_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "shown" },
            { "impressions", new string('x', 8193) },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("impressions must not exceed", response.Body);
    }

    [Fact]
    public async Task MalformedImpressionsJson_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "shown" },
            { "impressions", "{not json" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("impressions must be a valid JSON string", response.Body);
    }

    [Fact]
    public async Task NegativeMatchCount_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "shown" },
            { "matchCount", -3 },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("matchCount must be a non-negative integer", response.Body);
    }

    [Fact]
    public async Task MalformedOccurredAt_IsRejected()
    {
        var op = EventOp(data: new Dictionary<string, object>
        {
            { "eventId", "ev-1" },
            { "intentId", "intent-1" },
            { "buyerId", "buyer-1" },
            { "eventType", "opened" },
            { "listingId", "crop-1" },
            { "occurredAt", "yesterday" },
        });
        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("occurredAt must be an ISO 8601 date", response.Body);
    }

    [Fact]
    public async Task WithoutRepo_ReportsUnavailable()
    {
        var fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries");
        var response = await fn.FunctionHandler(Req(EventOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("MATCH_EVENT_UNAVAILABLE", response.Body);
    }
}
