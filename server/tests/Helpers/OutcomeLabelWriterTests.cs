using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FRD-18 (fraud-detection-architecture.md section 20.1): the label row the three decision paths write. Its
/// key is the case's key time plus the outcome's slot, so a retried decision builds the same row and the
/// conditional put writes it once; a failure is reported, never thrown. The callers are tested with their
/// handlers (<c>AdminFraudActionTests</c>, <c>AdminFunctionTests</c>, <c>EscrowServiceFraudHoldTests</c>).
/// </summary>
public class OutcomeLabelWriterTests
{
    private readonly ITransactionAuditRepository _repo = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _written = new();

    private static readonly AuditContext Admin = new() { Actor = "admin-1", ActorRole = "admin", IpAddress = "10.0.0.1" };

    public OutcomeLabelWriterTests()
    {
        _repo.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(_written.Add)).Returns(true);
    }

    private static OutcomeLabel Label(string outcome = OutcomeLabels.BuyerWins, string keyTime = "2026-11-01T10:00:00.0000000Z",
        string? reason = null) => new()
        {
            Case = OutcomeLabels.CaseDispute,
            CaseId = "escrow-order-ord-1",
            Outcome = outcome,
            Source = "admin_dispute_resolved",
            TransactionId = "ord-1",
            KeyTime = keyTime,
            AdminId = "admin-1",
            LabelledAt = "2026-11-02T08:30:00.0000000Z",
            Reason = reason,
            OrderId = "ord-1",
            EscrowId = "escrow-order-ord-1",
            DeliveryId = "del-1",
            BuyerId = "buyer-1",
            FarmerId = "farmer-1",
            Amount = 8_000m,
            DisputedBy = "buyer-1"
        };

    [Fact]
    public async Task Write_AppendsOneRowIfAbsent_InTheCasePartition_OnTheLabelStream()
    {
        var outcome = await OutcomeLabelWriter.WriteAsync(_repo, Label(reason: "  Photos show spoiled maize.  "), Admin);

        outcome.Should().Be(OutcomeLabelWrite.Written);
        await _repo.DidNotReceiveWithAnyArgs().AppendAsync(default!);
        var row = _written.Single();
        row["transactionId"].Should().Be("ord-1");
        row["timestamp"].Should().Be("2026-11-01T10:00:00.0000004Z"); // buyer_wins is slot 4
        row["entityType"].Should().Be("label");
        row["entityId"].Should().Be("outcome-labels");
        row["action"].Should().Be("outcome_label");
        row["toState"].Should().Be("buyer_wins");
        row["actor"].Should().Be("admin-1");
        row["actorRole"].Should().Be("admin");
        row["metadata"].Should().Be("case=dispute;outcome=buyer_wins");
        row["labelVersion"].Should().Be(1);
        row["labelCase"].Should().Be("dispute");
        row["labelCaseId"].Should().Be("escrow-order-ord-1");
        row["labelOutcome"].Should().Be("buyer_wins");
        row["labelSource"].Should().Be("admin_dispute_resolved");
        row["labelledAt"].Should().Be("2026-11-02T08:30:00.0000000Z");
        row["labelReason"].Should().Be("Photos show spoiled maize.");
        row["escrowId"].Should().Be("escrow-order-ord-1");
        row["deliveryId"].Should().Be("del-1");
        row["buyerId"].Should().Be("buyer-1");
        row["farmerId"].Should().Be("farmer-1");
        row["amount"].Should().Be(8_000m);
        row["disputedBy"].Should().Be("buyer-1");
        row.Should().NotContainKeys("truckerId", "signalIds", "subjectUserId", "holdReason", "whitelistUntil");
    }

    [Fact]
    public async Task TheSameDecisionTwice_BuildsTheSameRow_SoTheConditionalPutKeepsOne()
    {
        _repo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true, false);

        var first = await OutcomeLabelWriter.WriteAsync(_repo, Label(), Admin);
        var second = await OutcomeLabelWriter.WriteAsync(_repo, Label() with { LabelledAt = "2026-11-02T09:00:00.0000000Z" }, Admin);

        first.Should().Be(OutcomeLabelWrite.Written);
        second.Should().Be(OutcomeLabelWrite.AlreadyLabelled);
        _written.Should().HaveCount(2);
        _written[1]["transactionId"].Should().Be(_written[0]["transactionId"]);
        _written[1]["timestamp"].Should().Be(_written[0]["timestamp"]);
        _written[1]["auditId"].Should().Be(_written[0]["auditId"]);
    }

    [Fact]
    public void EachOutcome_HasItsOwnSlot()
    {
        var outcomes = new[]
        {
            OutcomeLabels.FalsePositive, OutcomeLabels.FraudConfirmed, OutcomeLabels.Whitelisted, OutcomeLabels.BuyerWins,
            OutcomeLabels.FarmerWins, OutcomeLabels.HoldReleased, OutcomeLabels.HoldRefunded
        };

        outcomes.Select(o => OutcomeLabelWriter.KeyTimestamp(Label(o))).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("2026-11-01T10:00:00Z", "2026-11-01T10:00:00.0000004Z")]
    [InlineData("2026-11-01T13:00:00+03:00", "2026-11-01T10:00:00.0000004Z")]
    [InlineData("not a time", "2026-11-02T08:30:00.0000004Z")]
    public void TheKey_IsTheKeyTimeInUtc_ElseTheDecisionTime(string keyTime, string expected)
    {
        OutcomeLabelWriter.KeyTimestamp(Label(keyTime: keyTime)).Should().Be(expected);
    }

    [Fact]
    public async Task AWriteThatFails_IsReported_NeverThrown()
    {
        _repo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var outcome = await OutcomeLabelWriter.WriteAsync(_repo, Label(), Admin);

        outcome.Should().Be(OutcomeLabelWrite.Failed);
    }

    [Fact]
    public async Task NoTable_OrNoPartition_WritesNothing()
    {
        (await OutcomeLabelWriter.WriteAsync(null, Label(), Admin)).Should().Be(OutcomeLabelWrite.NotConfigured);
        (await OutcomeLabelWriter.WriteAsync(_repo, Label() with { TransactionId = "" }, Admin))
            .Should().Be(OutcomeLabelWrite.NotConfigured);
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task ABlankReason_IsLeftOut()
    {
        await OutcomeLabelWriter.WriteAsync(_repo, Label(reason: "   "), Admin);

        _written.Single().Should().NotContainKey("labelReason");
    }

    [Theory]
    [InlineData("approve", OutcomeLabels.FalsePositive)]
    [InlineData("reject", OutcomeLabels.FraudConfirmed)]
    [InlineData("whitelist", OutcomeLabels.Whitelisted)]
    [InlineData("Approve", null)]
    public void FraudActions_MapToOutcomes(string action, string? outcome)
    {
        OutcomeLabels.FraudOutcome(action).Should().Be(outcome);
    }

    // FRD-20 P1: the feature snapshot attributes.

    [Fact]
    public void ASnapshot_IsStoredAsCountsCodesAndAges_UnderItsVersion()
    {
        var entry = OutcomeLabelWriter.BuildEntry(Label() with
        {
            Snapshot = new LabelFeatureSnapshot
            {
                SignalUnits = new Dictionary<string, int> { ["PRICE_ANOMALY"] = 3, ["SELF_TRADE"] = 1 },
                CreditUnits = 2,
                CreditPoints = 10,
                Truncated = true,
                WindowDays = 30,
                AccountAgeDays = 12,
                FaydaVerified = false,
                Role = "buyer",
                CompletedEscrows = 0,
                CompletedEscrowsPartial = false
            }
        }, Admin);

        entry["snapshotVersion"].Should().Be(OutcomeLabels.SnapshotVersion);
        entry["snapshotWindowDays"].Should().Be(30);
        entry["snapshotSignalUnits"].Should().BeAssignableTo<IDictionary<string, object>>()
            .Which.Should().BeEquivalentTo(new Dictionary<string, object> { ["PRICE_ANOMALY"] = 3, ["SELF_TRADE"] = 1 });
        entry["snapshotCreditUnits"].Should().Be(2);
        entry["snapshotCreditPoints"].Should().Be(10);
        entry["snapshotTruncated"].Should().Be(true);
        entry["snapshotAccountAgeDays"].Should().Be(12);
        entry["snapshotFaydaVerified"].Should().Be(false);
        entry["snapshotRole"].Should().Be("buyer");
        entry["snapshotCompletedEscrows"].Should().Be(0);
        entry["snapshotCompletedEscrowsPartial"].Should().Be(false);
        entry["labelVersion"].Should().Be(OutcomeLabels.SchemaVersion);

        // The map reaches DynamoDB as a map of numbers.
        var stored = WeredaConnect.Server.Data.DynamoDbHelper.ToAttributeValue(entry["snapshotSignalUnits"]);
        stored.M["PRICE_ANOMALY"].N.Should().Be("3");
    }

    [Fact]
    public void ASnapshotWithoutControls_LeavesThemOut_AndALabelWithoutASnapshotHasNoSnapshotField()
    {
        var entry = OutcomeLabelWriter.BuildEntry(Label() with
        {
            Snapshot = new LabelFeatureSnapshot { SignalUnits = new Dictionary<string, int>(), WindowDays = 30 }
        }, Admin);

        entry.Should().ContainKeys("snapshotVersion", "snapshotSignalUnits", "snapshotCreditUnits", "snapshotTruncated");
        entry.Should().NotContainKeys("snapshotAccountAgeDays", "snapshotFaydaVerified", "snapshotRole",
            "snapshotCompletedEscrows", "snapshotCompletedEscrowsPartial");

        OutcomeLabelWriter.BuildEntry(Label(), Admin).Keys
            .Should().NotContain(k => k.StartsWith("snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void ASignalTypeThatIsNotAPlainCode_IsCountedAsOther_SoNoTextReachesTheRow()
    {
        var entry = OutcomeLabelWriter.BuildEntry(Label() with
        {
            Snapshot = new LabelFeatureSnapshot
            {
                SignalUnits = new Dictionary<string, int>
                {
                    ["PRICE_ANOMALY"] = 1,
                    ["+251911000001"] = 2,
                    ["call Abebe"] = 1,
                    [""] = 1
                },
                WindowDays = 30
            }
        }, Admin);

        entry["snapshotSignalUnits"].Should().BeAssignableTo<IDictionary<string, object>>()
            .Which.Should().BeEquivalentTo(new Dictionary<string, object> { ["PRICE_ANOMALY"] = 1, ["OTHER"] = 4 });
    }
}
