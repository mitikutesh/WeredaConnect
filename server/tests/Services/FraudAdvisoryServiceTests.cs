using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-19 (fraud-detection-architecture.md section 21): the hourly AI advisory run with a fake DynamoDB, a fake
/// score and a fake reviewer (no Bedrock, no AWS). The kill switch, the candidate read on the status index, the
/// HIGH gate, one review per user and day, the per-run cap, failures, and the one row it writes.
/// </summary>
public class FraudAdvisoryServiceTests
{
    private const string FraudTable = "fraud-signals-test";
    private const string UsersTable = "users-test";
    private const string AuditTable = "transaction-audit-test";
    private static readonly DateTime Now = new(2026, 11, 7, 14, 30, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly FakeReviewer _reviewer = new();
    private readonly List<string> _log = new();
    private readonly TestLambdaContext _context = new();

    /// <summary>The users of the candidate rows the status index answers with, newest first.</summary>
    private readonly List<(string UserId, string Type)> _candidateRows = new();

    /// <summary>Users who already have today's advisory row.</summary>
    private readonly HashSet<string> _reviewedToday = new();

    /// <summary>Users whose put meets an existing row (a run that raced this one).</summary>
    private readonly HashSet<string> _putRaces = new();

    public FraudAdvisoryServiceTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var query = ci.ArgAt<QueryRequest>(0);
            if (query.IndexName == FraudAdvisoryService.StatusIndex)
            {
                return Task.FromResult(Page(_candidateRows.Select(c => new Dictionary<string, AttributeValue>
                {
                    { "userId", new AttributeValue { S = c.UserId } },
                    { "signalType", new AttributeValue { S = c.Type } }
                })));
            }

            if (query.TableName == FraudTable)
            {
                var userId = query.ExpressionAttributeValues[":uid"].S;
                return Task.FromResult(Page(new[]
                {
                    SignalRow(userId, "IDOR_CREATE_CONFLICT", Now.AddHours(-1), "order", $"ord-of-{userId}"),
                    SignalRow(userId, "ROLE_FORBIDDEN", Now.AddHours(-2), "delivery", $"del-of-{userId}")
                }));
            }

            return Task.FromResult(Page(new[]
            {
                new Dictionary<string, AttributeValue>
                {
                    { "timestamp", new AttributeValue { S = Now.AddHours(-3).ToString("o", CultureInfo.InvariantCulture) } },
                    { "action", new AttributeValue { S = "order_created" } },
                    { "actor", new AttributeValue { S = "someone" } },
                    { "entityType", new AttributeValue { S = "order" } }
                }
            }));
        });
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var get = ci.ArgAt<GetItemRequest>(0);
            if (get.TableName == UsersTable)
            {
                return Task.FromResult(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "role", new AttributeValue { S = "buyer" } },
                        { "createdAt", new AttributeValue { S = Now.AddDays(-2).ToString("o", CultureInfo.InvariantCulture) } },
                        { "faydaVerified", new AttributeValue { BOOL = false } }
                    }
                });
            }

            var userId = get.Key["userId"].S;
            return Task.FromResult(new GetItemResponse
            {
                Item = _reviewedToday.Contains(userId)
                    ? new Dictionary<string, AttributeValue> { { "signalKey", get.Key["signalKey"] } }
                    : new Dictionary<string, AttributeValue>()
            });
        });
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _putRaces.Contains(ci.ArgAt<PutItemRequest>(0).Item["userId"].S)
                ? Task.FromException<PutItemResponse>(new ConditionalCheckFailedException("exists"))
                : Task.FromResult(new PutItemResponse()));
    }

    private static QueryResponse Page(IEnumerable<Dictionary<string, AttributeValue>> rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = new Dictionary<string, AttributeValue>() };

    private static Dictionary<string, AttributeValue> SignalRow(
        string userId, string type, DateTime at, string entityType, string entityId) => new()
    {
        { "userId", new AttributeValue { S = userId } },
        { "signalKey", new AttributeValue { S = $"{at.ToString("o", CultureInfo.InvariantCulture)}#{type}" } },
        { "signalType", new AttributeValue { S = type } },
        { "status", new AttributeValue { S = "pending" } },
        { "entityType", new AttributeValue { S = entityType } },
        { "entityId", new AttributeValue { S = entityId } },
        { "createdAt", new AttributeValue { S = at.ToString("o", CultureInfo.InvariantCulture) } }
    };

    private void ScoreOf(string userId, int score, int enforcement, RiskTier tier,
        RiskScoreSource source = RiskScoreSource.Computed) =>
        _fraud.GetUserRiskScoreAsync(userId).Returns(new RiskScoreResult
        {
            Score = score,
            EnforcementScore = enforcement,
            Tier = tier,
            Source = source
        });

    private void Candidates(params string[] userIds) =>
        _candidateRows.AddRange(userIds.Select(id => (id, "IDOR_CREATE_CONFLICT")));

    private FraudAdvisoryService Service(bool enabled = true, int maxPerRun = 5, IAdvisoryReviewer? reviewer = null,
        bool withReviewer = true) =>
        new(_ddb, FraudTable, UsersTable, AuditTable, _fraud, withReviewer ? reviewer ?? _reviewer : null,
            new FraudAdvisorySettings(enabled, "wereda-connect-dev-fraud-ai-reviewer", maxPerRun, 2),
            RiskScoreSettings.Default)
        {
            Log = _log.Add
        };

    private static CronRunSlice Slice() =>
        new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(50));

    private Task<(string Outcome, FraudAdvisorySummary Summary)> RunAsync(FraudAdvisoryService service) =>
        service.RunAsync(Now, Slice(), _context);

    private List<PutItemRequest> Puts => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<PutItemRequest>().ToList();

    private List<QueryRequest> Queries => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<QueryRequest>().ToList();

    // ── kill switch ────────────────────────────────────────

    [Fact]
    public async Task KillSwitchOff_ReadsNothing_SendsNothing_WritesNothing()
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 80, 80, RiskTier.High);

        var (outcome, summary) = await RunAsync(Service(enabled: false));

        outcome.Should().Be("off");
        _ddb.ReceivedCalls().Should().BeEmpty();
        await _fraud.DidNotReceiveWithAnyArgs().GetUserRiskScoreAsync(default!);
        _reviewer.Requests.Should().BeEmpty();
        summary.Reviewed.Should().Be(0);
        _log.Should().ContainSingle().Which.Should().StartWith("FRAUD_AI_REVIEW_OFF");
    }

    [Fact]
    public async Task NoReviewer_IsUnconfigured_AndReadsNothing()
    {
        Candidates("buyer-1");

        var (outcome, _) = await RunAsync(Service(withReviewer: false));

        outcome.Should().Be("unconfigured");
        _ddb.ReceivedCalls().Should().BeEmpty();
        _log.Should().ContainSingle().Which.Should().StartWith("FRAUD_AI_REVIEW_UNCONFIGURED");
    }

    // ── candidates ─────────────────────────────────────────

    [Fact]
    public async Task Candidates_AreReadOnTheStatusIndex_KeyBounded_NewestFirst_WithoutScan()
    {
        var (_, _) = await RunAsync(Service());

        var query = Queries.Should().ContainSingle(q => q.IndexName == FraudAdvisoryService.StatusIndex).Subject;
        query.TableName.Should().Be(FraudTable);
        query.KeyConditionExpression.Should().Be("#s = :pending AND #c >= :since");
        query.ExpressionAttributeValues[":pending"].S.Should().Be("pending");
        query.ExpressionAttributeValues[":since"].S.Should().Be("2026-11-07T12:30:00.0000000Z");
        query.ScanIndexForward.Should().BeFalse();
        _ddb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<ScanRequest>().Should().BeEmpty();
    }

    [Fact]
    public async Task Candidates_LeaveOutAdvisoryRows_AndEachUserIsScoredOnce()
    {
        _candidateRows.Add(("buyer-1", "IDOR_CREATE_CONFLICT"));
        _candidateRows.Add(("buyer-2", FraudDetectionService.AdvisorySignalType));
        _candidateRows.Add(("buyer-1", "ROLE_FORBIDDEN"));
        ScoreOf("buyer-1", 20, 20, RiskTier.Low);

        var (_, summary) = await RunAsync(Service());

        summary.Candidates.Should().Be(1);
        await _fraud.Received(1).GetUserRiskScoreAsync("buyer-1");
        await _fraud.DidNotReceive().GetUserRiskScoreAsync("buyer-2");
    }

    // ── the HIGH gate ──────────────────────────────────────

    [Fact]
    public async Task BelowHigh_OrHighOnlyThroughEarlierAdvice_OrUnscored_IsNotReviewed()
    {
        Candidates("medium-1", "advice-only-1", "unscored-1");
        ScoreOf("medium-1", 45, 45, RiskTier.Medium);
        ScoreOf("advice-only-1", 65, 55, RiskTier.High);
        ScoreOf("unscored-1", 0, 0, RiskTier.Low, RiskScoreSource.Unavailable);

        var (outcome, summary) = await RunAsync(Service());

        outcome.Should().Be("ok");
        _reviewer.Requests.Should().BeEmpty();
        Puts.Should().BeEmpty();
        summary.BelowHigh.Should().Be(2);
        summary.Unscored.Should().Be(1);
    }

    // ── the review and the row ─────────────────────────────

    [Fact]
    public async Task HighUser_IsReviewedOnce_WithTheRedactedTimeline_AndOneCappedRowIsWritten()
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        _reviewer.Reply = new AdvisoryReviewReply
        {
            Ok = true,
            Concern = "high",
            Explanation = "Probes other users' orders.\nThen pays just under the KYC line.",
            Model = "eu.anthropic.claude-opus-5-5",
            InputTokens = 2100,
            OutputTokens = 300
        };

        var (outcome, summary) = await RunAsync(Service());

        outcome.Should().Be("ok");
        summary.Reviewed.Should().Be(1);
        summary.Written.Should().Be(1);

        var request = _reviewer.Requests.Should().ContainSingle().Subject;
        var sent = System.Text.Json.JsonSerializer.Serialize(request, AppJsonContext.Default.AdvisoryReviewRequest);
        sent.Should().NotContain("buyer-1").And.NotContain("ord-of-").And.NotContain("del-of-").And.NotContain("someone");
        request.Subject.Role.Should().Be("buyer");
        request.Signals.Select(s => s.Entity).Should().Equal("order-1", "delivery-1");
        request.Events.Should().HaveCount(2).And.OnlyContain(e => e.Actor == "other");

        var put = Puts.Should().ContainSingle().Subject;
        put.TableName.Should().Be(FraudTable);
        put.ConditionExpression.Should().Be("attribute_not_exists(signalKey)");
        var item = put.Item;
        item["userId"].S.Should().Be("buyer-1");
        item["signalKey"].S.Should().Be("2026-11-07T00:00:00.0000000Z#AI_ADVISORY");
        item["signalId"].S.Should().Be(FraudDetectionService.BuildSignalId("buyer-1", item["signalKey"].S));
        item["signalType"].S.Should().Be("AI_ADVISORY");
        item["recordType"].S.Should().Be("signal");
        item["status"].S.Should().Be("pending");
        item["weight"].N.Should().Be("10");
        item["severity"].S.Should().Be("medium");
        item["aiConcern"].S.Should().Be("high");
        item["aiModel"].S.Should().Be("eu.anthropic.claude-opus-5-5");
        item["description"].S.Should().Be("Probes other users' orders. Then pays just under the KYC line.");
        item["entityType"].S.Should().Be("user");
        item.Should().NotContainKey("entityId");
        item["createdAt"].S.Should().Be("2026-11-07T14:30:00.0000000Z");
        item["expiresAt"].N.Should().Be(new DateTimeOffset(Now).AddDays(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        _log.Should().Contain("{\"event\":\"FRAUD_SIGNAL_RECORDED\",\"signalType\":\"AI_ADVISORY\",\"entityType\":\"user\"}");
        _log.Should().Contain(l => l.StartsWith("FRAUD_AI_REVIEW_WRITTEN userId=buyer-1 concern=high weight=10"));
        _log.Last().Should().StartWith("FRAUD_AI_REVIEW_RUN outcome=ok candidates=1");
    }

    [Fact]
    public async Task OnlyFraudSignalRowsAreWritten_NeverAnUpdateOrDelete_NeverAnotherTable()
    {
        Candidates("buyer-1", "farmer-2");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        ScoreOf("farmer-2", 130, 130, RiskTier.Critical);

        await RunAsync(Service());

        var writes = _ddb.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => !name.StartsWith("Query") && !name.StartsWith("GetItem")).ToList();
        writes.Should().OnlyContain(name => name == "PutItemAsync");
        Puts.Should().HaveCount(2).And.OnlyContain(p => p.TableName == FraudTable);
    }

    [Fact]
    public async Task TheCriticalTierIsSentAsCritical()
    {
        Candidates("farmer-2");
        ScoreOf("farmer-2", 130, 130, RiskTier.Critical);

        await RunAsync(Service());

        _reviewer.Requests.Should().ContainSingle().Which.Subject.RiskTier.Should().Be("critical");
    }

    [Fact]
    public async Task AUserReviewedToday_IsNotSentAgain()
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        _reviewedToday.Add("buyer-1");

        var (_, summary) = await RunAsync(Service());

        summary.AlreadyReviewed.Should().Be(1);
        _reviewer.Requests.Should().BeEmpty();
        Puts.Should().BeEmpty();
        var get = _ddb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<GetItemRequest>()
            .Should().ContainSingle(g => g.TableName == FraudTable).Subject;
        get.ConsistentRead.Should().BeTrue();
        get.Key["signalKey"].S.Should().Be(FraudAdvisory.DayKey(Now));
    }

    [Fact]
    public async Task ARunThatRacedThisOne_LeavesItsRow_AndNothingMoreIsWritten()
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        _putRaces.Add("buyer-1");

        var (outcome, summary) = await RunAsync(Service());

        outcome.Should().Be("ok");
        summary.Written.Should().Be(0);
        summary.AlreadyReviewed.Should().Be(1);
        _log.Should().NotContain(l => l.Contains("FRAUD_SIGNAL_RECORDED") || l.StartsWith("FRAUD_AI_REVIEW_WRITTEN"));
    }

    [Fact]
    public async Task TheRunStopsAtThePerRunCap()
    {
        Candidates("u-1", "u-2", "u-3");
        foreach (var id in new[] { "u-1", "u-2", "u-3" })
        {
            ScoreOf(id, 70, 70, RiskTier.High);
        }

        var (_, summary) = await RunAsync(Service(maxPerRun: 2));

        _reviewer.Requests.Should().HaveCount(2);
        summary.Capped.Should().BeTrue();
        await _fraud.DidNotReceive().GetUserRiskScoreAsync("u-3");
    }

    [Fact]
    public async Task TheRunStopsWhenItsTimeIsUsedUp()
    {
        Candidates("u-1", "u-2");
        ScoreOf("u-1", 70, 70, RiskTier.High);
        ScoreOf("u-2", 70, 70, RiskTier.High);
        var elapsed = TimeSpan.Zero;
        var slice = new CronRunBudget(() => elapsed).Slice(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(50));
        _reviewer.OnReview = () => elapsed = TimeSpan.FromMinutes(3);

        var (_, summary) = await Service().RunAsync(Now, slice, _context);

        _reviewer.Requests.Should().ContainSingle();
        summary.StopReason.Should().Be("time");
    }

    // ── failures ───────────────────────────────────────────

    [Theory]
    [InlineData("unparsable")]
    [InlineData("refused")]
    [InlineData("disabled")]
    public async Task ARefusedAnswer_WritesNothing_AndIsLogged(string error)
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        _reviewer.Reply = new AdvisoryReviewReply { Ok = false, Error = error };

        var (outcome, summary) = await RunAsync(Service());

        outcome.Should().Be("partial");
        summary.Failed.Should().Be(1);
        Puts.Should().BeEmpty();
        _log.Should().Contain($"FRAUD_AI_REVIEW_FAILED userId=buyer-1 reason={error}");
    }

    [Fact]
    public async Task AnAnswerWithAConcernOutsideTheScale_WritesNothing()
    {
        Candidates("buyer-1");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        _reviewer.Reply = new AdvisoryReviewReply { Ok = true, Concern = "certain_fraud", Explanation = "Hold the money." };

        var (_, summary) = await RunAsync(Service());

        summary.Failed.Should().Be(1);
        Puts.Should().BeEmpty();
        _log.Should().Contain("FRAUD_AI_REVIEW_FAILED userId=buyer-1 reason=bad_concern");
    }

    [Fact]
    public async Task AFailedInvocation_IsCounted_AndTheNextUserIsStillReviewed()
    {
        Candidates("buyer-1", "buyer-2");
        ScoreOf("buyer-1", 75, 75, RiskTier.High);
        ScoreOf("buyer-2", 75, 75, RiskTier.High);
        var calls = 0;
        _reviewer.OnReview = () =>
        {
            if (calls++ == 0)
            {
                throw new AdvisoryReviewerException("function_error:Unhandled");
            }
        };

        var (outcome, summary) = await RunAsync(Service());

        outcome.Should().Be("partial");
        summary.Failed.Should().Be(1);
        summary.Written.Should().Be(1);
        Puts.Should().ContainSingle().Which.Item["userId"].S.Should().Be("buyer-2");
        _log.Should().Contain("FRAUD_AI_REVIEW_FAILED userId=buyer-1 reason=function_error:Unhandled");
    }

    [Fact]
    public async Task AFailedScoreRead_IsUnscored_NeverHigh()
    {
        Candidates("buyer-1");
        _fraud.GetUserRiskScoreAsync("buyer-1").ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var (_, summary) = await RunAsync(Service());

        summary.Unscored.Should().Be(1);
        _reviewer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCandidateRead_FailsTheRun()
    {
        _ddb.QueryAsync(Arg.Is<QueryRequest>(q => q.IndexName == FraudAdvisoryService.StatusIndex), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var act = () => RunAsync(Service());

        await act.Should().ThrowAsync<ProvisionedThroughputExceededException>();
    }

    // ── the cron handler ───────────────────────────────────

    [Fact]
    public async Task Handler_WithoutTheTable_AnswersUnconfigured()
    {
        var response = await new FraudAdvisoryCronFunction(null, () => Now)
            .FunctionHandler(new Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
    }

    [Fact]
    public async Task Handler_KillSwitchOff_AnswersOff()
    {
        var response = await new FraudAdvisoryCronFunction(Service(enabled: false), () => Now)
            .FunctionHandler(new Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"off\"");
    }

    [Fact]
    public async Task Handler_AFailedRun_Answers500()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var response = await new FraudAdvisoryCronFunction(Service(), () => Now)
            .FunctionHandler(new Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest(), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("FRAUD_AI_REVIEW_ERROR");
    }

    // ── the Lambda reviewer client ─────────────────────────

    [Fact]
    public async Task LambdaReviewer_InvokesSynchronously_AndReadsTheAnswer()
    {
        var lambda = Substitute.For<IAmazonLambda>();
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(new InvokeResponse
        {
            StatusCode = 200,
            Payload = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"ok\":true,\"concern\":\"low\",\"explanation\":\"One soft signal.\",\"model\":\"m\",\"inputTokens\":10}"))
        });

        var reply = await new LambdaAdvisoryReviewer(lambda, "fn").ReviewAsync(Request());

        reply.Should().BeEquivalentTo(new AdvisoryReviewReply
        {
            Ok = true,
            Concern = "low",
            Explanation = "One soft signal.",
            Model = "m",
            InputTokens = 10
        });
        var invoke = (InvokeRequest)lambda.ReceivedCalls().Single().GetArguments()[0]!;
        invoke.FunctionName.Should().Be("fn");
        invoke.InvocationType.Should().Be(InvocationType.RequestResponse);
        invoke.Payload.Should().Contain("\"subject\":{\"role\":\"buyer\"");
    }

    [Fact]
    public async Task LambdaReviewer_AFunctionError_Throws()
    {
        var lambda = Substitute.For<IAmazonLambda>();
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(new InvokeResponse
        {
            StatusCode = 200,
            FunctionError = "Unhandled",
            Payload = new MemoryStream(Encoding.UTF8.GetBytes("{\"errorMessage\":\"boom\"}"))
        });

        var act = () => new LambdaAdvisoryReviewer(lambda, "fn").ReviewAsync(Request());

        (await act.Should().ThrowAsync<AdvisoryReviewerException>()).Which.Reason.Should().Be("function_error:Unhandled");
    }

    [Fact]
    public void LambdaReviewer_TheClientNeverRetries()
    {
        using var client = (AmazonLambdaClient)LambdaAdvisoryReviewer.CreateClient();

        client.Config.MaxErrorRetry.Should().Be(0);
        client.Config.Timeout.Should().Be(TimeSpan.FromSeconds(40));
    }

    private static AdvisoryReviewRequest Request() => new()
    {
        Subject = new AdvisorySubject { Role = "buyer", RiskTier = "high", Score = 70, EnforcementScore = 70 },
        Signals = new List<AdvisorySignal>(),
        Events = new List<AdvisoryEvent>(),
        WindowDays = 30
    };

    private sealed class FakeReviewer : IAdvisoryReviewer
    {
        public List<AdvisoryReviewRequest> Requests { get; } = new();

        public AdvisoryReviewReply? Reply { get; set; } = new()
        {
            Ok = true,
            Concern = "medium",
            Explanation = "Several refused operations.",
            Model = "test-model"
        };

        public Action? OnReview { get; set; }

        public Task<AdvisoryReviewReply?> ReviewAsync(AdvisoryReviewRequest request)
        {
            Requests.Add(request);
            OnReview?.Invoke();
            return Task.FromResult(Reply);
        }
    }
}
