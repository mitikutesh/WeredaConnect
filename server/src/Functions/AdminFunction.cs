using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Admin Lambda handler for all /admin/* endpoints.
/// Requires admin role in JWT claims. Routes based on HTTP method and path segments.
///
/// Endpoints:
/// - GET  /admin/dashboard/stats        → KPI aggregation
/// - GET  /admin/escrows                → paginated escrow list
/// - GET  /admin/escrows/{id}           → single escrow detail
/// - GET  /admin/escrows/{id}/audit     → audit trail
/// - GET  /admin/disputes               → disputed escrows
/// - POST /admin/disputes/{id}/resolve  → resolve dispute
/// - GET  /admin/fraud-signals          → fraud signals list
/// - POST /admin/fraud-signals/{id}/action → review fraud signal
/// - GET  /admin/users                  → user list with search
/// - GET  /admin/users/{id}             → user detail
/// - POST /admin/users/{id}/action      → suspend/verify/change role
/// - GET  /admin/health/overview        → system health metrics
/// - GET  /admin/compliance/summary     → NBE compliance
/// - GET  /admin/analytics/revenue      → commission analytics
/// - GET  /admin/analytics/transactions → volume metrics
/// - GET  /admin/transactions           → transaction monitor list (order-anchored)
/// - GET  /admin/transactions/{orderId} → stitched transaction detail + timeline
/// </summary>
public class AdminFunction
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _escrowsTable;
    private readonly string _escrowAuditTable;
    private readonly string _transactionAuditTable;
    private readonly string _fraudSignalsTable;
    private readonly string _usersTable;
    private readonly string _cropsTable;
    private readonly string _tripsTable;
    private readonly string _ordersTable;
    private readonly string _deliveriesTable;
    private readonly string _paymentsTable;

    public AdminFunction()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE") ?? "";
        _escrowAuditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE") ?? "";
        _transactionAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE") ?? "";
        _fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        _usersTable = Environment.GetEnvironmentVariable("USERS_TABLE") ?? "";
        _cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
        _tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE") ?? "";
        _ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE") ?? "";
        _deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE") ?? "";
        _paymentsTable = Environment.GetEnvironmentVariable("PAYMENTS_TABLE") ?? "";
    }

    /// <summary>
    /// Test/DI constructor: inject the DynamoDB client and the users table name so the
    /// handler can be exercised without a live AWS client. Other table names default to
    /// empty (their handlers short-circuit on empty table names).
    /// </summary>
    internal AdminFunction(IAmazonDynamoDB dynamoDb, string usersTable)
    {
        _dynamoDb = dynamoDb;
        _usersTable = usersTable;
        _escrowsTable = "";
        _escrowAuditTable = "";
        _transactionAuditTable = "";
        _fraudSignalsTable = "";
        _cropsTable = "";
        _tripsTable = "";
        _ordersTable = "";
        _deliveriesTable = "";
        _paymentsTable = "";
    }

    /// <summary>
    /// Test/DI constructor for the transaction-monitor endpoints: inject the client
    /// plus the tables those handlers touch (orders + escrows + deliveries + escrow
    /// audit). Unrelated table names default to empty.
    /// </summary>
    internal AdminFunction(
        IAmazonDynamoDB dynamoDb,
        string usersTable,
        string ordersTable,
        string escrowsTable,
        string deliveriesTable,
        string escrowAuditTable,
        string transactionAuditTable = "")
    {
        _dynamoDb = dynamoDb;
        _usersTable = usersTable;
        _ordersTable = ordersTable;
        _escrowsTable = escrowsTable;
        _deliveriesTable = deliveriesTable;
        _escrowAuditTable = escrowAuditTable;
        _transactionAuditTable = transactionAuditTable;
        _fraudSignalsTable = "";
        _cropsTable = "";
        _tripsTable = "";
        _paymentsTable = "";
    }

    /// <summary>
    /// Marketplace roles an admin may assign via change_role. "admin" is deliberately
    /// excluded: allowing it here would let an admin escalate any account (including their
    /// own) to admin, which EscrowService trusts for fund release (CHAIN-01). Compared
    /// case-insensitively; the matched canonical lowercase value is what gets stored,
    /// matching the role-claim convention used across the server (e.g. "buyer"/"farmer").
    /// </summary>
    private static readonly string[] AssignableRoles = { "farmer", "trucker", "buyer" };

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"AdminFunction invoked: {request.HttpMethod} {request.Path}");

        if (!AdminAuthHelper.IsAdmin(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        var segments = GetPathSegments(request);

        try
        {
            return (request.HttpMethod, segments) switch
            {
                ("GET", ["admin", "dashboard", "stats"]) => await HandleDashboardStatsAsync(request, context),
                ("GET", ["admin", "escrows"]) => await HandleListEscrowsAsync(request, context),
                ("GET", ["admin", "escrows", _, "audit"]) => await HandleEscrowAuditAsync(segments[2], context),
                ("GET", ["admin", "escrows", _]) => await HandleGetEscrowAsync(segments[2], context),
                ("GET", ["admin", "disputes"]) => await HandleListDisputesAsync(request, context),
                ("POST", ["admin", "disputes", _, "resolve"]) => await HandleResolveDisputeAsync(segments[2], request, context),
                ("GET", ["admin", "fraud-signals"]) => await HandleListFraudSignalsAsync(request, context),
                ("POST", ["admin", "fraud-signals", _, "action"]) => await HandleFraudActionAsync(segments[2], request, context),
                ("GET", ["admin", "users"]) => await HandleListUsersAsync(request, context),
                ("GET", ["admin", "users", _]) => await HandleGetUserAsync(segments[2], context),
                ("POST", ["admin", "users", _, "action"]) => await HandleUserActionAsync(segments[2], request, context),
                ("GET", ["admin", "health", "overview"]) => await HandleHealthOverviewAsync(context),
                ("GET", ["admin", "compliance", "summary"]) => await HandleComplianceSummaryAsync(context),
                ("GET", ["admin", "analytics", "revenue"]) => await HandleRevenueAnalyticsAsync(request, context),
                ("GET", ["admin", "analytics", "transactions"]) => await HandleTransactionAnalyticsAsync(request, context),
                ("GET", ["admin", "transactions"]) => await HandleListTransactionsAsync(request, context),
                ("GET", ["admin", "transactions", _]) => await HandleGetTransactionAsync(segments[2], context),
                ("GET", ["admin", "audit"]) => await HandleAuditLogAsync(request, context),
                _ => LambdaResponse.Error(404, "ADMIN_002", "Admin endpoint not found.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"AdminFunction error: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ADMIN_500", "Internal server error.");
        }
    }

    // ──────────────────────────────────────────────
    // Dashboard
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleDashboardStatsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var usersCount = await ScanCountAsync(_usersTable);
        // Roles are stored lowercase (validated case-insensitively, persisted
        // verbatim from the lowercase wire value); the DynamoDB filter is
        // case-sensitive, so capitalized values matched nothing and counted 0.
        var farmersCount = await ScanCountWithFilterAsync(_usersTable, "role", "farmer");
        var truckersCount = await ScanCountWithFilterAsync(_usersTable, "role", "trucker");
        var buyersCount = await ScanCountWithFilterAsync(_usersTable, "role", "buyer");
        var escrowsCount = await ScanCountAsync(_escrowsTable);
        // Funded-but-not-released escrows are persisted as "escrowed" (EscrowService
        // sets this after a successful Telebirr hold); "held" is never written to the
        // escrow record, so the old filter always counted 0.
        var activeEscrows = await ScanCountWithFilterAsync(_escrowsTable, "status", "escrowed");
        var disputedEscrows = await ScanCountWithFilterAsync(_escrowsTable, "status", "disputed");
        var ordersCount = await ScanCountAsync(_ordersTable);
        // Crops are created with status "available" (never "active"); the old
        // filter counted 0. Valid crop statuses: available/reserved/sold/deleted.
        var activeListings = await ScanCountWithFilterAsync(_cropsTable, "status", "available");
        var fraudSignalsCount = await ScanCountAsync(_fraudSignalsTable);
        var (totalVolume, totalCommissions) = await ScanEscrowTotalsAsync();

        var response = new AdminDashboardStatsResponse
        {
            TotalUsers = usersCount,
            TotalFarmers = farmersCount,
            TotalTruckers = truckersCount,
            TotalBuyers = buyersCount,
            TotalEscrows = escrowsCount,
            ActiveEscrows = activeEscrows,
            DisputedEscrows = disputedEscrows,
            TotalOrders = ordersCount,
            ActiveListings = activeListings,
            FraudSignalsCount = fraudSignalsCount,
            TotalEscrowVolume = totalVolume,
            TotalCommissions = totalCommissions,
            GeneratedAt = DateTime.UtcNow.ToString("o")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDashboardStatsResponse);
    }

    // ──────────────────────────────────────────────
    // Escrows
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleListEscrowsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("nextToken", out var nextToken);
        var limit = ParseLimit(queryParams, 20);

        Dictionary<string, AttributeValue>? startKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
        }

        var (items, lastKey) = await ScanItemsAsync(_escrowsTable, status, "status", limit, startKey);
        var escrows = items.Select(MapToEscrowDto).ToList();
        await EnrichEscrowNamesAsync(escrows);

        string? newNextToken = null;
        if (lastKey != null)
        {
            newNextToken = PaginationTokenHelper.EncodeNextToken(lastKey);
        }

        var response = new AdminEscrowListResponse
        {
            Escrows = escrows,
            NextToken = newNextToken,
            Total = escrows.Count
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminEscrowListResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleGetEscrowAsync(string escrowId, ILambdaContext context)
    {
        var item = await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId);
        if (item == null)
        {
            return LambdaResponse.Error(404, "ADMIN_003", "Escrow not found.");
        }

        var escrow = MapToEscrowDto(DynamoDbHelper.FromItem(item));
        await EnrichEscrowNamesAsync(new List<EscrowDto> { escrow });
        var response = new AdminEscrowDetailResponse { Escrow = escrow };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminEscrowDetailResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleEscrowAuditAsync(string escrowId, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_escrowAuditTable))
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Audit table not configured.");
        }

        var auditRepo = new EscrowAuditRepository(_dynamoDb, _escrowAuditTable);

        // The admin dashboard requests the global feed via the "all" sentinel
        // (useEscrowAudit("all") → /admin/escrows/all/audit). Real escrow IDs are
        // "escrow-{guid}", so this never collides. For "all" we scan recent
        // entries across every escrow (newest first); otherwise we return the
        // per-escrow trail. See EscrowAuditRepository for the scan cap + GSI
        // scale path. F-35.
        var auditItems =
            string.Equals(escrowId, "all", StringComparison.OrdinalIgnoreCase)
                ? await auditRepo.GetRecentAcrossEscrowsAsync(100)
                : await auditRepo.GetAuditTrailAsync(escrowId);
        var entries = auditItems.Select(MapToAuditEntry).ToList();

        var response = new AdminEscrowAuditResponse
        {
            EscrowId = escrowId,
            AuditEntries = entries
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminEscrowAuditResponse);
    }

    // ──────────────────────────────────────────────
    // Disputes
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleListDisputesAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("nextToken", out var nextToken);
        var limit = ParseLimit(queryParams, 20);

        Dictionary<string, AttributeValue>? startKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
        }

        var (items, lastKey) = await ScanItemsAsync(_escrowsTable, "disputed", "status", limit, startKey);
        var disputes = items.Select(MapToEscrowDto).ToList();
        await EnrichEscrowNamesAsync(disputes);

        string? newNextToken = null;
        if (lastKey != null)
        {
            newNextToken = PaginationTokenHelper.EncodeNextToken(lastKey);
        }

        var response = new AdminDisputeListResponse
        {
            Disputes = disputes,
            NextToken = newNextToken,
            Total = disputes.Count
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDisputeListResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleResolveDisputeAsync(
        string escrowId,
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        ResolveDisputeRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.ResolveDisputeRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        var item = await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId);
        if (item == null)
        {
            return LambdaResponse.Error(404, "ADMIN_003", "Escrow not found.");
        }

        var now = DateTime.UtcNow.ToString("o");
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";

        var createdAt = item.TryGetValue("createdAt", out var ca) ? ca.S : "";

        var updateRequest = new UpdateItemRequest
        {
            TableName = _escrowsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET #status = :newStatus, #updatedAt = :now, #resolution = :resolution, #resolvedBy = :resolvedBy, #resolvedAt = :resolvedAt",
            // Only resolve an escrow that is actually disputed — prevents force-
            // transitioning held/released/refunded/already-resolved escrows.
            ConditionExpression = "#status = :disputed",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updatedAt", "updatedAt" },
                { "#resolution", "disputeResolution" },
                { "#resolvedBy", "disputeResolvedBy" },
                { "#resolvedAt", "disputeResolvedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":newStatus", new AttributeValue { S = "dispute_resolved" } },
                { ":now", new AttributeValue { S = now } },
                { ":resolution", new AttributeValue { S = body.Resolution } },
                { ":resolvedBy", new AttributeValue { S = adminUserId } },
                { ":resolvedAt", new AttributeValue { S = now } },
                { ":disputed", new AttributeValue { S = "disputed" } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(updateRequest);
        }
        catch (ConditionalCheckFailedException)
        {
            return LambdaResponse.Error(409, "ADMIN_010",
                "Escrow is not in a disputed state.");
        }

        // Audit: this admin dispute-resolution is otherwise unrecorded (it updates
        // the escrow directly, not via EscrowService, so no escrow-audit entry).
        var disputeOrderId = item.TryGetValue("orderId", out var doid) && doid.S != null ? doid.S : escrowId;
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, disputeOrderId, "escrow", escrowId,
            "admin_dispute_resolved", "disputed", "dispute_resolved",
            AuditContext.From(request, adminUserId, "admin"), metadata: body.Resolution);
        AuditLogger.LogAudit("ADMIN", "RESOLVE_DISPUTE",
            userId: adminUserId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "escrow", entityId: escrowId, outcome: "success");

        var response = new ResolveDisputeResponse
        {
            EscrowId = escrowId,
            Status = "dispute_resolved",
            Resolution = body.Resolution,
            ResolvedAt = now,
            Message = "Dispute resolved successfully."
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.ResolveDisputeResponse);
    }

    // ──────────────────────────────────────────────
    // Fraud Signals
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleListFraudSignalsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_fraudSignalsTable))
        {
            return LambdaResponse.Ok(
                new AdminFraudSignalListResponse { Signals = new List<FraudSignalDto>(), Total = 0 },
                AppJsonContext.Default.AdminFraudSignalListResponse);
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("nextToken", out var nextToken);
        var limit = ParseLimit(queryParams, 20);

        Dictionary<string, AttributeValue>? startKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
        }

        var (items, lastKey) = await ScanItemsAsync(_fraudSignalsTable, status, "status", limit, startKey);
        var signals = items.Select(MapToFraudSignalDto).ToList();

        string? newNextToken = null;
        if (lastKey != null)
        {
            newNextToken = PaginationTokenHelper.EncodeNextToken(lastKey);
        }

        var response = new AdminFraudSignalListResponse
        {
            Signals = signals,
            NextToken = newNextToken,
            Total = signals.Count
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminFraudSignalListResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleFraudActionAsync(
        string signalId,
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_fraudSignalsTable))
        {
            return LambdaResponse.Error(503, "ADMIN_006", "Fraud signals table not configured.");
        }

        FraudActionRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.FraudActionRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        var now = DateTime.UtcNow.ToString("o");
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";

        var updateRequest = new UpdateItemRequest
        {
            TableName = _fraudSignalsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "signalId", new AttributeValue { S = signalId } }
            },
            UpdateExpression = "SET #status = :status, #reviewedBy = :reviewedBy, #reviewedAt = :reviewedAt, #reviewNotes = :notes",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#reviewedBy", "reviewedBy" },
                { "#reviewedAt", "reviewedAt" },
                { "#reviewNotes", "reviewNotes" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = body.Action } },
                { ":reviewedBy", new AttributeValue { S = adminUserId } },
                { ":reviewedAt", new AttributeValue { S = now } },
                { ":notes", body.Notes != null ? new AttributeValue { S = body.Notes } : new AttributeValue { NULL = true } }
            }
        };

        await _dynamoDb.UpdateItemAsync(updateRequest);

        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, signalId, "fraud", signalId,
            $"fraud_{body.Action}", null, body.Action,
            AuditContext.From(request, adminUserId, "admin"), metadata: body.Notes);
        AuditLogger.LogAudit("ADMIN", "FRAUD_ACTION",
            userId: adminUserId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "fraudSignal", entityId: signalId, detail: body.Action, outcome: "success");

        var response = new FraudActionResponse
        {
            SignalId = signalId,
            Status = body.Action,
            ReviewedAt = now,
            Message = "Fraud signal reviewed successfully."
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.FraudActionResponse);
    }

    // ──────────────────────────────────────────────
    // Users
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleListUsersAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("role", out var role);
        queryParams.TryGetValue("search", out var search);
        queryParams.TryGetValue("nextToken", out var nextToken);
        var limit = ParseLimit(queryParams, 20);

        Dictionary<string, AttributeValue>? startKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
        }

        var scanRequest = new ScanRequest
        {
            TableName = _usersTable,
            Limit = limit
        };

        if (startKey != null)
        {
            scanRequest.ExclusiveStartKey = startKey;
        }

        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>();

        if (!string.IsNullOrEmpty(role))
        {
            filterParts.Add("#role = :role");
            attrNames["#role"] = "role";
            attrValues[":role"] = new AttributeValue { S = role };
        }

        if (!string.IsNullOrEmpty(search))
        {
            filterParts.Add("(contains(#phone, :search) OR contains(#displayName, :search))");
            attrNames["#phone"] = "phoneNumber";
            attrNames["#displayName"] = "displayName";
            attrValues[":search"] = new AttributeValue { S = search };
        }

        if (filterParts.Count > 0)
        {
            scanRequest.FilterExpression = string.Join(" AND ", filterParts);
            scanRequest.ExpressionAttributeNames = attrNames;
            scanRequest.ExpressionAttributeValues = attrValues;
        }

        var scanResponse = await _dynamoDb.ScanAsync(scanRequest);
        var users = scanResponse.Items.Select(UserRepository.FromItem).ToList();

        string? newNextToken = null;
        if (scanResponse.LastEvaluatedKey.Count > 0)
        {
            newNextToken = PaginationTokenHelper.EncodeNextToken(scanResponse.LastEvaluatedKey);
        }

        var response = new AdminUserListResponse
        {
            Users = users,
            NextToken = newNextToken,
            Total = users.Count
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminUserListResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleGetUserAsync(string userId, ILambdaContext context)
    {
        var userRepo = new UserRepository(_dynamoDb, _usersTable);
        var user = await userRepo.GetUserByIdAsync(userId);
        if (user == null)
        {
            return LambdaResponse.Error(404, "ADMIN_007", "User not found.");
        }

        var response = new AdminUserDetailResponse { User = user };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminUserDetailResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleUserActionAsync(
        string userId,
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        UserActionRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.UserActionRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        var now = DateTime.UtcNow.ToString("o");
        var updateParts = new List<string> { "#updatedAt = :now" };
        var attrNames = new Dictionary<string, string>
        {
            { "#updatedAt", "updatedAt" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":now", new AttributeValue { S = now } }
        };

        switch (body.Action)
        {
            case "suspend":
                updateParts.Add("#suspended = :suspended");
                attrNames["#suspended"] = "suspended";
                attrValues[":suspended"] = new AttributeValue { BOOL = true };
                if (body.Reason != null)
                {
                    updateParts.Add("#suspendReason = :reason");
                    attrNames["#suspendReason"] = "suspendReason";
                    attrValues[":reason"] = new AttributeValue { S = body.Reason };
                }
                break;
            case "unsuspend":
                updateParts.Add("#suspended = :suspended");
                attrNames["#suspended"] = "suspended";
                attrValues[":suspended"] = new AttributeValue { BOOL = false };
                break;
            case "verify":
                updateParts.Add("#faydaVerified = :verified");
                attrNames["#faydaVerified"] = "faydaVerified";
                attrValues[":verified"] = new AttributeValue { BOOL = true };
                break;
            case "change_role":
                if (string.IsNullOrEmpty(body.NewRole))
                {
                    return LambdaResponse.Error(400, "ADMIN_008", "NewRole is required for change_role action.");
                }
                // Allowlist guard (CHAIN-01): reject any role outside the three marketplace
                // roles. This forbids escalation to "admin" (which EscrowService trusts for
                // fund release) and any other arbitrary value. Compared case-insensitively;
                // the canonical lowercase value is stored.
                var requestedRole = AssignableRoles.FirstOrDefault(
                    r => string.Equals(r, body.NewRole, StringComparison.OrdinalIgnoreCase));
                if (requestedRole == null)
                {
                    return LambdaResponse.Error(400, "ADMIN_011",
                        "NewRole must be one of: farmer, trucker, buyer.");
                }
                updateParts.Add("#role = :newRole");
                attrNames["#role"] = "role";
                attrValues[":newRole"] = new AttributeValue { S = requestedRole };
                break;
            default:
                return LambdaResponse.Error(400, "ADMIN_009", $"Unknown action: {body.Action}");
        }

        var updateRequest = new UpdateItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } }
            },
            UpdateExpression = "SET " + string.Join(", ", updateParts),
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ConditionExpression = "attribute_exists(userId)"
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(updateRequest);
        }
        catch (ConditionalCheckFailedException)
        {
            return LambdaResponse.Error(404, "ADMIN_007", "User not found.");
        }

        var actingAdminId = AuthHelper.GetUserId(request) ?? "admin";
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, userId, "user", userId,
            $"user_{body.Action}", null, null,
            AuditContext.From(request, actingAdminId, "admin"),
            metadata: body.Action == "change_role" ? body.NewRole : body.Reason);
        AuditLogger.LogAudit("ADMIN", "USER_ACTION",
            userId: actingAdminId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "user", entityId: userId, detail: body.Action, outcome: "success");

        var response = new UserActionResponse
        {
            UserId = userId,
            Action = body.Action,
            Status = "completed",
            UpdatedAt = now,
            Message = $"User action '{body.Action}' completed successfully."
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.UserActionResponse);
    }

    // ──────────────────────────────────────────────
    // Health & Compliance
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleHealthOverviewAsync(ILambdaContext context)
    {
        var tableNames = new[] { _usersTable, _escrowsTable, _ordersTable, _cropsTable, _tripsTable }
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        var activeTables = 0;
        foreach (var table in tableNames)
        {
            try
            {
                var desc = await _dynamoDb.DescribeTableAsync(table);
                if (desc.Table.TableStatus == TableStatus.ACTIVE)
                {
                    activeTables++;
                }
            }
            catch
            {
                // Table not accessible
            }
        }

        var response = new AdminHealthOverviewResponse
        {
            Status = activeTables == tableNames.Length ? "healthy" : "degraded",
            Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown",
            ActiveTables = activeTables,
            Uptime = "N/A",
            CheckedAt = DateTime.UtcNow.ToString("o")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminHealthOverviewResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleComplianceSummaryAsync(ILambdaContext context)
    {
        var totalTransactions = await ScanCountAsync(_escrowsTable);
        var faydaVerified = await ScanCountWithBoolFilterAsync(_usersTable, "faydaVerified", true);
        var unverified = await ScanCountWithBoolFilterAsync(_usersTable, "faydaVerified", false);
        var disputesOpen = await ScanCountWithFilterAsync(_escrowsTable, "status", "disputed");
        var disputesResolved = await ScanCountWithFilterAsync(_escrowsTable, "status", "dispute_resolved");
        var (totalVolume, _) = await ScanEscrowTotalsAsync();

        var response = new AdminComplianceSummaryResponse
        {
            TotalTransactions = totalTransactions,
            FaydaVerifiedUsers = faydaVerified,
            UnverifiedUsers = unverified,
            DisputesOpen = disputesOpen,
            DisputesResolved = disputesResolved,
            TotalVolume = totalVolume,
            Currency = "ETB",
            GeneratedAt = DateTime.UtcNow.ToString("o")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminComplianceSummaryResponse);
    }

    // ──────────────────────────────────────────────
    // Analytics
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleRevenueAnalyticsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("period", out var period);
        period ??= "all";

        var (totalVolume, totalCommissions) = await ScanEscrowTotalsAsync();
        // "completed" is the terminal settled state (contract §Status Definitions);
        // "released" is transient and immediately transitions to "completed".
        var completedCount = await ScanCountWithFilterAsync(_escrowsTable, "status", "completed");

        var avgCommission = completedCount > 0 ? totalCommissions / completedCount : 0m;

        var response = new AdminRevenueAnalyticsResponse
        {
            TotalCommissions = totalCommissions,
            TotalVolume = totalVolume,
            TransactionCount = completedCount,
            AverageCommission = avgCommission,
            Currency = "ETB",
            Period = period,
            GeneratedAt = DateTime.UtcNow.ToString("o")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminRevenueAnalyticsResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleTransactionAnalyticsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("period", out var period);
        period ??= "all";

        var total = await ScanCountAsync(_escrowsTable);
        // Terminal settled state is "completed", not the transient "released".
        var completed = await ScanCountWithFilterAsync(_escrowsTable, "status", "completed");
        // In-flight (funded, not yet released) escrows are stored as "escrowed",
        // not "held" — the old filter always returned 0.
        var pending = await ScanCountWithFilterAsync(_escrowsTable, "status", "escrowed");
        var disputed = await ScanCountWithFilterAsync(_escrowsTable, "status", "disputed");
        var refunded = await ScanCountWithFilterAsync(_escrowsTable, "status", "refunded");
        var (totalVolume, _) = await ScanEscrowTotalsAsync();

        var response = new AdminTransactionAnalyticsResponse
        {
            TotalTransactions = total,
            CompletedTransactions = completed,
            PendingTransactions = pending,
            DisputedTransactions = disputed,
            RefundedTransactions = refunded,
            TotalVolume = totalVolume,
            Currency = "ETB",
            Period = period,
            GeneratedAt = DateTime.UtcNow.ToString("o")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminTransactionAnalyticsResponse);
    }

    // ──────────────────────────────────────────────
    // Transaction Monitor (unified order + delivery + escrow view)
    // ──────────────────────────────────────────────

    /// <summary>
    /// GET /admin/transactions — order-anchored list of every transaction with a
    /// derived lifecycle stage. Supports ?status= (exact order status), ?search=
    /// (order id / buyer / farmer name contains), and cursor pagination. The order
    /// status alone encodes the stage, and orders carry party names, so this is a
    /// single scan with no per-row joins.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleListTransactionsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_ordersTable))
        {
            return LambdaResponse.Ok(
                new AdminTransactionListResponse { Transactions = new List<AdminTransactionSummary>(), Total = 0 },
                AppJsonContext.Default.AdminTransactionListResponse);
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("search", out var search);
        var limit = ParseLimit(queryParams, 50);

        var scanRequest = new ScanRequest { TableName = _ordersTable };

        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>();

        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            attrNames["#status"] = "status";
            attrValues[":status"] = new AttributeValue { S = status };
        }

        if (!string.IsNullOrEmpty(search))
        {
            filterParts.Add("(contains(#orderId, :search) OR contains(#buyerName, :search) OR contains(#farmerName, :search))");
            attrNames["#orderId"] = "orderId";
            attrNames["#buyerName"] = "buyerName";
            attrNames["#farmerName"] = "farmerName";
            attrValues[":search"] = new AttributeValue { S = search };
        }

        if (filterParts.Count > 0)
        {
            scanRequest.FilterExpression = string.Join(" AND ", filterParts);
            scanRequest.ExpressionAttributeNames = attrNames;
            scanRequest.ExpressionAttributeValues = attrValues;
        }

        // Scan has no ordering and applies the filter AFTER the per-page read, so we
        // page through (bounded) collecting EVERY match — otherwise a search would
        // silently miss rows beyond an arbitrary first page. Sort newest-first in
        // memory and return the most recent `limit`. Bounded by MaxScanItems to cap
        // cost; a GSI keyed for a global newest-first feed is the scale path.
        const int MaxScanItems = 5000;
        var matched = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        var scanned = 0;
        var capped = false;

        do
        {
            if (lastKey != null)
            {
                scanRequest.ExclusiveStartKey = lastKey;
            }

            var resp = await _dynamoDb.ScanAsync(scanRequest);
            scanned += resp.ScannedCount;
            matched.AddRange(resp.Items.Select(DynamoDbHelper.FromItem));
            lastKey = resp.LastEvaluatedKey.Count > 0 ? resp.LastEvaluatedKey : null;

            if (scanned >= MaxScanItems)
            {
                capped = true;
                break;
            }
        } while (lastKey != null);

        if (capped)
        {
            context.Logger.LogWarning(
                $"Transaction list scan hit the {MaxScanItems}-item cap; older transactions may be omitted.");
        }

        // Newest-first (createdAt is ISO-8601 → ordinal descending == chronological).
        matched.Sort((a, b) => string.CompareOrdinal(GetString(b, "createdAt"), GetString(a, "createdAt")));

        var response = new AdminTransactionListResponse
        {
            Transactions = matched.Take(limit).Select(MapToTransactionSummary).ToList(),
            NextToken = null,
            Total = matched.Count
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminTransactionListResponse);
    }

    /// <summary>
    /// GET /admin/transactions/{orderId} — the full stitched transaction. Joins the
    /// escrow (payment/settlement hub) via the OrderIndex GSI, the delivery via
    /// escrow.deliveryId, and merges order + delivery timestamps with the immutable
    /// escrow audit trail into a single chronological timeline.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetTransactionAsync(string orderId, ILambdaContext context)
    {
        var orderItem = await QuerySingleItemAsync(_ordersTable, "orderId", orderId);
        if (orderItem == null)
        {
            return LambdaResponse.Error(404, "ADMIN_012", "Transaction not found.");
        }

        var order = OrderFunction.MapToOrderDto(DynamoDbHelper.FromItem(orderItem));

        // Escrow is the hub: it links the order to its delivery and holds the
        // payment/settlement state. Looked up by orderId via the OrderIndex GSI.
        var escrowItem = await GetEscrowByOrderIdAsync(orderId);
        EscrowDto? escrow = escrowItem != null ? MapToEscrowDto(escrowItem) : null;
        if (escrow != null)
        {
            await EnrichEscrowNamesAsync(new List<EscrowDto> { escrow });
        }

        // Unified audit for the order/delivery/admin legs (Phase 2), anchored on
        // orderId. Gathered first because it can also surface the deliveryId when
        // the escrow join can't (see below).
        var unifiedEvents = new List<Dictionary<string, object>>();
        var txRepo = TxAuditRepo();
        if (txRepo != null)
        {
            unifiedEvents.AddRange(await txRepo.GetByTransactionAsync(orderId));
        }

        // Delivery join: prefer the escrow's deliveryId (the primary link), but fall
        // back to a deliveryId surfaced by a unified delivery event — so the delivery
        // card + GPS still render for orders whose escrow lacks orderId (sparse
        // OrderIndex) or has no escrow yet. Admins see phone + QR token.
        var deliveryId = escrow?.DeliveryId;
        if (string.IsNullOrEmpty(deliveryId))
        {
            var deliveryEvent = unifiedEvents.FirstOrDefault(e => GetString(e, "entityType") == "delivery");
            if (deliveryEvent != null)
            {
                deliveryId = GetStringOrNull(deliveryEvent, "entityId");
            }
        }

        DeliveryDto? delivery = null;
        if (!string.IsNullOrEmpty(deliveryId) && !string.IsNullOrEmpty(_deliveriesTable))
        {
            var deliveryItem = await QuerySingleItemAsync(_deliveriesTable, "deliveryId", deliveryId);
            if (deliveryItem != null)
            {
                delivery = DeliveryFunction.MapToDeliveryDto(
                    DynamoDbHelper.FromItem(deliveryItem), includePhone: true, includeQrToken: true);
            }
        }

        // Also pull any delivery events anchored on the deliveryId (when the order
        // wasn't known at write time), deduped against the by-order query.
        if (txRepo != null && !string.IsNullOrEmpty(deliveryId))
        {
            var seen = new HashSet<string>(unifiedEvents.Select(e => GetString(e, "auditId")));
            var byDelivery = await txRepo.GetByEntityAsync(deliveryId);
            unifiedEvents.AddRange(byDelivery.Where(e => seen.Add(GetString(e, "auditId"))));
        }

        // The escrow audit trail is the real who/what/when for the payment leg.
        var auditEntries = new List<EscrowAuditEntryDto>();
        if (escrow != null && !string.IsNullOrEmpty(_escrowAuditTable))
        {
            var auditRepo = new EscrowAuditRepository(_dynamoDb, _escrowAuditTable);
            var auditItems = await auditRepo.GetAuditTrailAsync(escrow.EscrowId);
            auditEntries = auditItems.Select(MapToAuditEntry).ToList();
        }

        var response = new AdminTransactionDetailResponse
        {
            Order = order,
            Escrow = escrow,
            Delivery = delivery,
            Timeline = BuildTimeline(order, delivery, escrow, auditEntries, unifiedEvents)
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminTransactionDetailResponse);
    }

    /// <summary>Build the transaction-audit repository, or null when unconfigured.</summary>
    private ITransactionAuditRepository? TxAuditRepo() =>
        string.IsNullOrEmpty(_transactionAuditTable)
            ? null
            : new TransactionAuditRepository(_dynamoDb, _transactionAuditTable);

    /// <summary>
    /// GET /admin/audit — recent cross-entity audit feed (order/delivery/admin
    /// legs) newest-first, for the admin audit log. The escrow leg has its own
    /// per-escrow trail at /admin/escrows/{id}/audit.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAuditLogAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var txRepo = TxAuditRepo();
        var entries = new List<AdminAuditEntryDto>();
        if (txRepo != null)
        {
            var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
            var limit = ParseLimit(queryParams, 100);
            var items = await txRepo.GetRecentAsync(limit);
            entries = items.Select(MapToTransactionAuditEntry).ToList();
        }

        var response = new AdminAuditListResponse { Entries = entries, Total = entries.Count };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminAuditListResponse);
    }

    private static AdminAuditEntryDto MapToTransactionAuditEntry(Dictionary<string, object> item) =>
        new()
        {
            AuditId = GetString(item, "auditId"),
            TransactionId = GetString(item, "transactionId"),
            EntityType = GetString(item, "entityType"),
            EntityId = GetString(item, "entityId"),
            Action = GetString(item, "action"),
            FromState = GetStringOrNull(item, "fromState"),
            ToState = GetStringOrNull(item, "toState"),
            Actor = GetString(item, "actor"),
            ActorRole = GetStringOrNull(item, "actorRole"),
            IpAddress = GetStringOrNull(item, "ipAddress"),
            CorrelationId = GetStringOrNull(item, "correlationId"),
            Timestamp = GetString(item, "timestamp"),
            Metadata = GetStringOrNull(item, "metadata"),
        };

    /// <summary>
    /// Look up the escrow for an order via the OrderIndex GSI (sparse — only escrows
    /// created from an order carry orderId). Returns the newest match, or null.
    /// </summary>
    private async Task<Dictionary<string, object>?> GetEscrowByOrderIdAsync(string orderId)
    {
        if (string.IsNullOrEmpty(_escrowsTable))
        {
            return null;
        }

        var request = new QueryRequest
        {
            TableName = _escrowsTable,
            IndexName = "OrderIndex",
            KeyConditionExpression = "#orderId = :orderId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#orderId", "orderId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":orderId", new AttributeValue { S = orderId } }
            },
            Limit = 1,
            ScanIndexForward = false
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? DynamoDbHelper.FromItem(response.Items[0]) : null;
    }

    /// <summary>
    /// Map an order item to a transaction-monitor summary row. Names live on the
    /// order record, so no user-table enrichment is needed.
    /// </summary>
    private static AdminTransactionSummary MapToTransactionSummary(Dictionary<string, object> item)
    {
        var status = GetString(item, "status");
        return new AdminTransactionSummary
        {
            OrderId = GetString(item, "orderId"),
            Status = status,
            Stage = DeriveStage(status),
            BuyerId = GetString(item, "buyerId"),
            BuyerName = GetStringOrNull(item, "buyerName"),
            FarmerId = GetString(item, "farmerId"),
            FarmerName = GetStringOrNull(item, "farmerName"),
            TruckerId = GetStringOrNull(item, "truckerId"),
            TruckerName = GetStringOrNull(item, "truckerName"),
            CropType = GetString(item, "cropType"),
            Quantity = GetDoubleValue(item, "quantity"),
            OrderTotal = GetDoubleValue(item, "orderTotal"),
            Currency = "ETB",
            PaymentMethod = GetStringOrNull(item, "paymentMethod"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt")
        };
    }

    /// <summary>
    /// Collapse the fine-grained order status into a coarse lifecycle stage used to
    /// group/colour rows in the monitor. Kept in sync with the OrderDto state machine.
    /// </summary>
    private static string DeriveStage(string status) => status switch
    {
        "created" or "pending_payment" => "awaiting_payment",
        "payment_submitted" or "funded" or "accepted"
            or "pickup_confirmed" or "in_transit" or "disbursing" => "in_progress",
        "delivered" => "delivered",
        "settled" => "completed",
        "cancelled" => "cancelled",
        "payment_failed" or "disputed" or "dispute_resolved" => "problem",
        _ => "unknown"
    };

    /// <summary>
    /// Merge the order, delivery, and escrow-audit legs into one chronological
    /// timeline. ISO-8601 timestamps sort lexically; empty timestamps are dropped.
    /// </summary>
    private static List<AdminTransactionTimelineEvent> BuildTimeline(
        OrderDto order,
        DeliveryDto? delivery,
        EscrowDto? escrow,
        List<EscrowAuditEntryDto> auditEntries,
        List<Dictionary<string, object>> unifiedEvents)
    {
        var events = new List<AdminTransactionTimelineEvent>();

        void Add(
            string? timestamp,
            string entity,
            string action,
            string? toState = null,
            string? actor = null,
            string? detail = null,
            LatLngDto? location = null,
            string? telebirrRef = null,
            string? fromState = null,
            string? ipAddress = null)
        {
            if (string.IsNullOrEmpty(timestamp))
            {
                return;
            }

            events.Add(new AdminTransactionTimelineEvent
            {
                Timestamp = timestamp,
                Entity = entity,
                Action = action,
                FromState = fromState,
                ToState = toState,
                Actor = actor,
                TelebirrRef = telebirrRef,
                IpAddress = ipAddress,
                Location = location,
                Detail = detail
            });
        }

        // Unified audit events (Phase 2) — the real who/what/when/where for the
        // order/delivery/admin legs. When present these supersede the synthesised
        // reconstruction below; GPS is attached from the delivery record by action.
        // Gated per-entity (not one combined flag) so a transaction that straddles
        // the Phase-2 deploy — a pre-deploy order with only post-deploy delivery
        // events, or vice versa — still gets the synthesised history for the leg
        // that has no unified events yet.
        var hasUnifiedOrder = false;
        var hasUnifiedDelivery = false;
        foreach (var e in unifiedEvents)
        {
            var entity = GetString(e, "entityType");
            var action = GetString(e, "action");
            if (entity == "order") hasUnifiedOrder = true;
            if (entity == "delivery") hasUnifiedDelivery = true;

            LatLngDto? location = null;
            if (entity == "delivery" && delivery != null)
            {
                location = action == "picked_up" ? delivery.GpsPickup
                    : action == "delivered" ? delivery.GpsDropoff
                    : null;
            }

            Add(GetStringOrNull(e, "timestamp"), entity, action,
                toState: GetStringOrNull(e, "toState"),
                actor: GetStringOrNull(e, "actor"),
                detail: GetStringOrNull(e, "metadata"),
                location: location,
                fromState: GetStringOrNull(e, "fromState"),
                ipAddress: GetStringOrNull(e, "ipAddress"));
        }

        // Order leg synthesised from timestamp fields — only when the unified trail
        // has no order events (e.g. an order created before Phase 2).
        if (!hasUnifiedOrder)
        {
            Add(order.CreatedAt, "order", "order_created", "created", order.BuyerName ?? order.BuyerId);
            Add(order.PaymentVerifiedAt, "order", "payment_verified", "funded",
                order.BuyerName ?? order.BuyerId, detail: BuildPaymentDetail(order));
            Add(order.RespondedAt, "order", "farmer_responded", null,
                order.FarmerName ?? order.FarmerId, detail: order.RejectionReason);
            Add(order.CancelledAt, "order", "order_cancelled", "cancelled", detail: order.CancelReason);
        }

        // Delivery leg synthesised likewise — independent of the order leg.
        if (!hasUnifiedDelivery && delivery != null)
        {
            Add(delivery.CreatedAt, "delivery", "delivery_created", "pending", delivery.TruckerName);
            Add(delivery.PickedUpAt, "delivery", "picked_up", "pickedUp",
                delivery.TruckerName, location: delivery.GpsPickup);
            Add(delivery.ConfirmedAt, "delivery", "delivered", "delivered",
                delivery.BuyerName, location: delivery.GpsDropoff);
        }

        // Escrow / payment leg — always from the immutable escrow audit trail.
        foreach (var entry in auditEntries)
        {
            Add(entry.Timestamp, "escrow", entry.Action, entry.ToState,
                entry.Actor, telebirrRef: entry.TelebirrRef, fromState: entry.FromState,
                ipAddress: entry.IpAddress);
        }

        // Chronological. Parse timestamps so mixed ISO-8601 precisions (with/without
        // fractional seconds) still order correctly; OrderBy is stable, so ties keep
        // insertion order.
        return events.OrderBy(e => ParseTimestamp(e.Timestamp)).ToList();
    }

    /// <summary>Parse an ISO-8601 timestamp for sorting; unparseable → epoch (sorts first).</summary>
    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto
            : DateTimeOffset.MinValue;

    private static string? BuildPaymentDetail(OrderDto order)
    {
        if (!string.IsNullOrEmpty(order.CbeTransactionId))
        {
            return string.IsNullOrEmpty(order.PaymentMethod)
                ? order.CbeTransactionId
                : $"{order.PaymentMethod} · {order.CbeTransactionId}";
        }
        return order.PaymentMethod;
    }

    // ──────────────────────────────────────────────
    // DynamoDB Helpers
    // ──────────────────────────────────────────────

    private static string[] GetPathSegments(APIGatewayProxyRequest request)
    {
        var path = request.Path ?? "";
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int ParseLimit(IDictionary<string, string> queryParams, int defaultLimit)
    {
        if (queryParams.TryGetValue("limit", out var limitStr) &&
            int.TryParse(limitStr, out var limit) &&
            limit >= 1 && limit <= 100)
        {
            return limit;
        }
        return defaultLimit;
    }

    private async Task<int> ScanCountAsync(string tableName)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            return 0;
        }

        var request = new ScanRequest
        {
            TableName = tableName,
            Select = Select.COUNT
        };

        var count = 0;
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);
            count += response.Count;
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return count;
    }

    private async Task<int> ScanCountWithFilterAsync(string tableName, string filterField, string filterValue)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            return 0;
        }

        var request = new ScanRequest
        {
            TableName = tableName,
            Select = Select.COUNT,
            FilterExpression = "#field = :value",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#field", filterField }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":value", new AttributeValue { S = filterValue } }
            }
        };

        var count = 0;
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);
            count += response.Count;
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return count;
    }

    private async Task<int> ScanCountWithBoolFilterAsync(string tableName, string filterField, bool filterValue)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            return 0;
        }

        var request = new ScanRequest
        {
            TableName = tableName,
            Select = Select.COUNT,
            FilterExpression = "#field = :value",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#field", filterField }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":value", new AttributeValue { BOOL = filterValue } }
            }
        };

        var count = 0;
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);
            count += response.Count;
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return count;
    }

    private async Task<(decimal TotalVolume, decimal TotalCommissions)> ScanEscrowTotalsAsync()
    {
        if (string.IsNullOrEmpty(_escrowsTable))
        {
            return (0m, 0m);
        }

        var request = new ScanRequest
        {
            TableName = _escrowsTable,
            ProjectionExpression = "amount, commission"
        };

        var totalVolume = 0m;
        var totalCommissions = 0m;
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);

            foreach (var item in response.Items)
            {
                if (item.TryGetValue("amount", out var amountAttr) && amountAttr.N != null)
                {
                    if (decimal.TryParse(amountAttr.N, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                    {
                        totalVolume += amount;
                    }
                }
                if (item.TryGetValue("commission", out var commAttr) && commAttr.N != null)
                {
                    if (decimal.TryParse(commAttr.N, NumberStyles.Any, CultureInfo.InvariantCulture, out var commission))
                    {
                        totalCommissions += commission;
                    }
                }
            }

            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return (totalVolume, totalCommissions);
    }

    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastKey)> ScanItemsAsync(
        string tableName,
        string? filterValue,
        string filterField,
        int limit,
        Dictionary<string, AttributeValue>? startKey)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            return (new List<Dictionary<string, object>>(), null);
        }

        var request = new ScanRequest
        {
            TableName = tableName,
            Limit = limit
        };

        if (startKey != null)
        {
            request.ExclusiveStartKey = startKey;
        }

        if (!string.IsNullOrEmpty(filterValue))
        {
            request.FilterExpression = "#field = :value";
            request.ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#field", filterField }
            };
            request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":value", new AttributeValue { S = filterValue } }
            };
        }

        var response = await _dynamoDb.ScanAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        var lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;

        return (items, lastKey);
    }

    private async Task<Dictionary<string, AttributeValue>?> QuerySingleItemAsync(
        string tableName,
        string keyName,
        string keyValue)
    {
        var request = new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = $"{keyName} = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = keyValue } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Batch-fetch display names for a set of user IDs from the users table.
    /// Returns a dictionary mapping userId → displayName.
    /// </summary>
    private async Task<Dictionary<string, string>> BatchGetUserNamesAsync(IEnumerable<string> userIds)
    {
        var distinct = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var result = new Dictionary<string, string>();
        if (distinct.Count == 0) return result;

        // BatchGetItem supports max 100 keys per request
        foreach (var batch in distinct.Chunk(100))
        {
            var keys = batch.Select(id => new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = id } }
            }).ToList();

            var requestItems = new Dictionary<string, KeysAndAttributes>
            {
                {
                    _usersTable,
                    new KeysAndAttributes
                    {
                        Keys = keys,
                        ProjectionExpression = "userId, displayName"
                    }
                }
            };

            // BatchGetItem can return UnprocessedKeys on partial results (response-size
            // cap or throttling) WITHOUT throwing; the low-level client does not auto-retry.
            // Re-issue with the leftover keys (bounded) so name enrichment isn't silently dropped.
            var attempts = 0;
            while (requestItems.Count > 0 && attempts++ < 5)
            {
                var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = requestItems
                });

                if (response.Responses.TryGetValue(_usersTable, out var items))
                {
                    foreach (var item in items)
                    {
                        var id = item.TryGetValue("userId", out var uid) ? uid.S : "";
                        var name = item.TryGetValue("displayName", out var dn) ? dn.S : "";
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                            result[id] = name;
                    }
                }

                requestItems = response.UnprocessedKeys is { Count: > 0 }
                    ? response.UnprocessedKeys
                    : new Dictionary<string, KeysAndAttributes>();
            }
        }

        return result;
    }

    /// <summary>
    /// Enrich a list of EscrowDto with buyer/farmer display names from the users table.
    /// </summary>
    private async Task EnrichEscrowNamesAsync(List<EscrowDto> escrows)
    {
        var userIds = escrows.SelectMany(e => new[] { e.BuyerId, e.FarmerId });
        var names = await BatchGetUserNamesAsync(userIds);
        foreach (var e in escrows)
        {
            e.BuyerName ??= names.GetValueOrDefault(e.BuyerId);
            e.FarmerName ??= names.GetValueOrDefault(e.FarmerId);
        }
    }

    // ──────────────────────────────────────────────
    // DTO Mapping
    // ──────────────────────────────────────────────

    private static EscrowDto MapToEscrowDto(Dictionary<string, object> item)
    {
        return new EscrowDto
        {
            EscrowId = GetString(item, "escrowId"),
            DeliveryId = GetString(item, "deliveryId"),
            OrderId = GetStringOrNull(item, "orderId"),
            BuyerId = GetString(item, "buyerId"),
            BuyerName = GetStringOrNull(item, "buyerName"),
            FarmerId = GetString(item, "farmerId"),
            FarmerName = GetStringOrNull(item, "farmerName"),
            Amount = GetDecimal(item, "amount"),
            Commission = GetDecimal(item, "commission"),
            CommissionRate = GetDecimal(item, "commissionRate"),
            FarmerPayout = GetDecimal(item, "farmerPayout"),
            Currency = GetString(item, "currency", "ETB"),
            Status = GetString(item, "status"),
            TelebirrRef = GetStringOrNull(item, "telebirrRef"),
            TelebirrHoldRef = GetStringOrNull(item, "telebirrHoldRef"),
            TelebirrReleaseRef = GetStringOrNull(item, "telebirrReleaseRef"),
            TelebirrRefundRef = GetStringOrNull(item, "telebirrRefundRef"),
            Dispute = null,
            BuyerFaydaVerified = GetBool(item, "buyerFaydaVerified"),
            FarmerFaydaVerified = GetBool(item, "farmerFaydaVerified"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            ExpiresAt = GetString(item, "expiresAt"),
            DeliveryConfirmedAt = GetStringOrNull(item, "deliveryConfirmedAt"),
            DisputeWindowEndsAt = GetStringOrNull(item, "disputeWindowEndsAt"),
            ReleasedAt = GetStringOrNull(item, "releasedAt"),
            RefundedAt = GetStringOrNull(item, "refundedAt"),
            CompletedAt = GetStringOrNull(item, "completedAt"),
            Version = GetInt(item, "version", 1)
        };
    }

    private static EscrowAuditEntryDto MapToAuditEntry(Dictionary<string, object> item)
    {
        return new EscrowAuditEntryDto
        {
            AuditId = GetString(item, "auditId"),
            EscrowId = GetString(item, "escrowId"),
            Action = GetString(item, "action"),
            FromState = GetString(item, "fromState"),
            ToState = GetString(item, "toState"),
            Actor = GetString(item, "actor"),
            TelebirrRef = GetStringOrNull(item, "telebirrRef"),
            Timestamp = GetString(item, "timestamp"),
            IpAddress = GetStringOrNull(item, "ipAddress")
        };
    }

    private static FraudSignalDto MapToFraudSignalDto(Dictionary<string, object> item)
    {
        return new FraudSignalDto
        {
            SignalId = GetString(item, "signalId"),
            UserId = GetString(item, "userId"),
            SignalType = GetString(item, "signalType"),
            Severity = GetString(item, "severity"),
            Description = GetString(item, "description"),
            Status = GetString(item, "status"),
            EntityId = GetStringOrNull(item, "entityId"),
            EntityType = GetStringOrNull(item, "entityType"),
            ReviewedBy = GetStringOrNull(item, "reviewedBy"),
            ReviewedAt = GetStringOrNull(item, "reviewedAt"),
            ReviewNotes = GetStringOrNull(item, "reviewNotes"),
            CreatedAt = GetString(item, "createdAt")
        };
    }

    private static string GetString(Dictionary<string, object> item, string key, string defaultValue = "")
    {
        return item.TryGetValue(key, out var val) ? val?.ToString() ?? defaultValue : defaultValue;
    }

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
    {
        return item.TryGetValue(key, out var val) ? val?.ToString() : null;
    }

    private static decimal GetDecimal(Dictionary<string, object> item, string key)
    {
        if (item.TryGetValue(key, out var val) && val != null)
        {
            if (val is decimal d)
            {
                return d;
            }

            if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return 0m;
    }

    private static int GetInt(Dictionary<string, object> item, string key, int defaultValue = 0)
    {
        if (item.TryGetValue(key, out var val) && val != null)
        {
            if (val is int i)
            {
                return i;
            }

            if (int.TryParse(val.ToString(), out var parsed))
            {
                return parsed;
            }
        }
        return defaultValue;
    }

    private static double GetDoubleValue(Dictionary<string, object> item, string key)
    {
        if (item.TryGetValue(key, out var val) && val != null)
        {
            if (val is double d)
            {
                return d;
            }

            if (double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return 0.0;
    }

    private static bool GetBool(Dictionary<string, object> item, string key)
    {
        if (item.TryGetValue(key, out var val) && val != null)
        {
            if (val is bool b)
            {
                return b;
            }

            if (bool.TryParse(val.ToString(), out var parsed))
            {
                return parsed;
            }
        }
        return false;
    }
}
