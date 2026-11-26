using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Helpers;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// GCR-03: the reputation stream writes the conduct stamp of a settled order whatever <c>reputationPenalties</c> says,
/// once per settle record, and a stamp that fails never fails the record or the penalty derivation after it.
/// </summary>
public class ReputationStreamConductTests
{
    private readonly FakeConductStamps _stamps = new();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly TestLambdaContext _context = new();

    public ReputationStreamConductTests()
    {
        _deliveries.GetByIdAsync("del-1", true).Returns(ConductScoringTests.Delivery());
    }

    private ConductStampService Conduct()
        => new(_stamps, _deliveries, null, null, null, Substitute.For<IFraudDetectionService>(),
            new ManualClock(new DateTime(2026, 11, 20, 9, 5, 0, DateTimeKind.Utc)), _ => Task.CompletedTask);

    private static Dictionary<string, object> Order(string status) => new()
    {
        ["orderId"] = "order-1",
        ["farmerId"] = "farmer-1",
        ["buyerId"] = "buyer-1",
        ["truckerId"] = "trucker-1",
        ["tripId"] = "trip-1",
        ["fulfillment"] = "trucker",
        ["truckerResponse"] = "accepted",
        ["activeDeliveryId"] = "del-1",
        ["orderTotal"] = 1200m,
        ["status"] = status,
        ["updatedAt"] = "2026-11-20T09:00:00.000Z"
    };

    private static string Settle(string sequence = "100") => OrderRecords.Modify(Order("disbursing"), Order("settled"), sequence);

    private static Stream Input(params string[] records)
        => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OrderRecords.Batch(records)));

    [Fact]
    public async Task PenaltiesOff_ASettleRecord_StampsEveryParty()
    {
        var fn = new ReputationStreamFunction(null, null, null, penaltiesEnabled: false, conduct: Conduct());

        var response = await fn.ProcessBatchAsync(Input(Settle()), _context);

        response.BatchItemFailures.Should().BeEmpty();
        _stamps.Rows.Keys.Select(k => k.UserId).Should().BeEquivalentTo("farmer-1", "trucker-1", "buyer-1");
        _stamps.Rows.Values.Should().OnlyContain(r => r.OccurredAt == new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task OnlyTheSettleItself_IsStamped_AndAReplayWritesNothingMore()
    {
        var fn = new ReputationStreamFunction(null, null, null, penaltiesEnabled: false, conduct: Conduct());
        var stillSettled = OrderRecords.Modify(Order("settled"), Order("settled"), "101");
        var delivered = OrderRecords.Modify(Order("delivered"), Order("disbursing"), "102");

        await fn.ProcessBatchAsync(Input(stillSettled, delivered), _context);
        _stamps.Rows.Should().BeEmpty();

        await fn.ProcessBatchAsync(Input(Settle("103")), _context);
        await fn.ProcessBatchAsync(Input(Settle("103")), _context);
        _stamps.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task AFailingStamp_FailsNoRecord_AndThePenaltyDerivationStillRuns()
    {
        _stamps.ThrowOnPut = new InvalidOperationException("throttled");
        var repo = new FakeReputationRepository();
        repo.AddUser("farmer-1");
        repo.AddUser("trucker-1", "trucker");
        var processor = new ReputationRecordProcessor(
            new ReputationService(repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(),
                new ManualClock(new DateTime(2026, 11, 20, 9, 5, 0, DateTimeKind.Utc)), _ => Task.CompletedTask),
            _ => Task.FromResult(true), timeoutPenaltiesEnabled: false);
        var fn = new ReputationStreamFunction(processor, null, null, penaltiesEnabled: true, conduct: Conduct());

        var response = await fn.ProcessBatchAsync(Input(Settle()), _context);

        response.BatchItemFailures.Should().BeEmpty();
        _stamps.Rows.Should().BeEmpty();
        repo.RowsOf("farmer-1").Should().ContainSingle(r => r.Code == ReputationCodes.OrderCompleted);
        repo.RowsOf("trucker-1").Should().ContainSingle(r => r.Code == ReputationCodes.OrderCompleted);
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(ConductStampService.FailedToken);
    }

    [Fact]
    public async Task SettledOrder_ReadsTheNewImage_AndSkipsEveryOtherRecord()
    {
        using var settle = System.Text.Json.JsonDocument.Parse(Settle());
        var order = ReputationStreamFunction.SettledOrder(settle.RootElement)!;
        order.OrderId.Should().Be("order-1");
        order.TruckerId.Should().Be("trucker-1");
        order.OrderTotal.Should().Be(1200m);
        order.DeliveryId.Should().Be("del-1");

        using var insert = System.Text.Json.JsonDocument.Parse(Settle().Replace("\"MODIFY\"", "\"INSERT\""));
        ReputationStreamFunction.SettledOrder(insert.RootElement).Should().BeNull();
    }
}
