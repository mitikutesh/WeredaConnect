using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>Tests for the message (in-app chat) entity handler in SyncFunction.</summary>
public class SyncFunctionMessageTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionMessageTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _messages.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "messageId", "msg-1" }, { "version", "1" } }));

        // Default participants for the marketplace flow.
        _users.GetUserByIdAsync("buyer-1").Returns(User("buyer-1", "buyer", "+251900000001", "Buyer One"));
        _users.GetUserByIdAsync("farmer-1").Returns(User("farmer-1", "farmer", "+251900000002", "Farmer One"));
        _users.GetUserByIdAsync("trucker-1").Returns(User("trucker-1", "trucker", "+251900000003", "Trucker One"));
        _users.GetUserByIdAsync("stranger-9").Returns(User("stranger-9", "buyer", "+251900000009", "Stranger"));

        _fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries",
            userRepo: _users, orderRepo: _orders, messageRepo: _messages);
    }

    private static UserDto User(string id, string role, string phone, string name) => new()
    {
        UserId = id,
        PhoneNumber = phone,
        DisplayName = name,
        Role = role,
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1,
    };

    private void CropOwnedBy(string farmerId = "farmer-1")
        => _crop.GetByIdAsync("crop-1").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-1" },
            { "farmerId", farmerId },
        });

    private void OrderWith(string buyerId = "buyer-1", string farmerId = "farmer-1", string? truckerId = "trucker-1")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-1" },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
        };
        if (truckerId != null) order["truckerId"] = truckerId;
        _orders.GetByIdAsync("order-1").Returns(order);
    }

    private void TripOwnedBy(string truckerId = "trucker-1")
        => _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "trips"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "tripId", new AttributeValue { S = "trip-1" } },
                        { "truckerId", new AttributeValue { S = truckerId } },
                    }
                }
            });

    private void DeliveryWith(string buyerId = "buyer-1", string farmerId = "farmer-1", string truckerId = "trucker-1")
        => _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "deliveries"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "deliveryId", new AttributeValue { S = "delivery-1" } },
                        { "buyerId", new AttributeValue { S = buyerId } },
                        { "farmerId", new AttributeValue { S = farmerId } },
                        { "truckerId", new AttributeValue { S = truckerId } },
                    }
                }
            });

    private static string ConversationId(string contextType, string contextId, string a, string b)
    {
        var ids = new[] { a, b };
        Array.Sort(ids, StringComparer.Ordinal);
        return $"{contextType}:{contextId}:{ids[0]}:{ids[1]}";
    }

    private static APIGatewayProxyRequest Req(SyncOperationRequest op, string userId, string? role)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null) authorizer["custom:role"] = role;

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

    private static SyncOperationRequest MessageOp(
        string contextType = "crop",
        string contextId = "crop-1",
        string senderId = "buyer-1",
        string recipientId = "farmer-1",
        string messageId = "msg-1",
        string body = "Hello, is this still available?",
        string action = "create",
        Dictionary<string, object>? extraData = null)
    {
        var data = new Dictionary<string, object>
        {
            { "messageId", messageId },
            { "conversationId", ConversationId(contextType, contextId, senderId, recipientId) },
            { "contextType", contextType },
            { "contextId", contextId },
            { "senderId", senderId },
            { "recipientId", recipientId },
            { "body", body },
            { "createdAt", "2026-07-09T10:00:00Z" },
        };
        if (extraData != null)
        {
            foreach (var kv in extraData) data[kv.Key] = kv.Value;
        }

        return new SyncOperationRequest
        {
            OperationId = "op-msg",
            IdempotencyKey = $"{senderId}:message:{messageId}:create:1",
            EntityType = "message",
            EntityId = messageId,
            Action = action,
            Data = data,
            Timestamp = "2026-07-09T10:00:00Z",
        };
    }

    [Fact]
    public async Task BuyerMessagesFarmerAboutCrop_Succeeds()
    {
        CropOwnedBy();

        var response = await _fn.FunctionHandler(Req(MessageOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _messages.Received(1).CreateAsync("msg-1", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FarmerRepliesToBuyerAboutCrop_Succeeds()
    {
        CropOwnedBy();

        var response = await _fn.FunctionHandler(
            Req(MessageOp(senderId: "farmer-1", recipientId: "buyer-1"), "farmer-1", "farmer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task BuyerMessagesTruckerAboutTrip_Succeeds()
    {
        TripOwnedBy();

        var response = await _fn.FunctionHandler(
            Req(MessageOp(contextType: "trip", contextId: "trip-1",
                senderId: "buyer-1", recipientId: "trucker-1"), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task BuyerMessagesTruckerAboutOrder_Succeeds()
    {
        OrderWith();

        var response = await _fn.FunctionHandler(
            Req(MessageOp(contextType: "order", contextId: "order-1",
                senderId: "buyer-1", recipientId: "trucker-1"), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task FarmerMessagesTruckerAboutDelivery_Succeeds()
    {
        DeliveryWith();

        var response = await _fn.FunctionHandler(
            Req(MessageOp(contextType: "delivery", contextId: "delivery-1",
                senderId: "farmer-1", recipientId: "trucker-1"), "farmer-1", "farmer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task ThirdPartyMessagingOthersOrder_IsRejected()
    {
        OrderWith(); // participants: buyer-1, farmer-1, trucker-1 — stranger-9 is not one.

        var response = await _fn.FunctionHandler(
            Req(MessageOp(contextType: "order", contextId: "order-1",
                senderId: "stranger-9", recipientId: "farmer-1"), "stranger-9", "buyer"), _ctx);

        Assert.Contains("MESSAGE_FORBIDDEN", response.Body);
        await _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task MessagingAboutCropYouDoNotOwn_WithNonBuyerCounterpart_IsRejected()
    {
        // Two truckers cannot chat about a crop: exactly one side must own the
        // crop and the other must be a buyer.
        CropOwnedBy(farmerId: "farmer-1");

        var response = await _fn.FunctionHandler(
            Req(MessageOp(senderId: "trucker-1", recipientId: "stranger-9"), "trucker-1", "trucker"), _ctx);

        Assert.Contains("MESSAGE_FORBIDDEN", response.Body);
        await _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task SenderNotAuthenticatedUser_IsRejected()
    {
        CropOwnedBy();

        // Authenticated as farmer-1 but claims to send as buyer-1.
        var response = await _fn.FunctionHandler(Req(MessageOp(), "farmer-1", "farmer"), _ctx);

        Assert.Contains("MESSAGE_UNAUTHORIZED", response.Body);
        await _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ClientSuppliedPhone_IsStrippedAndServerDenormalized()
    {
        CropOwnedBy();

        var op = MessageOp(extraData: new Dictionary<string, object>
        {
            // A spoofing client tries to inject its own phone/name.
            { "senderPhone", "+251999999999" },
            { "recipientName", "Not The Farmer" },
        });

        Dictionary<string, object>? captured = null;
        _messages.CreateAsync("msg-1", Arg.Do<Dictionary<string, object>>(d => captured = d))
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "messageId", "msg-1" }, { "version", "1" } }));

        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(captured);
        // Server overwrote with the authoritative users-table values.
        Assert.Equal("+251900000001", captured!["senderPhone"]);
        Assert.Equal("+251900000002", captured["recipientPhone"]);
        Assert.Equal("Farmer One", captured["recipientName"]);
    }

    [Fact]
    public async Task UpdateAction_IsRejected_MessagesAreImmutable()
    {
        CropOwnedBy();
        var op = MessageOp(action: "update");

        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("MESSAGE_VALIDATION", response.Body);
        await _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task MismatchedConversationId_IsRejected()
    {
        CropOwnedBy();
        var op = MessageOp();
        op.Data!["conversationId"] = "crop:crop-1:someone:else";

        var response = await _fn.FunctionHandler(Req(op, "buyer-1", "buyer"), _ctx);

        Assert.Contains("MESSAGE_VALIDATION", response.Body);
        await _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task EmptyBody_IsRejected()
    {
        CropOwnedBy();

        var response = await _fn.FunctionHandler(Req(MessageOp(body: "   "), "buyer-1", "buyer"), _ctx);

        Assert.Contains("MESSAGE_VALIDATION", response.Body);
    }
}
