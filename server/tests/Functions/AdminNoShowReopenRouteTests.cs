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
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-330: <c>POST /admin/orders/{orderId}/reopen-no-show</c> through the admin Lambda with the DynamoDB requests
/// captured: the one transaction (order, trip booking, delivery, escrow check) and its conditions, the deadline the
/// repository derives, every guard before any write (the buyer already chose, the trip full, departed, cancelled or
/// gone, the money moved, a version or a judgement that changed), the replay, the excuse of the trucker's
/// <c>NO_SHOW</c> through ESC-32's decision path, and the exits on the transaction view.
/// </summary>
public class AdminNoShowReopenRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string TripsTable = "trips-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string OrderId = "ord-9";
    private const string QueuedAt = "2026-11-23T07:30:00.0000000Z";
    private const string Reason = "The farmer confirmed the trucker loaded 5 quintals on the 22nd.";
    private static readonly DateTime Now = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();
    private readonly List<PutItemRequest> _puts = new();
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly FakeReputationRepository _reputation = new();
    private readonly ManualClock _clock = new(Now);
    private Dictionary<string, AttributeValue> _order = NoShow();
    private Dictionary<string, AttributeValue>? _delivery = CancelledDelivery();
    private Dictionary<string, AttributeValue>? _escrow = HeldEscrow();
    private Dictionary<string, AttributeValue>? _trip = OpenTrip();

    public AdminNoShowReopenRouteTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            Dictionary<string, AttributeValue>? row = request.IndexName == "OrderIndex" ? _escrow
                : request.TableName == OrdersTable ? _order
                : request.TableName == DeliveriesTable ? _delivery
                : request.TableName == EscrowsTable ? _escrow
                : request.TableName == TripsTable ? _trip
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
        _db.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        _db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
            });
        _reputation.AddUser("trucker-9", "trucker");
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    /// <summary>
    /// A trucker no-show as the judgement leaves it: awaiting the buyer's choice, the slot released, a late scan's
    /// admin queue entry (<c>late_pickup_evidence</c>), the buyer-choice deadline indexed.
    /// </summary>
    private static Dictionary<string, AttributeValue> NoShow() => new()
    {
        ["orderId"] = S(OrderId),
        ["createdAt"] = S("2026-11-10T09:00:00.0000000Z"),
        ["status"] = S(OrderCommitment.AwaitingTransportStatus),
        ["version"] = N("8"),
        ["buyerId"] = S("buyer-9"),
        ["farmerId"] = S("farmer-9"),
        ["truckerId"] = S("trucker-9"),
        ["tripId"] = S("trip-9"),
        ["quantity"] = N("500"),
        [OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.Trucker),
        [OrderCommitment.TruckerResponseField] = S(PickupJudgement.NoShowResponse),
        [OrderCommitment.TransportFailureField] = S(PickupJudgement.TruckerNoShowFailure),
        [OrderCommitment.FaultPartyField] = S(OrderCommitment.Party.Trucker),
        [OrderCommitment.BuyerChoiceDeadlineAtField] = S("2026-11-24T06:01:00.0000000Z"),
        [OrderCommitment.BuyerChoiceReminderAtField] = S("2026-11-24T00:01:00.0000000Z"),
        [PickupDeadline.Field] = S("2026-11-22T06:00:00.0000000Z"),
        [PickupJudgement.Field] = S(PickupJudgement.Outcome.TruckerNoShow),
        [PickupJudgement.JudgedAtField] = S("2026-11-22T06:01:00.0000000Z"),
        [OrderCommitment.PickupOverdueNotifiedAtField] = S("2026-11-22T06:01:00.0000000Z"),
        [AdminQueueFields.Marker] = S(AdminQueueFields.MarkerOpen),
        [AdminQueueFields.QueuedAt] = S(QueuedAt),
        [AdminQueueFields.Reason] = S(PickupJudgement.ReasonLateEvidence),
        [AdminQueueFields.EscrowId] = S($"escrow-order-{OrderId}"),
        [OrderDeadlines.AtField] = S("2026-11-24T06:01:00.0000000Z"),
        [OrderDeadlines.KindField] = S(OrderDeadlines.Kind.BuyerChoice),
        [OrderDeadlines.ShardField] = S(OrderDeadlines.Shard),
        [PickupJudgement.CheckInsField] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [PickupJudgement.Kind.CropsReady] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.EntryReceivedAt] = S("2026-11-21T08:00:00.0000000Z"),
                        [PickupJudgement.EntryBy] = S("farmer-9"),
                        [PickupJudgement.EntryDistanceChecked] = new AttributeValue { BOOL = false },
                        [PickupJudgement.EntryLate] = new AttributeValue { BOOL = false }
                    }
                },
                [PickupJudgement.Kind.ArrivedPickup] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.EntryReceivedAt] = S("2026-11-22T09:00:00.0000000Z"),
                        [PickupJudgement.EntryBy] = S("trucker-9"),
                        [PickupJudgement.EntryLatitude] = N("8.7601"),
                        [PickupJudgement.EntryLongitude] = N("38.9785"),
                        [PickupJudgement.EntryDistanceKm] = N("0.87"),
                        [PickupJudgement.EntryDistanceChecked] = new AttributeValue { BOOL = true },
                        [PickupJudgement.EntryLate] = new AttributeValue { BOOL = true }
                    }
                }
            }
        }
    };

    /// <summary>The delivery the judgement cancelled, two seconds after the run's time, with its own stamp (FUP-363).</summary>
    private static Dictionary<string, AttributeValue> CancelledDelivery() => new()
    {
        ["deliveryId"] = S("del-9"),
        ["tripId"] = S("trip-9"),
        ["orderId"] = S(OrderId),
        ["buyerId"] = S("buyer-9"),
        ["farmerId"] = S("farmer-9"),
        ["truckerId"] = S("trucker-9"),
        ["status"] = S("cancelled"),
        ["version"] = N("3"),
        ["createdAt"] = S("2026-11-20T09:00:00.0000000Z"),
        ["updatedAt"] = S("2026-11-22T06:01:02.0000000Z"),
        [PickupJudgement.DeliveryCancelledAtField] = S("2026-11-22T06:01:02.0000000Z")
    };

    /// <summary>
    /// FUP-363: the trip as the order cron leaves it once the release emptied it and its day was over: closed as
    /// <c>expired</c>, no booking left (DynamoDB drops the emptied sets), all its room free.
    /// </summary>
    private static Dictionary<string, AttributeValue> ExpiredTrip(string capacity = "40") => new()
    {
        ["tripId"] = S("trip-9"),
        ["createdAt"] = S("2026-11-01T08:00:00.0000000Z"),
        ["truckerId"] = S("trucker-9"),
        ["status"] = S("cancelled"),
        ["cancelReason"] = S(TripLifecycle.ExpiredCancelReason),
        ["cancelledAt"] = S("2026-11-22T06:15:00.0000000Z"),
        ["departureDate"] = S("2026-11-20T06:00:00.000Z"),
        ["capacity"] = N(capacity),
        ["remainingQuintals"] = N(capacity),
        ["bookedQuintals"] = N("0"),
        ["version"] = N("6")
    };

    private static Dictionary<string, AttributeValue> HeldEscrow() => new()
    {
        ["escrowId"] = S($"escrow-order-{OrderId}"),
        ["createdAt"] = S("2026-11-10T09:30:00.0000000Z"),
        ["orderId"] = S(OrderId),
        ["deliveryId"] = S("del-9"),
        ["status"] = S("escrowed"),
        ["amount"] = N("17250"),
        ["version"] = N("3")
    };

    /// <summary>The trip the release gave the 5 quintals back to (another order still booked on it).</summary>
    private static Dictionary<string, AttributeValue> OpenTrip() => new()
    {
        ["tripId"] = S("trip-9"),
        ["createdAt"] = S("2026-11-01T08:00:00.0000000Z"),
        ["truckerId"] = S("trucker-9"),
        ["status"] = S("open"),
        ["capacity"] = N("40"),
        ["remainingQuintals"] = N("12"),
        ["bookedQuintals"] = N("28"),
        ["bookedOrderIds"] = new AttributeValue { SS = new List<string> { "ord-7" } },
        ["acceptedOrderIds"] = new AttributeValue { SS = new List<string> { "ord-7" } },
        ["version"] = N("4")
    };

    private ReputationService Reputation()
        => new(_reputation, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask);

    private AdminFunction Function(bool penalties = true, bool dropoff = false) =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable, tripsTable: TripsTable)
        {
            Clock = () => Now,
            ReputationPenaltiesEnabled = penalties,
            ReputationRepo = _reputation,
            ReputationSvc = Reputation(),
            DropoffJudgementOn = dropoff
        };

    /// <summary>The trucker's NO_SHOW as the reputation stream records it from the judgement (rule R10).</summary>
    private async Task RecordNoShowAsync()
    {
        await Reputation().RecordAsync(new ReputationCandidate
        {
            UserId = "trucker-9",
            EventId = NoShowReopen.NoShowEventId(OrderId),
            Code = ReputationCodes.NoShow,
            Party = "trucker",
            OrderId = OrderId,
            BuyerId = "buyer-9",
            OccurredAt = new DateTime(2026, 11, 22, 6, 1, 0, DateTimeKind.Utc)
        }, timeoutPenaltiesOn: true, new TestLambdaLogger());
    }

    private static APIGatewayProxyRequest Post(object? body = null, bool adminGroup = true, string action = NoShowReopen.Action) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/orders/{OrderId}/{action}",
        Body = JsonSerializer.Serialize(body ?? new { exit = "pending", reason = Reason, expectedVersion = 8, deadlineHours = 24 }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = adminGroup
                ? new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
                : new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private static object Handoff(int version = 8) => new { exit = "handoff_confirmed", reason = Reason, expectedVersion = version };

    private List<PutItemRequest> TxAudits => _puts.Where(p => p.TableName == TxAuditTable).ToList();

    /// <summary>The alias of an attribute the update sets (<c>#f</c>) or removes (<c>#r</c>); conditions use <c>#c</c>, absences <c>#a</c>.</summary>
    private static string Name(Update update, string attribute, string prefix = "#f")
        => update.ExpressionAttributeNames.Single(n => n.Value == attribute && n.Key.StartsWith(prefix, StringComparison.Ordinal)).Key;

    private static Dictionary<string, string> Conditioned(Update update)
        => update.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#c", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => update.ExpressionAttributeValues[":" + n.Key[1..]].S);

    [Fact]
    public async Task Pending_ReopensTheOrderAndRebooksTheTrip_InOneTransaction_AndExcusesTheNoShow()
    {
        await RecordNoShowAsync();

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"status\":\"accepted\"")
            .And.Contain("\"pickupJudgement\":\"pending\"")
            .And.Contain("\"pickupDeadlineAt\":\"2026-11-24T08:00:00.0000000Z\"")
            .And.Contain("\"fromJudgement\":\"trucker_no_show\"")
            .And.Contain("\"version\":9")
            .And.Contain("\"noShowEvent\":\"excused\"")
            .And.Contain("\"alreadyApplied\":false");

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(3, "the order, the trip booking and the escrow check; no delivery is touched");

        var update = items[0].Update;
        update.TableName.Should().Be(OrdersTable);
        var values = update.ExpressionAttributeValues;
        values[":expectedVersion"].N.Should().Be("8");
        values[":" + Name(update, "status")[1..]].S.Should().Be("accepted");
        values[":" + Name(update, OrderCommitment.TruckerResponseField)[1..]].S.Should().Be(OrderCommitment.Response.Accepted);
        values[":" + Name(update, PickupJudgement.Field)[1..]].S.Should().Be(PickupJudgement.Outcome.Pending);
        values[":" + Name(update, PickupDeadline.Field)[1..]].S.Should().Be("2026-11-24T08:00:00.0000000Z");
        var stamp = values[":" + Name(update, PickupReopen.Field)[1..]].M;
        stamp[PickupReopen.StampExit].S.Should().Be("pending");
        stamp[PickupReopen.StampFromVersion].N.Should().Be("8");
        stamp[PickupReopen.StampFromJudgement].S.Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        update.ExpressionAttributeNames.Values.Should().NotContain("activeDeliveryId", "the trucker's own create makes the new delivery");

        foreach (var removed in new[]
                 {
                     OrderCommitment.TransportFailureField, OrderCommitment.FaultPartyField,
                     OrderCommitment.BuyerChoiceDeadlineAtField, OrderCommitment.BuyerChoiceReminderAtField,
                     PickupJudgement.JudgedAtField, OrderCommitment.PickupOverdueNotifiedAtField,
                     AdminQueueFields.Marker, AdminQueueFields.QueuedAt, AdminQueueFields.Reason, AdminQueueFields.EscrowId
                 })
        {
            update.UpdateExpression.Should().Contain(Name(update, removed, "#r"), removed);
        }

        // The new window counts fresh check-ins: the map is emptied as the accept stamps it (the trucker's late arrival
        // would otherwise stay late and block a new one); what was there goes to the audit row.
        var checkIns = values[":" + Name(update, PickupJudgement.CheckInsField)[1..]];
        checkIns.IsMSet.Should().BeTrue();
        checkIns.M.Should().BeEmpty();

        // Judged again a minute after the new deadline, in the sweep's partition; the buyer-choice deadline is gone.
        values[":ndKind"].S.Should().Be(OrderDeadlines.Kind.PickupJudgement);
        values[":ndShard"].S.Should().Be(OrderDeadlines.Shard);
        values[":ndAt"].S.Should().Be("2026-11-24T08:01:00.0000000Z");

        Conditioned(update).Should().Equal(new Dictionary<string, string>
        {
            { "status", OrderCommitment.AwaitingTransportStatus },
            { PickupJudgement.Field, PickupJudgement.Outcome.TruckerNoShow },
            { OrderCommitment.TransportFailureField, PickupJudgement.TruckerNoShowFailure },
            { OrderCommitment.TruckerResponseField, PickupJudgement.NoShowResponse },
            { AdminQueueFields.QueuedAt, QueuedAt }
        });

        var trip = items[1].Update;
        trip.TableName.Should().Be(TripsTable);
        trip.Key["tripId"].S.Should().Be("trip-9");
        trip.Key["createdAt"].S.Should().Be("2026-11-01T08:00:00.0000000Z");
        trip.ExpressionAttributeValues[":q"].N.Should().Be("5");
        trip.ExpressionAttributeValues[":ids"].SS.Should().Equal(OrderId);
        trip.UpdateExpression.Should().Contain("ADD #bookedIds :ids, #acceptedIds :ids");
        trip.ConditionExpression.Should().Contain("#remaining >= :q").And.Contain("NOT contains(#bookedIds, :orderId)")
            .And.Contain("#status IN (:open, :matched)").And.Contain("attribute_not_exists(#deletedAt)");

        var check = items[2].ConditionCheck;
        check.TableName.Should().Be(EscrowsTable);
        check.ConditionExpression.Should().Contain("#status = :escrowed").And.Contain("attribute_not_exists(#holdCap)");

        var audit = TxAudits.Single(a => a.Item["action"].S == PickupReopen.AuditAction);
        audit.Item["actor"].S.Should().Be("admin-1");
        audit.Item["fromState"].S.Should().Be(OrderCommitment.AwaitingTransportStatus);
        audit.Item["toState"].S.Should().Be("accepted");
        audit.Item["metadata"].S.Should().Be(
            "exit=pending;judgement=trucker_no_show;deadlineAt=2026-11-24T08:00:00.0000000Z;trip=reserved;queue=removed"
            + ";clearedCheckIns=arrived_pickup:receivedAt=2026-11-22T09:00:00.0000000Z,by=trucker-9,latitude=8.7601,longitude=38.9785,"
            + "distanceKm=0.87,distanceChecked=true,late=true|crops_ready:receivedAt=2026-11-21T08:00:00.0000000Z,by=farmer-9,"
            + $"distanceChecked=false,late=false;reason={Reason}");

        // The excuse: ESC-32's decision, made by the admin, audited on the trucker.
        var row = _reputation.Row("trucker-9", NoShowReopen.NoShowEventId(OrderId));
        row.Status.Should().Be(ReputationStatuses.Excused);
        row.DecidedBy.Should().Be("admin-1");
        row.DecisionNote.Should().Be(NoShowReopen.ExcuseNote);
        var excused = TxAudits.Single(a => a.Item["action"].S == "reputation_excused");
        excused.Item["entityId"].S.Should().Be("trucker-9");
        excused.Item["metadata"].S.Should().StartWith($"event=NOSHOW#{OrderId};note={NoShowReopen.ExcuseNote};source=reopen-no-show");

        // 12 quintals were left, 5 taken: the trip stays open.
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task HandoffConfirmed_BringsTheCancelledDeliveryBackAsPickedUp_AndClaimsTheSlot()
    {
        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"status\":\"pickup_confirmed\"")
            .And.Contain("\"pickupJudgement\":\"scanned\"")
            .And.Contain("\"deliveryId\":\"del-9\"");

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(4);
        var order = items[0].Update;
        var values = order.ExpressionAttributeValues;
        values[":" + Name(order, "status")[1..]].S.Should().Be("pickup_confirmed");
        values[":" + Name(order, PickupJudgement.Field)[1..]].S.Should().Be(PickupJudgement.Outcome.Scanned);
        values[":" + Name(order, "activeDeliveryId")[1..]].S.Should().Be("del-9");
        values[":" + Name(order, "activeDeliveryClaimedAt")[1..]].S.Should().Be(OrderCommitment.Iso(Now));
        order.ExpressionAttributeNames.Values.Should().NotContain(PickupDeadline.Field, "the deadline is kept");
        order.ExpressionAttributeNames.Values.Should().NotContain(PickupJudgement.CheckInsField, "the judged window's evidence stays");
        order.ExpressionAttributeNames.Values.Should().NotContain(DropoffJudgement.Field, "dropoffClassification is off");
        order.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#r", StringComparison.Ordinal)).Select(n => n.Value)
            .Should().NotContain(new[] { PickupJudgement.JudgedAtField, OrderCommitment.PickupOverdueNotifiedAtField });
        // The slot was free at read: no other delivery may take it before the write.
        var absent = order.ExpressionAttributeNames.Where(n => n.Key.StartsWith("#a", StringComparison.Ordinal)).Select(n => n.Value);
        absent.Should().Contain("activeDeliveryId");
        // pickup_confirmed has no deadline of the index.
        values.Should().NotContainKey(":ndAt");

        items[1].Update.TableName.Should().Be(TripsTable);

        var delivery = items[2].Update;
        delivery.TableName.Should().Be(DeliveriesTable);
        delivery.Key["deliveryId"].S.Should().Be("del-9");
        delivery.Key["tripId"].S.Should().Be("trip-9");
        delivery.ConditionExpression.Should().StartWith("#status = :cancelled AND #v = :expected")
            .And.Contain("attribute_not_exists(#cancelReason)").And.Contain("attribute_not_exists(#resolution)")
            .And.Contain("attribute_exists(#judged)");
        // FUP-363: the judgement's stamp goes with the cancel it marked.
        delivery.UpdateExpression.Should().EndWith("REMOVE #judged");
        delivery.ExpressionAttributeNames["#judged"].Should().Be(PickupJudgement.DeliveryCancelledAtField);
        delivery.ExpressionAttributeValues[":expected"].N.Should().Be("3");
        delivery.ExpressionAttributeValues[":next"].N.Should().Be("4");
        delivery.ExpressionAttributeValues[":pickedUp"].S.Should().Be("pickedUp");
        delivery.ExpressionAttributeValues[":now"].S.Should().Be(OrderCommitment.Iso(Now));
        items[3].ConditionCheck.TableName.Should().Be(EscrowsTable);

        TxAudits.Select(a => a.Item["action"].S).Should().Equal(PickupReopen.AuditAction, "picked_up");
        TxAudits[0].Item["toState"].S.Should().Be("pickup_confirmed");
        TxAudits[0].Item["metadata"].S.Should().Be(
            $"exit=handoff_confirmed;judgement=trucker_no_show;deliveryId=del-9;trip=reserved;queue=removed;reason={Reason}");
        TxAudits[1].Item["entityType"].S.Should().Be("delivery");
        TxAudits[1].Item["fromState"].S.Should().Be("cancelled");
        TxAudits[1].Item["metadata"].S.Should().Be("handoff=admin");
        response.Body.Should().Contain("\"noShowEvent\":\"not_recorded\"", "the stream recorded no NO_SHOW in this test");
    }

    [Fact]
    public async Task ESC36_HandoffConfirmed_StampsTheDropoffJudgement_WithTheFlagOn()
    {
        var response = await Function(dropoff: true).FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var order = _transactions.Should().ContainSingle().Subject.TransactItems[0].Update;
        var values = order.ExpressionAttributeValues;
        values[":" + Name(order, "status")[1..]].S.Should().Be("pickup_confirmed");
        values[":" + Name(order, DropoffJudgement.Field)[1..]].S.Should().Be(DropoffJudgement.Outcome.Pending);
        // T8 from the admin's pickup time, which the delivery's pickedUpAt takes in the same transaction.
        values[":" + Name(order, DropoffJudgement.DeadlineField)[1..]].S.Should().Be(OrderCommitment.Iso(Now.AddHours(72)));
        values[":" + Name(order, DropoffJudgement.TripReadField)[1..]].BOOL.Should().BeTrue();
        // T8 is the sweep's deadline now, instead of no deadline at pickup_confirmed.
        values[":ndKind"].S.Should().Be(OrderDeadlines.Kind.DeliveryOverdue);
        values[":ndShard"].S.Should().Be(OrderDeadlines.Shard);
    }

    [Fact]
    public async Task ESC36_APendingReopen_StampsNoDropoffJudgement_TheScanWill()
    {
        var response = await Function(dropoff: true).FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var order = _transactions.Single().TransactItems[0].Update;
        order.ExpressionAttributeNames.Values.Should().NotContain(DropoffJudgement.Field);
        order.ExpressionAttributeValues[":ndKind"].S.Should().Be(OrderDeadlines.Kind.PickupJudgement);
    }

    [Fact]
    public async Task AReleaseThatNeverLanded_KeepsTheBookingAndTheSlot_AndOnlyMarksTheAcceptAgain()
    {
        _trip!["bookedOrderIds"] = new AttributeValue { SS = new List<string> { "ord-7", OrderId } };
        _order["activeDeliveryId"] = S("del-9");

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var items = _transactions.Single().TransactItems;
        var trip = items[1].Update;
        trip.UpdateExpression.Should().Be("SET #updated = :now ADD #acceptedIds :ids");
        trip.ConditionExpression.Should().StartWith("contains(#bookedIds, :orderId)");
        Conditioned(items[0].Update)["activeDeliveryId"].Should().Be("del-9", "the slot as read");
        TxAudits[0].Item["metadata"].S.Should().Contain(";trip=kept;");
    }

    [Fact]
    public async Task TheLastRoomTaken_MovesTheTripToMatched_AfterTheCommit()
    {
        _trip!["remainingQuintals"] = N("5");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var status = _updates.Should().ContainSingle().Subject;
        status.TableName.Should().Be(TripsTable);
        status.ExpressionAttributeValues[":from"].S.Should().Be("open");
        status.ExpressionAttributeValues[":to"].S.Should().Be("matched");
    }

    [Theory]
    [InlineData("matched", "3", false, "full")]
    [InlineData("open", "4.9", false, "full")]
    [InlineData("inTransit", "12", false, "departed")]
    [InlineData("delivered", "12", false, "departed")]
    [InlineData("cancelled", "12", false, "cancelled")]
    [InlineData("open", "12", true, "cancelled")]
    public async Task ATripThatCannotTakeTheBookingBack_RefusesTheReopen_BeforeAnyWrite(
        string status, string remaining, bool deleted, string why)
    {
        _trip!["status"] = S(status);
        _trip["remainingQuintals"] = N(remaining);
        if (deleted)
        {
            _trip["deletedAt"] = S("2026-11-22T10:00:00.0000000Z");
        }

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(NoShowReopen.TripUnavailableCode).And.Contain(why);
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task ATripThatIsGone_RefusesTheReopen()
    {
        _trip = null;

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(NoShowReopen.TripUnavailableCode).And.Contain("gone");
        _transactions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("accepted")]
    public async Task TheBuyerAlreadyChose_OrTheOrderMovedOn_RefusesTheReopen(string status)
    {
        _order["status"] = S(status);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.NotAllowedCode);
        _transactions.Should().BeEmpty();
        TxAudits.Should().BeEmpty();
    }

    [Theory]
    [InlineData("held")]
    [InlineData("refunded")]
    [InlineData("disputed")]
    public async Task MoneyThatMoved_RefusesTheReopen(string escrowStatus)
    {
        _escrow!["status"] = S(escrowStatus);

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefundArmedByTheSweep_RefusesTheReopen()
    {
        _escrow!["refundReason"] = S("order_cancelled");

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheHandoff_IsRefused_ForADeliveryAPartyCancelledBeforeTheJudgement()
    {
        _delivery!["updatedAt"] = S("2026-11-21T10:00:00.0000000Z");
        _delivery.Remove(PickupJudgement.DeliveryCancelledAtField);

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("before the judgement");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task FUP363_AJudgementTheCronRetried_StillBringsItsDeliveryBack()
    {
        // The order write after the cancel failed; the next run judged it 15 minutes after the cancel.
        _order[PickupJudgement.JudgedAtField] = S("2026-11-22T06:16:00.0000000Z");

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items[2].Update.TableName.Should().Be(DeliveriesTable);
        items[2].Update.Key["deliveryId"].S.Should().Be("del-9");
    }

    [Fact]
    public async Task FUP363_AnAdminCancelAfterPickup_IsNeverBroughtBack()
    {
        _delivery!["cancelReason"] = S("admin_cancelled");
        _delivery.Remove(PickupJudgement.DeliveryCancelledAtField);

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("support or a dispute");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task FUP363_ATripTheCronClosedAfterTheRelease_IsOpenedAgain_InTheSameTransaction()
    {
        _trip = ExpiredTrip();

        var response = await Function().FunctionHandler(Post(Handoff()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(4, "the order, the trip, the delivery and the escrow check");
        var trip = items[1].Update;
        trip.TableName.Should().Be(TripsTable);
        trip.Key["tripId"].S.Should().Be("trip-9");
        trip.Key["createdAt"].S.Should().Be("2026-11-01T08:00:00.0000000Z");
        trip.UpdateExpression.Should().StartWith("SET #status = :to")
            .And.Contain("#v = :next").And.Contain("REMOVE #cancelReason, #cancelledAt")
            .And.Contain("ADD #bookedIds :ids, #acceptedIds :ids");
        trip.ConditionExpression.Should().StartWith("#v = :expected AND #status = :cancelled AND #cancelReason = :expired")
            .And.Contain("attribute_not_exists(#deletedAt)").And.Contain("attribute_not_exists(#bookedIds)")
            .And.Contain("attribute_not_exists(#acceptedIds)").And.Contain("#remaining >= :q");
        var values = trip.ExpressionAttributeValues;
        values[":to"].S.Should().Be("open", "35 quintals stay free");
        values[":expected"].N.Should().Be("6");
        values[":next"].N.Should().Be("7");
        values[":q"].N.Should().Be("5");
        values[":expired"].S.Should().Be(TripLifecycle.ExpiredCancelReason);
        values[":ids"].SS.Should().Equal(OrderId);

        TxAudits[0].Item["metadata"].S.Should().Contain(";trip=revived;");
        _updates.Should().BeEmpty("the status is set in the transaction, never after it");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString()
            .Should().Contain($"{NoShowReopen.LogToken} orderId={OrderId} exit=handoff_confirmed trip=revived");
    }

    [Fact]
    public async Task FUP363_ARevivedTripTheOrderFills_IsMatchedInTheSameWrite()
    {
        _trip = ExpiredTrip(capacity: "5");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var trip = _transactions.Single().TransactItems[1].Update;
        trip.ExpressionAttributeValues[":to"].S.Should().Be("matched");
        _updates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("trucker", false, "trip was cancelled")]
    [InlineData(TripLifecycle.ExpiredCancelReason, true, "holds a booking")]
    public async Task FUP363_ATripItsTruckerCancelled_OrOneStillBooked_StaysClosed(string cancelReason, bool booked, string why)
    {
        _trip = ExpiredTrip();
        _trip["cancelReason"] = S(cancelReason);
        if (booked)
        {
            _trip["bookedOrderIds"] = new AttributeValue { SS = new List<string> { "ord-7" } };
        }

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(NoShowReopen.TripUnavailableCode).And.Contain(why);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderAtAnotherVersion_IsChanged_BeforeAnyWrite()
    {
        var response = await Function().FunctionHandler(Post(new { exit = "pending", reason = Reason, expectedVersion = 7, deadlineHours = 24 }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.ChangedCode);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ATripThatFilledUpBetweenTheReadAndTheWrite_IsChanged_AndNothingIsWritten()
    {
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = new List<CancellationReason>
                {
                    new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" }
                }
            }));

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.ChangedCode).And.Contain("trip");
        TxAudits.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameRequestAgain_AnswersAlreadyApplied_WritesNothingOnTheOrder_AndRetriesTheExcuse()
    {
        await RecordNoShowAsync();
        _order = NoShow();
        _order["status"] = S("accepted");
        _order["version"] = N("9");
        _order[PickupJudgement.Field] = S(PickupJudgement.Outcome.Pending);
        _order[PickupReopen.Field] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [PickupReopen.StampExit] = S("pending"),
                [PickupReopen.StampFromVersion] = N("8"),
                [PickupReopen.StampFromJudgement] = S(PickupJudgement.Outcome.TruckerNoShow),
                [PickupReopen.StampAt] = S("2026-11-23T07:59:00.0000000Z")
            }
        };

        // The first answer's excuse failed: the replay makes it.
        var first = await Function().FunctionHandler(Post(), _context);
        first.StatusCode.Should().Be(200, first.Body);
        first.Body.Should().Contain("\"alreadyApplied\":true").And.Contain("\"noShowEvent\":\"excused\"");

        var second = await Function().FunctionHandler(Post(), _context);
        second.Body.Should().Contain("\"alreadyApplied\":true").And.Contain("\"noShowEvent\":\"already_excused\"");

        _transactions.Should().BeEmpty();
        TxAudits.Select(a => a.Item["action"].S).Should().Equal("reputation_excused");
    }

    [Fact]
    public async Task WithPenaltiesOff_NothingIsExcused_AndTheAnswerSaysSo()
    {
        var response = await Function(penalties: false).FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"noShowEvent\":\"off\"");
        TxAudits.Select(a => a.Item["action"].S).Should().Equal(PickupReopen.AuditAction);
    }

    [Fact]
    public async Task AnUpheldNoShow_Stays_AndTheReopenStillLands()
    {
        await RecordNoShowAsync();
        var row = _reputation.Row("trucker-9", NoShowReopen.NoShowEventId(OrderId));
        _reputation.Put(row with { AppealDecision = ReputationService.DecisionUpheld });

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"noShowEvent\":\"upheld\"");
        _transactions.Should().ContainSingle();
    }

    [Fact]
    public async Task AnExcuseThatThrows_IsReported_AndNeverUndoesTheReopen()
    {
        await RecordNoShowAsync();
        _reputation.ReadFailure = new InvalidOperationException("boom");

        var response = await Function().FunctionHandler(Post(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"noShowEvent\":\"failed\"");
        _transactions.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyTheAdminGroup_MayReopenTheNoShow()
    {
        var roleOnly = await Function().FunctionHandler(Post(adminGroup: false), _context);

        roleOnly.StatusCode.Should().Be(403);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheJudgementReopen_StillRefusesATruckerNoShow_AndNamesThisAction()
    {
        var response = await Function().FunctionHandler(Post(action: PickupReopen.Action), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("Re-open the no-show");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ABadRequest_NamesItsField_AndReadsNothing()
    {
        var request = Post();
        request.Body = "{\"exit\":\"pending\",\"reason\":\"short\",\"expectedVersion\":8,\"deadlineHours\":24}";

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain(PickupReopen.InvalidCode).And.Contain("\"field\":\"reason\"");
        await _db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    // ── the exits on the transaction view ───────────────────────────────────

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
    public async Task TheTransactionView_OffersTheNoShowExits_AndNoJudgementReopen()
    {
        var response = await Function().FunctionHandler(Get(), _context);

        response.StatusCode.Should().Be(200, response.Body);
        using var doc = JsonDocument.Parse(response.Body);
        var evidence = doc.RootElement.GetProperty("pickupEvidence");
        evidence.GetProperty("judgement").GetString().Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        evidence.GetProperty("reopenExits").GetArrayLength().Should().Be(0);
        evidence.GetProperty("noShowReopenExits").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(PickupReopen.Exit.Pending, PickupReopen.Exit.HandoffConfirmed);

        _order["status"] = S("cancelled");
        var chosen = await Function().FunctionHandler(Get(), _context);
        using var after = JsonDocument.Parse(chosen.Body);
        after.RootElement.GetProperty("pickupEvidence").GetProperty("noShowReopenExits").GetArrayLength().Should().Be(0);
    }
}
