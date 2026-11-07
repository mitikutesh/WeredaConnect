using Amazon.DynamoDBv2;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FRD-19 (fraud-detection-architecture.md section 21): the hourly AI advisory run, an EventBridge rule's target.
/// It picks the users the rules have just put at HIGH or CRITICAL, sends each one's redacted audit timeline to the
/// separate non-AOT reviewer Lambda (the only code that calls Bedrock) and writes the answer as one capped
/// <c>AI_ADVISORY</c> signal row. Its role reads the fraud-signals table and its status index, reads users rows and
/// the transaction audit, puts fraud-signal rows and invokes the reviewer: it holds no grant on orders, escrows or
/// any money table, so it cannot move money whatever the model says.
/// <para>
/// Kill switch: <c>FRAUD_AI_REVIEW_ENABLED</c> (CDK, off in every environment by default). Off, a run logs
/// <c>FRAUD_AI_REVIEW_OFF</c> and reads nothing. The reviewer has its own switch, so turning either off stops every
/// Bedrock call.
/// </para>
/// Responses: 200 with the run summary (outcome <c>off</c>, <c>unconfigured</c>, <c>ok</c> or <c>partial</c>);
/// 500 <c>FRAUD_AI_REVIEW_ERROR</c> when the candidate read failed.
/// </summary>
public class FraudAdvisoryCronFunction
{
    /// <summary>The run's time budget: reviews start only in the first two minutes of the three-minute Lambda.</summary>
    internal static readonly TimeSpan ReviewTime = TimeSpan.FromMinutes(2);

    /// <summary>Time kept for one more reviewer call (40 seconds at most) and the summary.</summary>
    internal static readonly TimeSpan ReviewReserve = TimeSpan.FromSeconds(50);

    private readonly FraudAdvisoryService? _service;
    private readonly Func<DateTime> _clock;

    /// <summary>Lambda runtime: built from the environment; without <c>FRAUD_SIGNALS_TABLE</c> the run is unconfigured.</summary>
    public FraudAdvisoryCronFunction()
    {
        _clock = () => DateTime.UtcNow;
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE");
        if (string.IsNullOrEmpty(fraudSignalsTable))
        {
            return;
        }

        var dynamoDb = new AmazonDynamoDBClient();
        var reviewerFunction = Environment.GetEnvironmentVariable("FRAUD_AI_REVIEWER_FUNCTION");
        _service = new FraudAdvisoryService(
            dynamoDb,
            fraudSignalsTable,
            Environment.GetEnvironmentVariable("USERS_TABLE"),
            Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE"),
            // The score never reads the crops or rate-limit tables (the cron construction of FRD-04).
            new FraudDetectionService(dynamoDb, "", fraudSignalsTable, new RateLimitRepository(dynamoDb, "")),
            string.IsNullOrEmpty(reviewerFunction)
                ? null
                : new LambdaAdvisoryReviewer(LambdaAdvisoryReviewer.CreateClient(), reviewerFunction));
    }

    /// <summary>Tests: a service with fakes and a fixed clock (null service: the table is not configured).</summary>
    internal FraudAdvisoryCronFunction(FraudAdvisoryService? service, Func<DateTime> clock)
    {
        _service = service;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>EventBridge invokes it hourly with <c>{"body": "{\"action\":\"ai_review\"}"}</c>; the body is not read.</summary>
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var budget = CronRunBudget.StartNew();
        if (_service == null)
        {
            context.Logger.LogWarning(
                $"{FraudAdvisory.UnconfiguredToken} FRAUD_SIGNALS_TABLE is not set; nothing read, nothing sent.");
            return Summary(new FraudAdvisorySummary().ToDictionary("unconfigured"));
        }

        try
        {
            var (outcome, summary) = await _service.RunAsync(
                _clock(), budget.Slice(ReviewTime, ReviewReserve), context);
            return Summary(summary.ToDictionary(outcome));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"{FraudAdvisory.FailedToken} userId=- reason=run:{ex.GetType().Name}");
            return LambdaResponse.Error(500, "FRAUD_AI_REVIEW_ERROR", "The AI advisory run failed.");
        }
    }

    private static APIGatewayProxyResponse Summary(Dictionary<string, string> summary) => new()
    {
        StatusCode = 200,
        Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
    };
}
