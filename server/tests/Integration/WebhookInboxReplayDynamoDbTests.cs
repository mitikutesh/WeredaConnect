using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-45 against real DynamoDB (LocalStack in CI): the Telebirr webhook inbox and its replay sweep
/// with the real condition expressions and the sparse InboxReplayIndex. A transient failure after the
/// inbox write is recovered by the sweep with no human step; a redelivery racing the sweep funds once;
/// two sweeps claim a row once; a row is given up after 8 attempts, never over a finalize that won; and
/// the order cron never fails an order whose verified payment is still pending (D8).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WebhookInboxReplayDynamoDbTests : IDisposable
{
    private const string PaymentSubmittedAt = "2026-10-09T08:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly RSA _telebirrKey = RSA.Create(2048);
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);
    private readonly string _orderId = $"order-esc45-{Guid.NewGuid():N}";
    private readonly string _listingId = $"crop-esc45-{Guid.NewGuid():N}";
    private readonly string _paymentOrderId = $"TB{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    public WebhookInboxReplayDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    public void Dispose() => _telebirrKey.Dispose();

    private IAmazonDynamoDB Ddb => _db.Client;
    private OrderRepository Orders => new(Ddb, _db.Orders);
    private WebhookInboxRepository Inbox => new(Ddb, _db.Idempotency);
    private string MerchOrderId => EscrowService.BuildOrderMerchOrderId(_orderId, 1);
    private string InboxKey => WebhookInboxRepository.KeyOf(MerchOrderId, _paymentOrderId, "Completed");
    private string EscrowId => $"escrow-order-{_orderId}";

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private async Task SeedAsync(string orderCreatedAt = "2026-10-09T07:00:00.0000000Z", string paymentSubmittedAt = PaymentSubmittedAt)
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(_listingId),
                ["createdAt"] = S("2026-10-01T10:00:00.0000000Z"),
                ["status"] = S("active"),
                ["quantity"] = N("10"),
                ["version"] = N("1")
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(_orderId),
                ["createdAt"] = S(orderCreatedAt),
                ["status"] = S("payment_submitted"),
                ["buyerId"] = S("buyer-esc45"),
                ["farmerId"] = S("farmer-esc45"),
                ["listingId"] = S(_listingId),
                ["quantity"] = N("500"),
                ["orderTotal"] = N("15000"),
                ["cropCost"] = N("14600"),
                ["paymentSubmittedAt"] = S(paymentSubmittedAt),
                ["version"] = N("3")
            }
        });
        var checkouts = new OrderCheckoutRepository(Ddb, _db.Idempotency);
        (await checkouts.StoreMappingAsync(MerchOrderId, _orderId, "buyer-esc45", 15000m, paymentSubmittedAt))
            .Should().BeTrue();
    }

    /// <summary>The documented flat notify, signed with the test "Telebirr" key (PSS).</summary>
    private string Notify(DateTimeOffset? sentAt = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["notify_url"] = "https://api.example/webhooks/telebirr",
            ["appid"] = "853694808089634",
            ["notify_time"] = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(),
            ["merch_code"] = "220100",
            ["merch_order_id"] = MerchOrderId,
            ["payment_order_id"] = _paymentOrderId,
            ["total_amount"] = "15000.00",
            ["trans_id"] = "TXN-ESC45",
            ["trans_currency"] = "ETB",
            ["trade_status"] = "Completed",
            ["trans_end_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        };
        var signer = new TelebirrSignatureService(_telebirrKey.ExportPkcs8PrivateKeyPem(), null, TelebirrSignaturePadding.Pss);
        var body = fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body["sign"] = signer.Sign(signer.BuildFlatCanonicalString(fields));
        body["sign_type"] = "SHA256WithRSA";
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// An order repository whose consistent read fails <paramref name="failures"/> times (a DynamoDB
    /// brownout), then behaves like the real one.
    /// </summary>
    private IOrderRepository FlakyOrders(int failures)
    {
        var real = Orders;
        var flaky = Substitute.For<IOrderRepository>();
        var left = failures;
        flaky.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(call =>
        {
            if (Interlocked.Decrement(ref left) >= 0)
            {
                throw new AmazonDynamoDBException("brownout");
            }

            return real.GetByIdAsync(call.ArgAt<string>(0), call.ArgAt<bool>(1));
        });
        flaky.TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(call => real.TryMarkFundedAsync(call.ArgAt<string>(0), call.ArgAt<Dictionary<string, object>>(1)));
        flaky.TryHoldForPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(call => real.TryHoldForPaymentAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string?>(2)));
        return flaky;
    }

    private TelebirrWebhookFunction Webhook(IOrderRepository orders, ITransactionAuditRepository? txAudit = null)
    {
        var escrowRepo = new EscrowRepository(Ddb, _db.Escrows);
        var audit = new EscrowAuditRepository(Ddb, _db.EscrowAudit);
        var escrowService = new EscrowService(escrowRepo, audit, idempotencyRepo: null, telebirrGateway: null,
            userRepo: null, orderRepo: Orders);
        return new TelebirrWebhookFunction(escrowRepo, audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency),
            new TelebirrSignatureService(null, _telebirrKey.ExportSubjectPublicKeyInfoPem(), TelebirrSignaturePadding.Pkcs1),
            orderCheckoutRepo: new OrderCheckoutRepository(Ddb, _db.Idempotency),
            orderRepo: orders,
            orderFunding: new OrderFundingService(orders, new CropRepository(Ddb, _db.Crops), escrowService),
            txAuditRepo: txAudit ?? new TransactionAuditRepository(Ddb, _db.TransactionAudit),
            inboxRepo: Inbox,
            clock: _clock);
    }

    private static TestLambdaContext Context() => new() { RemainingTime = TimeSpan.FromSeconds(15) };

    private static APIGatewayProxyRequest Post(string body)
        => new() { HttpMethod = "POST", Path = "/webhooks/telebirr", Body = body, RequestContext = new APIGatewayProxyRequest.ProxyRequestContext() };

    private static APIGatewayProxyRequest Replay() => new() { Body = "{\"action\":\"webhook_inbox_replay\"}" };

    private static string Logs(TestLambdaContext context) => ((TestLambdaLogger)context.Logger).Buffer.ToString();

    private async Task<Dictionary<string, AttributeValue>> RawInboxRowAsync()
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(InboxKey) },
            ConsistentRead = true
        })).Item;

    private async Task<Dictionary<string, object>> OrderAsync()
        => (await Orders.GetByIdAsync(_orderId, consistentRead: true))!;

    private async Task<bool> InDueQueueAsync(DateTime at)
    {
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (rows, next) = await Inbox.QueryDueAsync(at, 100, start);
            if (rows.Any(r => r.Key == InboxKey))
            {
                return true;
            }

            start = next;
        }
        while (start != null);

        return false;
    }

    private async Task AssertFundedOnceAsync()
    {
        var order = await OrderAsync();
        order["status"].ToString().Should().Be("funded");
        order[OrderFundingService.PaymentOrderIdField].ToString().Should().Be(_paymentOrderId);

        var crop = (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Crops,
            Key = new Dictionary<string, AttributeValue> { ["cropId"] = S(_listingId), ["createdAt"] = S("2026-10-01T10:00:00.0000000Z") },
            ConsistentRead = true
        })).Item;
        double.Parse(crop["quantity"].N, System.Globalization.CultureInfo.InvariantCulture).Should().Be(5.0, "the stock is taken once");

        (await new EscrowRepository(Ddb, _db.Escrows).QueryByOrderIdAsync(_orderId)).Should().HaveCount(1);
        (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByEntityAsync(_orderId))
            .Count(e => e.TryGetValue("action", out var a) && a?.ToString() == "payment_verified").Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task A_transient_failure_after_the_inbox_write_is_recovered_by_the_sweep()
    {
        await SeedAsync();
        var webhook = Webhook(FlakyOrders(failures: 1));

        var first = await webhook.FunctionHandler(Post(Notify()), Context());

        first.StatusCode.Should().Be(500, "a verified payment that was not applied asks Telebirr to send it again");
        var pending = await RawInboxRowAsync();
        pending["status"].S.Should().Be("received");
        pending["inboxReplay"].S.Should().Be("pending");
        pending["orderId"].S.Should().Be(_orderId);
        (await OrderAsync()).Should().ContainKey(PaymentHoldFields.HoldUntil);

        // No redelivery arrives; the sweep runs after the first replay time.
        _clock.Advance(TimeSpan.FromMinutes(3));
        var sweepContext = Context();
        await webhook.FunctionHandler(Replay(), sweepContext);

        await AssertFundedOnceAsync();
        var done = await RawInboxRowAsync();
        done["status"].S.Should().Be("applied");
        done.Should().NotContainKey("inboxReplay").And.NotContainKey("notificationBiz");
        (await InDueQueueAsync(_clock.GetUtcNow().UtcDateTime.AddDays(1))).Should().BeFalse();
        Logs(sweepContext).Should().Contain("TELEBIRR_INBOX_REPLAY_RUN").And.Contain("applied=1");
    }

    [LocalDynamoDbFact]
    public async Task A_redelivery_and_the_sweep_at_the_same_moment_fund_once()
    {
        await SeedAsync();
        (await Webhook(FlakyOrders(failures: 1)).FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(500);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var webhook = Webhook(Orders);
        await Task.WhenAll(
            webhook.FunctionHandler(Post(Notify()), Context()),
            Webhook(Orders).FunctionHandler(Replay(), Context()));

        await AssertFundedOnceAsync();
        (await RawInboxRowAsync())["status"].S.Should().BeOneOf("applied", "applied_again");
    }

    [LocalDynamoDbFact]
    public async Task A_payment_verified_row_lost_at_funding_is_written_once_by_the_redeliveries()
    {
        // ESC-47 (2): the transaction-audit write after the funding fails; every later delivery of the
        // same payment takes the repair path, and the real conditional write keeps one row.
        await SeedAsync();
        var real = new TransactionAuditRepository(Ddb, _db.TransactionAudit);
        var flaky = Substitute.For<ITransactionAuditRepository>();
        var left = 1;
        flaky.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(call =>
            Interlocked.Decrement(ref left) >= 0
                ? Task.FromException<bool>(new AmazonDynamoDBException("brownout"))
                : real.AppendIfAbsentAsync(call.Arg<Dictionary<string, object>>()));
        flaky.GetByTransactionAsync(Arg.Any<string>()).Returns(call => real.GetByTransactionAsync(call.Arg<string>()));
        var webhook = Webhook(Orders, flaky);
        static bool IsPaymentVerified(Dictionary<string, object> e)
            => e.TryGetValue("action", out var a) && a?.ToString() == "payment_verified";

        (await webhook.FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(200);
        (await real.GetByTransactionAsync(_orderId)).Count(IsPaymentVerified).Should().Be(0);

        (await webhook.FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(200);
        (await webhook.FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(200);

        await AssertFundedOnceAsync();
        var row = (await real.GetByTransactionAsync(_orderId)).Single(IsPaymentVerified);
        row["timestamp"].ToString().Should().Be((await OrderAsync())["paymentVerifiedAt"].ToString());
        row["actor"].ToString().Should().Be("telebirr");
        (await RawInboxRowAsync())["status"].S.Should().Be("applied_again");

        // The key alone refuses a second row, even for a writer that does not look first.
        (await real.AppendIfAbsentAsync(new Dictionary<string, object>(row) { ["auditId"] = "txaudit-other" }))
            .Should().BeFalse();
        (await real.GetByTransactionAsync(_orderId)).Count(IsPaymentVerified).Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task Two_sweeps_at_once_claim_a_row_once()
    {
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, DateTime.UtcNow.AddMinutes(-10))).Should().BeTrue();

        // One order repository for both sweeps: each replay that runs reads the order once (and fails).
        var orders = FlakyOrders(failures: 100);
        await Task.WhenAll(
            Webhook(orders).FunctionHandler(Replay(), Context()),
            Webhook(orders).FunctionHandler(Replay(), Context()));

        (await RawInboxRowAsync())["replayAttempts"].N.Should().Be("1");
        orders.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IOrderRepository.GetByIdAsync))
            .Should().Be(1, "only the sweep that won the claim replays the row");
    }

    [LocalDynamoDbFact]
    public async Task A_claim_holds_only_for_the_replay_time_and_attempt_count_that_were_read()
    {
        // Two runners that read the same due row: the second claim must fail on both the replay time
        // and the attempt count, or both would replay the row in one round.
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        var now = DateTime.UtcNow;
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, now.AddMinutes(-10))).Should().BeTrue();
        var seen = (await Inbox.GetAsync(InboxKey))!;

        (await Inbox.TryClaimAsync(InboxKey, seen.ReplayAt!, seen.Attempts, now.AddMinutes(5), now)).Should().BeTrue();
        (await Inbox.TryClaimAsync(InboxKey, seen.ReplayAt!, seen.Attempts, now.AddMinutes(6), now))
            .Should().BeFalse("the first claim moved the replay time and counted the attempt");

        var afterFirst = (await Inbox.GetAsync(InboxKey))!;
        (await Inbox.TryClaimAsync(InboxKey, afterFirst.ReplayAt!, seen.Attempts, now.AddMinutes(7), now))
            .Should().BeFalse("a stale attempt count never claims, even with the current replay time");
        (await Inbox.TryClaimAsync(InboxKey, seen.ReplayAt!, afterFirst.Attempts, now.AddMinutes(8), now))
            .Should().BeFalse("a stale replay time never claims, even with the current attempt count");

        var row = await RawInboxRowAsync();
        row["replayAttempts"].N.Should().Be("1");
        row["inboxReplayAt"].S.Should().Be(WebhookInbox.Iso(now.AddMinutes(5)));
    }

    [LocalDynamoDbFact]
    public async Task An_always_failing_row_is_given_up_after_eight_attempts_with_one_alarm_and_one_audit_row()
    {
        await SeedAsync();
        var webhook = Webhook(FlakyOrders(failures: 1000));
        (await webhook.FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(500);

        var logs = new List<string>();
        for (var run = 0; run < 10; run++)
        {
            _clock.Advance(TimeSpan.FromMinutes(61));
            var context = Context();
            await webhook.FunctionHandler(Replay(), context);
            logs.Add(Logs(context));
        }

        var row = await RawInboxRowAsync();
        row["inboxReplay"].S.Should().Be("exhausted");
        row["status"].S.Should().Be("unprocessed");
        row["replayAttempts"].N.Should().Be("8");
        // The table is shared by the collection, so other tests' rows may be swept too: count ours.
        logs.SelectMany(l => l.Split('\n'))
            .Count(l => l.Contains("reason=replay_exhausted") && l.Contains(MerchOrderId)).Should().Be(1);
        (await new EscrowAuditRepository(Ddb, _db.EscrowAudit).GetAuditTrailAsync(EscrowId))
            .Count(e => e["action"].ToString() == "order_payment_unprocessed").Should().Be(1);
        (await OrderAsync())["status"].ToString().Should().Be("payment_submitted");
    }

    [LocalDynamoDbFact]
    public async Task A_finalize_that_wins_is_never_undone_by_a_give_up()
    {
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, DateTime.UtcNow.AddMinutes(-10))).Should().BeTrue();

        (await Inbox.FinalizeAsync(InboxKey, "applied")).Should().BeTrue();
        (await Inbox.ExhaustAsync(InboxKey, 0)).Should().BeFalse();
        (await Inbox.MarkUnprocessedAsync(InboxKey)).Should().BeFalse();

        var row = await RawInboxRowAsync();
        row["status"].S.Should().Be("applied");
        row.Should().NotContainKey("inboxReplay");
    }

    [LocalDynamoDbFact]
    public async Task A_row_whose_eighth_attempt_died_is_given_up_by_the_next_sweep()
    {
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, DateTime.UtcNow.AddHours(-5))).Should().BeTrue();
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(InboxKey) },
            UpdateExpression = "SET replayAttempts = :eight",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":eight"] = N("8") }
        });

        var context = Context();
        await Webhook(Orders).FunctionHandler(Replay(), context);

        (await RawInboxRowAsync())["inboxReplay"].S.Should().Be("exhausted");
        Logs(context).Split('\n').Should().Contain(l => l.Contains("reason=replay_exhausted") && l.Contains(MerchOrderId));
        (await OrderAsync())["status"].ToString().Should().Be("payment_submitted", "a given-up row is not replayed");
    }

    [LocalDynamoDbFact]
    public async Task A_requeued_row_that_applies_says_stop_any_manual_refund()
    {
        // The runbook requeue sets the row pending again; exhaustedAt stays, so the apply is flagged.
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, DateTime.UtcNow.AddHours(-5))).Should().BeTrue();
        (await Inbox.ExhaustAsync(InboxKey, 0)).Should().BeTrue();
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(InboxKey) },
            UpdateExpression = "SET inboxReplay = :p, replayAttempts = :z, inboxReplayAt = :now",
            ConditionExpression = "inboxReplay = :x",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":p"] = S("pending"),
                [":z"] = N("0"),
                [":x"] = S("exhausted"),
                [":now"] = S(WebhookInbox.Iso(DateTime.UtcNow.AddMinutes(-1)))
            }
        });

        var context = Context();
        await Webhook(Orders).FunctionHandler(Replay(), context);

        await AssertFundedOnceAsync();
        Logs(context).Split('\n').Should().Contain(l => l.Contains("stop any manual refund") && l.Contains(MerchOrderId));
        (await RawInboxRowAsync()).Should().ContainKey("exhaustedAt");
    }

    [LocalDynamoDbFact]
    public async Task The_due_query_returns_only_pending_rows_whose_time_has_come()
    {
        await SeedAsync();
        var biz = TelebirrNotifyParser.ToBiz(TelebirrNotifyParser.TryParse(Notify(), out var parsed, out _) ? parsed!.Fields : throw new InvalidOperationException());
        var now = DateTime.UtcNow;
        (await Inbox.RecordAsync(InboxKey, EscrowId, _orderId, MerchOrderId, biz, now)).Should().BeTrue();

        (await InDueQueueAsync(now)).Should().BeFalse("the first replay is two minutes after receipt");
        (await InDueQueueAsync(now.AddMinutes(3))).Should().BeTrue();

        // A row written before ESC-45 has no marker and is never indexed.
        var legacyKey = $"webhook-inbox:{MerchOrderId}:LEGACY:Completed";
        await new EscrowIdempotencyRepository(Ddb, _db.Idempotency).StoreAsync(legacyKey, EscrowId, "received", MerchOrderId);
        var (rows, _) = await Inbox.QueryDueAsync(now.AddDays(1), 100, null);
        rows.Should().NotContain(r => r.Key == legacyKey);

        await Inbox.FinalizeAsync(InboxKey, "applied");
        (await InDueQueueAsync(now.AddDays(1))).Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task The_order_cron_never_fails_an_order_whose_verified_payment_is_pending()
    {
        // D8, the 24 h race: the payment arrives at the end of the clock and its apply fails.
        var paymentSubmittedAt = DateTime.UtcNow.AddHours(-25).ToString("o");
        await SeedAsync(orderCreatedAt: DateTime.UtcNow.AddHours(-30).ToString("o"), paymentSubmittedAt: paymentSubmittedAt);
        var webhook = Webhook(FlakyOrders(failures: 1));
        (await webhook.FunctionHandler(Post(Notify()), Context())).StatusCode.Should().Be(500);
        var versionBefore = (await OrderAsync())["version"].ToString();

        var cron = new OrderCronFunction(Orders, new TransactionAuditRepository(Ddb, _db.TransactionAudit), Inbox);
        var cronRequest = new APIGatewayProxyRequest { Body = "{\"action\":\"auto_cancel_expired\"}" };
        await cron.FunctionHandler(cronRequest, new TestLambdaContext());

        var held = await OrderAsync();
        held["status"].ToString().Should().Be("payment_submitted");
        held["version"].ToString().Should().Be(versionBefore, "the hold never moves the clients' version");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await webhook.FunctionHandler(Replay(), Context());
        await AssertFundedOnceAsync();

        await cron.FunctionHandler(cronRequest, new TestLambdaContext());
        (await OrderAsync())["status"].ToString().Should().Be("funded");
    }

    [LocalDynamoDbFact]
    public async Task A_hold_written_after_the_cron_read_the_order_still_wins()
    {
        await SeedAsync();
        var read = await OrderAsync();
        var version = int.Parse(read["version"].ToString()!, System.Globalization.CultureInfo.InvariantCulture);

        (await Orders.TryHoldForPaymentAsync(_orderId, WebhookInbox.Iso(DateTime.UtcNow.AddHours(4)), PaymentSubmittedAt))
            .Should().BeTrue();
        (await Orders.TryMarkPaymentFailedAsync(_orderId, version, WebhookInbox.Iso(DateTime.UtcNow))).Should().BeFalse();

        (await OrderAsync())["status"].ToString().Should().Be("payment_submitted");
    }

    [LocalDynamoDbFact]
    public async Task The_hold_lapses_is_extend_only_and_belongs_to_one_payment_attempt()
    {
        await SeedAsync();
        var later = WebhookInbox.Iso(DateTime.UtcNow.AddHours(4));
        var sooner = WebhookInbox.Iso(DateTime.UtcNow.AddHours(1));

        (await Orders.TryHoldForPaymentAsync(_orderId, later, "2026-10-01T00:00:00.0000000Z"))
            .Should().BeFalse("a payment from an earlier attempt never holds the current one");
        (await Orders.TryHoldForPaymentAsync(_orderId, later, PaymentSubmittedAt)).Should().BeTrue();
        (await Orders.TryHoldForPaymentAsync(_orderId, sooner, PaymentSubmittedAt)).Should().BeFalse("extend-only");
        (await OrderAsync())[PaymentHoldFields.HoldUntil].ToString().Should().Be(later);

        var version = int.Parse((await OrderAsync())["version"].ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        (await Orders.TryMarkPaymentFailedAsync(_orderId, version, WebhookInbox.Iso(DateTime.UtcNow.AddHours(5))))
            .Should().BeTrue("a lapsed hold no longer protects the order");
        (await OrderAsync())["status"].ToString().Should().Be("payment_failed");
        (await Orders.TryHoldForPaymentAsync(_orderId, WebhookInbox.Iso(DateTime.UtcNow.AddHours(9)), PaymentSubmittedAt))
            .Should().BeFalse("only a payment_submitted order is held");
    }
}
