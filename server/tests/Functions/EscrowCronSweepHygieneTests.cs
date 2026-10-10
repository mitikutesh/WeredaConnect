using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-10 sweep hygiene, unit level: the paged sweeps and their budget, the order and delivery check
/// before an auto-release (FUP-28), the refund check of /escrow/initiate escrows, and the block reason
/// stored and audited once. The 150-row acceptance runs on LocalStack in SweepHygieneDynamoDbTests.
/// </summary>
public class EscrowCronSweepHygieneTests
{
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _txRows = new();
    private readonly List<string> _alarms = new();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly Dictionary<string, (string Reason, string At)> _markers = new();
    private readonly TestLambdaContext _context = new();

    public EscrowCronSweepHygieneTests()
    {
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.StubEmptySweeps();
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrows.TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        // The block reason behaves like the conditional write: stored once per value.
        _escrows.TrySetSettlementBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<string>(0);
                var reason = ci.ArgAt<string>(3);
                if (_markers.TryGetValue(id, out var stored) && stored.Reason == reason)
                {
                    return false;
                }

                _markers[id] = (reason, ci.ArgAt<string>(4));
                return true;
            });
        _orders.QueryAdminQueueAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(ci => new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{ci.Arg<ReleaseFundsRequest>().TransactionRef}" });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(ci => new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{ci.Arg<RefundFundsRequest>().TransactionRef}" });
        _audit.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(r => _auditRows.Add(r))).Returns(true);
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(r => _auditRows.Add(r))).Returns(Task.CompletedTask);
        _txAudit.AppendAsync(Arg.Do<Dictionary<string, object>>(r => _txRows.Add(r))).Returns(Task.CompletedTask);
    }

    private EscrowCronFunction Cron(
        bool withDeliveries = true, bool withOrders = true, int maxRows = 1000, int maxPages = 50,
        Func<TimeSpan>? elapsed = null)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: null,
            orderRepo: withOrders ? _orders : null, deliveryRepo: withDeliveries ? _deliveries : null,
            transactionAuditRepo: _txAudit)
        {
            AlarmLog = _alarms.Add
        };
        return new EscrowCronFunction(service, _escrows, _audit, _gateway,
            deliveryRepo: withDeliveries ? _deliveries : null, orderRepo: withOrders ? _orders : null,
            transactionAuditRepo: _txAudit)
        {
            SweepMaxRows = maxRows,
            SweepMaxPages = maxPages,
            Elapsed = elapsed
        };
    }

    private Task<APIGatewayProxyResponse> RunAsync(EscrowCronFunction cron, string action = "auto_release_and_refund")
        => cron.FunctionHandler(new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", action } })
        }, _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static Dictionary<string, object> Escrow(
        string escrowId, bool confirmed = false, string? orderId = null, bool orderSourced = false,
        double createdDaysAgo = 8, string? expiresAt = null)
    {
        var row = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", $"del-{escrowId}" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { "amount", 5_000m },
            { "commission", 125m },
            { "farmerPayout", 4_875m },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "telebirrHoldRef", "HOLD-1" },
            { "createdAt", DateTime.UtcNow.AddDays(-createdDaysAgo).ToString("o") },
            { "updatedAt", DateTime.UtcNow.AddDays(-createdDaysAgo).ToString("o") },
            { "expiresAt", expiresAt ?? DateTime.UtcNow.AddDays(-1).ToString("o") },
            { "version", 2 }
        };
        if (confirmed)
        {
            row["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-50).ToString("o");
            row["disputeWindowEndsAt"] = DateTime.UtcNow.AddHours(-2).ToString("o");
        }

        if (orderId != null)
        {
            row["orderId"] = orderId;
        }

        if (orderSourced)
        {
            row["source"] = "order";
        }

        return row;
    }

    /// <summary>The escrow's base row, read either way, carrying the block reason stored so far.</summary>
    private void Row(Dictionary<string, object> escrow)
    {
        var id = (string)escrow["escrowId"];
        _escrows.GetByIdAsync(id).Returns(_ => WithMarker(escrow));
        _escrows.GetByIdAsync(id, true).Returns(_ => WithMarker(escrow));
    }

    private Dictionary<string, object> WithMarker(Dictionary<string, object> escrow)
    {
        var copy = new Dictionary<string, object>(escrow);
        if (_markers.TryGetValue((string)escrow["escrowId"], out var stored))
        {
            copy[SettlementBlockFields.Reason] = stored.Reason;
            copy[SettlementBlockFields.ReasonAt] = stored.At;
        }

        return copy;
    }

    /// <summary>The release sweep serves these rows as the repository would: with the reason stored so far.</summary>
    private SweepStub ReleaseSweep(params Dictionary<string, object>[] rows)
    {
        foreach (var row in rows)
        {
            Row(row);
        }

        var stub = new SweepStub();
        _escrows.QueryCronPageAsync(
                Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true && q.ExcludeLocked),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(_ =>
            {
                stub.Served += rows.Length;
                return CronSweepStubs.Page(rows.Select(WithMarker));
            });
        return stub;
    }

    private SweepStub RefundSweep(params Dictionary<string, object>[] rows)
    {
        foreach (var row in rows)
        {
            Row(row);
        }

        var stub = new SweepStub();
        _escrows.QueryCronPageAsync(
                Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.ExpiresAtBefore != null && q.DeliveryConfirmed == false),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(_ =>
            {
                stub.Served += rows.Length;
                return CronSweepStubs.Page(rows.Select(WithMarker));
            });
        return stub;
    }

    private void DeliveryIs(string escrowId, string status, Action<Dictionary<string, object>>? more = null)
    {
        var row = new Dictionary<string, object> { { "deliveryId", $"del-{escrowId}" }, { "status", status } };
        more?.Invoke(row);
        _deliveries.GetByIdAsync($"del-{escrowId}", true).Returns(row);
        _deliveries.GetByIdAsync($"del-{escrowId}").Returns(row);
    }

    private void OrderIs(string orderId, string status)
    {
        var row = new Dictionary<string, object> { { "orderId", orderId }, { "status", status }, { "version", 3 } };
        _orders.GetByIdAsync(orderId, true).Returns(row);
        _orders.GetByIdAsync(orderId).Returns(row);
    }

    private Task Released(string escrowId)
        => _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{escrowId}"));

    private Task NotReleased(string escrowId)
        => _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{escrowId}"));

    private Task Refunded(string escrowId)
        => _gateway.Received(1).RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == $"refund:{escrowId}"));

    private Task NotRefunded(string escrowId)
        => _gateway.DidNotReceive().RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == $"refund:{escrowId}"));

    private List<Dictionary<string, object>> AuditRows(string escrowId, string action)
        => _auditRows.Where(r => (string)r["escrowId"] == escrowId && (string)r["action"] == action).ToList();

    // ===== The pager =====

    [Fact]
    public async Task RowsOnThreePages_AreAllHandled_AndEachQueryCarriesTheReleaseFilters()
    {
        var a = Escrow("escrow-p1", confirmed: true);
        var b = Escrow("escrow-p2", confirmed: true);
        var c = Escrow("escrow-p3", confirmed: true);
        foreach (var e in new[] { a, b, c })
        {
            Row(e);
            DeliveryIs((string)e["escrowId"], "delivered");
        }

        var k1 = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "escrow-p1" } } };
        var k2 = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "escrow-p2" } } };
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), null)
            .Returns(CronSweepStubs.Page([a], k1));
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), k1)
            .Returns(CronSweepStubs.Page([], k2));
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), k2)
            .Returns(CronSweepStubs.Page([b, c]));

        var response = await RunAsync(Cron());

        response.Body.Should().Contain("\"released\":\"3\"").And.Contain("\"sweepsTruncated\":\"-\"");
        await Released("escrow-p1");
        await Released("escrow-p3");
        await _escrows.Received(3).QueryCronPageAsync(
            Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true && q.ExcludeLocked
                                        && q.ExpiresAtBefore == null && q.PageSize == 100),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task EachPagedSweep_AsksForItsOwnFilters()
    {
        await RunAsync(Cron());
        await RunAsync(Cron(), "reconciliation");

        await _escrows.Received().QueryCronPageAsync(
            Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.ExpiresAtBefore != null && q.DeliveryConfirmed == false && q.ExcludeLocked),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _escrows.Received().QueryCronPageAsync(
            Arg.Is<CronSweepQuery>(q => q.Status == "dispute_resolved" && q.DeliveryConfirmed == null && q.ExcludeLocked),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _escrows.Received().QueryCronPageAsync(
            Arg.Is<CronSweepQuery>(q => q.Status == "initiated" && !q.ExcludeLocked && q.DeliveryConfirmed == null),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task AQueryWithoutItems_IsAnEmptyPage()
    {
        _escrows.QueryCronPageAsync(Arg.Any<CronSweepQuery>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((null!, (Dictionary<string, AttributeValue>?)null));

        var response = await RunAsync(Cron());

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"released\":\"0\"");
    }

    [Fact]
    public async Task RowCap_StopsTheSweep_AndLogsSweepTruncated()
    {
        var rows = Enumerable.Range(1, 3).Select(i => Escrow($"escrow-cap{i}", confirmed: true)).ToArray();
        foreach (var e in rows)
        {
            DeliveryIs((string)e["escrowId"], "delivered");
        }

        ReleaseSweep(rows);

        var response = await RunAsync(Cron(maxRows: 2));

        response.Body.Should().Contain("\"released\":\"2\"").And.Contain("\"sweepsTruncated\":\"release_query\"");
        Logs.Should().Contain("ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_truncated outcome=failed code=release_query:rows");
        await NotReleased("escrow-cap3");
    }

    [Fact]
    public async Task RowsThatNeedNoRead_DoNotCountTowardTheRowCap()
    {
        // Confirmed rows whose window is still open are skipped without any I/O.
        var open = Enumerable.Range(1, 5).Select(i =>
        {
            var e = Escrow($"escrow-open{i}", confirmed: true);
            e["disputeWindowEndsAt"] = DateTime.UtcNow.AddHours(10).ToString("o");
            return e;
        });
        var due = Escrow("escrow-due", confirmed: true);
        DeliveryIs("escrow-due", "delivered");
        ReleaseSweep(open.Append(due).ToArray());

        var response = await RunAsync(Cron(maxRows: 1));

        await Released("escrow-due");
        response.Body.Should().Contain("\"sweepsTruncated\":\"-\"");
    }

    [Fact]
    public async Task PageCap_StopsTheSweep_AndLogsSweepTruncated()
    {
        var k1 = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "x" } } };
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), null)
            .Returns(CronSweepStubs.Page([], k1));

        await RunAsync(Cron(maxPages: 1));

        Logs.Should().Contain("action=sweep_truncated outcome=failed code=release_query:pages");
    }

    [Fact]
    public async Task TimeBudget_IsAnchoredToTheRun_ALateRunStopsTheReleaseSweepButNotTheRefundSweep()
    {
        var due = Escrow("escrow-late", confirmed: true);
        DeliveryIs("escrow-late", "delivered");
        ReleaseSweep(due);
        var expired = Escrow("escrow-late-refund");
        DeliveryIs("escrow-late-refund", "cancelled");
        RefundSweep(expired);

        // 100 s into the run: past the release sweep's 90 s, before the refund sweep's 135 s.
        var response = await RunAsync(Cron(elapsed: () => TimeSpan.FromSeconds(100)));

        await NotReleased("escrow-late");
        await Refunded("escrow-late-refund");
        Logs.Should().Contain("code=release_query:time");
        response.Body.Should().Contain("\"sweepsTruncated\":\"release_query\"");
    }

    [Fact]
    public async Task LittleLambdaTimeLeft_StopsEveryPagedSweep()
    {
        var due = Escrow("escrow-short", confirmed: true);
        DeliveryIs("escrow-short", "delivered");
        ReleaseSweep(due);
        _context.RemainingTime = EscrowCronFunction.TruckerRetryTimeReserve + TimeSpan.FromSeconds(10);

        await RunAsync(Cron());

        await NotReleased("escrow-short");
        Logs.Should().Contain("code=release_query:time");
    }

    [Fact]
    public async Task APageQueryFailingMidSweep_EndsThatSweep_TheRowsBeforeItAreSettled_TheNextSweepRuns()
    {
        var a = Escrow("escrow-before-fail", confirmed: true);
        Row(a);
        DeliveryIs("escrow-before-fail", "delivered");
        var k1 = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "escrow-before-fail" } } };
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), null)
            .Returns(CronSweepStubs.Page([a], k1));
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "escrowed" && q.DeliveryConfirmed == true), k1)
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));
        var expired = Escrow("escrow-after-fail");
        DeliveryIs("escrow-after-fail", "cancelled");
        RefundSweep(expired);

        await RunAsync(Cron());

        await Released("escrow-before-fail");
        await Refunded("escrow-after-fail");
        Logs.Should().Contain("action=sweep_failed outcome=failed code=release_query:ProvisionedThroughputExceededException");
    }

    // ===== Auto-release: the order and delivery check (FUP-28) =====

    [Theory]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disbursing")]
    public async Task Release_DeliveredDelivery_OrderInFlight_IsPaid(string orderStatus)
    {
        var e = Escrow("escrow-ok", confirmed: true, orderId: "ord-ok", orderSourced: true);
        DeliveryIs("escrow-ok", "delivered");
        OrderIs("ord-ok", orderStatus);
        ReleaseSweep(e);

        await RunAsync(Cron());

        await Released("escrow-ok");
    }

    [Theory]
    [InlineData("pickedUp", "delivery_pickedUp")]
    [InlineData("inTransit", "delivery_inTransit")]
    [InlineData("pending", "delivery_pending")]
    [InlineData("cancelled", "delivery_cancelled")]
    public async Task Release_DeliveryNotDelivered_IsBlocked(string deliveryStatus, string reason)
    {
        var e = Escrow("escrow-nd", confirmed: true);
        DeliveryIs("escrow-nd", deliveryStatus);
        var swept = ReleaseSweep(e);

        var response = await RunAsync(Cron());

        await NotReleased("escrow-nd");
        response.Body.Should().Contain("\"releaseBlocked\":\"1\"");
        _markers["escrow-nd"].Reason.Should().Be($"release:trade:{reason}");
        AuditRows("escrow-nd", "auto_release_blocked").Should().ContainSingle()
            .Which["metadata"].Should().Be($"reason={reason};orderStatus=-;deliveryStatus={deliveryStatus}");
        swept.AssertServed();
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("funded")]
    [InlineData("settled")]
    [InlineData("created")]
    [InlineData("something_new")]
    public async Task Release_OrderNotInFlight_IsBlocked(string orderStatus)
    {
        var e = Escrow("escrow-oc", confirmed: true, orderId: "ord-oc", orderSourced: true);
        DeliveryIs("escrow-oc", "delivered");
        OrderIs("ord-oc", orderStatus);
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());

        await NotReleased("escrow-oc");
        _markers["escrow-oc"].Reason.Should().Be($"release:trade:order_{orderStatus}");
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_OrderOrDeliveryMissing_IsBlocked()
    {
        var noOrder = Escrow("escrow-om", confirmed: true, orderId: "ord-gone", orderSourced: true);
        DeliveryIs("escrow-om", "delivered");
        var noDelivery = Escrow("escrow-dm", confirmed: true);
        var swept = ReleaseSweep(noOrder, noDelivery);

        await RunAsync(Cron());

        await NotReleased("escrow-om");
        await NotReleased("escrow-dm");
        _markers["escrow-om"].Reason.Should().Be("release:trade:order_missing");
        _markers["escrow-dm"].Reason.Should().Be("release:trade:delivery_missing");
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_NoRepositories_FailClosed_RepoUnavailable_AndAlarmOnce()
    {
        var e = Escrow("escrow-norepo", confirmed: true);
        var swept = ReleaseSweep(e);

        await RunAsync(Cron(withDeliveries: false));
        await RunAsync(Cron(withDeliveries: false));

        await NotReleased("escrow-norepo");
        _markers["escrow-norepo"].Reason.Should().Be("release:trade:repo_unavailable");
        _alarms.Should().ContainSingle(l => l.Contains("action=auto_release_blocked") && l.Contains("escrowId=escrow-norepo"));
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_OrderIdWithoutAnOrderRepository_FailsClosed()
    {
        var e = Escrow("escrow-noorders", confirmed: true, orderId: "ord-x", orderSourced: true);
        DeliveryIs("escrow-noorders", "delivered");
        var swept = ReleaseSweep(e);

        await RunAsync(Cron(withOrders: false));

        await NotReleased("escrow-noorders");
        _markers["escrow-noorders"].Reason.Should().Be("release:trade:repo_unavailable");
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_DeliveryReadThrows_NothingPaid_AndTheRowAlarms()
    {
        var e = Escrow("escrow-readfail", confirmed: true);
        _deliveries.GetByIdAsync("del-escrow-readfail", true).ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());

        await NotReleased("escrow-readfail");
        Logs.Should().Contain("ESCROW_SETTLEMENT_FAILED escrowId=escrow-readfail action=auto_release_exception outcome=failed");
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_BlockedThreeRuns_StoresOneReason_OneAuditRow_OneAlarmLine()
    {
        var e = Escrow("escrow-thrice", confirmed: true, orderId: "ord-thrice", orderSourced: true);
        DeliveryIs("escrow-thrice", "delivered");
        OrderIs("ord-thrice", "cancelled");
        var swept = ReleaseSweep(e);

        for (var run = 0; run < 3; run++)
        {
            await RunAsync(Cron());
        }

        await NotReleased("escrow-thrice");
        await _escrows.Received(1).TrySetSettlementBlockReasonAsync("escrow-thrice", Arg.Any<string>(), "escrowed",
            "release:trade:order_cancelled", Arg.Any<string>());
        AuditRows("escrow-thrice", "auto_release_blocked").Select(r => r["timestamp"]).Distinct().Should().ContainSingle()
            .Which.Should().Be(_markers["escrow-thrice"].At);
        _alarms.Where(l => l.Contains("auto_release_blocked")).Should().ContainSingle()
            .Which.Should().Contain("code=release:trade:order_cancelled");
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_GatePasses_ClearsItsOwnStoredReason_ThenPays()
    {
        var e = Escrow("escrow-cleared", confirmed: true, orderId: "ord-cleared", orderSourced: true);
        DeliveryIs("escrow-cleared", "delivered");
        OrderIs("ord-cleared", "delivered");
        _markers["escrow-cleared"] = ("release:trade:order_cancelled", "2026-10-09T10:00:00.0000000Z");
        ReleaseSweep(e);

        await RunAsync(Cron());

        await _escrows.Received(1).TryClearSettlementBlockReasonAsync("escrow-cleared", Arg.Any<string>(), "release:trade:order_cancelled");
        await Released("escrow-cleared");
    }

    [Fact]
    public async Task Release_DisputedDelivery_FreezesThisEscrow_AndTheOrderFollows()
    {
        var e = Escrow("escrow-dsp", confirmed: true, orderId: "ord-dsp");
        DeliveryIs("escrow-dsp", "disputed", d => d["disputedBy"] = "buyer-1");
        OrderIs("ord-dsp", "delivered");
        _escrows.GetByDeliveryIdAsync("del-escrow-dsp").Returns(new Dictionary<string, object> { { "escrowId", "escrow-dsp" } });
        var swept = ReleaseSweep(e);

        var response = await RunAsync(Cron());

        await NotReleased("escrow-dsp");
        await _escrows.Received(1).UpdateStatusAsync("escrow-dsp", Arg.Any<string>(), "disputed", "escrowed",
            Arg.Any<Dictionary<string, object>?>());
        response.Body.Should().Contain("\"disputesFrozen\":\"1\"");
        // MirrorDisputeAsync: the delivered order follows the frozen money.
        await _orders.Received().UpdateAsync("ord-dsp",
            Arg.Is<Dictionary<string, object>>(d => (string)d["status"] == "disputed"), Arg.Any<int>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_DisputedOrder_DeliveredDelivery_FreezesAndMarksTheDelivery()
    {
        var e = Escrow("escrow-dspo", confirmed: true, orderId: "ord-dspo");
        DeliveryIs("escrow-dspo", "delivered");
        OrderIs("ord-dspo", "disputed");
        _escrows.GetByDeliveryIdAsync("del-escrow-dspo").Returns(new Dictionary<string, object> { { "escrowId", "escrow-dspo" } });
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());

        await NotReleased("escrow-dspo");
        await _escrows.Received(1).UpdateStatusAsync("escrow-dspo", Arg.Any<string>(), "disputed", "escrowed",
            Arg.Any<Dictionary<string, object>?>());
        await _deliveries.Received(1).TryMarkDisputedAsync("del-escrow-dspo", Arg.Any<string>(), Arg.Any<string?>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_FreezeWouldHitTheOrderEscrow_BlocksFreezeOtherEscrow_AndDoesNotAskAgain()
    {
        // An /escrow/initiate escrow on an order that also has its own escrow: ESC-07 freezes the order's.
        var e = Escrow("escrow-other", confirmed: true, orderId: "ord-two");
        DeliveryIs("escrow-other", "disputed");
        OrderIs("ord-two", "disputed");
        var orderEscrow = Escrow("escrow-order-ord-two", orderId: "ord-two", orderSourced: true);
        orderEscrow["status"] = "disputed";
        orderEscrow.Remove("deliveryId");
        _escrows.GetByIdAsync("escrow-order-ord-two", true).Returns(orderEscrow);
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());
        await RunAsync(Cron());

        await NotReleased("escrow-other");
        await _escrows.DidNotReceive().UpdateStatusAsync("escrow-other", Arg.Any<string>(), "disputed", Arg.Any<string?>(),
            Arg.Any<Dictionary<string, object>?>());
        _markers["escrow-other"].Reason.Should().Be("release:trade:freeze_other_escrow");
        // The second run sees the stored permanent refusal and does not ask the freeze path again.
        await _escrows.Received(1).GetByIdAsync("escrow-order-ord-two", true);
        _alarms.Where(l => l.Contains("auto_release_blocked")).Should().ContainSingle();
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_ResolvedDispute_Blocks_NeverFreezes()
    {
        var byOrder = Escrow("escrow-res1", confirmed: true, orderId: "ord-res1", orderSourced: true);
        DeliveryIs("escrow-res1", "disputed");
        OrderIs("ord-res1", "dispute_resolved");
        var byDelivery = Escrow("escrow-res2", confirmed: true);
        DeliveryIs("escrow-res2", "disputed", d => d["disputeResolution"] = "farmer_wins");
        var swept = ReleaseSweep(byOrder, byDelivery);

        await RunAsync(Cron());

        await NotReleased("escrow-res1");
        await NotReleased("escrow-res2");
        _markers["escrow-res1"].Reason.Should().Be("release:trade:dispute_resolved");
        _markers["escrow-res2"].Reason.Should().Be("release:trade:dispute_resolved");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "disputed", Arg.Any<string?>(),
            Arg.Any<Dictionary<string, object>?>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Release_FarmerPayoutGateBlocks_IsQuietAndCounted_NotAnError()
    {
        var e = Escrow("escrow-kyc", confirmed: true);
        DeliveryIs("escrow-kyc", "delivered");
        var swept = ReleaseSweep(e);
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("farmer-1").Returns(new UserDto
        {
            UserId = "farmer-1",
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = false,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, users, _orders, _deliveries) { AlarmLog = _alarms.Add };
        var cron = new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries, _orders);

        var response = await RunAsync(cron);

        response.Body.Should().Contain("\"releaseBlocked\":\"1\"");
        Logs.Should().Contain("ESCROW_RELEASE_BLOCKED escrowId=escrow-kyc code=ESCROW_029 sweep=auto-release");
        Logs.Should().NotContain("Auto-release failed");
        _markers["escrow-kyc"].Reason.Should().Be("release:payout:phone_unverified");
        _alarms.Should().BeEmpty();
        swept.AssertServed();
    }

    // ===== /escrow/initiate refunds =====

    [Fact]
    public async Task Initiate_GoodsInTransit_NoOrder_IsHeld_NotRefunded()
    {
        var e = Escrow("escrow-it", createdDaysAgo: 8);
        DeliveryIs("escrow-it", "inTransit");
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-it");
        var cap = DateTime.Parse((string)e["createdAt"], null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().AddDays(21);
        await _escrows.Received(1).TryExtendExpiryAsync("escrow-it", (string)e["createdAt"], Arg.Is<string>(s =>
            DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() <= cap
            && DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() > DateTime.UtcNow.AddHours(47)));
        swept.AssertServed();
    }

    [Theory]
    [InlineData("pickedUp", "accepted")]
    [InlineData("inTransit", "in_transit")]
    [InlineData("pending", "accepted")]
    [InlineData("cancelled", "accepted")]
    public async Task Initiate_LiveOrder_IsHeld_NotRefunded(string deliveryStatus, string orderStatus)
    {
        var e = Escrow("escrow-live", orderId: "ord-live");
        DeliveryIs("escrow-live", deliveryStatus);
        OrderIs("ord-live", orderStatus);
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-live");
        await _escrows.Received(1).TryExtendExpiryAsync("escrow-live", Arg.Any<string>(), Arg.Any<string>());
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_GoodsMovingBehindAnEndedOrder_IsBlocked_DeliveryMoving()
    {
        var e = Escrow("escrow-mv", orderId: "ord-mv");
        e["refundReason"] = "order_cancelled";
        DeliveryIs("escrow-mv", "inTransit");
        OrderIs("ord-mv", "cancelled");
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-mv");
        _markers["escrow-mv"].Reason.Should().Be("refund:trade:delivery_moving");
        _alarms.Should().ContainSingle(l => l.Contains("action=auto_refund_blocked"));
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_PendingDelivery_NoLiveOrder_IsCancelledFirst_ThenRefunded()
    {
        var e = Escrow("escrow-pd");
        DeliveryIs("escrow-pd", "pending");
        _deliveries.TryCancelPendingAsync("del-escrow-pd").Returns(true);
        RefundSweep(e);

        var response = await RunAsync(Cron());

        Received.InOrder(() =>
        {
            _deliveries.TryCancelPendingAsync("del-escrow-pd");
            _gateway.RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == "refund:escrow-pd"));
        });
        response.Body.Should().Contain("\"pendingDeliveriesCancelled\":\"1\"").And.Contain("\"refunded\":\"1\"");
        _txRows.Should().ContainSingle(r => (string)r["action"] == "delivery_cancelled" && (string)r["entityId"] == "del-escrow-pd"
                                           && ((string)r["metadata"]).Contains("reason=escrow_expired"));
    }

    [Fact]
    public async Task Initiate_PendingDelivery_PickedUpBeforeTheCancel_IsNotRefunded()
    {
        var e = Escrow("escrow-race");
        DeliveryIs("escrow-race", "pending");
        _deliveries.TryCancelPendingAsync("del-escrow-race").Returns(false);
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-race");
        Logs.Should().Contain("ESCROW_REFUND_DELIVERY_CANCEL_LOST escrowId=escrow-race");
        swept.AssertServed();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task Initiate_CancelledDelivery_NoLiveOrder_IsRefunded(string? orderStatus)
    {
        var e = Escrow("escrow-cd", orderId: orderStatus == null ? null : "ord-cd");
        DeliveryIs("escrow-cd", "cancelled");
        if (orderStatus != null)
        {
            OrderIs("ord-cd", orderStatus);
        }

        RefundSweep(e);

        await RunAsync(Cron());

        await Refunded("escrow-cd");
        // An /escrow/initiate escrow never cancels or restocks the order.
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Initiate_DeliveredDelivery_IsStamped_NotRefunded()
    {
        var e = Escrow("escrow-dl");
        DeliveryIs("escrow-dl", "delivered", d => d["confirmedAt"] = DateTime.UtcNow.AddHours(-3).ToString("o"));
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-dl");
        await _escrows.Received(1).UpdateStatusAsync("escrow-dl", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f!.ContainsKey("deliveryConfirmedAt")));
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_DisputedDelivery_FreezesTheEscrow()
    {
        var e = Escrow("escrow-idsp");
        DeliveryIs("escrow-idsp", "disputed");
        _escrows.GetByDeliveryIdAsync("del-escrow-idsp").Returns(new Dictionary<string, object> { { "escrowId", "escrow-idsp" } });
        var swept = RefundSweep(e);

        var response = await RunAsync(Cron());

        await NotRefunded("escrow-idsp");
        await _escrows.Received(1).UpdateStatusAsync("escrow-idsp", Arg.Any<string>(), "disputed", "escrowed",
            Arg.Any<Dictionary<string, object>?>());
        response.Body.Should().Contain("\"disputesFrozen\":\"1\"");
        swept.AssertServed();
    }

    [Theory]
    [InlineData("missing_delivery")]
    [InlineData("no_repository")]
    [InlineData("double_escrow")]
    [InlineData("unknown_order_status")]
    [InlineData("dispute_resolved")]
    public async Task Initiate_FailsClosed(string scenario)
    {
        var e = Escrow("escrow-fc", orderId: scenario is "double_escrow" or "unknown_order_status" or "dispute_resolved" ? "ord-fc" : null);
        var withDeliveries = scenario != "no_repository";
        var expected = scenario switch
        {
            "missing_delivery" => "refund:trade:delivery_missing",
            "no_repository" => "refund:trade:repo_unavailable",
            "double_escrow" => "refund:trade:double_escrow",
            "unknown_order_status" => "refund:trade:order_brand_new",
            _ => "refund:trade:dispute_resolved"
        };
        if (scenario != "missing_delivery")
        {
            DeliveryIs("escrow-fc", "cancelled");
        }

        if (scenario == "double_escrow")
        {
            OrderIs("ord-fc", "cancelled");
            var orderEscrow = Escrow("escrow-order-ord-fc", orderId: "ord-fc", orderSourced: true);
            _escrows.GetByIdAsync("escrow-order-ord-fc", true).Returns(orderEscrow);
        }
        else if (scenario == "unknown_order_status")
        {
            OrderIs("ord-fc", "brand_new");
        }
        else if (scenario == "dispute_resolved")
        {
            OrderIs("ord-fc", "dispute_resolved");
        }

        var swept = RefundSweep(e);

        await RunAsync(Cron(withDeliveries: withDeliveries));

        await NotRefunded("escrow-fc");
        _markers["escrow-fc"].Reason.Should().Be(expected);
        AuditRows("escrow-fc", "auto_refund_blocked").Should().ContainSingle();
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_OrderMissing_IsNotRefunded_AndFromTheCapPlus24HoursLogsOrderMissing()
    {
        var e = Escrow("escrow-omiss", orderId: "ord-omiss", createdDaysAgo: 23);
        DeliveryIs("escrow-omiss", "cancelled");
        var swept = RefundSweep(e);

        var response = await RunAsync(Cron());

        await NotRefunded("escrow-omiss");
        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-omiss reason=order_missing ageHours=");
        response.Body.Should().Contain("\"adminQueueOverdue\":\"1\"");
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_GoodsMovingAtTheCap_WithAnOrder_EntersTheAdminQueueForThisEscrow()
    {
        var e = Escrow("escrow-capq", orderId: "ord-capq", createdDaysAgo: 22);
        DeliveryIs("escrow-capq", "inTransit");
        OrderIs("ord-capq", "in_transit");
        _orders.TryEnterAdminQueueAsync("ord-capq", "hold_cap", "escrow-capq", Arg.Any<string>(), "in_transit").Returns(true);
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await NotRefunded("escrow-capq");
        await _orders.Received(1).TryEnterAdminQueueAsync("ord-capq", "hold_cap", "escrow-capq", Arg.Any<string>(), "in_transit");
        await _escrows.Received(1).UpdateStatusAsync("escrow-capq", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f!.ContainsKey(AdminQueueFields.HoldCapReachedAt)));
        await _escrows.DidNotReceive().TryExtendExpiryAsync("escrow-capq", Arg.Any<string>(), Arg.Any<string>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Initiate_GoodsMovingAtTheCap_NoOrder_LogsNoOrderEveryRun_AuditsOnce_NoEdgeAlarm()
    {
        var e = Escrow("escrow-noorder", createdDaysAgo: 23);
        DeliveryIs("escrow-noorder", "inTransit");
        var swept = RefundSweep(e);

        var first = await RunAsync(Cron());
        await RunAsync(Cron());

        await NotRefunded("escrow-noorder");
        Logs.Split('\n').Count(l => l.Contains("ADMIN_QUEUE_OVERDUE orderId=- reason=no_order ageHours=") && l.Contains("escrowId=escrow-noorder"))
            .Should().Be(2);
        first.Body.Should().Contain("\"adminQueueOverdue\":\"1\"");
        AuditRows("escrow-noorder", "auto_refund_blocked").Select(r => r["timestamp"]).Distinct().Should().ContainSingle();
        _markers["escrow-noorder"].Reason.Should().Be("refund:trade:hold_cap_no_order");
        _alarms.Should().BeEmpty();
        swept.AssertServed();
    }

    [Fact]
    public async Task OrderEscrows_KeepTheEsc06Gate_TheDeliveryIsNotAsked()
    {
        var e = Escrow("escrow-order-ord-o6", orderId: "ord-o6", orderSourced: true);
        OrderIs("ord-o6", "rejected");
        RefundSweep(e);

        await RunAsync(Cron());

        await Refunded("escrow-order-ord-o6");
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>(), true);
    }

    // ===== The admin queue has one entry per order =====

    [Fact]
    public async Task HoldCapQueue_EntryOwnedByAnotherEscrow_DoesNotStampThisOne()
    {
        var e = Escrow("escrow-order-ord-own", orderId: "ord-own", orderSourced: true, createdDaysAgo: 22);
        OrderIs("ord-own", "in_transit");
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        _orders.GetByIdAsync("ord-own", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-own" }, { "status", "in_transit" }, { "version", 3 },
            { AdminQueueFields.QueuedAt, "2026-10-01T00:00:00.0000000Z" }, { AdminQueueFields.EscrowId, "escrow-someone-else" }
        });
        var swept = RefundSweep(e);

        await RunAsync(Cron());

        await _escrows.DidNotReceive().UpdateStatusAsync("escrow-order-ord-own", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f!.ContainsKey(AdminQueueFields.HoldCapReachedAt)));
        AuditRows("escrow-order-ord-own", "hold_cap_reached").Should().BeEmpty();
        swept.AssertServed();
    }

    // ===== Every order status has a class =====

    [Fact]
    public void EveryOrderStatusOfTheStateMachine_HasAClassInTheSweeps()
    {
        var statuses = SyncFunction.OrderTransitions.Keys
            .Concat(SyncFunction.OrderTransitions.Values.SelectMany(v => v))
            .Distinct()
            .ToList();

        statuses.Should().NotBeEmpty();
        foreach (var status in statuses)
        {
            EscrowCronFunction.ClassifyOrder(status).Should().NotBe(EscrowCronFunction.OrderTradeClass.Unknown,
                $"order status '{status}' needs a class in EscrowCronFunction (ESC-10): in flight, idle or disputed");
        }

        EscrowCronFunction.ClassifyOrder("disbursing").Should().Be(EscrowCronFunction.OrderTradeClass.InFlight);
        EscrowCronFunction.ClassifyOrder("settled").Should().Be(EscrowCronFunction.OrderTradeClass.Idle);
        EscrowCronFunction.ClassifyOrder(null).Should().Be(EscrowCronFunction.OrderTradeClass.Unknown);
    }

    // ===== Reconciliation: one row never stops the others =====

    [Fact]
    public async Task Reconciliation_AConditionFailureIsAQuietSkip_AnErrorAlarms_TheOtherRowsGoOn()
    {
        Dictionary<string, object> Initiated(string id) => new()
        {
            { "escrowId", id }, { "status", "initiated" },
            { "createdAt", DateTime.UtcNow.AddHours(-5).ToString("o") }
        };

        var won = Initiated("escrow-webhook-won");
        var broken = Initiated("escrow-broken");
        var fine = Initiated("escrow-fine");
        _escrows.StubSweep("initiated", won, broken, fine);
        _escrows.UpdateStatusAsync("escrow-webhook-won", Arg.Any<string>(), "cancelled", "initiated", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("escrowed meanwhile"));
        _escrows.UpdateStatusAsync("escrow-broken", Arg.Any<string>(), "cancelled", "initiated", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var response = await RunAsync(Cron(), "reconciliation");

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"reconciledToCancelled\":\"1\"").And.Contain("\"total\":\"3\"");
        await _escrows.Received(1).UpdateStatusAsync("escrow-fine", Arg.Any<string>(), "cancelled", "initiated",
            Arg.Any<Dictionary<string, object>?>());
        Logs.Should().Contain("escrow-webhook-won is no longer initiated; skipped");
        Logs.Should().NotContain("escrowId=escrow-webhook-won action=");
        Logs.Should().Contain("ESCROW_SETTLEMENT_FAILED escrowId=escrow-broken action=reconciliation_exception outcome=failed");
    }

    [Fact]
    public async Task Reconciliation_PageQueryFailure_StillFailsTheRun()
    {
        _escrows.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => q.Status == "initiated"), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var response = await RunAsync(Cron(), "reconciliation");

        response.StatusCode.Should().Be(500);
        Logs.Should().Contain("action=sweep_failed outcome=failed code=reconciliation:Exception");
    }

    [Fact]
    public async Task Release_EscrowWithoutADelivery_IsBlocked_NoDelivery()
    {
        var e = Escrow("escrow-nodel", confirmed: true);
        e.Remove("deliveryId");
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());

        await NotReleased("escrow-nodel");
        _markers["escrow-nodel"].Reason.Should().Be("release:trade:no_delivery");
        swept.AssertServed();
    }

    [Fact]
    public async Task DisputeSweep_FarmerPayoutGateBlocks_IsQuietAndCounted()
    {
        var e = Escrow("escrow-dr-kyc");
        e["status"] = "dispute_resolved";
        e["disputeResolution"] = "farmer_wins";
        e.Remove("deliveryId"); // no order or delivery: the verdict mirror has nothing to move
        Row(e);
        var swept = _escrows.StubSweep("dispute_resolved", e);
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("farmer-1").Returns(new UserDto
        {
            UserId = "farmer-1",
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = false,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, users, _orders, _deliveries) { AlarmLog = _alarms.Add };
        var cron = new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries, _orders);

        var response = await RunAsync(cron);

        await NotReleased("escrow-dr-kyc");
        response.Body.Should().Contain("\"releaseBlocked\":\"1\"");
        Logs.Should().Contain("ESCROW_RELEASE_BLOCKED escrowId=escrow-dr-kyc code=ESCROW_029 sweep=dispute release");
        await _escrows.Received(1).TrySetSettlementBlockReasonAsync("escrow-dr-kyc", Arg.Any<string>(), "dispute_resolved",
            "release:payout:phone_unverified", Arg.Any<string>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Reconciliation_ConfirmLostToTheWebhook_IsAQuietSkip()
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "escrow-ref-won" }, { "status", "initiated" }, { "telebirrRef", "TB-1" },
            { "createdAt", DateTime.UtcNow.AddHours(-1).ToString("o") }
        };
        _escrows.StubSweep("initiated", escrow);
        _gateway.GetTransactionStatusAsync("TB-1").Returns(new TransactionStatusResponse
        {
            TransactionRef = "TB-1",
            Status = "completed",
            TelebirrRef = "TB-1",
            Amount = 0,
            UpdatedAt = DateTime.UtcNow.ToString("o")
        });
        _escrows.UpdateStatusAsync("escrow-ref-won", Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("escrowed by the webhook"));

        var response = await RunAsync(Cron(), "reconciliation");

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"reconciledToEscrowed\":\"0\"").And.Contain("\"skipped\":\"1\"");
        Logs.Should().Contain("escrow-ref-won is no longer initiated; skipped");
        Logs.Should().NotContain("Telebirr query failed");
    }
}
