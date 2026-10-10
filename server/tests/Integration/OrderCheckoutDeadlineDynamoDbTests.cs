using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-45 (D6) timeout test on the real checkout path (LocalStack in CI): the live gateway over a
/// Telebirr that answers the token and never answers preOrder. The call is cut at the invocation's
/// deadline, so the slot is recorded as unknown with its window and an audit row, and the buyer gets
/// 502 ESCROW_013, all before the run would have ended; with too little time left nothing is sent and
/// the attempt is a definite failure the buyer can retry at once.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderCheckoutDeadlineDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _orderId = $"order-esc45-dl-{Guid.NewGuid():N}";

    public OrderCheckoutDeadlineDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    // A throwaway merchant key: preOrder is never answered, so nobody verifies the signature.
    private static readonly string TestPrivateKeyPem = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    private static TelebirrConfig Config() => new()
    {
        FabricAppId = "local-fabric-app-id",
        AppSecret = "local-app-secret",
        MerchantAppId = "local-merchant-app-id",
        MerchantCode = "220100",
        ShortCode = "220100",
        BaseUrl = "http://telebirr.local",
        WebUrl = "http://telebirr.local/payment/web/paygate",
        MerchantPrivateKeyPem = TestPrivateKeyPem,
        TelebirrPublicKeyPem = "unused",
        ReturnUrl = "weredaconnect://payment/complete",
        NotifyUrl = "http://server.local/webhooks/telebirr",
        Padding = TelebirrSignaturePadding.Pss,
    };

    /// <summary>Answers the token; holds every preOrder until it is cancelled.</summary>
    private sealed class PreOrderNeverAnswers : HttpMessageHandler
    {
        public int PreOrders;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/payment/v1/token", StringComparison.Ordinal))
            {
                var token = JsonSerializer.Serialize(
                    new TelebirrTokenResponse { Token = "Bearer T", ExpiresIn = 3600 }, AppJsonContext.Default.TelebirrTokenResponse);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(token, Encoding.UTF8, "application/json") };
            }

            Interlocked.Increment(ref PreOrders);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private async Task SeedOrderAsync()
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = _orderId },
                ["createdAt"] = new() { S = DateTime.UtcNow.AddHours(-1).ToString("o") },
                ["status"] = new() { S = "payment_submitted" },
                ["buyerId"] = new() { S = "buyer-esc45-dl" },
                ["farmerId"] = new() { S = "farmer-esc45-dl" },
                ["orderTotal"] = new() { N = "15000" },
                ["cropCost"] = new() { N = "14600" },
                ["paymentSubmittedAt"] = new() { S = DateTime.UtcNow.AddMinutes(-30).ToString("o") },
                ["version"] = new() { N = "3" }
            }
        });
    }

    private (EscrowService Service, OrderCheckoutRepository Checkouts, EscrowAuditRepository Audit) Service(HttpMessageHandler telebirr)
    {
        var escrowRepo = new EscrowRepository(Ddb, _db.Escrows);
        var audit = new EscrowAuditRepository(Ddb, _db.EscrowAudit);
        var checkouts = new OrderCheckoutRepository(Ddb, _db.Idempotency);
        var gateway = new TelebirrSandboxGateway(
            new HttpClient(telebirr) { Timeout = TimeSpan.FromSeconds(30) },
            new TelebirrSignatureService(TestPrivateKeyPem, null, TelebirrSignaturePadding.Pss), Config());
        var service = new EscrowService(escrowRepo, audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway,
            orderRepo: new OrderRepository(Ddb, _db.Orders), orderCheckoutRepo: checkouts);
        return (service, checkouts, audit);
    }

    [LocalDynamoDbFact]
    public async Task A_preorder_that_never_answers_is_cut_at_the_deadline_and_recorded_as_unknown()
    {
        await SeedOrderAsync();
        var telebirr = new PreOrderNeverAnswers();
        var (service, checkouts, audit) = Service(telebirr);

        // The escrow Lambda with 8 s left keeps 5 s back: Telebirr gets 3 s.
        using var deadline = GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(8) - TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        var result = await service.CreateOrderCheckoutAsync(_orderId, "buyer-esc45-dl");
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7), "the answer is out before the 8 s run would end");
        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(502);
        result.Error.Code.Should().Be("ESCROW_013");
        telebirr.PreOrders.Should().Be(1);

        var slot = await checkouts.GetSlotAsync(_orderId);
        slot!.Outcome.Should().Be(OrderCheckoutOutcome.Unknown, "Telebirr may have created the payment");
        slot.OpenUntilUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(120));
        (await audit.GetAuditTrailAsync($"escrow-order-{_orderId}"))
            .Should().Contain(e => e["action"].ToString() == "order_checkout_unknown");
    }

    [LocalDynamoDbFact]
    public async Task With_too_little_time_left_nothing_is_sent_and_the_buyer_can_retry_at_once()
    {
        await SeedOrderAsync();
        var telebirr = new PreOrderNeverAnswers();
        var (service, checkouts, _) = Service(telebirr);

        using (GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(1)))
        {
            var result = await service.CreateOrderCheckoutAsync(_orderId, "buyer-esc45-dl");
            result.Error!.Code.Should().Be("ESCROW_013");
        }

        telebirr.PreOrders.Should().Be(0);
        (await checkouts.GetSlotAsync(_orderId))!.Outcome.Should().Be(OrderCheckoutOutcome.Failed);

        // The next request opens a new attempt instead of waiting out a 130-minute window.
        using (GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(1)))
        {
            await service.CreateOrderCheckoutAsync(_orderId, "buyer-esc45-dl");
        }

        (await checkouts.GetSlotAsync(_orderId))!.Attempt.Should().Be(2);
    }
}
