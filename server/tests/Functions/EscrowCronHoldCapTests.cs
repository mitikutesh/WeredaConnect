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
/// ESC-06: an order escrow is never extended past funding + 21 days; at the cap its order enters
/// the admin stuck-order queue, whose hourly report produces the <c>ADMIN_QUEUE_OVERDUE</c> alarm
/// line and clears entries on escrow evidence only. Also the restock before the refund of a
/// buyer's transport-failure cancel.
/// </summary>
public class EscrowCronHoldCapTests
{
    private const string OrderId = "ord-1";
    private const string EscrowId = "escrow-order-ord-1";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _txRows = new();
    private readonly TestLambdaContext _context = new();

    private List<Dictionary<string, object>> _queue = new();

    public EscrowCronHoldCapTests()
    {
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.StubEmptySweeps();
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.QueryAdminQueueAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(_ => (_queue, (Dictionary<string, AttributeValue>?)null));
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-REF" });
        _txAudit.AppendAsync(Arg.Do<Dictionary<string, object>>(r => _txRows.Add(r))).Returns(Task.CompletedTask);
        _escrows.TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
    }

    private EscrowCronFunction MakeFunction()
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway);
        return new EscrowCronFunction(service, _escrows, _audit, _gateway,
            deliveryRepo: _deliveries, orderRepo: _orders, cropRepo: _crops, transactionAuditRepo: _txAudit);
    }

    private async Task<Dictionary<string, string>> RunAsync()
    {
        var response = await MakeFunction().FunctionHandler(new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", "auto_release_and_refund" } })
        }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    // ===== The cap =====

    [Theory]
    [InlineData(7.0)]
    [InlineData(15.0)]
    [InlineData(19.5)]
    [InlineData(20.0)]
    [InlineData(20.99)]
    public async Task ProgressingOrderBeforeTheCap_IsExtended_NeverPastFundingPlus21Days(double fundedDaysAgo)
    {
        var fundedAt = DateTime.UtcNow.AddDays(-fundedDaysAgo);
        var cap = fundedAt.AddDays(21);
        SweepFinds(Escrow(fundedAt));
        OrderIs("in_transit");
        string? extendedTo = null;
        _escrows.TryExtendExpiryAsync(EscrowId, Arg.Any<string>(), Arg.Do<string>(e => extendedTo = e)).Returns(true);

        await RunAsync();

        extendedTo.Should().NotBeNull();
        var extended = DateTime.Parse(extendedTo!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        extended.Should().BeOnOrBefore(cap.AddMilliseconds(1));
        extended.Should().BeCloseTo(Min(DateTime.UtcNow.AddHours(48), cap), TimeSpan.FromSeconds(5));
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        _swept!.AssertServed();
    }

    [Theory]
    [InlineData(21.0)]
    [InlineData(30.0)]
    public async Task AtFundingPlus21Days_NothingExtends_TheOrderEntersTheQueue_AndTheStampIsLast(double fundedDaysAgo)
    {
        var fundedAt = DateTime.UtcNow.AddDays(-fundedDaysAgo).AddMinutes(-1);
        SweepFinds(Escrow(fundedAt));
        OrderIs("accepted");
        _orders.TryEnterAdminQueueAsync(OrderId, "hold_cap", EscrowId, Arg.Any<string>(), "accepted").Returns(true);

        var summary = await RunAsync();

        await _escrows.DidNotReceive().TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        Received.InOrder(() =>
        {
            _orders.TryEnterAdminQueueAsync(OrderId, "hold_cap", EscrowId, Arg.Any<string>(), "accepted");
            _audit.AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(a => (string)a["action"] == "hold_cap_reached"));
            _escrows.UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
                Arg.Is<Dictionary<string, object>>(f => f.ContainsKey("holdCapReachedAt")));
        });
        _txRows.Should().ContainSingle(r => (string)r["action"] == "admin_queue_entered");
        summary["adminQueueEntered"].Should().Be("1");
        Logs.Should().Contain($"ORDER_ESCROW_HOLD_CAPPED orderId=ord-1 escrowId=escrow-order-ord-1");
        _swept!.AssertServed();
    }

    [Fact]
    public async Task AlreadyQueued_RepairsTheAuditAndTheStamp_WithTheQueueTime_AndAuditsTheEntryOnce()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-22)));
        OrderIs("accepted", queuedAt: "2026-10-01T08:00:00.0000000Z");
        _orders.TryEnterAdminQueueAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        await RunAsync();

        await _audit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(a =>
            (string)a["timestamp"] == "2026-10-01T08:00:00.0000000Z"
            && (string)a["auditId"] == EscrowAuditRepository.DeterministicAuditId("hold_cap_reached", "2026-10-01T08:00:00.0000000Z")));
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["holdCapReachedAt"] == "2026-10-01T08:00:00.0000000Z"));
        _txRows.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderMovedOnBeforeTheMarkerWrite_NothingIsStamped()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-22)));
        OrderIs("accepted");
        _orders.TryEnterAdminQueueAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        await RunAsync();

        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _audit.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
        _swept!.AssertServed();
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("payment_submitted")]
    public async Task NonRefundableStatusPastTheCap_IsQueuedToo(string status)
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-25)));
        OrderIs(status);
        _orders.TryEnterAdminQueueAsync(OrderId, "hold_cap", EscrowId, Arg.Any<string>(), status).Returns(true);

        await RunAsync();

        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, "hold_cap", EscrowId, Arg.Any<string>(), status);
    }

    [Fact]
    public async Task NonRefundableStatusBeforeTheCap_IsLeftAsBefore()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-10)));
        OrderIs("disputed");

        await RunAsync();

        await _escrows.DidNotReceive().TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        Logs.Should().Contain("needs reconciliation");
        _swept!.AssertServed();
    }

    [Fact]
    public async Task UnreadableFundingTime_CountsAsCapped()
    {
        var escrow = Escrow(DateTime.UtcNow);
        escrow["createdAt"] = "not-a-time";
        SweepFinds(escrow);
        OrderIs("accepted");
        _orders.TryEnterAdminQueueAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        await RunAsync();

        await _escrows.DidNotReceive().TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, "hold_cap", EscrowId, Arg.Any<string>(), "accepted");
        _swept!.AssertServed();
    }

    [Fact]
    public async Task MissingOrderPastTheCapBy24Hours_LogsTheOverdueLineEveryRun_AndIsNotStamped()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-21).AddHours(-30)));

        var summary = await RunAsync();

        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=order_missing ageHours=30");
        summary["adminQueueOverdue"].Should().Be("1");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        _swept!.AssertServed();
    }

    [Fact]
    public async Task MissingOrderJustPastTheCap_IsNotOverdueYet()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-21).AddHours(-2)));

        await RunAsync();

        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    // ===== Refund of a transport-failure cancel: stock first =====

    [Fact]
    public async Task BuyerCancelAfterTheCap_RestoresTheStock_ThenRefunds()
    {
        var escrow = Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "2026-10-01T08:00:00Z", refundReason: "order_transport_failed");
        SweepFinds(escrow);
        OrderIs("cancelled", cancelReason: "transport_failed_buyer_cancelled");
        _crops.RestoreStockAsync("listing-1", OrderId, 5.0).Returns(StockRestoreResult.Restored);

        await RunAsync();

        Received.InOrder(() =>
        {
            _crops.RestoreStockAsync("listing-1", OrderId, 5.0);
            _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        });
    }

    [Theory]
    [InlineData(StockRestoreResult.AlreadyRestored)]
    [InlineData(StockRestoreResult.ListingNotFound)]
    public async Task TransportCancel_StockAlreadyBackOrListingGone_Refunds(StockRestoreResult restock)
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-3), refundReason: "order_transport_failed"));
        OrderIs("cancelled", cancelReason: "transport_failed_buyer_cancelled");
        _crops.RestoreStockAsync("listing-1", OrderId, 5.0).Returns(restock);

        await RunAsync();

        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task TransportCancel_RestockKeepsFailing_BlocksTheRefundThisRun()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-3), refundReason: "order_transport_failed"));
        OrderIs("cancelled", cancelReason: "transport_failed_buyer_cancelled");
        _crops.RestoreStockAsync("listing-1", OrderId, 5.0).Returns(StockRestoreResult.Failed);

        await RunAsync();

        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        Logs.Should().Contain("ORDER_STOCK_RESTORE_FAILED orderId=ord-1 listingId=listing-1");
        _swept!.AssertServed();
    }

    [Fact]
    public async Task TransportCancel_NoPositiveQuantity_NeverBlocksTheRefund()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-3), refundReason: "order_transport_failed"));
        OrderIs("cancelled", cancelReason: "transport_failed_buyer_cancelled", quantity: 0);

        await RunAsync();

        await _crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        _swept!.AssertServed();
    }

    [Fact]
    public async Task OtherCancelReasons_DoNotRestockInTheSweep()
    {
        SweepFinds(Escrow(DateTime.UtcNow.AddDays(-3), refundReason: "order_cancelled"));
        OrderIs("cancelled", cancelReason: "changed my mind");

        await RunAsync();

        await _crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        _swept!.AssertServed();
    }

    // ===== The admin queue report =====

    [Fact]
    public async Task Report_EntryDueFor24Hours_LogsOneExactLine()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-30));
        EscrowRowIs(Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x"));
        OrderIs("in_transit");

        var summary = await RunAsync();

        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=hold_cap ageHours=30");
        summary["adminQueueOverdue"].Should().Be("1");
        await _orders.DidNotReceive().TryLeaveAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Report_EntryYoungerThan24Hours_IsQuiet()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-5));
        EscrowRowIs(Escrow(DateTime.UtcNow.AddDays(-21), holdCapReachedAt: "x"));
        OrderIs("in_transit");

        await RunAsync();

        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    [Fact]
    public async Task Report_AcceptedOrderWithALaterPickupDeadline_IsDueFromTheDeadline()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-80));
        EscrowRowIs(Escrow(DateTime.UtcNow.AddDays(-24), holdCapReachedAt: "x"));
        OrderIs("accepted", pickupDeadlineAt: DateTime.UtcNow.AddHours(-26));

        await RunAsync();

        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=hold_cap ageHours=26");
    }

    [Fact]
    public async Task Report_AcceptedOrderWhoseTripHasNotFailedYet_IsNotOverdue()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-80));
        EscrowRowIs(Escrow(DateTime.UtcNow.AddDays(-24), holdCapReachedAt: "x"));
        OrderIs("accepted", pickupDeadlineAt: DateTime.UtcNow.AddDays(2));

        await RunAsync();

        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    public static IEnumerable<object[]> EscrowEvidence() => new[]
    {
        new object[] { "missing" }, new object[] { "refunded" }, new object[] { "disputed" },
        new object[] { "manual_payout" }, new object[] { "delivery_confirmed" }
    };

    [Theory]
    [MemberData(nameof(EscrowEvidence))]
    public async Task Report_ClearsOnlyOnEscrowEvidence(string evidence)
    {
        var queuedAt = DateTime.UtcNow.AddHours(-40);
        QueueHas(queuedAt: queuedAt);
        EscrowRowIs(evidence switch
        {
            "missing" => null,
            "refunded" => Escrow(DateTime.UtcNow.AddDays(-23), status: "refunded"),
            "disputed" => Escrow(DateTime.UtcNow.AddDays(-23), status: "disputed"),
            "manual_payout" => Escrow(DateTime.UtcNow.AddDays(-23), manualPayout: true),
            _ => Escrow(DateTime.UtcNow.AddDays(-23), deliveryConfirmedAt: "2026-10-09T08:00:00Z")
        });
        OrderIs("accepted");
        _orders.TryLeaveAdminQueueAsync(OrderId, queuedAt.ToString("o")).Returns(true);

        var summary = await RunAsync();

        await _orders.Received(1).TryLeaveAdminQueueAsync(OrderId, queuedAt.ToString("o"));
        _txRows.Should().ContainSingle(r => (string)r["action"] == "admin_queue_cleared");
        summary["adminQueueCleared"].Should().Be("1");
        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task Report_OrderEndedWithoutARefund_IsReArmed_NotCleared(string status)
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-40));
        var escrow = Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x");
        EscrowRowIs(escrow);
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { escrow });
        OrderIs(status);

        await RunAsync();

        await _orders.DidNotReceive().TryLeaveAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>());
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == $"order_{status}"));
        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    [Fact]
    public async Task Report_DeliveredOrderWithAnUnstampedEscrow_GetsTheStampRepair()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-40));
        var escrow = Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x");
        escrow["deliveryId"] = "del-1";
        EscrowRowIs(escrow);
        OrderIs("delivered");
        _deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-1" }, { "status", "delivered" }, { "confirmedAt", "2026-10-09T08:00:00Z" }
        });

        await RunAsync();

        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryConfirmedAt"] == "2026-10-09T08:00:00Z"));
        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    [Fact]
    public async Task Report_OrderLaggingItsDeliveredDelivery_GetsTheStampRepairToo()
    {
        // The order status can lag its delivery: any queued entry with a linked delivered delivery is stamped.
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-40));
        var escrow = Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x");
        escrow["deliveryId"] = "del-1";
        EscrowRowIs(escrow);
        OrderIs("in_transit");
        _deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-1" }, { "status", "delivered" }, { "confirmedAt", "2026-10-09T08:00:00Z" }
        });

        await RunAsync();

        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["deliveryConfirmedAt"] == "2026-10-09T08:00:00Z"));
        Logs.Should().NotContain("ADMIN_QUEUE_OVERDUE");
    }

    [Fact]
    public async Task Report_LinkedDeliveryNotDeliveredYet_IsStillOverdue()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-30));
        var escrow = Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x");
        escrow["deliveryId"] = "del-1";
        EscrowRowIs(escrow);
        OrderIs("in_transit");
        _deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-1" }, { "status", "inTransit" }
        });

        await RunAsync();

        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=hold_cap ageHours=30");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Report_EntryThatCannotBeReviewed_IsNeverCleared_AndStillAlarmsWhenOld()
    {
        QueueHas(queuedAt: DateTime.UtcNow.AddHours(-30));
        _escrows.GetByIdAsync(EscrowId, true).ThrowsAsync(new InvalidOperationException("throttled"));

        await RunAsync();

        await _orders.DidNotReceive().TryLeaveAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>());
        Logs.Should().Contain("ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=hold_cap ageHours=30");
    }

    [Fact]
    public async Task Report_QueryFails_LogsADefiniteSweepFailure()
    {
        _orders.QueryAdminQueueAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new InvalidOperationException("index missing"));

        await RunAsync();

        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=admin_queue_query:InvalidOperationException");
    }

    [Fact]
    public async Task Report_HostileOrderId_CannotSpellASecondToken()
    {
        const string hostile = "ord_ADMIN_QUEUE_OVERDUE_ESCROW_SETTLEMENT_FAILED";
        _queue = new List<Dictionary<string, object>>
        {
            new()
            {
                { "orderId", hostile }, { "adminQueue", "open" },
                { "adminQueuedAt", DateTime.UtcNow.AddHours(-30).ToString("o") },
                { "adminQueueReason", "hold_cap" }, { "adminQueueEscrowId", EscrowId }
            }
        };
        EscrowRowIs(Escrow(DateTime.UtcNow.AddDays(-23), holdCapReachedAt: "x"));
        _orders.GetByIdAsync(hostile, true).Returns(new Dictionary<string, object> { { "orderId", hostile }, { "status", "in_transit" } });

        await RunAsync();

        var line = Logs.Split('\n').Single(l => l.Contains("ADMIN_QUEUE_OVERDUE"));
        System.Text.RegularExpressions.Regex.Matches(line, "ADMIN_QUEUE_OVERDUE").Should().HaveCount(1);
        line.Should().NotContain("ESCROW_SETTLEMENT_FAILED");
    }

    // ===== helpers =====

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>ESC-10: the refund sweep's stub, so a test that expects nothing to happen proves the row was read.</summary>
    private SweepStub? _swept;

    private void SweepFinds(Dictionary<string, object> escrow)
    {
        _swept = _escrows.StubRefundSweep(escrow);
        EscrowRowIs(escrow);
    }

    private void EscrowRowIs(Dictionary<string, object>? escrow)
    {
        _escrows.GetByIdAsync(EscrowId, true).Returns(escrow);
        _escrows.GetByIdAsync(EscrowId).Returns(escrow);
    }

    private void QueueHas(DateTime queuedAt)
        => _queue = new List<Dictionary<string, object>>
        {
            new()
            {
                { "orderId", OrderId }, { "adminQueue", "open" }, { "adminQueuedAt", queuedAt.ToString("o") },
                { "adminQueueReason", "hold_cap" }, { "adminQueueEscrowId", EscrowId }
            }
        };

    private void OrderIs(
        string status, string? queuedAt = null, string? cancelReason = null, double quantity = 500,
        DateTime? pickupDeadlineAt = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "status", status }, { "version", "3" },
            { "listingId", "listing-1" }, { "quantity", quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        if (queuedAt != null)
        {
            order["adminQueue"] = "open";
            order["adminQueuedAt"] = queuedAt;
        }

        if (cancelReason != null)
        {
            order["cancelReason"] = cancelReason;
        }

        if (pickupDeadlineAt != null)
        {
            order["pickupDeadlineAt"] = pickupDeadlineAt.Value.ToString("o");
        }

        _orders.GetByIdAsync(OrderId).Returns(order);
        _orders.GetByIdAsync(OrderId, true).Returns(order);
    }

    private static Dictionary<string, object> Escrow(
        DateTime fundedAt, string status = "escrowed", string? holdCapReachedAt = null, string? refundReason = null,
        bool manualPayout = false, string? deliveryConfirmedAt = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", EscrowId }, { "orderId", OrderId }, { "source", "order" },
            { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" },
            { "status", status }, { "amount", 2000m }, { "farmerPayout", 1900m }, { "commission", 100m },
            { "telebirrHoldRef", "HOLD-1" },
            { "createdAt", fundedAt.ToString("o") },
            { "expiresAt", DateTime.UtcNow.AddHours(-1).ToString("o") },
            { "version", 1 }
        };
        if (holdCapReachedAt != null)
        {
            escrow["holdCapReachedAt"] = holdCapReachedAt;
        }

        if (refundReason != null)
        {
            escrow["refundReason"] = refundReason;
        }

        if (manualPayout)
        {
            escrow["manualPayout"] = "open";
        }

        if (deliveryConfirmedAt != null)
        {
            escrow["deliveryConfirmedAt"] = deliveryConfirmedAt;
        }

        return escrow;
    }
}
