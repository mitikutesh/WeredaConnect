using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// Verifies the Telebirr webhook handler: a valid signed "Completed" moves the
/// escrow initiated→escrowed; "Failed" cancels it; and each security layer
/// (bad signature, stale timestamp, replay, amount mismatch) blocks the
/// transition. All responses are 200 OK.
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

    private TelebirrWebhookFunction NewFunction()
        => new(_escrowRepo, _auditRepo, _idemRepo,
            new TelebirrSignatureService(null, PublicKeyPem, TelebirrSignaturePadding.Pkcs1));

    private static string SignedBody(string tradeStatus, string totalAmount = "15000.00", bool tamper = false, long? timestampMs = null)
    {
        var ts = (timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
        var signable = new Dictionary<string, object?>
        {
            ["method"] = "payment.notify",
            ["nonce_str"] = "n-123",
            ["timestamp"] = ts,
            ["notify_time"] = "2026-07-07T10:05:00Z",
            ["sign_type"] = "SHA256WithRSA",
            ["biz_content"] = new Dictionary<string, object?>
            {
                ["merch_order_id"] = MerchOrderId,
                ["payment_order_id"] = "TB123",
                ["total_amount"] = totalAmount,
                ["trans_currency"] = "ETB",
                ["trade_status"] = tradeStatus,
                ["merch_code"] = "220100",
                ["trans_id"] = "TXN9",
            },
        };
        var signer = new TelebirrSignatureService(PrivateKeyPem, null, TelebirrSignaturePadding.Pkcs1);
        var sign = tamper ? "AAAA" + signer.SignObject(signable)[4..] : signer.SignObject(signable);

        var notification = new
        {
            method = "payment.notify",
            nonce_str = "n-123",
            timestamp = ts,
            notify_time = "2026-07-07T10:05:00Z",
            sign_type = "SHA256WithRSA",
            sign,
            biz_content = new
            {
                merch_order_id = MerchOrderId,
                payment_order_id = "TB123",
                total_amount = totalAmount,
                trans_currency = "ETB",
                trade_status = tradeStatus,
                merch_code = "220100",
                trans_id = "TXN9",
            },
        };
        // Serialize with camel/snake keys as-is (anonymous type uses these exact names).
        return JsonSerializer.Serialize(notification);
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
        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed", timestampMs: stale)), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Replayed_delivery_does_not_transition_again()
    {
        _idemRepo.StoreAsync(Arg.Is<string>(k => k.StartsWith("webhook:")), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false); // already processed

        var resp = await NewFunction().FunctionHandler(Req(SignedBody("Completed")), _ctx);

        resp.StatusCode.Should().Be(200);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }
}
