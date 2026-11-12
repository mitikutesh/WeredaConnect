using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-05: the claim routes of the manual payout queue and the admin audit rows of a claim, a release and a
/// refused duplicate reference, through the admin Lambda with the DynamoDB requests captured.
/// </summary>
public class AdminManualPayoutClaimRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string IdempotencyTable = "escrow-idempotency-test";
    private const string LedgerTable = "escrow-ledger-test";
    private const string EscrowId = "escrow-order-ord-7";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<PutItemRequest> _puts = new();
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly Dictionary<string, AttributeValue> _escrow = QueuedRefund();

    public AdminManualPayoutClaimRouteTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { new(_escrow) } });
        _db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<GetItemRequest>();
            return new GetItemResponse { Item = request.TableName == UsersTable ? Buyer() : new() };
        });
        _db.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        _db.PutItemAsync(Arg.Do<PutItemRequest>(_puts.Add), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static Dictionary<string, AttributeValue> QueuedRefund() => new()
    {
        ["escrowId"] = S(EscrowId),
        ["createdAt"] = S("2026-10-01T10:00:00Z"),
        ["orderId"] = S("ord-7"),
        ["buyerId"] = S("buyer-7"),
        ["farmerId"] = S("farmer-7"),
        ["status"] = S("escrowed"),
        ["amount"] = new AttributeValue { N = "20000" },
        ["farmerPayout"] = new AttributeValue { N = "19500" },
        ["commission"] = new AttributeValue { N = "500" },
        ["currency"] = S("ETB"),
        ["settlementLock"] = S("refund"),
        ["manualPayout"] = S("open"),
        ["manualPayoutQueuedAt"] = S("2026-10-02T09:00:00Z"),
        ["manualPayoutOp"] = S("refund"),
        ["manualPayoutReason"] = S("PAYOUT_NOT_SUPPORTED"),
        ["manualPayoutFromStatus"] = S("escrowed"),
        ["manualBuyerStatus"] = S("pending"),
    };

    private static Dictionary<string, AttributeValue> Buyer() => new()
    {
        ["userId"] = S("buyer-7"),
        ["phoneNumber"] = S("+251911000007"),
        ["displayName"] = S("Sara"),
        ["role"] = S("buyer"),
        ["woreda"] = S("w"),
        ["isPhoneVerified"] = new AttributeValue { BOOL = true },
        ["createdAt"] = S("2026-01-01T00:00:00Z"),
        ["updatedAt"] = S("2026-01-01T00:00:00Z"),
    };

    private AdminFunction Function(string idempotencyTable = IdempotencyTable, bool withLedger = true) =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable, idempotencyTable)
        {
            LedgerTable = withLedger ? LedgerTable : ""
        };

    private static APIGatewayProxyRequest Post(string path, string admin = "admin-1", string? body = null) => new()
    {
        HttpMethod = "POST",
        Path = path,
        Body = body,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", admin }, { "cognito:groups", "admin" } }
        },
    };

    private PutItemRequest TxAudit(string action) => _puts.Single(p => p.TableName == TxAuditTable && p.Item["action"].S == action);

    [Theory]
    [InlineData("farmer", true)]
    [InlineData("buyer", true)]
    [InlineData("release%23farmer", true)]
    [InlineData("release#trucker#trucker-1", true)]
    [InlineData("refund%23duplicate%23PAY-2", true)]
    [InlineData("platform", false)]
    [InlineData("release%23far mer", false)]
    [InlineData("%23", true)]
    [InlineData("", false)]
    public void ALegInThePath_IsALegNameOrALedgerRef(string leg, bool valid)
        => AdminFunction.IsPayoutLegRef(leg).Should().Be(valid);

    [Fact]
    public async Task Claim_WritesTheConditionalClaim_AndAnAdminAuditRow()
    {
        var response = await Function().FunctionHandler(Post($"/admin/payouts/{EscrowId}/buyer/claim"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"claimed\":true").And.Contain("\"claimedBy\":\"admin-1\"");
        var update = _updates.Should().ContainSingle().Subject;
        update.TableName.Should().Be(EscrowsTable);
        update.ConditionExpression.Should().Contain("attribute_not_exists(#claimBy) OR #claimBy = :actor OR #claimExp < :now");
        update.ExpressionAttributeNames["#claimBy"].Should().Be("manualBuyerClaimedBy");
        update.UpdateExpression.Should().NotContain("version");
        TxAudit("admin_manual_payout_claimed").Item["metadata"].S.Should().StartWith("leg=buyer;expiresAt=");
        _puts.Should().Contain(p => p.TableName == AuditTable && p.Item["action"].S == "manual_payout_claimed");
    }

    [Fact]
    public async Task Claim_LostToAnotherAdmin_Is409WithTheirName()
    {
        _db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _escrow["manualBuyerClaimedBy"] = S("admin-2");
            _escrow["manualBuyerClaimExpiresAt"] = S(DateTime.UtcNow.AddMinutes(20).ToString("o"));
            return Task.FromException<UpdateItemResponse>(new ConditionalCheckFailedException("claimed"));
        });

        var response = await Function().FunctionHandler(Post($"/admin/payouts/{EscrowId}/buyer/claim"), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_046").And.Contain("admin-2");
        _puts.Should().NotContain(p => p.TableName == TxAuditTable);
    }

    [Fact]
    public async Task Unclaim_ByTheHolder_RemovesTheClaim_AndAnAdminAuditRow()
    {
        _escrow["manualBuyerClaimedBy"] = S("admin-1");
        _escrow["manualBuyerClaimExpiresAt"] = S(DateTime.UtcNow.AddMinutes(20).ToString("o"));

        var response = await Function().FunctionHandler(Post($"/admin/payouts/{EscrowId}/buyer/unclaim"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"released\":true");
        var update = _updates.Should().ContainSingle().Subject;
        update.UpdateExpression.Should().Be("REMOVE #claimBy, #claimAt, #claimExp");
        update.ConditionExpression.Should().Be("#claimBy = :actor");
        TxAudit("admin_manual_payout_claim_released").Item["metadata"].S.Should().Be("leg=buyer");
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("unclaim")]
    [InlineData("complete")]
    public async Task WithoutTheReferenceTable_TheRoutesRefuse(string action)
    {
        var response = await Function(idempotencyTable: "").FunctionHandler(
            Post($"/admin/payouts/{EscrowId}/buyer/{action}", body: JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })),
            _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_WithoutTheAdminGroup_Is403()
    {
        var request = Post($"/admin/payouts/{EscrowId}/buyer/claim");
        request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_ADuplicateReference_Is409NamingWhere_AndLeavesAnAdminAuditRow()
    {
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("canceled")
            {
                CancellationReasons = new() { new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" } }
            });
        _db.GetItemAsync(Arg.Is<GetItemRequest>(g => g.TableName == IdempotencyTable), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new()
                {
                    ["idempotencyKey"] = S("payout-ref#TB-REF-0009"),
                    ["escrowId"] = S("escrow-order-ord-3"),
                    ["ref"] = S("farmer"),
                    ["recordedBy"] = S("admin-2"),
                }
            });

        var response = await Function().FunctionHandler(
            Post($"/admin/payouts/{EscrowId}/buyer/complete", body: JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })),
            _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ESCROW_047")
            .And.Contain("on escrow escrow-order-ord-3, leg farmer.")
            .And.Contain("\"field\":\"externalRef\"");
        await _db.Received().GetItemAsync(
            Arg.Is<GetItemRequest>(g => g.TableName == IdempotencyTable && g.ConsistentRead == true
                                        && g.Key["idempotencyKey"].S == "payout-ref#TB-REF-0009"),
            Arg.Any<CancellationToken>());
        TxAudit("admin_manual_payout_refused").Item["metadata"].S.Should().Be("leg=buyer;code=ESCROW_047;ref=TB-REF-0009");
        _puts.Should().Contain(p => p.TableName == AuditTable && p.Item["action"].S == "manual_payout_refused"
                                    && p.Item["metadata"].S.EndsWith(";usedOn=escrow-order-ord-3/farmer"));
        _puts.Should().NotContain(p => p.TableName == TxAuditTable && p.Item["action"].S == "admin_manual_payout");
    }

    [Fact]
    public async Task Complete_ALedgerRefInThePath_ReachesTheLedger()
    {
        // Before FUP-05 the route answered 404 for any ledger ref, so no ledger row could be recorded from the app.
        var response = await Function().FunctionHandler(
            Post($"/admin/payouts/{EscrowId}/release%23farmer/complete", body: JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })),
            _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ESCROW_035");
        await _db.Received().GetItemAsync(
            Arg.Is<GetItemRequest>(g => g.TableName == LedgerTable && g.Key["ref"].S == "release#farmer"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ShowsTheClaim_AndWhetherTheViewerHoldsIt()
    {
        _escrow["manualBuyerClaimedBy"] = S("admin-2");
        _escrow["manualBuyerClaimedAt"] = S(DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        _escrow["manualBuyerClaimExpiresAt"] = S(DateTime.UtcNow.AddMinutes(25).ToString("o"));
        var request = Post("/admin/payouts/manual");
        request.HttpMethod = "GET";

        var response = await Function(withLedger: false).FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"claimedBy\":\"admin-2\"").And.Contain("\"claimedByMe\":false");
    }
}
