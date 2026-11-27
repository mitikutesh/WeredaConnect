using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-16: the <c>commitment_deadlines</c> sweep over the deadline index and the daily
/// <c>deadline_repair</c> walk. The sweep reads only due rows (a Query, never a status walk), reads
/// each again before deciding, settles its deadline after (corrected, retried later, or left to the
/// write that won a race), stays inside its pages and time, and one failing row never stops the
/// rest. The decisions per kind are in <see cref="OrderCronCommitmentTests"/>.
/// </summary>
public class OrderCronDeadlineSweepTests
{
    private const string OrderId = "ord-1";
    private const string TripId = "trip-1";
    private const string DeliveryId = "del-1";
    private const string CreatedAt = "2026-11-06T06:00:00.0000000Z";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly List<(string OrderId, string CreatedAt, int Version, OrderDeadline? Deadline)> _deadlineWrites = new();
    private TimeSpan _elapsed = TimeSpan.Zero;

    public OrderCronDeadlineSweepTests()
    {
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.QueryDeadlinePartitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _orders.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "orderId", ci.Arg<string>() } }));
        // FUP-210: a deadline cancel and its refund arm are one transaction.
        _orders.TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(ci => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written,
                new Dictionary<string, object> { { "orderId", ci.ArgAt<string>(0) } }));
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(true);
        _orders.TrySetDeadlineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OrderDeadline?>())
            .Returns(ci =>
            {
                _deadlineWrites.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<int>(2), ci.ArgAt<OrderDeadline?>(3)));
                return true;
            });
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object>
        {
            { "tripId", TripId }, { "status", "open" }, { "departureDate", "2026-11-20" }
        });
        _trips.TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _crops.TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(ci => new List<Dictionary<string, object>> { EscrowedRow(ci.Arg<string>()) });
        _escrows.GetByIdAsync(Arg.Any<string>(), true)
            .Returns(ci => EscrowedRow(ci.ArgAt<string>(0)["escrow-order-".Length..]));
    }

    private static Dictionary<string, object> EscrowedRow(string orderId) => new()
    {
        { "escrowId", $"escrow-order-{orderId}" }, { "orderId", orderId },
        { "createdAt", CreatedAt }, { "status", "escrowed" }, { "buyerId", "buyer-1" }, { "amount", "1000" }
    };

    // ===== fixture =====

    private OrderCronFunction Sut(bool enabled = true, DeadlineRepairMarker? marker = null)
        => new(_orders, _txAudit, inboxRepo: null,
            escrowService: new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                orderRepo: _orders, deliveryRepo: _deliveries),
            cropRepo: _crops, tripRepo: _trips, deliveryRepo: _deliveries)
        {
            CommitmentsEnabled = enabled,
            BudgetFactory = () => new CronRunBudget(() => _elapsed),
            AutoRepair = marker
        };

    private Task<APIGatewayProxyResponse> RunAsync(
        string action = "commitment_deadlines", bool enabled = true, DeadlineRepairMarker? marker = null)
        => Sut(enabled, marker).FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = $"{{\"action\":\"{action}\"}}" }, _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private DateTime HoursFromNow(double hours) => _now.AddHours(hours);

    private Dictionary<string, object> Order(string id, string status, params (string Key, object? Value)[] fields)
    {
        var row = new Dictionary<string, object>
        {
            { "orderId", id },
            { "createdAt", CreatedAt },
            { "status", status },
            { "version", "4" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { "listingId", "listing-1" },
            { "quantity", "500" },
            { "updatedAt", Iso(HoursFromNow(-26)) },
            { "paymentVerifiedAt", Iso(HoursFromNow(-26)) }
        };
        foreach (var (key, value) in fields)
        {
            if (value == null)
            {
                row.Remove(key);
            }
            else
            {
                row[key] = value;
            }
        }

        return row;
    }

    private Dictionary<string, object> FarmerExpired(string id = OrderId)
        => Order(id, "funded",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.FarmerDeadlineAtField, Iso(HoursFromNow(-1))),
            (OrderCommitment.FarmerReminderAtField, Iso(HoursFromNow(-7))),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.None));

    private Dictionary<string, object> AcceptedOnTrip(DateTime pickupBy, string id = OrderId)
        => Order(id, "accepted",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            ("tripId", TripId),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Accepted),
            ("activeDeliveryId", DeliveryId),
            (PickupDeadline.Field, PickupDeadline.Format(pickupBy)));

    /// <summary>The index answers with these rows (one page); each read again answers <paramref name="reRead"/> or itself.</summary>
    private void IndexServes(params Dictionary<string, object>[] rows)
    {
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((rows.ToList(), (Dictionary<string, AttributeValue>?)null));
        foreach (var row in rows)
        {
            _orders.GetByIdAsync((string)row["orderId"], true).Returns(row);
        }
    }

    private static Dictionary<string, object> IndexCopy(string id) => new() { { "orderId", id }, { "createdAt", CreatedAt } };

    private static Dictionary<string, AttributeValue> Key(string id) => new() { { "orderId", new AttributeValue { S = id } } };

    /// <summary>Nothing armed: no separate arm, and no transaction carrying one landed (no <c>refund_scheduled</c> row).</summary>
    private async Task AssertNoRefund()
    {
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "refund_scheduled"));
    }

    private Task AssertNoCancel()
        => _orders.DidNotReceiveWithAnyArgs().TryUpdateWithAsync(default!, default!, default, default!, default!);

    // ===== reading the index =====

    [Fact]
    public async Task Sweep_QueriesTheIndexForEverythingDueBeforeNow_AndReadsEachRowAgain()
    {
        var before = DateTime.UtcNow;
        IndexServes(FarmerExpired(), AcceptedOnTrip(HoursFromNow(-1), "ord-2"));

        var actual = await RunAsync();

        var after = DateTime.UtcNow;
        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"outcome\":\"ok\"", actual.Body);
        Assert.Contains("\"due\":\"2\"", actual.Body);
        Assert.Contains("\"farmerExpired\":\"1\"", actual.Body);
        Assert.Contains("\"pickupOverdue\":\"1\"", actual.Body);
        var call = _orders.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IOrderRepository.QueryDueDeadlinesAsync));
        var dueBefore = DateTime.Parse((string)call.GetArguments()[0]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(dueBefore, before, after);
        Assert.Equal(OrderCronFunction.DeadlinePageSize, call.GetArguments()[1]);
        Assert.Null(call.GetArguments()[2]);
        await _orders.Received(1).GetByIdAsync(OrderId, true);
        await _orders.Received(1).GetByIdAsync("ord-2", true);
        // No status walk any more.
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Sweep_FollowsThePages_FromTheLastKey()
    {
        var page2Key = Key("ord-1");
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), null)
            .Returns((new List<Dictionary<string, object>> { IndexCopy("ord-1") }, page2Key));
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), page2Key)
            .Returns((new List<Dictionary<string, object>> { IndexCopy("ord-2") }, (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync("ord-1", true).Returns(FarmerExpired("ord-1"));
        _orders.GetByIdAsync("ord-2", true).Returns(FarmerExpired("ord-2"));

        var actual = await RunAsync();

        Assert.Contains("\"due\":\"2\"", actual.Body);
        Assert.Contains("\"farmerExpired\":\"2\"", actual.Body);
        Assert.Contains("\"stopReason\":\"\"", actual.Body);
    }

    [Fact]
    public async Task Sweep_ReadsAtMostTheMaxPages_ThenStopsPartial()
    {
        var page = 0;
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(_ =>
            {
                var id = $"ord-{page++}";
                _orders.GetByIdAsync(id, true).Returns((Dictionary<string, object>?)null);
                return (new List<Dictionary<string, object>> { IndexCopy(id) }, Key(id));
            });

        var actual = await RunAsync();

        Assert.Contains("\"outcome\":\"partial\"", actual.Body);
        Assert.Contains("\"stopReason\":\"pages\"", actual.Body);
        Assert.Contains($"\"due\":\"{OrderCronFunction.DeadlineMaxPages}\"", actual.Body);
        Assert.Equal(OrderCronFunction.DeadlineMaxPages, page);
    }

    [Fact]
    public async Task Sweep_ARowSeenTwice_IsDecidedOnce()
    {
        IndexServes(FarmerExpired());
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { IndexCopy(OrderId), IndexCopy(OrderId) }, (Dictionary<string, AttributeValue>?)null));

        var actual = await RunAsync();

        Assert.Contains("\"due\":\"1\"", actual.Body);
        await _orders.Received(1).GetByIdAsync(OrderId, true);
    }

    [Fact]
    public async Task Sweep_StopsWhenItsTimeIsUsedUp()
    {
        IndexServes(FarmerExpired());
        _elapsed = OrderCronFunction.DeadlineSweepTime;

        var actual = await RunAsync();

        Assert.Contains("\"outcome\":\"partial\"", actual.Body);
        Assert.Contains("\"stopReason\":\"time\"", actual.Body);
        Assert.Contains("\"due\":\"0\"", actual.Body);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task DeletedOrder_IsSkipped()
    {
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { IndexCopy(OrderId) }, (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);

        var actual = await RunAsync();

        Assert.Contains("\"due\":\"1\"", actual.Body);
        Assert.Empty(_deadlineWrites);
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ===== settling the deadline after the decision =====

    [Fact]
    public async Task StaleIndexEntry_RowNotDueYet_GetsItsDeadlineBack()
    {
        var pickupBy = HoursFromNow(30);
        IndexServes(AcceptedOnTrip(pickupBy));

        var actual = await RunAsync();

        Assert.Contains("\"restamped\":\"1\"", actual.Body);
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal((OrderId, CreatedAt, 4), (write.OrderId, write.CreatedAt, write.Version));
        Assert.Equal(new OrderDeadline(PickupDeadline.TryParse(PickupDeadline.Format(pickupBy), out var at) ? at : default,
            OrderDeadlines.Kind.PickupOverdue), write.Deadline);
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    [Fact]
    public async Task RowAlreadyCarryingItsDeadline_IsLeftAlone()
    {
        var row = AcceptedOnTrip(HoursFromNow(30));
        var next = OrderDeadlines.Next(row)!.Value;
        row[OrderDeadlines.AtField] = next.AtIso;
        row[OrderDeadlines.KindField] = next.Kind;
        row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        IndexServes(row);

        var actual = await RunAsync();

        Assert.Contains("\"restamped\":\"0\"", actual.Body);
        Assert.Empty(_deadlineWrites);
    }

    [Fact]
    public async Task ReRun_AfterTheCancel_RemovesTheDeadline_AndRefundsOnce()
    {
        // First run: the farmer's window expired, the order is cancelled and refunded.
        var row = FarmerExpired();
        IndexServes(row);
        await RunAsync();
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        _escrows.Received(1).BuildRefundArm(Arg.Any<string>(), Arg.Any<string>(), "order_cancelled", Arg.Any<string>(), false);

        // Second run: the index copy is still behind, the row read again is cancelled.
        _orders.ClearReceivedCalls();
        _escrows.ClearReceivedCalls();
        _escrowAudit.ClearReceivedCalls();
        row["status"] = "cancelled";
        row["version"] = "5";
        row[OrderDeadlines.AtField] = Iso(HoursFromNow(-1));
        row[OrderDeadlines.KindField] = OrderDeadlines.Kind.FarmerResponse;
        row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        var second = await RunAsync();

        Assert.Contains("\"farmerExpired\":\"0\"", second.Body);
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoCancel();
        await AssertNoRefund();
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(5, write.Version);
        Assert.Null(write.Deadline);
    }

    // ===== races with a sync =====

    [Fact]
    public async Task FarmerAcceptedBeforeTheReRead_NothingCancelled_TheTruckersDeadlineTakesOver()
    {
        // The index copy says the farmer's window passed; the farmer's accept landed first.
        var askedAt = HoursFromNow(-0.5);
        var accepted = Order(OrderId, "funded",
            ("version", "5"),
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            (OrderCommitment.FarmerDeadlineAtField, Iso(HoursFromNow(-1))),
            ("tripId", TripId),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.TruckerAskedAtField, Iso(askedAt)),
            (OrderCommitment.TruckerDeadlineAtField, Iso(askedAt.AddHours(24))));
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { IndexCopy(OrderId) }, (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync(OrderId, true).Returns(accepted);

        var actual = await RunAsync();

        Assert.Contains("\"farmerExpired\":\"0\"", actual.Body);
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoCancel();
        await AssertNoRefund();
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(5, write.Version);
        Assert.Equal(OrderDeadlines.Kind.TruckerReminder, write.Deadline?.Kind);
    }

    [Fact]
    public async Task SyncWritesBetweenTheReReadAndTheCancel_TheCronLoses_AndLeavesTheDeadlineToTheWinner()
    {
        IndexServes(FarmerExpired());
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderChanged));

        var actual = await RunAsync();

        Assert.Contains("\"farmerExpired\":\"0\"", actual.Body);
        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        await AssertNoRefund();
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        Assert.Empty(_deadlineWrites);
    }

    [Fact]
    public async Task CorrectionLosesToAWrite_CountedAsAConflict()
    {
        _orders.TrySetDeadlineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OrderDeadline?>())
            .Returns(false);
        IndexServes(AcceptedOnTrip(HoursFromNow(30)));

        var actual = await RunAsync();

        Assert.Contains("\"restamped\":\"0\"", actual.Body);
        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        Assert.Contains("changed before its deadline was corrected", Logs);
    }

    // ===== rows that must wait =====

    [Fact]
    public async Task TripCancelledWhileGoodsMove_RowComesBackAnHourLater()
    {
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object> { { "tripId", TripId }, { "status", "cancelled" } });
        _deliveries.GetByIdAsync(DeliveryId).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "pickedUp" }
        });
        IndexServes(AcceptedOnTrip(HoursFromNow(-2)));

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        Assert.Contains("\"tripCancelled\":\"0\"", actual.Body);
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(OrderDeadlines.Kind.PickupOverdue, write.Deadline?.Kind);
        Assert.InRange(write.Deadline!.Value.At, _now + OrderCronFunction.DeadlineRetryAfter,
            DateTime.UtcNow + OrderCronFunction.DeadlineRetryAfter);
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UnreadableDelivery_WithOnlyThePickupWaitLeft_MovesToTheWatchPartition()
    {
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object> { { "tripId", TripId }, { "status", "cancelled" } });
        _deliveries.GetByIdAsync(DeliveryId).ThrowsAsync(new AmazonDynamoDBException("throttled"));
        var row = AcceptedOnTrip(HoursFromNow(-2));
        row[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(HoursFromNow(-1));
        row[OrderDeadlines.AtField] = Iso(HoursFromNow(-1));
        row[OrderDeadlines.KindField] = OrderDeadlines.Kind.PickupOverdue;
        row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        IndexServes(row);

        var actual = await RunAsync();

        // FUP-170: nothing in it is the sweep's, so no retry an hour later: the wait at its pickup deadline.
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(OrderDeadlines.Next(row), write.Deadline);
        Assert.Equal(OrderDeadlines.Kind.PickupWaiting, write.Deadline?.Kind);
        Assert.Equal(OrderDeadlines.WatchShard, write.Deadline?.Shard);
        Assert.Contains("\"retried\":\"0\"", actual.Body);
        Assert.DoesNotContain(OrderCronFunction.DeadlineStuckToken, Logs);
    }

    [Fact]
    public async Task LaggingEntryOfANotifiedPickup_IsNotStuck_ItMovesToTheWatchPartition_AndNothingIsDecided()
    {
        var row = AcceptedOnTrip(HoursFromNow(-3));
        row[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(HoursFromNow(-2));
        row[OrderDeadlines.AtField] = Iso(HoursFromNow(-3));
        row[OrderDeadlines.KindField] = OrderDeadlines.Kind.PickupOverdue;
        row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        IndexServes(row);

        var actual = await RunAsync();

        Assert.Contains("\"restamped\":\"1\"", actual.Body);
        Assert.Contains("\"pickupOverdue\":\"0\"", actual.Body);
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(OrderDeadlines.Kind.PickupWaiting, write.Deadline?.Kind);
        Assert.Equal(OrderDeadlines.Next(row)!.Value.At, write.Deadline?.At);
        Assert.DoesNotContain(OrderCronFunction.DeadlineStuckToken, Logs);
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await AssertNoCancel();
        await AssertNoRefund();
    }

    [Fact]
    public async Task Sweep_NeverReadsTheWatchPartition()
    {
        IndexServes(FarmerExpired());

        await RunAsync();

        await _orders.Received(1).QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
        await _orders.DidNotReceive().QueryDeadlinePartitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task ARowThatThrows_IsCounted_AndTheNextRowIsStillDecided()
    {
        IndexServes(FarmerExpired("ord-bad"), FarmerExpired("ord-2"));
        _orders.GetByIdAsync("ord-bad", true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"rowErrors\":\"1\"", actual.Body);
        Assert.Contains("\"farmerExpired\":\"1\"", actual.Body);
        Assert.Contains($"{OrderCronFunction.DeadlineRowFailedToken} orderId=ord-bad reason=[AmazonDynamoDBException]", Logs);
        await _orders.Received(1).TryUpdateWithAsync("ord-2", Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
    }

    // ===== deadline_repair =====

    [Fact]
    public async Task Repair_FlagOff_ReadsNothing()
    {
        var actual = await RunAsync("deadline_repair", enabled: false);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"action\":\"deadline_repair\"", actual.Body);
        Assert.Contains("\"outcome\":\"disabled\"", actual.Body);
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
        Assert.Empty(_deadlineWrites);
    }

    [Fact]
    public async Task Repair_StampsWhatIsMissingOrWrong_LeavesTheRest_AndDecidesNothing()
    {
        // A row in flight from before the index: no deadline yet.
        var unstamped = FarmerExpired("ord-legacy");
        // A row already right.
        var right = AcceptedOnTrip(HoursFromNow(30), "ord-right");
        var next = OrderDeadlines.Next(right)!.Value;
        right[OrderDeadlines.AtField] = next.AtIso;
        right[OrderDeadlines.KindField] = next.Kind;
        right[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        // A row whose marker is stamped but whose deadline was left behind.
        var stale = AcceptedOnTrip(HoursFromNow(-3), "ord-stale");
        stale[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(HoursFromNow(-2));
        stale[OrderDeadlines.AtField] = Iso(HoursFromNow(-3));
        stale[OrderDeadlines.KindField] = OrderDeadlines.Kind.PickupOverdue;
        stale[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        // An awaiting row whose stamp lost to a newer write.
        var raced = Order("ord-raced", OrderCommitment.AwaitingTransportStatus,
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(HoursFromNow(40))));
        _orders.QueryByStatusForCronAsync("funded", null).Returns(new List<Dictionary<string, object>> { unstamped });
        _orders.QueryByStatusForCronAsync("accepted", null).Returns(new List<Dictionary<string, object>> { right, stale });
        _orders.QueryByStatusForCronAsync(OrderCommitment.AwaitingTransportStatus, null)
            .Returns(new List<Dictionary<string, object>> { raced });
        _orders.TrySetDeadlineAsync("ord-raced", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OrderDeadline?>()).Returns(false);

        var actual = await RunAsync("deadline_repair");

        Assert.Contains("\"outcome\":\"ok\"", actual.Body);
        Assert.Contains("\"read\":\"4\"", actual.Body);
        Assert.Contains("\"stamped\":\"2\"", actual.Body);
        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        Assert.Equal(
            new[] { ("ord-legacy", OrderDeadlines.Next(unstamped)), ("ord-stale", OrderDeadlines.Next(stale)) },
            _deadlineWrites.Select(w => (w.OrderId, w.Deadline)).ToArray());
        Assert.Equal(OrderDeadlines.Kind.FarmerResponse, OrderDeadlines.Next(unstamped)?.Kind);
        // FUP-170: a notified pickup moves to the watch partition (also the backfill of the waits in flight).
        Assert.Equal(OrderDeadlines.Kind.PickupWaiting, OrderDeadlines.Next(stale)?.Kind);
        // It only stamps: the expired farmer window is the sweep's to settle.
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await _orders.DidNotReceive().QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
        await AssertNoRefund();
    }

    [Fact]
    public async Task Repair_ReadsTheWatchPartition_TakesOutAnEntryItsOrderNoLongerGives_AndDecidesNothing()
    {
        // Picked up, but the write that did it could not take the wait out of the index.
        var picked = Order("ord-picked", "pickup_confirmed",
            (PickupDeadline.Field, PickupDeadline.Format(HoursFromNow(-30))),
            (OrderCommitment.PickupOverdueNotifiedAtField, Iso(HoursFromNow(-29))),
            (OrderDeadlines.AtField, Iso(HoursFromNow(-30))),
            (OrderDeadlines.KindField, OrderDeadlines.Kind.PickupWaiting),
            (OrderDeadlines.ShardField, OrderDeadlines.WatchShard));
        // Still waiting, its entry right.
        var waiting = AcceptedOnTrip(HoursFromNow(-20), "ord-waiting");
        waiting[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(HoursFromNow(-19));
        var due = OrderDeadlines.Next(waiting)!.Value;
        waiting[OrderDeadlines.AtField] = due.AtIso;
        waiting[OrderDeadlines.KindField] = due.Kind;
        waiting[OrderDeadlines.ShardField] = OrderDeadlines.WatchShard;
        _orders.GetByIdAsync("ord-picked", true).Returns(picked);
        _orders.GetByIdAsync("ord-waiting", true).Returns(waiting);
        _orders.GetByIdAsync("ord-deleted", true).Returns((Dictionary<string, object>?)null);
        var page2 = Key("ord-waiting");
        _orders.QueryDeadlinePartitionAsync(Arg.Is(OrderDeadlines.WatchShard), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Is<Dictionary<string, AttributeValue>?>(k => k == null))
            .Returns((new List<Dictionary<string, object>> { IndexCopy("ord-picked"), IndexCopy("ord-waiting") }, page2));
        _orders.QueryDeadlinePartitionAsync(Arg.Is(OrderDeadlines.WatchShard), Arg.Any<string>(), Arg.Any<int>(), Arg.Is(page2))
            .Returns((new List<Dictionary<string, object>> { IndexCopy("ord-deleted") }, (Dictionary<string, AttributeValue>?)null));

        var actual = await RunAsync("deadline_repair");

        Assert.Contains("\"outcome\":\"ok\"", actual.Body);
        Assert.Contains("\"read\":\"2\"", actual.Body);
        Assert.Contains("\"stamped\":\"1\"", actual.Body);
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(("ord-picked", 4, (OrderDeadline?)null), (write.OrderId, write.Version, write.Deadline));
        var reads = _orders.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IOrderRepository.QueryDeadlinePartitionAsync))
            .Select(c => c.GetArguments()).ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, args =>
        {
            Assert.Equal(OrderDeadlines.WatchShard, args[0]);
            Assert.True(string.CompareOrdinal((string)args[1]!, Iso(DateTime.UtcNow.AddYears(100))) > 0, "every entry of the partition");
            Assert.Equal(OrderCronFunction.DeadlinePageSize, args[2]);
        });
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await AssertNoCancel();
        await AssertNoRefund();
    }

    [Fact]
    public async Task Repair_StopsWhenItsTimeIsUsedUp()
    {
        _orders.QueryByStatusForCronAsync("funded", null).Returns(new List<Dictionary<string, object>> { FarmerExpired() });
        _elapsed = OrderCronFunction.RepairWalkTime;

        var actual = await RunAsync("deadline_repair");

        Assert.Contains("\"outcome\":\"partial\"", actual.Body);
        Assert.Contains("\"stopReason\":\"time\"", actual.Body);
        Assert.Contains("\"read\":\"0\"", actual.Body);
        Assert.Empty(_deadlineWrites);
    }

    // ===== the automatic repair pass of commitment_deadlines =====

    [Fact]
    public async Task AutoRepair_TheFirstRunOfAContainer_StampsTheOrdersInFlight_ThenNotAgainForADay()
    {
        // A deploy starts new containers: the first run stamps an order in flight that has no deadline.
        var marker = new DeadlineRepairMarker();
        var legacy = FarmerExpired("ord-legacy");
        _orders.QueryByStatusForCronAsync("funded", null).Returns(new List<Dictionary<string, object>> { legacy });

        var first = await RunAsync(marker: marker);

        Assert.Contains("\"outcome\":\"ok\"", first.Body);
        Assert.Contains("\"repairStamped\":\"1\"", first.Body);
        var write = Assert.Single(_deadlineWrites);
        Assert.Equal(("ord-legacy", OrderDeadlines.Next(legacy)), (write.OrderId, write.Deadline));
        Assert.NotNull(marker.LastCompletedAt);
        // It only stamps: the expired window is the next sweep's.
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());

        _orders.ClearReceivedCalls();
        var second = await RunAsync(marker: marker);

        Assert.Contains("\"repairRead\":\"0\"", second.Body);
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AutoRepair_ADayAfterTheLastPass_RunsAgain()
    {
        var marker = new DeadlineRepairMarker();
        marker.Completed(DateTime.UtcNow - OrderCronFunction.AutoRepairEvery - TimeSpan.FromMinutes(1));

        await RunAsync(marker: marker);

        await _orders.Received(1).QueryByStatusForCronAsync("funded", null);
        Assert.True(marker.LastCompletedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AutoRepair_StoppedByItsShare_IsNotRecorded_SoTheNextRunGoesOn()
    {
        var marker = new DeadlineRepairMarker();
        _orders.QueryByStatusForCronAsync("funded", null).Returns(new List<Dictionary<string, object>> { FarmerExpired("ord-legacy") });
        // Inside the sweep's share, at the end of the repair's.
        _elapsed = OrderCronFunction.AutoRepairTime;

        var actual = await RunAsync(marker: marker);

        Assert.Contains("\"outcome\":\"ok\"", actual.Body);
        Assert.Contains("\"repairRead\":\"0\"", actual.Body);
        Assert.Empty(_deadlineWrites);
        Assert.Null(marker.LastCompletedAt);
    }

    [Fact]
    public async Task AutoRepair_AFailedRead_IsLogged_AndTheRunStillAnswers()
    {
        var marker = new DeadlineRepairMarker();
        _orders.QueryByStatusForCronAsync("funded", null).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var actual = await RunAsync(marker: marker);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"outcome\":\"ok\"", actual.Body);
        Assert.Contains($"{OrderCronFunction.AutoRepairFailedToken} reason=[AmazonDynamoDBException]", Logs);
        Assert.Null(marker.LastCompletedAt);
    }

    [Fact]
    public async Task AutoRepair_FlagOff_NothingIsRead()
    {
        var marker = new DeadlineRepairMarker();

        await RunAsync(enabled: false, marker: marker);

        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
        Assert.Null(marker.LastCompletedAt);
    }

    [Fact]
    public async Task Repair_ACompleteDailyPass_CountsForTheAutomaticOne()
    {
        var marker = new DeadlineRepairMarker();

        await RunAsync("deadline_repair", marker: marker);
        _orders.ClearReceivedCalls();
        await RunAsync(marker: marker);

        Assert.NotNull(marker.LastCompletedAt);
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnknownAction_NamesTheRepairAction()
    {
        var actual = await RunAsync("nope");

        Assert.Equal(400, actual.StatusCode);
        Assert.Contains("deadline_repair", actual.Body);
    }
}
