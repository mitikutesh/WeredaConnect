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
using WeredaConnect.Server.Services;
using Xunit;
using WeredaConnect.Server.Models;

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
    }

    private TelebirrWebhookFunction NewFunction(string? merchantCode = null,
        IUserRepository? userRepo = null, bool requirePayerMatch = false)
        => new(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1),
            merchantCode: merchantCode, userRepo: userRepo, requirePayerMatch: requirePayerMatch);

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

    private TelebirrWebhookFunction NewOrderFunction(
        bool wireOrders = true, IUserRepository? userRepo = null, bool requirePayerMatch = false)
    {
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId)
            .Returns(new OrderCheckoutMapping(OrderMerchOrderId, OrderId, "buyer-1", 15000m));
        var escrowService = new EscrowService(_escrowRepo, _auditRepo, null, null, orderRepo: _orderRepo);
        return new TelebirrWebhookFunction(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1),
            userRepo: userRepo, requirePayerMatch: requirePayerMatch,
            orderCheckoutRepo: _checkoutRepo,
            orderRepo: wireOrders ? _orderRepo : null,
            orderFunding: wireOrders ? new OrderFundingService(_orderRepo, _cropRepo, escrowService) : null,
            txAuditRepo: _txAuditRepo);
    }

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
            order["paymentVerifiedAt"] = "2026-09-30T09:00:00.0000000Z";
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
        await _txAuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(
            e => (string)e["action"] == "payment_verified"));
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
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _idemRepo.StoreAsync(inboxKey, OrderEscrowId, "received", OrderMerchOrderId)
            .Returns(_ => { calls.Add("inbox"); return true; });
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(_ => { calls.Add("fund"); return StoredOrder("funded", OrderPaymentId); });
        _idemRepo.When(r => r.UpdateAsync(inboxKey, "applied", Arg.Any<string?>()))
            .Do(_ => calls.Add("applied"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        calls.Should().Equal("inbox", "fund", "applied");
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
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "applied_again", Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "duplicate", Arg.Any<string?>());
    }

    [Fact]
    public async Task Order_payment_for_an_order_no_longer_waiting_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("cancelled"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await AuditRecorded("order_payment_unmatched");
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "unmatched:status_cancelled", Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "applied_again", Arg.Any<string?>());
    }

    [Fact]
    public async Task Failed_order_payment_is_only_recorded_and_keeps_the_checkout_open()
    {
        // Telebirr may still accept the same payment (a wrong PIN or a low balance can be
        // retried), so a Failed try neither closes the checkout nor touches the order.
        var resp = await NewOrderFunction().FunctionHandler(Req(OrderBody(tradeStatus: "Failed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await AuditRecorded("order_payment_failed");
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Failed", "payment_failed", Arg.Any<string?>());
        await _checkoutRepo.DidNotReceive().CloseSlotForMerchOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _idemRepo.DidNotReceive().UpdateAsync(OrderMerchOrderId, Arg.Any<string>(), Arg.Any<string?>());
        await _orderRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
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
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Pending", "ignored", Arg.Any<string?>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_mapping_lookup_failure_is_alarmed_not_dropped()
    {
        var fn = NewOrderFunction();
        _checkoutRepo.GetMappingAsync(OrderMerchOrderId).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var resp = await fn.FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        Logs.Should().Contain($"ORDER_PAYMENT_UNPROCESSED merchOrderId={OrderMerchOrderId} paymentOrderId={OrderPaymentId}");
        await _idemRepo.DidNotReceive().GetAsync(Arg.Any<string>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_for_a_missing_order_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_UNMATCHED");
        await AuditRecorded("order_payment_unmatched");
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "unmatched:order_missing", Arg.Any<string?>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_for_an_order_whose_total_changed_is_unmatched()
    {
        var order = StoredOrder("payment_submitted");
        order["orderTotal"] = "16000";
        _orderRepo.GetByIdAsync(OrderId, true).Returns(order);

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "unmatched:order_changed", Arg.Any<string?>());
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_without_a_payment_order_id_is_unmatched()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody(paymentOrderId: "")), _ctx);

        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}::Completed", "unmatched:no_payment_order_id", Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "applied", Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "applied_again", Arg.Any<string?>());
        Logs.Should().NotContain("ORDER_PAYMENT_UNMATCHED").And.NotContain("ORDER_PAYMENT_DUPLICATE");
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
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "applied", Arg.Any<string?>());
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
        _orderRepo.GetByIdAsync(OrderId, true).Returns(StoredOrder("payment_submitted"));
        _orderRepo.TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>())
            .Returns(StoredOrder("funded", OrderPaymentId));
        _idemRepo.UpdateAsync(OrderInboxKey, "applied", Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_BOOKKEEPING_FAILED").And.Contain("step=inbox");
        Logs.Should().NotContain("ORDER_PAYMENT_UNPROCESSED");
        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Order_payment_without_order_tables_is_left_unprocessed()
    {
        var resp = await NewOrderFunction(wireOrders: false).FunctionHandler(Req(OrderBody()), _ctx);

        resp.StatusCode.Should().Be(200);
        await _orderRepo.DidNotReceive().TryMarkFundedAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _idemRepo.Received(1).UpdateAsync(
            $"webhook-inbox:{OrderMerchOrderId}:{OrderPaymentId}:Completed", "unprocessed", Arg.Any<string?>());
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

    private void InboxAlreadyHolds(string status)
    {
        _idemRepo.StoreAsync(OrderInboxKey, OrderEscrowId, "received", OrderMerchOrderId).Returns(false);
        _idemRepo.GetAsync(OrderInboxKey).Returns(new Dictionary<string, object>
        {
            { "idempotencyKey", OrderInboxKey }, { "escrowId", OrderEscrowId }, { "status", status }
        });
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
        await _idemRepo.DidNotReceive().UpdateAsync(OrderInboxKey, Arg.Any<string>(), Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "applied", Arg.Any<string?>());
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
        await _idemRepo.Received(1).UpdateAsync(OrderInboxKey, "unmatched:stale_checkout", Arg.Any<string?>());
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
    public async Task Order_payment_that_cannot_be_processed_leaves_an_audit_row()
    {
        _orderRepo.GetByIdAsync(OrderId, true).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        await NewOrderFunction().FunctionHandler(Req(OrderBody()), _ctx);

        Logs.Should().Contain("ORDER_PAYMENT_UNPROCESSED");
        await AuditRecorded("order_payment_unprocessed");
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
            ["data"] = relabelled, ["trade_status"] = "Failed", ["sign"] = sign, ["sign_type"] = "SHA256WithRSA",
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
}
