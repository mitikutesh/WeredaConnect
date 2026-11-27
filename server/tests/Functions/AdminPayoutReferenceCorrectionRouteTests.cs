using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-218: <c>POST /admin/payouts/{escrowId}/{ref}/correct</c> through the admin Lambda with the DynamoDB requests
/// captured: one transaction for the leg, the reference items and the escrow-audit row, one admin Audit Log row per
/// correction (none on a replay), the admin group, and the admin escrow DTO's 1.x manual legs the page acts on.
/// </summary>
public class AdminPayoutReferenceCorrectionRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string IdempotencyTable = "escrow-idempotency-test";
    private const string EscrowId = "escrow-order-ord-7";
    private const string Reason = "Typed the portal id of the other refund.";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<PutItemRequest> _puts = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();
    private readonly Dictionary<string, AttributeValue> _escrow = RecordedRefund();

    public AdminPayoutReferenceCorrectionRouteTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { new(_escrow) } });
        _db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<GetItemRequest>();
            // The old reference's item names this leg.
            return new GetItemResponse
            {
                Item = request.TableName == IdempotencyTable && request.Key["idempotencyKey"].S == "payout-ref#TB-B-PAYMENT"
                    ? new Dictionary<string, AttributeValue>
                    {
                        ["idempotencyKey"] = S("payout-ref#TB-B-PAYMENT"),
                        ["escrowId"] = S(EscrowId),
                        ["ref"] = S("buyer")
                    }
                    : new Dictionary<string, AttributeValue>()
            };
        });
        _db.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(_transactions.Add), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _db.PutItemAsync(Arg.Do<PutItemRequest>(_puts.Add), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static Dictionary<string, AttributeValue> RecordedRefund() => new()
    {
        ["escrowId"] = S(EscrowId),
        ["createdAt"] = S("2026-10-01T10:00:00Z"),
        ["orderId"] = S("ord-7"),
        ["buyerId"] = S("buyer-7"),
        ["farmerId"] = S("farmer-7"),
        ["status"] = S("refunded"),
        ["version"] = new AttributeValue { N = "6" },
        ["amount"] = new AttributeValue { N = "20000" },
        ["telebirrRefundRef"] = S("TB-B-PAYMENT"),
        ["manualPayoutOp"] = S("refund"),
        ["manualBuyerStatus"] = S("paid"),
        ["manualBuyerRef"] = S("TB-B-PAYMENT"),
        ["manualBuyerPaidBy"] = S("admin-2"),
        ["manualBuyerPaidAt"] = S("2026-10-02T09:00:00Z"),
    };

    private AdminFunction Function(string idempotencyTable = IdempotencyTable) =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable, idempotencyTable);

    private static APIGatewayProxyRequest Post(string leg = "buyer", object? body = null, bool adminGroup = true) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/payouts/{EscrowId}/{leg}/correct",
        Body = JsonSerializer.Serialize(body ?? new { oldExternalRef = "TB-B-PAYMENT", externalRef = "TB-A-PAYMENT", reason = Reason }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = adminGroup
                ? new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
                : new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        },
    };

    private List<PutItemRequest> TxAudits => _puts.Where(p => p.TableName == TxAuditTable).ToList();

    [Fact]
    public async Task Correct_WritesOneTransaction_AndOneAdminAuditRow()
    {
        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"externalRef\":\"TB-A-PAYMENT\"")
            .And.Contain("\"oldExternalRef\":\"TB-B-PAYMENT\"")
            .And.Contain("\"correctedBy\":\"admin-1\"")
            .And.Contain("\"alreadyCorrected\":false");

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(4);
        items[0].Update.TableName.Should().Be(EscrowsTable);
        items[0].Update.ExpressionAttributeNames["#legRef"].Should().Be("manualBuyerRef");
        items[0].Update.ExpressionAttributeNames["#settlementRef"].Should().Be("telebirrRefundRef");
        items[0].Update.ExpressionAttributeValues[":paidBy"].S.Should().Be("admin-2");
        items[0].Update.ExpressionAttributeValues[":v"].N.Should().Be("6");
        items[1].Delete.Key["idempotencyKey"].S.Should().Be("payout-ref#TB-B-PAYMENT");
        items[2].Put.Item["idempotencyKey"].S.Should().Be("payout-ref#TB-A-PAYMENT");
        items[2].Put.Item["recordedBy"].S.Should().Be("admin-1");
        items[3].Put.TableName.Should().Be(AuditTable);
        items[3].Put.Item["action"].S.Should().Be("manual_payout_ref_corrected");
        items[3].Put.Item["actor"].S.Should().Be("admin-1");

        var txAudit = TxAudits.Should().ContainSingle().Subject;
        txAudit.Item["action"].S.Should().Be("admin_manual_payout_ref_corrected");
        txAudit.Item["metadata"].S.Should().Be($"leg=buyer;oldRef=TB-B-PAYMENT;newRef=TB-A-PAYMENT;reason={Reason}");
        // Nothing else is written outside the transaction: no escrow update, no other audit row.
        await _db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        _puts.Should().OnlyContain(p => p.TableName == TxAuditTable);
    }

    [Fact]
    public async Task Correct_Replayed_AnswersAlreadyCorrected_AndWritesNothing()
    {
        _escrow["manualBuyerRef"] = S("TB-A-PAYMENT");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"alreadyCorrected\":true");
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task Correct_WithoutTheAdminGroup_Is403_AndWritesNothing()
    {
        var response = await Function().FunctionHandler(Post(adminGroup: false), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Correct_WithoutAReason_Is400OnTheReasonField()
    {
        var response = await Function().FunctionHandler(
            Post(body: new { oldExternalRef = "TB-B-PAYMENT", externalRef = "TB-A-PAYMENT", reason = "short" }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_048").And.Contain("\"field\":\"reason\"");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Correct_WithABodyThatIsNotJson_Is400()
    {
        var request = Post();
        request.Body = "{not json";

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
    }

    [Fact]
    public async Task Correct_WithoutTheReferenceTable_Is503()
    {
        var response = await Function(idempotencyTable: "").FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownPayoutAction_IsStill404()
    {
        var request = Post();
        request.Path = $"/admin/payouts/{EscrowId}/buyer/rewrite";

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_002");
    }

    [Fact]
    public void TheAdminEscrowDto_ListsThe1xManualLegs_OpenOrClosed()
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            [ManualPayoutFields.StatusAttr("farmer")] = "paid",
            [ManualPayoutFields.RefAttr("farmer")] = "GW-1",
            [ManualPayoutFields.PaidByAttr("farmer")] = "gateway",
            [ManualPayoutFields.PaidAtAttr("farmer")] = "2026-10-02T09:00:00Z",
            [ManualPayoutFields.StatusAttr("trucker")] = "paid",
            [ManualPayoutFields.RefAttr("trucker")] = "TB-T-1",
            [ManualPayoutFields.PaidByAttr("trucker")] = "admin-2",
            [ManualPayoutFields.PaidAtAttr("trucker")] = "2026-10-02T10:00:00Z"
        };

        var legs = AdminFunction.ManualLegsOf(item);

        legs.Should().HaveCount(2);
        legs![0].Ref.Should().Be("farmer");
        legs[0].PaidBy.Should().Be("gateway");
        legs[1].Ref.Should().Be("trucker");
        legs[1].Status.Should().Be("paid");
        legs[1].ExternalRef.Should().Be("TB-T-1");
        legs[1].PaidBy.Should().Be("admin-2");
        AdminFunction.ManualLegsOf(new Dictionary<string, object> { ["escrowId"] = EscrowId }).Should().BeNull();
    }
}
