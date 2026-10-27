using System.Text;
using System.Text.Json;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-21: the orders, deliveries and escrows stream consumer. Every derived event becomes one inbox
/// row (conditional on its key, so a retried record writes nothing twice) and, when it asks for one,
/// an FCM push in the recipient's language with a deadline-bound time to live. A failed derive or
/// inbox write is retried by sequence number; nothing on the push path fails a record.
/// </summary>
public class OrderNotifyStreamFunctionTests
{
    private const string OrdersArn = "arn:aws:dynamodb:eu-north-1:123:table/wereda-connect-dev-orders/stream/2026-10-26T00:00:00.000";
    private const string DeliveriesArn = "arn:aws:dynamodb:eu-north-1:123:table/wereda-connect-dev-deliveries/stream/2026-10-26T00:00:00.000";
    private const string EscrowsArn = "arn:aws:dynamodb:eu-north-1:123:table/wereda-connect-dev-escrows/stream/2026-10-26T00:00:00.000";
    private const string MessagesArn = "arn:aws:dynamodb:eu-north-1:123:table/wereda-connect-dev-messages/stream/2026-10-26T00:00:00.000";

    // 2026-10-26T12:00:00Z
    private const long RecordEpochSeconds = 1793016000;
    private static readonly DateTime RecordTime = DateTime.UnixEpoch.AddSeconds(RecordEpochSeconds);

    private readonly IDeviceTokenRepository _devices = Substitute.For<IDeviceTokenRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly INotificationRepository _inbox = Substitute.For<INotificationRepository>();
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly TestLambdaContext _context = new();
    private readonly List<NotificationRow> _rows = new();
    private readonly List<PublishRequest> _published = new();
    private readonly OrderNotifyStreamFunction _function;

    public OrderNotifyStreamFunctionTests()
    {
        _inbox.TryPutAsync(Arg.Do<NotificationRow>(r => _rows.Add(r))).Returns(true);
        _sns.PublishAsync(Arg.Do<PublishRequest>(r => _published.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new PublishResponse());
        _devices.GetEndpointArnsByUserAsync("buyer-1").Returns(new List<string> { "arn:endpoint/buyer-1" });
        _devices.GetEndpointArnsByUserAsync("farmer-1").Returns(new List<string> { "arn:endpoint/farmer-1a", "arn:endpoint/farmer-1b" });
        _devices.GetEndpointArnsByUserAsync("trucker-1").Returns(new List<string>());
        _users.GetPreferredLanguagesAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, string> { ["buyer-1"] = "en", ["farmer-1"] = "am" });
        _function = new OrderNotifyStreamFunction(_devices, _users, _inbox, _sns, new FixedClock(RecordTime.AddMinutes(1)));
    }

    // ===== through the real rules =====

    [Fact]
    public async Task FundedOrder_WritesBothInboxRows_AndPushesEveryDevice()
    {
        var response = await RunAsync(FundedRecord());

        response.BatchItemFailures.Should().BeEmpty();
        _rows.Should().Contain(r => r.UserId == "buyer-1" && r.Type == NotificationTypes.PaymentConfirmed);
        _rows.Should().Contain(r => r.UserId == "farmer-1" && r.Type == NotificationTypes.OrderRequest);
        var request = _rows.Single(r => r.Type == NotificationTypes.OrderRequest);
        request.SortKey.Should().Be($"{RecordTime:o}#{NotificationTypes.OrderRequest}#order-1");
        request.CreatedAt.Should().Be(RecordTime.ToString("o"));
        request.ExpiresAt.Should().Be(new DateTimeOffset(RecordTime).AddDays(90).ToUnixTimeSeconds());
        request.OrderId.Should().Be("order-1");
        // The write time (the clock, a minute after the change): the incremental pull reads by it.
        request.WrittenAt.Should().Be(RecordTime.AddMinutes(1).ToString("o"));

        _published.Select(p => p.TargetArn).Should().Contain(new[] { "arn:endpoint/buyer-1", "arn:endpoint/farmer-1a", "arn:endpoint/farmer-1b" });
        var farmerPush = _published.First(p => p.TargetArn == "arn:endpoint/farmer-1a");
        farmerPush.MessageStructure.Should().Be("json");
        var fcm = JsonDocument.Parse(JsonDocument.Parse(farmerPush.Message).RootElement.GetProperty("GCM").GetString()!).RootElement;
        fcm.GetProperty("data").GetProperty("type").GetString().Should().Be("order");
        fcm.GetProperty("data").GetProperty("event").GetString().Should().Be(NotificationTypes.OrderRequest);
        fcm.GetProperty("data").GetProperty("orderId").GetString().Should().Be("order-1");
        fcm.GetProperty("data").GetProperty("notificationId").GetString().Should().Be(request.SortKey);
        fcm.GetProperty("notification").GetProperty("tag").GetString().Should().Be("order-order-1");
        fcm.GetProperty("notification").GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        farmerPush.Message.Should().NotContain("+251");
        // An action-required push lives until the farmer's deadline (24h after funding, the clock is 1 minute in).
        farmerPush.MessageAttributes["AWS.SNS.MOBILE.FCM.TTL"].StringValue.Should().Be(((24 * 60 - 1) * 60).ToString());
        // The line the setup runbook filters on (contract section 5).
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(
            "NOTIFY_EMITTED type=order_request entity=order entityId=order-1 recipient=farmer-1 push=true new=true");
    }

    [Fact]
    public async Task AnEventWithoutAnOrder_PushesNoTagAndNoOrderId()
    {
        _function.Derive = (_, _, _, _, _) => new[]
        {
            new NotificationEvent("buyer-1", NotificationTypes.RefundSent, "escrow", "escrow-1", null,
                new Dictionary<string, string>())
        };

        await RunAsync(Record(EscrowsArn, "seq-1"));

        var fcm = JsonDocument.Parse(JsonDocument.Parse(_published.Single().Message).RootElement.GetProperty("GCM").GetString()!).RootElement;
        fcm.GetProperty("notification").TryGetProperty("tag", out _).Should().BeFalse();
        fcm.GetProperty("data").TryGetProperty("orderId", out _).Should().BeFalse();
        fcm.GetProperty("data").GetProperty("entityId").GetString().Should().Be("escrow-1");
        _rows.Single().OrderId.Should().BeNull();
    }

    [Fact]
    public async Task ARecordWithoutItsChangeTime_Fails_RatherThanGetAClockKey()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };
        var record = $$"""
            {
              "eventName": "MODIFY",
              "eventSourceARN": "{{OrdersArn}}",
              "dynamodb": {
                "SequenceNumber": "seq-1",
                "OldImage": { "orderId": { "S": "order-1" } },
                "NewImage": { "orderId": { "S": "order-1" } }
              }
            }
            """;

        var response = await RunAsync(record);

        response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().Equal("seq-1");
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task OneLanguageLookupPerBatch()
    {
        await RunAsync(FundedRecord("seq-1"), FundedRecord("seq-2", orderId: "order-2"));

        await _users.Received(1).GetPreferredLanguagesAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // ===== through a fixed rule set =====

    [Fact]
    public async Task DuplicateRow_StillPushes()
    {
        _inbox.TryPutAsync(Arg.Any<NotificationRow>()).Returns(false);
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };

        var response = await RunAsync(Record(OrdersArn, "seq-1"));

        response.BatchItemFailures.Should().BeEmpty();
        _published.Should().ContainSingle(p => p.TargetArn == "arn:endpoint/buyer-1");
    }

    [Fact]
    public async Task InboxOnlyEvent_IsNotPushed()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.UnderReview, push: false) };

        await RunAsync(Record(OrdersArn, "seq-1"));

        _rows.Should().ContainSingle(r => r.Type == NotificationTypes.UnderReview);
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownTable_IsSkipped()
    {
        var derived = false;
        _function.Derive = (_, _, _, _, _) =>
        {
            derived = true;
            return Array.Empty<NotificationEvent>();
        };

        var response = await RunAsync(Record(MessagesArn, "seq-1"));

        response.BatchItemFailures.Should().BeEmpty();
        derived.Should().BeFalse();
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TheTableComesFromTheSourceArn()
    {
        string? table = null;
        _function.Derive = (t, _, _, _, _) =>
        {
            table = t;
            return Array.Empty<NotificationEvent>();
        };

        await RunAsync(Record(DeliveriesArn, "seq-1", eventName: "INSERT"));

        table.Should().Be("deliveries");
        OrderNotifyStreamFunction.TableOf("arn:aws:dynamodb:x:1:table/p-escrows/stream/s").Should().Be("escrows");
        OrderNotifyStreamFunction.TableOf("arn:aws:dynamodb:x:1:table/p-orders-archive/stream/s").Should().BeNull();
    }

    [Fact]
    public async Task FailedInboxWrite_ReportsThatRecord_AndStopsTheBatchThere()
    {
        // Lambda retries from the first failed record on, so the records after it wait for the
        // retry instead of being pushed now and again then.
        _function.Derive = (_, _, _, newImage, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed, entityId: newImage.String("orderId")) };
        _inbox.TryPutAsync(Arg.Is<NotificationRow>(r => r.EntityId == "order-bad"))
            .ThrowsAsync(new InvalidOperationException("dynamo down"));

        var response = await RunAsync(
            Record(OrdersArn, "seq-good", orderId: "order-good"),
            Record(OrdersArn, "seq-bad", orderId: "order-bad"),
            Record(OrdersArn, "seq-after", orderId: "order-after"));

        response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().Equal("seq-bad");
        _published.Should().ContainSingle();
        _rows.Should().NotContain(r => r.EntityId == "order-after");
    }

    [Fact]
    public async Task FailedDerive_ReportsThatRecord_AndStopsTheBatchThere()
    {
        _function.Derive = (_, _, _, newImage, _) => newImage.String("orderId") == "order-bad"
            ? throw new InvalidOperationException("bad image")
            : new[] { Event("buyer-1", NotificationTypes.OrderConfirmed, entityId: newImage.String("orderId")) };

        var response = await RunAsync(
            Record(OrdersArn, "seq-1", orderId: "order-good"),
            Record(OrdersArn, "seq-2", orderId: "order-bad"),
            Record(OrdersArn, "seq-3", orderId: "order-after"));

        response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().Equal("seq-2");
        _rows.Select(r => r.EntityId).Should().Equal("order-good");
    }

    [Fact]
    public async Task Push_GoesToAtMostTheDeviceCap()
    {
        _devices.GetEndpointArnsByUserAsync("buyer-1")
            .Returns(Enumerable.Range(1, 25).Select(i => $"arn:endpoint/buyer-1-{i}").ToList());
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };

        await RunAsync(Record(OrdersArn, "seq-1"));

        _published.Should().HaveCount(OrderNotifyStreamFunction.MaxEndpointsPerUser);
    }

    [Fact]
    public async Task OutOfTime_StillWritesTheInbox_ButSendsNoPush()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };
        var json = $"{{\"Records\":[{Record(OrdersArn, "seq-1")}]}}";
        var lateContext = new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(2) };

        var response = await _function.ProcessBatchAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), lateContext);

        response.BatchItemFailures.Should().BeEmpty();
        _rows.Should().ContainSingle();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishFailure_DoesNotFailTheRecord()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InternalErrorException("sns down"));

        var response = await RunAsync(Record(OrdersArn, "seq-1"));

        response.BatchItemFailures.Should().BeEmpty();
        _rows.Should().ContainSingle();
    }

    [Fact]
    public async Task DisabledEndpoint_IsPruned()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new EndpointDisabledException("disabled"));

        await RunAsync(Record(OrdersArn, "seq-1"));

        await _devices.Received(1).DeleteAsync("buyer-1", "arn:endpoint/buyer-1");
    }

    [Fact]
    public async Task NoDevice_WritesTheInboxOnly()
    {
        _function.Derive = (_, _, _, _, _) => new[] { Event("trucker-1", NotificationTypes.TransportRequest) };

        var response = await RunAsync(Record(OrdersArn, "seq-1"));

        response.BatchItemFailures.Should().BeEmpty();
        _rows.Should().ContainSingle(r => r.UserId == "trucker-1");
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task LanguageLookupFailure_StillPushes()
    {
        _users.GetPreferredLanguagesAsync(Arg.Any<IReadOnlyCollection<string>>())
            .ThrowsAsync(new InvalidOperationException("users down"));
        _function.Derive = (_, _, _, _, _) => new[] { Event("buyer-1", NotificationTypes.OrderConfirmed) };

        var response = await RunAsync(Record(OrdersArn, "seq-1"));

        response.BatchItemFailures.Should().BeEmpty();
        _published.Should().ContainSingle();
    }

    [Fact]
    public void PushTtl_FollowsTheDeadline_OnlyForActionRequiredTypes()
    {
        var now = RecordTime;
        OrderNotifyStreamFunction.PushTtlSeconds(Event("farmer-1", NotificationTypes.OrderRequest, deadline: now.AddHours(2)), now)
            .Should().Be(7200);
        OrderNotifyStreamFunction.PushTtlSeconds(Event("farmer-1", NotificationTypes.OrderRequest, deadline: now.AddSeconds(-30)), now)
            .Should().Be(OrderNotifyStreamFunction.MinPushTtlSeconds);
        OrderNotifyStreamFunction.PushTtlSeconds(Event("buyer-1", NotificationTypes.OrderConfirmed, deadline: now.AddHours(2)), now)
            .Should().Be(OrderNotifyStreamFunction.DefaultPushTtlSeconds);
        OrderNotifyStreamFunction.PushTtlSeconds(Event("farmer-1", NotificationTypes.OrderRequest), now)
            .Should().Be(OrderNotifyStreamFunction.DefaultPushTtlSeconds);
    }

    // ===== helpers =====

    private async Task<WeredaConnect.Server.Models.StreamBatchResponse> RunAsync(params string[] records)
    {
        var json = $"{{\"Records\":[{string.Join(",", records)}]}}";
        return await _function.ProcessBatchAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), _context);
    }

    private static NotificationEvent Event(
        string recipient, string type, bool push = true, string entityId = "order-1", DateTime? deadline = null)
        => new(recipient, type, "order", entityId, "order-1",
            new Dictionary<string, string> { [NotificationParams.BuyerName] = "Meron Alemu" }, push, deadline);

    private static string Record(string arn, string sequenceNumber, string eventName = "MODIFY", string orderId = "order-1") => $$"""
        {
          "eventName": "{{eventName}}",
          "eventSourceARN": "{{arn}}",
          "dynamodb": {
            "ApproximateCreationDateTime": {{RecordEpochSeconds}},
            "SequenceNumber": "{{sequenceNumber}}",
            "OldImage": { "orderId": { "S": "{{orderId}}" }, "status": { "S": "accepted" } },
            "NewImage": { "orderId": { "S": "{{orderId}}" }, "status": { "S": "pickup_confirmed" } }
          }
        }
        """;

    /// <summary>A recorded orders-table MODIFY: the mock payment was verified (payment_submitted to funded).</summary>
    private static string FundedRecord(string sequenceNumber = "seq-1", string orderId = "order-1")
    {
        var deadline = RecordTime.AddHours(24).ToString("o");
        var common = $$"""
            "orderId": { "S": "{{orderId}}" }, "buyerId": { "S": "buyer-1" }, "farmerId": { "S": "farmer-1" },
            "buyerName": { "S": "Meron Alemu" }, "farmerName": { "S": "Abebe Kebede" },
            "tripId": { "S": "trip-1" }, "truckerId": { "S": "trucker-1" }, "truckerName": { "S": "Tadesse Worku" },
            "cropType": { "S": "teff" }, "quantity": { "N": "500" }, "orderTotal": { "N": "24187.5" }
            """;
        return $$"""
            {
              "eventName": "MODIFY",
              "eventSourceARN": "{{OrdersArn}}",
              "dynamodb": {
                "ApproximateCreationDateTime": {{RecordEpochSeconds}},
                "SequenceNumber": "{{sequenceNumber}}",
                "OldImage": { {{common}}, "status": { "S": "payment_submitted" } },
                "NewImage": { {{common}}, "status": { "S": "funded" },
                  "farmerResponse": { "S": "pending" }, "truckerResponse": { "S": "not_asked" },
                  "farmerDeadlineAt": { "S": "{{deadline}}" } }
              }
            }
            """;
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTime nowUtc) => _now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
