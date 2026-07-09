using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// EventBridge scheduled event handler for order background jobs.
/// Routes based on "action" field in the request body:
///   - auto_cancel_expired (hourly): cancel unpaid orders older than 24h
///
/// Error codes:
/// - 400: ORDER_CRON_VALIDATION - Invalid or missing action
/// - 500: ORDER_CRON_ERROR - Unexpected failure during processing
/// </summary>
public class OrderCronFunction
{
    private readonly IOrderRepository _orderRepo;
    // Optional: unified transaction-audit trail. Auto-cancel is a system-driven
    // order transition that would otherwise be invisible in the monitor.
    private readonly ITransactionAuditRepository? _transactionAuditRepo;

    private const int ExpirationHours = 24;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public OrderCronFunction()
    {
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _orderRepo = new OrderRepository(dynamoDb, ordersTable);

        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _transactionAuditRepo = txAuditTable != null
            ? new TransactionAuditRepository(dynamoDb, txAuditTable)
            : null;
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public OrderCronFunction(IOrderRepository orderRepo, ITransactionAuditRepository? transactionAuditRepo = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _transactionAuditRepo = transactionAuditRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("OrderCronFunction invoked.");

        // Parse action from request body
        string? action = null;
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                var payload = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.DictionaryStringString);
                payload?.TryGetValue("action", out action);
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    "Invalid JSON in request body.");
            }
        }

        // Fallback: check query string parameters
        if (string.IsNullOrEmpty(action))
        {
            request.QueryStringParameters?.TryGetValue("action", out action);
        }

        if (string.IsNullOrEmpty(action))
        {
            return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                "Missing 'action' field. Expected: auto_cancel_expired.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Order cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_cancel_expired" => await HandleAutoCancelExpiredAsync(context),
                _ => LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: auto_cancel_expired.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"OrderCronFunction error for action '{safeAction}': [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ORDER_CRON_ERROR",
                $"Order cron job '{safeAction}' failed unexpectedly.");
        }
    }

    /// <summary>
    /// Auto-cancel orders in "created" or "pending_payment" status older than 24 hours.
    /// These are orders where the buyer never completed payment.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAutoCancelExpiredAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-ExpirationHours).ToString("o");

        var cancelledCount = 0;

        // Cancel expired "created" orders
        cancelledCount += await CancelExpiredByStatusAsync("created", cutoff, now, context);

        // Cancel expired "pending_payment" orders
        cancelledCount += await CancelExpiredByStatusAsync("pending_payment", cutoff, now, context);

        var summary = new Dictionary<string, string>
        {
            { "action", "auto_cancel_expired" },
            { "cancelled", cancelledCount.ToString() },
            { "completedAt", now.ToString("o") }
        };

        context.Logger.LogInformation($"Order auto-cancel complete: {cancelledCount} orders cancelled.");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    private async Task<int> CancelExpiredByStatusAsync(
        string status, string createdBefore, DateTime now, ILambdaContext context)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync(status, createdBefore);
        var cancelledCount = 0;

        foreach (var order in orders)
        {
            var orderId = GetString(order, "orderId");
            var version = GetInt(order, "version");

            context.Logger.LogInformation($"Auto-cancelling expired order {orderId} (was {status}).");

            var updateData = new Dictionary<string, object>
            {
                { "status", "cancelled" },
                { "cancelledAt", now.ToString("o") },
                { "cancelReason", $"Auto-cancelled: no payment within {ExpirationHours} hours." }
            };

            var result = await _orderRepo.UpdateAsync(orderId, updateData, version);

            if (result.IsSuccess)
            {
                cancelledCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_cancelled", status, "cancelled",
                    AuditContext.ForSystem(),
                    metadata: $"Auto-cancelled: no payment within {ExpirationHours}h.");
            }
            else
            {
                context.Logger.LogError(
                    $"Auto-cancel failed for order {orderId}: {result.Error?.Code} - {result.Error?.Message}");
            }
        }

        return cancelledCount;
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value)) return 0;
        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}
