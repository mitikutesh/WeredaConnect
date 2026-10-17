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
    private readonly MessageFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public MessageFunctionTests()
    {
        _fn = new MessageFunction(_messages);
    }

    private static APIGatewayProxyRequest Get(
        Dictionary<string, string>? query = null,
        Dictionary<string, string>? path = null,
        string? userId = "buyer-1")
        => new()
        {
            HttpMethod = "GET",
            Path = "/messages",
            QueryStringParameters = query,
            PathParameters = path,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = userId != null
                    ? new APIGatewayCustomAuthorizerContext { { "sub", userId } }
                    : null
            }
        };

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
}
