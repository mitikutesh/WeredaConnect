using System.Text;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class CropMatchStreamFunctionTests
{
    private readonly IIntentRepository _intents = Substitute.For<IIntentRepository>();
    private readonly IDeviceTokenRepository _devices = Substitute.For<IDeviceTokenRepository>();
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly CropMatchStreamFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public CropMatchStreamFunctionTests()
    {
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResponse());
        // No substitute intents unless a test overrides.
        _intents.QueryActiveByCropTypeAsync(Arg.Any<string>())
            .Returns(new List<Dictionary<string, object>>());
        _fn = new CropMatchStreamFunction(_intents, _devices, _sns);
    }

    private static Stream StreamEvent(
        string eventName = "INSERT",
        string cropType = "wheat",
        string status = "available",
        double pricePerKg = 100,
        double quantity = 10,
        string woreda = "Adama",
        string sequenceNumber = "seq-1")
    {
        var json = $$"""
        {
          "Records": [
            {
              "eventName": "{{eventName}}",
              "dynamodb": {
                "SequenceNumber": "{{sequenceNumber}}",
                "NewImage": {
                  "cropId": { "S": "crop-1" },
                  "cropType": { "S": "{{cropType}}" },
                  "status": { "S": "{{status}}" },
                  "pricePerKg": { "N": "{{pricePerKg}}" },
                  "quantity": { "N": "{{quantity}}" },
                  "woreda": { "S": "{{woreda}}" },
                  "locationName": { "S": "{{woreda}}" },
                  "createdAt": { "S": "2026-06-19T00:00:00Z" }
                }
              }
            }
          ]
        }
        """;
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, object> Intent(
        string intentId = "i1",
        string buyerId = "b1",
        string cropType = "wheat",
        string maxPricePerKg = "150",
        string quantityNeeded = "5",
        string deliveryWoreda = "Adama") => new()
    {
        { "intentId", intentId },
        { "buyerId", buyerId },
        { "cropType", cropType },
        { "maxPricePerKg", maxPricePerKg },
        { "quantityNeeded", quantityNeeded },
        { "deliveryWoreda", deliveryWoreda },
    };

    [Fact]
    public async Task MatchingIntent_WithEndpoint_PublishesPush()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1")
            .Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(1).PublishAsync(
            Arg.Is<PublishRequest>(r =>
                r.TargetArn == "arn:endpoint:1"
                && r.MessageStructure == "json"
                && r.Message.Contains("i1")
                && r.Message.Contains("crop-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonMatchingIntent_DoesNotPublish()
    {
        // Budget far below the crop price → not a candidate.
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent(maxPricePerKg: "50") });
        _devices.GetEndpointArnsByUserAsync(Arg.Any<string>())
            .Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MatchingIntent_NoEndpoint_DoesNotPublish()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string>());

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonInsertEvent_IsIgnored()
    {
        await _fn.ProcessStreamAsync(StreamEvent(eventName: "MODIFY"), _ctx);

        await _intents.DidNotReceive().QueryActiveByCropTypeAsync(Arg.Any<string>());
        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonAvailableCrop_IsIgnored()
    {
        await _fn.ProcessStreamAsync(StreamEvent(status: "sold"), _ctx);

        await _intents.DidNotReceive().QueryActiveByCropTypeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task TwoIntentsSameBuyer_PublishPerIntent_QueryEndpointsOnce()
    {
        // Endpoint-cache + dedupe: two distinct matching intents for one buyer
        // each get a push, but device tokens are queried only once.
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(intentId: "i1"),
            Intent(intentId: "i2"),
        });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(2).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
        await _devices.Received(1).GetEndpointArnsByUserAsync("b1");
    }

    [Fact]
    public async Task NewBarleyCrop_AlertsWheatIntent_ViaSubstituteFanOut()
    {
        // A new barley crop is an accepted substitute for a wheat intent — the
        // symmetric substitute map must fan the alert out to the wheat buyer.
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent(cropType: "wheat") });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(cropType: "barley"), _ctx);

        await _sns.Received(1).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordThatThrows_IsReportedAsBatchItemFailure()
    {
        // A transient fault on a record must be reported (by sequence number) so
        // Lambda retries it, rather than silently dropping the alert.
        _intents.QueryActiveByCropTypeAsync(Arg.Any<string>())
            .Returns<List<Dictionary<string, object>>>(_ => throw new Exception("dynamo throttle"));

        var response = await _fn.ProcessBatchAsync(StreamEvent(sequenceNumber: "seq-42"), _ctx);

        Assert.Single(response.BatchItemFailures);
        Assert.Equal("seq-42", response.BatchItemFailures[0].ItemIdentifier);
    }

    [Fact]
    public async Task SuccessfulBatch_ReportsNoFailures()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        var response = await _fn.ProcessBatchAsync(StreamEvent(), _ctx);

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task DisabledEndpoint_IsPruned()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:dead" });
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns<PublishResponse>(_ => throw new EndpointDisabledException("disabled"));

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _devices.Received(1).DeleteAsync("b1", "arn:dead");
    }
}
