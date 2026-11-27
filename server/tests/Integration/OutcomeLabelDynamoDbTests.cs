using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FRD-18 against real DynamoDB (LocalStack in CI): a retried decision writes one label row (the conditional
/// put at the case's key), the label shows in its transaction's partition (the admin timeline), every label
/// is on the one <c>EntityIndex</c> partition <c>scripts/export_outcome_labels.py</c> Queries, and the Fraud
/// Review route labels each change of decision once.
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

    [LocalDynamoDbFact]
    public async Task FraudReview_LabelsEachChangeOfDecisionOnce()
    {
        var user = $"farmer-frd18-{_run}";
        var signals = new FraudDetectionService(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>());
        await signals.RecordFraudSignalAsync(user, "GPS_ANOMALY", $"del-{_run}", "delivery", "gps");
        var signalKey = (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = user } }
        })).Items.Single()["signalKey"].S;
        var signalId = FraudDetectionService.BuildSignalId(user, signalKey);
        var admin = new AdminFunction(Ddb, _db.Users, _db.FraudSignals, cognitoAuth: null, escrowsTable: _db.Escrows,
            transactionAuditTable: _db.TransactionAudit);

        async Task ActAsync(string action)
        {
            var response = await admin.FunctionHandler(new APIGatewayProxyRequest
            {
                HttpMethod = "POST",
                Path = $"/admin/fraud-signals/{signalId}/action",
                Body = $"{{\"action\":\"{action}\"}}",
                RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
                }
            }, new TestLambdaContext());
            response.StatusCode.Should().Be(200, response.Body);
        }

        await ActAsync("approve");
        await ActAsync("approve"); // the same decision again
        await ActAsync("whitelist"); // a change of mind

        var labels = (await Repo.GetByTransactionAsync(signalId))
            .Where(r => (string)r["action"] == "outcome_label")
            .ToList();
        labels.Select(r => r["labelOutcome"]).Should().Equal("false_positive", "whitelisted");

        // FRD-20 P1 (FUP-144): each label carries its snapshot, read before its decision. The first sees the
        // pending signal; the second sees none counted (the approve cleared it), an empty map DynamoDB keeps.
        var rows = (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.TransactionAudit,
            KeyConditionExpression = "transactionId = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { S = signalId } }
        })).Items.Where(r => r["action"].S == "outcome_label").ToList();
        rows.Should().HaveCount(2).And.OnlyContain(r => r["snapshotVersion"].N == "1" && r["snapshotTruncated"].BOOL == false);
        rows[0]["snapshotSignalUnits"].M.Should().ContainSingle().Which.Key.Should().Be("GPS_ANOMALY");
        rows[0]["snapshotSignalUnits"].M["GPS_ANOMALY"].N.Should().Be("1");
        rows[1]["snapshotSignalUnits"].M.Should().BeEmpty();
        labels.Should().OnlyContain(r => (string)r["subjectUserId"] == user && (string)r["deliveryId"] == $"del-{_run}"
                                         && (string)r["signalType"] == "GPS_ANOMALY");
    }
}
