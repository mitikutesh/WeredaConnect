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
/// FUP-287 against real DynamoDB (LocalStack in CI), what the mocked route tests cannot show: the re-open's one
/// transaction on the real orders, deliveries and escrows tables (SET and REMOVE together, the conditions on the
/// fields written without a version bump, the escrow check), the <c>DeadlineIndex</c> entry the repository derives
/// for the new deadline, the admin queue entry leaving <c>AdminQueueIndex</c>, the replay, and the order cron judging
/// a re-opened order again from its kept check-ins.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PickupReopenDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T09:00:00.0000000Z";
    private const string Reason = "Called both parties: the crops left the farm on time.";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly TestLambdaContext _context = new();

    public PickupReopenDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private DeliveryRepository Deliveries => new(Ddb, _db.Deliveries);

    private string Id(string name) => $"{name}-fup287-{_run}";

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private AdminFunction Admin(DateTime? now = null) =>
        new(Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit)
        {
            Clock = () => now ?? DateTime.UtcNow
        };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue Entry(string by, DateTime receivedAt) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            [PickupJudgement.EntryReceivedAt] = S(Iso(receivedAt)),
            [PickupJudgement.EntryBy] = S(by),
            [PickupJudgement.EntryLatitude] = new() { N = "8.7601" },
            [PickupJudgement.EntryLongitude] = new() { N = "38.9785" },
            [PickupJudgement.EntryDistanceKm] = new() { N = "0.87" },
            [PickupJudgement.EntryDistanceChecked] = new() { BOOL = true },
            [PickupJudgement.EntryLate] = new() { BOOL = false }
        }
    };

    /// <summary>
    /// An order the cron judged both present, nothing scanned, two days ago: accepted, in the admin queue, waiting in
    /// the watch partition; its delivery pending and its escrow escrowed. The index entry is derived by the repository.
    /// </summary>
    private async Task<string> SeedJudgedAsync(string queueReason = "both_present_no_scan", string? refundReason = null)
    {
        var orderId = Id("order");
        var deadline = DateTime.UtcNow.AddDays(-2);
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("accepted"),
                ["version"] = new() { N = "6" },
                ["buyerId"] = S(Id("buyer")),
                ["farmerId"] = S(Id("farmer")),
                ["truckerId"] = S(Id("trucker")),
                ["tripId"] = S(Id("trip")),
                ["listingId"] = S(Id("listing")),
                ["quantity"] = new() { N = "500" },
                ["activeDeliveryId"] = S(Id("delivery")),
                [OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.Trucker),
                [PickupDeadline.Field] = S(Iso(deadline)),
                [PickupJudgement.Field] = S(PickupJudgement.Outcome.Pending),
                [PickupJudgement.PickupLatitudeField] = new() { N = "8.7523" },
                [PickupJudgement.PickupLongitudeField] = new() { N = "38.9785" },
                [PickupJudgement.CheckInsField] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.Kind.ArrivedPickup] = Entry(Id("trucker"), deadline.AddHours(-20)),
                        [PickupJudgement.Kind.CropsReady] = Entry(Id("farmer"), deadline.AddHours(-21))
                    }
                }
            }
        });

        // The judgement as the cron writes it (a stamp: no version bump), then its queue entry.
        var judgedAt = Iso(deadline.AddMinutes(1));
        (await Repo.TryStampAsync(orderId,
            new Dictionary<string, object>
            {
                { PickupJudgement.Field, PickupJudgement.Outcome.BothPresentNoScan },
                { PickupJudgement.JudgedAtField, judgedAt },
                { OrderCommitment.PickupOverdueNotifiedAtField, judgedAt }
            },
            new Dictionary<string, string> { { "status", "accepted" } })).Should().BeTrue();
        (await Repo.TryEnterAdminQueueAsync(orderId, queueReason, $"escrow-order-{orderId}", judgedAt, "accepted")).Should().BeTrue();

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(Id("delivery")),
                ["tripId"] = S(Id("trip")),
                ["orderId"] = S(orderId),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S("pending"),
                ["version"] = new() { N = "2" },
                ["createdAt"] = S(CreatedAt)
            }
        });

        // The trip is still there: the order cron would otherwise fail the transport for a trip that is gone.
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = S(Id("trip")),
                ["createdAt"] = S(CreatedAt),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S("matched"),
                ["version"] = new() { N = "2" }
            }
        });

        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S($"escrow-order-{orderId}"),
            ["createdAt"] = S(CreatedAt),
            ["orderId"] = S(orderId),
            ["status"] = S("escrowed"),
            ["amount"] = new() { N = "17250" },
            ["version"] = new() { N = "2" }
        };
        if (refundReason != null)
        {
            escrow["refundReason"] = S(refundReason);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = escrow });
        return orderId;
    }

    private async Task<Dictionary<string, object>> ReadAsync(string orderId)
        => (await Repo.GetByIdAsync(orderId, consistentRead: true))!;

    private static APIGatewayProxyRequest Post(string orderId, object body) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/orders/{orderId}/reopen-pickup",
        Body = JsonSerializer.Serialize(body),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
        }
    };

    private static object Pending(int hours = 24) => new { exit = "pending", reason = Reason, expectedVersion = 6, deadlineHours = hours };

    private async Task<bool> InAdminQueueAsync(string orderId)
    {
        var page = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.Orders,
            IndexName = AdminQueueFields.IndexName,
            KeyConditionExpression = "#m = :open",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#m", AdminQueueFields.Marker } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { ":open", S(AdminQueueFields.MarkerOpen) } }
        });
        return page.Items.Any(i => i["orderId"].S == orderId);
    }

    [LocalDynamoDbFact]
    public async Task Pending_ReopensTheJudgement_InTheSweptPartition_AndAReplayWritesNothing()
    {
        var orderId = await SeedJudgedAsync();
        var before = await ReadAsync(orderId);
        OrderDeadlines.Stored(before)!.Value.Kind.Should().Be(OrderDeadlines.Kind.PickupWaiting);
        before[OrderDeadlines.ShardField].Should().Be(OrderDeadlines.WatchShard);
        (await InAdminQueueAsync(orderId)).Should().BeTrue();

        var now = DateTime.UtcNow;
        var response = await Admin(now).FunctionHandler(Post(orderId, Pending()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var after = await ReadAsync(orderId);
        after["status"].Should().Be("accepted");
        after["version"].Should().Be("7");
        after[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending);
        after[PickupDeadline.Field].Should().Be(PickupDeadline.Format(now.AddHours(24)));
        after.Should().NotContainKey(PickupJudgement.JudgedAtField)
            .And.NotContainKey(OrderCommitment.PickupOverdueNotifiedAtField)
            .And.NotContainKey(AdminQueueFields.Marker)
            .And.NotContainKey(AdminQueueFields.Reason);
        PickupJudgement.EntryOf(after, PickupJudgement.Kind.ArrivedPickup).Should().NotBeNull("the check-ins are kept");
        var stored = OrderDeadlines.Stored(after)!.Value;
        stored.Kind.Should().Be(OrderDeadlines.Kind.PickupJudgement);
        stored.AtIso.Should().Be(Iso(now.AddHours(24) + PickupJudgement.SettleMargin));
        after[OrderDeadlines.ShardField].Should().Be(OrderDeadlines.Shard, "the judgement is the sweep's again");
        (await InAdminQueueAsync(orderId)).Should().BeFalse();
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("pending");

        var audit = await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(orderId);
        audit.Should().ContainSingle(a => (string)a["action"] == PickupReopen.AuditAction);

        var replay = await Admin().FunctionHandler(Post(orderId, Pending()), _context);
        replay.StatusCode.Should().Be(200, replay.Body);
        replay.Body.Should().Contain("\"alreadyApplied\":true");
        (await ReadAsync(orderId))["version"].Should().Be("7");
    }

    [LocalDynamoDbFact]
    public async Task HandoffConfirmed_PicksTheDeliveryUp_AndTheOrderLeavesTheIndexAndTheQueue()
    {
        var orderId = await SeedJudgedAsync(queueReason: PickupJudgement.ReasonLateEvidence);

        var response = await Admin().FunctionHandler(
            Post(orderId, new { exit = "handoff_confirmed", reason = Reason, expectedVersion = 6 }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var after = await ReadAsync(orderId);
        after["status"].Should().Be("pickup_confirmed");
        after["version"].Should().Be("7");
        after[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Scanned);
        after.Should().ContainKey(PickupJudgement.JudgedAtField);
        OrderDeadlines.Stored(after).Should().BeNull();
        after.Should().NotContainKey(OrderDeadlines.ShardField);
        (await InAdminQueueAsync(orderId)).Should().BeFalse();

        var delivery = (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!;
        delivery["status"].Should().Be("pickedUp");
        delivery["version"].Should().Be("3");
        delivery.Should().ContainKey("pickedUpAt").And.NotContainKey("gpsPickup");

        var actions = (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(orderId))
            .Select(a => (string)a["action"]).ToList();
        actions.Should().Contain(PickupReopen.AuditAction).And.Contain("picked_up");
    }

    [LocalDynamoDbFact]
    public async Task ARefundArmed_RefusesTheReopen_AndNothingMoves()
    {
        var orderId = await SeedJudgedAsync(refundReason: "order_cancelled");

        var response = await Admin().FunctionHandler(Post(orderId, Pending()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);
        var after = await ReadAsync(orderId);
        after["version"].Should().Be("6");
        after[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.BothPresentNoScan);
        (await InAdminQueueAsync(orderId)).Should().BeTrue();
    }

    [LocalDynamoDbFact]
    public async Task TheTransaction_RefusesAnEscrowThatChanged_AndAJudgementOrQueueEntryThatMoved()
    {
        var orderId = await SeedJudgedAsync();
        var escrowKey = new EscrowRowKey(_db.Escrows, $"escrow-order-{orderId}", CreatedAt);
        var set = new Dictionary<string, object> { { PickupJudgement.Field, PickupJudgement.Outcome.Pending } };

        // A refund armed between the admin's read and the write.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(escrowKey.EscrowId), ["createdAt"] = S(CreatedAt) },
            UpdateExpression = "SET refundReason = :r",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = S("order_cancelled") }
        });
        var refused = await Repo.TryUpdateGuardedAsync(orderId, set, Array.Empty<string>(), 6,
            new Dictionary<string, string> { { PickupJudgement.Field, PickupJudgement.Outcome.BothPresentNoScan } },
            new[] { OrderRepository.EscrowHoldsMoneyCheck(escrowKey) });
        refused.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);

        // A judgement that is not the one read (a field written without a version bump).
        var moved = await Repo.TryUpdateGuardedAsync(orderId, set, Array.Empty<string>(), 6,
            new Dictionary<string, string> { { PickupJudgement.Field, PickupJudgement.Outcome.UncheckedCheckIn } },
            Array.Empty<TransactWriteItem>());
        moved.Outcome.Should().Be(OrderLinkedWriteOutcome.OrderChanged);

        (await ReadAsync(orderId))["version"].Should().Be("6");
    }

    [LocalDynamoDbFact]
    public async Task AHoldCapEntry_Stays_WhenTheJudgementIsReopened()
    {
        var orderId = await SeedJudgedAsync(queueReason: AdminQueueFields.ReasonHoldCap);

        var response = await Admin().FunctionHandler(Post(orderId, Pending()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        (await ReadAsync(orderId))[AdminQueueFields.Reason].Should().Be(AdminQueueFields.ReasonHoldCap);
        (await InAdminQueueAsync(orderId)).Should().BeTrue();
    }

    [LocalDynamoDbFact]
    public async Task TheCron_JudgesAReopenedOrderAgain_FromItsKeptCheckIns()
    {
        var orderId = await SeedJudgedAsync();
        // Re-opened a day ago for one hour: its new deadline has passed.
        var reopened = await Admin(DateTime.UtcNow.AddHours(-25)).FunctionHandler(Post(orderId, Pending(hours: 1)), _context);
        reopened.StatusCode.Should().Be(200, reopened.Body);

        var deliveries = Deliveries;
        var cron = new OrderCronFunction(Repo, new TransactionAuditRepository(Ddb, _db.TransactionAudit), inboxRepo: null,
            escrowService: new EscrowService(new EscrowRepository(Ddb, _db.Escrows), new EscrowAuditRepository(Ddb, _db.EscrowAudit),
                idempotencyRepo: null, telebirrGateway: null, orderRepo: Repo, deliveryRepo: deliveries),
            cropRepo: new CropRepository(Ddb, _db.Crops), tripRepo: new TripRepository(Ddb, _db.Trips), deliveryRepo: deliveries)
        {
            CommitmentsEnabled = true,
            PickupClassificationEnabled = true
        };

        await cron.FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, new TestLambdaContext());

        // Both check-ins came before the new deadline too: both present again, back in the admin queue, money held.
        var judged = await ReadAsync(orderId);
        judged["status"].Should().Be("accepted");
        judged[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.BothPresentNoScan);
        judged.Should().ContainKey(PickupJudgement.JudgedAtField);
        judged[AdminQueueFields.Reason].Should().Be(PickupJudgement.ReasonBothPresent);
        OrderDeadlines.Stored(judged)!.Value.Kind.Should().Be(OrderDeadlines.Kind.PickupWaiting);
    }
}
