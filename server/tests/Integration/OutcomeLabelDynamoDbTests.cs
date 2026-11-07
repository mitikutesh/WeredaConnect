using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FRD-18 against real DynamoDB (LocalStack in CI): a retried decision writes one label row (the conditional
/// put at the case's key), the label shows in its transaction's partition (the admin timeline), and every
/// label is on the one <c>EntityIndex</c> partition <c>scripts/export_outcome_labels.py</c> Queries.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutcomeLabelDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N");

    public OutcomeLabelDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private TransactionAuditRepository Repo => new(Ddb, _db.TransactionAudit);

    private static readonly AuditContext Admin = new() { Actor = "admin-1", ActorRole = "admin" };

    private OutcomeLabel Verdict(string labelledAt) => new()
    {
        Case = OutcomeLabels.CaseDispute,
        CaseId = $"escrow-order-ord-{_run}",
        Outcome = OutcomeLabels.BuyerWins,
        Source = "admin_dispute_resolved",
        TransactionId = $"ord-{_run}",
        KeyTime = "2026-11-01T10:00:00.0000000Z",
        AdminId = "admin-1",
        LabelledAt = labelledAt,
        OrderId = $"ord-{_run}",
        EscrowId = $"escrow-order-ord-{_run}",
        BuyerId = $"buyer-{_run}",
        FarmerId = $"farmer-{_run}",
        Amount = 8_000m
    };

    private OutcomeLabel FraudDecision() => new()
    {
        Case = OutcomeLabels.CaseFraudSignal,
        CaseId = $"signal-{_run}",
        Outcome = OutcomeLabels.FraudConfirmed,
        Source = "fraud_reject",
        TransactionId = $"signal-{_run}",
        KeyTime = "2026-11-01T09:00:00.0000000Z",
        AdminId = "admin-1",
        LabelledAt = "2026-11-02T08:00:00.0000000Z",
        SubjectUserId = $"farmer-{_run}",
        SignalIds = new[] { $"signal-{_run}" },
        SignalType = "SELF_TRADE"
    };

    [LocalDynamoDbFact]
    public async Task ARetriedDecision_WritesOneRow_InItsTransactionPartition()
    {
        var first = await OutcomeLabelWriter.WriteAsync(Repo, Verdict("2026-11-01T10:00:00.0000000Z"), Admin);
        var retry = await OutcomeLabelWriter.WriteAsync(Repo, Verdict("2026-11-01T10:00:05.0000000Z"), Admin);

        first.Should().Be(OutcomeLabelWrite.Written);
        retry.Should().Be(OutcomeLabelWrite.AlreadyLabelled);
        var timeline = await Repo.GetByTransactionAsync($"ord-{_run}");
        var label = timeline.Should().ContainSingle().Subject;
        label["action"].Should().Be("outcome_label");
        label["labelOutcome"].Should().Be("buyer_wins");
        label["labelledAt"].Should().Be("2026-11-01T10:00:00.0000000Z");
    }

    [LocalDynamoDbFact]
    public async Task EveryLabel_IsOnTheExportPartition_WithItsLinks()
    {
        await OutcomeLabelWriter.WriteAsync(Repo, Verdict("2026-11-01T10:00:00.0000000Z"), Admin);
        await OutcomeLabelWriter.WriteAsync(Repo, FraudDecision(), Admin);

        // The export's read: one Query of the stream partition, paged.
        var labels = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var page = await Ddb.QueryAsync(new QueryRequest
            {
                TableName = _db.TransactionAudit,
                IndexName = "EntityIndex",
                KeyConditionExpression = "entityId = :stream",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":stream"] = new() { S = OutcomeLabels.StreamEntityId }
                },
                ExclusiveStartKey = start
            });
            labels.AddRange(page.Items);
            start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (start != null);

        var mine = labels.Where(l => l["labelCaseId"].S.EndsWith(_run, StringComparison.Ordinal)).ToList();
        mine.Select(l => l["labelOutcome"].S).Should().BeEquivalentTo("buyer_wins", "fraud_confirmed");
        var fraud = mine.Single(l => l["labelCase"].S == "fraud_signal");
        fraud["subjectUserId"].S.Should().Be($"farmer-{_run}");
        fraud["signalIds"].L.Select(v => v.S).Should().Equal($"signal-{_run}");
        var verdict = mine.Single(l => l["labelCase"].S == "dispute");
        verdict["amount"].N.Should().Be("8000");
        verdict["buyerId"].S.Should().Be($"buyer-{_run}");
    }
}
