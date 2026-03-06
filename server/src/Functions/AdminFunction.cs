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
/// </summary>
public class AdminFunction
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _escrowsTable;
    private readonly string _escrowAuditTable;
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
        _fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        _usersTable = Environment.GetEnvironmentVariable("USERS_TABLE") ?? "";
        _cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
        _tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE") ?? "";
        _ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE") ?? "";
        _deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE") ?? "";
        _paymentsTable = Environment.GetEnvironmentVariable("PAYMENTS_TABLE") ?? "";
    }

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
        var farmersCount = await ScanCountWithFilterAsync(_usersTable, "role", "Farmer");
        var truckersCount = await ScanCountWithFilterAsync(_usersTable, "role", "Trucker");
        var buyersCount = await ScanCountWithFilterAsync(_usersTable, "role", "Buyer");
        var escrowsCount = await ScanCountAsync(_escrowsTable);
        var activeEscrows = await ScanCountWithFilterAsync(_escrowsTable, "status", "held");
        var disputedEscrows = await ScanCountWithFilterAsync(_escrowsTable, "status", "disputed");
        var ordersCount = await ScanCountAsync(_ordersTable);
        var activeListings = await ScanCountWithFilterAsync(_cropsTable, "status", "active");
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
        var auditItems = await auditRepo.GetAuditTrailAsync(escrowId);
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
                { ":resolvedAt", new AttributeValue { S = now } }
            }
        };

        await _dynamoDb.UpdateItemAsync(updateRequest);

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
                updateParts.Add("#role = :newRole");
                attrNames["#role"] = "role";
                attrValues[":newRole"] = new AttributeValue { S = body.NewRole };
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
        var completedCount = await ScanCountWithFilterAsync(_escrowsTable, "status", "released");

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
        var completed = await ScanCountWithFilterAsync(_escrowsTable, "status", "released");
        var pending = await ScanCountWithFilterAsync(_escrowsTable, "status", "held");
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
            FarmerId = GetString(item, "farmerId"),
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
