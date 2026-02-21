using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Escrow Lambda handler for all /escrow/* endpoints.
/// Routes based on HTTP method and path segment.
///
/// Endpoints:
/// - POST /escrow/initiate  → Buyer initiates escrow payment
/// - POST /escrow/release   → System/admin releases funds to farmer
/// - POST /escrow/dispute   → Buyer/farmer raises a dispute
/// - POST /escrow/refund    → System/admin refunds buyer
/// - GET  /escrow/history   → Authenticated user's payment history
/// - GET  /escrow/{id}      → Get single escrow by ID
///
/// Error codes: ESCROW_001 through ESCROW_023 (see escrow-protocol.md)
/// </summary>
public class EscrowFunction
{
    private readonly EscrowService _escrowService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public EscrowFunction()
    {
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE")
                           ?? throw new InvalidOperationException("ESCROWS_TABLE is not set.");
        var auditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE")
                         ?? throw new InvalidOperationException("ESCROW_AUDIT_TABLE is not set.");
        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE")
                               ?? throw new InvalidOperationException("ESCROW_IDEMPOTENCY_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();

        var escrowRepo = new EscrowRepository(dynamoDb, escrowsTable);
        var auditRepo = new EscrowAuditRepository(dynamoDb, auditTable);
        var idempotencyRepo = new EscrowIdempotencyRepository(dynamoDb, idempotencyTable);
        var telebirrGateway = CreateTelebirrGateway();

        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        IUserRepository? userRepo = null;
        if (!string.IsNullOrEmpty(usersTable))
        {
            userRepo = new UserRepository(dynamoDb, usersTable);
        }

        _escrowService = new EscrowService(escrowRepo, auditRepo, idempotencyRepo, telebirrGateway, userRepo);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public EscrowFunction(EscrowService escrowService)
    {
        _escrowService = escrowService ?? throw new ArgumentNullException(nameof(escrowService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"EscrowFunction invoked: {request.HttpMethod} {request.Path}");

        var action = ExtractAction(request);

        return (request.HttpMethod, action) switch
        {
            ("POST", "initiate") => await HandleInitiateAsync(request, context),
            ("POST", "release") => await HandleReleaseAsync(request, context),
            ("POST", "dispute") => await HandleDisputeAsync(request, context),
            ("POST", "refund") => await HandleRefundAsync(request, context),
            ("GET", "history") => await HandleHistoryAsync(request, context),
            ("GET", _) when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetByIdAsync(request, context),
            _ => LambdaResponse.Error(405, "ESCROW_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// POST /escrow/initiate — Buyer initiates escrow payment for a delivery.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleInitiateAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        InitiateEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.InitiateEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        try
        {
            var result = await _escrowService.InitiateAsync(body, userId);
            return MapResult(result,
                value => value.IsDuplicate
                    ? LambdaResponse.Ok(value, AppJsonContext.Default.InitiateEscrowResponse)
                    : LambdaResponse.Created(value, AppJsonContext.Default.InitiateEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error initiating escrow: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/release — Release escrowed funds to the farmer.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleReleaseAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        ReleaseEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.ReleaseEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var role = GetAuthenticatedUserRole(request);

        try
        {
            var result = await _escrowService.ReleaseAsync(body, userId, role);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.ReleaseEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error releasing escrow: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/dispute — Buyer or farmer raises a dispute.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDisputeAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        DisputeEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.DisputeEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        try
        {
            var result = await _escrowService.DisputeAsync(body, userId);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.DisputeEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error disputing escrow: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/refund — Refund escrowed funds to the buyer.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleRefundAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        RefundEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.RefundEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var role = GetAuthenticatedUserRole(request);

        try
        {
            var result = await _escrowService.RefundAsync(body, userId, role);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.RefundEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error refunding escrow: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// GET /escrow/history — Authenticated user's payment history.
    /// Query params: status, role, fromDate, toDate, limit, nextToken.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleHistoryAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("role", out var role);
        queryParams.TryGetValue("fromDate", out var fromDate);
        queryParams.TryGetValue("toDate", out var toDate);
        queryParams.TryGetValue("nextToken", out var nextToken);

        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "ESCROW_023",
                    "Invalid limit parameter. Must be between 1 and 50.");
            }
        }

        try
        {
            var result = await _escrowService.GetHistoryAsync(
                userId, status, role, fromDate, toDate, limit, nextToken);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.GetEscrowHistoryResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying escrow history: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// GET /escrow/{id} — Get a single escrow by ID.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        if (!request.PathParameters.TryGetValue("id", out var escrowId) || string.IsNullOrEmpty(escrowId))
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Missing escrow ID in path.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        try
        {
            var result = await _escrowService.GetByIdAsync(escrowId, userId);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.EscrowDto));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting escrow {escrowId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    // ──────────────────────────────────────────────
    // Routing helper
    // ──────────────────────────────────────────────

    /// <summary>
    /// Extract the action segment from the request path (e.g. "initiate" from "/escrow/initiate").
    /// Falls back to empty string for paths like "/escrow/{id}".
    /// </summary>
    private static string ExtractAction(APIGatewayProxyRequest request)
    {
        var path = request.Path ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expected: ["escrow", "initiate"] or ["escrow", "{id}"]
        if (segments.Length >= 2)
        {
            return segments[^1];
        }

        return "";
    }

    // ──────────────────────────────────────────────
    // Auth helpers (same pattern as DeliveryFunction)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Check if the request is authenticated by looking for an Authorization header
    /// or JWT authorizer claims in the request context.
    /// </summary>
    private static bool IsAuthenticated(APIGatewayProxyRequest request)
    {
        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "authorization", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(header.Value))
                {
                    return true;
                }
            }
        }

        if (request.RequestContext?.Authorizer != null
            && request.RequestContext.Authorizer.Count > 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extract the authenticated user ID from JWT authorizer claims.
    /// </summary>
    private static string? GetAuthenticatedUserId(APIGatewayProxyRequest request)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null)
        {
            return null;
        }

        if (authorizer.ContainsKey("sub"))
        {
            return authorizer["sub"]?.ToString();
        }

        if (authorizer.ContainsKey("userId"))
        {
            return authorizer["userId"]?.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extract the JWT role claim from the authorizer context.
    /// The API Gateway authorizer populates "role" from the Cognito custom:role claim.
    /// Returns null if the role claim is absent (e.g. for unauthenticated or legacy requests).
    /// </summary>
    private static string? GetAuthenticatedUserRole(APIGatewayProxyRequest request)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null)
        {
            return null;
        }

        return authorizer.ContainsKey("role") ? authorizer["role"]?.ToString() : null;
    }

    // ──────────────────────────────────────────────
    // Gateway factory
    // ──────────────────────────────────────────────

    /// <summary>
    /// Create the Telebirr gateway based on the TELEBIRR_MOCK environment variable.
    /// Set TELEBIRR_MOCK=true in development/staging to use the mock gateway.
    /// In production (TELEBIRR_MOCK absent or false), this throws to prevent
    /// accidental mock usage with real user funds.
    /// </summary>
    private static ITelebirrGateway CreateTelebirrGateway()
    {
        var useMock = string.Equals(
            Environment.GetEnvironmentVariable("TELEBIRR_MOCK"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (useMock)
        {
            return new MockTelebirrGateway();
        }

        throw new InvalidOperationException(
            "Real Telebirr gateway is not yet implemented. " +
            "Set TELEBIRR_MOCK=true for development/staging environments. " +
            "Do NOT deploy to production until a real ITelebirrGateway is wired here.");
    }

    // ──────────────────────────────────────────────
    // Result mapping
    // ──────────────────────────────────────────────

    /// <summary>
    /// Map an EscrowResult to an APIGatewayProxyResponse.
    /// On success/duplicate: use the provided success mapper.
    /// On failure: map EscrowResultError to LambdaResponse.Error.
    /// </summary>
    private static APIGatewayProxyResponse MapResult<T>(
        EscrowResult<T> result,
        Func<T, APIGatewayProxyResponse> onSuccess)
    {
        if (result.IsSuccess)
        {
            return onSuccess(result.Value!);
        }

        var error = result.Error!;
        return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
    }
}
