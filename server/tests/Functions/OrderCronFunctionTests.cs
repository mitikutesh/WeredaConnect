using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class OrderCronFunctionTests
{
    private readonly IOrderRepository _mockOrderRepo;
    private readonly OrderCronFunction _function;
    private readonly TestLambdaContext _context;

    public OrderCronFunctionTests()
    {
        _mockOrderRepo = Substitute.For<IOrderRepository>();
        _mockOrderRepo.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ => new List<Dictionary<string, object>>());
        _function = new OrderCronFunction(_mockOrderRepo);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeRequest(string action)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = $"{{\"action\":\"{action}\"}}"
        };
    }

    [Fact]
    public async Task HandleAutoCancelExpired_NoExpiredOrders_Returns200()
    {
        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"0\"", response.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_CancelsExpiredCreatedOrders()
    {
        var expiredOrder = new Dictionary<string, object>
        {
            { "orderId", "order-expired" },
            { "status", "created" },
            { "version", "1" },
            { "createdAt", DateTime.UtcNow.AddHours(-25).ToString("o") }
        };

        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>> { expiredOrder });
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());

        _mockOrderRepo.UpdateAsync("order-expired", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-expired" },
                { "status", "cancelled" }
            }));

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"1\"", response.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_CancelsExpiredPendingPaymentOrders()
    {
        var expiredOrder = new Dictionary<string, object>
        {
            { "orderId", "order-pending-expired" },
            { "status", "pending_payment" },
            { "version", "2" },
            { "createdAt", DateTime.UtcNow.AddHours(-30).ToString("o") }
        };

        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>> { expiredOrder });

        _mockOrderRepo.UpdateAsync("order-pending-expired", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-pending-expired" },
                { "status", "cancelled" }
            }));

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"1\"", response.Body);
    }

    // ===== ESC-47: the pending_payment auto-cancel counts from when the order entered it =====

    /// <summary>
    /// An order created 30 hours ago whose first payment failed (submitted 25 hours ago, failed by the
    /// cron an hour ago) and that the buyer moved back to pending_payment
    /// <paramref name="pendingPaymentAgo"/> ago (null: a row from before the stamp).
    /// </summary>
    private void StubRetriedPendingPaymentOrder(TimeSpan? pendingPaymentAgo)
    {
        var now = DateTime.UtcNow;
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-retry" },
            { "status", "pending_payment" },
            { "version", "6" },
            { "createdAt", now.AddHours(-30).ToString("o") },
            { "paymentSubmittedAt", now.AddHours(-25).ToString("o") },
            { "updatedAt", now.AddMinutes(-10).ToString("o") }
        };
        if (pendingPaymentAgo != null)
        {
            order["pendingPaymentAt"] = (now - pendingPaymentAgo.Value).ToString("o");
        }

        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>> { order });
        _mockOrderRepo.UpdateAsync("order-retry", Arg.Any<Dictionary<string, object>>(), 6)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-retry" },
                { "status", "cancelled" }
            }));
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OldOrderRetriedIntoPendingPaymentRecently_IsNotCancelled()
    {
        var txAuditRepo = Substitute.For<ITransactionAuditRepository>();
        var sut = new OrderCronFunction(_mockOrderRepo, txAuditRepo);
        StubRetriedPendingPaymentOrder(pendingPaymentAgo: TimeSpan.FromMinutes(10));

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"cancelled\":\"0\"", actual.Body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(
            "order-retry", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await txAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task HandleAutoCancelExpired_PendingPaymentWithNoStatusChangeFor25Hours_IsCancelled()
    {
        var txAuditRepo = Substitute.For<ITransactionAuditRepository>();
        var sut = new OrderCronFunction(_mockOrderRepo, txAuditRepo);
        StubRetriedPendingPaymentOrder(pendingPaymentAgo: TimeSpan.FromHours(25));

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"cancelled\":\"1\"", actual.Body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-retry", Arg.Is<Dictionary<string, object>>(d =>
            (string)d["status"] == "cancelled"), 6);
        await txAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == "order-retry" && (string)e["fromState"] == "pending_payment"
            && (string)e["toState"] == "cancelled"));
    }

    [Fact]
    public async Task HandleAutoCancelExpired_PendingPaymentFromBeforeTheStamp_CountsFromCreation()
    {
        // A row that entered pending_payment before the server stamped pendingPaymentAt keeps the
        // old clock (createdAt), even though its last write was recent.
        StubRetriedPendingPaymentOrder(pendingPaymentAgo: null);

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"cancelled\":\"1\"", actual.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_PendingPaymentStampJustInsideTheWindow_IsNotCancelled()
    {
        StubRetriedPendingPaymentOrder(pendingPaymentAgo: TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"cancelled\":\"0\"", actual.Body);
    }

    private static Dictionary<string, object> SubmittedOrder(
        string orderId, DateTime createdAt, DateTime? paymentSubmittedAt, string version = "3")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId },
            { "status", "payment_submitted" },
            { "version", version },
            { "createdAt", createdAt.ToString("o") }
        };
        if (paymentSubmittedAt != null)
        {
            order["paymentSubmittedAt"] = paymentSubmittedAt.Value.ToString("o");
        }

        return order;
    }

    private void StubSubmittedOrders(params Dictionary<string, object>[] orders)
        => _mockOrderRepo.QueryByStatusForCronAsync("payment_submitted", Arg.Any<string?>())
            .Returns(orders.ToList());

    private void StubUpdateSucceeds(string orderId, int version)
        => _mockOrderRepo.TryMarkPaymentFailedAsync(orderId, version, Arg.Any<string>()).Returns(true);

    [Fact]
    public async Task HandleAutoCancelExpired_PaymentSubmittedOver24HoursAgo_FailsThePayment()
    {
        var txAuditRepo = Substitute.For<ITransactionAuditRepository>();
        var sut = new OrderCronFunction(_mockOrderRepo, txAuditRepo);
        var now = DateTime.UtcNow;
        StubSubmittedOrders(SubmittedOrder("order-stale", now.AddHours(-30), now.AddHours(-25)));
        StubUpdateSucceeds("order-stale", 3);

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"paymentFailed\":\"1\"", actual.Body);
        await _mockOrderRepo.Received(1).TryMarkPaymentFailedAsync("order-stale", 3, Arg.Any<string>());
        await txAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == "order-stale" && (string)e["fromState"] == "payment_submitted"
            && (string)e["toState"] == "payment_failed"));
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OldOrderWithARecentPaymentSubmission_IsLeftWaiting()
    {
        var now = DateTime.UtcNow;
        StubSubmittedOrders(SubmittedOrder("order-retried", now.AddHours(-30), now.AddHours(-2)));

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"paymentFailed\":\"0\"", actual.Body);
        await _mockOrderRepo.DidNotReceive().TryMarkPaymentFailedAsync("order-retried", Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OrderFromBeforeThePaymentClock_UsesItsCreationTime()
    {
        StubSubmittedOrders(SubmittedOrder("order-legacy", DateTime.UtcNow.AddHours(-25), paymentSubmittedAt: null));
        StubUpdateSucceeds("order-legacy", 3);

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"paymentFailed\":\"1\"", actual.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OrderFundedMeanwhile_IsNotFailed()
    {
        var txAuditRepo = Substitute.For<ITransactionAuditRepository>();
        var sut = new OrderCronFunction(_mockOrderRepo, txAuditRepo);
        var now = DateTime.UtcNow;
        StubSubmittedOrders(SubmittedOrder("order-funded", now.AddHours(-30), now.AddHours(-25)));
        _mockOrderRepo.TryMarkPaymentFailedAsync("order-funded", 3, Arg.Any<string>()).Returns(false);

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"paymentFailed\":\"0\"", actual.Body);
        await txAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ===== ESC-45 (D8): never fail an order whose verified payment is pending =====

    [Fact]
    public async Task HandleAutoCancelExpired_OrderHeldForAPendingPayment_IsSkippedWithoutAWrite()
    {
        var now = DateTime.UtcNow;
        var order = SubmittedOrder("order-held", now.AddHours(-30), now.AddHours(-25));
        order[PaymentHoldFields.HoldUntil] = WebhookInbox.Iso(now.AddHours(2));
        StubSubmittedOrders(order);

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"paymentFailed\":\"0\"", actual.Body);
        await _mockOrderRepo.DidNotReceive().TryMarkPaymentFailedAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OrderWhoseHoldEnded_IsFailed()
    {
        var now = DateTime.UtcNow;
        var order = SubmittedOrder("order-was-held", now.AddHours(-30), now.AddHours(-25));
        order[PaymentHoldFields.HoldUntil] = WebhookInbox.Iso(now.AddMinutes(-5));
        StubSubmittedOrders(order);
        StubUpdateSucceeds("order-was-held", 3);

        var actual = await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"paymentFailed\":\"1\"", actual.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_OrderWithAPendingInboxRow_IsSkipped()
    {
        var inbox = Substitute.For<IWebhookInboxRepository>();
        inbox.QueryPendingAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new[] { PendingRow("order-pending") }, (Dictionary<string, AttributeValue>?)null));
        var sut = new OrderCronFunction(_mockOrderRepo, inboxRepo: inbox);
        var now = DateTime.UtcNow;
        StubSubmittedOrders(
            SubmittedOrder("order-pending", now.AddHours(-30), now.AddHours(-25)),
            SubmittedOrder("order-unpaid", now.AddHours(-30), now.AddHours(-25)));
        StubUpdateSucceeds("order-unpaid", 3);

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Contains("\"paymentFailed\":\"1\"", actual.Body);
        await _mockOrderRepo.DidNotReceive().TryMarkPaymentFailedAsync("order-pending", Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAutoCancelExpired_UnreadableInbox_SkipsThePaymentFailedStepThisRun()
    {
        var inbox = Substitute.For<IWebhookInboxRepository>();
        inbox.QueryPendingAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        var sut = new OrderCronFunction(_mockOrderRepo, inboxRepo: inbox);
        var now = DateTime.UtcNow;
        StubSubmittedOrders(SubmittedOrder("order-unpaid", now.AddHours(-30), now.AddHours(-25)));
        StubUpdateSucceeds("order-unpaid", 3);

        var actual = await sut.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"paymentFailed\":\"0\"", actual.Body);
        await _mockOrderRepo.DidNotReceive().TryMarkPaymentFailedAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>());
    }

    private static WebhookInboxRow PendingRow(string orderId)
        => new($"webhook-inbox:WCO1:TB1:Completed", $"escrow-order-{orderId}", WebhookInbox.Received, orderId,
            "WCO1", WebhookInbox.Pending, WebhookInbox.Iso(DateTime.UtcNow), 1, WebhookInbox.Iso(DateTime.UtcNow), null);

    [Fact]
    public async Task HandleAutoCancelExpired_QueriesSubmittedPaymentsCreatedBeforeTheThreshold()
    {
        var before = DateTime.UtcNow;

        await _function.FunctionHandler(MakeRequest("auto_cancel_expired"), _context);

        var after = DateTime.UtcNow;
        await _mockOrderRepo.Received(1).QueryByStatusForCronAsync("payment_submitted", Arg.Is<string?>(s =>
            DateTime.Parse(s!, null, System.Globalization.DateTimeStyles.RoundtripKind) >= before.AddHours(-24)
            && DateTime.Parse(s!, null, System.Globalization.DateTimeStyles.RoundtripKind) <= after.AddHours(-24)));
    }

    [Fact]
    public async Task Handle_MissingAction_Returns400()
    {
        var request = new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{}" };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ORDER_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task Handle_UnknownAction_Returns400()
    {
        var request = MakeRequest("unknown_action");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ORDER_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task Handle_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { HttpMethod = "POST", Body = "not json" };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }
}
