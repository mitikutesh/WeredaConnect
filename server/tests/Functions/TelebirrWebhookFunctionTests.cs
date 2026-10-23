using System.Text.Json;
using Amazon.DynamoDBv2;
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
/// Verifies the Telebirr webhook handler: a valid signed "Completed" moves the
/// escrow initiated→escrowed; "Failed" cancels it; and each security layer
/// (bad signature, stale notify_time, replay, amount mismatch) blocks the
/// transition. Notifications use the documented flat body (ESC-45).
/// </summary>
public class TelebirrWebhookFunctionTests
{
    private const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA6yIoxNNWCDxM/XbGqwXv\nF6wL8j9LNkBbhc/5wIvTOqDiUY+PAKMeJPOSnDPmz/BUs1w0sILm3J2KMxpAiqIB\nuQ6c2WbLXrsdF6nKPQSsOSC8lqX6ZTrWEtQvlHgyIuBWCEIyd7jwTaKzJmonT10J\nt2F+ZY+1JdD6XLHh8ea1gSUb50X4VVm+zZzGU0SX+cul+RktVgsLZS85z74vVgNQ\nXnaWa9D/zjtr22A30K4KbZ51DmnWVvrwj/yNPu+AMzehhoYtessyK6gZgbbWhkAD\nzpKCtpnui86D7RxYy/UJCNklWGBq+GrEeR0aAKzwiQATYmKMBPzH652gladoyLBw\nTQIDAQAB\n-----END PUBLIC KEY-----\n";

    private const string PrivateKeyPem =
        "-----BEGIN PRIVATE KEY-----\nMIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDrIijE01YIPEz9\ndsarBe8XrAvyP0s2QFuFz/nAi9M6oOJRj48Aox4k85KcM+bP8FSzXDSwgubcnYoz\nGkCKogG5DpzZZsteux0Xqco9BKw5ILyWpfplOtYS1C+UeDIi4FYIQjJ3uPBNorMm\naidPXQm3YX5lj7Ul0PpcseHx5rWBJRvnRfhVWb7NnMZTRJf5y6X5GS1WCwtlLznP\nvi9WA1BedpZr0P/OO2vbYDfQrgptnnUOadZW+vCP/I0+74AzN6GGhi16yzIrqBmB\nttaGQAPOkoK2me6LzoPtHFjL9QkI2SVYYGr4asR5HRoArPCJABNiYowE/MfrnaCV\np2jIsHBNAgMBAAECggEAWZzGLuVgZ3kb2om3A50aV0pBr0yCDKYQe3oc9E4V7cU9\nG9p7pMGGDo0p9hAtylgM3+KnThJFjBUag4lgybUetPmlu5FatrrXRpp2MjUisg8Q\n8NVHDeeTiM+HZq08ZmBbSYB5HXnFU8FLR7uzSaLqvhIRCwHvBv+TO1qsAvZrfFD+\nHyXkRxRAqG02YwOBl/pdnlH+nHNmhsZgrf8Y5a1/9m1n5Mnh1+aZNxo/VphLCKLl\nwtyBzpZMS/oC+UYrSNZDyzPR5FHefw8lT7QL52r34qgxWRwVg3t6ZMJkARxT2mFB\nJUkMBXF++sC0JSyQNWm6ErSp00M/i3DGLqvn0n2kvQKBgQD/Gx847DNM2RDlt4Ux\nFqoIcy74qggu/RaKkfU16JBdzDJvy9kQ75Gxts+QrvoubNHt5/xq1SOFpOPNmbp/\nF7qhZm3aXItiDzQF3QUuQIFsf3aOhWBaldBJYzbXsAKYHrXsV6Xv3Lp1rWBhsYzL\nkdvq+Hqa5Dr7FT2HAdns24jLOwKBgQDr9R5BvidMbNXEdg0l2YHsUE3XVgdR18Ez\ndITwkGffIQGZGUh01QDlt1pGbg3qKIbEKD0jZsgq7afnJMpdELCkEglpKcrmSqlZ\ndsh6AHMMtbcmk0waQN2A5gfkwl0WNcw6+bdCad21W/2r8HReyvzDqukdel32YawP\n8tWgmEOqFwKBgGhS/hy4M2584nUe7OAbRaO4/v+AAWK962b1s5K9LbS+Ge4wLe/I\nGQvVF+MyO2ua4uro0Qgba7Busx5UfLkWyiLREaGX8QRD9WFkdoboqETnh2NXVgUV\neR1Z7oacGyN8g6dIXLrPyIzxPxYUluB1ILthrDoFlb0yfXvlnOpJCRMbAoGAaYxo\nUHhDFAjLRuqXzwqQ96ikaWLuO0oL/nxsyz+Nd15IXLfWlYyCrj9qfn7Ucp5WSoDF\neISNncZZiSgH7cF9gqce1o//VyymvIJKA2kYDeNrXjIdpNDHRNWhSk83XKwMA4WY\n2HiK2Imh27vgElcy4kQdqzYFuiTO1wOFVCVIf1kCgYAWDAwS2TyAxJo8i/u3z7Al\nFY74wqYCVuy1HtR164CMdR3Qh8y7I5PevHpjz5PLLIubfWAcDREuOvMqkFun0Vdx\nR7uQCfGXjvI7LWssollFoTk31jH1GRsjIgyRI20/tAt9s15fJQhQvlgjOh45fWU8\nGztgay7ux2C/q4RWFqd/jw==\n-----END PRIVATE KEY-----\n";

    private const string EscrowId = "escrow-webhook-1";
    private const string MerchOrderId = "buyer-1:delivery-1:initiate:1700000000000";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idemRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly TestLambdaContext _ctx = new();

    public TelebirrWebhookFunctionTests()
    {
        _idemRepo.GetAsync(MerchOrderId).Returns(new Dictionary<string, object> { { "escrowId", EscrowId } });
        _escrowRepo.GetByIdAsync(EscrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", EscrowId },
            { "createdAt", "2026-07-07T10:00:00.0000000Z" },
            { "amount", 15000m },
            { "status", "initiated" },
        });
        // First webhook delivery (not a replay).
        _idemRepo.StoreAsync(Arg.Is<string>(k => k.StartsWith("webhook:")), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        // A real order trail: created and submitted for payment, no payment_verified row yet (a test
        // that needs another trail stubs it again).
        _txAuditRepo.GetByTransactionAsync(Arg.Any<string>()).Returns(_ => OrderTrailBeforeFunding());
    }

    /// <summary>The rows sync writes for every order before it is paid: order_created and order_updated.</summary>
    private static List<Dictionary<string, object>> OrderTrailBeforeFunding() => new()
    {
        new() { { "transactionId", "order-1" }, { "timestamp", "2026-09-30T08:00:00.0000000Z" },
            { "action", "order_created" }, { "toState", "created" } },
        new() { { "transactionId", "order-1" }, { "timestamp", "2026-09-30T08:30:00.0000000Z" },
            { "action", "order_updated" }, { "fromState", "created" }, { "toState", "payment_submitted" } },
    };

    private TelebirrWebhookFunction NewFunction(string? merchantCode = null,
        IUserRepository? userRepo = null, bool requirePayerMatch = false, IFraudDetectionService? fraudService = null)
        => new(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1),
            merchantCode: merchantCode, userRepo: userRepo, requirePayerMatch: requirePayerMatch,
            fraudService: fraudService);

    /// <summary>
    /// The notify Telebirr documents (Notify_Callback, "Step 7"): one flat object, signed over every
    /// field but sign and sign_type (ESC-45). <paramref name="transIdOnWire"/> sends the transaction
    /// id as transId while it is signed as trans_id, as live Telebirr has been seen to do.
    /// </summary>
    private static string SignedBody(string tradeStatus, string totalAmount = "15000.00", bool tamper = false,
        long? notifyTimeMs = null, string? rawNotifyTime = null, string? msisdn = null,
        string merchOrderId = MerchOrderId, string paymentOrderId = "TB123", string transCurrency = "ETB",
        bool transIdOnWire = false, TelebirrSignaturePadding padding = TelebirrSignaturePadding.Pkcs1)
    {
        var fields = SignedFields(tradeStatus, totalAmount, notifyTimeMs, rawNotifyTime, msisdn,
            merchOrderId, paymentOrderId, transCurrency);
        var sign = Sign(fields, padding);
        if (tamper)
        {
            sign = "AAAA" + sign[4..];
        }

        var body = fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        if (transIdOnWire)
        {
            body["transId"] = body["trans_id"];
            body.Remove("trans_id");
        }

        body["sign"] = sign;
        body["sign_type"] = "SHA256WithRSA";
        return JsonSerializer.Serialize(body);
    }

    private static Dictionary<string, string> SignedFields(string tradeStatus, string totalAmount = "15000.00",
        long? notifyTimeMs = null, string? rawNotifyTime = null, string? msisdn = null,
        string merchOrderId = MerchOrderId, string paymentOrderId = "TB123", string transCurrency = "ETB")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fields = new Dictionary<string, string>
        {
            ["notify_url"] = "https://api.example/webhooks/telebirr",
            ["appid"] = "853694808089634",
            ["notify_time"] = rawNotifyTime ?? (notifyTimeMs ?? now).ToString(),
            ["merch_code"] = "220100",
            ["merch_order_id"] = merchOrderId,
            ["payment_order_id"] = paymentOrderId,
            ["total_amount"] = totalAmount,
            ["trans_id"] = "TXN9",
            ["trans_currency"] = transCurrency,
            ["trade_status"] = tradeStatus,
            ["trans_end_time"] = now.ToString(),
        };
        if (msisdn != null)
        {
            fields["msisdn"] = msisdn;
        }

        return fields;
    }

    private static string Sign(IReadOnlyDictionary<string, string> fields,
        TelebirrSignaturePadding padding = TelebirrSignaturePadding.Pkcs1)
    {
        var signer = new TelebirrSignatureService(PrivateKeyPem, null, padding);
        return signer.Sign(signer.BuildFlatCanonicalString(fields));
    }

    private static IUserRepository BuyerWithPhone(string phone)
    {
        var repo = Substitute.For<IUserRepository>();
        repo.GetUserByIdAsync("buyer-1").Returns(new UserDto
        {
            UserId = "buyer-1",
            PhoneNumber = phone,
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-07-07T09:00:00Z",
            UpdatedAt = "2026-07-07T09:00:00Z",
            Version = 1
        });
        return repo;
    }

    private static APIGatewayProxyRequest Req(string body)
        => new() { HttpMethod = "POST", Path = "/webhooks/telebirr", Body = body };

    [Fact]
    public async Task Completed_transitions_initiated_to_escrowed()
    {
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, "2026-07-07T10:00:00.0000000Z", "escrowed", "initiated",
            Arg.Any<Dictionary<string, object>>());
        await _idemRepo.Received(1).UpdateAsync(MerchOrderId, "completed", Arg.Any<string?>());
    }

    [Fact]
    public async Task Failed_transitions_initiated_to_cancelled()
    {
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Failed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, "2026-07-07T10:00:00.0000000Z", "cancelled", "initiated", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Bad_signature_does_not_transition()
    {
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed", tamper: true)), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Amount_mismatch_does_not_transition()
    {
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed", totalAmount: "99999.00")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Stale_timestamp_does_not_transition()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed", notifyTimeMs: stale)), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Replayed_delivery_does_not_transition_again()
    {
        // Idempotency is enforced by the escrow CAS (not an upfront dedupe key, which
        // would strand a paid payment if the transition failed transiently): a prior
        // delivery already moved the escrow out of 'initiated', so the conditional
        // write throws and the handler treats the replay as a 200 no-op.
        _escrowRepo.UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("escrow already processed"));

        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed")), _ctx);

        resp.StatusCode.Should().Be(200);
        // The replay/dedupe key + idempotency completion are recorded only AFTER a
        // successful transition, so a CAS-failing replay does not mark completion.
        await _idemRepo.DidNotReceive().UpdateAsync(MerchOrderId, "completed");
    }

    // ===== P0-6: fail-closed timestamp and merchant binding =====

    [Fact]
    public async Task Unparseable_timestamp_does_not_transition()
    {
        await NewFunction().FunctionHandler(Req(SignedBody("Completed", rawNotifyTime: "not-a-number")), _ctx);

        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Empty_timestamp_does_not_transition()
    {
        await NewFunction().FunctionHandler(Req(SignedBody("Completed", rawNotifyTime: "")), _ctx);

        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Foreign_merch_code_does_not_transition()
    {
        await NewFunction(merchantCode: "999999").FunctionHandler(Req(SignedBody("Completed")), _ctx);

        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Matching_merch_code_transitions()
    {
        await NewFunction(merchantCode: "220100").FunctionHandler(Req(SignedBody("Completed")), _ctx);

        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
    }

    // ===== P0-6: payer binding =====

    private void EscrowHasBuyer()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", EscrowId },
            { "buyerId", "buyer-1" },
            { "createdAt", "2026-07-07T10:00:00.0000000Z" },
            { "amount", 15000m },
            { "status", "initiated" },
        });
    }

    [Fact]
    public async Task Payer_mismatch_is_recorded_but_allowed_when_not_required()
    {
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"));

        await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: "0922000002")), _ctx);

        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
        await _auditRepo.Received().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e.Values.Any(v => v != null && v.ToString() == "webhook_payer_mismatch")));
    }

    [Fact]
    public async Task Payer_mismatch_is_rejected_when_required()
    {
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true);

        await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: "+251922000002")), _ctx);

        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Matching_payer_transitions_even_when_required()
    {
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true);

        await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: "0911000001")), _ctx);

        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Missing_payer_is_rejected_only_when_required()
    {
        EscrowHasBuyer();

        await NewFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true)
            .FunctionHandler(Req(SignedBody("Completed")), _ctx);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());

        await NewFunction(userRepo: BuyerWithPhone("+251911000001"))
            .FunctionHandler(Req(SignedBody("Completed")), _ctx);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
    }

    // ===== ESC-01: live order payments =====

    private const string OrderId = "order-1";
    private const string OrderMerchOrderId = "WCOORDER101";
    private const string OrderPaymentId = "TB-ORD-1";
    private const string OrderEscrowId = "escrow-order-order-1";

    private readonly IOrderCheckoutRepository _checkoutRepo = Substitute.For<IOrderCheckoutRepository>();
    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _cropRepo = Substitute.For<ICropRepository>();
    private readonly ITransactionAuditRepository _txAuditRepo = Substitute.For<ITransactionAuditRepository>();
    private readonly IWebhookInboxRepository _inboxRepo = Substitute.For<IWebhookInboxRepository>();

    private TelebirrWebhookFunction NewOrderFunction(
        bool wireOrders = true, IUserRepository? userRepo = null, bool requirePayerMatch = false,
        IFraudDetectionService? fraudService = null)
    {
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId)
            .Returns(new OrderCheckoutMapping(OrderMerchOrderId, OrderId, "buyer-1", 15000m));
        _inboxRepo.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).Returns(true);
        _inboxRepo.FinalizeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _inboxRepo.MarkUnprocessedAsync(Arg.Any<string>()).Returns(true);
        _ctx.RemainingTime = TimeSpan.FromSeconds(15);
        var escrowService = new EscrowService(_escrowRepo, _auditRepo, null, null, orderRepo: _orderRepo);
        return new TelebirrWebhookFunction(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1),
            userRepo: userRepo, requirePayerMatch: requirePayerMatch,
            orderCheckoutRepo: _checkoutRepo,
            orderRepo: wireOrders ? _orderRepo : null,
            orderFunding: wireOrders ? new OrderFundingService(_orderRepo, _cropRepo, escrowService) : null,
            txAuditRepo: _txAuditRepo,
            inboxRepo: _inboxRepo,
            clock: _clock,
            fraudService: fraudService);
    }

    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);

    /// <summary>A clock the sweep and freshness rules read; advanced by the replay tests.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The funding time of a paid order in these tests: also the key of its payment_verified row.</summary>
    private const string VerifiedAt = "2026-09-30T09:00:00.0000000Z";

    private static Dictionary<string, object> StoredOrder(string status, string? paymentOrderId = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" },
            { "listingId", "listing-1" }, { "quantity", "500" }, { "orderTotal", "15000" },
            { "cropCost", "14000" }, { "transportCost", "300" }, { "status", status }, { "version", "4" }
        };
        if (paymentOrderId != null)
        {
            order["telebirrPaymentOrderId"] = paymentOrderId;
            order["paymentVerifiedAt"] = VerifiedAt;
        }

        return order;
    }

    private static string OrderBody(string tradeStatus = "Completed", string totalAmount = "15000.00",
        string paymentOrderId = OrderPaymentId, string transCurrency = "ETB", string? msisdn = null)
        => SignedBody(tradeStatus, totalAmount, msisdn: msisdn,
            merchOrderId: OrderMerchOrderId, paymentOrderId: paymentOrderId, transCurrency: transCurrency);

    private Task AuditRecorded(string action)
        => _auditRepo.Received().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e.Values.Any(v => v != null && v.ToString() == action)));

    private const string OrderInboxKey = $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed";

    private string Logs => ((TestLambdaLogger)_ctx.Logger).Buffer.ToString();

    [Fact]
    public async Task Order_payment_funds_the_order_once_with_its_side_effects()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0).Returns(StockDecrementResult.Decremented);

        var resp = await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["telebirrPaymentOrderId"] == OrderPaymentId
            && (string)f["telebirrMerchOrderId"] == OrderMerchOrderId
            && (string)f["telebirrTransId"] == "TXN9"
            && f.ContainsKey("paymentVerifiedAt")));
        await _cropRepo.Received(1).TryDecrementStockAsync("listing-1", OrderId, 5.0);
        await _escrowRepo.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(r =>
            (string)r["escrowId"] == OrderEscrowId && (string)r["telebirrRef"] == OrderPaymentId));
        // ESC-47: one conditional write keyed by the funding time, so a redelivery can never add a second row.
        await _txAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "payment_verified" && (string)e["transactionId"] == OrderId
            && (string)e["timestamp"] == VerifiedAt && (string)e["actor"] == "telebirr"));
        await _txAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _txAuditRepo.DidNotReceive().GetByTransactionAsync(Arg.Any<string>());
        await _checkoutRepo.Received(1).CloseSlotForMerchOrderAsync(OrderId, OrderMerchOrderId, OrderCheckoutOutcome.Paid);
        await _idemRepo.Received(1).UpdateAsync(OrderMerchOrderId, "completed", Arg.Any<string?>());
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Order_payment_is_recorded_in_the_inbox_before_it_is_applied()
    {
        var calls = new List<string>();
        var inboxKey = $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed";
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryHoldForPaymentAsync(OrderId, Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ => { calls.Add("hold"); return true; });
        _inboxRepo.RecordAsync(inboxKey, OrderEscrowId, OrderId, OrderMerchOrderId,
                Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>())
            .Returns(_ => { calls.Add("inbox"); return true; });
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(_ => { calls.Add("fund"); return StoredOrder("funded", OrderPaymentId); });
        _inboxRepo.FinalizeAsync(inboxKey, "applied").Returns(_ => { calls.Add("applied"); return true; });

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        // ESC-45 (D8): the hold lands before the inbox row, so the cron cannot strike in between.
        calls.Should().Equal("hold", "inbox", "fund", "applied");
    }

    [Fact]
    public async Task Order_payment_redelivery_repairs_without_funding_again()
    {
        // The farmer already accepted: a redelivery of the same payment is not "unmatched".
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("accepted", OrderPaymentId));
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0).Returns(StockDecrementResult.AlreadyApplied);
        _escrowRepo.GetByIdAsync(OrderEscrowId).Returns(new Dictionary<string, object>
        {
            { "escrowId", OrderEscrowId }, { "status", "escrowed" }, { "createdAt", "2026-09-30T09:00:00.0000000Z" }
        });

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _cropRepo.Received(1).TryDecrementStockAsync("listing-1", OrderId, 5.0);
        await _escrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "applied_again");
    }

    [Fact]
    public async Task Order_payment_redelivery_creates_a_missing_escrow()
    {
        // A crash after the funding write left no escrow; the redelivery finishes the job.
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _escrowRepo.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(r =>
            (string)r["escrowId"] == OrderEscrowId && (string)r["telebirrRef"] == OrderPaymentId));
    }

    [Fact]
    public async Task Order_payment_on_an_order_paid_by_another_payment_is_a_duplicate()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", "TB-EARLIER"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("order_payment_duplicate");
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "duplicate");
    }

    [Fact]
    public async Task Order_payment_for_an_order_no_longer_waiting_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("cancelled"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("order_payment_unmatched");
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "unmatched:status_cancelled");
    }

    [Fact]
    public async Task Order_payment_with_a_different_amount_does_not_fund()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody(totalAmount: "14000.00")), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("webhook_amount_mismatch");
        await AuditRecorded("order_payment_unmatched");
    }

    [Fact]
    public async Task Order_payment_in_another_currency_does_not_fund()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody(transCurrency: "USD")), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("order_payment_unmatched");
    }

    [Fact]
    public async Task Order_payment_from_another_wallet_is_rejected_when_required()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        var fn = NewOrderFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true);

        await fn.FunctionHandler(Req(OrderBody(msisdn: "+251922000002")), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("webhook_payer_mismatch");
    }

    [Fact]
    public async Task Order_payment_that_loses_a_race_is_classified_again()
    {
        // The funding write found the order changed; the re-read shows this same payment
        // already applied, so it is repaired, never treated as done or funded twice.
        _orderRepo.GetByIdAsync(OrderId, true).Returns(
            StoredOrder("payment_submitted"), StoredOrder("funded", OrderPaymentId));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns((Dictionary<string, object>?)null);

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "applied_again");
    }

    [Fact]
    public async Task Failed_order_payment_is_only_recorded_and_keeps_the_checkout_open()
    {
        // Telebirr may still accept the same payment (a wrong PIN or a low balance can be
        // retried), so a Failed try neither closes the checkout nor touches the order.
        var resp = await NewOrderFunction().FunctionHandler(Req(OrderBody(tradeStatus: "Failed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await AuditRecorded("order_payment_failed");
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Failed", "payment_failed");
        await _checkoutRepo.DidNotReceive().CloseSlotForMerchOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _idemRepo.DidNotReceive().UpdateAsync(OrderMerchOrderId, Arg.Any<string>(), Arg.Any<string?>());
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _orderRepo.DidNotReceive().TryHoldForPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _orderRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Failed_notification_after_the_order_was_paid_changes_nothing()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(OrderBody(tradeStatus: "Failed", paymentOrderId: "TB-LATE-FAIL")), _ctx);

        await _checkoutRepo.DidNotReceive().CloseSlotForMerchOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _idemRepo.DidNotReceive().UpdateAsync(OrderMerchOrderId, Arg.Any<string>(), Arg.Any<string?>());
        await _orderRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        Logs.Should().NotContain("ORDER_PAYMENT_");
    }

    [Fact]
    public async Task Order_payment_with_an_unknown_status_is_only_recorded()
    {
        await NewOrderFunction().FunctionHandler(Req(OrderBody(tradeStatus: "Pending")), _ctx);

        await AuditRecorded("order_payment_status_ignored");
        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Pending", "ignored");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_mapping_lookup_failure_is_alarmed_not_dropped()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        // ESC-45 (D1): verified but nothing durable yet: 500 so Telebirr sends it again.
        resp.StatusCode.Should().Be(500);
        Logs.Should().Contain($"ORDER_PAYMENT_UNPROCESSED merchOrderId={OrderMerchOrderId} paymentOrderId={OrderPaymentId}");
        await _inboxRepo.DidNotReceive().GetAsync(Arg.Any<string>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_for_a_missing_order_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_UNMATCHED");
        await AuditRecorded("order_payment_unmatched");
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "unmatched:order_missing");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_for_an_order_whose_total_changed_is_unmatched()
    {
        var order = StoredOrder("payment_submitted");
        order["orderTotal"] = "16000";
        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "unmatched:order_changed");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_without_a_payment_order_id_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody(paymentOrderId: "")), _ctx);

        await _inboxRepo.Received(1).FinalizeAsync($"webhook-inbox:{OrderMerchOrderId}::Completed", "unmatched:no_payment_order_id");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_from_another_wallet_funds_but_is_recorded_when_not_required()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        var fn = NewOrderFunction(userRepo: BuyerWithPhone("+251911000001"));

        await fn.FunctionHandler(Req(OrderBody(msisdn: "+251922000002")), _ctx);

        await AuditRecorded("webhook_payer_mismatch");
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("payment_failed")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("dispute_resolved")]
    public async Task Order_payment_redelivery_for_a_closed_order_repairs_nothing(string status)
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder(status, OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _cropRepo.DidNotReceive().TryDecrementStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _escrowRepo.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _checkoutRepo.DidNotReceive().CloseSlotForMerchOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied_again");
        Logs.Should().NotContain("ORDER_PAYMENT_UNMATCHED").And.NotContain("ORDER_PAYMENT_DUPLICATE");
        // ESC-47: the funding happened, so its missing audit row is still written (once, at its time).
        await _txAuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["action"] == "payment_verified" && (string)e["timestamp"] == VerifiedAt));
    }

    // ===== ESC-47 (2): the payment_verified row survives a failed write after funding =====

    [Fact]
    public async Task Order_payment_verified_row_lost_at_funding_is_written_once_by_the_redeliveries()
    {
        var fn = NewOrderFunction();
        // A transaction-audit table that keeps the (transactionId, timestamp) key unique, holds the
        // order's earlier rows, and whose first write fails.
        var rows = OrderTrailBeforeFunding();
        var writes = 0;
        _txAuditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(call =>
        {
            if (++writes == 1)
            {
                return Task.FromException<bool>(new AmazonDynamoDBException("throttled"));
            }

            var entry = call.Arg<Dictionary<string, object>>();
            if (rows.Any(r => Equals(r["transactionId"], entry["transactionId"]) && Equals(r["timestamp"], entry["timestamp"])))
            {
                return Task.FromResult(false);
            }

            rows.Add(entry);
            return Task.FromResult(true);
        });
        _txAuditRepo.GetByTransactionAsync(OrderId)
            .Returns(_ => rows.Where(r => Equals(r["transactionId"], OrderId)).ToList());
        var order = StoredOrder("payment_submitted");
        _orderRepo.GetByIdAsync(OrderId, true).Returns(_ => order);
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(_ => StoredOrder("funded", OrderPaymentId));

        // 1st delivery: funds the order; the audit write fails (swallowed, logged with the order).
        (await fn.FunctionHandler(Req(OrderBody()), _ctx)).StatusCode.Should().Be(200);
        rows.Should().NotContain(r => (string)r["action"] == "payment_verified");
        Logs.Should().Contain("transaction-audit write failed (non-fatal)");
        Logs.Should().Contain($"ORDER_PAYMENT_BOOKKEEPING_FAILED orderId={OrderId}")
            .And.Contain("step=payment_verified_audit reason=write_failed");

        // 2nd delivery: the same payment again; the repair writes the missing row.
        order = StoredOrder("funded", OrderPaymentId);
        InboxAlreadyHolds("applied");
        (await fn.FunctionHandler(Req(OrderBody()), _ctx)).StatusCode.Should().Be(200);

        // 3rd delivery: the row is there; nothing new is written.
        (await fn.FunctionHandler(Req(OrderBody()), _ctx)).StatusCode.Should().Be(200);

        // The order's earlier rows did not stop the repair: it looks for payment_verified only.
        rows.Select(r => (string)r["action"]).Should().Equal("order_created", "order_updated", "payment_verified");
        var row = rows.Single(r => (string)r["action"] == "payment_verified");
        row["timestamp"].Should().Be(VerifiedAt);
        row["fromState"].Should().Be("payment_submitted");
        row["toState"].Should().Be("funded");
        row["actor"].Should().Be("telebirr");
        row["metadata"].Should().Be($"merchOrderId={OrderMerchOrderId},paymentOrderId={OrderPaymentId}");
        writes.Should().Be(2, "the third delivery finds the row and writes nothing");
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        await _txAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(2).FinalizeAsync(OrderInboxKey, "applied_again");
    }

    [Fact]
    public async Task Order_payment_redelivery_repairs_stock_and_escrow_before_the_audit_row()
    {
        // A slow or throttled audit table must not use up the time the money repair needs.
        var calls = new List<string>();
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0)
            .Returns(_ => { calls.Add("stock"); return StockDecrementResult.AlreadyApplied; });
        _escrowRepo.CreateAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(_ => { calls.Add("escrow"); return Task.CompletedTask; });
        _txAuditRepo.GetByTransactionAsync(OrderId)
            .Returns(_ => { calls.Add("audit_read"); return OrderTrailBeforeFunding(); });
        _txAuditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(_ => { calls.Add("audit_write"); return true; });

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        calls.Should().Equal("stock", "escrow", "audit_read", "audit_write");
    }

    [Fact]
    public async Task Order_payment_redelivery_keeps_a_payment_verified_row_written_at_another_time()
    {
        // A server before ESC-47 wrote the row at the time of the write, not at paymentVerifiedAt:
        // the repair finds it by action and adds nothing.
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));
        _txAuditRepo.GetByTransactionAsync(OrderId).Returns(new List<Dictionary<string, object>>
        {
            new() { { "transactionId", OrderId }, { "timestamp", "2026-09-30T09:00:00.4123456Z" }, { "action", "payment_verified" } }
        });

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        await _txAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
        await _txAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied_again");
    }

    [Fact]
    public async Task Order_payment_redelivery_with_an_unreadable_audit_trail_writes_no_row_but_still_repairs()
    {
        // Writing blind could duplicate a row from before ESC-47; the next redelivery tries again.
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));
        _txAuditRepo.GetByTransactionAsync(OrderId).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        await _txAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
        Logs.Should().Contain("ORDER_PAYMENT_BOOKKEEPING_FAILED").And.Contain("step=payment_verified_audit");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _escrowRepo.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(r =>
            (string)r["escrowId"] == OrderEscrowId));
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied_again");
    }

    [Fact]
    public async Task Order_payment_redelivery_of_an_order_without_a_funding_time_writes_no_row()
    {
        var fn = NewOrderFunction();
        var order = StoredOrder("funded", OrderPaymentId);
        order.Remove("paymentVerifiedAt");
        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        // No deterministic key: a row at the time of the write could be written twice.
        await _txAuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
        Logs.Should().Contain("step=payment_verified_audit reason=no_payment_verified_at");
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied_again");
    }

    [Fact]
    public async Task Order_payment_bookkeeping_failure_after_funding_is_not_a_refund_alarm()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        _checkoutRepo.CloseSlotForMerchOrderAsync(OrderId, OrderMerchOrderId, OrderCheckoutOutcome.Paid)
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_BOOKKEEPING_FAILED").And.Contain("step=after_funding");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
    }

    [Fact]
    public async Task Order_payment_for_an_order_id_that_spells_tokens_logs_no_alarm_token()
    {
        // ESC-13: the order id is client-chosen and lands in every payment line of this log group.
        var hostileOrderId = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.Hostile;
        var function = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId)
            .Returns(new OrderCheckoutMapping(OrderMerchOrderId, hostileOrderId, "buyer-1", 15000m));
        _orderRepo.GetByIdAsync(hostileOrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(hostileOrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        _cropRepo.TryDecrementStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(StockDecrementResult.Decremented);
        _checkoutRepo.CloseSlotForMerchOrderAsync(hostileOrderId, OrderMerchOrderId, OrderCheckoutOutcome.Paid)
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await function.FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_BOOKKEEPING_FAILED orderId=ESCROW-SETTLEMENT-FAILED-");
        WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens.Where(Logs.Contains).Should().BeEmpty();
    }

    [Fact]
    public async Task Order_payment_inbox_update_failure_is_not_a_refund_alarm()
    {
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        _inboxRepo.FinalizeAsync(OrderInboxKey, "applied").ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_BOOKKEEPING_FAILED").And.Contain("step=inbox");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_without_order_tables_is_left_unprocessed()
    {
        var resp = await NewOrderFunction(wireOrders: false).FunctionHandler(Req(OrderBody()), _ctx);

        // A deployment fault: alarmed now, 200 (a retry cannot fix it), the row stays pending.
        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED").And.Contain("reason=orders_not_configured");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).MarkUnprocessedAsync($"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed");
    }

    [Fact]
    public async Task Escrow_payments_still_take_the_escrow_path_when_order_checkouts_are_wired()
    {
        // A merch_order_id that is not an order checkout keeps the /escrow/initiate behaviour.
        var resp = await NewOrderFunction().FunctionHandler(Req(SignedBody("Completed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, "2026-07-07T10:00:00.0000000Z", "escrowed", "initiated",
            Arg.Any<Dictionary<string, object>>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    private static WebhookInboxRow InboxRow(string status, string? marker = null, int attempts = 0)
        => new(OrderInboxKey, OrderEscrowId, status, OrderId, OrderMerchOrderId, marker,
            marker == null ? null : WebhookInbox.Iso(DateTime.UtcNow.AddMinutes(-1)), attempts,
            WebhookInbox.Iso(DateTime.UtcNow.AddMinutes(-10)),
            new TelebirrWebhookBiz
            {
                MerchOrderId = OrderMerchOrderId,
                PaymentOrderId = OrderPaymentId,
                TotalAmount = "15000.00",
                TransCurrency = "ETB",
                TradeStatus = "Completed",
                MerchCode = "220100",
                TransId = "TXN9",
            });

    private void InboxAlreadyHolds(string status)
    {
        var pending = status is "received" or "unprocessed";
        _inboxRepo.GetAsync(OrderInboxKey).Returns(InboxRow(status, pending ? WebhookInbox.Pending : null));
    }

    [Theory]
    [InlineData("unmatched:status_payment_failed")]
    [InlineData("unmatched:payer")]
    [InlineData("duplicate")]
    public async Task Order_payment_redelivery_keeps_an_earlier_refusal(string earlierOutcome)
    {
        // The refused payment may already be refunded by hand: even though the order waits for
        // payment again, the redelivered payment must not fund it.
        InboxAlreadyHolds(earlierOutcome);
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.DidNotReceive().FinalizeAsync(OrderInboxKey, Arg.Any<string>());
        Logs.Should().Contain("verdict kept");
    }

    [Theory]
    [InlineData("received")]
    [InlineData("unprocessed")]
    public async Task Order_payment_redelivery_after_an_unfinished_try_is_applied(string earlierOutcome)
    {
        InboxAlreadyHolds(earlierOutcome);
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
    }

    [Fact]
    public async Task Order_payment_from_an_earlier_payment_attempt_never_funds()
    {
        // Checkout opened at the first payment_submitted; the order failed and was submitted again.
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).Returns(new OrderCheckoutMapping(
            OrderMerchOrderId, OrderId, "buyer-1", 15000m, "2026-09-28T08:00:00.0000000Z"));
        var order = StoredOrder("payment_submitted");
        order["paymentSubmittedAt"] = "2026-09-29T09:30:00.0000000Z";
        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("order_payment_unmatched");
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "unmatched:stale_checkout");
    }

    [Fact]
    public async Task Order_payment_in_the_attempt_it_was_opened_in_funds()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).Returns(new OrderCheckoutMapping(
            OrderMerchOrderId, OrderId, "buyer-1", 15000m, "2026-09-29T09:30:00.0000000Z"));
        var order = StoredOrder("payment_submitted");
        order["paymentSubmittedAt"] = "2026-09-29T09:30:00.0000000Z";
        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_whose_apply_fails_answers_500_and_stays_pending()
    {
        // ESC-45 (D1, D5): recorded in the inbox, not applied: 500 for a redelivery, the sweep as the
        // backstop, and no page (the page comes only if the sweep gives up).
        _orderRepo.GetByIdAsync(OrderId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(500);
        Logs.Should().Contain("ORDER_PAYMENT_REPLAY_PENDING").And.NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _inboxRepo.Received(1).RecordAsync(OrderInboxKey, OrderEscrowId, OrderId, OrderMerchOrderId,
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>());
        await _inboxRepo.DidNotReceive().FinalizeAsync(Arg.Any<string>(), Arg.Any<string>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e.Values.Any(v => v != null && v.ToString() == "order_payment_unprocessed")));
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    // ===== ESC-45 D0: the documented notify dialect =====

    private Task EscrowNotMoved()
        => _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>());

    private Task EscrowMovedTo(string status)
        => _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), status, "initiated", Arg.Any<Dictionary<string, object>?>());

    private static string Json(Dictionary<string, object?> body) => JsonSerializer.Serialize(body);

    private static Dictionary<string, object?> Signed(Dictionary<string, string> fields)
    {
        var body = fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body["sign"] = Sign(fields);
        body["sign_type"] = "SHA256WithRSA";
        return body;
    }

    [Fact]
    public async Task A_notification_wrapped_in_a_data_envelope_is_read_from_the_envelope()
    {
        var envelope = new Dictionary<string, object?> { ["data"] = Signed(SignedFields("Completed")) };

        await NewFunction().FunctionHandler(Req(Json(envelope)), _ctx);

        await EscrowMovedTo("escrowed");
    }

    [Fact]
    public async Task An_envelope_cannot_relabel_a_captured_notification()
    {
        // Validator item 1: a captured Failed notify, its fields put in data with Completed, Failed and
        // the original signature at the top. The verified map must be the read map.
        var captured = SignedFields("Failed");
        var sign = Sign(captured);
        var relabelled = captured.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        relabelled["trade_status"] = "Completed";
        var attack = new Dictionary<string, object?>
        {
            ["data"] = relabelled,
            ["trade_status"] = "Failed",
            ["sign"] = sign,
            ["sign_type"] = "SHA256WithRSA",
        };

        await NewFunction().FunctionHandler(Req(Json(attack)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_nested_object_next_to_the_signed_fields_refuses_the_body()
    {
        var body = Signed(SignedFields("Failed"));
        body["data"] = Signed(SignedFields("Completed"));

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_duplicate_key_refuses_the_body()
    {
        // The signed view says Failed; a second trade_status says Completed.
        var signedJson = Json(Signed(SignedFields("Failed")));
        var withDuplicate = signedJson[..^1] + ",\"trade_status\":\"Completed\"}";

        await NewFunction().FunctionHandler(Req(withDuplicate), _ctx);

        await EscrowNotMoved();
        Logs.Should().Contain("duplicate_key");
    }

    [Fact]
    public async Task An_array_value_refuses_the_body()
    {
        var body = Signed(SignedFields("Completed"));
        body["extra"] = new[] { "a" };

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_null_field_is_absent()
    {
        var body = Signed(SignedFields("Completed"));
        body["callback_info"] = null;

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowMovedTo("escrowed");
    }

    [Fact]
    public async Task A_transaction_id_sent_as_transId_verifies_as_trans_id()
    {
        await NewFunction().FunctionHandler(Req(SignedBody("Completed", transIdOnWire: true)), _ctx);

        await EscrowMovedTo("escrowed");
    }

    [Fact]
    public async Task An_unsigned_trans_id_next_to_a_signed_transId_is_refused()
    {
        // Validator item 2: the alias applies only when trans_id itself is absent.
        var fields = SignedFields("Completed");
        var body = Signed(fields);
        body["transId"] = body["trans_id"];
        body["trans_id"] = "FORGED";

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task The_transId_alias_feeds_the_order_its_transaction_id()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));

        await NewOrderFunction().FunctionHandler(Req(SignedBody("Completed", merchOrderId: OrderMerchOrderId,
            paymentOrderId: OrderPaymentId, transIdOnWire: true)), _ctx);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["telebirrTransId"] == "TXN9"));
    }

    [Theory]
    [InlineData(TelebirrSignaturePadding.Pss, TelebirrSignaturePadding.Pkcs1)]
    [InlineData(TelebirrSignaturePadding.Pkcs1, TelebirrSignaturePadding.Pss)]
    public async Task Either_padding_verifies_whatever_is_configured(
        TelebirrSignaturePadding configured, TelebirrSignaturePadding signedWith)
    {
        var fn = new TelebirrWebhookFunction(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, configured));

        await fn.FunctionHandler(Req(SignedBody("Completed", padding: signedWith)), _ctx);

        await EscrowMovedTo("escrowed");
        Logs.Should().Contain($"padding={signedWith.ToString().ToLowerInvariant()}");
    }

    [Fact]
    public async Task A_body_signed_by_another_key_is_refused_under_both_paddings()
    {
        using var other = System.Security.Cryptography.RSA.Create(2048);
        var otherSigner = new TelebirrSignatureService(other.ExportPkcs8PrivateKeyPem(), null, TelebirrSignaturePadding.Pss);
        var fields = SignedFields("Completed");
        var body = fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body["sign"] = otherSigner.Sign(otherSigner.BuildFlatCanonicalString(fields));

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_notify_time_in_seconds_is_fresh()
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        await NewFunction().FunctionHandler(Req(SignedBody("Completed", rawNotifyTime: seconds)), _ctx);

        await EscrowMovedTo("escrowed");
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("123456789012345")]
    [InlineData("-1700000000000")]
    [InlineData("2026-10-10T10:00:00Z")]
    public async Task A_notify_time_outside_the_epoch_digit_ranges_is_refused(string raw)
    {
        await NewFunction().FunctionHandler(Req(SignedBody("Completed", rawNotifyTime: raw)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_stale_notify_time_in_seconds_is_refused()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();

        await NewFunction().FunctionHandler(Req(SignedBody("Completed", rawNotifyTime: stale)), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task The_earlier_nested_shape_still_verifies_through_its_timestamp()
    {
        // Our earlier simulator: biz_content nested, notify_time an ISO string, timestamp in ms.
        var fields = SignedFields("Completed");
        fields["notify_time"] = "2026-07-07T10:05:00Z";
        fields["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var bizKeys = new[] { "merch_order_id", "payment_order_id", "total_amount", "trans_currency", "trade_status", "merch_code", "trans_id" };
        var body = fields.Where(kv => !bizKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body["biz_content"] = fields.Where(kv => bizKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        body["sign"] = Sign(fields);

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowMovedTo("escrowed");
    }

    [Fact]
    public async Task A_biz_content_key_that_repeats_a_top_level_key_refuses_the_body()
    {
        var fields = SignedFields("Failed");
        var body = Signed(fields);
        body["biz_content"] = new Dictionary<string, string> { ["trade_status"] = "Completed" };

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Paying")]
    public async Task An_in_progress_status_never_cancels_an_escrow(string status)
    {
        // Validator item 7: Pending means "payment completed, awaiting order synchronization".
        await NewFunction().FunctionHandler(Req(SignedBody(status)), _ctx);

        await EscrowNotMoved();
    }

    [Theory]
    [InlineData("Failure")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    public async Task A_documented_failure_cancels_an_escrow(string status)
    {
        await NewFunction().FunctionHandler(Req(SignedBody(status)), _ctx);

        await EscrowMovedTo("cancelled");
    }

    [Fact]
    public async Task A_Failure_order_notification_is_recorded_as_a_failed_try()
    {
        await NewOrderFunction().FunctionHandler(Req(OrderBody(tradeStatus: "Failure")), _ctx);

        await AuditRecorded("order_payment_failed");
    }

    [Fact]
    public async Task A_refused_body_cannot_spell_an_alarm_token_in_the_log()
    {
        // Validator item 3: field names of an unverified body reach the log only defanged.
        var body = Signed(SignedFields("Completed"));
        foreach (var token in WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens)
        {
            body[token] = "x";
        }

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
        WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens.Where(Logs.Contains).Should().BeEmpty();
    }

    // ===== ESC-45: answers (D1, D2), late window (D3), hold (D8) =====

    private void OrderFundsCleanly()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
    }

    private static string OrderBodyAt(DateTimeOffset sentAt, string tradeStatus = "Completed")
        => SignedBody(tradeStatus, merchOrderId: OrderMerchOrderId, paymentOrderId: OrderPaymentId,
            notifyTimeMs: sentAt.ToUnixTimeMilliseconds());

    [Fact]
    public async Task A_final_answer_carries_a_small_json_body()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        resp.Body.Should().Be("{\"code\":\"0\",\"msg\":\"success\"}");
    }

    [Fact]
    public async Task An_inbox_write_failure_answers_500_and_alarms()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        _inboxRepo.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>()).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(500);
        resp.Body.Should().Be("{\"code\":\"1\",\"msg\":\"error\"}");
        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED").And.Contain("reason=inbox_write_");
        await AuditRecorded("order_payment_unprocessed");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task An_inbox_read_failure_answers_500()
    {
        var fn = NewOrderFunction();
        _inboxRepo.GetAsync(OrderInboxKey).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(500);
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_bad_signature_answers_200_without_touching_the_inbox()
    {
        var fn = NewOrderFunction();
        _inboxRepo.GetAsync(Arg.Any<string>()).ThrowsAsync(new AmazonDynamoDBException("down"));

        var resp = await fn.FunctionHandler(Req(SignedBody("Completed", merchOrderId: OrderMerchOrderId, tamper: true)), _ctx);

        resp.StatusCode.Should().Be(200);
        await _inboxRepo.DidNotReceive().GetAsync(Arg.Any<string>());
        await _checkoutRepo.DidNotReceive().GetMappingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task A_malformed_mapping_answers_200_with_the_alarm()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).ThrowsAsync(new InvalidDataException("no amount"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED").And.Contain("reason=mapping_invalid");
    }

    [Fact]
    public async Task A_funding_conflict_answers_500_without_a_page()
    {
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns((Dictionary<string, object>?)null);

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(500);
        Logs.Should().Contain("ORDER_PAYMENT_REPLAY_PENDING").And.Contain("reason=funding_conflict");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _inboxRepo.Received(1).MarkUnprocessedAsync(OrderInboxKey);
    }

    [Fact]
    public async Task Too_little_time_left_after_the_inbox_write_answers_500_before_any_order_read()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        _ctx.RemainingTime = TimeSpan.FromSeconds(2);

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(500);
        await _inboxRepo.Received(1).RecordAsync(OrderInboxKey, OrderEscrowId, OrderId, OrderMerchOrderId,
            Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>());
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        Logs.Should().Contain("reason=low_time");
    }

    [Fact]
    public async Task An_escrow_path_failure_after_verification_answers_500()
    {
        _idemRepo.GetAsync(MerchOrderId).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed")), _ctx);

        resp.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task A_completed_payment_holds_its_order_for_the_replay_horizon_in_its_attempt()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).Returns(new OrderCheckoutMapping(
            OrderMerchOrderId, OrderId, "buyer-1", 15000m, "2026-09-29T09:30:00.0000000Z"));
        OrderFundsCleanly();

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        var expected = _clock.GetUtcNow().UtcDateTime + WebhookInbox.ReplayHorizon;
        await _orderRepo.Received(1).TryHoldForPaymentAsync(OrderId,
            Arg.Is<string>(h => Math.Abs((DateTime.Parse(h, null, System.Globalization.DateTimeStyles.AdjustToUniversal) - expected).TotalSeconds) < 2),
            "2026-09-29T09:30:00.0000000Z");
    }

    [Fact]
    public async Task A_hold_failure_is_logged_and_the_payment_still_funds()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        _orderRepo.TryHoldForPaymentAsync(OrderId, Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_HOLD_FAILED");
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task A_late_order_notification_is_still_applied()
    {
        // D3: Telebirr may resend the original notify_time after our 500.
        var fn = NewOrderFunction();
        OrderFundsCleanly();

        await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-5))), _ctx);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        Logs.Should().Contain("late=true");
    }

    [Fact]
    public async Task A_late_escrow_notification_is_still_refused()
    {
        await NewFunction().FunctionHandler(Req(SignedBody("Completed",
            notifyTimeMs: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds())), _ctx);

        await EscrowNotMoved();
    }

    [Fact]
    public async Task A_late_redelivery_of_a_handled_payment_changes_nothing()
    {
        // Validator item 4: no re-apply, no hold, no alarm line for a replayed captured notify.
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        InboxAlreadyHolds("applied");

        var resp = await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-30))), _ctx);

        resp.StatusCode.Should().Be(200);
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _orderRepo.DidNotReceive().TryHoldForPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _inboxRepo.DidNotReceive().FinalizeAsync(Arg.Any<string>(), Arg.Any<string>());
        Logs.Should().NotContain("ORDER_PAYMENT_");
    }

    [Theory]
    [InlineData(-49 * 60)]  // older than the late window
    [InlineData(10)]        // ten minutes in the future
    public async Task An_out_of_window_notification_never_handled_is_alarmed_not_applied(int minutesOld)
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();

        var resp = await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddMinutes(minutesOld))), _ctx);

        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED").And.Contain("reason=stale_notify_time");
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.DidNotReceive().RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TelebirrWebhookBiz>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task An_out_of_window_failure_notification_is_not_alarmed()
    {
        var fn = NewOrderFunction();

        await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-60), "Failure")), _ctx);

        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task An_out_of_window_redelivery_of_a_handled_payment_is_not_alarmed()
    {
        var fn = NewOrderFunction();
        InboxAlreadyHolds("applied");

        await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-60))), _ctx);

        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
    }

    [Fact]
    public async Task An_out_of_window_redelivery_of_a_pending_payment_is_left_to_the_sweep()
    {
        var fn = NewOrderFunction();
        InboxAlreadyHolds("received");

        await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-60))), _ctx);

        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_payment_applied_after_its_row_was_given_up_says_so()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        _inboxRepo.GetAsync(OrderInboxKey).Returns(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Exhausted, 8));

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
        Logs.Should().Contain("stop any manual refund");
    }

    [Fact]
    public void The_late_window_stays_inside_the_inbox_ttl()
    {
        // D3 relies on the inbox row (30-day TTL) to recognise a redelivery.
        TelebirrWebhookFunction.LateWindow.Should().BeLessThan(TimeSpan.FromDays(30));
    }

    // ===== ESC-45: inbox replay sweep (D4) =====

    private static APIGatewayProxyRequest ReplayRequest()
        => new() { Body = "{\"action\":\"webhook_inbox_replay\"}" };

    private void DueRows(params WebhookInboxRow[] rows)
        => _inboxRepo.QueryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((rows, (Dictionary<string, AttributeValue>?)null));

    private void ClaimSucceeds()
        => _inboxRepo.TryClaimAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(true);

    [Fact]
    public async Task The_sweep_replays_a_due_row_and_funds_the_order()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        DueRows(InboxRow(WebhookInbox.Received, WebhookInbox.Pending));
        ClaimSucceeds();

        var resp = await fn.FunctionHandler(ReplayRequest(), _ctx);

        resp.StatusCode.Should().Be(200);
        await _inboxRepo.Received(1).TryClaimAsync(OrderInboxKey, Arg.Any<string>(), 0, Arg.Any<DateTime>(), Arg.Any<DateTime>());
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
        await _orderRepo.Received(1).TryHoldForPaymentAsync(OrderId, Arg.Any<string>(), Arg.Any<string?>());
        Logs.Should().Contain("TELEBIRR_INBOX_REPLAY_RUN due=1 claimed=1 applied=1 pending=0 exhausted=0");
    }

    [Fact]
    public async Task The_sweep_skips_a_row_whose_claim_it_lost()
    {
        var fn = NewOrderFunction();
        DueRows(InboxRow(WebhookInbox.Received, WebhookInbox.Pending));

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        Logs.Should().Contain("claimed=0");
    }

    [Fact]
    public async Task A_replay_that_fails_before_the_last_attempt_stays_pending_without_a_page()
    {
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));
        DueRows(InboxRow(WebhookInbox.Received, WebhookInbox.Pending, attempts: 2));
        ClaimSucceeds();

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        await _inboxRepo.Received(1).MarkUnprocessedAsync(OrderInboxKey);
        await _inboxRepo.DidNotReceive().ExhaustAsync(Arg.Any<string>(), Arg.Any<int>());
        Logs.Should().Contain("ORDER_PAYMENT_REPLAY_PENDING").And.Contain("attempt=3/8");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
    }

    [Fact]
    public async Task The_last_failed_replay_gives_the_row_up_with_the_alarm_and_an_audit_row()
    {
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));
        DueRows(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Pending, attempts: 7));
        ClaimSucceeds();
        _inboxRepo.ExhaustAsync(OrderInboxKey, 8).Returns(true);

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED").And.Contain("reason=replay_exhausted").And.Contain("attempts=8");
        await AuditRecorded("order_payment_unprocessed");
        Logs.Should().Contain("exhausted=1");
    }

    [Fact]
    public async Task A_give_up_that_loses_to_a_finalize_raises_nothing()
    {
        // Validator item 5: the exhaust is conditional, and the alarm and audit row follow it.
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));
        DueRows(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Pending, attempts: 7));
        ClaimSucceeds();
        _inboxRepo.ExhaustAsync(OrderInboxKey, 8).Returns(false);

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e.Values.Any(v => v != null && v.ToString() == "order_payment_unprocessed")));
    }

    [Fact]
    public async Task A_row_with_every_attempt_used_is_given_up_without_a_replay()
    {
        // A run that died during the 8th attempt.
        var fn = NewOrderFunction();
        DueRows(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Pending, attempts: 8));
        _inboxRepo.ExhaustAsync(OrderInboxKey, 8).Returns(true);

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        await _inboxRepo.DidNotReceive().TryClaimAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>());
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        Logs.Should().Contain("reason=replay_exhausted");
    }

    [Fact]
    public async Task A_malformed_mapping_on_replay_is_given_up_at_once()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).ThrowsAsync(new InvalidDataException("no amount"));
        DueRows(InboxRow(WebhookInbox.Received, WebhookInbox.Pending, attempts: 0));
        ClaimSucceeds();
        _inboxRepo.ExhaustAsync(OrderInboxKey, 1).Returns(true);

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        await _inboxRepo.Received(1).ExhaustAsync(OrderInboxKey, 1);
        Logs.Should().Contain("cause=mapping-invalid");
    }

    [Fact]
    public async Task A_failed_index_query_logs_no_heartbeat()
    {
        var fn = NewOrderFunction();
        _inboxRepo.QueryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new AmazonDynamoDBException("index missing"));

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        Logs.Should().NotContain("TELEBIRR_INBOX_REPLAY_RUN").And.Contain("could not be read");
    }

    [Fact]
    public async Task The_sweep_runs_before_the_ip_allow_list()
    {
        _inboxRepo.QueryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((Array.Empty<WebhookInboxRow>(), (Dictionary<string, AttributeValue>?)null));
        var fn = new TelebirrWebhookFunction(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1),
            allowedSourceIps: new[] { "196.188.0.1" }, orderCheckoutRepo: _checkoutRepo, inboxRepo: _inboxRepo);

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        Logs.Should().Contain("TELEBIRR_INBOX_REPLAY_RUN due=0");
    }

    [Fact]
    public async Task The_public_route_can_never_reach_the_sweep()
    {
        var fn = NewOrderFunction();
        var request = ReplayRequest();
        request.RequestContext = new APIGatewayProxyRequest.ProxyRequestContext();

        var resp = await fn.FunctionHandler(request, _ctx);

        resp.StatusCode.Should().Be(200);
        await _inboxRepo.DidNotReceive().QueryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ===== ESC-45 review: separators cannot re-split a captured notification =====

    /// <summary>
    /// A captured, genuinely signed notification re-split into a different map with the same signed
    /// string: <paramref name="moveKey"/>'s pair is appended to <paramref name="intoKey"/>'s value (its
    /// neighbour in the sorted string) and the key itself is dropped.
    /// </summary>
    private static string ReSplit(Dictionary<string, string> captured, string intoKey, string moveKey)
    {
        var body = captured.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        body[intoKey] = $"{captured[intoKey]}&{moveKey}={captured[moveKey]}";
        body.Remove(moveKey);
        body["sign"] = Sign(captured);
        body["sign_type"] = "SHA256WithRSA";
        return JsonSerializer.Serialize(body);
    }

    [Fact]
    public async Task A_payment_reference_moved_into_notify_url_is_refused_before_any_state()
    {
        // Variant (a): verified before the fix; the reader got no payment_order_id and a new inbox key.
        var fn = NewOrderFunction();
        var captured = SignedFields("Completed", merchOrderId: OrderMerchOrderId, paymentOrderId: OrderPaymentId);
        var signer = new TelebirrSignatureService(PrivateKeyPem, null, TelebirrSignaturePadding.Pkcs1);
        signer.BuildFlatCanonicalString(new Dictionary<string, string>(captured) { ["notify_url"] = captured["notify_url"] + "&payment_order_id=" + OrderPaymentId }
                .Where(kv => kv.Key != "payment_order_id").ToDictionary(kv => kv.Key, kv => kv.Value))
            .Should().Be(signer.BuildFlatCanonicalString(captured), "the attack relies on an identical signed string");

        var resp = await fn.FunctionHandler(Req(ReSplit(captured, "notify_url", "payment_order_id")), _ctx);

        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain("separator_in_field").And.NotContain("ORDER_PAYMENT_");
        await _checkoutRepo.DidNotReceive().GetMappingAsync(Arg.Any<string>());
        await _inboxRepo.DidNotReceive().GetAsync(Arg.Any<string>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task An_amount_moved_into_the_payment_reference_is_refused_before_any_state()
    {
        // Variant (b): on an order funded by this payment it logged a DUPLICATE refund case.
        var fn = NewOrderFunction();
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("funded", OrderPaymentId));
        var captured = SignedFields("Completed", merchOrderId: OrderMerchOrderId, paymentOrderId: OrderPaymentId);

        var resp = await fn.FunctionHandler(Req(ReSplit(captured, "payment_order_id", "total_amount")), _ctx);

        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain("separator_in_field").And.NotContain("ORDER_PAYMENT_");
        await _checkoutRepo.DidNotReceive().GetMappingAsync(Arg.Any<string>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task A_key_with_a_separator_is_refused()
    {
        var body = Signed(SignedFields("Completed"));
        body["trade_status=Completed"] = "x";

        await NewFunction().FunctionHandler(Req(Json(body)), _ctx);

        await EscrowNotMoved();
        Logs.Should().Contain("separator_in_field");
    }

    [Fact]
    public async Task An_equals_sign_inside_a_value_still_verifies()
    {
        // '=' cannot re-split once keys may not contain it: a query string in notify_url is fine.
        var fields = SignedFields("Completed");
        fields["notify_url"] = "https://api.example/webhooks/telebirr?src=telebirr";

        await NewFunction().FunctionHandler(Req(Json(Signed(fields))), _ctx);

        await EscrowMovedTo("escrowed");
    }

    // ===== ESC-45 review: a payment that applies after its row was given up says so =====

    [Fact]
    public async Task A_requeued_row_that_the_sweep_applies_says_stop_any_manual_refund()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        DueRows(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Pending) with { ExhaustedAt = WebhookInbox.Iso(DateTime.UtcNow.AddHours(-2)) });
        ClaimSucceeds();

        await fn.FunctionHandler(ReplayRequest(), _ctx);

        await _inboxRepo.Received(1).FinalizeAsync(OrderInboxKey, "applied");
        Logs.Should().Contain("stop any manual refund");
    }

    [Fact]
    public async Task A_redelivery_of_a_requeued_row_that_applies_says_stop_any_manual_refund()
    {
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        _inboxRepo.GetAsync(OrderInboxKey).Returns(
            InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Pending) with { ExhaustedAt = WebhookInbox.Iso(DateTime.UtcNow.AddHours(-2)) });

        await fn.FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("stop any manual refund");
    }

    [Fact]
    public async Task An_out_of_window_redelivery_of_a_given_up_row_points_at_the_requeue()
    {
        var fn = NewOrderFunction();
        _inboxRepo.GetAsync(OrderInboxKey).Returns(InboxRow(WebhookInbox.Unprocessed, WebhookInbox.Exhausted, 8));

        await fn.FunctionHandler(Req(OrderBodyAt(_clock.GetUtcNow().AddHours(-60))), _ctx);

        Logs.Should().Contain("gave up on").And.Contain("requeue it to apply").And.NotContain("the sweep applies it");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
    }

    [Fact]
    public async Task A_real_notify_with_a_query_string_in_raw_request_still_funds()
    {
        // ESC-45 review: unsigned transport keys are exempt from the separator rule.
        var fn = NewOrderFunction();
        OrderFundsCleanly();
        var body = Signed(SignedFields("Completed", merchOrderId: OrderMerchOrderId, paymentOrderId: OrderPaymentId));
        body["raw_request"] = "appid=853694808089634&merch_code=220100&prepay_id=P1&sign=x";
        body["header"] = new Dictionary<string, string> { ["requestId"] = "r1&r2" };

        var resp = await fn.FunctionHandler(Req(Json(body)), _ctx);

        resp.StatusCode.Should().Be(200);
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
        Logs.Should().NotContain("separator_in_field");
    }

    [Fact]
    public async Task No_re_split_of_a_captured_notification_verifies()
    {
        // Every way to keep the signed string while moving a signed pair into its neighbour's value
        // (sorted order), with and without an unsigned raw_request beside it: all refused. Without the
        // separator rule each one verifies and the Failed notification cancels the escrow.
        var captured = SignedFields("Failed");
        var keys = captured.Keys.Order(StringComparer.Ordinal).ToList();
        var attempts = 0;
        foreach (var withRawRequest in new[] { false, true })
        {
            for (var i = 0; i + 1 < keys.Count; i++)
            {
                var body = captured.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                body[keys[i]] = $"{captured[keys[i]]}&{keys[i + 1]}={captured[keys[i + 1]]}";
                body.Remove(keys[i + 1]);
                body["sign"] = Sign(captured);
                if (withRawRequest)
                {
                    body["raw_request"] = $"a=1&{keys[i + 1]}={captured[keys[i + 1]]}";
                }

                await NewFunction().FunctionHandler(Req(Json(body)), _ctx);
                attempts++;
            }
        }

        attempts.Should().Be(2 * (keys.Count - 1));
        await EscrowNotMoved();
    }

    // ===== FRD-08: payer mismatch is a fraud signal on the buyer; unverified bodies write nothing =====

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();

    private Task NoSignal() => _fraud.DidNotReceiveWithAnyArgs()
        .RecordFraudSignalAsync(default!, default!, default, default!, default);

    [Fact]
    public async Task Verified_payer_mismatch_applied_anyway_writes_a_weight_zero_signal_on_the_escrows_buyer()
    {
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), fraudService: _fraud);

        var resp = await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: "0922000002")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _fraud.Received(1).RecordFraudSignalAsync(
            "buyer-1", FraudDetectionService.TelebirrPayerMismatchSignal, EscrowId, "escrow", Arg.Any<string?>());
        // A relative's wallet is common: logged for review, never scored.
        FraudDetectionService.SignalWeight(FraudDetectionService.TelebirrPayerMismatchSignal).Should().Be(0);
        // Not required: the payment still applies, as before.
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Order_payment_refused_for_another_wallet_signals_the_checkouts_buyer_with_weight()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        var fn = NewOrderFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true, fraudService: _fraud);

        await fn.FunctionHandler(Req(OrderBody(msisdn: "+251922000002")), _ctx);

        // Enforced: the payment is refused, so the signal scores (soft 5, once per order escrow).
        await _fraud.Received(1).RecordFraudSignalAsync(
            "buyer-1", FraudDetectionService.TelebirrPayerRefusedSignal, OrderEscrowId, "escrow", Arg.Any<string?>());
        FraudDetectionService.SignalWeight(FraudDetectionService.TelebirrPayerRefusedSignal).Should().Be(5);
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Forged_notification_never_writes_a_signal_onto_the_buyer_it_names()
    {
        // Anyone can post to the public route: a bad signature is logged and nothing else.
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), fraudService: _fraud);

        var resp = await fn.FunctionHandler(Req(SignedBody("Completed", tamper: true, msisdn: "0922000002")), _ctx);

        resp.StatusCode.Should().Be(200);
        await NoSignal();
        await EscrowNotMoved();
    }

    [Fact]
    public async Task Matching_or_absent_payer_writes_no_signal()
    {
        EscrowHasBuyer();

        await NewFunction(userRepo: BuyerWithPhone("+251911000001"), fraudService: _fraud)
            .FunctionHandler(Req(SignedBody("Completed", msisdn: "0911000001")), _ctx);
        await NewFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true, fraudService: _fraud)
            .FunctionHandler(Req(SignedBody("Completed")), _ctx);

        await NoSignal();
    }

    [Fact]
    public async Task Payer_mismatch_with_no_buyer_row_writes_no_signal()
    {
        // Nothing is known about a buyer whose row cannot be found: audited, attributed to no one.
        EscrowHasBuyer();
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("buyer-1").Returns((UserDto?)null);

        await NewFunction(userRepo: users, fraudService: _fraud)
            .FunctionHandler(Req(SignedBody("Completed", msisdn: "0922000002")), _ctx);

        await AuditRecorded("webhook_payer_mismatch");
        await NoSignal();
    }

    [Fact]
    public async Task Payer_mismatch_on_a_buyer_row_without_a_phone_writes_no_signal()
    {
        // A deleted buyer's tombstone has no phone: no wallet of theirs to compare, nothing to attribute.
        EscrowHasBuyer();

        await NewFunction(userRepo: BuyerWithPhone(""), fraudService: _fraud)
            .FunctionHandler(Req(SignedBody("Completed", msisdn: "0922000002")), _ctx);

        await AuditRecorded("webhook_payer_mismatch");
        await NoSignal();
    }

    [Theory]
    [InlineData("251911000001")]
    [InlineData("+251911000001")]
    [InlineData("0911000001")]
    [InlineData(" 251911000001 ")]
    public async Task The_buyers_own_wallet_matches_in_every_form_Telebirr_may_send(string msisdn)
    {
        // Telebirr's MSISDN is 12 digits without the plus; the users table stores +251.
        EscrowHasBuyer();
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), requirePayerMatch: true, fraudService: _fraud);

        await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: msisdn)), _ctx);

        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => e.Values.Any(v => v != null && v.ToString() == "webhook_payer_mismatch")));
        await NoSignal();
    }

    [Theory]
    [InlineData("251911000001", "+251911000001")]
    [InlineData("+251911000001", "+251911000001")]
    [InlineData("0911000001", "+251911000001")]
    [InlineData("00251911000001", "+251911000001")]
    [InlineData("2519110000012", "2519110000012")]
    [InlineData("251abc000001", "251abc000001")]
    public void Payer_phone_is_normalized_to_the_users_table_form(string msisdn, string expected)
        => TelebirrWebhookFunction.NormalizePayerPhone(msisdn).Should().Be(expected);

    [Fact]
    public async Task A_failing_signal_write_changes_neither_the_answer_nor_the_payment()
    {
        EscrowHasBuyer();
        _fraud.RecordFraudSignalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("table down"));
        var fn = NewFunction(userRepo: BuyerWithPhone("+251911000001"), fraudService: _fraud);

        var resp = await fn.FunctionHandler(Req(SignedBody("Completed", msisdn: "0922000002")), _ctx);

        resp.StatusCode.Should().Be(200);
        resp.Body.Should().Be("{\"code\":\"0\",\"msg\":\"success\"}");
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, Arg.Any<string>(), "escrowed", "initiated", Arg.Any<Dictionary<string, object>?>());
        Logs.Should().Contain("Failed to record fraud signal TELEBIRR_PAYER_MISMATCH");
    }
}
