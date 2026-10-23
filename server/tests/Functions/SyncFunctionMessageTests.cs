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

    /// <summary>The order every thread test talks about; open for contact unless told otherwise.</summary>
    private Dictionary<string, object> OrderWith(
        string status = "accepted", string? truckerId = "trucker-1", string? overrideFlag = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-1" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { "status", status },
        };
        if (truckerId != null) order["truckerId"] = truckerId;
        if (overrideFlag != null) order[overrideFlag] = "2026-10-10T10:00:00Z";
        _orders.GetByIdAsync("order-1").Returns(order);
        return order;
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

    private void DeliveryWith(string? orderId = "order-1")
    {
        var delivery = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "delivery-1" } },
            { "buyerId", new AttributeValue { S = "buyer-1" } },
            { "farmerId", new AttributeValue { S = "farmer-1" } },
            { "truckerId", new AttributeValue { S = "trucker-1" } },
        };
        if (orderId != null) delivery["orderId"] = new AttributeValue { S = orderId };
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "deliveries"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { delivery } });
    }

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
        string contextType = "order",
        string contextId = "order-1",
        string senderId = "buyer-1",
        string recipientId = "farmer-1",
        string messageId = "msg-1",
        string body = "Hello, when can you load?",
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

    private static SyncOperationResult Result(APIGatewayProxyResponse response)
    {
        Assert.Equal(200, response.StatusCode);
        var sync = JsonSerializer.Deserialize<SyncResponse>(response.Body, Json)!;
        return Assert.Single(sync.Results);
    }

    private async Task<SyncOperationResult> Send(SyncOperationRequest op, string userId, string role)
        => Result(await _fn.FunctionHandler(Req(op, userId, role), _ctx));

    private Task AssertNothingStored()
        => _messages.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());

    // --- ESC-48: order and delivery threads, only while the order's contact is open ---

    [Theory]
    [InlineData("buyer-1", "buyer", "farmer-1")]
    [InlineData("farmer-1", "farmer", "buyer-1")]
    [InlineData("buyer-1", "buyer", "trucker-1")]
    [InlineData("trucker-1", "trucker", "buyer-1")]
    [InlineData("farmer-1", "farmer", "trucker-1")]
    [InlineData("trucker-1", "trucker", "farmer-1")]
    public async Task OpenOrder_AnyTwoParties_MayMessage(string sender, string role, string recipient)
    {
        OrderWith();

        var result = await Send(MessageOp(senderId: sender, recipientId: recipient), sender, role);

        Assert.Equal("success", result.Status);
        await _messages.Received(1).CreateAsync("msg-1", Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("disputed")]
    public async Task OrderThread_OpenStatus_Succeeds(string status)
    {
        OrderWith(status);

        var result = await Send(MessageOp(), "buyer-1", "buyer");

        Assert.Equal("success", result.Status);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_submitted")]
    [InlineData("payment_failed")]
    [InlineData("funded")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("dispute_resolved")]
    public async Task OrderThread_ClosedStatus_IsLocked(string status)
    {
        OrderWith(status);

        var result = await Send(MessageOp(senderId: "farmer-1", recipientId: "buyer-1"), "farmer-1", "farmer");

        Assert.Equal("error", result.Status);
        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public async Task OrderThread_RefundedByAdminOverride_IsLocked(string flag)
    {
        // An admin refund over goods on the move leaves the order in its status but flagged.
        OrderWith("in_transit", overrideFlag: flag);

        var result = await Send(MessageOp(), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task DeliveryThread_OrderOpen_Succeeds()
    {
        OrderWith("pickup_confirmed");
        DeliveryWith();

        var result = await Send(MessageOp(contextType: "delivery", contextId: "delivery-1",
            senderId: "farmer-1", recipientId: "trucker-1"), "farmer-1", "farmer");

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task DeliveryThread_OrderCancelled_IsLocked()
    {
        OrderWith("cancelled");
        DeliveryWith();

        var result = await Send(MessageOp(contextType: "delivery", contextId: "delivery-1",
            senderId: "buyer-1", recipientId: "trucker-1"), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task DeliveryThread_WithoutOrder_IsLocked()
    {
        DeliveryWith(orderId: null);

        var result = await Send(MessageOp(contextType: "delivery", contextId: "delivery-1",
            senderId: "buyer-1", recipientId: "farmer-1"), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await AssertNothingStored();
    }

    [Fact]
    public async Task DeliveryThread_OrderGone_IsLocked()
    {
        DeliveryWith(orderId: "order-gone");

        var result = await Send(MessageOp(contextType: "delivery", contextId: "delivery-1",
            senderId: "buyer-1", recipientId: "farmer-1"), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task DeliveryThread_OrderReadFails_IsATransientError()
    {
        DeliveryWith();
        _orders.GetByIdAsync("order-1").ThrowsAsync(new AmazonDynamoDBException("down"));

        var result = await Send(MessageOp(contextType: "delivery", contextId: "delivery-1",
            senderId: "buyer-1", recipientId: "farmer-1"), "buyer-1", "buyer");

        // Unprefixed: the client retries; nothing is stored or refused for good.
        Assert.Equal("error", result.Status);
        Assert.DoesNotContain("MESSAGE_", result.Message);
        await AssertNothingStored();
    }

    // --- ESC-48: crop and trip threads (before any order) take no new message from anyone ---

    [Theory]
    [InlineData("buyer-1", "buyer", "farmer-1")]
    [InlineData("farmer-1", "farmer", "buyer-1")]
    public async Task CropThread_IsLocked_ForBuyerAndOwner(string sender, string role, string recipient)
    {
        CropOwnedBy();

        var result = await Send(MessageOp(contextType: "crop", contextId: "crop-1",
            senderId: sender, recipientId: recipient), sender, role);

        Assert.Equal("error", result.Status);
        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Theory]
    [InlineData("buyer-1", "buyer", "trucker-1")]
    [InlineData("trucker-1", "trucker", "buyer-1")]
    public async Task TripThread_IsLocked_ForBuyerAndOwner(string sender, string role, string recipient)
    {
        TripOwnedBy();

        var result = await Send(MessageOp(contextType: "trip", contextId: "trip-1",
            senderId: sender, recipientId: recipient), sender, role);

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task LockedResend_OfAMessageThisSenderAlreadyStored_IsADuplicate()
    {
        // A crop message stored before ESC-48 whose answer was lost: its resend must not be
        // refused (the client would delete a message the server keeps).
        _messages.GetByIdAsync("msg-1").Returns(new Dictionary<string, object>
        {
            { "messageId", "msg-1" },
            { "senderId", "buyer-1" },
        });

        var result = await Send(MessageOp(contextType: "crop", contextId: "crop-1"), "buyer-1", "buyer");

        Assert.Equal("duplicate", result.Status);
        await AssertNothingStored();
    }

    [Fact]
    public async Task LockedMessage_WhoseIdBelongsToAnotherSender_IsStillLocked()
    {
        _messages.GetByIdAsync("msg-1").Returns(new Dictionary<string, object>
        {
            { "messageId", "msg-1" },
            { "senderId", "farmer-1" },
        });

        var result = await Send(MessageOp(contextType: "crop", contextId: "crop-1"), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_CONTACT_LOCKED:", result.Message);
    }

    // --- Participants, identity, validation ---

    [Fact]
    public async Task ThirdPartyMessagingOthersOrder_IsRejected()
    {
        OrderWith(); // participants: buyer-1, farmer-1, trucker-1; stranger-9 is not one.

        var result = await Send(MessageOp(senderId: "stranger-9", recipientId: "farmer-1"), "stranger-9", "buyer");

        Assert.StartsWith("MESSAGE_FORBIDDEN", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task SenderNotAuthenticatedUser_IsRejected()
    {
        OrderWith();

        // Authenticated as farmer-1 but claims to send as buyer-1.
        var result = await Send(MessageOp(), "farmer-1", "farmer");

        Assert.StartsWith("MESSAGE_UNAUTHORIZED", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task StoredMessage_HasServerNames_AndNoPhones()
    {
        OrderWith();

        var op = MessageOp(extraData: new Dictionary<string, object>
        {
            // A spoofing client tries to inject phones and a name.
            { "senderPhone", "+251999999999" },
            { "recipientPhone", "+251999999998" },
            { "recipientName", "Not The Farmer" },
        });

        Dictionary<string, object>? captured = null;
        _messages.CreateAsync("msg-1", Arg.Do<Dictionary<string, object>>(d => captured = d))
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "messageId", "msg-1" }, { "version", "1" } }));

        var result = await Send(op, "buyer-1", "buyer");

        Assert.Equal("success", result.Status);
        Assert.NotNull(captured);
        // Names come from the users table; ESC-48: phones are never stored on a message.
        Assert.Equal("Buyer One", captured!["senderName"]);
        Assert.Equal("Farmer One", captured["recipientName"]);
        Assert.False(captured.ContainsKey("senderPhone"));
        Assert.False(captured.ContainsKey("recipientPhone"));
    }

    [Fact]
    public async Task UpdateAction_IsRejected_MessagesAreImmutable()
    {
        OrderWith();

        var result = await Send(MessageOp(action: "update"), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_VALIDATION", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task MismatchedConversationId_IsRejected()
    {
        OrderWith();
        var op = MessageOp();
        op.Data!["conversationId"] = "order:order-1:someone:else";

        var result = await Send(op, "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_VALIDATION", result.Message);
        await AssertNothingStored();
    }

    [Fact]
    public async Task EmptyBody_IsRejected()
    {
        OrderWith();

        var result = await Send(MessageOp(body: "   "), "buyer-1", "buyer");

        Assert.StartsWith("MESSAGE_VALIDATION", result.Message);
    }

    // --- ACC-02: deleted accounts ---

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    public async Task Message_ToARecipientWhoDeletedTheirAccount_IsRefused(string state)
    {
        OrderWith();
        _users.GetUserByIdAsync("farmer-1")
            .Returns(User("farmer-1", "farmer", "+251900000002", "Farmer One") with { DeletionState = state });

        var result = await Send(MessageOp(), "buyer-1", "buyer");

        Assert.Equal("error", result.Status);
        Assert.StartsWith("SYNC_VALIDATION", result.Message);
        await AssertNothingStored();
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("cognito_pending")]
    [InlineData("completed")]
    public async Task AnyBatch_FromADeletingOrDeletedAccount_Is410_AndNothingRuns(string state)
    {
        OrderWith();
        _users.GetUserByIdAsync("buyer-1", true)
            .Returns(User("buyer-1", "buyer", "+251900000001", "Buyer One") with { DeletionState = state });

        var response = await _fn.FunctionHandler(Req(MessageOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(410, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("ACCOUNT_004", error.GetProperty("code").GetString());
        Assert.Equal(state, error.GetProperty("deletionState").GetString());
        await AssertNothingStored();
        await _meta.DidNotReceiveWithAnyArgs().IdempotencyKeyExistsAsync(default!);
    }

    [Fact]
    public async Task Batch_WhenTheCallersAccountCannotBeRead_IsARetryable503()
    {
        OrderWith();
        _users.GetUserByIdAsync("buyer-1", true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var response = await _fn.FunctionHandler(Req(MessageOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("SYNC_SERVER_ERROR", response.Body);
        await AssertNothingStored();
    }
}
