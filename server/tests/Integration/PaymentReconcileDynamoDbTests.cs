using System.Globalization;
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
/// FUP-37 against real DynamoDB (LocalStack in CI): the queryOrder reconciliation with the real inbox,
/// slot and order conditions. A paid order whose notification never came is held, handed to the inbox
/// and funded once by the inbox sweep (the webhook's own funding path), never failed by the order cron;
/// a late notification of the same payment changes nothing. An order whose checkouts are all closed is
/// failed only after its verdict. A stuck checkout closed at Telebirr frees the buyer to open a new one.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PaymentReconcileDynamoDbTests : IDisposable
{
    private const string BuyerId = "buyer-fup37";
    private const string ListingCreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly RSA _telebirrKey = RSA.Create(2048);
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly string _orderId = $"order-fup37-{Guid.NewGuid():N}";
    private readonly string _listingId = $"crop-fup37-{Guid.NewGuid():N}";
    private readonly string _paymentOrderId = $"TB{Guid.NewGuid():N}"[..20].ToUpperInvariant();
    private string _cycle = "";

    public PaymentReconcileDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    public void Dispose() => _telebirrKey.Dispose();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private OrderRepository Orders => new(Ddb, _db.Orders);
    private OrderCheckoutRepository Checkouts => new(Ddb, _db.Idempotency);
    private WebhookInboxRepository Inbox => new(Ddb, _db.Idempotency);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private string Merch(int attempt) => EscrowService.BuildOrderMerchOrderId(_orderId, attempt);
    private string InboxKey => WebhookInboxRepository.KeyOf(Merch(1), _paymentOrderId, "Completed");

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    /// <summary>An order whose payment clock started <paramref name="submittedAgo"/> ago, its listing and checkout 1.</summary>
    private async Task SeedAsync(TimeSpan submittedAgo, string outcome = OrderCheckoutOutcome.UrlIssued, DateTime? openedAt = null)
    {
        var submitted = Now - submittedAgo;
        _cycle = WebhookInbox.Iso(submitted);
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(_listingId),
                ["createdAt"] = S(ListingCreatedAt),
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
                ["createdAt"] = S(WebhookInbox.Iso(submitted.AddHours(-1))),
                ["status"] = S("payment_submitted"),
                ["buyerId"] = S(BuyerId),
                ["farmerId"] = S("farmer-fup37"),
                ["listingId"] = S(_listingId),
                ["quantity"] = N("500"),
                ["orderTotal"] = N("15000"),
                ["cropCost"] = N("14600"),
                ["paymentSubmittedAt"] = S(_cycle),
                ["version"] = N("3")
            }
        });
        var opened = openedAt ?? submitted.AddHours(1);
        (await Checkouts.TryOpenSlotAsync(new OrderCheckoutSlot
        {
            OrderId = _orderId,
            BuyerId = BuyerId,
            Attempt = 1,
            MerchOrderId = Merch(1),
            Amount = 15000m,
            Outcome = outcome,
            OpenedAtUtc = opened,
            OpenUntilUtc = opened.AddMinutes(130)
        }, previousAttempt: null)).Should().BeTrue();
        (await Checkouts.StoreMappingAsync(Merch(1), _orderId, BuyerId, 15000m, _cycle)).Should().BeTrue();
    }

    private void TelebirrSays(string status, string? paymentOrderId = null)
        => _gateway.QueryOrderAsync(Merch(1)).Returns(new TelebirrQueryOrderResult
        {
            State = TelebirrQueryOrder.Classify(status, null).State,
            Status = status,
            MerchOrderId = Merch(1),
            PaymentOrderId = paymentOrderId,
            TotalAmount = "15000.00",
            TransCurrency = "ETB",
            TransId = "TXN-FUP37"
        });

    private async Task<OrderPaymentReconcileCounts> ReconcileAsync()
        => await new OrderPaymentReconciler(Orders, Checkouts, Inbox, _gateway, new EscrowAuditRepository(Ddb, _db.EscrowAudit), _clock)
            .RunAsync(() => true, new TestLambdaContext());

    private async Task<string> AutoCancelAsync()
    {
        var cron = new OrderCronFunction(Orders, new TransactionAuditRepository(Ddb, _db.TransactionAudit), Inbox,
            checkoutRepo: Checkouts);
        var context = new TestLambdaContext();
        await cron.FunctionHandler(new APIGatewayProxyRequest { Body = "{\"action\":\"auto_cancel_expired\"}" }, context);
        return ((TestLambdaLogger)context.Logger).Buffer.ToString();
    }

    private TelebirrWebhookFunction Webhook()
    {
        var escrowRepo = new EscrowRepository(Ddb, _db.Escrows);
        var audit = new EscrowAuditRepository(Ddb, _db.EscrowAudit);
        var escrowService = new EscrowService(escrowRepo, audit, idempotencyRepo: null, telebirrGateway: null,
            userRepo: null, orderRepo: Orders);
        return new TelebirrWebhookFunction(escrowRepo, audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency),
            new TelebirrSignatureService(null, _telebirrKey.ExportSubjectPublicKeyInfoPem(), TelebirrSignaturePadding.Pkcs1),
            orderCheckoutRepo: Checkouts,
            orderRepo: Orders,
            orderFunding: new OrderFundingService(Orders, new CropRepository(Ddb, _db.Crops), escrowService),
            txAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit),
            inboxRepo: Inbox,
            clock: _clock);
    }

    private static TestLambdaContext WebhookContext() => new() { RemainingTime = TimeSpan.FromSeconds(15) };

    /// <summary>The documented flat notify of the same payment, signed with the test "Telebirr" key.</summary>
    private string Notify()
    {
        var fields = new Dictionary<string, string>
        {
            ["notify_url"] = "https://api.example/webhooks/telebirr",
            ["appid"] = "853694808089634",
            ["notify_time"] = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["merch_code"] = "220100",
            ["merch_order_id"] = Merch(1),
            ["payment_order_id"] = _paymentOrderId,
            ["total_amount"] = "15000.00",
            ["trans_id"] = "TXN-FUP37",
            ["trans_currency"] = "ETB",
            ["trade_status"] = "Completed",
        };
        var signer = new TelebirrSignatureService(_telebirrKey.ExportPkcs8PrivateKeyPem(), null, TelebirrSignaturePadding.Pss);
        var body = fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body["sign"] = signer.Sign(signer.BuildFlatCanonicalString(fields));
        body["sign_type"] = "SHA256WithRSA";
        return JsonSerializer.Serialize(body);
    }

    private async Task<Dictionary<string, object>> OrderAsync() => (await Orders.GetByIdAsync(_orderId, consistentRead: true))!;

    private async Task<Dictionary<string, AttributeValue>> InboxRowAsync()
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(InboxKey) },
            ConsistentRead = true
        })).Item;

    private async Task<double> StockAsync()
    {
        var crop = (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Crops,
            Key = new Dictionary<string, AttributeValue> { ["cropId"] = S(_listingId), ["createdAt"] = S(ListingCreatedAt) },
            ConsistentRead = true
        })).Item;
        return double.Parse(crop["quantity"].N, CultureInfo.InvariantCulture);
    }

    [LocalDynamoDbFact]
    public async Task A_paid_order_with_no_notification_is_funded_once_through_the_inbox_and_never_failed()
    {
        await SeedAsync(TimeSpan.FromHours(25));
        TelebirrSays("PAY_SUCCESS", _paymentOrderId);

        (await ReconcileAsync()).Paid.Should().Be(1);

        var pending = await InboxRowAsync();
        pending["status"].S.Should().Be(WebhookInbox.Received);
        pending[WebhookInbox.Marker].S.Should().Be(WebhookInbox.Pending);
        (await OrderAsync()).Should().ContainKey(PaymentHoldFields.HoldUntil);
        var slot = (await Checkouts.GetSlotAsync(_orderId))!;
        slot.QueryVerdict.Should().Be(OrderCheckoutQuery.Paid);
        slot.QueryCycle.Should().Be(_cycle);

        // The order cron runs before the sweep: the payment is pending, so the order waits.
        await AutoCancelAsync();
        (await OrderAsync())["status"].ToString().Should().Be("payment_submitted");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await Webhook().FunctionHandler(new APIGatewayProxyRequest { Body = "{\"action\":\"webhook_inbox_replay\"}" }, WebhookContext());

        var funded = await OrderAsync();
        funded["status"].ToString().Should().Be("funded");
        funded[OrderFundingService.PaymentOrderIdField].ToString().Should().Be(_paymentOrderId);
        (await StockAsync()).Should().Be(5.0);
        (await new EscrowRepository(Ddb, _db.Escrows).QueryByOrderIdAsync(_orderId)).Should().HaveCount(1);
        (await InboxRowAsync())["status"].S.Should().Be("applied");

        // The notification finally arrives: the same inbox row, nothing funded twice.
        var late = await Webhook().FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/webhooks/telebirr",
            Body = Notify(),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext()
        }, WebhookContext());
        late.StatusCode.Should().Be(200);
        (await OrderAsync())["status"].ToString().Should().Be("funded");
        (await StockAsync()).Should().Be(5.0, "the stock is taken once");
        (await new EscrowRepository(Ddb, _db.Escrows).QueryByOrderIdAsync(_orderId)).Should().HaveCount(1);
        (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByEntityAsync(_orderId))
            .Count(e => e.TryGetValue("action", out var a) && a?.ToString() == "payment_verified").Should().Be(1);
        (await InboxRowAsync())["status"].S.Should().Be("applied_again");
    }

    [LocalDynamoDbFact]
    public async Task An_order_whose_checkouts_are_closed_fails_only_after_its_verdict()
    {
        await SeedAsync(TimeSpan.FromHours(25));
        TelebirrSays("ORDER_CLOSED");

        (await AutoCancelAsync()).Should().Contain("waits for the queryOrder check");
        (await OrderAsync())["status"].ToString().Should().Be("payment_submitted");

        (await ReconcileAsync()).NotPaid.Should().Be(1);
        (await Checkouts.GetSlotAsync(_orderId))!.QueryVerdict.Should().Be(OrderCheckoutQuery.NotPaid);
        (await InboxRowAsync()).Should().BeNullOrEmpty();

        await AutoCancelAsync();
        (await OrderAsync())["status"].ToString().Should().Be("payment_failed");
    }

    [LocalDynamoDbFact]
    public async Task An_unknown_checkout_closed_at_Telebirr_frees_the_buyer_and_its_verdict_dies_with_it()
    {
        await SeedAsync(TimeSpan.FromHours(2), OrderCheckoutOutcome.Unknown, openedAt: Now.AddMinutes(-10));
        TelebirrSays("ORDER_CLOSED");

        (await ReconcileAsync()).SlotsClosed.Should().Be(1);

        var slot = (await Checkouts.GetSlotAsync(_orderId))!;
        slot.Outcome.Should().Be(OrderCheckoutOutcome.Failed);
        slot.IsOpen(Now).Should().BeFalse("the buyer may open a new checkout now");

        // A verdict belongs to its attempt: the next checkout replaces the slot, and a late write is refused.
        (await Checkouts.RecordQueryVerdictAsync(_orderId, 1, OrderCheckoutQuery.Unresolved, _cycle, Now)).Should().BeTrue();
        (await Checkouts.TryOpenSlotAsync(slot with
        {
            Attempt = 2,
            MerchOrderId = Merch(2),
            Outcome = OrderCheckoutOutcome.Opening,
            OpenedAtUtc = Now,
            OpenUntilUtc = Now.AddMinutes(130),
            QueryVerdict = null,
            QueryCycle = null,
            QueriedAtUtc = null
        }, previousAttempt: 1)).Should().BeTrue();
        var next = (await Checkouts.GetSlotAsync(_orderId))!;
        next.QueryVerdict.Should().BeNull();
        (await Checkouts.RecordQueryVerdictAsync(_orderId, 1, OrderCheckoutQuery.NotPaid, _cycle, Now)).Should().BeFalse();
        (await Checkouts.GetSlotAsync(_orderId))!.QueryVerdict.Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task A_mapping_carries_its_own_status()
    {
        await SeedAsync(TimeSpan.FromHours(2));
        (await Checkouts.GetMappingAsync(Merch(1)))!.Status.Should().Be("processing");

        await new EscrowIdempotencyRepository(Ddb, _db.Idempotency).UpdateAsync(Merch(1), "failed");

        (await Checkouts.GetMappingAsync(Merch(1)))!.Status.Should().Be("failed");
    }
}
