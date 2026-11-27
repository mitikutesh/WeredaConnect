using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// Order-lifecycle 2.26.0: <c>POST /admin/orders/{orderId}/commission</c> through the admin Lambda with the DynamoDB
/// requests captured: the one guarded write (the money at the new rate, the stamp, its conditions), the audit row, the
/// refusals before any write, the replay and the lost race.
/// </summary>
public class AdminCommissionChangeRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string OrderId = "ord-c1";
    private const string Reason = "Agreed 2.5% with the buyer on the phone, first order from this cooperative.";
    private static readonly DateTime Now = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();
    private readonly List<PutItemRequest> _puts = new();
    private Dictionary<string, AttributeValue> _order = Unpaid();

    public AdminCommissionChangeRouteTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            var row = request.TableName == OrdersTable && _order.Count > 0 ? _order : null;
            return new QueryResponse
            {
                Items = row == null ? new List<Dictionary<string, AttributeValue>>() : new List<Dictionary<string, AttributeValue>> { new(row) },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            };
        });
        _db.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(_transactions.Add), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _db.PutItemAsync(Arg.Do<PutItemRequest>(_puts.Add), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    /// <summary>170 kg at 18.99 ETB with a trip: crop 3228.30, transport 500, 5 % commission 161.42, total 3889.72.</summary>
    private static Dictionary<string, AttributeValue> Unpaid(string status = "created") => new()
    {
        ["orderId"] = S(OrderId),
        ["createdAt"] = S("2026-11-26T06:00:00.0000000Z"),
        ["status"] = S(status),
        ["version"] = N("2"),
        ["buyerId"] = S("buyer-c1"),
        ["farmerId"] = S("farmer-c1"),
        ["truckerId"] = S("trucker-c1"),
        ["quantity"] = N("170"),
        ["pricePerUnit"] = N("18.99"),
        ["cropCost"] = N("3228.3"),
        ["transportCost"] = N("500"),
        ["commissionRate"] = N("0.05"),
        ["commissionAmount"] = N("161.42"),
        ["orderTotal"] = N("3889.72")
    };

    private AdminFunction Function() =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable) { Clock = () => Now };

    private static APIGatewayProxyRequest Post(object? body = null, bool adminGroup = true) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/orders/{OrderId}/commission",
        Body = JsonSerializer.Serialize(body ?? new { commissionRate = 0.025m, reason = Reason, expectedVersion = 2 }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = adminGroup
                ? new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
                : new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private List<PutItemRequest> TxAudits => _puts.Where(p => p.TableName == TxAuditTable).ToList();

    private static string Name(Update update, string attribute, string prefix = "#f")
        => update.ExpressionAttributeNames.Single(n => n.Value == attribute && n.Key.StartsWith(prefix, StringComparison.Ordinal)).Key;

    private static AttributeValue Value(Update update, string attribute)
        => update.ExpressionAttributeValues[":" + Name(update, attribute)[1..]];

    private void StampApplied(int fromVersion, string toRate)
    {
        _order["version"] = N((fromVersion + 1).ToString());
        _order["commissionRate"] = N(toRate);
        _order[OrderCommissionChange.Field] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [OrderCommissionChange.StampAt] = S("2026-11-26T07:59:00.0000000Z"),
                [OrderCommissionChange.StampFromRate] = N("0.05"),
                [OrderCommissionChange.StampToRate] = N(toRate),
                [OrderCommissionChange.StampFromTotal] = N("3889.72"),
                [OrderCommissionChange.StampFromVersion] = N(fromVersion.ToString())
            }
        };
    }

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    public async Task AnUnpaidOrder_TakesTheNewRate_InOneGuardedWrite_AndOneAuditRow(string status)
    {
        _order = Unpaid(status);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"commissionRate\":0.025")
            .And.Contain("\"commissionAmount\":80.71")
            .And.Contain("\"orderTotal\":3809.01")
            .And.Contain("\"commissionRateBefore\":0.05")
            .And.Contain("\"orderTotalBefore\":3889.72")
            .And.Contain("\"version\":3")
            .And.Contain($"\"status\":\"{status}\"")
            .And.Contain("\"alreadyApplied\":false");

        var update = _transactions.Should().ContainSingle().Subject.TransactItems.Should().ContainSingle().Subject.Update;
        update.TableName.Should().Be(OrdersTable);
        var values = update.ExpressionAttributeValues;
        values[":expectedVersion"].N.Should().Be("2");
        values[":newVersion"].N.Should().Be("3");
        // 3228.30 x 0.025 = 80.7075, to the santim halves away from zero: 80.71; 3228.30 + 500 + 80.71.
        Value(update, "commissionRate").N.Should().Be("0.025");
        Value(update, "commissionAmount").N.Should().Be("80.71");
        Value(update, "orderTotal").N.Should().Be("3809.01");
        update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#f", StringComparison.Ordinal)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { "commissionRate", "commissionAmount", "orderTotal", OrderCommissionChange.Field },
                "only the commission and the total move");

        var stamp = Value(update, OrderCommissionChange.Field).M;
        stamp[OrderCommissionChange.StampFromRate].N.Should().Be("0.05");
        stamp[OrderCommissionChange.StampToRate].N.Should().Be("0.025");
        stamp[OrderCommissionChange.StampFromTotal].N.Should().Be("3889.72");
        stamp[OrderCommissionChange.StampFromVersion].N.Should().Be("2");
        stamp[OrderCommissionChange.StampAt].S.Should().Be(OrderCommitment.Iso(Now));
        stamp.Should().NotContainKey("reason", "the reason goes only to the audit trail");

        // Lands only on the order as read: version, status, no verified payment and no payment hold.
        update.ConditionExpression.Should().Contain("#v = :expectedVersion");
        var conditioned = update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#c", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => values[":" + n.Key[1..]].S);
        conditioned.Should().Equal(new Dictionary<string, string> { { "status", status } });
        update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#a", StringComparison.Ordinal)).Select(n => n.Value)
            .Should().BeEquivalentTo("paymentVerifiedAt", PaymentHoldFields.HoldUntil);

        var audit = TxAudits.Should().ContainSingle().Subject;
        audit.Item["action"].S.Should().Be(OrderCommissionChange.AuditAction);
        audit.Item["actor"].S.Should().Be("admin-1");
        audit.Item["fromState"].S.Should().Be(status);
        audit.Item["toState"].S.Should().Be(status);
        audit.Item["metadata"].S.Should().Be($"fromRate=0.05;toRate=0.025;fromTotal=3889.72;toTotal=3809.01;reason={Reason}");
    }

    [Fact]
    public async Task ZeroCommission_IsAllowed_AndTheTotalIsCropPlusTransport()
    {
        var response = await Function().FunctionHandler(Post(new { commissionRate = 0, reason = Reason, expectedVersion = 2 }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var update = _transactions.Single().TransactItems.Single().Update;
        Value(update, "commissionAmount").N.Should().Be("0");
        Value(update, "orderTotal").N.Should().Be("3728.3");
    }

    [Fact]
    public async Task AnOldPaymentHold_ThatHasLapsed_IsChecked_AsRead()
    {
        _order = Unpaid("pending_payment");
        _order[PaymentHoldFields.HoldUntil] = S("2026-11-26T07:00:00.0000000Z");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var update = _transactions.Single().TransactItems.Single().Update;
        var conditioned = update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#c", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => update.ExpressionAttributeValues[":" + n.Key[1..]].S);
        conditioned.Should().Contain(PaymentHoldFields.HoldUntil, "2026-11-26T07:00:00.0000000Z",
            "the webhook extends the hold without a version bump, so the hold itself is checked");
        update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#a", StringComparison.Ordinal)).Select(n => n.Value)
            .Should().Equal("paymentVerifiedAt");
    }

    [Theory]
    [InlineData("payment_submitted")]
    [InlineData("payment_failed")]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("cancelled")]
    public async Task AnOrderThatIsNotUnpaid_IsRefused_BeforeAnyWrite(string status)
    {
        _order = Unpaid(status);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(OrderCommissionChange.NotAllowedCode).And.Contain(status);
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task AVerifiedPayment_OrAPaymentOnItsWay_IsRefused_BeforeAnyWrite()
    {
        _order[PaymentHoldFields.HoldUntil] = S("2026-11-26T09:00:00.0000000Z");
        var held = await Function().FunctionHandler(Post(), _context);
        held.StatusCode.Should().Be(409);
        held.Body.Should().Contain(OrderCommissionChange.NotAllowedCode).And.Contain("on its way");

        _order = Unpaid();
        _order["paymentVerifiedAt"] = S("2026-11-26T07:30:00.0000000Z");
        var verified = await Function().FunctionHandler(Post(), _context);
        verified.StatusCode.Should().Be(409);
        verified.Body.Should().Contain("already verified");

        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheRateTheOrderAlreadyHas_IsRefused()
    {
        var response = await Function().FunctionHandler(Post(new { commissionRate = 0.05, reason = Reason, expectedVersion = 2 }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(OrderCommissionChange.NotAllowedCode).And.Contain("already has this commission rate");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderWithoutACropCost_IsRefused()
    {
        _order.Remove("cropCost");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("no crop cost");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderAtAnotherVersion_IsChanged_BeforeAnyWrite()
    {
        _order["version"] = N("5");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(OrderCommissionChange.ChangedCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameRequestAgain_AnswersAlreadyApplied_AndWritesNothing()
    {
        StampApplied(fromVersion: 2, toRate: "0.025");
        _order["commissionAmount"] = N("80.71");
        _order["orderTotal"] = N("3809.01");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"alreadyApplied\":true").And.Contain("\"orderTotal\":3809.01")
            .And.Contain("\"orderTotalBefore\":3889.72");
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();

        // Another rate from the same version is not the same request: the order moved on.
        var other = await Function().FunctionHandler(Post(new { commissionRate = 0.03, reason = Reason, expectedVersion = 2 }), _context);
        other.StatusCode.Should().Be(409);
        other.Body.Should().Contain(OrderCommissionChange.ChangedCode);
    }

    [Fact]
    public async Task ALostRace_ThatTurnsOutToBeThisChange_IsAReplay_AnythingElseIsChanged()
    {
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // Another tab's identical request landed first.
            StampApplied(fromVersion: 2, toRate: "0.025");
            return Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" } }
            });
        });

        var replay = await Function().FunctionHandler(Post(), _context);
        replay.StatusCode.Should().Be(200, replay.Body);
        replay.Body.Should().Contain("\"alreadyApplied\":true");
        TxAudits.Should().BeEmpty();

        _order = Unpaid();
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" } }
            }));

        var changed = await Function().FunctionHandler(Post(), _context);
        changed.StatusCode.Should().Be(409);
        changed.Body.Should().Contain(OrderCommissionChange.ChangedCode);
        TxAudits.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"commissionRate\":0.21,\"reason\":\"Agreed with the buyer today.\",\"expectedVersion\":2}", "commissionRate")]
    [InlineData("{\"commissionRate\":-0.01,\"reason\":\"Agreed with the buyer today.\",\"expectedVersion\":2}", "commissionRate")]
    [InlineData("{\"commissionRate\":0.02505,\"reason\":\"Agreed with the buyer today.\",\"expectedVersion\":2}", "commissionRate")]
    [InlineData("{\"reason\":\"Agreed with the buyer today.\",\"expectedVersion\":2}", "commissionRate")]
    [InlineData("{\"commissionRate\":0.03,\"reason\":\"   short   \",\"expectedVersion\":2}", "reason")]
    [InlineData("{\"commissionRate\":0.03,\"expectedVersion\":2}", "reason")]
    [InlineData("{\"commissionRate\":0.03,\"reason\":\"Agreed with the buyer today.\"}", "expectedVersion")]
    [InlineData("{\"commissionRate\":0.03,\"reason\":\"Agreed with the buyer today.\",\"expectedVersion\":0}", "expectedVersion")]
    public async Task ABadRequest_NamesItsField_AndReadsNothing(string body, string field)
    {
        var request = Post();
        request.Body = body;

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain(OrderCommissionChange.InvalidCode).And.Contain($"\"field\":\"{field}\"");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALongReason_IsRefused_AndTheReasonIsTrimmed()
    {
        var tooLong = await Function().FunctionHandler(
            Post(new { commissionRate = 0.025m, reason = new string('a', 501), expectedVersion = 2 }), _context);
        tooLong.StatusCode.Should().Be(400);

        var padded = await Function().FunctionHandler(
            Post(new { commissionRate = 0.025m, reason = $"  {Reason}  ", expectedVersion = 2 }), _context);
        padded.StatusCode.Should().Be(200, padded.Body);
        TxAudits.Single().Item["metadata"].S.Should().EndWith($";reason={Reason}");
    }

    [Fact]
    public async Task OnlyTheAdminGroup_MayChangeTheCommission()
    {
        var roleOnly = await Function().FunctionHandler(Post(adminGroup: false), _context);

        roleOnly.StatusCode.Should().Be(403);
        _transactions.Should().BeEmpty();
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownOrder_Is404()
    {
        _order = new Dictionary<string, AttributeValue>();

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_012");
    }
}
