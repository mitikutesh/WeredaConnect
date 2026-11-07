using System.Text.Json;
using Amazon.Lambda.TestUtilities;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-28: the second orders stream consumer (reputation-architecture.md section 7): records events
/// from recorded order images, never twice for one slot, acks without a write while the flag is
/// off, parks a failing record in SQS with projected images (under 256 KB even for 400 KB images),
/// reports it only when parking fails, reads reachability for an expiry, and the order-cron's
/// unpark replays a parked record through the same path.
/// </summary>
public class ReputationStreamFunctionTests
{
    private const string QueueUrl = "https://sqs.eu-north-1.amazonaws.com/123/wereda-connect-dev-reputation-parked";

    private readonly FakeReputationRepository _repo = new();
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
    private readonly TestLambdaContext _context = new();
    private readonly List<string> _reachabilityReads = new();
    private bool _reachable = true;

    public ReputationStreamFunctionTests()
    {
        _repo.AddUser("farmer-1");
        _repo.AddUser("trucker-1", "trucker");
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());
    }

    private ReputationRecordProcessor Processor(bool timeoutPenalties = false)
        => new(
            new ReputationService(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(),
                new ManualClock(new DateTime(2026, 11, 7, 9, 5, 0, DateTimeKind.Utc)), _ => Task.CompletedTask),
            userId =>
            {
                _reachabilityReads.Add(userId);
                return Task.FromResult(_reachable);
            },
            timeoutPenalties);

    private ReputationStreamFunction Function(bool enabled = true, bool timeoutPenalties = false)
        => new(Processor(timeoutPenalties), _sqs, QueueUrl, enabled);

    private static Dictionary<string, object> Order(string orderId, params (string Key, object Value)[] fields)
    {
        var image = new Dictionary<string, object>
        {
            ["orderId"] = orderId,
            ["farmerId"] = "farmer-1",
            ["buyerId"] = "buyer-1",
            ["truckerId"] = "trucker-1",
            ["tripId"] = "trip-1",
            ["paymentVerifiedAt"] = "2026-11-06T09:00:00.000Z",
            ["farmerDeadlineAt"] = "2026-11-07T09:00:00.000Z",
            ["updatedAt"] = "2026-11-07T09:00:00.000Z"
        };
        foreach (var (key, value) in fields)
        {
            image[key] = value;
        }

        return image;
    }

    private static string FarmerDecline(string orderId = "order-1", string sequence = "100")
        => OrderRecords.Modify(
            Order(orderId, ("status", "funded"), ("farmerResponse", "pending")),
            Order(orderId, ("status", "cancelled"), ("farmerResponse", "declined")),
            sequence);

    private static string FarmerExpiry(string orderId = "order-9", string sequence = "300")
        => OrderRecords.Modify(
            Order(orderId, ("status", "funded"), ("farmerResponse", "pending")),
            Order(orderId, ("status", "cancelled"), ("farmerResponse", "expired")),
            sequence);

    private Task<StreamBatchResponse> Run(ReputationStreamFunction function, params string[] records)
        => function.ProcessBatchAsync(OrderRecords.AsStream(OrderRecords.Batch(records)), _context);

    [Fact]
    public async Task ARecord_RecordsItsEvent()
    {
        var response = await Run(Function(), FarmerDecline());

        response.BatchItemFailures.Should().BeEmpty();
        var row = _repo.Row("farmer-1", "REQUEST#order-1");
        row.Code.Should().Be(ReputationCodes.DeclinedRequest);
        row.Points.Should().Be(1);
        row.SourceSeq.Should().Be("100");
    }

    [Fact]
    public async Task TheSameRecordTwice_InOneBatchOrTwo_CountsOnce()
    {
        var function = Function();
        await Run(function, FarmerDecline(), FarmerDecline());
        await Run(function, FarmerDecline(sequence: "101"));

        _repo.RowsOf("farmer-1").Count(r => r.Code == ReputationCodes.DeclinedRequest).Should().Be(1);
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(1);
        _repo.Committed.Should().ContainSingle();
    }

    [Fact]
    public async Task TwoOrdersOfOneTripCancel_GiveOneTripEvent()
    {
        string TripCancel(string orderId, string sequence) => OrderRecords.Modify(
            Order(orderId, ("status", "accepted"), ("farmerResponse", "accepted"), ("truckerResponse", "accepted")),
            Order(orderId, ("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "cancelled"),
                ("transportFailure", "trip_cancelled")),
            sequence);

        await Run(Function(), TripCancel("order-1", "200"), TripCancel("order-2", "201"));

        _repo.RowsOf("trucker-1").Where(r => r.Code == ReputationCodes.TripCancelledWithBookings).Should().ContainSingle()
            .Which.Points.Should().Be(3);
    }

    [Fact]
    public async Task FlagOff_AcksWithoutAWrite()
    {
        ReputationStreamFunction.ResetForTests();
        var response = await Run(Function(enabled: false), FarmerDecline());

        response.BatchItemFailures.Should().BeEmpty();
        _repo.Committed.Should().BeEmpty();
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(ReputationStreamFunction.DisabledToken);
    }

    [Fact]
    public async Task TimeoutPenaltiesOff_AnExpiryIsRecordedWithZero_WithoutAReachabilityRead()
    {
        await Run(Function(timeoutPenalties: false), FarmerExpiry());

        var row = _repo.Row("farmer-1", "REQUEST#order-9");
        row.Points.Should().Be(0);
        row.ZeroReason.Should().Be(ReputationZeroReasons.TimeoutPenaltiesOff);
        _reachabilityReads.Should().BeEmpty();
    }

    [Fact]
    public async Task TimeoutPenaltiesOn_ReachabilityDecides()
    {
        _reachable = false;
        await Run(Function(timeoutPenalties: true), FarmerExpiry());
        _repo.Row("farmer-1", "REQUEST#order-9").ZeroReason.Should().Be(ReputationZeroReasons.NotReachable);
        _reachabilityReads.Should().Equal("farmer-1");

        _reachable = true;
        await Run(Function(timeoutPenalties: true), FarmerExpiry("order-10", "301"));
        _repo.Row("farmer-1", "REQUEST#order-10").Points.Should().Be(1);
    }

    [Fact]
    public async Task AFailingRecord_IsParked_AndTheBatchMovesOn()
    {
        _repo.ReadFailure = new InvalidOperationException("throttled");
        SendMessageRequest? sent = null;
        _sqs.SendMessageAsync(Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());

        var response = await Run(Function(), FarmerDecline());

        response.BatchItemFailures.Should().BeEmpty();
        sent!.QueueUrl.Should().Be(QueueUrl);
        using var doc = JsonDocument.Parse(sent.MessageBody);
        doc.RootElement.GetProperty("dynamodb").GetProperty("SequenceNumber").GetString().Should().Be("100");
        doc.RootElement.GetProperty("dynamodb").GetProperty("NewImage").GetProperty("farmerResponse").GetProperty("S").GetString()
            .Should().Be("declined");
    }

    [Fact]
    public async Task AFailingRecord_ThatCannotBeParked_IsReported_AndTheBatchStops()
    {
        _repo.ReadFailure = new InvalidOperationException("throttled");
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AmazonSQSException("down"));

        var response = await Run(Function(), FarmerDecline(sequence: "100"), FarmerDecline("order-2", "101"));

        response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().Equal("100");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("REPUTATION_PARK_FAILED reason=send");
    }

    [Fact]
    public async Task WithoutAQueue_AFailingRecordIsReported()
    {
        _repo.ReadFailure = new InvalidOperationException("throttled");

        var response = await new ReputationStreamFunction(Processor(), null, null, penaltiesEnabled: true)
            .ProcessBatchAsync(OrderRecords.AsStream(OrderRecords.Batch(FarmerDecline())), _context);

        response.BatchItemFailures.Select(f => f.ItemIdentifier).Should().Equal("100");
    }

    [Fact]
    public void AParkedMessage_ForTwo400KbImages_KeepsOnlyTheDerivationsAttributes()
    {
        var bulk = new string('x', 400_000);
        var record = OrderRecords.Modify(
            Order("order-1", ("status", "funded"), ("farmerResponse", "pending"), ("buyerNotes", bulk), ("cropPhotos", bulk)),
            Order("order-1", ("status", "cancelled"), ("farmerResponse", "declined"), ("buyerNotes", bulk), ("cropPhotos", bulk)));
        using var doc = JsonDocument.Parse(record);

        var message = ReputationParking.BuildMessage(doc.RootElement);

        message.Should().NotBeNull();
        message!.Length.Should().BeLessThan(2048);
        message.Should().NotContain("buyerNotes");
        using var parked = JsonDocument.Parse(message);
        ReputationDerivation.Derive(
                new StreamImage(parked.RootElement.GetProperty("dynamodb").GetProperty("OldImage").Clone()),
                new StreamImage(parked.RootElement.GetProperty("dynamodb").GetProperty("NewImage").Clone()),
                null, null)
            .Should().ContainSingle(c => c.EventId == "REQUEST#order-1");
    }

    [Fact]
    public async Task Unpark_ReplaysAParkedRecord_AndDeletesIt_KeepsOneThatFails()
    {
        using var good = JsonDocument.Parse(FarmerDecline());
        var goodMessage = ReputationParking.BuildMessage(good.RootElement)!;
        _sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReceiveMessageResponse
                {
                    Messages = new List<Message>
                    {
                        new() { Body = goodMessage, ReceiptHandle = "r-good" },
                        new() { Body = "not json", ReceiptHandle = "r-bad" }
                    }
                },
                new ReceiveMessageResponse { Messages = new List<Message>() });
        _sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new DeleteMessageResponse());
        var job = new ReputationUnparkJob(Processor(), _sqs, QueueUrl);

        var summary = await job.RunAsync(new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4)), _context);

        summary["received"].Should().Be("2");
        summary["replayed"].Should().Be("1");
        summary["failed"].Should().Be("1");
        _repo.Row("farmer-1", "REQUEST#order-1").Points.Should().Be(1);
        await _sqs.Received(1).DeleteMessageAsync(Arg.Is<DeleteMessageRequest>(r => r.ReceiptHandle == "r-good"), Arg.Any<CancellationToken>());
        await _sqs.DidNotReceive().DeleteMessageAsync(Arg.Is<DeleteMessageRequest>(r => r.ReceiptHandle == "r-bad"), Arg.Any<CancellationToken>());
        _repo.Committed.Should().ContainSingle();
    }
}
