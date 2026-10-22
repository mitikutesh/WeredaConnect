using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-02: GET /admin/payments/unapplied and POST /admin/payments/{orderId}/refund-manual,
/// through the real handler, service and repositories over a substituted DynamoDB client. The
/// webhook rows are shaped exactly as TelebirrWebhookFunction writes them.
/// </summary>
public class AdminFunctionUnappliedPaymentTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string Merch = "WCO0123456789ABCDEF01234567001";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    public AdminFunctionUnappliedPaymentTests()
    {
        _db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        ScanReturns();
        TrailReturns();
        OrderReturns(null);
    }

    private AdminFunction Function() =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

    private static AttributeValue S(string v) => new() { S = v };

    private static APIGatewayProxyRequest Request(
        string method, string path, string? body = null, Dictionary<string, string>? query = null, bool adminGroup = true)
    {
        // Admin-pool tokens carry cognito:groups; a role claim alone is not the admin group.
        var authorizer = adminGroup
            ? new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
            : new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = path,
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static string RefundBody(
        string paymentOrderId = "TB-PAY-1", string telebirrRef = "TB-REFUND-9", decimal amount = 1500m,
        string reason = "Order was cancelled before the payment arrived", string? merchOrderId = null)
        => JsonSerializer.Serialize(new { paymentOrderId, telebirrRef, amount, reason, merchOrderId });

    private static Dictionary<string, AttributeValue> AuditRow(
        string action, string timestamp, string? telebirrRef, string? metadata, string escrowId = "escrow-order-ord-1",
        string actor = "telebirr")
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["timestamp"] = S(timestamp),
            ["auditId"] = S($"audit-{Guid.NewGuid():N}"),
            ["action"] = S(action),
            ["actor"] = S(actor),
            ["fromState"] = S(""),
            ["toState"] = S(""),
        };
        if (telebirrRef != null)
        {
            row["telebirrRef"] = S(telebirrRef);
        }

        if (metadata != null)
        {
            row["metadata"] = S(metadata);
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> Unmatched(
        string paymentOrderId = "TB-PAY-1", string reason = "status_cancelled", string escrowId = "escrow-order-ord-1")
        => AuditRow("order_payment_unmatched", "2026-10-20T08:00:00.0000000Z", paymentOrderId,
            $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason={reason}", escrowId);

    private static Dictionary<string, AttributeValue> Unprocessed(string paymentOrderId = "TB-PAY-1")
        => AuditRow("order_payment_unprocessed", "2026-10-20T08:00:00.0000000Z", paymentOrderId,
            $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason=TimeoutException");

    private static Dictionary<string, AttributeValue> RefundRow(string paymentOrderId = "TB-PAY-1", string telebirrRef = "TB-REFUND-9")
        => AuditRow("order_payment_refunded_manual", "2026-10-21T09:00:00.0000000Z", telebirrRef,
            $"paymentOrderId={paymentOrderId};amount=1500;merchOrderId={Merch}", actor: "admin-0");

    private static Dictionary<string, AttributeValue> Order(string status, string? fundedBy = null)
    {
        var order = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("ord-1"),
            ["createdAt"] = S("2026-10-19T08:00:00Z"),
            ["status"] = S(status),
            ["buyerId"] = S("buyer-1"),
        };
        if (fundedBy != null)
        {
            order["telebirrPaymentOrderId"] = S(fundedBy);
        }

        return order;
    }

    private void ScanReturns(
        List<Dictionary<string, AttributeValue>>? items = null, Dictionary<string, AttributeValue>? lastKey = null)
        => _db.ScanAsync(Arg.Is<ScanRequest>(s => s.TableName == AuditTable), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Items = items ?? new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>(),
            });

    private void TrailReturns(params Dictionary<string, AttributeValue>[] rows)
        => _db.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == AuditTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = rows.ToList() });

    private void OrderReturns(Dictionary<string, AttributeValue>? order)
        => _db.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = order == null
                    ? new List<Dictionary<string, AttributeValue>>()
                    : new List<Dictionary<string, AttributeValue>> { order },
            });

    private static Dictionary<string, AttributeValue> ScanHit(string escrowId = "escrow-order-ord-1")
        => new() { ["escrowId"] = S(escrowId), ["action"] = S("order_payment_unmatched") };

    private async Task NothingWritten()
    {
        await _db.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ── GET /admin/payments/unapplied ─────────────────────────────

    [Fact]
    public async Task List_Empty_ReturnsNoPaymentsAndNoToken()
    {
        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!;
        body.Payments.Should().BeEmpty();
        body.NextToken.Should().BeNull();
        body.Total.Should().Be(0);
        await _db.Received(1).ScanAsync(
            Arg.Is<ScanRequest>(s => s.TableName == AuditTable && s.Limit == 500
                                     && s.FilterExpression.Contains("#action IN")
                                     && s.ExpressionAttributeValues.Values.Any(v => v.S == "order_payment_unmatched")
                                     && s.ExpressionAttributeValues.Values.Any(v => v.S == "order_payment_duplicate")
                                     && s.ExpressionAttributeValues.Values.Any(v => v.S == "order_payment_unprocessed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_OneUnappliedPayment_IsListedWithItsOrder()
    {
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() });
        TrailReturns(Unmatched());
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        response.StatusCode.Should().Be(200);
        var payment = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!
            .Payments.Should().ContainSingle().Subject;
        payment.OrderId.Should().Be("ord-1");
        payment.EscrowId.Should().Be("escrow-order-ord-1");
        payment.PaymentOrderId.Should().Be("TB-PAY-1");
        payment.MerchOrderId.Should().Be(Merch);
        payment.CapturedAmount.Should().Be(1500m);
        payment.Currency.Should().Be("ETB");
        payment.Actions.Should().Equal("order_payment_unmatched");
        payment.Reasons.Should().Equal("status_cancelled");
        payment.OrderStatus.Should().Be("cancelled");
        payment.BuyerId.Should().Be("buyer-1");
        payment.MayStillApply.Should().BeFalse();
    }

    [Fact]
    public async Task List_RefundedPayment_IsNotListed_AndItsOrderIsNotRead()
    {
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() });
        TrailReturns(Unmatched(), RefundRow());

        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        response.StatusCode.Should().Be(200);
        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!
            .Payments.Should().BeEmpty();
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(q => q.TableName == OrdersTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_PaymentThatLaterFundedTheOrder_IsNotListed()
    {
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() });
        TrailReturns(Unprocessed());
        OrderReturns(Order("funded", fundedBy: "TB-PAY-1"));

        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!
            .Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task List_UnprocessedWhileTheOrderWaits_IsFlaggedMayStillApply()
    {
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() });
        TrailReturns(Unprocessed());
        OrderReturns(Order("payment_submitted"));

        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!
            .Payments.Should().ContainSingle().Which.MayStillApply.Should().BeTrue();
    }

    [Fact]
    public async Task List_Paging_TokenResumesTheScan_AndSkipsTheOrderAlreadyRead()
    {
        // Page 1 ends inside ord-1's partition: ord-1 was read in full with a Query.
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-order-ord-1"),
            ["timestamp"] = S("2026-10-20T08:00:00.0000000Z"),
        };
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() }, lastKey);
        TrailReturns(Unmatched());
        OrderReturns(Order("cancelled"));

        var first = await Function().FunctionHandler(
            Request("GET", "/admin/payments/unapplied", query: new Dictionary<string, string> { ["limit"] = "1" }), _context);

        var firstBody = JsonSerializer.Deserialize(first.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!;
        firstBody.Payments.Should().ContainSingle();
        firstBody.NextToken.Should().NotBeNullOrEmpty();
        await _db.Received(1).ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());

        // Page 2 starts with ord-1's remaining rows: they are not listed twice.
        _db.ClearReceivedCalls();
        ScanReturns(new List<Dictionary<string, AttributeValue>> { ScanHit() });
        var second = await Function().FunctionHandler(
            Request("GET", "/admin/payments/unapplied",
                query: new Dictionary<string, string> { ["nextToken"] = firstBody.NextToken! }), _context);

        second.StatusCode.Should().Be(200);
        var secondBody = JsonSerializer.Deserialize(second.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!;
        secondBody.Payments.Should().BeEmpty();
        secondBody.NextToken.Should().BeNull();
        await _db.Received(1).ScanAsync(
            Arg.Is<ScanRequest>(s => s.ExclusiveStartKey.Count == 2
                                     && s.ExclusiveStartKey["escrowId"].S == "escrow-order-ord-1"
                                     && s.ExclusiveStartKey["timestamp"].S == "2026-10-20T08:00:00.0000000Z"),
            Arg.Any<CancellationToken>());
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(q => q.TableName == AuditTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ReadsAtMostFourScanPagesPerCall()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-order-ord-9"),
            ["timestamp"] = S("2026-10-20T08:00:00.0000000Z"),
        };
        ScanReturns(lastKey: lastKey);

        var response = await Function().FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);

        var body = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.UnappliedPaymentListResponse)!;
        body.Payments.Should().BeEmpty();
        body.NextToken.Should().NotBeNullOrEmpty();
        await _db.Received(4).ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_TokenOfAnotherList_Returns400Escrow023()
    {
        // Every admin list signs its tokens with the same key; a manual payout token is not a scan position here.
        var foreign = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-order-ord-1"),
            ["manualPayout"] = S("open"),
            ["manualPayoutQueuedAt"] = S("2026-10-20T08:00:00Z"),
        });

        var response = await Function().FunctionHandler(
            Request("GET", "/admin/payments/unapplied", query: new Dictionary<string, string> { ["nextToken"] = foreign }),
            _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_023");
        await _db.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    // ── POST /admin/payments/{orderId}/refund-manual ──────────────

    [Fact]
    public async Task Refund_WritesTheReservedRowAndTheAdminAudit_AndNothingElse()
    {
        TrailReturns(Unmatched());
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.RecordManualRefundResponse)!;
        body.AlreadyRecorded.Should().BeFalse();
        body.AmountChecked.Should().BeTrue();
        body.PaymentOrderId.Should().Be("TB-PAY-1");
        body.TelebirrRef.Should().Be("TB-REFUND-9");
        body.Amount.Should().Be(1500m);
        body.RecordedBy.Should().Be("admin-1");

        await _db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable
                                        && p.Item["escrowId"].S == "escrow-order-ord-1"
                                        && p.Item["action"].S == "order_payment_refunded_manual"
                                        && p.Item["telebirrRef"].S == "TB-REFUND-9"
                                        && p.Item["metadata"].S == $"paymentOrderId=TB-PAY-1;amount=1500;merchOrderId={Merch}"
                                        && p.Item["actor"].S == "admin-1"
                                        && p.Item["reason"].S == "Order was cancelled before the payment arrived"
                                        && p.Item["timestamp"].S == body.RecordedAt
                                        && p.ConditionExpression == "attribute_not_exists(escrowId)"),
            Arg.Any<CancellationToken>());
        await _db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == TxAuditTable
                                        && p.Item["transactionId"].S == "ord-1"
                                        && p.Item["action"].S == "admin_payment_refunded_manual"
                                        && p.Item["actor"].S == "admin-1"
                                        && p.Item["metadata"].S.StartsWith("paymentOrderId=TB-PAY-1;telebirrRef=TB-REFUND-9;amount=1500;amountChecked=true;")),
            Arg.Any<CancellationToken>());
        // The payment was never applied: no order, escrow or other table changes.
        await _db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _db.Received(2).PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
        // The trail and the order are read strongly consistent.
        await _db.Received().QueryAsync(
            Arg.Is<QueryRequest>(q => q.TableName == AuditTable && q.ConsistentRead == true), Arg.Any<CancellationToken>());
        await _db.Received().QueryAsync(
            Arg.Is<QueryRequest>(q => q.TableName == OrdersTable && q.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refund_LogLine_DefangsClientValues()
    {
        TrailReturns(Unmatched("ESCROW_SETTLEMENT_FAILED"));
        OrderReturns(Order("cancelled"));
        var logger = new TestLambdaLogger();
        var context = new TestLambdaContext { Logger = logger };

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(paymentOrderId: "ESCROW_SETTLEMENT_FAILED")),
            context);

        response.StatusCode.Should().Be(200);
        var line = logger.Buffer.ToString().Split('\n').Single(l => l.Contains("ORDER_PAYMENT_REFUNDED_MANUAL"));
        line.Should().Contain("paymentOrderId=ESCROW-SETTLEMENT-FAILED").And.NotContain("ESCROW_SETTLEMENT_FAILED");
        line.Should().Contain("amount=1500").And.Contain("amountChecked=true").And.Contain("admin=admin-1");
    }

    [Fact]
    public async Task Refund_Replay_AnswersAlreadyRecorded_AndWritesNothing()
    {
        TrailReturns(Unmatched(), RefundRow());
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.RecordManualRefundResponse)!;
        body.AlreadyRecorded.Should().BeTrue();
        body.RecordedBy.Should().Be("admin-0");
        body.RecordedAt.Should().Be("2026-10-21T09:00:00.0000000Z");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_ReplayWithAnotherReference_Returns409Escrow036()
    {
        TrailReturns(Unmatched(), RefundRow(telebirrRef: "TB-REFUND-1"));
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(telebirrRef: "TB-REFUND-2")), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_036");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_ReferenceAlreadyUsedForAnotherPayment_Returns409Escrow036()
    {
        TrailReturns(Unmatched("TB-PAY-1"), Unmatched("TB-PAY-2"), RefundRow("TB-PAY-1", "TB-REFUND-9"));
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(paymentOrderId: "TB-PAY-2")), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_036");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_WrongAmount_Returns409Escrow043()
    {
        TrailReturns(Unmatched());
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(amount: 1400m)), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_043").And.Contain("1500");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_CapturedAmountUnknown_IsRecordedUnchecked()
    {
        TrailReturns(Unmatched(reason: "currency"));
        OrderReturns(Order("payment_submitted"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(amount: 1234.5m)), _context);

        response.StatusCode.Should().Be(200);
        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.RecordManualRefundResponse)!
            .AmountChecked.Should().BeFalse();
        await _db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == TxAuditTable && p.Item["metadata"].S.Contains("amountChecked=false")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refund_NoUnappliedPayment_Returns404Escrow042()
    {
        TrailReturns(Unmatched("TB-PAY-OTHER"));
        OrderReturns(Order("cancelled"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ESCROW_042");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_PaymentThatFundedTheOrder_Returns409Escrow044()
    {
        TrailReturns(Unprocessed());
        OrderReturns(Order("funded", fundedBy: "TB-PAY-1"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_044");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_UnprocessedWhileTheOrderWaits_Returns409Escrow044()
    {
        TrailReturns(Unprocessed());
        OrderReturns(Order("payment_submitted"));

        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_044");
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_NamelessPayment_IsMatchedByItsMerchOrderId()
    {
        TrailReturns(AuditRow("order_payment_unmatched", "2026-10-20T08:00:00.0000000Z", null,
            $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason=no_payment_order_id"));
        OrderReturns(Order("cancelled"));

        var withoutMerch = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(paymentOrderId: "TB-PAY-PORTAL")), _context);
        withoutMerch.StatusCode.Should().Be(404);

        var withMerch = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual",
                RefundBody(paymentOrderId: "TB-PAY-PORTAL", merchOrderId: Merch)), _context);

        withMerch.StatusCode.Should().Be(200);
        await _db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable
                                        && p.Item["metadata"].S == $"paymentOrderId=TB-PAY-PORTAL;amount=1500;merchOrderId={Merch}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refund_RoleClaimOnly_Returns403Escrow015_NothingRead()
    {
        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(), adminGroup: false), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await NothingWritten();
    }

    [Theory]
    [InlineData("TB", "TB-REFUND-9", 1500, "Order was cancelled before payment", "paymentOrderId")]
    [InlineData("TB-PAY-1", "bad ref!", 1500, "Order was cancelled before payment", "telebirrRef")]
    [InlineData("TB-PAY-1", "TB-REFUND-9", 0, "Order was cancelled before payment", "amount")]
    [InlineData("TB-PAY-1", "TB-REFUND-9", -5, "Order was cancelled before payment", "amount")]
    [InlineData("TB-PAY-1", "TB-REFUND-9", 1500.123, "Order was cancelled before payment", "amount")]
    [InlineData("TB-PAY-1", "TB-REFUND-9", 1500, "too short", "reason")]
    public async Task Refund_BadField_Returns400Escrow040_NothingRead(
        string paymentOrderId, string telebirrRef, double amount, string reason, string field)
    {
        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual",
                RefundBody(paymentOrderId, telebirrRef, (decimal)amount, reason)), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_040").And.Contain($"\"field\":\"{field}\"");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await NothingWritten();
    }

    [Fact]
    public async Task Refund_ReasonOver500Characters_Returns400()
    {
        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody(reason: new string('r', 501))), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_040");
    }

    [Fact]
    public async Task Refund_BadJson_Returns400Admin005()
    {
        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", "{not json"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
    }

    [Fact]
    public async Task Refund_UnsafeOrderId_Returns404_NothingRead()
    {
        var response = await Function().FunctionHandler(
            Request("POST", "/admin/payments/ord%201/refund-manual", RefundBody()), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ESCROW_042");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutTables_Returns503Admin004()
    {
        var bare = new AdminFunction(_db, UsersTable);

        var list = await bare.FunctionHandler(Request("GET", "/admin/payments/unapplied"), _context);
        var record = await bare.FunctionHandler(
            Request("POST", "/admin/payments/ord-1/refund-manual", RefundBody()), _context);

        list.StatusCode.Should().Be(503);
        record.StatusCode.Should().Be(503);
        list.Body.Should().Contain("ADMIN_004");
    }

    [Fact]
    public void Dtos_RoundTripThroughTheSourceGeneratedContext()
    {
        // AOT: every new type is registered in AppJsonContext and serialized camelCase, nulls omitted.
        var request = JsonSerializer.Deserialize(
            "{\"paymentOrderId\":\"TB-PAY-1\",\"telebirrRef\":\"TB-REFUND-9\",\"amount\":1500.50,\"reason\":\"why it was refunded\",\"merchOrderId\":null}",
            AppJsonContext.Default.RecordManualRefundRequest)!;
        request.Amount.Should().Be(1500.50m);
        request.MerchOrderId.Should().BeNull();

        var json = JsonSerializer.Serialize(new UnappliedPaymentListResponse
        {
            Payments = new List<UnappliedPaymentDto>
            {
                new()
                {
                    OrderId = "ord-1", EscrowId = "escrow-order-ord-1", PaymentOrderId = null, MerchOrderId = Merch,
                    Actions = new List<string> { "order_payment_unmatched" }, Reasons = new List<string>(),
                    FirstSeenAt = "a", LastSeenAt = "b", CapturedAmount = null, Currency = "ETB", MayStillApply = false,
                },
            },
            Total = 1,
        }, AppJsonContext.Default.UnappliedPaymentListResponse);
        json.Should().Contain("\"merchOrderId\":\"" + Merch + "\"").And.Contain("\"mayStillApply\":false")
            .And.NotContain("paymentOrderId").And.NotContain("nextToken");
    }
}
