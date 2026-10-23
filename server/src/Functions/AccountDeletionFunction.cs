using System.Diagnostics;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ACC-02: <c>DELETE /user/account</c> and the completion sweep (account-deletion.md).
///   - API Gateway event (it always sets requestContext): the caller deletes their own account.
///     200 deleted (or a replay), 409 ACCOUNT_002 blockers, 503 ACCOUNT_005 started and resumable,
///     400 ACCOUNT_001, 403 ACCOUNT_003 / AUTH_040, 404 AUTH_013, 500 AUTH_007 before any write.
///   - Scheduled or ops invoke (no requestContext): <c>{"action":"sweep"}</c> finishes or aborts
///     every deletion started more than an hour ago; <c>{"action":"reapply","userIds":[...]}</c>
///     reruns the erasure after a backup restore.
/// The same handler backs two Lambdas: the API one (30 s) and the sweep (5 minutes, concurrency 1).
/// </summary>
public class AccountDeletionFunction
{
    /// <summary>API Gateway answers 503 itself at 30 s, so a request stops starting writes before that.</summary>
    internal static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(24);

    /// <summary>Lambda time a request keeps back for the response after its last write.</summary>
    internal static readonly TimeSpan RequestReserve = TimeSpan.FromSeconds(4);

    private readonly AccountDeletionService _service;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public AccountDeletionFunction()
    {
        var dynamoDb = new AmazonDynamoDBClient();
        var tables = new AccountDeletionTables
        {
            Users = Env("USERS_TABLE"),
            Crops = Env("CROPS_TABLE"),
            Trips = Env("TRIPS_TABLE"),
            Orders = Env("ORDERS_TABLE"),
            Deliveries = Env("DELIVERIES_TABLE"),
            Escrows = Env("ESCROWS_TABLE"),
            EscrowAudit = Env("ESCROW_AUDIT_TABLE"),
            EscrowIdempotency = Env("ESCROW_IDEMPOTENCY_TABLE"),
            Ratings = Env("RATINGS_TABLE"),
            Messages = Env("MESSAGES_TABLE"),
            Intents = Env("INTENTS_TABLE"),
            MatchEvents = Env("MATCH_EVENTS_TABLE"),
            DeviceTokens = Env("DEVICE_TOKENS_TABLE")
        };

        _service = new AccountDeletionService(
            new AccountDeletionRepository(dynamoDb, tables),
            new CognitoAuthService(
                new AmazonCognitoIdentityProviderClient(), Env("COGNITO_CLIENT_ID"), Env("COGNITO_USER_POOL_ID")),
            new AmazonSimpleNotificationServiceClient(),
            new TransactionAuditRepository(dynamoDb, Env("TRANSACTION_AUDIT_TABLE")));
    }

    /// <summary>Constructor for testing.</summary>
    public AccountDeletionFunction(AccountDeletionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (request.RequestContext == null)
        {
            return await HandleJobAsync(request, context);
        }

        if (!string.Equals(request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return LambdaResponse.Error(405, "AUTH_007", "Method not allowed.");
        }

        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "AUTH_020", "Authentication required.");
        }

        context.Logger.LogInformation($"Account deletion requested by {MoneyAlarmLog.LogId(userId)}");

        var started = Stopwatch.StartNew();
        var budget = new DeletionBudget(() =>
            started.Elapsed >= RequestBudget || CronRunBudget.IsLambdaTimeLow(context, RequestReserve));

        var outcome = await _service.RequestAsync(
            userId,
            confirmed: IsConfirmBody(request.Body),
            stepUpFresh: StepUpPolicy.IsFresh(request),
            AuditContext.From(request, userId, AuthHelper.GetRole(request)),
            budget,
            context);

        AuditLogger.LogSecurity("ACCOUNT_DELETE", userId: userId,
            correlationId: AuditLogger.GetCorrelationId(request),
            detail: outcome.Result.ToString(),
            outcome: outcome.Result == AccountDeletionResult.Completed ? "success" : "failure",
            sourceIp: AuditLogger.GetSourceIp(request));

        return ToResponse(userId, outcome);
    }

    /// <summary>True only for exactly <c>{ "confirm": true }</c>.</summary>
    internal static bool IsConfirmBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize(body, AppJsonContext.Default.AccountDeletionRequest)?.Confirm == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static APIGatewayProxyResponse ToResponse(string userId, AccountDeletionOutcome outcome)
    {
        switch (outcome.Result)
        {
            case AccountDeletionResult.Completed:
                return LambdaResponse.Ok(new AccountDeletionResponse
                {
                    Deleted = true,
                    UserId = userId,
                    DeletionState = AccountDeletionPolicy.Completed,
                    DeletedAt = outcome.DeletedAt ?? ""
                }, AppJsonContext.Default.AccountDeletionResponse);

            case AccountDeletionResult.Blocked:
                return Error(409, new AccountDeletionError
                {
                    Code = "ACCOUNT_002",
                    Message = "Finish or cancel your active orders, deliveries and disputes before deleting your account.",
                    Blockers = outcome.Blockers.Take(AccountDeletionPolicy.MaxListedBlockers).ToList(),
                    BlockerCount = outcome.Blockers.Count
                });

            case AccountDeletionResult.Incomplete:
                var retry = Error(503, new AccountDeletionError
                {
                    Code = "ACCOUNT_005",
                    Message = "Account deletion is still in progress. Retry in a moment.",
                    RetryAfterSeconds = AccountDeletionService.RetryAfterSeconds
                }, outcome.DeletionState);
                retry.Headers["Retry-After"] = AccountDeletionService.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return retry;

            case AccountDeletionResult.NotFound:
                return LambdaResponse.Error(404, "AUTH_013", "User profile not found.");

            case AccountDeletionResult.Suspended:
                return Error(403, new AccountDeletionError
                {
                    Code = "ACCOUNT_003",
                    Message = "This account is suspended. Contact support to delete it."
                });

            case AccountDeletionResult.InvalidBody:
                return Error(400, new AccountDeletionError
                {
                    Code = "ACCOUNT_001",
                    Message = "Send { \"confirm\": true } to delete the account."
                });

            case AccountDeletionResult.StepUpRequired:
                return LambdaResponse.Error(403, StepUpPolicy.ErrorCode, StepUpPolicy.ErrorMessage);

            default:
                return LambdaResponse.Error(500, "AUTH_007", "The account could not be deleted. Please try again later.");
        }
    }

    private static APIGatewayProxyResponse Error(int status, AccountDeletionError error, string? deletionState = null)
        => LambdaResponse.Json(status, new AccountDeletionErrorResponse
        {
            Error = error,
            DeletionState = deletionState
        }, AppJsonContext.Default.AccountDeletionErrorResponse);

    // ──────────────────────────────────────────────
    // sweep and reapply (no API route reaches these)
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleJobAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        AccountDeletionJobRequest? job = null;
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            try
            {
                job = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.AccountDeletionJobRequest);
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "ACCOUNT_JOB_VALIDATION", "Invalid JSON in request body.");
            }
        }

        var budget = new DeletionBudget(() => CronRunBudget.IsLambdaTimeLow(context, CronRunBudget.DefaultReserve));
        var summary = new Dictionary<string, string> { { "action", job?.Action ?? "" } };

        switch (job?.Action)
        {
            case "sweep":
                var swept = await _service.SweepAsync(budget, context);
                summary["finished"] = swept.Finished.ToString(System.Globalization.CultureInfo.InvariantCulture);
                summary["aborted"] = swept.Aborted.ToString(System.Globalization.CultureInfo.InvariantCulture);
                summary["failed"] = swept.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                summary["truncated"] = swept.Truncated ? "true" : "false";
                context.Logger.LogInformation(
                    $"Account deletion sweep: {swept.Finished} finished, {swept.Aborted} aborted, {swept.Failed} failed, truncated={swept.Truncated}.");
                break;

            case "reapply":
                int reapplied = 0, skipped = 0, failed = 0;
                foreach (var userId in (job.UserIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
                {
                    if (budget.IsLow)
                    {
                        summary["truncated"] = "true";
                        break;
                    }

                    try
                    {
                        if (await _service.ReapplyAsync(userId, budget, context))
                        {
                            reapplied++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        context.Logger.LogError(
                            $"ACCOUNT_DELETION_INCOMPLETE userId={MoneyAlarmLog.LogId(userId)} state=reapply reason=[{ex.GetType().Name}]");
                    }
                }

                summary["reapplied"] = reapplied.ToString(System.Globalization.CultureInfo.InvariantCulture);
                summary["skipped"] = skipped.ToString(System.Globalization.CultureInfo.InvariantCulture);
                summary["failed"] = failed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;

            default:
                return LambdaResponse.Error(400, "ACCOUNT_JOB_VALIDATION", "Expected action 'sweep' or 'reapply'.");
        }

        return LambdaResponse.Ok(summary, AppJsonContext.Default.DictionaryStringString);
    }

    private static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set.");
}
