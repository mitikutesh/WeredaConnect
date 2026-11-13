using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.Runtime;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-330 against real DynamoDB (LocalStack in CI), what the mocked route tests cannot show: the order cron judging a
/// trucker no-show (the pending delivery cancelled, the slot and the trip booking released) and the admin re-opening
/// it in one transaction on the real orders, trips, deliveries and escrows tables (the booking taken back under the
/// trip's own conditions, the delivery back from <c>cancelled</c>, the derived <c>DeadlineIndex</c> entry), the excuse
/// of the trucker's <c>NO_SHOW</c> on the real reputation table, the replay, and each guard leaving every row as it was.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NoShowReopenDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T09:00:00.0000000Z";
    private const string TripCreatedAt = "2026-11-01T08:00:00.0000000Z";
    private const string Reason = "The farmer confirmed the trucker loaded the crops; the scan synced late.";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly TestLambdaContext _context = new();

    public NoShowReopenDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private DeliveryRepository Deliveries => new(Ddb, _db.Deliveries);

    private TripRepository Trips => new(Ddb, _db.Trips);

    private ReputationEventRepository Reputation => new(Ddb, _db.ReputationEvents, _db.Users);

    private string Id(string name) => $"{name}-fup330-{_run}";

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private AdminFunction Admin(DateTime? now = null, bool dropoff = false) =>
        new(Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit, tripsTable: _db.Trips)
        {
            Clock = () => now ?? DateTime.UtcNow,
            ReputationPenaltiesEnabled = true,
            ReputationRepo = Reputation,
            ReputationSvc = new ReputationService(Reputation, ReputationPolicySettings.Defaults, backoff: _ => Task.CompletedTask),
            DropoffJudgementOn = dropoff
        };

    private static APIGatewayProxyRequest Post(string orderId, object body, string action = NoShowReopen.Action) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/orders/{orderId}/{action}",
        Body = JsonSerializer.Serialize(body),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
        }
    };

    private static object Pending(int version, int hours = 24) => new { exit = "pending", reason = Reason, expectedVersion = version, deadlineHours = hours };

    private static object Handoff(int version) => new { exit = "handoff_confirmed", reason = Reason, expectedVersion = version };

    private async Task<Dictionary<string, object>> ReadAsync(string orderId)
        => (await Repo.GetByIdAsync(orderId, consistentRead: true))!;

    private async Task<Dictionary<string, AttributeValue>> TripAsync()
        => (await Trips.GetRawItemAsync(Id("trip")))!;

    /// <summary>The trip with this order's 5 quintals and another order's 28 booked (40 in all), or as given.</summary>
    private async Task SeedTripAsync(bool withThisBooking, string status = "open", double remaining = 7)
    {
        var booked = new List<string> { Id("other") };
        if (withThisBooking)
        {
            booked.Add(Id("order"));
        }

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = S(Id("trip")),
                ["createdAt"] = S(TripCreatedAt),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S(status),
                ["capacity"] = N("40"),
                ["remainingQuintals"] = N(remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["bookedQuintals"] = N((40 - remaining).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["bookedOrderIds"] = new AttributeValue { SS = booked },
                ["acceptedOrderIds"] = new AttributeValue { SS = new List<string>(booked) },
                ["version"] = N("3")
            }
        });
    }

    private async Task SeedEscrowAsync(string? refundReason = null)
    {
        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S($"escrow-order-{Id("order")}"),
            ["createdAt"] = S(CreatedAt),
            ["orderId"] = S(Id("order")),
            ["deliveryId"] = S(Id("delivery")),
            ["status"] = S("escrowed"),
            ["amount"] = N("17250"),
            ["version"] = N("2")
        };
        if (refundReason != null)
        {
            escrow["refundReason"] = S(refundReason);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = escrow });
    }

    private async Task SeedTruckerAsync()
        => await Ddb.PutItemAsync(_db.Users, new Dictionary<string, AttributeValue>
        {
            ["userId"] = S(Id("trucker")),
            ["role"] = S("trucker"),
            ["version"] = N("1")
        });

    /// <summary>The common order fields of a committed trip order of 500 kg.</summary>
    private Dictionary<string, AttributeValue> OrderBase(string status, int version) => new()
    {
        ["orderId"] = S(Id("order")),
        ["createdAt"] = S(CreatedAt),
        ["status"] = S(status),
        ["version"] = N(version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ["buyerId"] = S(Id("buyer")),
        ["farmerId"] = S(Id("farmer")),
        ["truckerId"] = S(Id("trucker")),
        ["tripId"] = S(Id("trip")),
        ["listingId"] = S(Id("listing")),
        ["quantity"] = N("500"),
        [OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.Trucker),
        [OrderCommitment.FarmerResponseField] = S(OrderCommitment.Response.Accepted)
    };

    /// <summary>
    /// An accepted order waiting for its pickup judgement, its deadline two hours past and due in the sweep's partition:
    /// the farmer said the crops were ready, the trucker never checked in and nothing was scanned. Its delivery is
    /// pending, the trip holds its booking, the escrow holds the money and links the delivery.
    /// </summary>
    private async Task SeedAcceptedAsync()
    {
        var deadline = DateTime.UtcNow.AddHours(-2);
        var order = OrderBase("accepted", 6);
        order[OrderCommitment.TruckerResponseField] = S(OrderCommitment.Response.Accepted);
        order["activeDeliveryId"] = S(Id("delivery"));
        order[PickupDeadline.Field] = S(Iso(deadline));
        order[PickupJudgement.Field] = S(PickupJudgement.Outcome.Pending);
        order[PickupJudgement.CheckInsField] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                [PickupJudgement.Kind.CropsReady] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.EntryReceivedAt] = S(Iso(deadline.AddHours(-20))),
                        [PickupJudgement.EntryBy] = S(Id("farmer")),
                        [PickupJudgement.EntryDistanceChecked] = new() { BOOL = false },
                        [PickupJudgement.EntryLate] = new() { BOOL = false }
                    }
                }
            }
        };
        order[OrderDeadlines.AtField] = S(Iso(deadline + PickupJudgement.SettleMargin));
        order[OrderDeadlines.KindField] = S(OrderDeadlines.Kind.PickupJudgement);
        order[OrderDeadlines.ShardField] = S(OrderDeadlines.Shard);
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = order });

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(Id("delivery")),
                ["tripId"] = S(Id("trip")),
                ["orderId"] = S(Id("order")),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S("pending"),
                ["version"] = N("2"),
                ["createdAt"] = S(CreatedAt)
            }
        });
        await SeedTripAsync(withThisBooking: true);
        await SeedEscrowAsync();
        await SeedTruckerAsync();
    }

    /// <summary>
    /// The state a trucker no-show judgement leaves, written directly: <c>awaiting_transport</c> (version 8), the
    /// delivery cancelled after the judgement, the slot and the booking released (12 quintals free).
    /// </summary>
    private async Task SeedNoShowAsync(string? refundReason = null, string tripStatus = "open", double tripRemaining = 12)
    {
        var judgedAt = DateTime.UtcNow.AddHours(-3);
        var order = OrderBase(OrderCommitment.AwaitingTransportStatus, 8);
        order[OrderCommitment.TruckerResponseField] = S(PickupJudgement.NoShowResponse);
        order[OrderCommitment.TransportFailureField] = S(PickupJudgement.TruckerNoShowFailure);
        order[OrderCommitment.FaultPartyField] = S(OrderCommitment.Party.Trucker);
        order[OrderCommitment.BuyerChoiceDeadlineAtField] = S(Iso(judgedAt.AddHours(48)));
        order[PickupDeadline.Field] = S(Iso(judgedAt.AddMinutes(-1)));
        order[PickupJudgement.Field] = S(PickupJudgement.Outcome.TruckerNoShow);
        order[PickupJudgement.JudgedAtField] = S(Iso(judgedAt));
        order[OrderCommitment.PickupOverdueNotifiedAtField] = S(Iso(judgedAt));
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = order });

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(Id("delivery")),
                ["tripId"] = S(Id("trip")),
                ["orderId"] = S(Id("order")),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S("cancelled"),
                ["version"] = N("3"),
                ["createdAt"] = S(CreatedAt),
                ["updatedAt"] = S(Iso(judgedAt.AddSeconds(2))),
                // FUP-363: the judgement's own cancel carries its stamp.
                [PickupJudgement.DeliveryCancelledAtField] = S(Iso(judgedAt.AddSeconds(2)))
            }
        });
        await SeedTripAsync(withThisBooking: false, status: tripStatus, remaining: tripRemaining);
        await SeedEscrowAsync(refundReason);
        await SeedTruckerAsync();
    }

    /// <summary>The order cron on the real tables, through <paramref name="client"/> when given (else the fixture's).</summary>
    private OrderCronFunction Cron(IAmazonDynamoDB? client = null)
    {
        var ddb = client ?? Ddb;
        var orders = new OrderRepository(ddb, _db.Orders);
        var deliveries = new DeliveryRepository(ddb, _db.Deliveries);
        return new OrderCronFunction(orders, new TransactionAuditRepository(ddb, _db.TransactionAudit), inboxRepo: null,
            escrowService: new EscrowService(new EscrowRepository(ddb, _db.Escrows), new EscrowAuditRepository(ddb, _db.EscrowAudit),
                idempotencyRepo: null, telebirrGateway: null, orderRepo: orders, deliveryRepo: deliveries),
            cropRepo: new CropRepository(ddb, _db.Crops), tripRepo: new TripRepository(ddb, _db.Trips), deliveryRepo: deliveries)
        {
            CommitmentsEnabled = true,
            PickupClassificationEnabled = true
        };
    }

    private static Task RunCronAsync(OrderCronFunction cron) => cron.FunctionHandler(
        new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, new TestLambdaContext());

    /// <summary>
    /// FUP-363: the real client, except that the trucker no-show judgement's first write on the order is lost, as when
    /// another write on the order lands first: the cron sees a version conflict after it cancelled the delivery.
    /// </summary>
    private sealed class LosesTheFirstJudgementWrite : AmazonDynamoDBClient
    {
        private readonly string _orders;

        public LosesTheFirstJudgementWrite(string ordersTable)
            : base(new BasicAWSCredentials("test", "test"), new AmazonDynamoDBConfig
            {
                ServiceURL = LocalDynamoDbEnvironment.Endpoint,
                AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1"
            })
            => _orders = ordersTable;

        public int Lost { get; private set; }

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            if (Lost == 0 && request.TableName == _orders
                && request.ExpressionAttributeValues.Values.Any(v => v.S == PickupJudgement.TruckerNoShowFailure))
            {
                Lost++;
                throw new ConditionalCheckFailedException("Another write on the order landed first.");
            }

            return base.UpdateItemAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// FUP-363: an accepted order alone on its trip (the order's 5 quintals booked), the trip's day three days past, its
    /// pickup deadline two hours past and due in the sweep's partition. The trucker's arrival reached the server late
    /// (an offline phone), so it does not count and the judgement queues it as evidence.
    /// </summary>
    private async Task SeedAloneOnADepartedTripAsync(double capacity)
    {
        await SeedAcceptedAsync();
        var deadline = DateTime.UtcNow.AddHours(-2);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Orders,
            Key = new Dictionary<string, AttributeValue> { ["orderId"] = S(Id("order")), ["createdAt"] = S(CreatedAt) },
            UpdateExpression = "SET #checkIns.#arrived = :arrived",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#checkIns"] = PickupJudgement.CheckInsField,
                ["#arrived"] = PickupJudgement.Kind.ArrivedPickup
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":arrived"] = new()
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        [PickupJudgement.EntryReceivedAt] = S(Iso(deadline.AddMinutes(30))),
                        [PickupJudgement.EntryBy] = S(Id("trucker")),
                        [PickupJudgement.EntryDistanceChecked] = new() { BOOL = true },
                        [PickupJudgement.EntryLate] = new() { BOOL = true }
                    }
                }
            }
        });

        var departure = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var remaining = capacity - 5;
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = S(Id("trip")),
                ["createdAt"] = S(TripCreatedAt),
                ["truckerId"] = S(Id("trucker")),
                ["status"] = S(remaining > 0 ? "open" : "matched"),
                ["departureDate"] = S(departure),
                ["capacity"] = N(capacity.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["remainingQuintals"] = N(remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["bookedQuintals"] = N("5"),
                ["bookedOrderIds"] = new AttributeValue { SS = new List<string> { Id("order") } },
                ["acceptedOrderIds"] = new AttributeValue { SS = new List<string> { Id("order") } },
                ["version"] = N("3")
            }
        });
    }

    private static double Number(Dictionary<string, AttributeValue> row, string key)
        => double.Parse(row[key].N, System.Globalization.CultureInfo.InvariantCulture);

    [LocalDynamoDbFact]
    public async Task TheCronsNoShow_IsReopenedWithANewWindow_TheBookingTakenBack_AndTheNoShowExcused()
    {
        await SeedAcceptedAsync();
        await Cron().FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, new TestLambdaContext());

        // The judgement as the cron leaves it.
        var judged = await ReadAsync(Id("order"));
        judged["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        judged[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        judged.Should().NotContainKey("activeDeliveryId");
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("cancelled");
        Number(await TripAsync(), "remainingQuintals").Should().Be(12);
        OrderDeadlines.Stored(judged)!.Value.Kind.Should().Be(OrderDeadlines.Kind.BuyerChoiceReminder);

        // What the reputation stream records from that change (rule R10), on the real table.
        (await new ReputationService(Reputation, ReputationPolicySettings.Defaults, backoff: _ => Task.CompletedTask).RecordAsync(
            new ReputationCandidate
            {
                UserId = Id("trucker"),
                EventId = NoShowReopen.NoShowEventId(Id("order")),
                Code = ReputationCodes.NoShow,
                Party = "trucker",
                OrderId = Id("order"),
                BuyerId = Id("buyer"),
                OccurredAt = DateTime.UtcNow
            }, timeoutPenaltiesOn: true, new TestLambdaLogger())).Should().Be(ReputationRecordResult.Recorded);

        var version = int.Parse((string)judged["version"], System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var response = await Admin(now).FunctionHandler(Post(Id("order"), Pending(version)), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"noShowEvent\":\"excused\"");
        var after = await ReadAsync(Id("order"));
        after["status"].Should().Be("accepted");
        after["version"].Should().Be((version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        after[OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.Accepted);
        after[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending);
        after[PickupDeadline.Field].Should().Be(PickupDeadline.Format(now.AddHours(24)));
        after.Should().NotContainKey(OrderCommitment.TransportFailureField)
            .And.NotContainKey(OrderCommitment.FaultPartyField)
            .And.NotContainKey(OrderCommitment.BuyerChoiceDeadlineAtField)
            .And.NotContainKey(OrderCommitment.BuyerChoiceReminderAtField)
            .And.NotContainKey(PickupJudgement.JudgedAtField)
            .And.NotContainKey(OrderCommitment.PickupOverdueNotifiedAtField)
            .And.NotContainKey("activeDeliveryId");
        // The new window counts fresh check-ins: the map is empty again, and the trucker's arrival is taken in it.
        PickupJudgement.EntryOf(after, PickupJudgement.Kind.CropsReady).Should().BeNull("the judged window's check-ins are cleared");
        (await Repo.TryRecordCheckInAsync(Id("order"), CreatedAt, PickupJudgement.Kind.ArrivedPickup,
            new Dictionary<string, object>
            {
                { PickupJudgement.EntryReceivedAt, Iso(DateTime.UtcNow) }, { PickupJudgement.EntryBy, Id("trucker") },
                { PickupJudgement.EntryDistanceChecked, true }, { PickupJudgement.EntryLate, false }
            }, whileOpen: true)).Should().BeTrue();
        PickupJudgement.EntryOf(await ReadAsync(Id("order")), PickupJudgement.Kind.ArrivedPickup).Should().NotBeNull();
        var reopenAudit = (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(Id("order")))
            .Single(a => (string)a["action"] == PickupReopen.AuditAction);
        ((string)reopenAudit["metadata"]).Should().Contain($";clearedCheckIns=crops_ready:receivedAt=").And.Contain($"by={Id("farmer")}");
        var stored = OrderDeadlines.Stored(after)!.Value;
        stored.Kind.Should().Be(OrderDeadlines.Kind.PickupJudgement);
        stored.AtIso.Should().Be(Iso(now.AddHours(24) + PickupJudgement.SettleMargin));
        after[OrderDeadlines.ShardField].Should().Be(OrderDeadlines.Shard, "judged again by the sweep");

        // The booking is back, accepted, and the trip has 7 quintals left again.
        var trip = await TripAsync();
        Number(trip, "remainingQuintals").Should().Be(7);
        Number(trip, "bookedQuintals").Should().Be(33);
        trip["bookedOrderIds"].SS.Should().Contain(Id("order"));
        trip["acceptedOrderIds"].SS.Should().Contain(Id("order"));
        trip["status"].S.Should().Be("open");

        // The trucker creates the new pending delivery; the judged one stays cancelled.
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("cancelled");

        var noShow = (await Reputation.GetEventAsync(Id("trucker"), NoShowReopen.NoShowEventId(Id("order"))))!;
        noShow.Status.Should().Be(ReputationStatuses.Excused);
        noShow.DecidedBy.Should().Be("admin-1");
        (await Reputation.GetUserStateAsync(Id("trucker")))!.StoredActivePoints.Should().Be(0);

        var actions = (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(Id("order")))
            .Select(a => (string)a["action"]).ToList();
        actions.Should().Contain(PickupReopen.AuditAction);

        // The same request again: nothing written on the order or the trip, the excuse already made.
        var replay = await Admin().FunctionHandler(Post(Id("order"), Pending(version)), _context);
        replay.StatusCode.Should().Be(200, replay.Body);
        replay.Body.Should().Contain("\"alreadyApplied\":true").And.Contain("\"noShowEvent\":\"already_excused\"");
        (await ReadAsync(Id("order")))["version"].Should().Be((version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Number(await TripAsync(), "remainingQuintals").Should().Be(7);
    }

    [LocalDynamoDbFact]
    public async Task HandoffConfirmed_BringsTheJudgedDeliveryBackPickedUp_WithItsSlotAndItsBooking()
    {
        await SeedNoShowAsync(tripRemaining: 5);

        var now = DateTime.UtcNow;
        var response = await Admin(now, dropoff: true).FunctionHandler(Post(Id("order"), Handoff(8)), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"noShowEvent\":\"not_recorded\"");
        var after = await ReadAsync(Id("order"));
        after["status"].Should().Be("pickup_confirmed");
        after["version"].Should().Be("9");
        after[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Scanned);
        after["activeDeliveryId"].Should().Be(Id("delivery"));
        after.Should().ContainKey("activeDeliveryClaimedAt").And.ContainKey(PickupJudgement.JudgedAtField);
        after.Should().NotContainKey(OrderCommitment.TransportFailureField);
        // ESC-36 with dropoffClassification on: stamped for the drop-off judgement as a pickup is, T8 the sweep's deadline.
        after[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.Pending);
        after[DropoffJudgement.DeadlineField].Should().Be(OrderCommitment.Iso(now.AddHours(72)));
        var stored = OrderDeadlines.Stored(after)!.Value;
        stored.Kind.Should().Be(OrderDeadlines.Kind.DeliveryOverdue);
        after[OrderDeadlines.ShardField].Should().Be(OrderDeadlines.Shard);

        var delivery = (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!;
        delivery["status"].Should().Be("pickedUp");
        delivery["version"].Should().Be("4");
        delivery.Should().ContainKey("pickedUpAt").And.NotContainKey("gpsPickup");

        // The last 5 quintals: the trip is full again and leaves the open browse list.
        var trip = await TripAsync();
        Number(trip, "remainingQuintals").Should().Be(0);
        trip["status"].S.Should().Be("matched");
        trip["acceptedOrderIds"].SS.Should().Contain(Id("order"));
    }

    [LocalDynamoDbFact]
    public async Task ATripThatFilledUp_OrDeparted_RefusesTheReopen_AndNothingMoves()
    {
        await SeedNoShowAsync(tripRemaining: 2);

        var full = await Admin().FunctionHandler(Post(Id("order"), Handoff(8)), _context);

        full.StatusCode.Should().Be(409);
        full.Body.Should().Contain(NoShowReopen.TripUnavailableCode).And.Contain("full");
        (await ReadAsync(Id("order")))["version"].Should().Be("8");
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("cancelled");
        Number(await TripAsync(), "remainingQuintals").Should().Be(2);

        await SeedTripAsync(withThisBooking: false, status: "inTransit", remaining: 12);
        var departed = await Admin().FunctionHandler(Post(Id("order"), Pending(8)), _context);
        departed.StatusCode.Should().Be(409);
        departed.Body.Should().Contain("departed");
        (await ReadAsync(Id("order")))["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
    }

    [LocalDynamoDbFact]
    public async Task TheTransaction_RefusesATripThatFilledOrLeftSinceTheRead_AndADeliveryClosedSince()
    {
        await SeedNoShowAsync();
        var expected = new Dictionary<string, string> { { "status", OrderCommitment.AwaitingTransportStatus } };
        var set = new Dictionary<string, object> { { "status", "accepted" } };

        // Another order took the room after the admin's read: 2 quintals left, 5 needed.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Trips,
            Key = new Dictionary<string, AttributeValue> { ["tripId"] = S(Id("trip")), ["createdAt"] = S(TripCreatedAt) },
            UpdateExpression = "SET remainingQuintals = :r",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = N("2") }
        });
        var full = await Repo.TryUpdateGuardedAsync(Id("order"), set, Array.Empty<string>(), 8, expected, Array.Empty<string>(),
            new[] { Trips.BuildReclaimBookingItem(Id("trip"), TripCreatedAt, Id("order"), 5, Iso(DateTime.UtcNow)) });
        full.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);

        // The trucker started the trip with the other order meanwhile.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Trips,
            Key = new Dictionary<string, AttributeValue> { ["tripId"] = S(Id("trip")), ["createdAt"] = S(TripCreatedAt) },
            UpdateExpression = "SET remainingQuintals = :r, #s = :s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = N("12"), [":s"] = S("inTransit") }
        });
        var left = await Repo.TryUpdateGuardedAsync(Id("order"), set, Array.Empty<string>(), 8, expected, Array.Empty<string>(),
            new[] { Trips.BuildReclaimBookingItem(Id("trip"), TripCreatedAt, Id("order"), 5, Iso(DateTime.UtcNow)) });
        left.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);

        // An admin's cancel after pickup (or a verdict) closed the delivery with a reason: never brought back.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(Id("delivery")), ["tripId"] = S(Id("trip")) },
            UpdateExpression = "SET cancelReason = :r",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = S("admin_cancelled") }
        });
        var closed = await Repo.TryUpdateGuardedAsync(Id("order"), set, Array.Empty<string>(), 8, expected, Array.Empty<string>(),
            new[] { Deliveries.BuildRevivedPickupItem(Id("delivery"), Id("trip"), 3, Iso(DateTime.UtcNow)) });
        closed.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);

        (await ReadAsync(Id("order")))["version"].Should().Be("8");
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("cancelled");
        (await TripAsync())["bookedOrderIds"].SS.Should().NotContain(Id("order"));
    }

    [LocalDynamoDbFact]
    public async Task TheBuyersChoice_OrMoneyThatMoved_RefusesTheReopen_AndNothingMoves()
    {
        await SeedNoShowAsync(refundReason: "order_transport_failed");
        var refunding = await Admin().FunctionHandler(Post(Id("order"), Pending(8)), _context);
        refunding.StatusCode.Should().Be(409);
        refunding.Body.Should().Contain(PickupReopen.MoneyNotHeldCode);

        // The buyer cancels for the refund (the only choice today): a new version, status cancelled.
        await SeedNoShowAsync();
        (await Repo.UpdateAsync(Id("order"), new Dictionary<string, object>
        {
            { "status", "cancelled" }, { "cancelReason", PickupDeadline.CancelReason }
        }, 8)).IsSuccess.Should().BeTrue();

        var stale = await Admin().FunctionHandler(Post(Id("order"), Pending(8)), _context);
        stale.StatusCode.Should().Be(409);
        stale.Body.Should().Contain(PickupReopen.ChangedCode);

        var chosen = await Admin().FunctionHandler(Post(Id("order"), Pending(9)), _context);
        chosen.StatusCode.Should().Be(409);
        chosen.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("the buyer already chose");

        (await ReadAsync(Id("order")))["status"].Should().Be("cancelled");
        (await TripAsync())["bookedOrderIds"].SS.Should().NotContain(Id("order"));
    }

    [LocalDynamoDbTheory]
    [InlineData(PickupReopen.Exit.HandoffConfirmed, 8.0, "open")]
    [InlineData(PickupReopen.Exit.Pending, 5.0, "matched")]
    public async Task FUP363_ASingleOrderTripTheCronClosedAfterTheNoShow_IsOpenedAgainByTheReopen(
        string exit, double capacity, string tripStatus)
    {
        await SeedAloneOnADepartedTripAsync(capacity);

        // One run judges the no-show (the late arrival queued as evidence), releases the trip's only booking, then
        // closes the trip it left empty: its day is over and an order in awaiting_transport does not keep it.
        await RunCronAsync(Cron());
        var judged = await ReadAsync(Id("order"));
        judged["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        judged[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        judged[AdminQueueFields.Reason].Should().Be(PickupJudgement.ReasonLateEvidence);
        var closed = await TripAsync();
        closed["status"].S.Should().Be("cancelled");
        closed["cancelReason"].S.Should().Be(TripLifecycle.ExpiredCancelReason);
        closed.Should().NotContainKey("bookedOrderIds").And.NotContainKey("acceptedOrderIds");
        var closedVersion = int.Parse(closed["version"].N, System.Globalization.CultureInfo.InvariantCulture);

        var version = int.Parse((string)judged["version"], System.Globalization.CultureInfo.InvariantCulture);
        var response = await Admin().FunctionHandler(
            Post(Id("order"), exit == PickupReopen.Exit.Pending ? Pending(version) : Handoff(version)), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var trip = await TripAsync();
        trip["status"].S.Should().Be(tripStatus);
        trip.Should().NotContainKey("cancelReason").And.NotContainKey("cancelledAt");
        trip["version"].N.Should().Be((closedVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        trip["bookedOrderIds"].SS.Should().Equal(Id("order"));
        trip["acceptedOrderIds"].SS.Should().Equal(Id("order"));
        Number(trip, "bookedQuintals").Should().Be(5);
        Number(trip, "remainingQuintals").Should().Be(capacity - 5);

        var after = await ReadAsync(Id("order"));
        after["status"].Should().Be(exit == PickupReopen.Exit.Pending ? "accepted" : "pickup_confirmed");
        if (exit == PickupReopen.Exit.HandoffConfirmed)
        {
            var delivery = (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!;
            delivery["status"].Should().Be("pickedUp");
            delivery.Should().NotContainKey(PickupJudgement.DeliveryCancelledAtField);
        }

        var audit = (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(Id("order")))
            .Single(a => (string)a["action"] == PickupReopen.AuditAction);
        ((string)audit["metadata"]).Should().Contain(";trip=revived;");

        // The next run keeps the trip: it holds the booking again.
        await RunCronAsync(Cron());
        var kept = await TripAsync();
        kept["status"].S.Should().Be(tripStatus);
        kept["version"].N.Should().Be(trip["version"].N);
    }

    [LocalDynamoDbFact]
    public async Task FUP363_AJudgementWhoseOrderWriteFailedOnce_StillHasItsDeliveryBroughtBack_ByTheHandoff()
    {
        await SeedAcceptedAsync();

        // The first run cancels the pending delivery with the judgement's stamp, then loses its order write.
        using (var lossy = new LosesTheFirstJudgementWrite(_db.Orders))
        {
            await RunCronAsync(Cron(lossy));
            lossy.Lost.Should().Be(1);
        }

        var cancelled = (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!;
        cancelled["status"].Should().Be("cancelled");
        var stamp = (string)cancelled[PickupJudgement.DeliveryCancelledAtField];
        (await ReadAsync(Id("order")))[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending, "the order write was lost");

        // The next run judges again: the order's pickupJudgedAt is later than the cancel.
        await RunCronAsync(Cron());
        var judged = await ReadAsync(Id("order"));
        judged["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        OrderCommitment.TryParseUtc((string)judged[PickupJudgement.JudgedAtField], out var judgedAt).Should().BeTrue();
        OrderCommitment.TryParseUtc(stamp, out var cancelledAt).Should().BeTrue();
        judgedAt.Should().BeAfter(cancelledAt);

        var version = int.Parse((string)judged["version"], System.Globalization.CultureInfo.InvariantCulture);
        var response = await Admin().FunctionHandler(Post(Id("order"), Handoff(version)), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var delivery = (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!;
        delivery["status"].Should().Be("pickedUp");
        delivery.Should().NotContainKey(PickupJudgement.DeliveryCancelledAtField);
        var after = await ReadAsync(Id("order"));
        after["status"].Should().Be("pickup_confirmed");
        after["activeDeliveryId"].Should().Be(Id("delivery"));
    }

    [LocalDynamoDbFact]
    public async Task FUP363_ADeliveryAPartyOrAnAdminCancelled_IsNeverBroughtBack()
    {
        await SeedNoShowAsync();
        var key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(Id("delivery")), ["tripId"] = S(Id("trip")) };

        // A party's cancel carries no judgement stamp.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = key,
            UpdateExpression = "REMOVE #judged",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#judged"] = PickupJudgement.DeliveryCancelledAtField }
        });
        var party = await Admin().FunctionHandler(Post(Id("order"), Handoff(8)), _context);
        party.StatusCode.Should().Be(409);
        party.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("not by it");

        // The transaction's own condition refuses it too, whatever the read said.
        var raw = await Repo.TryUpdateGuardedAsync(Id("order"), new Dictionary<string, object> { { "status", "pickup_confirmed" } },
            Array.Empty<string>(), 8, new Dictionary<string, string> { { "status", OrderCommitment.AwaitingTransportStatus } },
            Array.Empty<string>(), new[] { Deliveries.BuildRevivedPickupItem(Id("delivery"), Id("trip"), 3, Iso(DateTime.UtcNow)) });
        raw.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);

        // FUP-19's admin cancel after pickup writes a reason (and no stamp).
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = key,
            UpdateExpression = "SET cancelReason = :r",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = S(AdminDeliveryCancel.CancelReason) }
        });
        var admin = await Admin().FunctionHandler(Post(Id("order"), Handoff(8)), _context);
        admin.StatusCode.Should().Be(409);
        admin.Body.Should().Contain(PickupReopen.NotAllowedCode).And.Contain("support or a dispute");

        (await ReadAsync(Id("order")))["version"].Should().Be("8");
        (await Deliveries.GetByIdAsync(Id("delivery"), consistentRead: true))!["status"].Should().Be("cancelled");
    }
}
