using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class MessageFunctionTests
{
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly MessageFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public MessageFunctionTests()
    {
        _fn = new MessageFunction(_messages, _orders, _deliveries);
    }

    private static APIGatewayProxyRequest Get(
        Dictionary<string, string>? query = null,
        Dictionary<string, string>? path = null,
        string? userId = "buyer-1",
        string? role = null)
    {
        APIGatewayCustomAuthorizerContext? authorizer = null;
        if (userId != null)
        {
            authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
            if (role != null)
            {
                authorizer["custom:role"] = role;
            }
        }

        return new()
        {
            HttpMethod = "GET",
            Path = "/messages",
            QueryStringParameters = query,
            PathParameters = path,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static Dictionary<string, object> Item(
        string messageId = "msg-1",
        string senderId = "buyer-1",
        string recipientId = "farmer-1",
        string createdAt = "2026-07-09T10:00:00Z",
        string body = "hi")
        => new()
        {
            { "messageId", messageId },
            { "conversationId", "crop:crop-1:buyer-1:farmer-1" },
            { "contextType", "crop" },
            { "contextId", "crop-1" },
            { "senderId", senderId },
            { "recipientId", recipientId },
            { "senderName", "Buyer One" },
            { "senderPhone", "+251900000001" },
            { "recipientName", "Farmer One" },
            { "recipientPhone", "+251900000002" },
            { "body", body },
            { "createdAt", createdAt },
            { "version", "1" },
        };

    [Fact]
    public async Task Thread_ReturnsParticipantMessages()
    {
        _messages.QueryByConversationAsync("crop:crop-1:buyer-1:farmer-1",
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { Item() },
                (Dictionary<string, AttributeValue>?)null));

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "conversationId", "crop:crop-1:buyer-1:farmer-1" } }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("msg-1", response.Body);
        AssertNoPhones(response.Body);
    }

    /// <summary>
    /// ESC-48: rows written before it still hold both phones; no read returns them (the parties
    /// take a number from the order, only while its contact is open).
    /// </summary>
    private static void AssertNoPhones(string body)
    {
        Assert.DoesNotContain("senderPhone", body);
        Assert.DoesNotContain("recipientPhone", body);
        Assert.DoesNotContain("251900000001", body);
        Assert.DoesNotContain("251900000002", body);
    }

    [Fact]
    public async Task Thread_FiltersOutMessagesTheCallerIsNotPartyTo()
    {
        // A message between two other users leaked into the query result must be
        // dropped (defense in depth against a guessed conversationId).
        _messages.QueryByConversationAsync(Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>
            {
                Item(messageId: "mine", senderId: "buyer-1", recipientId: "farmer-1"),
                Item(messageId: "not-mine", senderId: "x", recipientId: "y"),
            }, (Dictionary<string, AttributeValue>?)null));

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "conversationId", "c" } }),
            _ctx);

        Assert.Contains("mine", response.Body);
        Assert.DoesNotContain("not-mine", response.Body);
    }

    [Fact]
    public async Task Pull_MergesInboundAndOutbound()
    {
        _messages.QueryByRecipientSinceAsync("buyer-1", Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { Item(messageId: "inbound", senderId: "farmer-1", recipientId: "buyer-1") });
        _messages.QueryBySenderSinceAsync("buyer-1", Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { Item(messageId: "outbound", senderId: "buyer-1", recipientId: "farmer-1") });

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", "2026-07-09T00:00:00Z" } }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("inbound", response.Body);
        Assert.Contains("outbound", response.Body);
        AssertNoPhones(response.Body);
    }

    [Fact]
    public async Task MissingBothParams_Returns400()
    {
        var response = await _fn.FunctionHandler(Get(query: new Dictionary<string, string>()), _ctx);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("MESSAGE_VALIDATION", response.Body);
    }

    private void StubEmptyPull()
    {
        _messages.QueryByRecipientSinceAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _messages.QueryBySenderSinceAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
    }

    // SIM-34: a non-ISO (or non-UTC) since cursor used to answer 200 with the
    // wrong window, because DynamoDB compares it as plain text.
    [Theory]
    [InlineData("not-a-date")]
    [InlineData("yesterday")]
    [InlineData("1727740800")]
    [InlineData("10/01/2026")]
    [InlineData(" ")]
    [InlineData("2026-13-45T00:00:00Z")]
    [InlineData("2026-02-30")]
    [InlineData("2026-07-09T25:00:00Z")]
    [InlineData("2026-07-09T10:00:00")]
    [InlineData("2026-07-09T10:00:00+03:00")]
    [InlineData("2026-07-09T10:00Z")]
    [InlineData("2026-07-09T10:00:00Z\n")]
    [InlineData("2026-07-09\n")]
    [InlineData("٢٠٢٦-07-09")]
    public async Task Pull_InvalidSince_Returns400AndDoesNotQuery(string since)
    {
        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", since } }),
            _ctx);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("MESSAGE_VALIDATION", response.Body);
        await _messages.DidNotReceive().QueryByRecipientSinceAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>());
        await _messages.DidNotReceive().QueryBySenderSinceAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("2026-07-09T00:00:00Z")]
    [InlineData("2026-07-09T10:00:00.000Z")]
    [InlineData("2026-07-09T10:00:00.123456Z")]
    [InlineData("2026-07-09")]
    public async Task Pull_ValidSince_PassesCursorThroughUnchanged(string since)
    {
        StubEmptyPull();

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", since } }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        await _messages.Received(1).QueryByRecipientSinceAsync("buyer-1", since, Arg.Any<int>());
        await _messages.Received(1).QueryBySenderSinceAsync("buyer-1", since, Arg.Any<int>());
    }

    [Fact]
    public async Task Pull_EmptySince_IsFullPull()
    {
        // Recipient bootstrap: an empty cursor means no lower bound.
        StubEmptyPull();

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", "" } }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        await _messages.Received(1).QueryByRecipientSinceAsync("buyer-1", "", Arg.Any<int>());
        await _messages.Received(1).QueryBySenderSinceAsync("buyer-1", "", Arg.Any<int>());
    }

    [Fact]
    public async Task Thread_IgnoresSince()
    {
        _messages.QueryByConversationAsync(Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { Item() },
                (Dictionary<string, AttributeValue>?)null));

        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string>
            {
                { "conversationId", "c" },
                { "since", "garbage" },
            }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        await _messages.Received(1).QueryByConversationAsync(
            "c", Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
        await _messages.DidNotReceive().QueryByRecipientSinceAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>());
        await _messages.DidNotReceive().QueryBySenderSinceAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "conversationId", "c" } }, userId: null),
            _ctx);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task GetById_Participant_ReturnsMessage()
    {
        _messages.GetByIdAsync("msg-1").Returns(Item());

        var response = await _fn.FunctionHandler(
            Get(path: new Dictionary<string, string> { { "id", "msg-1" } }),
            _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("msg-1", response.Body);
        AssertNoPhones(response.Body);
    }

    [Fact]
    public async Task GetById_NonParticipant_Returns403()
    {
        _messages.GetByIdAsync("msg-1").Returns(Item(senderId: "x", recipientId: "y"));

        var response = await _fn.FunctionHandler(
            Get(path: new Dictionary<string, string> { { "id", "msg-1" } }),
            _ctx);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("MESSAGE_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        _messages.GetByIdAsync("missing").Returns((Dictionary<string, object>?)null);

        var response = await _fn.FunctionHandler(
            Get(path: new Dictionary<string, string> { { "id", "missing" } }),
            _ctx);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("MESSAGE_NOT_FOUND", response.Body);
    }

    // ===== FUP-124: a trucker who is not committed reads none of the order's messages =====

    private static Dictionary<string, object> ContextItem(
        string messageId,
        string contextType,
        string contextId,
        string senderId = "trucker-1",
        string recipientId = "buyer-1",
        string createdAt = "2026-11-01T10:00:00Z")
        => new()
        {
            { "messageId", messageId },
            { "conversationId", $"{contextType}:{contextId}:buyer-1:trucker-1" },
            { "contextType", contextType },
            { "contextId", contextId },
            { "senderId", senderId },
            { "recipientId", recipientId },
            { "body", "hello" },
            { "createdAt", createdAt },
            { "version", "1" },
        };

    /// <summary>A trip order of buyer-1, farmer-1 and trucker-1; <paramref name="trucker"/> null is a row from before ESC-15.</summary>
    private static Dictionary<string, object> OrderRow(string? trucker, string status = "accepted")
    {
        var order = new Dictionary<string, object>
        {
            ["status"] = status,
            ["buyerId"] = "buyer-1",
            ["farmerId"] = "farmer-1",
            ["truckerId"] = "trucker-1",
            ["tripId"] = "trip-1",
            [OrderCommitment.FarmerResponseField] = OrderCommitment.Response.Accepted
        };
        if (trucker != null)
        {
            order[OrderCommitment.TruckerResponseField] = trucker;
        }

        return order;
    }

    private void StubOrder(string orderId, Dictionary<string, object>? order)
        => _orders.GetByIdAsync(orderId).Returns(order);

    private void StubDelivery(string deliveryId, string? orderId)
    {
        var delivery = new Dictionary<string, object> { ["deliveryId"] = deliveryId, ["truckerId"] = "trucker-1" };
        if (orderId != null)
        {
            delivery["orderId"] = orderId;
        }

        _deliveries.GetByIdAsync(deliveryId).Returns(delivery);
    }

    private void StubThreadPage(string conversationId, params Dictionary<string, object>[] items)
        => _messages.QueryByConversationAsync(conversationId, Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((items.ToList(), new Dictionary<string, AttributeValue> { ["messageId"] = new() { S = "older" } }));

    private void StubPull(string userId, List<Dictionary<string, object>> inbound, List<Dictionary<string, object>> outbound)
    {
        _messages.QueryByRecipientSinceAsync(userId, Arg.Any<string?>(), Arg.Any<int>()).Returns(inbound);
        _messages.QueryBySenderSinceAsync(userId, Arg.Any<string?>(), Arg.Any<int>()).Returns(outbound);
    }

    /// <summary>The message ids of a page, in order, and its nextToken (null when absent).</summary>
    private static (List<string> Ids, string? NextToken) Page(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        var ids = root.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("messageId").GetString()!)
            .ToList();
        var token = root.TryGetProperty("nextToken", out var t) ? t.GetString() : null;
        return (ids, token);
    }

    private Task<APIGatewayProxyResponse> GetThread(string conversationId, string userId, string? role)
        => _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "conversationId", conversationId } }, userId: userId, role: role),
            _ctx);

    private Task<APIGatewayProxyResponse> GetPull(string userId, string? role)
        => _fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", "" } }, userId: userId, role: role),
            _ctx);

    private Task<APIGatewayProxyResponse> GetById(string messageId, string userId, string? role)
        => _fn.FunctionHandler(
            Get(path: new Dictionary<string, string> { { "id", messageId } }, userId: userId, role: role),
            _ctx);

    [Theory]
    [InlineData("awaiting_transport", OrderCommitment.Response.Declined)]
    [InlineData("awaiting_transport", OrderCommitment.Response.Expired)]
    [InlineData("awaiting_transport", OrderCommitment.Response.Cancelled)] // dropped out
    [InlineData("funded", OrderCommitment.Response.Pending)]
    [InlineData("funded", OrderCommitment.Response.NotAsked)]
    [InlineData("funded", null)] // a funded trip order from before ESC-15: not asked
    public async Task Thread_TruckerNotCommitted_GetsAnEmptyLastPage(string status, string? trucker)
    {
        StubThreadPage("order:order-1:buyer-1:trucker-1", ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(trucker, status));

        var response = await GetThread("order:order-1:buyer-1:trucker-1", "trucker-1", "trucker");

        Assert.Equal(200, response.StatusCode);
        var (ids, nextToken) = Page(response.Body);
        Assert.Empty(ids);
        Assert.Null(nextToken);
    }

    [Theory]
    [InlineData("accepted", OrderCommitment.Response.Accepted)]
    [InlineData("in_transit", OrderCommitment.Response.Accepted)]
    [InlineData("delivered", OrderCommitment.Response.Accepted)]
    [InlineData("settled", OrderCommitment.Response.Accepted)]
    [InlineData("dispute_resolved", OrderCommitment.Response.Accepted)]
    [InlineData("accepted", null)] // an accepted trip order from before ESC-15
    public async Task Thread_CommittedTrucker_ReadsAsBefore(string status, string? trucker)
    {
        StubThreadPage("order:order-1:buyer-1:trucker-1", ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(trucker, status));

        var response = await GetThread("order:order-1:buyer-1:trucker-1", "trucker-1", "trucker");

        Assert.Equal(200, response.StatusCode);
        var (ids, nextToken) = Page(response.Body);
        Assert.Equal(["m1"], ids);
        Assert.NotNull(nextToken);
    }

    [Theory]
    [InlineData("buyer-1", "buyer")]
    [InlineData("farmer-1", "farmer")]
    public async Task Thread_BuyerAndFarmer_ReadAsBefore_WithoutAnOrderRead(string userId, string role)
    {
        // The trucker declined; the buyer's and the farmer's own threads of the order are unchanged.
        StubThreadPage("order:order-1:buyer-1:farmer-1",
            ContextItem("m1", "order", "order-1", senderId: "buyer-1", recipientId: "farmer-1"));
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Declined, "awaiting_transport"));

        var response = await GetThread("order:order-1:buyer-1:farmer-1", userId, role);

        Assert.Equal(200, response.StatusCode);
        var (ids, nextToken) = Page(response.Body);
        Assert.Equal(["m1"], ids);
        Assert.NotNull(nextToken);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("buyer-1")]
    [InlineData("farmer-1")]
    public async Task Thread_BuyerAndFarmer_WithoutARoleClaim_StillRead(string userId)
    {
        // A token without the role claim is checked against the order, where the caller is the
        // buyer or the farmer.
        StubThreadPage("order:order-1:buyer-1:farmer-1",
            ContextItem("m1", "order", "order-1", senderId: "buyer-1", recipientId: "farmer-1"));
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Declined, "awaiting_transport"));

        var response = await GetThread("order:order-1:buyer-1:farmer-1", userId, role: null);

        Assert.Equal(["m1"], Page(response.Body).Ids);
    }

    [Fact]
    public async Task Thread_TruckerWithoutARoleClaim_IsCheckedToo()
    {
        StubThreadPage("order:order-1:buyer-1:trucker-1", ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Declined, "awaiting_transport"));

        var response = await GetThread("order:order-1:buyer-1:trucker-1", "trucker-1", role: null);

        Assert.Empty(Page(response.Body).Ids);
    }

    [Theory]
    [InlineData("crop")]
    [InlineData("trip")]
    public async Task Thread_CropAndTripThreads_AreUnchangedForTheTrucker(string contextType)
    {
        StubThreadPage($"{contextType}:x-1:buyer-1:trucker-1", ContextItem("m1", contextType, "x-1"));

        var response = await GetThread($"{contextType}:x-1:buyer-1:trucker-1", "trucker-1", "trucker");

        Assert.Equal(["m1"], Page(response.Body).Ids);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Thread_DeliveryThread_FollowsTheDeliveryOrder()
    {
        StubThreadPage("delivery:del-1:buyer-1:trucker-1", ContextItem("m1", "delivery", "del-1"));
        StubDelivery("del-1", "order-1");
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Cancelled, "awaiting_transport"));

        var response = await GetThread("delivery:del-1:buyer-1:trucker-1", "trucker-1", "trucker");

        Assert.Empty(Page(response.Body).Ids);
    }

    [Fact]
    public async Task Thread_OrderGone_IsClosedToTheTrucker()
    {
        StubThreadPage("order:order-1:buyer-1:trucker-1", ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", null);

        var response = await GetThread("order:order-1:buyer-1:trucker-1", "trucker-1", "trucker");

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(Page(response.Body).Ids);
    }

    [Fact]
    public async Task Pull_Trucker_DropsOnlyTheOrdersAndDeliveriesTheyAreNotCommittedTo()
    {
        StubPull("trucker-1",
            inbound:
            [
                ContextItem("declined-1", "order", "order-declined", senderId: "buyer-1", recipientId: "trucker-1"),
                ContextItem("committed-1", "order", "order-committed", senderId: "farmer-1", recipientId: "trucker-1",
                    createdAt: "2026-11-01T11:00:00Z"),
                ContextItem("crop-1", "crop", "crop-1", senderId: "buyer-1", recipientId: "trucker-1"),
            ],
            outbound:
            [
                ContextItem("declined-2", "order", "order-declined", createdAt: "2026-11-01T12:00:00Z"),
                ContextItem("committed-2", "order", "order-committed", createdAt: "2026-11-01T13:00:00Z"),
                ContextItem("delivery-open", "delivery", "del-committed", createdAt: "2026-11-01T14:00:00Z"),
                ContextItem("delivery-dropped", "delivery", "del-dropped", createdAt: "2026-11-01T15:00:00Z"),
                ContextItem("delivery-no-order", "delivery", "del-legacy", createdAt: "2026-11-01T16:00:00Z"),
                ContextItem("trip-1", "trip", "trip-1", createdAt: "2026-11-01T17:00:00Z"),
            ]);
        StubOrder("order-declined", OrderRow(OrderCommitment.Response.Declined, "awaiting_transport"));
        StubOrder("order-committed", OrderRow(OrderCommitment.Response.Accepted, "in_transit"));
        StubOrder("order-dropped", OrderRow(OrderCommitment.Response.Cancelled, "awaiting_transport"));
        StubDelivery("del-committed", "order-committed");
        StubDelivery("del-dropped", "order-dropped");
        StubDelivery("del-legacy", orderId: null);

        var response = await GetPull("trucker-1", "trucker");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(["crop-1", "committed-1", "committed-2", "delivery-open", "trip-1"], Page(response.Body).Ids);
    }

    [Fact]
    public async Task Pull_Trucker_ReadsEachOrderAndDeliveryOnce()
    {
        StubPull("trucker-1",
            inbound:
            [
                ContextItem("a", "order", "order-1", senderId: "buyer-1", recipientId: "trucker-1"),
                ContextItem("b", "order", "order-1", senderId: "farmer-1", recipientId: "trucker-1"),
                ContextItem("c", "delivery", "del-1", senderId: "buyer-1", recipientId: "trucker-1"),
            ],
            outbound:
            [
                ContextItem("d", "order", "order-1"),
                ContextItem("e", "delivery", "del-1"),
            ]);
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Accepted));
        StubDelivery("del-1", "order-1");

        var response = await GetPull("trucker-1", "trucker");

        Assert.Equal(5, Page(response.Body).Ids.Count);
        await _orders.Received(1).GetByIdAsync("order-1");
        await _deliveries.Received(1).GetByIdAsync("del-1");
    }

    [Theory]
    [InlineData("buyer-1", "buyer")]
    [InlineData("farmer-1", "farmer")]
    public async Task Pull_BuyerAndFarmer_Unchanged(string userId, string role)
    {
        StubPull(userId,
            inbound: [ContextItem("in", "order", "order-1", senderId: "trucker-1", recipientId: userId)],
            outbound: [ContextItem("out", "delivery", "del-1", senderId: userId, recipientId: "trucker-1")]);

        var response = await GetPull(userId, role);

        Assert.Equal(["in", "out"], Page(response.Body).Ids);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Pull_Trucker_OrderReadFails_Answers500_NotAPartialPull()
    {
        // Dropping the messages would let the client's since cursor move past them for good.
        StubPull("trucker-1",
            inbound: [ContextItem("m1", "order", "order-1", senderId: "buyer-1", recipientId: "trucker-1")],
            outbound: []);
        _orders.GetByIdAsync("order-1").Returns<Dictionary<string, object>?>(
            _ => throw new AmazonDynamoDBException("throttled"));

        var response = await GetPull("trucker-1", "trucker");

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("MESSAGE_SERVER_ERROR", response.Body);
        Assert.DoesNotContain("m1", response.Body);
    }

    [Fact]
    public async Task Pull_TablesNotConfigured_TruckerOrderMessages_Answer500_BuyerUnchanged()
    {
        // A code deploy ahead of the infra one: the trucker's order messages fail (the client
        // retries), a buyer's pull is untouched.
        var fn = new MessageFunction(_messages);
        StubPull("trucker-1",
            inbound: [ContextItem("t", "order", "order-1", senderId: "buyer-1", recipientId: "trucker-1")],
            outbound: []);
        StubPull("buyer-1",
            inbound: [ContextItem("b", "order", "order-1", senderId: "trucker-1", recipientId: "buyer-1")],
            outbound: []);

        var trucker = await fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", "" } }, userId: "trucker-1", role: "trucker"), _ctx);
        var buyer = await fn.FunctionHandler(
            Get(query: new Dictionary<string, string> { { "since", "" } }, userId: "buyer-1", role: "buyer"), _ctx);

        Assert.Equal(500, trucker.StatusCode);
        Assert.Equal(["b"], Page(buyer.Body).Ids);
    }

    [Theory]
    [InlineData(OrderCommitment.Response.Declined)]
    [InlineData(OrderCommitment.Response.Expired)]
    [InlineData(OrderCommitment.Response.Cancelled)]
    public async Task GetById_TruckerNotCommitted_Returns403(string trucker)
    {
        _messages.GetByIdAsync("m1").Returns(ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(trucker, "awaiting_transport"));

        var response = await GetById("m1", "trucker-1", "trucker");

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("MESSAGE_FORBIDDEN", response.Body);
        Assert.DoesNotContain("hello", response.Body);
    }

    [Fact]
    public async Task GetById_TruckerNotCommitted_DeliveryMessage_Returns403()
    {
        _messages.GetByIdAsync("m1").Returns(ContextItem("m1", "delivery", "del-1"));
        StubDelivery("del-1", "order-1");
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Cancelled, "awaiting_transport"));

        var response = await GetById("m1", "trucker-1", "trucker");

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task GetById_CommittedTrucker_ReturnsMessage()
    {
        _messages.GetByIdAsync("m1").Returns(ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Accepted, "delivered"));

        var response = await GetById("m1", "trucker-1", "trucker");

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("m1", response.Body);
    }

    [Theory]
    [InlineData("buyer-1", "buyer")]
    [InlineData("buyer-1", null)]
    public async Task GetById_Buyer_ReadsAnOrderMessage_WhateverTheTrucker(string userId, string? role)
    {
        _messages.GetByIdAsync("m1").Returns(ContextItem("m1", "order", "order-1"));
        StubOrder("order-1", OrderRow(OrderCommitment.Response.Declined, "awaiting_transport"));

        var response = await GetById("m1", userId, role);

        Assert.Equal(200, response.StatusCode);
    }
}
