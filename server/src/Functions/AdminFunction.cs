using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

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
/// - GET  /admin/payouts/manual         → manual payout queue (ESC-02)
/// - POST /admin/payouts/{escrowId}/{ref}/complete → record an admin-paid leg (admin group only)
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

    // Account controls that must reach Cognito (disable, global sign-out, role
    // attribute); null in tests that only exercise DynamoDB behaviour.
    private readonly ICognitoAuthService? _cognitoAuth;

    public AdminFunction()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID");
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID");
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(userPoolId))
        {
            _cognitoAuth = new CognitoAuthService(
                new Amazon.CognitoIdentityProvider.AmazonCognitoIdentityProviderClient(), clientId, userPoolId);
        }
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
    internal AdminFunction(IAmazonDynamoDB dynamoDb, string usersTable, string fraudSignalsTable = "",
        ICognitoAuthService? cognitoAuth = null)
    {
        _dynamoDb = dynamoDb;
        _usersTable = usersTable;
        _cognitoAuth = cognitoAuth;
        _escrowsTable = "";
        _escrowAuditTable = "";
        _transactionAuditTable = "";
        _fraudSignalsTable = fraudSignalsTable;
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
                ("GET", ["admin", "payouts", "manual"]) => await HandleListManualPayoutsAsync(request, context),
                ("POST", ["admin", "payouts", _, _, "complete"]) => await HandleCompleteManualPayoutAsync(segments[2], segments[3], request, context),
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
        // ESC-03: the sweep settles a verdict within the hour, so recording one is a money
        // action and takes the admin group, like the manual payout queue.
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return LambdaResponse.Error(403, "ESCROW_015", "Only an admin in the admin group can resolve a dispute.");
        }

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

        // ESC-03: the verdict decides which way the money goes, so only the two verdicts with
        // a settlement are accepted, and the action must say the same thing.
        var verdictAction = body.Resolution switch
        {
            "farmer_wins" => "release",
            "buyer_wins" => "refund",
            _ => null
        };
        if (verdictAction == null)
        {
            return LambdaResponse.Error(400, "ADMIN_014",
                body.Resolution == "partial_refund"
                    ? "partial_refund is not supported until per-leg settlement. Choose farmer_wins or buyer_wins."
                    : "resolution must be farmer_wins or buyer_wins.",
                "resolution");
        }

        if (!string.Equals(body.Action, verdictAction, StringComparison.Ordinal))
        {
            return LambdaResponse.Error(400, "ADMIN_014",
                $"action must be '{verdictAction}' for {body.Resolution}.", "action");
        }

        // FUP-16: a disposition belongs to one verdict and always carries the admin's reason.
        var disposition = string.IsNullOrEmpty(body.Disposition) ? null : body.Disposition;
        string? dispositionReason = null;
        if (disposition != null)
        {
            var dispositionVerdict = DisputeDisposition.VerdictOf(disposition);
            if (dispositionVerdict == null)
            {
                return LambdaResponse.Error(400, "ADMIN_014",
                    "disposition must be return_to_farmer, delivered_anyway, settle_now or wait_for_delivery.", "disposition");
            }

            if (dispositionVerdict != body.Resolution)
            {
                return LambdaResponse.Error(400, "ADMIN_014",
                    $"disposition '{disposition}' goes with {dispositionVerdict}, not {body.Resolution}.", "disposition");
            }

            dispositionReason = body.Reason?.Trim() ?? "";
            if (dispositionReason.Length is < 10 or > 500)
            {
                return LambdaResponse.Error(400, "ADMIN_014",
                    "reason must be 10 to 500 characters with a disposition.", "reason");
            }
        }

        var item = await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId);
        if (item == null)
        {
            return LambdaResponse.Error(404, "ADMIN_003", "Escrow not found.");
        }

        var now = DateTime.UtcNow.ToString("o");
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";

        var createdAt = item.TryGetValue("createdAt", out var ca) ? ca.S : "";

        // FUP-16: decide the order and the delivery before the verdict is written. A verdict they
        // cannot follow as they are is recorded only with a disposition that resolves it, so no new
        // verdict waits on a mismatch. An escrow already decided goes to the replay below, and one with a
        // queued payout or a settlement in flight to the 409 answers of the conditional write.
        var mirror = VerdictMirror();
        string? applyDisposition = null;
        string? mismatchedLegs = null;
        var dispositionIgnored = false;
        if (item.TryGetValue("status", out var currentStatusAttr) && currentStatusAttr.S == "disputed"
            && !item.ContainsKey(ManualPayoutFields.Marker) && !item.ContainsKey("settlementLock"))
        {
            var plan = await mirror.PlanDispositionAsync(DynamoDbHelper.FromItem(item), body.Resolution);
            if (plan.Case == DispositionCase.Unknown)
            {
                return LambdaResponse.Error(503, "ESCROW_012",
                    "The order or delivery behind this escrow could not be read. Nothing was recorded; try again.");
            }

            if (plan.Case != DispositionCase.NotNeeded)
            {
                if (DispositionRefusal(plan, disposition, body.Resolution, replay: false) is { } refusal)
                {
                    return refusal;
                }

                applyDisposition = disposition;
                mismatchedLegs = plan.Legs;
            }
            else
            {
                dispositionIgnored = disposition != null;
            }
        }

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
            // transitioning held/released/refunded/already-resolved escrows. ESC-02: a
            // settlement already waiting in the manual payout queue cannot be contradicted
            // by a verdict; the admin settles or clears the queue first. ESC-03: nor can a
            // settlement in flight, whose own status write would then fail after paying.
            ConditionExpression = "#status = :disputed AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lock)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updatedAt", "updatedAt" },
                { "#resolution", "disputeResolution" },
                { "#resolvedBy", "disputeResolvedBy" },
                { "#resolvedAt", "disputeResolvedAt" },
                { "#manualPayout", ManualPayoutFields.Marker },
                { "#lock", "settlementLock" }
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
        if (applyDisposition != null)
        {
            // FUP-16: the verdict and its disposition are recorded in the same conditional write.
            updateRequest.UpdateExpression += ", #disposition = :disposition, #dispositionReason = :dispositionReason, "
                                              + "#dispositionBy = :resolvedBy, #dispositionAt = :resolvedAt";
            updateRequest.ExpressionAttributeNames["#disposition"] = DisputeDispositionFields.Disposition;
            updateRequest.ExpressionAttributeNames["#dispositionReason"] = DisputeDispositionFields.Reason;
            updateRequest.ExpressionAttributeNames["#dispositionBy"] = DisputeDispositionFields.By;
            updateRequest.ExpressionAttributeNames["#dispositionAt"] = DisputeDispositionFields.At;
            updateRequest.ExpressionAttributeValues[":disposition"] = new AttributeValue { S = applyDisposition };
            updateRequest.ExpressionAttributeValues[":dispositionReason"] = new AttributeValue { S = dispositionReason };
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(updateRequest);
        }
        catch (ConditionalCheckFailedException)
        {
            // Strongly consistent, so a double click sees the verdict the first click wrote.
            var latest = await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId, consistentRead: true);
            var latestStatus = latest != null && latest.TryGetValue("status", out var ls) ? ls.S : null;
            var recorded = latest != null && latest.TryGetValue("disputeResolution", out var lr) ? lr.S : null;

            // ESC-03: the same verdict again is a replay. Nothing new is recorded; the order and
            // delivery are re-mirrored, which repairs a leg the first attempt could not write.
            // FUP-16: a replay also records or repairs a disposition.
            if (latest != null && latestStatus != "disputed" && recorded == body.Resolution)
            {
                return await ReplayVerdictAsync(
                    latest, body.Resolution, disposition, dispositionReason, adminUserId, now, request, context);
            }

            if (latest != null && latest.TryGetValue(ManualPayoutFields.Marker, out var marker)
                && marker.S == ManualPayoutFields.MarkerOpen)
            {
                return LambdaResponse.Error(409, "ADMIN_013",
                    "A payout for this escrow is waiting in the manual payout queue. Settle it there first.");
            }

            if (latest != null && latestStatus == "disputed" && latest.ContainsKey("settlementLock"))
            {
                return LambdaResponse.Error(409, "ADMIN_010",
                    "A settlement is in progress for this escrow.");
            }

            return LambdaResponse.Error(409, "ADMIN_010",
                recorded != null
                    ? "A different verdict is already recorded for this escrow."
                    : "Escrow is not in a disputed state.");
        }

        // Every escrow state change gets an escrow-audit entry (escrow-protocol.md
        // audit-trail rule). The settlement itself is audited by EscrowService when
        // the cron releases or refunds.
        if (!string.IsNullOrEmpty(_escrowAuditTable))
        {
            try
            {
                await new EscrowAuditRepository(_dynamoDb, _escrowAuditTable).AppendAsync(new Dictionary<string, object>
                {
                    { "auditId", $"audit-{Guid.NewGuid():N}" },
                    { "escrowId", escrowId },
                    { "action", "admin_dispute_resolved" },
                    { "fromState", "disputed" },
                    { "toState", "dispute_resolved" },
                    { "actor", adminUserId },
                    { "timestamp", now },
                    { "metadata", body.Resolution }
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Escrow audit write failed for dispute resolution: [{ex.GetType().Name}]");
            }
        }

        var disputeOrderId = item.TryGetValue("orderId", out var doid) && doid.S != null ? doid.S : escrowId;
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, disputeOrderId, "escrow", escrowId,
            "admin_dispute_resolved", "disputed", "dispute_resolved",
            AuditContext.From(request, adminUserId, "admin"), metadata: body.Resolution);
        AuditLogger.LogAudit("ADMIN", "RESOLVE_DISPUTE",
            userId: adminUserId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "escrow", entityId: escrowId, outcome: "success");

        if (applyDisposition != null)
        {
            return await ApplyDispositionAsync(mirror, item, body.Resolution, applyDisposition, dispositionReason!, mismatchedLegs,
                dispositionAt: now, resolvedAt: now, "dispute_resolved", adminUserId, request, context);
        }

        // ESC-03: the order and delivery follow the verdict now; the settlement repairs a leg
        // that fails here.
        await MirrorVerdictAsync(item, body.Resolution, now, adminUserId, context);

        var response = new ResolveDisputeResponse
        {
            EscrowId = escrowId,
            Status = "dispute_resolved",
            Resolution = body.Resolution,
            ResolvedAt = now,
            Message = (body.Resolution == "farmer_wins"
                    ? "Verdict recorded. The settlement sweep releases the funds to the farmer within the hour."
                    : "Verdict recorded. The settlement sweep refunds the buyer within the hour.")
                + (dispositionIgnored ? " No disposition was needed: the order can follow the verdict." : "")
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.ResolveDisputeResponse);
    }

    /// <summary>
    /// The same verdict again. ESC-03: nothing new is recorded and the legs are mirrored again.
    /// FUP-16: on a verdict not settled yet whose legs cannot follow it, the disposition in the request
    /// (or the one already recorded) is applied: a new choice is recorded on the escrow first
    /// (conditional on no settlement in flight), and one already recorded is re-applied, which finishes a
    /// run that stopped half way. Without any disposition such a verdict (recorded before 1.21.0) answers
    /// <c>422 ADMIN_015</c> so the admin chooses one.
    /// </summary>
    private async Task<APIGatewayProxyResponse> ReplayVerdictAsync(
        Dictionary<string, AttributeValue> latest, string resolution, string? disposition, string? dispositionReason,
        string adminUserId, string now, APIGatewayProxyRequest request, ILambdaContext context)
    {
        var escrowId = latest.TryGetValue("escrowId", out var eid) ? eid.S : "";
        var latestStatus = latest.TryGetValue("status", out var ls) ? ls.S : null;
        var recordedAt = latest.TryGetValue("disputeResolvedAt", out var lra) && lra.S != null ? lra.S : now;
        var recordedDisposition = latest.TryGetValue(DisputeDispositionFields.Disposition, out var rd) ? rd.S : null;
        if (latestStatus == "dispute_resolved")
        {
            var mirror = VerdictMirror();
            var plan = await mirror.PlanDispositionAsync(DynamoDbHelper.FromItem(latest), resolution);
            if (plan.Case == DispositionCase.Unknown)
            {
                return LambdaResponse.Error(503, "ESCROW_012",
                    "The order or delivery behind this escrow could not be read. Nothing was changed; try again.");
            }

            if (plan.Case != DispositionCase.NotNeeded)
            {
                var chosen = disposition ?? recordedDisposition;
                if (DispositionRefusal(plan, chosen, resolution, replay: true) is { } refusal)
                {
                    return refusal;
                }

                var reason = disposition != null
                    ? dispositionReason!
                    : latest.TryGetValue(DisputeDispositionFields.Reason, out var rr) ? rr.S ?? "" : "";
                var dispositionAt = latest.TryGetValue(DisputeDispositionFields.At, out var rat) && rat.S != null ? rat.S : recordedAt;
                if (disposition != null && disposition != recordedDisposition)
                {
                    if (!await RecordDispositionAsync(latest, resolution, disposition, recordedDisposition, reason, adminUserId, now))
                    {
                        return LambdaResponse.Error(409, "ADMIN_010",
                            "The verdict can no longer take this disposition: a settlement is in progress or done, or another "
                            + "admin recorded a choice meanwhile. Reload the page.");
                    }

                    dispositionAt = now;
                }

                return await ApplyDispositionAsync(mirror, latest, resolution, chosen!, reason, plan.Legs, dispositionAt,
                    recordedAt, latestStatus, adminUserId, request, context);
            }
        }

        await MirrorVerdictAsync(latest, resolution, recordedAt, adminUserId, context);
        return LambdaResponse.Ok(new ResolveDisputeResponse
        {
            EscrowId = escrowId,
            Status = latestStatus ?? "dispute_resolved",
            Resolution = resolution,
            ResolvedAt = recordedAt,
            Disposition = recordedDisposition,
            Message = "Verdict already recorded."
        }, AppJsonContext.Default.ResolveDisputeResponse);
    }

    /// <summary>
    /// FUP-16: why a verdict whose legs cannot follow it is not recorded (or not changed) with
    /// <paramref name="disposition"/>; null when the disposition resolves it. Nothing is written on a refusal.
    /// </summary>
    private static APIGatewayProxyResponse? DispositionRefusal(
        DispositionPlan plan, string? disposition, string resolution, bool replay)
    {
        if (plan.Case == DispositionCase.NoFit)
        {
            // A recorded verdict cannot change; before it is recorded the admin can still pick the other one.
            return LambdaResponse.Error(422, "ADMIN_016",
                $"The order or delivery behind this verdict cannot follow {resolution} ({plan.Legs}) and no disposition fits. "
                + (replay
                    ? "Nothing was changed. The verdict is recorded and cannot change: the order must be repaired by hand "
                      + "first (for buyer_wins, the deprecated overrideOrderMismatch refund still settles it)."
                    : "Nothing was recorded: bring the order to a status the verdict can follow, or choose the other verdict."));
        }

        if (disposition == null)
        {
            return LambdaResponse.Error(422, "ADMIN_015", plan.Case == DispositionCase.GoodsMoving
                ? "The goods are on the way, so the order cannot follow buyer_wins as it is. Nothing was changed: "
                  + "choose a disposition (return_to_farmer or delivered_anyway) with a reason."
                : "The order is not delivered, so it cannot follow farmer_wins as it is. Nothing was changed: "
                  + "choose a disposition (wait_for_delivery" + (plan.GoodsHandedOver ? " or settle_now" : "") + ") with a reason.",
                "disposition");
        }

        if (!plan.Fits(disposition))
        {
            return LambdaResponse.Error(422, "ADMIN_016",
                disposition == DisputeDisposition.SettleNow && !plan.GoodsHandedOver
                    ? "settle_now needs the goods picked up; before pickup choose wait_for_delivery. Nothing was changed."
                    : $"{disposition} does not fit this order ({plan.Legs}). Nothing was changed.",
                "disposition");
        }

        return null;
    }

    /// <summary>
    /// FUP-16: record a new disposition on a verdict that is not settled yet. Conditional on the escrow
    /// still being <c>dispute_resolved</c> with this verdict, with no manual payout and no settlement lock,
    /// and still carrying the disposition this request read (<paramref name="previous"/>, or none), so two
    /// admins choosing at once cannot overwrite each other unseen. False when the condition fails.
    /// </summary>
    private async Task<bool> RecordDispositionAsync(
        Dictionary<string, AttributeValue> escrow, string resolution, string disposition, string? previous, string reason,
        string adminUserId, string now)
    {
        var values = new Dictionary<string, AttributeValue>
        {
            { ":disposition", new AttributeValue { S = disposition } },
            { ":reason", new AttributeValue { S = reason } },
            { ":by", new AttributeValue { S = adminUserId } },
            { ":now", new AttributeValue { S = now } },
            { ":resolved", new AttributeValue { S = "dispute_resolved" } },
            { ":resolution", new AttributeValue { S = resolution } }
        };
        var unchanged = "attribute_not_exists(#disposition)";
        if (previous != null)
        {
            values[":previous"] = new AttributeValue { S = previous };
            unchanged = "#disposition = :previous";
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _escrowsTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "escrowId", escrow["escrowId"] },
                    { "createdAt", escrow["createdAt"] }
                },
                UpdateExpression = "SET #disposition = :disposition, #dispositionReason = :reason, #dispositionBy = :by, "
                                   + "#dispositionAt = :now, #updatedAt = :now",
                ConditionExpression = "#status = :resolved AND #resolution = :resolution "
                                      + "AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lock) AND " + unchanged,
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#disposition", DisputeDispositionFields.Disposition },
                    { "#dispositionReason", DisputeDispositionFields.Reason },
                    { "#dispositionBy", DisputeDispositionFields.By },
                    { "#dispositionAt", DisputeDispositionFields.At },
                    { "#updatedAt", "updatedAt" },
                    { "#status", "status" },
                    { "#resolution", "disputeResolution" },
                    { "#manualPayout", ManualPayoutFields.Marker },
                    { "#lock", "settlementLock" }
                },
                ExpressionAttributeValues = values
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>
    /// FUP-16: audit the admin's disposition, then move the legs (<see cref="DisputeVerdictMirror.ApplyDispositionAsync"/>).
    /// The escrow-audit row comes first, at a deterministic key (the choice time plus four ticks, conditional
    /// put), with a transaction-audit row when it is new; if it cannot be written nothing moves (503). A leg
    /// that does not move answers 503 too: the verdict and the disposition stand, and the same request again
    /// finishes the job. The settlement sweep never applies a disposition; it only finds the legs following.
    /// </summary>
    private async Task<APIGatewayProxyResponse> ApplyDispositionAsync(
        DisputeVerdictMirror mirror, Dictionary<string, AttributeValue> escrowItem, string resolution, string disposition,
        string reason, string? legs, string dispositionAt, string resolvedAt, string? escrowStatus, string adminUserId,
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var escrowId = escrowItem.TryGetValue("escrowId", out var eid) ? eid.S : "";
        var escrow = DynamoDbHelper.FromItem(escrowItem);
        var action = DisputeDisposition.AuditAction(disposition);
        if (!string.IsNullOrEmpty(_escrowAuditTable))
        {
            try
            {
                var timestamp = DisputeVerdictMirror.VerdictAuditTimestamp(dispositionAt, action);
                var created = await new EscrowAuditRepository(_dynamoDb, _escrowAuditTable).AppendIfAbsentAsync(
                    new Dictionary<string, object>
                    {
                        { "auditId", EscrowAuditRepository.DeterministicAuditId(action, timestamp) },
                        { "escrowId", escrowId },
                        { "action", action },
                        { "fromState", "dispute_resolved" },
                        { "toState", "dispute_resolved" },
                        { "actor", adminUserId },
                        { "timestamp", timestamp },
                        { "metadata", $"disposition={disposition};reason={reason};legs={legs}" }
                    });
                if (created)
                {
                    var orderId = escrowItem.TryGetValue("orderId", out var oid) && !string.IsNullOrEmpty(oid.S) ? oid.S : escrowId;
                    await TransactionAuditWriter.EmitAsync(TxAuditRepo(), context, orderId, "escrow", escrowId,
                        action, "dispute_resolved", "dispute_resolved", AuditContext.From(request, adminUserId, "admin"),
                        metadata: $"disposition={disposition};reason={reason}");
                    context.Logger.LogInformation(
                        $"DISPUTE_DISPOSITION_RECORDED escrowId={MoneyAlarmLog.LogId(escrowId)} admin={MoneyAlarmLog.LogId(adminUserId)} disposition={disposition}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"DISPUTE_DISPOSITION_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
                return LambdaResponse.Error(503, "ESCROW_012",
                    "The verdict is recorded, but its disposition could not be audited. Nothing moved: send the same verdict again.");
            }
        }

        var result = await mirror.ApplyDispositionAsync(escrow, resolution, disposition, resolvedAt, context);
        if (result.DispositionNotNeeded)
        {
            // The legs moved since the verdict was decided (a handoff landed) and now follow it: the
            // plain verdict mirror applies. The choice stays recorded and audited, unused.
            await MirrorVerdictAsync(escrowItem, resolution, resolvedAt, adminUserId, context);
            return LambdaResponse.Ok(new ResolveDisputeResponse
            {
                EscrowId = escrowId,
                Status = escrowStatus ?? "dispute_resolved",
                Resolution = resolution,
                ResolvedAt = resolvedAt,
                Disposition = disposition,
                Message = "Verdict recorded. The order can now follow the verdict as it is, so the disposition was not needed; "
                          + "the settlement sweep settles it within the hour."
            }, AppJsonContext.Default.ResolveDisputeResponse);
        }

        if (result.FailedLegs != null)
        {
            context.Logger.LogWarning(
                $"DISPUTE_DISPOSITION_INCOMPLETE escrowId={MoneyAlarmLog.LogId(escrowId)} disposition={disposition} legs={MoneyAlarmLog.LogText(result.FailedLegs)}");
            return LambdaResponse.Error(503, "ESCROW_012",
                $"The verdict and its disposition are recorded, but the order or delivery did not move ({result.FailedLegs}). "
                + "Nothing was settled: send the same verdict again to finish.");
        }

        return LambdaResponse.Ok(new ResolveDisputeResponse
        {
            EscrowId = escrowId,
            Status = escrowStatus ?? "dispute_resolved",
            Resolution = resolution,
            ResolvedAt = resolvedAt,
            Disposition = disposition,
            Message = disposition switch
            {
                DisputeDisposition.ReturnToFarmer =>
                    "Verdict recorded. The delivery is cancelled and the goods go back to the farmer; the settlement sweep refunds the buyer within the hour.",
                DisputeDisposition.DeliveredAnyway =>
                    "Verdict recorded. The goods count as delivered to the buyer; the settlement sweep refunds the buyer within the hour.",
                DisputeDisposition.SettleNow =>
                    "Verdict recorded. The delivery is cancelled; the settlement sweep releases the funds to the farmer within the hour.",
                _ => "Verdict recorded. The funds are released to the farmer once the order is delivered."
            }
        }, AppJsonContext.Default.ResolveDisputeResponse);
    }

    /// <summary>The verdict mirror over this Lambda's tables (a missing table is a leg with nothing to mirror).</summary>
    private DisputeVerdictMirror VerdictMirror() => new(
        string.IsNullOrEmpty(_ordersTable) ? null : new OrderRepository(_dynamoDb, _ordersTable),
        string.IsNullOrEmpty(_deliveriesTable) ? null : new DeliveryRepository(_dynamoDb, _deliveriesTable),
        TxAuditRepo());

    /// <summary>
    /// ESC-03: move the order and delivery behind a dispute verdict to it
    /// (<see cref="DisputeVerdictMirror"/>). Best-effort: the verdict itself stands. A leg that
    /// does not reach the verdict is logged as <c>DISPUTE_VERDICT_PROPAGATION_FAILED</c> and
    /// written to the escrow audit ledger once per escrow and action (at the verdict time plus
    /// one or two ticks, shared with the settlement), as <c>dispute_verdict_mismatch</c> when it
    /// cannot follow (the settlement then waits until an admin fixes the order) or
    /// <c>dispute_verdict_propagation_failed</c> when a read or write failed, with
    /// <c>metadata = legs=...</c>. A <c>buyer_wins</c> order before pickup is left to the refund
    /// settlement, which cancels it.
    /// </summary>
    private async Task MirrorVerdictAsync(
        Dictionary<string, AttributeValue> escrowItem, string resolution, string resolvedAt,
        string actor, ILambdaContext context)
    {
        var escrowId = escrowItem.TryGetValue("escrowId", out var eid) ? eid.S : null;
        try
        {
            var result = await VerdictMirror().ApplyAsync(DynamoDbHelper.FromItem(escrowItem), resolution, resolvedAt, context);
            if (result.FailedLegs == null)
            {
                return;
            }

            context.Logger.LogWarning(
                $"DISPUTE_VERDICT_PROPAGATION_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(result.OrderId)} deliveryId={MoneyAlarmLog.LogId(result.DeliveryId)} legs={MoneyAlarmLog.LogText(result.FailedLegs)}");
            if (!string.IsNullOrEmpty(_escrowAuditTable) && !string.IsNullOrEmpty(escrowId))
            {
                var now = DateTime.UtcNow;
                var timestamp = DisputeVerdictMirror.VerdictAuditTimestamp(resolvedAt, result.AuditAction);
                await new EscrowAuditRepository(_dynamoDb, _escrowAuditTable).AppendIfAbsentAsync(new Dictionary<string, object>
                {
                    { "auditId", EscrowAuditRepository.DeterministicAuditId(result.AuditAction, timestamp) },
                    { "escrowId", escrowId },
                    { "action", result.AuditAction },
                    { "fromState", "dispute_resolved" },
                    { "toState", "dispute_resolved" },
                    { "actor", actor },
                    { "timestamp", timestamp },
                    { "metadata", $"legs={result.FailedLegs};at={now:o}" }
                });
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"DISPUTE_VERDICT_PROPAGATION_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }
    }

    // ──────────────────────────────────────────────
    // Manual payout queue (ESC-02)
    // ──────────────────────────────────────────────

    /// <summary>
    /// The escrow service without gateway or idempotency wiring: the queue endpoints never
    /// call Telebirr. Null when the escrow tables are not configured. The order and delivery
    /// repositories find the trucker of an escrow that has none (ESC-04), and a closed payout moves
    /// its order after the money, audited in transaction-audit (ESC-09).
    /// </summary>
    private EscrowService? ManualPayoutService()
    {
        if (string.IsNullOrEmpty(_escrowsTable) || string.IsNullOrEmpty(_escrowAuditTable))
        {
            return null;
        }

        return new EscrowService(
            new EscrowRepository(_dynamoDb, _escrowsTable),
            new EscrowAuditRepository(_dynamoDb, _escrowAuditTable),
            idempotencyRepo: null,
            telebirrGateway: null,
            userRepo: string.IsNullOrEmpty(_usersTable) ? null : new UserRepository(_dynamoDb, _usersTable),
            orderRepo: string.IsNullOrEmpty(_ordersTable) ? null : new OrderRepository(_dynamoDb, _ordersTable),
            deliveryRepo: string.IsNullOrEmpty(_deliveriesTable) ? null : new DeliveryRepository(_dynamoDb, _deliveriesTable),
            transactionAuditRepo: string.IsNullOrEmpty(_transactionAuditTable)
                ? null
                : new TransactionAuditRepository(_dynamoDb, _transactionAuditTable));
    }

    /// <summary>GET /admin/payouts/manual?limit=&nextToken= (oldest first).</summary>
    private async Task<APIGatewayProxyResponse> HandleListManualPayoutsAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var service = ManualPayoutService();
        if (service == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrow tables not configured.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        queryParams.TryGetValue("nextToken", out var nextToken);
        var limit = Math.Min(ParseLimit(queryParams, 20), 50);

        var result = await service.ListManualPayoutsAsync(limit, nextToken);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        return LambdaResponse.Ok(result.Value!, AppJsonContext.Default.ManualPayoutListResponse);
    }

    /// <summary>
    /// POST /admin/payouts/{escrowId}/{ref}/complete with {externalRef}. Money-affecting, so
    /// it requires the admin pool's admin group (a role claim alone gets ESCROW_015).
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleCompleteManualPayoutAsync(
        string escrowId, string leg, APIGatewayProxyRequest request, ILambdaContext context)
    {
        var service = ManualPayoutService();
        if (service == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrow tables not configured.");
        }

        CompleteManualPayoutRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.CompleteManualPayoutRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        if (!IsSafeEscrowId(escrowId) || !ManualPayoutFields.IsLeg(leg))
        {
            return LambdaResponse.Error(404, "ESCROW_035", "No such payout leg.");
        }

        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var adminGroupVerified = AdminAuthHelper.IsInAdminGroup(request);
        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var correlationId = AuditLogger.GetCorrelationId(request);

        var result = await service.CompleteManualPayoutAsync(
            escrowId, leg, body.ExternalRef, adminUserId, adminGroupVerified, auditContext);

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            AuditLogger.LogAudit("ADMIN", "MANUAL_PAYOUT",
                userId: adminUserId, correlationId: correlationId,
                entityType: "escrow", entityId: escrowId, outcome: "failure", errorCode: error.Code);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var value = result.Value!;
        // A replay of an already recorded leg moved nothing: no second transaction-audit row.
        if (!value.AlreadyRecorded)
        {
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, value.OrderId ?? escrowId, "escrow", escrowId,
                "admin_manual_payout", null, value.EscrowStatus, auditContext,
                metadata: $"leg={leg};ref={value.ExternalRef}");
        }

        AuditLogger.LogAudit("ADMIN", "MANUAL_PAYOUT",
            userId: adminUserId, correlationId: correlationId,
            entityType: "escrow", entityId: escrowId, outcome: value.AlreadyRecorded ? "replay" : "success",
            detail: $"leg={leg};escrowStatus={value.EscrowStatus}");

        return LambdaResponse.Ok(value, AppJsonContext.Default.CompleteManualPayoutResponse);
    }

    /// <summary>Escrow ids are server-generated: 1 to 128 of [A-Za-z0-9_-].</summary>
    private static bool IsSafeEscrowId(string escrowId)
        => escrowId.Length is >= 1 and <= 128
           && escrowId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

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

        // The path id encodes the table's composite key (userId + signalKey).
        if (!Services.FraudDetectionService.TryParseSignalId(signalId, out var signalUserId, out var signalKey))
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid fraud signal id.");
        }

        var now = DateTime.UtcNow.ToString("o");
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";

        // status becomes "reviewed" (the admin UI keys on that value); the
        // chosen verb (approve/reject/whitelist) is kept in "resolution".
        var updateRequest = new UpdateItemRequest
        {
            TableName = _fraudSignalsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = signalUserId } },
                { "signalKey", new AttributeValue { S = signalKey } }
            },
            ConditionExpression = "attribute_exists(userId)",
            UpdateExpression = "SET #status = :status, #resolution = :resolution, #reviewedBy = :reviewedBy, #reviewedAt = :reviewedAt, #reviewNotes = :notes",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#resolution", "resolution" },
                { "#reviewedBy", "reviewedBy" },
                { "#reviewedAt", "reviewedAt" },
                { "#reviewNotes", "reviewNotes" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = "reviewed" } },
                { ":resolution", new AttributeValue { S = body.Action } },
                { ":reviewedBy", new AttributeValue { S = adminUserId } },
                { ":reviewedAt", new AttributeValue { S = now } },
                { ":notes", body.Notes != null ? new AttributeValue { S = body.Notes } : new AttributeValue { NULL = true } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(updateRequest);
        }
        catch (ConditionalCheckFailedException)
        {
            return LambdaResponse.Error(404, "ADMIN_006", "Fraud signal not found.");
        }

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
            Status = "reviewed",
            ReviewedAt = now,
            Message = $"Fraud signal reviewed successfully ({body.Action})."
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

        // The DynamoDB flag alone left refreshed and live tokens working: push the
        // change into Cognito so sessions actually end (or the new role is issued).
        var cognitoNote = await ApplyAccountControlAsync(userId, body.Action, attrValues, context);

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
            Message = cognitoNote == null
                ? $"User action '{body.Action}' completed successfully."
                : $"User action '{body.Action}' recorded, but {cognitoNote}"
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.UserActionResponse);
    }

    /// <summary>
    /// suspend: disable the Cognito user and revoke every refresh token (live tokens
    /// die within 15 minutes). unsuspend: re-enable. change_role: rewrite custom:role
    /// and sign the user out so no token with the old role outlives the change.
    /// Returns a note for the admin when Cognito could not be updated; the profile
    /// row already carries the change, so the action is not rolled back.
    /// </summary>
    private async Task<string?> ApplyAccountControlAsync(
        string userId, string action, Dictionary<string, AttributeValue> attrValues, ILambdaContext context)
    {
        if (action is not ("suspend" or "unsuspend" or "change_role"))
        {
            return null;
        }

        if (_cognitoAuth == null)
        {
            context.Logger.LogWarning($"Cognito account control unavailable for user_{action} on {userId}.");
            return "the sign-in service was not updated (no Cognito client configured).";
        }

        var phone = await ReadUserPhoneAsync(userId);
        if (string.IsNullOrEmpty(phone))
        {
            return "the sign-in service was not updated (profile has no phone number).";
        }

        var failures = new List<string>();
        async Task Apply(string what, Task<AuthResult<bool>> call)
        {
            var result = await call;
            if (!result.IsSuccess)
            {
                failures.Add(what);
                AuditLogger.LogSecurity("ADMIN_ACCOUNT_CONTROL_FAILED", userId: userId,
                    detail: $"{action}:{what}:{result.Error!.Code}", outcome: "failure");
            }
        }

        switch (action)
        {
            case "suspend":
                await Apply("disable", _cognitoAuth.AdminDisableUserAsync(phone));
                await Apply("sign-out", _cognitoAuth.AdminGlobalSignOutAsync(phone));
                break;
            case "unsuspend":
                await Apply("enable", _cognitoAuth.AdminEnableUserAsync(phone));
                break;
            case "change_role":
                var newRole = attrValues.TryGetValue(":newRole", out var roleAttr) ? roleAttr.S : null;
                if (!string.IsNullOrEmpty(newRole))
                {
                    await Apply("role", _cognitoAuth.AdminSetRoleAsync(phone, newRole));
                }
                await Apply("sign-out", _cognitoAuth.AdminGlobalSignOutAsync(phone));
                break;
        }

        return failures.Count == 0
            ? null
            : $"the sign-in service update failed for: {string.Join(", ", failures)}.";
    }

    private async Task<string?> ReadUserPhoneAsync(string userId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { { "userId", new AttributeValue { S = userId } } },
            ProjectionExpression = "phoneNumber"
        });
        return response.Item != null && response.Item.TryGetValue("phoneNumber", out var phone) ? phone.S : null;
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
        // "completed" is the terminal settled state (contract §Status Definitions): every leg is
        // paid. "released" means the farmer is paid; it stays released while the trucker leg is
        // still owed (ESC-04), so such an escrow counts here only once that leg is paid.
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
        // Terminal settled state is "completed", not "released" (the farmer is paid but a
        // trucker leg may still be owed, ESC-04).
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
    /// group/colour rows in the monitor. Kept in sync with the OrderDto state machine and
    /// admin/src/lib/transaction.ts. ESC-09: the order follows the money, so <c>disbursing</c>
    /// (paying out, possibly days in Manual Payouts) and <c>dispute_resolved</c> (decided) get
    /// stages of their own, and <c>rejected</c> ends like a cancel.
    /// </summary>
    internal static string DeriveStage(string status) => status switch
    {
        "created" or "pending_payment" => "awaiting_payment",
        "payment_submitted" or "funded" or "accepted"
            or "pickup_confirmed" or "in_transit" => "in_progress",
        "delivered" => "delivered",
        "disbursing" => "paying_out",
        "settled" => "completed",
        "cancelled" or "rejected" => "cancelled",
        "dispute_resolved" => "resolved",
        "payment_failed" or "disputed" => "problem",
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

        var items = new List<Dictionary<string, object>>();
        var lastKey = startKey;
        var pages = 0;
        do
        {
            var request = new ScanRequest
            {
                TableName = tableName,
                Limit = limit
            };

            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
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
            items.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
            pages++;
        }
        // FUP-16: DynamoDB applies the filter after the Limit, so one page of a filtered scan can hold
        // few matches or none. Keep reading until `limit` matched (the last page may add up to
        // limit - 1 more), the table ends, or the page budget is spent; the nextToken resumes after the
        // last page read, so nothing is skipped or repeated.
        while (!string.IsNullOrEmpty(filterValue) && items.Count < limit && lastKey != null && pages < MaxFilteredScanPages);

        return (items, lastKey);
    }

    /// <summary>The most Scan pages (of <c>limit</c> rows each) one filtered admin list request reads.</summary>
    private const int MaxFilteredScanPages = 25;

    private async Task<Dictionary<string, AttributeValue>?> QuerySingleItemAsync(
        string tableName,
        string keyName,
        string keyValue,
        bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = $"{keyName} = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = keyValue } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
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
        // Build the nested dispute from the flat fields so the admin Disputes list
        // shows a Reason. Previously Dispute was hardcoded null, so the client's
        // row.dispute.reason was always empty. Mirrors EscrowService.MapToEscrowDto.
        DisputeDto? dispute = null;
        var disputeReason = GetStringOrNull(item, "disputeReason");
        var disputeId = GetStringOrNull(item, "disputeId");
        if (disputeReason != null || disputeId != null)
        {
            var evidenceRefsRaw = GetStringOrNull(item, "disputeEvidenceRefs");
            var evidenceRefs = string.IsNullOrEmpty(evidenceRefsRaw)
                ? null
                : evidenceRefsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            dispute = new DisputeDto
            {
                DisputeId = disputeId ?? "",
                EscrowId = GetString(item, "escrowId"),
                DisputedBy = GetString(item, "disputedBy"),
                Reason = disputeReason ?? "",
                Description = GetStringOrNull(item, "disputeDescription"),
                EvidenceRefs = evidenceRefs,
                Resolution = GetStringOrNull(item, "disputeResolution"),
                ResolvedBy = GetStringOrNull(item, "disputeResolvedBy"),
                ResolvedAt = GetStringOrNull(item, "disputeResolvedAt"),
                CreatedAt = GetString(item, "disputeCreatedAt"),
                Disposition = GetStringOrNull(item, DisputeDispositionFields.Disposition),
                DispositionReason = GetStringOrNull(item, DisputeDispositionFields.Reason),
                DispositionBy = GetStringOrNull(item, DisputeDispositionFields.By),
                DispositionAt = GetStringOrNull(item, DisputeDispositionFields.At)
            };
        }

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
            Dispute = dispute,
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
