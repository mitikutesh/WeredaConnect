using Amazon.DynamoDBv2;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-37: the queryOrder reconciliation. A paid checkout reaches the webhook inbox exactly as a verified
/// notification does (hold first, the notification's key, trade status Completed); every checkout closed
/// settles the attempt as not paid; anything that decides nothing is alarmed and never settles it; a stuck
/// checkout closed at Telebirr frees the buyer; the run is bounded.
/// </summary>
public class OrderPaymentReconcilerTests
{
    private const string OrderId = "order-r1";
    private const string BuyerId = "buyer-r1";

    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkouts = Substitute.For<IOrderCheckoutRepository>();
    private readonly IWebhookInboxRepository _inbox = Substitute.For<IWebhookInboxRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly OrderPaymentReconciler _sut;

    public OrderPaymentReconcilerTests()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new List<Dictionary<string, object>>());
        _checkouts.RecordQueryVerdictAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>())
            .Returns(true);
        _inbox.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).Returns(true);
        _sut = new OrderPaymentReconciler(_orders, _checkouts, _inbox, _gateway, _audit, new FixedClock(Now));
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTime now) => _now = new DateTimeOffset(now);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static string Merch(int attempt, string orderId = OrderId) => EscrowService.BuildOrderMerchOrderId(orderId, attempt);

    /// <summary>A payment_submitted order whose payment clock started <paramref name="submittedAgo"/> ago.</summary>
    private static Dictionary<string, object> Order(TimeSpan submittedAgo, string orderId = OrderId)
        => new()
        {
            ["orderId"] = orderId,
            ["status"] = "payment_submitted",
            ["createdAt"] = WebhookInbox.Iso(Now - submittedAgo - TimeSpan.FromHours(1)),
            ["paymentSubmittedAt"] = Cycle(submittedAgo),
            ["version"] = "3"
        };

    private static string Cycle(TimeSpan submittedAgo) => WebhookInbox.Iso(Now - submittedAgo);

    /// <summary>The order's payment clock is 23.5 h old: inside the hour before its 24 h deadline.</summary>
    private static readonly TimeSpan Due = TimeSpan.FromHours(23.5);

    private void StubOrders(params Dictionary<string, object>[] orders)
        => _orders.QueryByStatusForCronAsync("payment_submitted", Arg.Any<string?>()).Returns(orders.ToList());

    private void StubSlot(int attempt, string outcome, DateTime openUntil, DateTime? openedAt = null,
        string? verdict = null, string? verdictCycle = null, DateTime? queriedAt = null, string orderId = OrderId)
        => _checkouts.GetSlotAsync(orderId).Returns(new OrderCheckoutSlot
        {
            OrderId = orderId,
            BuyerId = BuyerId,
            Attempt = attempt,
            MerchOrderId = Merch(attempt, orderId),
            Amount = 15000m,
            Outcome = outcome,
            OpenedAtUtc = openedAt ?? openUntil.AddMinutes(-130),
            OpenUntilUtc = openUntil,
            QueryVerdict = verdict,
            QueryCycle = verdictCycle,
            QueriedAtUtc = queriedAt
        });

    private void StubMapping(int attempt, string? cycle, string status = "processing", string orderId = OrderId)
        => _checkouts.GetMappingAsync(Merch(attempt, orderId))
            .Returns(new OrderCheckoutMapping(Merch(attempt, orderId), orderId, BuyerId, 15000m, cycle, status));

    private void Answer(int attempt, TelebirrQueryOrderResult result, string orderId = OrderId)
        => _gateway.QueryOrderAsync(Merch(attempt, orderId)).Returns(result);

    private static TelebirrQueryOrderResult Paid(string paymentOrderId = "TB-PAY-1")
        => new()
        {
            State = TelebirrPaymentState.Paid,
            Status = "PAY_SUCCESS",
            PaymentOrderId = paymentOrderId,
            TransId = "TXN-1",
            TotalAmount = "15000.00",
            TransCurrency = "ETB"
        };

    private static TelebirrQueryOrderResult Closed(string status = "ORDER_CLOSED")
        => new() { State = TelebirrPaymentState.NotPaid, Status = status };

    private Task<OrderPaymentReconcileCounts> RunAsync(Func<bool>? mayContinue = null)
        => _sut.RunAsync(mayContinue ?? (() => true), _context);

    private Task VerdictReceived(string verdict, int attempt = 1, TimeSpan? submittedAgo = null)
        => _checkouts.Received(1).RecordQueryVerdictAsync(OrderId, attempt, verdict, Cycle(submittedAgo ?? Due), Now);

    // ─── About to fail ───────────────────────────────────────────────────

    [Fact]
    public async Task A_paid_checkout_goes_to_the_inbox_as_a_verified_notification_would()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, Paid());

        var counts = await RunAsync();

        var key = $"webhook-inbox:{Merch(1)}:TB-PAY-1:Completed";
        Received.InOrder(() =>
        {
            _orders.TryHoldForPaymentAsync(OrderId, WebhookInbox.Iso(Now + WebhookInbox.ReplayHorizon), Cycle(Due));
            _inbox.RecordAsync(key, $"escrow-order-{OrderId}", OrderId, Merch(1),
                Arg.Is<TelebirrWebhookBiz>(b => b.MerchOrderId == Merch(1) && b.PaymentOrderId == "TB-PAY-1"
                                                && b.TradeStatus == "Completed" && b.TotalAmount == "15000.00"
                                                && b.TransCurrency == "ETB" && b.TransId == "TXN-1" && b.Msisdn == null),
                Now);
        });
        await VerdictReceived(OrderCheckoutQuery.Paid);
        await _audit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "order_payment_reconciled" && (string)e["escrowId"] == $"escrow-order-{OrderId}"
            && (string)e["telebirrRef"] == "TB-PAY-1"));
        counts.Paid.Should().Be(1);
        Logs.Should().Contain(OrderPaymentReconciler.ReconciledToken).And.NotContain(OrderPaymentReconciler.UnresolvedToken);
    }

    [Fact]
    public async Task A_payment_already_in_the_inbox_is_left_as_it_is_and_settles_the_attempt()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, Paid());
        _inbox.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).Returns(false);

        await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.Paid);
        await _audit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        Logs.Should().NotContain(OrderPaymentReconciler.ReconciledToken);
    }

    [Fact]
    public async Task Every_checkout_closed_settles_the_attempt_as_not_paid()
    {
        StubOrders(Order(Due));
        StubSlot(2, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(2, Cycle(Due));
        StubMapping(1, Cycle(Due));
        Answer(2, Closed());
        Answer(1, Closed("PAY_FAILED"));

        var counts = await RunAsync();

        await _gateway.Received(1).QueryOrderAsync(Merch(1));
        await _gateway.Received(1).QueryOrderAsync(Merch(2));
        await VerdictReceived(OrderCheckoutQuery.NotPaid, attempt: 2);
        await _inbox.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default!, default!, default!, default);
        counts.NotPaid.Should().Be(1);
        Logs.Should().NotContain(OrderPaymentReconciler.UnresolvedToken);
    }

    [Fact]
    public async Task A_payment_on_an_earlier_checkout_of_the_attempt_is_found()
    {
        // The buyer paid checkout 1, its notification never came, and they opened checkout 2.
        StubOrders(Order(Due));
        StubSlot(2, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(2, Cycle(Due));
        StubMapping(1, Cycle(Due));
        Answer(2, Closed());
        Answer(1, Paid());

        await RunAsync();

        await _inbox.Received(1).RecordAsync($"webhook-inbox:{Merch(1)}:TB-PAY-1:Completed", Arg.Any<string>(), OrderId,
            Merch(1), Arg.Any<TelebirrWebhookBiz>(), Now);
        await VerdictReceived(OrderCheckoutQuery.Paid, attempt: 2);
    }

    [Fact]
    public async Task Refused_preorders_unopened_attempts_and_earlier_payment_attempts_are_not_asked()
    {
        StubOrders(Order(Due));
        StubSlot(4, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(4, Cycle(Due));
        StubMapping(3, Cycle(Due), status: "failed");
        // Attempt 2 stopped before Telebirr: no mapping. Attempt 1 belongs to an earlier payment attempt.
        StubMapping(1, "2026-11-10T08:00:00.0000000Z");
        Answer(4, Closed());

        await RunAsync();

        await _gateway.Received(1).QueryOrderAsync(Arg.Any<string>());
        await _gateway.Received(1).QueryOrderAsync(Merch(4));
        await VerdictReceived(OrderCheckoutQuery.NotPaid, attempt: 4);
    }

    public static TheoryData<TelebirrQueryOrderResult> Undecided => new()
    {
        TelebirrQueryOrderResult.Failed(TelebirrGatewayErrors.Timeout),
        TelebirrQueryOrderResult.Failed("TELEBIRR_60200099"),
        new TelebirrQueryOrderResult { State = TelebirrPaymentState.Pending, Status = "WAIT_PAY" },
        new TelebirrQueryOrderResult { State = TelebirrPaymentState.Refunded, Status = "REFUND_SUCCESS" },
        new TelebirrQueryOrderResult { State = TelebirrPaymentState.Unknown, Status = "SOMETHING_NEW" },
        Paid(paymentOrderId: ""),
    };

    [Theory]
    [MemberData(nameof(Undecided))]
    public async Task An_answer_that_decides_nothing_is_alarmed_and_never_settles_the_attempt(TelebirrQueryOrderResult answer)
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, answer);

        var counts = await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.Unresolved);
        await _checkouts.DidNotReceive().RecordQueryVerdictAsync(OrderId, 1, OrderCheckoutQuery.NotPaid, Arg.Any<string>(), Arg.Any<DateTime>());
        await _inbox.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default!, default!, default!, default);
        counts.Unresolved.Should().Be(1);
        Logs.Should().Contain($"{OrderPaymentReconciler.UnresolvedToken} orderId={OrderId}");
    }

    [Fact]
    public async Task A_gateway_that_throws_is_an_answer_that_decides_nothing()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        _gateway.QueryOrderAsync(Merch(1)).ThrowsAsync(new HttpRequestException("down"));

        await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.Unresolved);
        Logs.Should().Contain("error=GATEWAY-EXCEPTION:HttpRequestException");
    }

    [Fact]
    public async Task An_inbox_write_that_fails_is_alarmed_and_never_settles_the_attempt()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, Paid());
        _inbox.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).ThrowsAsync(new AmazonDynamoDBException("brownout"));

        await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.Unresolved);
        Logs.Should().Contain(OrderPaymentReconciler.UnresolvedToken).And.Contain("reason=inbox-write-AmazonDynamoDBException");
    }

    [Fact]
    public async Task A_hold_that_fails_still_hands_the_payment_to_the_inbox_but_writes_no_verdict_this_run()
    {
        // The order cron may have read the pending inbox set before the row existed, so a paid verdict
        // without the hold could let it fail the order. The next run writes the hold and the verdict.
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, Paid());
        _orders.TryHoldForPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await RunAsync();

        await _inbox.Received(1).RecordAsync($"webhook-inbox:{Merch(1)}:TB-PAY-1:Completed", Arg.Any<string>(), OrderId,
            Merch(1), Arg.Any<TelebirrWebhookBiz>(), Now);
        await _checkouts.DidNotReceiveWithAnyArgs().RecordQueryVerdictAsync(default!, default, default!, default!, default);
        Logs.Should().Contain("no verdict this run").And.NotContain(OrderPaymentReconciler.UnresolvedToken);

        // Next run: the hold lands, the row is already there, the attempt is settled as paid.
        _orders.TryHoldForPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _inbox.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).Returns(false);

        await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.Paid);
    }

    [Fact]
    public async Task A_day_past_the_deadline_an_unresolved_attempt_is_given_up_alarmed_and_audited()
    {
        var longAgo = TimeSpan.FromHours(49);
        StubOrders(Order(longAgo));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-26));
        StubMapping(1, Cycle(longAgo));
        Answer(1, TelebirrQueryOrderResult.Failed(TelebirrGatewayErrors.Network));

        var counts = await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.GaveUp, submittedAgo: longAgo);
        await _audit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "order_payment_query_gave_up"));
        counts.GaveUp.Should().Be(1);
        Logs.Should().Contain("reason=gave_up");
    }

    [Fact]
    public async Task Too_many_checkouts_in_one_attempt_is_alarmed_and_never_settles_it()
    {
        var attempts = OrderPaymentReconciler.MaxAttemptsPerOrder + 1;
        StubOrders(Order(Due));
        StubSlot(attempts, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            StubMapping(attempt, Cycle(Due));
            Answer(attempt, Closed());
        }

        await RunAsync();

        await _gateway.Received(OrderPaymentReconciler.MaxAttemptsPerOrder).QueryOrderAsync(Arg.Any<string>());
        await VerdictReceived(OrderCheckoutQuery.Unresolved, attempt: attempts);
        Logs.Should().Contain("reason=too_many_attempts");
    }

    [Fact]
    public async Task An_order_not_due_yet_or_with_its_window_open_or_settled_is_not_asked()
    {
        StubOrders(Order(TimeSpan.FromHours(2), "o-early"), Order(Due, "o-open"), Order(Due, "o-settled"));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(1), orderId: "o-early");
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddMinutes(5), orderId: "o-open");
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2), verdict: OrderCheckoutQuery.NotPaid,
            verdictCycle: Cycle(Due), orderId: "o-settled");

        var counts = await RunAsync();

        await _gateway.DidNotReceiveWithAnyArgs().QueryOrderAsync(default!);
        counts.Seen.Should().Be(3);
        counts.Queries.Should().Be(0);
    }

    [Fact]
    public async Task A_verdict_about_an_earlier_payment_attempt_does_not_settle_this_one()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2), verdict: OrderCheckoutQuery.NotPaid,
            verdictCycle: "2026-11-10T08:00:00.0000000Z");
        StubMapping(1, Cycle(Due));
        Answer(1, Closed());

        await RunAsync();

        await VerdictReceived(OrderCheckoutQuery.NotPaid);
    }

    [Fact]
    public async Task An_order_without_a_checkout_is_never_asked()
    {
        StubOrders(Order(Due));

        await RunAsync();

        await _gateway.DidNotReceiveWithAnyArgs().QueryOrderAsync(default!);
        await _checkouts.DidNotReceiveWithAnyArgs().RecordQueryVerdictAsync(default!, default, default!, default!, default);
    }

    [Fact]
    public async Task A_run_asks_at_most_its_bound_and_leaves_the_rest_without_a_verdict()
    {
        var orders = Enumerable.Range(0, OrderPaymentReconciler.MaxQueriesPerRun + 5).Select(i => $"o-{i}").ToList();
        StubOrders(orders.Select(id => Order(Due, id)).ToArray());
        foreach (var id in orders)
        {
            StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2), orderId: id);
            StubMapping(1, Cycle(Due), orderId: id);
            Answer(1, Closed(), orderId: id);
        }

        var counts = await RunAsync();

        counts.Queries.Should().Be(OrderPaymentReconciler.MaxQueriesPerRun);
        counts.Truncated.Should().BeTrue();
        await _gateway.Received(OrderPaymentReconciler.MaxQueriesPerRun).QueryOrderAsync(Arg.Any<string>());
        Logs.Should().Contain($"{OrderPaymentReconciler.RunToken} seen={OrderPaymentReconciler.MaxQueriesPerRun}").And.Contain("truncated=true");
    }

    [Fact]
    public async Task A_run_out_of_time_stops_before_the_next_order()
    {
        StubOrders(Order(Due, "o-1"), Order(Due, "o-2"));
        var calls = 0;

        var counts = await RunAsync(() => ++calls <= 1);

        counts.Seen.Should().Be(1);
        counts.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_read_of_one_order_is_counted_and_the_walk_goes_on()
    {
        StubOrders(Order(Due, "o-broken"), Order(Due));
        _checkouts.GetSlotAsync("o-broken").ThrowsAsync(new AmazonDynamoDBException("throttled"));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, Closed());

        var counts = await RunAsync();

        counts.Errors.Should().Be(1);
        await VerdictReceived(OrderCheckoutQuery.NotPaid);
    }

    [Fact]
    public async Task An_unreadable_order_index_ends_the_run()
    {
        _orders.QueryByStatusForCronAsync("payment_submitted", Arg.Any<string?>()).ThrowsAsync(new AmazonDynamoDBException("x"));

        var counts = await RunAsync();

        counts.QueryFailed.Should().BeTrue();
    }

    [Fact]
    public async Task A_query_the_run_had_no_time_to_send_is_neither_alarmed_nor_recorded()
    {
        StubOrders(Order(Due));
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(-2));
        StubMapping(1, Cycle(Due));
        Answer(1, TelebirrQueryOrderResult.Failed(TelebirrGatewayErrors.NoTime));

        var counts = await RunAsync();

        counts.Truncated.Should().BeTrue();
        await _checkouts.DidNotReceiveWithAnyArgs().RecordQueryVerdictAsync(default!, default, default!, default!, default);
        Logs.Should().NotContain(OrderPaymentReconciler.UnresolvedToken);
    }

    // ─── A stuck checkout ────────────────────────────────────────────────

    private static readonly TimeSpan Early = TimeSpan.FromHours(2);

    private void StubStuck(string outcome = OrderCheckoutOutcome.Unknown, DateTime? queriedAt = null)
        => StubSlot(1, outcome, Now.AddHours(2), openedAt: Now.AddMinutes(-10), queriedAt: queriedAt,
            verdict: queriedAt == null ? null : OrderCheckoutQuery.Unresolved, verdictCycle: Cycle(Early));

    [Fact]
    public async Task An_unknown_checkout_closed_at_Telebirr_frees_the_buyer()
    {
        StubOrders(Order(Early));
        StubStuck();
        StubMapping(1, Cycle(Early));
        Answer(1, Closed());

        var counts = await RunAsync();

        await _checkouts.Received(1).UpdateSlotOutcomeAsync(OrderId, 1, OrderCheckoutOutcome.Failed);
        await _audit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "order_checkout_reconciled"));
        counts.SlotsClosed.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_checkout_that_was_paid_goes_to_the_inbox()
    {
        StubOrders(Order(Early));
        StubStuck();
        StubMapping(1, Cycle(Early));
        Answer(1, Paid());

        await RunAsync();

        await _inbox.Received(1).RecordAsync($"webhook-inbox:{Merch(1)}:TB-PAY-1:Completed", Arg.Any<string>(), OrderId,
            Merch(1), Arg.Any<TelebirrWebhookBiz>(), Now);
        await _checkouts.DidNotReceiveWithAnyArgs().UpdateSlotOutcomeAsync(default!, default, default!);
        // Only a pace for the next query: the check before T2 still asks every checkout.
        await _checkouts.Received(1).RecordQueryVerdictAsync(OrderId, 1, OrderCheckoutQuery.Unresolved, Cycle(Early), Now);
    }

    [Fact]
    public async Task An_unknown_checkout_still_payable_at_Telebirr_stays_blocked_without_a_page()
    {
        StubOrders(Order(Early));
        StubStuck();
        StubMapping(1, Cycle(Early));
        Answer(1, new TelebirrQueryOrderResult { State = TelebirrPaymentState.Pending, Status = "WAIT_PAY" });

        await RunAsync();

        await _checkouts.DidNotReceiveWithAnyArgs().UpdateSlotOutcomeAsync(default!, default, default!);
        await _checkouts.Received(1).RecordQueryVerdictAsync(OrderId, 1, OrderCheckoutQuery.Unresolved, Cycle(Early), Now);
        Logs.Should().NotContain(OrderPaymentReconciler.UnresolvedToken);
    }

    [Fact]
    public async Task A_stuck_checkout_asked_recently_or_an_opening_one_without_its_mapping_is_not_asked()
    {
        StubOrders(Order(Early, "o-recent"), Order(Early));
        StubSlot(1, OrderCheckoutOutcome.Unknown, Now.AddHours(2), openedAt: Now.AddMinutes(-40),
            verdict: OrderCheckoutQuery.Unresolved, verdictCycle: Cycle(Early), queriedAt: Now.AddMinutes(-10), orderId: "o-recent");
        StubStuck(OrderCheckoutOutcome.Opening);

        await RunAsync();

        await _gateway.DidNotReceiveWithAnyArgs().QueryOrderAsync(default!);
    }

    [Fact]
    public async Task A_checkout_just_opened_or_with_a_url_is_not_stuck()
    {
        StubOrders(Order(Early, "o-new"), Order(Early));
        StubSlot(1, OrderCheckoutOutcome.Unknown, Now.AddHours(2), openedAt: Now.AddMinutes(-1), orderId: "o-new");
        StubSlot(1, OrderCheckoutOutcome.UrlIssued, Now.AddHours(2), openedAt: Now.AddMinutes(-30));
        StubMapping(1, Cycle(Early), orderId: "o-new");
        StubMapping(1, Cycle(Early));

        await RunAsync();

        await _gateway.DidNotReceiveWithAnyArgs().QueryOrderAsync(default!);
    }
}
