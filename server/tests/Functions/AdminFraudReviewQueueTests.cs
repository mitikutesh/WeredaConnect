using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-07 (fraud-detection-architecture.md sections 14, 18, 19 and 20): Fraud Review reads the sparse
/// <c>StatusCreatedAtIndex</c> of the fraud-signals table instead of scanning it, one status partition at a
/// time, newest first, with the risk score of each user on the page; the dashboard counts pending signals and
/// held escrows on the indexes; the held queue reads the <c>held</c> partition of the escrows'
/// <c>StatusExpiresIndex</c>; the admin user detail carries the suspend reason.
/// </summary>
public class AdminFraudReviewQueueTests
{
    private const string UsersTable = "users-test";
    private const string FraudTable = "fraud-signals-test";
    private const string EscrowsTable = "escrows-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string FraudIndex = "StatusCreatedAtIndex";
    private const string EscrowIndex = "StatusExpiresIndex";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    /// <summary>The pages each index answers with, in turn; an index without any answers empty.</summary>
    private readonly Dictionary<string, Queue<QueryResponse>> _indexPages = new();

    /// <summary>The rows the risk score Query of each user answers with (the table's own keys, no index).</summary>
    private readonly Dictionary<string, List<Dictionary<string, AttributeValue>>> _scoreRows = new();

    /// <summary>Users whose risk score Query fails.</summary>
    private readonly HashSet<string> _scoreFails = new();

    /// <summary>The display names the users table answers with.</summary>
    private readonly Dictionary<string, string> _names = new();

    /// <summary>What the users table answers for a GetItem.</summary>
    private Dictionary<string, AttributeValue>? _userRow;

    public AdminFraudReviewQueueTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<QueryRequest>(0);
                if (query.IndexName == null && query.TableName == FraudTable)
                {
                    var userId = query.ExpressionAttributeValues[":uid"].S;
                    if (_scoreFails.Contains(userId))
                    {
                        return Task.FromException<QueryResponse>(new ProvisionedThroughputExceededException("throttled"));
                    }

                    return Task.FromResult(Page(null, _scoreRows.GetValueOrDefault(userId)?.ToArray() ?? []));
                }

                return Task.FromResult(
                    query.IndexName != null && _indexPages.TryGetValue(query.IndexName, out var pages) && pages.Count > 0
                        ? pages.Dequeue()
                        : Page(null));
            });
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });
        _ddb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var keys = ci.ArgAt<BatchGetItemRequest>(0).RequestItems[UsersTable].Keys;
                var rows = keys
                    .Select(k => k["userId"].S)
                    .Where(_names.ContainsKey)
                    .Select(id => new Dictionary<string, AttributeValue>
                    {
                        { "userId", new AttributeValue { S = id } },
                        { "displayName", new AttributeValue { S = _names[id] } }
                    })
                    .ToList();
                return Task.FromResult(new BatchGetItemResponse
                {
                    Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>> { { UsersTable, rows } },
                    UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
                });
            });
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new GetItemResponse { Item = _userRow }));
    }

    private AdminFunction Function() =>
        new(_ddb, UsersTable, FraudTable, cognitoAuth: null, escrowsTable: EscrowsTable, transactionAuditTable: TxAuditTable);

    private static APIGatewayProxyRequest Get(string path, Dictionary<string, string>? query = null) => new()
    {
        HttpMethod = "GET",
        Path = path,
        QueryStringParameters = query,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private Task<APIGatewayProxyResponse> GetAsync(string path, Dictionary<string, string>? query = null) =>
        Function().FunctionHandler(Get(path, query), _context);

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private static string ErrorCode(APIGatewayProxyResponse response) =>
        Json(response).GetProperty("error").GetProperty("code").GetString()!;

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private void IndexPages(string index, params QueryResponse[] pages) => _indexPages[index] = new Queue<QueryResponse>(pages);

    private List<QueryRequest> Queries => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<QueryRequest>().ToList();

    private List<QueryRequest> IndexQueries(string index) => Queries.Where(q => q.IndexName == index).ToList();

    private List<QueryRequest> ScoreQueries => Queries.Where(q => q.IndexName == null && q.TableName == FraudTable).ToList();

    private List<ScanRequest> Scans => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<ScanRequest>().ToList();

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> SignalRow(
        string userId, string type, DateTime at, string status = "pending", string? entityType = null, string? entityId = null)
    {
        var hour = new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0, DateTimeKind.Utc);
        var signalKey = $"{Iso(hour)}#{type}";
        var row = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = userId } },
            { "signalKey", new AttributeValue { S = signalKey } },
            { "signalId", new AttributeValue { S = FraudDetectionService.BuildSignalId(userId, signalKey) } },
            { "signalType", new AttributeValue { S = type } },
            { "severity", new AttributeValue { S = "high" } },
            { "weight", new AttributeValue { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) } },
            { "description", new AttributeValue { S = "d" } },
            { "status", new AttributeValue { S = status } },
            { "entityType", new AttributeValue { S = entityType ?? "crop" } },
            { "createdAt", new AttributeValue { S = Iso(at) } }
        };
        if (entityId != null)
        {
            row["entityId"] = new AttributeValue { S = entityId };
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> IndexKey(Dictionary<string, AttributeValue> row) => new()
    {
        { "userId", row["userId"] },
        { "signalKey", row["signalKey"] },
        { "status", row["status"] },
        { "createdAt", row["createdAt"] }
    };

    private static Dictionary<string, AttributeValue> HeldEscrow(string escrowId, string expiresAt) => new()
    {
        { "escrowId", new AttributeValue { S = escrowId } },
        { "createdAt", new AttributeValue { S = "2026-10-01T10:00:00Z" } },
        { "updatedAt", new AttributeValue { S = "2026-10-20T10:00:00Z" } },
        { "expiresAt", new AttributeValue { S = expiresAt } },
        { "status", new AttributeValue { S = "held" } },
        { "buyerId", new AttributeValue { S = "buyer-1" } },
        { "farmerId", new AttributeValue { S = "farmer-1" } },
        { "orderId", new AttributeValue { S = "ord-9" } },
        { "amount", new AttributeValue { N = "5000" } },
        { "holdReason", new AttributeValue { S = "fraud_confirmed" } },
        { "heldAt", new AttributeValue { S = "2026-10-20T10:00:00Z" } },
        { "heldBy", new AttributeValue { S = "system" } },
        { "heldFromStatus", new AttributeValue { S = "escrowed" } },
        { "heldFor", new AttributeValue { S = "release" } }
    };

    // ──────────────────────────────────────────────
    // The Fraud Review list
    // ──────────────────────────────────────────────

    [Fact]
    public async Task List_QueriesThePendingPartitionOfTheStatusIndex_NewestFirst_AndNeverScans()
    {
        IndexPages(FraudIndex, Page(null, SignalRow("farmer-1", "PRICE_ANOMALY", DateTime.UtcNow.AddHours(-1))));

        var response = await GetAsync("/admin/fraud-signals");

        response.StatusCode.Should().Be(200, response.Body);
        Scans.Should().BeEmpty();
        var query = IndexQueries(FraudIndex).Single();
        query.TableName.Should().Be(FraudTable);
        query.KeyConditionExpression.Should().Be("#status = :status");
        query.ExpressionAttributeNames["#status"].Should().Be("status");
        query.ExpressionAttributeValues[":status"].S.Should().Be("pending");
        query.ScanIndexForward.Should().BeFalse();
        query.Limit.Should().Be(20);
        // The index holds signal rows only (credit and whitelist rows carry no status); the filter is a second guard.
        query.FilterExpression.Should().Be("(attribute_not_exists(#recordType) OR #recordType = :signalRecord)");
        query.ExpressionAttributeValues[":signalRecord"].S.Should().Be("signal");
        Json(response).GetProperty("signals").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_Reviewed_QueriesTheReviewedPartition()
    {
        var response = await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "status", "reviewed" } });

        response.StatusCode.Should().Be(200, response.Body);
        IndexQueries(FraudIndex).Single().ExpressionAttributeValues[":status"].S.Should().Be("reviewed");
        Json(response).GetProperty("signals").GetArrayLength().Should().Be(0);
        Json(response).GetProperty("users").GetArrayLength().Should().Be(0);
        ScoreQueries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("all")]
    [InlineData("credit")]
    public async Task List_AnotherStatus_Returns400Admin005_AndReadsNothing(string status)
    {
        var response = await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "status", status } });

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("ADMIN_005");
        Queries.Should().BeEmpty();
        Scans.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ThePageIsCappedAtFifty()
    {
        await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "limit", "100" } });

        IndexQueries(FraudIndex).Single().Limit.Should().Be(50);
    }

    [Fact]
    public async Task List_APageTheFilterThinned_ReadsOnForTheMissingRowsOnly()
    {
        var first = SignalRow("farmer-1", "PRICE_ANOMALY", DateTime.UtcNow.AddHours(-1));
        var second = SignalRow("farmer-2", "GPS_ANOMALY", DateTime.UtcNow.AddHours(-2));
        IndexPages(FraudIndex,
            Page(IndexKey(first), first),
            Page(null, second));

        var response = await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "limit", "3" } });

        var queries = IndexQueries(FraudIndex);
        queries.Select(q => q.Limit).Should().Equal(3, 2);
        queries[1].ExclusiveStartKey.Should().BeEquivalentTo(IndexKey(first));
        Json(response).GetProperty("signals").GetArrayLength().Should().Be(2);
        Json(response).TryGetProperty("nextToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task List_TheNextTokenResumesRightAfterTheLastRow()
    {
        var row = SignalRow("farmer-1", "PRICE_ANOMALY", DateTime.UtcNow.AddHours(-1));
        IndexPages(FraudIndex, Page(IndexKey(row), row));

        var first = await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "limit", "1" } });
        var token = Json(first).GetProperty("nextToken").GetString()!;
        await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "limit", "1" }, { "nextToken", token } });

        IndexQueries(FraudIndex)[1].ExclusiveStartKey.Should().BeEquivalentTo(IndexKey(row));
    }

    [Fact]
    public async Task List_ATokenOfTheOtherStatusOrIndexOrForged_Returns400Admin005_AndReadsNothing()
    {
        var reviewed = SignalRow("farmer-1", "PRICE_ANOMALY", DateTime.UtcNow, status: "reviewed");
        var reviewedToken = PaginationTokenHelper.EncodeNextToken(IndexKey(reviewed));
        var escrowToken = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            { "escrowId", new AttributeValue { S = "e" } },
            { "createdAt", new AttributeValue { S = "c" } },
            { "status", new AttributeValue { S = "pending" } },
            { "expiresAt", new AttributeValue { S = "x" } }
        });

        foreach (var token in new[] { reviewedToken, escrowToken, "forged.token" })
        {
            var response = await GetAsync("/admin/fraud-signals", new Dictionary<string, string> { { "nextToken", token } });

            response.StatusCode.Should().Be(400);
            ErrorCode(response).Should().Be("ADMIN_005");
        }

        Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task List_GroupsByUser_WithEachUsersRiskScoreAndTier_ReadOncePerUser()
    {
        var now = DateTime.UtcNow;
        IndexPages(FraudIndex, Page(null,
            SignalRow("farmer-1", "GPS_ANOMALY", now.AddHours(-1)),
            SignalRow("buyer-2", "PRICE_ANOMALY", now.AddHours(-2)),
            SignalRow("farmer-1", "FRAUD_DUPLICATE_LISTING", now.AddHours(-3))));
        // The score reads the user's whole window, not the page: 25 + 20 + 15 = 60 is HIGH.
        _scoreRows["farmer-1"] = new List<Dictionary<string, AttributeValue>>
        {
            SignalRow("farmer-1", "GPS_ANOMALY", now.AddHours(-1)),
            SignalRow("farmer-1", "FRAUD_DUPLICATE_LISTING", now.AddHours(-3)),
            SignalRow("farmer-1", "FRAUD_VELOCITY_EXCEEDED", now.AddDays(-2))
        };
        _scoreRows["buyer-2"] = new List<Dictionary<string, AttributeValue>>
        {
            SignalRow("buyer-2", "PRICE_ANOMALY", now.AddHours(-2))
        };
        _names["farmer-1"] = "Abebe";

        var response = await GetAsync("/admin/fraud-signals");

        response.StatusCode.Should().Be(200, response.Body);
        ScoreQueries.Select(q => q.ExpressionAttributeValues[":uid"].S).Should().Equal("farmer-1", "buyer-2");
        var users = Json(response).GetProperty("users").EnumerateArray().ToList();
        users.Select(u => u.GetProperty("userId").GetString()).Should().Equal("farmer-1", "buyer-2");
        users[0].GetProperty("displayName").GetString().Should().Be("Abebe");
        users[0].GetProperty("riskScore").GetInt32().Should().Be(60);
        users[0].GetProperty("enforcementScore").GetInt32().Should().Be(60);
        users[0].GetProperty("riskTier").GetString().Should().Be("high");
        users[0].GetProperty("scoreAvailable").GetBoolean().Should().BeTrue();
        users[0].GetProperty("truncated").GetBoolean().Should().BeFalse();
        users[1].TryGetProperty("displayName", out _).Should().BeFalse();
        users[1].GetProperty("riskScore").GetInt32().Should().Be(10);
        users[1].GetProperty("riskTier").GetString().Should().Be("low");
    }

    [Fact]
    public async Task List_AScoreThatCannotBeRead_IsShownUnavailable_NotLow_AndThePageStillAnswers()
    {
        IndexPages(FraudIndex, Page(null, SignalRow("farmer-1", "GPS_ANOMALY", DateTime.UtcNow.AddHours(-1))));
        _scoreFails.Add("farmer-1");

        var response = await GetAsync("/admin/fraud-signals");

        response.StatusCode.Should().Be(200, response.Body);
        var user = Json(response).GetProperty("users")[0];
        user.GetProperty("scoreAvailable").GetBoolean().Should().BeFalse();
        Json(response).GetProperty("signals").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_ASignalOnAnOrderOrAnOrderEscrow_LinksItsTransaction()
    {
        var now = DateTime.UtcNow;
        IndexPages(FraudIndex, Page(null,
            SignalRow("buyer-1", "AMOUNT_ANOMALY", now.AddHours(-1), entityType: "order", entityId: "ord-1"),
            SignalRow("buyer-1", "CANCEL_AFTER_FUND_RATE", now.AddHours(-2), entityType: "escrow", entityId: "escrow-order-ord-2"),
            SignalRow("buyer-1", "TELEBIRR_PAYER_MISMATCH", now.AddHours(-3), entityType: "escrow", entityId: "escrow-3f2a"),
            SignalRow("trucker-1", "GPS_ANOMALY", now.AddHours(-4), entityType: "delivery", entityId: "del-1")));

        var response = await GetAsync("/admin/fraud-signals");

        var orderIds = Json(response).GetProperty("signals").EnumerateArray()
            .Select(s => s.TryGetProperty("orderId", out var id) ? id.GetString() : null)
            .ToList();
        orderIds.Should().Equal("ord-1", "ord-2", null, null);
    }

    [Fact]
    public async Task List_WithoutTheTable_AnswersAnEmptyList()
    {
        var response = await new AdminFunction(_ddb, UsersTable).FunctionHandler(Get("/admin/fraud-signals"), _context);

        response.StatusCode.Should().Be(200);
        Json(response).GetProperty("signals").GetArrayLength().Should().Be(0);
        Queries.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // Dashboard
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_CountsPendingSignalsAndHeldEscrowsOnTheIndexes_WithoutScanningTheSignals()
    {
        IndexPages(FraudIndex,
            new QueryResponse { Count = 7, LastEvaluatedKey = new Dictionary<string, AttributeValue> { { "userId", new AttributeValue { S = "u" } } } },
            new QueryResponse { Count = 2, LastEvaluatedKey = new Dictionary<string, AttributeValue>() });
        IndexPages(EscrowIndex, new QueryResponse { Count = 3, LastEvaluatedKey = new Dictionary<string, AttributeValue>() });

        var response = await GetAsync("/admin/dashboard/stats");

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).GetProperty("fraudSignalsCount").GetInt32().Should().Be(9);
        Json(response).GetProperty("heldEscrows").GetInt32().Should().Be(3);
        Scans.Should().NotContain(s => s.TableName == FraudTable);

        var signalCounts = IndexQueries(FraudIndex);
        signalCounts.Should().HaveCount(2);
        signalCounts.Should().OnlyContain(q => q.Select == Select.COUNT
            && q.TableName == FraudTable
            && q.ExpressionAttributeValues[":status"].S == "pending"
            && q.FilterExpression == "(attribute_not_exists(#recordType) OR #recordType = :signalRecord)");
        signalCounts[1].ExclusiveStartKey["userId"].S.Should().Be("u");
        var heldCount = IndexQueries(EscrowIndex).Single();
        heldCount.Select.Should().Be(Select.COUNT);
        heldCount.TableName.Should().Be(EscrowsTable);
        heldCount.ExpressionAttributeValues[":status"].S.Should().Be("held");
    }

    // ──────────────────────────────────────────────
    // The held queue
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HeldQueue_QueriesTheHeldPartitionOfStatusExpiresIndex_OldestFirst_WithTheHoldFieldsAndNames()
    {
        IndexPages(EscrowIndex, Page(null, HeldEscrow("escrow-order-ord-9", "2026-10-08T10:00:00Z")));
        _names["buyer-1"] = "Meron";
        _names["farmer-1"] = "Abebe";

        var response = await GetAsync("/admin/escrows/held");

        response.StatusCode.Should().Be(200, response.Body);
        Scans.Should().BeEmpty();
        var query = IndexQueries(EscrowIndex).Single();
        query.TableName.Should().Be(EscrowsTable);
        query.KeyConditionExpression.Should().Be("#status = :status");
        query.ExpressionAttributeValues[":status"].S.Should().Be("held");
        query.ScanIndexForward.Should().BeTrue();
        query.Limit.Should().Be(20);
        query.FilterExpression.Should().BeNull();

        var escrow = Json(response).GetProperty("escrows")[0];
        escrow.GetProperty("escrowId").GetString().Should().Be("escrow-order-ord-9");
        escrow.GetProperty("orderId").GetString().Should().Be("ord-9");
        escrow.GetProperty("holdReason").GetString().Should().Be("fraud_confirmed");
        escrow.GetProperty("heldFor").GetString().Should().Be("release");
        escrow.GetProperty("heldFromStatus").GetString().Should().Be("escrowed");
        escrow.GetProperty("buyerName").GetString().Should().Be("Meron");
        escrow.GetProperty("farmerName").GetString().Should().Be("Abebe");
    }

    [Fact]
    public async Task HeldQueue_PagesWithATokenOfItsOwnIndexOnly()
    {
        var row = HeldEscrow("escrow-order-ord-9", "2026-10-08T10:00:00Z");
        var key = new Dictionary<string, AttributeValue>
        {
            { "escrowId", row["escrowId"] }, { "createdAt", row["createdAt"] }, { "status", row["status"] }, { "expiresAt", row["expiresAt"] }
        };
        IndexPages(EscrowIndex, Page(key, row));

        var first = await GetAsync("/admin/escrows/held");
        var token = Json(first).GetProperty("nextToken").GetString()!;
        await GetAsync("/admin/escrows/held", new Dictionary<string, string> { { "nextToken", token } });
        IndexQueries(EscrowIndex)[1].ExclusiveStartKey.Should().BeEquivalentTo(key);

        var signalToken = PaginationTokenHelper.EncodeNextToken(IndexKey(SignalRow("u", "GPS_ANOMALY", DateTime.UtcNow)));
        var refused = await GetAsync("/admin/escrows/held", new Dictionary<string, string> { { "nextToken", signalToken } });
        refused.StatusCode.Should().Be(400);
        ErrorCode(refused).Should().Be("ADMIN_005");
        IndexQueries(EscrowIndex).Should().HaveCount(2);
    }

    [Fact]
    public async Task HeldQueue_WithoutTheEscrowsTable_Returns503()
    {
        var response = await new AdminFunction(_ddb, UsersTable, FraudTable).FunctionHandler(Get("/admin/escrows/held"), _context);

        response.StatusCode.Should().Be(503);
        ErrorCode(response).Should().Be("ADMIN_004");
        Queries.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // The user detail
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UserDetail_CarriesTheSuspendReason_NextToTheUser_NeverInsideIt()
    {
        _userRow = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = "farmer-1" } },
            { "phoneNumber", new AttributeValue { S = "+251911000001" } },
            { "role", new AttributeValue { S = "farmer" } },
            { "woreda", new AttributeValue { S = "Bishoftu" } },
            { "isPhoneVerified", new AttributeValue { BOOL = true } },
            { "suspended", new AttributeValue { BOOL = true } },
            { "suspendReason", new AttributeValue { S = "fraud_confirmed" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-10-23T00:00:00Z" } },
            { "version", new AttributeValue { N = "3" } }
        };

        var response = await GetAsync("/admin/users/farmer-1");

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).GetProperty("suspendReason").GetString().Should().Be("fraud_confirmed");
        var user = Json(response).GetProperty("user");
        user.GetProperty("suspended").GetBoolean().Should().BeTrue();
        user.TryGetProperty("suspendReason", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UserDetail_WithoutASuspendReason_LeavesItOut()
    {
        _userRow = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = "farmer-1" } },
            { "phoneNumber", new AttributeValue { S = "+251911000001" } },
            { "role", new AttributeValue { S = "farmer" } },
            { "woreda", new AttributeValue { S = "Bishoftu" } },
            { "isPhoneVerified", new AttributeValue { BOOL = true } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-10-23T00:00:00Z" } },
            { "version", new AttributeValue { N = "3" } }
        };

        var response = await GetAsync("/admin/users/farmer-1");

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).TryGetProperty("suspendReason", out _).Should().BeFalse();
    }
}
