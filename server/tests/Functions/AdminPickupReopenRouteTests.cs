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
/// FUP-287: <c>POST /admin/orders/{orderId}/reopen-pickup</c> through the admin Lambda with the DynamoDB requests
/// captured (one transaction, its conditions, the deadline the repository derives, the audit rows), the refusals
/// before any write, the replay, and the evidence on <c>GET /admin/transactions/{orderId}</c>.
/// </summary>
public class AdminPickupReopenRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string OrderId = "ord-9";
    private const string QueuedAt = "2026-11-22T06:01:30.0000000Z";
    private const string Reason = "Called both parties: the crops left the farm on the 21st.";
    private static readonly DateTime Now = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();
    private readonly List<PutItemRequest> _puts = new();
    private Dictionary<string, AttributeValue> _order = Judged();
    private Dictionary<string, AttributeValue>? _delivery = PendingDelivery();
    private Dictionary<string, AttributeValue>? _escrow = HeldEscrow();

    public AdminPickupReopenRouteTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            Dictionary<string, AttributeValue>? row = request.IndexName == "OrderIndex" ? _escrow
                : request.TableName == OrdersTable ? _order
                : request.TableName == DeliveriesTable ? _delivery
                : request.TableName == EscrowsTable ? _escrow
                : null;
            return new QueryResponse
            {
                Items = row == null ? new List<Dictionary<string, AttributeValue>>() : new List<Dictionary<string, AttributeValue>> { new(row) },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            };
        });
        _db.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(_transactions.Add), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _db.PutItemAsync(Arg.Do<PutItemRequest>(_puts.Add), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        _db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
            });
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    /// <summary>Both checked in, nothing scanned: accepted, judged, in the admin queue, waiting in the watch partition.</summary>
    private static Dictionary<string, AttributeValue> Judged(string judgement = PickupJudgement.Outcome.BothPresentNoScan) => new()
    {
        ["orderId"] = S(OrderId),
        ["createdAt"] = S("2026-11-10T09:00:00.0000000Z"),
        ["status"] = S("accepted"),
        ["version"] = N("7"),
        ["buyerId"] = S("buyer-9"),
        ["farmerId"] = S("farmer-9"),
        ["truckerId"] = S("trucker-9"),
        ["tripId"] = S("trip-9"),
        ["activeDeliveryId"] = S("del-9"),
        [OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.Trucker),
        [PickupDeadline.Field] = S("2026-11-22T06:00:00.0000000Z"),
        [PickupJudgement.Field] = S(judgement),
        [PickupJudgement.JudgedAtField] = S("2026-11-22T06:01:00.0000000Z"),
        [OrderCommitment.PickupOverdueNotifiedAtField] = S("2026-11-22T06:01:00.0000000Z"),
        [PickupJudgement.PickupLatitudeField] = N("8.7523"),
        [PickupJudgement.PickupLongitudeField] = N("38.9785"),
        [PickupJudgement.CheckInsField] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [PickupJudgement.Kind.ArrivedPickup] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.EntryReceivedAt] = S("2026-11-21T07:00:00.0000000Z"),
                        [PickupJudgement.EntryBy] = S("trucker-9"),
                        [PickupJudgement.EntryLatitude] = N("8.7601"),
                        [PickupJudgement.EntryLongitude] = N("38.9785"),
                        [PickupJudgement.EntryAccuracyM] = N("35"),
                        [PickupJudgement.EntryDistanceKm] = N("0.87"),
                        [PickupJudgement.EntryDistanceChecked] = new AttributeValue { BOOL = true },
                        [PickupJudgement.EntryLate] = new AttributeValue { BOOL = false }
                    }
                }
            }
        },
        [AdminQueueFields.Marker] = S(AdminQueueFields.MarkerOpen),
        [AdminQueueFields.QueuedAt] = S(QueuedAt),
        [AdminQueueFields.Reason] = S(PickupJudgement.ReasonBothPresent),
        [AdminQueueFields.EscrowId] = S($"escrow-order-{OrderId}"),
        [OrderDeadlines.AtField] = S("2026-11-22T06:00:00.0000000Z"),
        [OrderDeadlines.KindField] = S(OrderDeadlines.Kind.PickupWaiting),
        [OrderDeadlines.ShardField] = S(OrderDeadlines.WatchShard)
    };

    private static Dictionary<string, AttributeValue> PendingDelivery() => new()
    {
        ["deliveryId"] = S("del-9"),
        ["tripId"] = S("trip-9"),
        ["orderId"] = S(OrderId),
        ["buyerId"] = S("buyer-9"),
        ["farmerId"] = S("farmer-9"),
        ["truckerId"] = S("trucker-9"),
        ["status"] = S("pending"),
        ["version"] = N("2"),
        ["createdAt"] = S("2026-11-20T09:00:00.0000000Z")
    };

    private static Dictionary<string, AttributeValue> HeldEscrow() => new()
    {
        ["escrowId"] = S($"escrow-order-{OrderId}"),
        ["createdAt"] = S("2026-11-10T09:30:00.0000000Z"),
        ["orderId"] = S(OrderId),
        ["status"] = S("escrowed"),
        ["amount"] = N("17250"),
        ["version"] = N("3")
    };

    private AdminFunction Function() =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable) { Clock = () => Now };

    private static APIGatewayProxyRequest Post(object? body = null, bool adminGroup = true, string action = "reopen-pickup") => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/orders/{OrderId}/{action}",
        Body = JsonSerializer.Serialize(body ?? new { exit = "pending", reason = Reason, expectedVersion = 7, deadlineHours = 24 }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = adminGroup
                ? new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
                : new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private static object Handoff(int version = 7) => new { exit = "handoff_confirmed", reason = Reason, expectedVersion = version };

    private List<PutItemRequest> TxAudits => _puts.Where(p => p.TableName == TxAuditTable).ToList();

    /// <summary>The alias of an attribute the update sets (<c>#f</c>) or removes (<c>#r</c>); conditions use <c>#c</c>.</summary>
    private static string Name(Update update, string attribute, string prefix = "#f")
        => update.ExpressionAttributeNames.Single(n => n.Value == attribute && n.Key.StartsWith(prefix, StringComparison.Ordinal)).Key;

    [Fact]
    public async Task Pending_WritesOneTransaction_ThatReopensTheJudgementAtTheNewDeadline()
    {
        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"pickupJudgement\":\"pending\"")
            .And.Contain("\"pickupDeadlineAt\":\"2026-11-24T08:00:00.0000000Z\"")
            .And.Contain("\"fromJudgement\":\"both_present_no_scan\"")
            .And.Contain("\"version\":8")
            .And.Contain("\"alreadyApplied\":false");

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(2, "the order and the escrow check; no delivery is touched");
        var update = items[0].Update;
        update.TableName.Should().Be(OrdersTable);
        var values = update.ExpressionAttributeValues;
        values[":expectedVersion"].N.Should().Be("7");
        values[":newVersion"].N.Should().Be("8");
        values[":" + Name(update, PickupJudgement.Field)[1..]].S.Should().Be(PickupJudgement.Outcome.Pending);
        values[":" + Name(update, PickupDeadline.Field)[1..]].S.Should().Be("2026-11-24T08:00:00.0000000Z");
        var stamp = values[":" + Name(update, PickupReopen.Field)[1..]].M;
        stamp[PickupReopen.StampExit].S.Should().Be("pending");
        stamp[PickupReopen.StampFromVersion].N.Should().Be("7");
        stamp[PickupReopen.StampFromJudgement].S.Should().Be(PickupJudgement.Outcome.BothPresentNoScan);
        stamp.Should().NotContainKey("reason", "the reason goes only to the audit trail");

        // Judged again a minute after the new deadline, in the sweep's partition (derived, never written by hand).
        // FUP-286: the farmer has not checked in, so the new window's check-in reminder comes first, 6 hours before.
        values[":ndKind"].S.Should().Be(OrderDeadlines.Kind.PickupReminder);
        values[":ndShard"].S.Should().Be(OrderDeadlines.Shard);
        values[":ndAt"].S.Should().Be("2026-11-24T02:00:00.0000000Z");

        // The judgement's stamps, its queue entry and the first window's reminder go; the check-ins stay as evidence.
        foreach (var removed in new[]
                 {
                     PickupJudgement.JudgedAtField, OrderCommitment.PickupOverdueNotifiedAtField, PickupJudgement.ReminderAtField,
                     AdminQueueFields.Marker, AdminQueueFields.QueuedAt, AdminQueueFields.Reason, AdminQueueFields.EscrowId
                 })
        {
            update.UpdateExpression.Should().Contain(Name(update, removed, "#r"), removed);
        }

        update.ExpressionAttributeNames.Values.Should().NotContain(PickupJudgement.CheckInsField);
        update.UpdateExpression.Should().Contain(" REMOVE ");

        // Landed only while the order is as read: version, status, judgement and queue entry.
        update.ConditionExpression.Should().Contain("#v = :expectedVersion");
        var conditioned = update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#c", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => values[":" + n.Key[1..]].S);
        conditioned.Should().Equal(new Dictionary<string, string>
        {
            { "status", "accepted" },
            { PickupJudgement.Field, PickupJudgement.Outcome.BothPresentNoScan },
            { AdminQueueFields.QueuedAt, QueuedAt }
        });

        var check = items[1].ConditionCheck;
        check.TableName.Should().Be(EscrowsTable);
        check.Key["escrowId"].S.Should().Be($"escrow-order-{OrderId}");
        check.ConditionExpression.Should().Contain("#status = :escrowed").And.Contain("attribute_not_exists(#refundReason)")
            .And.Contain("attribute_not_exists(#holdCap)", "the entry goes, so the cap must not have been stamped since the read");
        check.ExpressionAttributeNames["#holdCap"].Should().Be(AdminQueueFields.HoldCapReachedAt);

        var audit = TxAudits.Should().ContainSingle().Subject;
        audit.Item["action"].S.Should().Be(PickupReopen.AuditAction);
        audit.Item["actor"].S.Should().Be("admin-1");
        audit.Item["fromState"].S.Should().Be("accepted");
        audit.Item["toState"].S.Should().Be("accepted");
        audit.Item["metadata"].S.Should().Be(
            $"exit=pending;judgement=both_present_no_scan;deadlineAt=2026-11-24T08:00:00.0000000Z;queue=removed;reason={Reason}");
        await _db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandoffConfirmed_MovesTheDeliveryAndTheOrderAsAScanWould_InTheSameTransaction()
    {
        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"status\":\"pickup_confirmed\"")
            .And.Contain("\"pickupJudgement\":\"scanned\"")
            .And.Contain("\"deliveryId\":\"del-9\"");

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(3);
        var order = items[0].Update;
        var values = order.ExpressionAttributeValues;
        values[":" + Name(order, "status")[1..]].S.Should().Be("pickup_confirmed");
        values[":" + Name(order, PickupJudgement.Field)[1..]].S.Should().Be(PickupJudgement.Outcome.Scanned);
        order.ExpressionAttributeNames.Values.Should().NotContain(PickupDeadline.Field, "the deadline is kept");
        // pickup_confirmed has no deadline of the index: the watch entry is removed.
        order.UpdateExpression.Should().Contain("#ndAt").And.Contain("REMOVE");
        values.Should().NotContainKey(":ndAt");

        var delivery = items[1].Update;
        delivery.TableName.Should().Be(DeliveriesTable);
        delivery.Key["deliveryId"].S.Should().Be("del-9");
        delivery.Key["tripId"].S.Should().Be("trip-9");
        delivery.ConditionExpression.Should().Be("#status = :pending AND #v = :expected");
        delivery.ExpressionAttributeValues[":expected"].N.Should().Be("2");
        delivery.ExpressionAttributeValues[":pickedUp"].S.Should().Be("pickedUp");
        delivery.ExpressionAttributeValues[":now"].S.Should().Be(OrderCommitment.Iso(Now));
        items[2].ConditionCheck.TableName.Should().Be(EscrowsTable);

        TxAudits.Select(a => a.Item["action"].S).Should().Equal(PickupReopen.AuditAction, "picked_up");
        TxAudits[0].Item["toState"].S.Should().Be("pickup_confirmed");
        TxAudits[0].Item["metadata"].S.Should().Be($"exit=handoff_confirmed;judgement=both_present_no_scan;deliveryId=del-9;queue=removed;reason={Reason}");
        TxAudits[1].Item["entityType"].S.Should().Be("delivery");
        TxAudits[1].Item["metadata"].S.Should().Be("handoff=admin");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ESC36_HandoffConfirmed_StampsTheDropoffJudgement_OnlyWithTheFlagOn(bool flag)
    {
        var function = new AdminFunction(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable)
        {
            Clock = () => Now,
            DropoffJudgementOn = flag
        };

        var response = await function.FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var order = _transactions.Should().ContainSingle().Subject.TransactItems[0].Update;
        var values = order.ExpressionAttributeValues;
        if (!flag)
        {
            order.ExpressionAttributeNames.Values.Should().NotContain(DropoffJudgement.Field);
            values.Should().NotContainKey(":ndAt");
            return;
        }

        values[":" + Name(order, DropoffJudgement.Field)[1..]].S.Should().Be(DropoffJudgement.Outcome.Pending);
        values[":" + Name(order, DropoffJudgement.DeadlineField)[1..]].S.Should().Be(OrderCommitment.Iso(Now.AddHours(72)));
        // T8 is the sweep's deadline now, instead of no deadline at pickup_confirmed.
        values[":ndKind"].S.Should().Be(OrderDeadlines.Kind.DeliveryOverdue);
        values[":ndShard"].S.Should().Be(OrderDeadlines.Shard);
    }

    [Fact]
    public async Task TheSameRequestAgain_AnswersAlreadyApplied_AndWritesNothing()
    {
        _order[PickupJudgement.Field] = S(PickupJudgement.Outcome.Pending);
        _order["version"] = N("8");
        _order[PickupReopen.Field] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [PickupReopen.StampExit] = S("pending"),
                [PickupReopen.StampFromVersion] = N("7"),
                [PickupReopen.StampFromJudgement] = S(PickupJudgement.Outcome.BothPresentNoScan),
                [PickupReopen.StampAt] = S("2026-11-23T07:59:00.0000000Z")
            }
        };

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"alreadyApplied\":true");
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task ALostRace_ThatTurnsOutToBeThisReopen_IsAReplay_AnythingElseIsChanged()
    {
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // Another tab's identical request landed first.
            _order[PickupJudgement.Field] = S(PickupJudgement.Outcome.Pending);
            _order["version"] = N("8");
            _order[PickupReopen.Field] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    [PickupReopen.StampExit] = S("pending"),
                    [PickupReopen.StampFromVersion] = N("7")
                }
            };
            return Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" } }
            });
        });

        var replay = await Function().FunctionHandler(Post(), _context);
        replay.StatusCode.Should().Be(200, replay.Body);
        replay.Body.Should().Contain("\"alreadyApplied\":true");
        TxAudits.Should().BeEmpty();

        _order = Judged();
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" } }
            }));

        var changed = await Function().FunctionHandler(Post(), _context);
        changed.StatusCode.Should().Be(409);
        changed.Body.Should().Contain(PickupReopen.ChangedCode).And.Contain("payment changed");
        TxAudits.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PickupJudgement.Outcome.TruckerNoShow, "awaiting_transport")]
    [InlineData(PickupJudgement.Outcome.FarmerNoShow, "cancelled")]
    [InlineData(PickupJudgement.Outcome.BuyerNoShow, "cancelled")]
    [InlineData(PickupJudgement.Outcome.Scanned, "accepted")]
    [InlineData(PickupJudgement.Outcome.Pending, "accepted")]
    public async Task AJudgementThatMovedSomethingOrNothingToReopen_IsRefused_BeforeAnyWrite(string judgement, string status)
    {
        _order = Judged(judgement);
        _order["status"] = S(status);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.NotAllowedCode);
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderAtAnotherVersion_IsChanged_BeforeAnyWrite()
    {
        var response = await Function().FunctionHandler(Post(new { exit = "pending", reason = Reason, expectedVersion = 6, deadlineHours = 24 }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.ChangedCode);
        _transactions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("held")]
    [InlineData("refunded")]
    [InlineData("disputed")]
    public async Task MoneyNoLongerSimplyHeld_RefusesTheReopen(string escrowStatus)
    {
        _escrow!["status"] = S(escrowStatus);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefundArmedOnTheEscrow_RefusesTheReopen()
    {
        _escrow!["refundReason"] = S("order_cancelled");

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderWithoutAnEscrowRecord_IsReopened_WithoutAMoneyCheck()
    {
        _escrow = null;

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        _transactions.Should().ContainSingle().Which.TransactItems.Should().ContainSingle();
    }

    [Fact]
    public async Task TheHandoff_IsRefusedForASelfPickup_AndForADeliveryThatIsNotPending()
    {
        _order[OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.SelfPickup);
        _order.Remove("truckerId");
        _order.Remove("tripId");
        var selfPickup = await Function().FunctionHandler(Post(Handoff()), _context);
        selfPickup.StatusCode.Should().Be(409);
        selfPickup.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("dispute verdict");

        _order = Judged();
        _delivery!["status"] = S("cancelled");
        var cancelled = await Function().FunctionHandler(Post(Handoff()), _context);
        cancelled.StatusCode.Should().Be(409);
        cancelled.Body.Should().Contain("only a pending delivery");

        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AHoldCapQueueEntry_IsAboutTheMoney_AndStays()
    {
        _order[AdminQueueFields.Reason] = S(AdminQueueFields.ReasonHoldCap);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var update = _transactions.Single().TransactItems[0].Update;
        update.ExpressionAttributeNames.Values.Should().NotContain(AdminQueueFields.Marker);
        update.ExpressionAttributeNames.Values.Should().NotContain(AdminQueueFields.QueuedAt);
    }

    [Fact]
    public async Task AHoldCapRidingOnTheJudgementsEntry_KeepsTheEntryAsHoldCap()
    {
        // The escrow cron found the judgement's entry naming this escrow, so it stamped the cap instead of an entry.
        _escrow![AdminQueueFields.HoldCapReachedAt] = S("2026-12-01T09:30:00.0000000Z");

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var items = _transactions.Single().TransactItems;
        var update = items[0].Update;
        update.ExpressionAttributeValues[":" + Name(update, AdminQueueFields.Reason)[1..]].S.Should().Be(AdminQueueFields.ReasonHoldCap);
        update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#r", StringComparison.Ordinal)).Select(n => n.Value)
            .Should().NotContain(new[] { AdminQueueFields.Marker, AdminQueueFields.QueuedAt, AdminQueueFields.EscrowId });
        var conditioned = update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#c", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => update.ExpressionAttributeValues[":" + n.Key[1..]].S);
        conditioned[AdminQueueFields.QueuedAt].Should().Be(QueuedAt, "the entry kept is the one read");
        items[2].ConditionCheck.ConditionExpression.Should().NotContain("#holdCap");
        TxAudits[0].Item["metadata"].S.Should().Contain(";queue=hold_cap;");
    }

    [Fact]
    public async Task ACappedEscrowWhoseEntryAnotherEscrowOwns_LeavesTheEntryAlone()
    {
        _escrow![AdminQueueFields.HoldCapReachedAt] = S("2026-12-01T09:30:00.0000000Z");
        _order[AdminQueueFields.EscrowId] = S("escrow-initiate-77");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var update = _transactions.Single().TransactItems[0].Update;
        update.ExpressionAttributeNames.Values.Should().NotContain(new[] { AdminQueueFields.Marker, AdminQueueFields.Reason });
        TxAudits[0].Item["metadata"].S.Should().Contain(";queue=kept;");
    }

    [Fact]
    public async Task AnOrderNotQueuedAtRead_TakesNoEntryThatLandsBeforeTheWrite()
    {
        foreach (var field in AdminQueueFields.All)
        {
            _order.Remove(field);
        }

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var update = _transactions.Single().TransactItems[0].Update;
        var absent = update.ExpressionAttributeNames.Single(n => n.Key.StartsWith("#a", StringComparison.Ordinal));
        absent.Value.Should().Be(AdminQueueFields.Marker);
        update.ConditionExpression.Should().Contain($"attribute_not_exists({absent.Key})");
        update.ExpressionAttributeNames.Values.Should().NotContain(AdminQueueFields.QueuedAt);
        TxAudits[0].Item["metadata"].S.Should().Contain(";queue=none;");
    }

    [Theory]
    [InlineData("{\"exit\":\"cancel\",\"reason\":\"Called both parties today.\",\"expectedVersion\":7}", "exit")]
    [InlineData("{\"exit\":\"pending\",\"reason\":\"   short   \",\"expectedVersion\":7,\"deadlineHours\":24}", "reason")]
    [InlineData("{\"exit\":\"pending\",\"reason\":\"Called both parties today.\",\"deadlineHours\":24}", "expectedVersion")]
    [InlineData("{\"exit\":\"pending\",\"reason\":\"Called both parties today.\",\"expectedVersion\":7}", "deadlineHours")]
    [InlineData("{\"exit\":\"pending\",\"reason\":\"Called both parties today.\",\"expectedVersion\":7,\"deadlineHours\":169}", "deadlineHours")]
    [InlineData("{\"exit\":\"handoff_confirmed\",\"reason\":\"Called both parties today.\",\"expectedVersion\":7,\"deadlineHours\":24}", "deadlineHours")]
    public async Task ABadRequest_NamesItsField_AndReadsNothing(string body, string field)
    {
        var request = Post();
        request.Body = body;

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain(PickupReopen.InvalidCode).And.Contain($"\"field\":\"{field}\"");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALongReason_IsRefused_AndTheReasonIsTrimmed()
    {
        var tooLong = await Function().FunctionHandler(
            Post(new { exit = "pending", reason = new string('a', 501), expectedVersion = 7, deadlineHours = 24 }), _context);
        tooLong.StatusCode.Should().Be(400);

        var padded = await Function().FunctionHandler(
            Post(new { exit = "pending", reason = $"  {Reason}  ", expectedVersion = 7, deadlineHours = 24 }), _context);
        padded.StatusCode.Should().Be(200, padded.Body);
        TxAudits.Single().Item["metadata"].S.Should().EndWith($";reason={Reason}");
    }

    [Fact]
    public async Task OnlyTheAdminGroup_MayReopen_AndOnlyThatActionExists()
    {
        var roleOnly = await Function().FunctionHandler(Post(adminGroup: false), _context);
        roleOnly.StatusCode.Should().Be(403);

        var other = await Function().FunctionHandler(Post(action: "cancel"), _context);
        other.StatusCode.Should().Be(404);
        other.Body.Should().Contain("ADMIN_002");

        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownOrder_Is404()
    {
        _order = new Dictionary<string, AttributeValue>();
        _db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_012");
    }

    // ── the evidence on the transaction view ────────────────────────────────

    private static APIGatewayProxyRequest Get() => new()
    {
        HttpMethod = "GET",
        Path = $"/admin/transactions/{OrderId}",
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    [Fact]
    public async Task TheTransactionView_ShowsTheCheckInsAsEvidence_WithTheExitsTheActionTakes()
    {
        var response = await Function().FunctionHandler(Get(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        using var doc = JsonDocument.Parse(response.Body);
        var evidence = doc.RootElement.GetProperty("pickupEvidence");
        evidence.GetProperty("judgement").GetString().Should().Be(PickupJudgement.Outcome.BothPresentNoScan);
        evidence.GetProperty("pointLatitude").GetDouble().Should().Be(8.7523);
        var arrived = evidence.GetProperty("checkIns").GetProperty(PickupJudgement.Kind.ArrivedPickup);
        arrived.GetProperty("latitude").GetDouble().Should().Be(8.7601);
        arrived.GetProperty("accuracyM").GetDouble().Should().Be(35);
        arrived.GetProperty("distanceKm").GetDouble().Should().Be(0.87);
        arrived.GetProperty("by").GetString().Should().Be("trucker-9");
        arrived.GetProperty("distanceChecked").GetBoolean().Should().BeTrue();
        evidence.GetProperty("reopenExits").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(PickupReopen.Exit.Pending, PickupReopen.Exit.HandoffConfirmed);

        // The order DTO in the same answer stays the parties' view: no coordinates, no sender.
        var partyView = doc.RootElement.GetProperty("order").GetProperty("checkIns").GetProperty(PickupJudgement.Kind.ArrivedPickup);
        partyView.TryGetProperty("latitude", out _).Should().BeFalse();
        partyView.TryGetProperty("by", out _).Should().BeFalse();
        doc.RootElement.GetProperty("order").TryGetProperty("pickupLatitude", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheTransactionView_OfAnOrderNeverStamped_HasNoEvidence()
    {
        _order.Remove(PickupJudgement.Field);
        _order.Remove(PickupJudgement.JudgedAtField);
        _order.Remove(PickupJudgement.CheckInsField);

        var response = await Function().FunctionHandler(Get(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().NotContain("pickupEvidence");
    }
}
