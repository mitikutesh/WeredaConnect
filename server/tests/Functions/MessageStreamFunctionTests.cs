using System.Text;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class MessageStreamFunctionTests
{
    private readonly IDeviceTokenRepository _devices = Substitute.For<IDeviceTokenRepository>();
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly MessageStreamFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public MessageStreamFunctionTests()
    {
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResponse());
        _fn = new MessageStreamFunction(_devices, _sns);
    }

    private static Stream StreamEvent(
        string eventName = "INSERT",
        string recipientId = "farmer-1",
        string senderName = "Buyer One",
        string body = "Is this still available?",
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
                  "messageId": { "S": "msg-1" },
                  "conversationId": { "S": "crop:crop-1:buyer-1:farmer-1" },
                  "contextType": { "S": "crop" },
                  "contextId": { "S": "crop-1" },
                  "senderId": { "S": "buyer-1" },
                  "recipientId": { "S": "{{recipientId}}" },
                  "senderName": { "S": "{{senderName}}" },
                  "body": { "S": "{{body}}" },
                  "createdAt": { "S": "2026-07-09T10:00:00Z" }
                }
              }
            }
          ]
        }
        """;
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task NewMessage_WithEndpoint_PublishesPushToRecipient()
    {
        _devices.GetEndpointArnsByUserAsync("farmer-1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(1).PublishAsync(
            Arg.Is<PublishRequest>(r =>
                r.TargetArn == "arn:endpoint:1"
                && r.MessageStructure == "json"
                && r.Message.Contains("Buyer One")
                && r.Message.Contains("crop:crop-1:buyer-1:farmer-1")
                && r.Message.Contains("msg-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewMessage_NoEndpoint_DoesNotPublish()
    {
        _devices.GetEndpointArnsByUserAsync("farmer-1").Returns(new List<string>());

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.DidNotReceive().PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonInsertEvent_IsIgnored()
    {
        await _fn.ProcessStreamAsync(StreamEvent(eventName: "MODIFY"), _ctx);

        await _devices.DidNotReceive().GetEndpointArnsByUserAsync(Arg.Any<string>());
        await _sns.DidNotReceive().PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledEndpoint_IsPruned()
    {
        _devices.GetEndpointArnsByUserAsync("farmer-1").Returns(new List<string> { "arn:dead" });
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns<PublishResponse>(_ => throw new EndpointDisabledException("disabled"));

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _devices.Received(1).DeleteAsync("farmer-1", "arn:dead");
    }

    [Fact]
    public async Task RecordThatThrows_IsReportedAsBatchItemFailure()
    {
        _devices.GetEndpointArnsByUserAsync(Arg.Any<string>())
            .Returns<List<string>>(_ => throw new Exception("dynamo throttle"));

        var response = await _fn.ProcessBatchAsync(StreamEvent(sequenceNumber: "seq-42"), _ctx);

        Assert.Single(response.BatchItemFailures);
        Assert.Equal("seq-42", response.BatchItemFailures[0].ItemIdentifier);
    }
}
