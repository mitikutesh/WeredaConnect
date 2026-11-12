using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-24 against real DynamoDB (LocalStack in CI): the admin's read routes as DynamoDB answers them. The ledger
/// page is a Query on the escrow's partition whose token resumes after the last row; the stuck orders come
/// from <c>AdminQueueIndex</c> and from <c>DeadlineIndex</c> (keys only, so each order is read again), and the
/// tokens of both indexes round-trip through the route.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminLegsDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly TestLambdaContext _context = new();

    public AdminLegsDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;
    private OrderRepository Orders => new(Ddb, _db.Orders);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private AdminFunction Function() => new(Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit)
    {
        LedgerTable = _db.EscrowLedger
    };

    private async Task<JsonElement> GetAsync(string path, Dictionary<string, string>? query = null)
    {
        var response = await Function().FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
            }
        }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement;
    }

    /// <summary>Every stuck order of <paramref name="source"/>, following the route's tokens to the end of the index.</summary>
    private async Task<List<JsonElement>> AllStuckAsync(string source)
    {
        var found = new List<JsonElement>();
        string? token = null;
        for (var page = 0; page < 200; page++)
        {
            var query = new Dictionary<string, string> { ["source"] = source, ["limit"] = "2" };
            if (token != null)
            {
                query["nextToken"] = token;
            }

            var body = await GetAsync("/admin/orders/queue", query);
            found.AddRange(body.GetProperty("orders").EnumerateArray().Select(o => o.Clone()));
            if (!body.TryGetProperty("nextToken", out var next) || next.ValueKind != JsonValueKind.String)
            {
                return found;
            }

            token = next.GetString();
        }

        throw new InvalidOperationException("The stuck order pages never ended.");
    }

    private async Task<string> SeedOrderAsync(string label, string status)
    {
        var orderId = $"order-esc24-{label}-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = orderId },
                ["createdAt"] = new() { S = CreatedAt },
                ["updatedAt"] = new() { S = CreatedAt },
                ["status"] = new() { S = status },
                ["version"] = new() { N = "2" },
                ["buyerId"] = new() { S = "buyer-esc24" },
                ["buyerName"] = new() { S = "Buyer" },
                ["farmerId"] = new() { S = "farmer-esc24" },
                ["farmerName"] = new() { S = "Farmer" },
                ["cropType"] = new() { S = "teff" },
                ["orderTotal"] = new() { N = "1000" }
            }
        });
        return orderId;
    }

    [LocalDynamoDbFact]
    public async Task TheLedgerRoute_PagesAnEscrowsRows_InRefOrder_AndTheTokenResumesAfterTheLastRow()
    {
        var escrowId = $"escrow-order-esc24-{Guid.NewGuid():N}"[..48];
        await new EscrowRepository(Ddb, _db.Escrows).CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-esc24",
            ["farmerId"] = "farmer-esc24",
            ["amount"] = 1_000m,
            ["farmerPayout"] = 950m,
            ["commission"] = 50m,
            ["currency"] = "ETB",
            ["source"] = "order",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 1,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(950m, 0m, 50m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        });
        foreach (var payment in new[] { "pay-c", "pay-a", "pay-b" })
        {
            var reference = LedgerRefs.RefundDuplicate(payment);
            var written = await Ledger.PutRowAsync(
                new LedgerRow
                {
                    EscrowId = escrowId,
                    Ref = reference,
                    Kind = LedgerFields.KindRefund,
                    PayeeRole = LedgerFields.PayeeBuyer,
                    PayeeId = "buyer-esc24",
                    Amount = 1_000m,
                    Status = LedgerFields.StatusPending,
                    GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
                    Reason = "duplicate_payment",
                    Actor = "system",
                    CreatedAt = CreatedAt,
                    UpdatedAt = CreatedAt,
                    DueAt = CreatedAt
                },
                new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(CreatedAt, reference, "ledger_row_pending"),
                    "ledger_row_pending", "", LedgerFields.StatusPending, "system", $"ref={reference}"));
            written.Should().Be(LedgerWrite.Written);
        }

        var first = await GetAsync($"/admin/escrows/{escrowId}/ledger", new() { ["limit"] = "2" });

        first.GetProperty("settlementModel").GetString().Should().Be("ledger");
        first.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("ref").GetString())
            .Should().Equal("refund#duplicate#pay-a", "refund#duplicate#pay-b");
        var row = first.GetProperty("rows")[0];
        row.GetProperty("amount").GetDecimal().Should().Be(1_000m);
        row.GetProperty("status").GetString().Should().Be("pending");
        row.GetProperty("payeeRole").GetString().Should().Be("buyer");
        row.GetProperty("gatewayRef").GetString().Should().StartWith("WCL");

        var second = await GetAsync($"/admin/escrows/{escrowId}/ledger",
            new() { ["limit"] = "2", ["nextToken"] = first.GetProperty("nextToken").GetString()! });

        second.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("ref").GetString())
            .Should().Equal("refund#duplicate#pay-c");
        second.TryGetProperty("nextToken", out _).Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task TheStuckOrders_OfTheAdminQueue_ComeFromAdminQueueIndex_AcrossPages()
    {
        var queued = await SeedOrderAsync("queued", "accepted");
        var other = await SeedOrderAsync("queued2", "accepted");
        var notQueued = await SeedOrderAsync("plain", "accepted");
        (await Orders.TryEnterAdminQueueAsync(queued, AdminQueueFields.ReasonHoldCap, $"escrow-order-{queued}", "2026-10-22T10:00:00.0000000Z", "accepted"))
            .Should().BeTrue();
        (await Orders.TryEnterAdminQueueAsync(other, AdminQueueFields.ReasonHoldCap, $"escrow-order-{other}", "2026-10-23T10:00:00.0000000Z", "accepted"))
            .Should().BeTrue();

        var stuck = await AllStuckAsync("queue");

        var ids = stuck.Select(o => o.GetProperty("orderId").GetString()).ToList();
        ids.Should().Contain(queued).And.Contain(other).And.NotContain(notQueued);
        ids.Should().OnlyHaveUniqueItems("a token never answers a page twice");
        ids.IndexOf(queued).Should().BeLessThan(ids.IndexOf(other), "oldest entry first");
        var mine = stuck.Single(o => o.GetProperty("orderId").GetString() == queued);
        mine.GetProperty("reason").GetString().Should().Be("hold_cap");
        mine.GetProperty("stuckSince").GetString().Should().Be("2026-10-22T10:00:00.0000000Z");
        mine.GetProperty("escrowId").GetString().Should().Be($"escrow-order-{queued}");
        mine.GetProperty("buyerName").GetString().Should().Be("Buyer");
        mine.GetProperty("ageHours").GetInt32().Should().BeGreaterThan(24);
    }

    [LocalDynamoDbFact]
    public async Task TheStuckOrders_OfADeadline_AreTheOnesWhoseOwnDeadlinePassed_ReadFromDeadlineIndex()
    {
        var now = DateTime.UtcNow;
        // The farmer never answered and the sweep did not act: 2 hours past the deadline.
        var late = await SeedOrderAsync("late", "payment_submitted");
        (await Orders.TryMarkFundedAsync(late, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(-2)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now.AddHours(-26)) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        })).Should().NotBeNull();
        // Only the reminder is late: the farmer still has 5 hours.
        var reminded = await SeedOrderAsync("reminder", "payment_submitted");
        (await Orders.TryMarkFundedAsync(reminded, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(5)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now.AddHours(-19)) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        })).Should().NotBeNull();
        // A row the sweep found stuck and pushed 40 minutes ahead for its retry is still listed.
        var retried = await SeedOrderAsync("retried", "payment_submitted");
        var funded = await Orders.TryMarkFundedAsync(retried, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(-4)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now.AddHours(-28)) },
            { OrderCommitment.FarmerReminderAtField, OrderCommitment.Iso(now.AddHours(-10)) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        });
        (await Orders.TrySetDeadlineAsync(retried, CreatedAt, int.Parse((string)funded!["version"]),
            new OrderDeadline(now.AddMinutes(40), OrderDeadlines.Kind.FarmerResponse))).Should().BeTrue();

        var stuck = await AllStuckAsync("deadline");

        var ids = stuck.Select(o => o.GetProperty("orderId").GetString()).ToList();
        ids.Should().Contain(late).And.Contain(retried).And.NotContain(reminded);
        ids.Should().OnlyHaveUniqueItems();
        var mine = stuck.Single(o => o.GetProperty("orderId").GetString() == late);
        mine.GetProperty("reason").GetString().Should().Be("farmer_response");
        mine.GetProperty("stuckSince").GetString().Should().Be(OrderCommitment.Iso(now.AddHours(-2)));
        mine.GetProperty("ageHours").GetInt32().Should().Be(2);
        mine.GetProperty("status").GetString().Should().Be("funded");
        mine.GetProperty("farmerName").GetString().Should().Be("Farmer");
    }

    [LocalDynamoDbFact]
    public async Task TheStuckOrders_OfADeadline_ListAnAcceptedOrderPastItsPickupWithTheNoticeSent_UntilItsPickup()
    {
        // FUP-170: past its pickup deadline, the overdue notice sent, nobody picked up or cancelled.
        var now = DateTime.UtcNow;
        var pickupBy = now.AddHours(-5);
        var waiting = await SeedOrderAsync("waiting", "payment_submitted");
        (await Orders.TryMarkFundedAsync(waiting, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(20)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now.AddHours(-30)) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        })).Should().NotBeNull();
        (await Orders.UpdateAsync(waiting, new Dictionary<string, object>
        {
            { "status", "accepted" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted },
            { PickupDeadline.Field, PickupDeadline.Format(pickupBy) }
        }, 3)).IsSuccess.Should().BeTrue();
        (await Orders.TryStampAsync(waiting,
            new Dictionary<string, object> { { OrderCommitment.PickupOverdueNotifiedAtField, OrderCommitment.Iso(pickupBy.AddMinutes(15)) } },
            new Dictionary<string, string> { { "status", "accepted" } })).Should().BeTrue();
        // A missed farmer answer in the sweep's partition, so the walk crosses from one partition to the other.
        var late = await SeedOrderAsync("late", "payment_submitted");
        (await Orders.TryMarkFundedAsync(late, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(-2)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now.AddHours(-26)) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        })).Should().NotBeNull();

        var stuck = await AllStuckAsync("deadline");

        var ids = stuck.Select(o => o.GetProperty("orderId").GetString()).ToList();
        ids.Should().Contain(waiting).And.Contain(late);
        ids.Should().OnlyHaveUniqueItems("the tokens cross from one partition to the other without repeating a row");
        ids.IndexOf(late).Should().BeLessThan(ids.IndexOf(waiting), "the sweep's partition first, then the waits");
        var mine = stuck.Single(o => o.GetProperty("orderId").GetString() == waiting);
        mine.GetProperty("reason").GetString().Should().Be("pickup_waiting");
        mine.GetProperty("stuckSince").GetString().Should().Be(PickupDeadline.Format(pickupBy));
        mine.GetProperty("ageHours").GetInt32().Should().Be(5);
        mine.GetProperty("status").GetString().Should().Be("accepted");

        // The pickup takes it off the list.
        (await Orders.UpdateAsync(waiting, new Dictionary<string, object> { { "status", "pickup_confirmed" } }, 4))
            .IsSuccess.Should().BeTrue();
        (await AllStuckAsync("deadline")).Select(o => o.GetProperty("orderId").GetString()).Should().NotContain(waiting);
    }
}
