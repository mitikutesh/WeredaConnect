using System.Globalization;
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
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>Own tables: the commitment sweep reads the whole deadline index and the refund sweep the whole expired window.</summary>
[CollectionDefinition(Name)]
public sealed class DeadlineRefundRaceDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "DeadlineRefundRaceDynamoDb";
}

/// <summary>
/// FUP-210, FUP-211 and FUP-230 against real DynamoDB (LocalStack in CI). FUP-210: the order cron's commitment
/// deadlines (<c>farmer_response_expired</c>, <c>transport_not_arranged</c>) and the funding's stock loss
/// (<c>listing_out_of_stock</c>) cancelled the order and armed its refund in a second write, so a fraud
/// hold that reached the escrow in between left a cancelled order with no refund armed. Each now writes
/// the cancel and the arm in one transaction. FUP-211: an admin refund whose stock restore fails after
/// its committed cancel leaves the refund armed, and the hourly sweep gives the stock back before it
/// refunds (fail closed). FUP-230: so does the sweep's own cancel of a stale order, in the same run. Every
/// interleaving is forced with a hook at the losing point.
/// </summary>
[Collection(DeadlineRefundRaceDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeadlineAndStockLossRefundRaceDynamoDbTests
{
    private const string AdminId = "admin-fup210";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string EscrowCreatedAt = "2026-11-01T10:05:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly List<string> _refunded = new();

    public DeadlineAndStockLossRefundRaceDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            lock (_refunded)
            {
                _refunded.Add(call.Arg<RefundFundsRequest>().TransactionRef);
            }

            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private EscrowIdempotencyRepository IdempotencyRepo => new(Ddb, _db.Idempotency);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);
    private TripRepository TripRepo => new(Ddb, _db.Trips);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    /// <summary>A crops table that does not exist: every stock restore through it throws.</summary>
    private CropRepository BrokenCropRepo => new(Ddb, $"crops-missing-{Guid.NewGuid():N}");

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static DateTime Utc(string? iso)
        => DateTime.Parse(iso!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private sealed record Trade(string OrderId, string EscrowId, string ListingId, string? TripId, string ExpiresAt);

    private async Task SeedListingAsync(string listingId, string quantity, string status) => await Ddb.PutItemAsync(new PutItemRequest
    {
        TableName = _db.Crops,
        Item = new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S(listingId),
            ["createdAt"] = S(CreatedAt),
            ["farmerId"] = S(_farmerId),
            ["status"] = S(status),
            ["quantity"] = N(quantity),
            ["version"] = N("1")
        }
    });

    private Dictionary<string, AttributeValue> EscrowItem(string orderId, string expiresAt, bool ledger)
    {
        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S($"escrow-order-{orderId}"),
            ["createdAt"] = S(EscrowCreatedAt),
            ["orderId"] = S(orderId),
            ["source"] = S("order"),
            ["status"] = S("escrowed"),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-FUP210"),
            ["expiresAt"] = S(expiresAt),
            ["version"] = N("1")
        };
        if (ledger)
        {
            escrow[LedgerEscrowFields.SettlementModel] = S(LedgerEscrowFields.ModelLedger);
            escrow[LedgerEscrowFields.LegStatus] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    [LedgerEscrowFields.LegCrop] = S(LedgerEscrowFields.Held),
                    [LedgerEscrowFields.LegCommission] = S(LedgerEscrowFields.Held)
                }
            };
        }

        return escrow;
    }

    /// <summary>
    /// A paid order whose commitment deadline passed an hour ago and is due in the deadline index, with
    /// its plain <c>escrowed</c> money (8,000 ETB, expiring in 6 days) and its stock taken (the listing
    /// holds 0 quintals; the order's 300 kg come back as 3). The farmer's answer on a <c>funded</c> order,
    /// or with <paramref name="buyerChoice"/> the buyer's decision on an <c>awaiting_transport</c> one.
    /// <paramref name="withTrip"/> books the order's 3 quintals on a trip it was never asked to carry.
    /// </summary>
    private async Task<Trade> SeedDueAsync(bool buyerChoice = false, bool withTrip = false, bool ledger = false)
    {
        var orderId = $"order-fup210-{Guid.NewGuid():N}";
        var listingId = $"crop-fup210-{Guid.NewGuid():N}";
        var tripId = withTrip ? $"trip-fup210-{Guid.NewGuid():N}" : null;
        var expiresAt = DateTime.UtcNow.AddDays(6).ToString("o");
        var due = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-1));
        await SeedListingAsync(listingId, "0", "reserved");

        var order = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S(CreatedAt),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["listingId"] = S(listingId),
            ["quantity"] = N("300"),
            ["orderTotal"] = N("8000"),
            ["cropCost"] = N("7800"),
            ["paymentVerifiedAt"] = S(OrderCommitment.Iso(DateTime.UtcNow.AddHours(-26))),
            ["version"] = N("4"),
            [OrderDeadlines.ShardField] = S(OrderDeadlines.Shard),
            [OrderDeadlines.AtField] = S(due)
        };
        if (buyerChoice)
        {
            order["status"] = S(OrderCommitment.AwaitingTransportStatus);
            order[OrderCommitment.FarmerResponseField] = S(OrderCommitment.Response.Accepted);
            order[OrderCommitment.TruckerResponseField] = S(OrderCommitment.Response.Declined);
            order[OrderCommitment.TransportFailureField] = S(OrderCommitment.TransportFailure.TruckerDeclined);
            order[OrderCommitment.BuyerChoiceDeadlineAtField] = S(due);
            order[OrderDeadlines.KindField] = S(OrderDeadlines.Kind.BuyerChoice);
        }
        else
        {
            order["status"] = S("funded");
            order[OrderCommitment.FarmerResponseField] = S(OrderCommitment.Response.Pending);
            order[OrderCommitment.FarmerDeadlineAtField] = S(due);
            order[OrderCommitment.TruckerResponseField] = S(withTrip ? OrderCommitment.Response.NotAsked : OrderCommitment.Response.None);
            order[OrderDeadlines.KindField] = S(OrderDeadlines.Kind.FarmerResponse);
        }

        if (tripId != null)
        {
            order["tripId"] = S(tripId);
            await Ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _db.Trips,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["tripId"] = S(tripId),
                    ["createdAt"] = S(CreatedAt),
                    ["truckerId"] = S($"trucker-{Guid.NewGuid():N}"),
                    ["status"] = S("open"),
                    ["departureDate"] = S(DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ["remainingQuintals"] = N("7"),
                    ["bookedQuintals"] = N("3"),
                    ["bookedOrderIds"] = new AttributeValue { SS = new List<string> { orderId } },
                    ["version"] = N("1")
                }
            });
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = order });
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = EscrowItem(orderId, expiresAt, ledger) });
        return new Trade(orderId, $"escrow-order-{orderId}", listingId, tripId, expiresAt);
    }

    private EscrowService CronEscrowService(IEscrowRepository? escrowRepo) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, idempotencyRepo: null, telebirrGateway: null,
        orderRepo: OrderRepo, deliveryRepo: DeliveryRepo, transactionAuditRepo: TxRepo);

    /// <summary>The order cron as its Lambda builds it, commitments on.</summary>
    private OrderCronFunction Cron(IEscrowRepository? escrowRepo = null, ITransactionAuditRepository? txAudit = null) => new(
        OrderRepo, txAudit ?? TxRepo, inboxRepo: null, escrowService: CronEscrowService(escrowRepo),
        cropRepo: CropRepo, tripRepo: TripRepo, deliveryRepo: DeliveryRepo)
    {
        CommitmentsEnabled = true
    };

    private static async Task<(JsonElement Summary, string Logs)> RunDeadlinesAsync(OrderCronFunction cron)
    {
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(5) };
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, context);
        response.StatusCode.Should().Be(200, response.Body);
        return (JsonDocument.Parse(response.Body).RootElement.Clone(), ((TestLambdaLogger)context.Logger).Buffer.ToString());
    }

    private Task<Dictionary<string, object>?> OrderAsync(Trade trade) => OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true);
    private Task<Dictionary<string, object>?> EscrowAsync(Trade trade) => EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);

    private Task<List<Dictionary<string, object>>> EscrowAuditAsync(string escrowId)
        => AuditRepo.GetAuditTrailAsync(escrowId, consistentRead: true);

    private async Task<List<Dictionary<string, object>>> OrderHopsAsync(string orderId)
        => (await TxRepo.GetByTransactionAsync(orderId)).Where(r => Str(r, "entityType") == "order").ToList();

    /// <summary>Quintals on the listing: 0 while the order holds its stock, 3 once it was given back.</summary>
    private async Task<double> ListingQuantityAsync(string listingId)
        => double.Parse(Str(await CropRepo.GetByIdAsync(listingId), "quantity")!, CultureInfo.InvariantCulture);

    private int Refunds(string escrowId)
    {
        lock (_refunded)
        {
            return _refunded.Count(r => r.Contains(escrowId, StringComparison.Ordinal));
        }
    }

    /// <summary>A fraud hold through the FRD-03 write, as the escrow cron's risk gate places it.</summary>
    private Task<EscrowHoldWrite> HoldAsync(string escrowId, string createdAt) => EscrowRepo.TryHoldAsync(escrowId, createdAt,
        new EscrowHoldRequest("escrowed", EscrowHoldFields.ForRefund, EscrowHoldFields.ReasonAccountSuspended, DateTime.UtcNow.ToString("o")));

    /// <summary>
    /// The real transaction-audit table, running <c>race</c> once, right after the order's
    /// <c>order_cancelled</c> hop is written: the cancel has committed by then. Before FUP-210 that
    /// point was between the cancel and the refund arm. The hop writer swallows exceptions, so the race
    /// records what it saw instead of asserting.
    /// </summary>
    private sealed class AfterCancelHop : ITransactionAuditRepository
    {
        private readonly ITransactionAuditRepository _inner;
        private readonly string _orderId;
        private readonly Func<Task> _race;
        private bool _ran;

        public AfterCancelHop(ITransactionAuditRepository inner, string orderId, Func<Task> race)
        {
            _inner = inner;
            _orderId = orderId;
            _race = race;
        }

        public async Task AppendAsync(Dictionary<string, object> entry)
        {
            await _inner.AppendAsync(entry);
            if (!_ran && Str(entry, "entityId") == _orderId && Str(entry, "action") == "order_cancelled")
            {
                _ran = true;
                await _race();
            }
        }

        public Task<bool> AppendIfAbsentAsync(Dictionary<string, object> entry) => _inner.AppendIfAbsentAsync(entry);
        public Task<List<Dictionary<string, object>>> GetByTransactionAsync(string transactionId) => _inner.GetByTransactionAsync(transactionId);
        public Task<List<Dictionary<string, object>>> GetByEntityAsync(string entityId) => _inner.GetByEntityAsync(entityId);
        public Task<(List<Dictionary<string, object>> Items, bool More)> GetLatestByTransactionAsync(string transactionId, int limit) => _inner.GetLatestByTransactionAsync(transactionId, limit);
        public Task<List<Dictionary<string, object>>> GetRecentAsync(int limit) => _inner.GetRecentAsync(limit);
    }

    /// <summary>Runs <paramref name="race"/> once, when the refund arm of <paramref name="escrowId"/> is built (right before its transaction).</summary>
    private static Action<string> BeforeTheArm(string escrowId, Func<Task> race)
    {
        var ran = false;
        return id =>
        {
            if (ran || id != escrowId)
            {
                return;
            }

            ran = true;
            Task.Run(race).GetAwaiter().GetResult();
        };
    }

    /// <summary>The armed refund: <c>refundReason</c> and an expiry of about now (the hourly sweep's window).</summary>
    private static void ShouldBeArmed(Dictionary<string, object>? escrow, string reason)
    {
        Str(escrow, "refundReason").Should().Be(reason);
        Utc(Str(escrow, "expiresAt")).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
    }

    // ──────────────────────────────────────────────
    // FUP-210 (1): the order cron's commitment deadlines
    // ──────────────────────────────────────────────

    [LocalDynamoDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_AHoldRightAfterTheCancel_FindsTheRefundAlreadyArmed(bool buyerChoice)
    {
        // Before FUP-210 the hold landed between the cancel and the arm: the arm found a held escrow and
        // armed nothing, so the cancelled order's money waited for the admin queue.
        var trade = await SeedDueAsync(buyerChoice);
        EscrowHoldWrite? hold = null;
        var hop = new AfterCancelHop(TxRepo, trade.OrderId, async () => hold = await HoldAsync(trade.EscrowId, EscrowCreatedAt));

        await RunDeadlinesAsync(Cron(txAudit: hop));

        hold.Should().Be(EscrowHoldWrite.Held);
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be(buyerChoice ? "transport_not_arranged" : "farmer_response_expired");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be(EscrowHoldFields.Status);
        ShouldBeArmed(escrow, "order_cancelled");
        (await EscrowAuditAsync(trade.EscrowId)).Select(a => Str(a, "action")).Should().Contain("refund_scheduled");
    }

    [LocalDynamoDbTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    public async Task Deadline_WithNothingInTheWay_EndsAsBefore(bool buyerChoice, bool withTrip, bool ledger)
    {
        var trade = await SeedDueAsync(buyerChoice, withTrip, ledger);

        var (summary, logs) = await RunDeadlinesAsync(Cron());

        summary.GetProperty(buyerChoice ? "buyerChoiceExpired" : "farmerExpired").GetString().Should().Be("1");
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "version").Should().Be("5");
        Str(order, "cancelReason").Should().Be(buyerChoice ? "transport_not_arranged" : "farmer_response_expired");
        Str(order, OrderCommitment.CancelledByField).Should().Be(OrderCommitment.Party.System);
        Str(order, OrderCommitment.FaultPartyField).Should().Be(buyerChoice ? OrderCommitment.Party.None : OrderCommitment.Party.Farmer);
        if (!buyerChoice)
        {
            Str(order, OrderCommitment.FarmerResponseField).Should().Be(OrderCommitment.Response.Expired);
        }

        // A cancelled order has no deadline left: it leaves the index.
        order.Should().NotContainKey(OrderDeadlines.AtField);

        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("escrowed");
        Str(escrow, "version").Should().Be("2");
        ShouldBeArmed(escrow, "order_cancelled");
        // The same rows as before FUP-210: one refund_scheduled by the system, one order hop.
        var trail = await EscrowAuditAsync(trade.EscrowId);
        var scheduled = trail.Should().ContainSingle().Subject;
        Str(scheduled, "action").Should().Be("refund_scheduled");
        Str(scheduled, "actor").Should().Be("system");
        Str(scheduled, "metadata").Should().Be("order_cancelled");
        var hop = (await OrderHopsAsync(trade.OrderId)).Should().ContainSingle().Subject;
        Str(hop, "action").Should().Be("order_cancelled");
        Str(hop, "fromState").Should().Be(buyerChoice ? OrderCommitment.AwaitingTransportStatus : "funded");
        Str(hop, "metadata").Should().Be(buyerChoice ? "transport_not_arranged" : "farmer_response_expired");
        logs.Should().Contain($"Order escrow refund scheduled: orderId={trade.OrderId} reason=order_cancelled");
        logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        if (trade.TripId != null)
        {
            var trip = await TripRepo.GetByIdAsync(trade.TripId);
            Str(trip, "remainingQuintals").Should().Be("10");
            Str(trip, "bookedQuintals").Should().Be("0");
        }

        // The hourly sweep then refunds it as before (a ledger escrow through its ledger rows, not tested here).
        if (!ledger)
        {
            await SweepAsync(EscrowRepo, CropRepo);
            Str(await EscrowAsync(trade), "status").Should().Be("refunded");
            Refunds(trade.EscrowId).Should().Be(1);
        }
    }

    [LocalDynamoDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_ASettlementLockBetweenThePlanAndTheWrite_LeavesTheOrder_AndTheNextRunCancels(bool buyerChoice)
    {
        var trade = await SeedDueAsync(buyerChoice);
        var locked = false;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = BeforeTheArm(trade.EscrowId, async () =>
                locked = await EscrowRepo.TryAcquireSettlementLockAsync(trade.EscrowId, EscrowCreatedAt, "refund", "escrowed"))
        };

        var (summary, logs) = await RunDeadlinesAsync(Cron(racing));

        locked.Should().BeTrue();
        summary.GetProperty(buyerChoice ? "buyerChoiceExpired" : "farmerExpired").GetString().Should().Be("0");
        summary.GetProperty("retried").GetString().Should().Be("1");
        logs.Should().Contain($"ORDER_DEADLINE_REFUND_WAITING orderId={trade.OrderId}").And.Contain("reason=escrow_busy");
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be(buyerChoice ? OrderCommitment.AwaitingTransportStatus : "funded");
        Str(order, "version").Should().Be("4");
        order.Should().NotContainKey("cancelReason");
        // Looked at again in an hour.
        Utc(Str(order, OrderDeadlines.AtField)).Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));
        var escrow = await EscrowAsync(trade);
        escrow.Should().NotContainKey("refundReason");
        Str(escrow, "expiresAt").Should().Be(trade.ExpiresAt);
        (await EscrowAuditAsync(trade.EscrowId)).Should().BeEmpty();
        (await OrderHopsAsync(trade.OrderId)).Should().BeEmpty();
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(0);

        // The settlement lets go; the order is due again: cancelled and armed together.
        await EscrowRepo.ReleaseSettlementLockAsync(trade.EscrowId, EscrowCreatedAt);
        (await OrderRepo.TrySetDeadlineAsync(trade.OrderId, CreatedAt, 4,
            new OrderDeadline(DateTime.UtcNow.AddMinutes(-1), buyerChoice ? OrderDeadlines.Kind.BuyerChoice : OrderDeadlines.Kind.FarmerResponse)))
            .Should().BeTrue();

        await RunDeadlinesAsync(Cron());

        Str(await OrderAsync(trade), "status").Should().Be("cancelled");
        ShouldBeArmed(await EscrowAsync(trade), "order_cancelled");
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
    }

    [LocalDynamoDbFact]
    public async Task Deadline_AHoldBetweenThePlanAndTheWrite_CancelsOnlyWhileTheHoldStands_AndArmsNothing()
    {
        // The money is held for review: its admin decides it (FRD-03), as when the hold came first
        // before FUP-210. The cancel is fenced on the hold, the same rule as the sync reject (FUP-53).
        var trade = await SeedDueAsync();
        EscrowHoldWrite? hold = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = BeforeTheArm(trade.EscrowId, async () => hold = await HoldAsync(trade.EscrowId, EscrowCreatedAt))
        };

        var (summary, logs) = await RunDeadlinesAsync(Cron(racing));

        hold.Should().Be(EscrowHoldWrite.Held);
        summary.GetProperty("farmerExpired").GetString().Should().Be("1");
        Str(await OrderAsync(trade), "status").Should().Be("cancelled");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be(EscrowHoldFields.Status);
        escrow.Should().NotContainKey("refundReason");
        (await EscrowAuditAsync(trade.EscrowId)).Select(a => Str(a, "action")).Should().NotContain("refund_scheduled");
        logs.Should().Contain($"No refundable order escrow for orderId={trade.OrderId} (order_cancelled): held for review.");
    }

    // ──────────────────────────────────────────────
    // FUP-210 (2): the funding that lost the listing's stock
    // ──────────────────────────────────────────────

    /// <summary>A funded order for 300 kg of a listing that holds only 1 quintal (the funding lost the race).</summary>
    private async Task<(Dictionary<string, object> Order, string ListingId)> SeedOutOfStockFundingAsync()
    {
        var orderId = $"order-fup210s-{Guid.NewGuid():N}";
        var listingId = $"crop-fup210s-{Guid.NewGuid():N}";
        await SeedListingAsync(listingId, "1", "available");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("funded"),
                ["listingId"] = S(listingId),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["quantity"] = N("300"),
                ["orderTotal"] = N("2388.75"),
                ["cropCost"] = N("2275"),
                ["paymentVerifiedAt"] = S(DateTime.UtcNow.AddMinutes(-1).ToString("o")),
                [OrderCommitment.FarmerResponseField] = S(OrderCommitment.Response.Pending),
                [OrderCommitment.TruckerResponseField] = S(OrderCommitment.Response.None),
                ["version"] = N("3")
            }
        });
        return ((await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!, listingId);
    }

    /// <summary>The funding side effects as the Telebirr webhook builds them.</summary>
    private OrderFundingService Funding(IEscrowRepository? escrowRepo = null, ITransactionAuditRepository? txAudit = null)
    {
        var escrowService = new EscrowService(escrowRepo ?? EscrowRepo, AuditRepo, idempotencyRepo: null, telebirrGateway: null,
            orderRepo: OrderRepo);
        return new OrderFundingService(OrderRepo, CropRepo, escrowService, tripRepo: null, commitmentsEnabled: true,
            txAuditRepo: txAudit ?? TxRepo);
    }

    [LocalDynamoDbFact]
    public async Task StockLoss_AHoldRightAfterTheEnd_FindsTheRefundAlreadyArmed()
    {
        var (order, _) = await SeedOutOfStockFundingAsync();
        var orderId = Str(order, "orderId")!;
        var escrowId = $"escrow-order-{orderId}";
        EscrowHoldWrite? hold = null;
        var hop = new AfterCancelHop(TxRepo, orderId, async () =>
        {
            var escrow = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            hold = await HoldAsync(escrowId, Str(escrow, "createdAt")!);
        });
        var context = new TestLambdaContext();

        var result = await Funding(txAudit: hop).EnsureFundedSideEffectsAsync(order, "telebirr", null, context);

        hold.Should().Be(EscrowHoldWrite.Held);
        result.Stock.Should().Be(StockDecrementResult.InsufficientStock);
        Str(result.OutOfStockOrder, "status").Should().Be(OrderFundingService.OutOfStockStatus);
        Str(await OrderRepo.GetByIdAsync(orderId, consistentRead: true), "cancelReason").Should().Be(OrderFundingService.OutOfStockReason);
        var armed = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        Str(armed, "status").Should().Be(EscrowHoldFields.Status);
        ShouldBeArmed(armed, OrderFundingService.OutOfStockReason);
        ((TestLambdaLogger)context.Logger).Buffer.ToString().Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [LocalDynamoDbFact]
    public async Task StockLoss_WithNothingInTheWay_EndsAsBefore()
    {
        var (order, listingId) = await SeedOutOfStockFundingAsync();
        var orderId = Str(order, "orderId")!;
        var escrowId = $"escrow-order-{orderId}";
        var context = new TestLambdaContext();

        var result = await Funding().EnsureFundedSideEffectsAsync(order, "telebirr", null, context);

        result.Stock.Should().Be(StockDecrementResult.InsufficientStock);
        result.EscrowCreated.Should().BeTrue();
        result.Trip.Should().BeNull();
        var ended = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        Str(result.OutOfStockOrder, "status").Should().Be(OrderFundingService.OutOfStockStatus);
        Str(result.OutOfStockOrder, "version").Should().Be(Str(ended, "version"));
        Str(ended, "status").Should().Be(OrderFundingService.OutOfStockStatus);
        Str(ended, "version").Should().Be("4");
        Str(ended, "cancelReason").Should().Be(OrderFundingService.OutOfStockReason);
        Str(ended, OrderCommitment.CancelledByField).Should().Be(OrderCommitment.Party.System);
        Str(ended, OrderCommitment.FaultPartyField).Should().Be(OrderCommitment.Party.None);
        Str(ended, OrderCommitment.FarmerResponseField).Should().Be(OrderCommitment.Response.Pending);
        ended.Should().ContainKey("cancelledAt");

        var escrow = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        Str(escrow, "status").Should().Be("escrowed");
        ShouldBeArmed(escrow, OrderFundingService.OutOfStockReason);
        var trail = await EscrowAuditAsync(escrowId);
        trail.Select(a => Str(a, "action")).Should().BeEquivalentTo("order_funded", "refund_scheduled");
        Str(trail.Single(a => Str(a, "action") == "refund_scheduled"), "metadata").Should().Be(OrderFundingService.OutOfStockReason);
        Str(trail.Single(a => Str(a, "action") == "refund_scheduled"), "actor").Should().Be("system");
        var hop = (await OrderHopsAsync(orderId)).Should().ContainSingle().Subject;
        Str(hop, "action").Should().Be("order_cancelled");
        Str(hop, "fromState").Should().Be("funded");
        Str(hop, "metadata").Should().Be($"cancelReason={OrderFundingService.OutOfStockReason};listingId={listingId}");
        // Nothing was taken from the listing and nothing is given back.
        (await ListingQuantityAsync(listingId)).Should().Be(1);
        var logs = ((TestLambdaLogger)context.Logger).Buffer.ToString();
        logs.Should().Contain($"Order escrow refund scheduled: orderId={orderId} reason={OrderFundingService.OutOfStockReason}");
        logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED").And.NotContain("ORDER_STOCK_DECREMENT_FAILED");

        // A redelivery of the same payment finds it ended and arms the refund again, harmlessly.
        var again = await Funding().EnsureFundedSideEffectsAsync(order, "telebirr", null, new TestLambdaContext());
        Str(again.OutOfStockOrder, "status").Should().Be(OrderFundingService.OutOfStockStatus);
        Str(await OrderRepo.GetByIdAsync(orderId, consistentRead: true), "version").Should().Be("4");
        ShouldBeArmed(await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true), OrderFundingService.OutOfStockReason);
    }

    [LocalDynamoDbFact]
    public async Task StockLoss_ASettlementLockBetweenThePlanAndTheWrite_LeavesTheOrderFunded_ForAPerson()
    {
        var (order, _) = await SeedOutOfStockFundingAsync();
        var orderId = Str(order, "orderId")!;
        var escrowId = $"escrow-order-{orderId}";
        var locked = false;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = BeforeTheArm(escrowId, async () =>
            {
                var escrow = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
                locked = await EscrowRepo.TryAcquireSettlementLockAsync(escrowId, Str(escrow, "createdAt")!, "refund", "escrowed");
            })
        };
        var context = new TestLambdaContext();

        var result = await Funding(racing).EnsureFundedSideEffectsAsync(order, "telebirr", null, context);

        locked.Should().BeTrue();
        result.OutOfStockOrder.Should().BeNull();
        var current = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        Str(current, "status").Should().Be("funded");
        Str(current, "version").Should().Be("3");
        (await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true)).Should().NotContainKey("refundReason");
        (await OrderHopsAsync(orderId)).Should().BeEmpty();
        ((TestLambdaLogger)context.Logger).Buffer.ToString()
            .Should().Contain($"ORDER_STOCK_DECREMENT_FAILED orderId={orderId}").And.Contain("status=escrow_busy");
    }

    // ──────────────────────────────────────────────
    // FUP-211: the admin refund's restore failed after its committed cancel
    // ──────────────────────────────────────────────

    /// <summary>The escrow service as the escrow and admin Lambdas build it.</summary>
    private EscrowService MoneyService(ICropRepository cropRepo) => new(
        EscrowRepo, AuditRepo, IdempotencyRepo, _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo, cropRepo: cropRepo);

    private async Task<string> SweepAsync(IEscrowRepository escrowRepo, ICropRepository cropRepo, ITripRepository? tripRepo = null)
    {
        var cron = new EscrowCronFunction(MoneyService(CropRepo), escrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
            cropRepo: cropRepo, idempotencyRepo: IdempotencyRepo, transactionAuditRepo: TxRepo, tripRepo: tripRepo);
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(5) };
        var response = await cron.FunctionHandler(new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, context);
        response.StatusCode.Should().Be(200, response.Body);
        return ((TestLambdaLogger)context.Logger).Buffer.ToString();
    }

    [LocalDynamoDbTheory]
    [InlineData("funded")]
    [InlineData("accepted")]
    public async Task AdminRefund_RestoreFailsAfterTheCancel_TheSweepGivesTheStockBackBeforeItRefunds(string orderStatus)
    {
        var trade = await SeedDueAsync();
        if (orderStatus == "accepted")
        {
            (await OrderRepo.UpdateAsync(trade.OrderId, new Dictionary<string, object> { { "status", "accepted" } }, 4))
                .IsSuccess.Should().BeTrue();
        }

        var refund = await MoneyService(BrokenCropRepo).RefundAsync(new RefundEscrowRequest
        {
            EscrowId = trade.EscrowId,
            Trigger = "admin_override",
            IdempotencyKey = $"admin:{trade.EscrowId}:refund:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        }, AdminId, "admin", adminGroupVerified: true);

        // The admin is told to try again; the cancel and the arm have landed together.
        refund.Error!.HttpStatus.Should().Be(503);
        refund.Error.Code.Should().Be("ESCROW_012");
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be("escrow_refunded");
        ShouldBeArmed(await EscrowAsync(trade), "admin_refund");
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(0);

        // The hourly sweep finds the armed refund before the admin's retry. Its restore fails too: no refund.
        var logs = await SweepAsync(EscrowRepo, BrokenCropRepo);

        logs.Should().Contain($"ORDER_STOCK_RESTORE_FAILED orderId={trade.OrderId}");
        Refunds(trade.EscrowId).Should().Be(0);
        var waiting = await EscrowAsync(trade);
        Str(waiting, "status").Should().Be("escrowed");
        ShouldBeArmed(waiting, "admin_refund");
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(0);

        // The next run restores the stock first, then refunds, once.
        await SweepAsync(EscrowRepo, CropRepo);

        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        Str(await EscrowAsync(trade), "status").Should().Be("refunded");
        Refunds(trade.EscrowId).Should().Be(1);

        // Idempotent per order: another run gives nothing back twice.
        await SweepAsync(EscrowRepo, CropRepo);
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        Refunds(trade.EscrowId).Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // FUP-230: the sweep's own cancel of a stale order gives the stock back before its refund
    // ──────────────────────────────────────────────

    /// <summary>The 7 day window of the trade's escrow passed an hour ago.</summary>
    private async Task ExpireAsync(Trade trade) => await Ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = _db.Escrows,
        Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(trade.EscrowId), ["createdAt"] = S(EscrowCreatedAt) },
        UpdateExpression = "SET expiresAt = :past",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":past"] = S(DateTime.UtcNow.AddHours(-1).ToString("o")) }
    });

    /// <summary>The trip's booked and remaining quintals (3 and 7 while the order holds its booking).</summary>
    private async Task<(double Booked, double Remaining)> TripLoadAsync(string tripId)
    {
        var trip = await TripRepo.GetByIdAsync(tripId);
        return (double.Parse(Str(trip, "bookedQuintals")!, CultureInfo.InvariantCulture),
            double.Parse(Str(trip, "remainingQuintals")!, CultureInfo.InvariantCulture));
    }

    [LocalDynamoDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SweepCancel_RestoreFails_NoRefundThisRun_TheNextRunRestoresFirstThenRefunds(bool buyerChoice)
    {
        // A stale funded order (or one parked in awaiting_transport) whose escrow's 7 days passed.
        var trade = await SeedDueAsync(buyerChoice: buyerChoice, withTrip: !buyerChoice);
        await ExpireAsync(trade);

        var logs = await SweepAsync(EscrowRepo, BrokenCropRepo, TripRepo);

        // The cancel and the arm landed together (FUP-53); the stock did not come back, so no money moved.
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be(EscrowCronFunction.EscrowRefundedCancelReason);
        var waiting = await EscrowAsync(trade);
        Str(waiting, "status").Should().Be("escrowed");
        ShouldBeArmed(waiting, EscrowCronFunction.ExpiredOrderRefundReason);
        Refunds(trade.EscrowId).Should().Be(0);
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(0);
        logs.Should().Contain($"ORDER_STOCK_RESTORE_FAILED orderId={trade.OrderId} listingId={trade.ListingId}")
            .And.Contain("refund deferred to the next sweep.");
        if (trade.TripId != null)
        {
            // The trip booking went back all the same: the next run sees a cancelled order and never comes back to it.
            (await TripLoadAsync(trade.TripId)).Should().Be((0d, 10d));
        }

        // The next run restores first, then refunds, once.
        await SweepAsync(EscrowRepo, CropRepo, TripRepo);

        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        Str(await EscrowAsync(trade), "status").Should().Be("refunded");
        Refunds(trade.EscrowId).Should().Be(1);
        if (trade.TripId != null)
        {
            (await TripLoadAsync(trade.TripId)).Should().Be((0d, 10d));
        }

        // Idempotent per order: another run gives nothing back twice and refunds nothing more.
        await SweepAsync(EscrowRepo, CropRepo, TripRepo);
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        Refunds(trade.EscrowId).Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task SweepCancel_RestoreLands_RefundsInTheSameRun()
    {
        var trade = await SeedDueAsync(withTrip: true);
        await ExpireAsync(trade);

        var logs = await SweepAsync(EscrowRepo, CropRepo, TripRepo);

        Str(await OrderAsync(trade), "cancelReason").Should().Be(EscrowCronFunction.EscrowRefundedCancelReason);
        (await ListingQuantityAsync(trade.ListingId)).Should().Be(3);
        Str(await EscrowAsync(trade), "status").Should().Be("refunded");
        Refunds(trade.EscrowId).Should().Be(1);
        (await TripLoadAsync(trade.TripId!)).Should().Be((0d, 10d));
        logs.Should().NotContain($"ORDER_STOCK_RESTORE_FAILED orderId={trade.OrderId}");
    }
}
