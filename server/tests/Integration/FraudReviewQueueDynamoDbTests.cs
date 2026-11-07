using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>FRD-07: its own tables, so the status partitions and the counts hold this class's rows only.</summary>
[CollectionDefinition(Name)]
public sealed class FraudReviewQueueDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "FraudReviewQueueDynamoDb";
}

/// <summary>
/// FRD-07 against real DynamoDB (LocalStack in CI): the sparse <c>StatusCreatedAtIndex</c> holds the signal rows
/// the writer makes and never a credit or whitelist row (they carry no <c>status</c>); a review moves a row from the
/// <c>pending</c> to the <c>reviewed</c> partition; the held queue reads the <c>held</c> partition of
/// <c>StatusExpiresIndex</c>; the dashboard counts both on the indexes.
/// </summary>
[Collection(FraudReviewQueueDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FraudReviewQueueDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly TestLambdaContext _context = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public FraudReviewQueueDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private AdminFunction Admin() =>
        new(Ddb, _db.Users, _db.FraudSignals, cognitoAuth: null, escrowsTable: _db.Escrows,
            transactionAuditTable: _db.TransactionAudit);

    private FraudDetectionService Signals() =>
        new(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>()) { AlarmLog = _ => { } };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static APIGatewayProxyRequest Request(string method, string path, Dictionary<string, string>? query = null, string? body = null) => new()
    {
        HttpMethod = method,
        Path = path,
        QueryStringParameters = query,
        Body = body,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    /// <summary>Every row of a status partition, through the admin list's pages.</summary>
    private async Task<(List<JsonElement> Signals, List<JsonElement> Users)> ListAsync(string status)
    {
        var signals = new List<JsonElement>();
        var users = new List<JsonElement>();
        string? token = null;
        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string> { { "status", status }, { "limit", "2" } };
            if (token != null)
            {
                query["nextToken"] = token;
            }

            var response = await Admin().FunctionHandler(Request("GET", "/admin/fraud-signals", query), _context);
            response.StatusCode.Should().Be(200, response.Body);
            var json = Json(response);
            signals.AddRange(json.GetProperty("signals").EnumerateArray().Select(e => e.Clone()));
            users.AddRange(json.GetProperty("users").EnumerateArray().Select(e => e.Clone()));
            token = json.TryGetProperty("nextToken", out var next) ? next.GetString() : null;
            if (token == null)
            {
                break;
            }
        }

        return (signals, users);
    }

    [LocalDynamoDbFact]
    public async Task TheIndexHoldsSignalRowsOnly_AndFraudReviewReadsThemWithTheirUsersScore()
    {
        var user = $"farmer-frd07-{_run}";
        var now = DateTime.UtcNow;
        await Signals().RecordFraudSignalAsync(user, "GPS_ANOMALY", $"del-{_run}", "delivery", "same pickup and drop-off");
        await Signals().RecordFraudSignalAsync(user, "FRAUD_DUPLICATE_LISTING", $"crop-{_run}", "crop", "duplicate");
        // A credit row (section 20): createdAt, no status.
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.FraudSignals,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new() { S = user },
                ["signalKey"] = new() { S = $"{Iso(now.AddHours(-1))}#CLEAN_DELIVERY#escrow-order-{_run}" },
                ["recordType"] = new() { S = "credit" },
                ["signalType"] = new() { S = "CLEAN_DELIVERY" },
                ["weight"] = new() { N = "-5" },
                ["entityType"] = new() { S = "escrow" },
                ["entityId"] = new() { S = $"escrow-order-{_run}" },
                ["createdAt"] = new() { S = Iso(now) }
            }
        });
        // The whitelist row (FRD-05): no status. Active, so the soft GPS_ANOMALY does not count.
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.FraudSignals,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new() { S = user },
                ["signalKey"] = new() { S = "~whitelist" },
                ["recordType"] = new() { S = "whitelist" },
                ["whitelistUntil"] = new() { S = Iso(now.AddDays(10)) },
                ["reviewedAt"] = new() { S = Iso(now) }
            }
        });

        var (signals, users) = await ListAsync("pending");

        var mine = signals.Where(s => s.GetProperty("userId").GetString() == user).ToList();
        mine.Select(s => s.GetProperty("signalType").GetString())
            .Should().BeEquivalentTo("GPS_ANOMALY", "FRAUD_DUPLICATE_LISTING");
        // Newest first: the two writes landed in this order.
        mine.Select(s => s.GetProperty("createdAt").GetString()).Should().BeInDescendingOrder();
        // 20 (duplicate) minus 5 (credit); the whitelisted soft GPS_ANOMALY does not count.
        var score = users.First(u => u.GetProperty("userId").GetString() == user);
        score.GetProperty("scoreAvailable").GetBoolean().Should().BeTrue();
        score.GetProperty("riskScore").GetInt32().Should().Be(15);
        score.GetProperty("riskTier").GetString().Should().Be("low");

        var dashboard = await Admin().FunctionHandler(Request("GET", "/admin/dashboard/stats"), _context);
        Json(dashboard).GetProperty("fraudSignalsCount").GetInt32().Should().Be(signals.Count);
    }

    [LocalDynamoDbFact]
    public async Task AReviewMovesTheRowFromThePendingToTheReviewedPartition()
    {
        var user = $"buyer-frd07-{_run}";
        await Signals().RecordFraudSignalAsync(user, "PRICE_ANOMALY", $"crop-{_run}", "crop", "price");
        var pending = (await ListAsync("pending")).Signals.Single(s => s.GetProperty("userId").GetString() == user);
        var signalId = pending.GetProperty("signalId").GetString()!;

        var action = await Admin().FunctionHandler(
            Request("POST", $"/admin/fraud-signals/{signalId}/action", body: "{\"action\":\"approve\"}"), _context);
        action.StatusCode.Should().Be(200, action.Body);

        (await ListAsync("pending")).Signals.Should().NotContain(s => s.GetProperty("userId").GetString() == user);
        var reviewed = (await ListAsync("reviewed")).Signals.Single(s => s.GetProperty("userId").GetString() == user);
        reviewed.GetProperty("resolution").GetString().Should().Be("approve");
        reviewed.GetProperty("signalId").GetString().Should().Be(signalId);
    }

    [LocalDynamoDbFact]
    public async Task TheHeldQueueReadsTheHeldPartitionOnly_AndTheDashboardCountsIt()
    {
        var held = $"escrow-order-held-{_run}";
        var escrowed = $"escrow-order-open-{_run}";
        foreach (var (escrowId, status) in new[] { (held, "held"), (escrowed, "escrowed") })
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = new() { S = escrowId },
                ["createdAt"] = new() { S = "2026-10-01T10:00:00.0000000Z" },
                ["updatedAt"] = new() { S = "2026-10-20T10:00:00.0000000Z" },
                ["expiresAt"] = new() { S = "2026-10-08T10:00:00.0000000Z" },
                ["status"] = new() { S = status },
                ["buyerId"] = new() { S = $"buyer-{_run}" },
                ["farmerId"] = new() { S = $"farmer-{_run}" },
                ["amount"] = new() { N = "5000" },
                ["currency"] = new() { S = "ETB" }
            };
            if (status == "held")
            {
                item["holdReason"] = new AttributeValue { S = "risk_critical" };
                item["heldAt"] = new AttributeValue { S = "2026-10-20T10:00:00.0000000Z" };
                item["heldBy"] = new AttributeValue { S = "system" };
                item["heldFromStatus"] = new AttributeValue { S = "escrowed" };
                item["heldFor"] = new AttributeValue { S = "release" };
            }

            await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        }

        var response = await Admin().FunctionHandler(Request("GET", "/admin/escrows/held"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var ids = Json(response).GetProperty("escrows").EnumerateArray()
            .Select(e => e.GetProperty("escrowId").GetString()).ToList();
        ids.Should().Contain(held).And.NotContain(escrowed);
        var row = Json(response).GetProperty("escrows").EnumerateArray().First(e => e.GetProperty("escrowId").GetString() == held);
        row.GetProperty("holdReason").GetString().Should().Be("risk_critical");

        var dashboard = await Admin().FunctionHandler(Request("GET", "/admin/dashboard/stats"), _context);
        Json(dashboard).GetProperty("heldEscrows").GetInt32().Should().Be(ids.Count);
    }
}
