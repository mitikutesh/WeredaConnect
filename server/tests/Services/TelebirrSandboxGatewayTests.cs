using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// Tests TelebirrSandboxGateway against a fake Telebirr (deterministic, no
/// network) that VERIFIES our request signature exactly as the simulator/live
/// gateway would — so a green test means a real Telebirr would accept the
/// request. A second, env-gated test drives the actual running Node simulator.
/// </summary>
public class TelebirrSandboxGatewayTests
{
    private const string TestPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA6yIoxNNWCDxM/XbGqwXv\nF6wL8j9LNkBbhc/5wIvTOqDiUY+PAKMeJPOSnDPmz/BUs1w0sILm3J2KMxpAiqIB\nuQ6c2WbLXrsdF6nKPQSsOSC8lqX6ZTrWEtQvlHgyIuBWCEIyd7jwTaKzJmonT10J\nt2F+ZY+1JdD6XLHh8ea1gSUb50X4VVm+zZzGU0SX+cul+RktVgsLZS85z74vVgNQ\nXnaWa9D/zjtr22A30K4KbZ51DmnWVvrwj/yNPu+AMzehhoYtessyK6gZgbbWhkAD\nzpKCtpnui86D7RxYy/UJCNklWGBq+GrEeR0aAKzwiQATYmKMBPzH652gladoyLBw\nTQIDAQAB\n-----END PUBLIC KEY-----\n";

    private const string TestPrivateKeyPem =
        "-----BEGIN PRIVATE KEY-----\nMIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDrIijE01YIPEz9\ndsarBe8XrAvyP0s2QFuFz/nAi9M6oOJRj48Aox4k85KcM+bP8FSzXDSwgubcnYoz\nGkCKogG5DpzZZsteux0Xqco9BKw5ILyWpfplOtYS1C+UeDIi4FYIQjJ3uPBNorMm\naidPXQm3YX5lj7Ul0PpcseHx5rWBJRvnRfhVWb7NnMZTRJf5y6X5GS1WCwtlLznP\nvi9WA1BedpZr0P/OO2vbYDfQrgptnnUOadZW+vCP/I0+74AzN6GGhi16yzIrqBmB\nttaGQAPOkoK2me6LzoPtHFjL9QkI2SVYYGr4asR5HRoArPCJABNiYowE/MfrnaCV\np2jIsHBNAgMBAAECggEAWZzGLuVgZ3kb2om3A50aV0pBr0yCDKYQe3oc9E4V7cU9\nG9p7pMGGDo0p9hAtylgM3+KnThJFjBUag4lgybUetPmlu5FatrrXRpp2MjUisg8Q\n8NVHDeeTiM+HZq08ZmBbSYB5HXnFU8FLR7uzSaLqvhIRCwHvBv+TO1qsAvZrfFD+\nHyXkRxRAqG02YwOBl/pdnlH+nHNmhsZgrf8Y5a1/9m1n5Mnh1+aZNxo/VphLCKLl\nwtyBzpZMS/oC+UYrSNZDyzPR5FHefw8lT7QL52r34qgxWRwVg3t6ZMJkARxT2mFB\nJUkMBXF++sC0JSyQNWm6ErSp00M/i3DGLqvn0n2kvQKBgQD/Gx847DNM2RDlt4Ux\nFqoIcy74qggu/RaKkfU16JBdzDJvy9kQ75Gxts+QrvoubNHt5/xq1SOFpOPNmbp/\nF7qhZm3aXItiDzQF3QUuQIFsf3aOhWBaldBJYzbXsAKYHrXsV6Xv3Lp1rWBhsYzL\nkdvq+Hqa5Dr7FT2HAdns24jLOwKBgQDr9R5BvidMbNXEdg0l2YHsUE3XVgdR18Ez\ndITwkGffIQGZGUh01QDlt1pGbg3qKIbEKD0jZsgq7afnJMpdELCkEglpKcrmSqlZ\ndsh6AHMMtbcmk0waQN2A5gfkwl0WNcw6+bdCad21W/2r8HReyvzDqukdel32YawP\n8tWgmEOqFwKBgGhS/hy4M2584nUe7OAbRaO4/v+AAWK962b1s5K9LbS+Ge4wLe/I\nGQvVF+MyO2ua4uro0Qgba7Busx5UfLkWyiLREaGX8QRD9WFkdoboqETnh2NXVgUV\neR1Z7oacGyN8g6dIXLrPyIzxPxYUluB1ILthrDoFlb0yfXvlnOpJCRMbAoGAaYxo\nUHhDFAjLRuqXzwqQ96ikaWLuO0oL/nxsyz+Nd15IXLfWlYyCrj9qfn7Ucp5WSoDF\neISNncZZiSgH7cF9gqce1o//VyymvIJKA2kYDeNrXjIdpNDHRNWhSk83XKwMA4WY\n2HiK2Imh27vgElcy4kQdqzYFuiTO1wOFVCVIf1kCgYAWDAwS2TyAxJo8i/u3z7Al\nFY74wqYCVuy1HtR164CMdR3Qh8y7I5PevHpjz5PLLIubfWAcDREuOvMqkFun0Vdx\nR7uQCfGXjvI7LWssollFoTk31jH1GRsjIgyRI20/tAt9s15fJQhQvlgjOh45fWU8\nGztgay7ux2C/q4RWFqd/jw==\n-----END PRIVATE KEY-----\n";

    private static TelebirrConfig TestConfig() => new()
    {
        FabricAppId = "local-fabric-app-id",
        AppSecret = "local-app-secret",
        MerchantAppId = "local-merchant-app-id",
        MerchantCode = "220100",
        ShortCode = "220100",
        BaseUrl = "http://telebirr.local",
        WebUrl = "http://telebirr.local/payment/web/paygate",
        MerchantPrivateKeyPem = TestPrivateKeyPem,
        TelebirrPublicKeyPem = TestPublicKeyPem,
        ReturnUrl = "weredaconnect://payment/complete",
        NotifyUrl = "http://server.local/webhooks/telebirr",
        Padding = TelebirrSignaturePadding.Pkcs1,
    };

    [Fact]
    public async Task HoldFunds_signs_a_request_the_gateway_would_accept_and_returns_toPayUrl()
    {
        // Fake Telebirr verifies OUR request signature with the merchant public key.
        var verifier = new TelebirrSignatureService(null, TestPublicKeyPem, TelebirrSignaturePadding.Pkcs1);
        string? capturedAuth = null, capturedAppKey = null;
        var signatureVerified = false;

        var handler = new FakeHandler(async (req) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/apply/fabric-token"))
            {
                capturedAppKey = req.Headers.TryGetValues("X-APP-Key", out var v) ? v.First() : null;
                return Json(new TelebirrTokenResponse { Token = "FAB-TOKEN", ExpiresIn = 7200 },
                    AppJsonContext.Default.TelebirrTokenResponse);
            }

            if (path.EndsWith("/payment/v1/merchant/preOrder"))
            {
                capturedAuth = req.Headers.TryGetValues("Authorization", out var a) ? a.First() : null;
                var bodyStr = await req.Content!.ReadAsStringAsync();
                var order = JsonSerializer.Deserialize(bodyStr, AppJsonContext.Default.TelebirrPreOrderRequest)!;
                // Reconstruct the signed view and verify — exactly like real Telebirr.
                var signable = new Dictionary<string, object?>
                {
                    ["nonce_str"] = order.NonceStr,
                    ["method"] = order.Method,
                    ["timestamp"] = order.Timestamp,
                    ["version"] = order.Version,
                    ["biz_content"] = new Dictionary<string, object?>
                    {
                        ["notify_url"] = order.BizContent.NotifyUrl,
                        ["appid"] = order.BizContent.AppId,
                        ["merch_code"] = order.BizContent.MerchCode,
                        ["merch_order_id"] = order.BizContent.MerchOrderId,
                        ["trade_type"] = order.BizContent.TradeType,
                        ["title"] = order.BizContent.Title,
                        ["total_amount"] = order.BizContent.TotalAmount,
                        ["trans_currency"] = order.BizContent.TransCurrency,
                        ["timeout_express"] = order.BizContent.TimeoutExpress,
                        ["business_type"] = order.BizContent.BusinessType,
                        ["callback_info"] = order.BizContent.CallbackInfo,
                        ["redirect_url"] = order.BizContent.RedirectUrl,
                    },
                };
                signatureVerified = verifier.VerifyObject(signable, order.Sign);
                return Json(new TelebirrPreOrderResponse
                {
                    Result = "SUCCESS",
                    Code = "0",
                    BizContent = new TelebirrPreOrderRespBiz { PrepayId = "PREPAY-XYZ", MerchOrderId = order.BizContent.MerchOrderId },
                }, AppJsonContext.Default.TelebirrPreOrderResponse);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gateway = new TelebirrSandboxGateway(
            new HttpClient(handler),
            new TelebirrSignatureService(TestPrivateKeyPem, null, TelebirrSignaturePadding.Pkcs1),
            TestConfig());

        var resp = await gateway.HoldFundsAsync(new HoldFundsRequest
        {
            TransactionRef = "buyer-1:delivery-1:initiate:1700000000000",
            BuyerPhone = "+251900100003",
            Amount = 15000m,
            Description = "WeredaConnect escrow for delivery delivery-1",
        });

        resp.Success.Should().BeTrue();
        signatureVerified.Should().BeTrue("real Telebirr would verify the preOrder signature");
        capturedAppKey.Should().Be("local-fabric-app-id");
        capturedAuth.Should().Be("FAB-TOKEN");
        resp.ToPayUrl.Should().NotBeNull();
        resp.ToPayUrl!.Should().StartWith("http://telebirr.local/payment/web/paygate?");
        resp.ToPayUrl.Should().Contain("prepay_id=PREPAY-XYZ").And.Contain("sign=").And.Contain("trade_type=Checkout");
        resp.TelebirrRef.Should().StartWith("PENDING-WEBHOOK-");
    }

    [Fact]
    public async Task HoldFunds_maps_preorder_failure_to_error()
    {
        var handler = new FakeHandler((req) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/apply/fabric-token"))
            {
                return Task.FromResult(Json(new TelebirrTokenResponse { Token = "T", ExpiresIn = 7200 },
                    AppJsonContext.Default.TelebirrTokenResponse));
            }

            return Task.FromResult(Json(new TelebirrPreOrderResponse { Result = "FAIL", Code = "60200099", Msg = "Verify the sign field failed" },
                AppJsonContext.Default.TelebirrPreOrderResponse));
        });

        var gateway = new TelebirrSandboxGateway(
            new HttpClient(handler),
            new TelebirrSignatureService(TestPrivateKeyPem, null, TelebirrSignaturePadding.Pkcs1),
            TestConfig());

        var resp = await gateway.HoldFundsAsync(new HoldFundsRequest
        {
            TransactionRef = "b:d:initiate:1", BuyerPhone = "+2519", Amount = 100m, Description = "x",
        });

        resp.Success.Should().BeFalse();
        resp.ErrorCode.Should().Be("TELEBIRR_60200099");
        resp.ToPayUrl.Should().BeNull();
    }

    [Fact]
    public async Task Release_and_refund_return_sandbox_sim_refs()
    {
        var gateway = new TelebirrSandboxGateway(
            new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new TelebirrSignatureService(TestPrivateKeyPem, TestPublicKeyPem),
            TestConfig());

        (await gateway.ReleaseFundsAsync(new ReleaseFundsRequest
        {
            TransactionRef = "release:e1", HoldRef = "h", FarmerPhone = "+2519", Amount = 1m, PlatformCommission = 0m,
        })).TelebirrRef.Should().StartWith("SANDBOX-SIM-PAYOUT-");

        (await gateway.RefundFundsAsync(new RefundFundsRequest
        {
            TransactionRef = "refund:e1", HoldRef = "h", BuyerPhone = "+2519", Amount = 1m,
        })).TelebirrRef.Should().StartWith("SANDBOX-SIM-REFUND-");
    }

    /// <summary>
    /// Live-simulator integration proof. Runs only when TELEBIRR_SIM_URL and
    /// TELEBIRR_CREDS_FILE point at a running simulator; otherwise skipped.
    ///   node tools/telebirr-sandbox/server.js &amp;
    ///   TELEBIRR_SIM_URL=http://127.0.0.1:8790 \
    ///   TELEBIRR_CREDS_FILE=tools/telebirr-sandbox/credentials.local.json dotnet test
    /// </summary>
    [Fact]
    public async Task HoldFunds_against_live_simulator()
    {
        var simUrl = Environment.GetEnvironmentVariable("TELEBIRR_SIM_URL");
        var credsFile = Environment.GetEnvironmentVariable("TELEBIRR_CREDS_FILE");
        if (string.IsNullOrEmpty(simUrl) || string.IsNullOrEmpty(credsFile) || !File.Exists(credsFile))
        {
            // No-op when the simulator isn't wired up (CI without a running sim).
            // Set TELEBIRR_SIM_URL + TELEBIRR_CREDS_FILE to exercise it for real.
            return;
        }

        var config = TelebirrConfig.FromJson(
            File.ReadAllText(credsFile!), notifyUrl: "http://127.0.0.1:1/webhooks/telebirr");
        var gateway = new TelebirrSandboxGateway(
            new HttpClient(),
            new TelebirrSignatureService(config.MerchantPrivateKeyPem, config.TelebirrPublicKeyPem, config.Padding),
            config);

        var resp = await gateway.HoldFundsAsync(new HoldFundsRequest
        {
            TransactionRef = $"itest:delivery:initiate:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            BuyerPhone = "+251900100003",
            Amount = 15000m,
            Description = "live-sim escrow test",
        });

        resp.Success.Should().BeTrue($"error: {resp.ErrorCode} {resp.ErrorMessage}");
        resp.ToPayUrl.Should().StartWith(config.WebUrl).And.Contain("prepay_id=");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, ti), Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _fn;
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _fn(request);
    }
}
