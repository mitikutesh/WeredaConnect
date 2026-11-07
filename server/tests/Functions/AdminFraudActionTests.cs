using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-05 (fraud-detection-architecture.md sections 14.2, 18.1, 19, 20): the side effects of the admin's
/// review actions on a fraud signal. <c>approve</c> stops the signal counting, <c>reject</c> suspends the user
/// as <c>fraud_confirmed</c> through the one suspend path and lists the open escrows without holding any,
/// <c>whitelist</c> writes the <c>~whitelist</c> row; every action writes its transaction-audit row. The row is
/// checked first and the side effect comes before the review stamp, so a failure in between leaves the signal
/// pending for a retry. What the admin writes is fed back through the readers (the risk score, the settlement
/// gate), so the test proves the writer and the reader agree.
/// </summary>
public class AdminFraudActionTests
{
    private const string UsersTable = "users-test";
    private const string FraudTable = "fraud-signals-test";
    private const string EscrowsTable = "escrows-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string UserId = "farmer-1";
    private const string Phone = "+251911000001";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly ICognitoAuthService _cognito = Substitute.For<ICognitoAuthService>();
    private readonly TestLambdaContext _context = new();

    /// <summary>The pages each escrow party index answers with, in turn; an index without any answers empty.</summary>
    private readonly Dictionary<string, Queue<QueryResponse>> _escrowPages = new();

    /// <summary>What the consistent read of the signal row returns; null is a missing row.</summary>
    private Dictionary<string, AttributeValue>? _signalRow = new()
    {
        { "userId", new AttributeValue { S = UserId } },
        { "signalKey", new AttributeValue { S = "k" } }
    };

    // One-shot failures (a throttle or a timeout, not a condition), cleared when they fire.
    private Exception? _failNextUserUpdate;
    private Exception? _failNextUserRead;
    private Exception? _failNextWhitelistWrite;
    private Exception? _failNextLabelPut;

    public AdminFraudActionTests()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<UpdateItemRequest>(0).TableName == UsersTable && Take(ref _failNextUserUpdate) is { } ex
                ? Task.FromException<UpdateItemResponse>(ex)
                : Task.FromResult(new UpdateItemResponse()));
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var put = ci.ArgAt<PutItemRequest>(0);
                var failure = IsLabel(put) ? Take(ref _failNextLabelPut) : null;
                return failure != null
                    ? Task.FromException<PutItemResponse>(failure)
                    : Task.FromResult(new PutItemResponse());
            });
        // FUP-103: the whitelist's stamp and its row are one transaction.
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Take(ref _failNextWhitelistWrite) is { } ex
                ? Task.FromException<TransactWriteItemsResponse>(ex)
                : Task.FromResult(new TransactWriteItemsResponse()));
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (ci.ArgAt<GetItemRequest>(0).TableName == FraudTable)
                {
                    return Task.FromResult(new GetItemResponse { Item = _signalRow });
                }

                return Take(ref _failNextUserRead) is { } ex
                    ? Task.FromException<GetItemResponse>(ex)
                    : Task.FromResult(new GetItemResponse
                    {
                        Item = new Dictionary<string, AttributeValue> { { "phoneNumber", new AttributeValue { S = Phone } } }
                    });
            });
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(
                _escrowPages.TryGetValue(ci.ArgAt<QueryRequest>(0).IndexName, out var pages) && pages.Count > 0
                    ? pages.Dequeue()
                    : new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() }));
        var ok = AuthResult<bool>.Success(true);
        _cognito.AdminDisableUserAsync(Arg.Any<string>()).Returns(ok);
        _cognito.AdminEnableUserAsync(Arg.Any<string>()).Returns(ok);
        _cognito.AdminGlobalSignOutAsync(Arg.Any<string>()).Returns(ok);
    }

    private AdminFunction Function() =>
        new(_ddb, UsersTable, FraudTable, _cognito, EscrowsTable, TxAuditTable);

    private static string SignalKey(string type = "PRICE_ANOMALY", DateTime? at = null) =>
        $"{(at ?? DateTime.UtcNow.AddHours(-2)).ToString("o", CultureInfo.InvariantCulture)}#{type}";

    private static APIGatewayProxyRequest Post(string path, object body) => new()
    {
        HttpMethod = "POST",
        Path = path,
        Body = JsonSerializer.Serialize(body),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
        }
    };

    private Task<APIGatewayProxyResponse> ActAsync(string action, string? signalKey = null, string? notes = null) =>
        Function().FunctionHandler(
            Post($"/admin/fraud-signals/{FraudDetectionService.BuildSignalId(UserId, signalKey ?? SignalKey())}/action",
                notes == null ? new { action } : new { action, notes }),
            _context);

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private static string ErrorCode(APIGatewayProxyResponse response) =>
        Json(response).GetProperty("error").GetProperty("code").GetString()!;

    private static Exception? Take(ref Exception? failure)
    {
        var next = failure;
        failure = null;
        return next;
    }

    /// <summary>Every request of one kind the client received, failed ones included, in order.</summary>
    private List<T> Requests<T>() => _ddb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<T>().ToList();

    /// <summary>The client calls as <c>Method:table</c>, in order.</summary>
    private List<string> Trace => _ddb.ReceivedCalls()
        .Select(c => $"{c.GetMethodInfo().Name}:" + (c.GetArguments()[0] switch
        {
            GetItemRequest r => r.TableName,
            UpdateItemRequest r => r.TableName,
            PutItemRequest r => r.TableName,
            QueryRequest r => r.TableName,
            ScanRequest r => r.TableName,
            TransactWriteItemsRequest r => string.Join(",", r.TransactItems.Select(i => i.Update?.TableName ?? i.Put?.TableName).Distinct()),
            _ => ""
        }))
        .ToList();

    private List<UpdateItemRequest> Updates => Requests<UpdateItemRequest>();

    private List<PutItemRequest> Puts => Requests<PutItemRequest>();

    private List<QueryRequest> Queries => Requests<QueryRequest>();

    private List<TransactWriteItemsRequest> Transactions => Requests<TransactWriteItemsRequest>();

    /// <summary>A review stamp of the signal row, written alone (approve, reject) or in the whitelist's transaction.</summary>
    private sealed record Stamp(
        Dictionary<string, AttributeValue> Key, Dictionary<string, AttributeValue> ExpressionAttributeValues, string ConditionExpression);

    private List<Stamp> SignalStamps => Requests<object>()
        .SelectMany(request => request switch
        {
            UpdateItemRequest u when u.TableName == FraudTable =>
                new[] { new Stamp(u.Key, u.ExpressionAttributeValues, u.ConditionExpression) },
            TransactWriteItemsRequest t => t.TransactItems
                .Where(i => i.Update?.TableName == FraudTable)
                .Select(i => new Stamp(i.Update.Key, i.Update.ExpressionAttributeValues, i.Update.ConditionExpression)),
            _ => Enumerable.Empty<Stamp>()
        })
        .ToList();

    private Stamp SignalUpdate => SignalStamps.Single();

    private IEnumerable<UpdateItemRequest> UserUpdates => Updates.Where(u => u.TableName == UsersTable);

    /// <summary>The whitelist rows sent, alone or in a transaction, failed writes included.</summary>
    private List<Dictionary<string, AttributeValue>> WhitelistRows => Puts.Where(p => p.TableName == FraudTable).Select(p => p.Item)
        .Concat(Transactions.SelectMany(t => t.TransactItems).Where(i => i.Put?.TableName == FraudTable).Select(i => i.Put.Item))
        .ToList();

    private Dictionary<string, AttributeValue>? WhitelistRow => WhitelistRows.SingleOrDefault();

    private List<string> AuditActions => Puts
        .Where(p => p.TableName == TxAuditTable)
        .Select(p => p.Item["action"].S)
        .ToList();

    private static bool IsLabel(PutItemRequest put) =>
        put.TableName == TxAuditTable && put.Item.TryGetValue("action", out var action) && action.S == "outcome_label";

    /// <summary>FRD-18: the outcome label rows written, in order.</summary>
    private List<PutItemRequest> LabelPuts => Puts.Where(IsLabel).ToList();

    private string? AuditMetadata(string action) => Puts
        .Where(p => p.TableName == TxAuditTable && p.Item["action"].S == action)
        .Select(p => p.Item.TryGetValue("metadata", out var m) ? m.S : null)
        .Single();

    private static Dictionary<string, AttributeValue> EscrowRow(string escrowId, string status, string party, decimal amount) => new()
    {
        { "escrowId", new AttributeValue { S = escrowId } },
        { "status", new AttributeValue { S = status } },
        { party + "Id", new AttributeValue { S = UserId } },
        { "amount", new AttributeValue { N = amount.ToString(CultureInfo.InvariantCulture) } },
        { "orderId", new AttributeValue { S = $"ord-{escrowId}" } },
        { "createdAt", new AttributeValue { S = "2026-10-20T10:00:00.0000000Z" } }
    };

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private static readonly Dictionary<string, AttributeValue> MorePages = new() { { "escrowId", new AttributeValue { S = "more" } } };

    /// <summary>The stored signal row after the action, as the risk score's Query projects it.</summary>
    private Dictionary<string, AttributeValue> SignalRowAfter(string type, int weight) => new()
    {
        { "signalKey", SignalUpdate.Key["signalKey"] },
        { "signalType", new AttributeValue { S = type } },
        { "weight", new AttributeValue { N = weight.ToString(CultureInfo.InvariantCulture) } },
        { "resolution", SignalUpdate.ExpressionAttributeValues[":resolution"] }
    };

    private static Dictionary<string, AttributeValue> PendingSignal(string type, int weight, DateTime at) => new()
    {
        { "signalKey", new AttributeValue { S = SignalKey(type, at) } },
        { "signalType", new AttributeValue { S = type } },
        { "weight", new AttributeValue { N = weight.ToString(CultureInfo.InvariantCulture) } }
    };

    /// <summary>The risk score of section 14 over the given rows (newest first), at the given instant.</summary>
    private static Task<RiskScoreResult> ScoreAsync(DateTime nowUtc, params Dictionary<string, AttributeValue>[] rows)
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(Page(null, rows));
        var service = new FraudDetectionService(ddb, "crops", FraudTable, Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _ => { }
        };
        return service.GetUserRiskScoreAsync(UserId, nowUtc, RiskScoreSettings.Default);
    }

    // ──────────────────────────────────────────────
    // Action validation
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("Approve")]
    [InlineData("REJECT")]
    [InlineData(" whitelist")]
    [InlineData("flag")]
    [InlineData("fraud_reject")]
    [InlineData("")]
    public async Task AnUnknownOrMiscasedAction_Returns400Admin009_NothingWritten(string action)
    {
        var response = await ActAsync(action);

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("ADMIN_009");
        _ddb.ReceivedCalls().Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDisableUserAsync(default!);
    }

    [Theory]
    [InlineData("approve", "whitelist")]
    [InlineData("reject", "whitelist")]
    [InlineData("whitelist", "whitelist")]
    [InlineData("reject", "credit")]
    [InlineData("whitelist", "credit")]
    [InlineData("reject", null)]
    [InlineData("whitelist", null)]
    public async Task ACreditOrWhitelistRow_OrAMissingOne_Returns404Admin006_BeforeAnyWrite(string action, string? recordType)
    {
        _signalRow = recordType == null
            ? null
            : new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = UserId } },
                { "recordType", new AttributeValue { S = recordType } }
            };

        var response = await ActAsync(action, signalKey: "~whitelist");

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_006");
        var read = Requests<GetItemRequest>().Single();
        read.TableName.Should().Be(FraudTable);
        read.ConsistentRead.Should().BeTrue();
        read.Key["userId"].S.Should().Be(UserId);
        read.Key["signalKey"].S.Should().Be("~whitelist");
        Updates.Should().BeEmpty();
        Puts.Should().BeEmpty();
        Transactions.Should().BeEmpty();
        Queries.Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDisableUserAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("signal")]
    public async Task ALegacyOrMarkedSignalRow_IsReviewed(string? recordType)
    {
        if (recordType != null)
        {
            _signalRow!["recordType"] = new AttributeValue { S = recordType };
        }

        (await ActAsync("approve")).StatusCode.Should().Be(200);
        SignalUpdate.ExpressionAttributeValues[":resolution"].S.Should().Be("approve");
    }

    // ──────────────────────────────────────────────
    // approve
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Approve_TheSignalNoLongerCountsInTheRiskScore_AndNothingElseChanges()
    {
        var response = await ActAsync("approve", SignalKey("FRAUD_DUPLICATE_LISTING"));

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("resolution").GetString().Should().Be("approve");
        body.GetProperty("userId").GetString().Should().Be(UserId);
        body.TryGetProperty("suspended", out _).Should().BeFalse();
        SignalUpdate.ExpressionAttributeValues[":status"].S.Should().Be("reviewed");

        var now = DateTime.UtcNow;
        (await ScoreAsync(now, PendingSignal("FRAUD_DUPLICATE_LISTING", 20, now.AddHours(-2)))).Score.Should().Be(20);
        (await ScoreAsync(now, SignalRowAfter("FRAUD_DUPLICATE_LISTING", 20))).Score.Should().Be(0);

        UserUpdates.Should().BeEmpty();
        WhitelistRow.Should().BeNull();
        Queries.Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDisableUserAsync(default!);
        AuditActions.Should().Equal("fraud_approve", "outcome_label");
    }

    // ──────────────────────────────────────────────
    // reject
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Reject_SuspendsTheUserAsFraudConfirmed_EndsSessions_ListsOpenEscrows_HoldsNothing()
    {
        _escrowPages["FarmerStatusIndex"] = new(new[]
        {
            Page(null, EscrowRow("esc-1", "escrowed", "farmer", 8_000m), EscrowRow("esc-2", "held", "farmer", 1_500m))
        });
        _escrowPages["TruckerStatusIndex"] = new(new[] { Page(null, EscrowRow("esc-3", "disputed", "trucker", 700m)) });

        var response = await ActAsync("reject", SignalKey("FRAUD_DUPLICATE_LISTING"), notes: "Same photos as farmer-9");

        response.StatusCode.Should().Be(200, response.Body);
        var body = Json(response);
        body.GetProperty("resolution").GetString().Should().Be("reject");
        body.GetProperty("suspended").GetBoolean().Should().BeTrue();
        body.GetProperty("suspendReason").GetString().Should().Be("fraud_confirmed");
        body.GetProperty("sessionsEnded").GetBoolean().Should().BeTrue();
        body.GetProperty("openEscrowsComplete").GetBoolean().Should().BeTrue();
        var escrows = body.GetProperty("openEscrows").EnumerateArray()
            .Select(e => (e.GetProperty("escrowId").GetString(), e.GetProperty("status").GetString(),
                e.GetProperty("party").GetString(), e.GetProperty("amount").GetDecimal()))
            .ToList();
        escrows.Should().Equal(("esc-1", "escrowed", "farmer", 8_000m), ("esc-2", "held", "farmer", 1_500m),
            ("esc-3", "disputed", "trucker", 700m));

        // The suspension goes through the one suspend path: the user row, Cognito, the audit row.
        var suspend = UserUpdates.Single();
        suspend.Key["userId"].S.Should().Be(UserId);
        suspend.ExpressionAttributeValues[":suspended"].BOOL.Should().BeTrue();
        suspend.ExpressionAttributeValues[":reason"].S.Should().Be("fraud_confirmed");
        await _cognito.Received(1).AdminDisableUserAsync(Phone);
        await _cognito.Received(1).AdminGlobalSignOutAsync(Phone);

        // The three party indexes, each filtered on the open statuses; read only.
        Queries.Select(q => q.IndexName).Should().Equal("BuyerStatusIndex", "FarmerStatusIndex", "TruckerStatusIndex");
        Queries.Should().OnlyContain(q => q.TableName == EscrowsTable
            && q.ExpressionAttributeValues[":userId"].S == UserId
            && q.FilterExpression == "#status IN (:s0, :s1, :s2, :s3)"
            && q.ExpressionAttributeValues[":s0"].S == "escrowed"
            && q.ExpressionAttributeValues[":s1"].S == "disputed"
            && q.ExpressionAttributeValues[":s2"].S == "dispute_resolved"
            && q.ExpressionAttributeValues[":s3"].S == "held");
        Updates.Should().NotContain(u => u.TableName == EscrowsTable);
        Puts.Should().NotContain(p => p.TableName == EscrowsTable);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);

        // Read the row, suspend, then stamp and audit the review; the escrows are read last.
        Trace.Should().ContainInOrder(
            $"GetItemAsync:{FraudTable}", $"UpdateItemAsync:{UsersTable}", $"UpdateItemAsync:{FraudTable}",
            $"PutItemAsync:{TxAuditTable}", $"QueryAsync:{EscrowsTable}");
        AuditActions.Should().Equal("user_suspend", "fraud_reject", "outcome_label");
        AuditMetadata("fraud_reject").Should().Be("Same photos as farmer-9");
        AuditMetadata("user_suspend").Should().Be("fraud_confirmed");
    }

    [Fact]
    public async Task Reject_TheSignalKeepsCounting()
    {
        await ActAsync("reject", SignalKey("FRAUD_DUPLICATE_LISTING"));

        (await ScoreAsync(DateTime.UtcNow, SignalRowAfter("FRAUD_DUPLICATE_LISTING", 20))).Score.Should().Be(20);
    }

    [Fact]
    public async Task Reject_TheUserRowItWrites_IsHeldByTheNextSweepAsFraudConfirmed()
    {
        await ActAsync("reject");

        // The profile row as the suspend left it, read back the way the settlement gate reads it.
        var suspend = UserUpdates.Single();
        var row = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = UserId } },
            { "phoneNumber", new AttributeValue { S = Phone } },
            { "role", new AttributeValue { S = "farmer" } },
            { "woreda", new AttributeValue { S = "w" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "suspended", suspend.ExpressionAttributeValues[":suspended"] },
            { "suspendReason", suspend.ExpressionAttributeValues[":reason"] }
        };
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync(UserId).Returns(UserRepository.FromItem(row));
        users.GetUserByIdAsync("buyer-1").Returns((UserDto?)null);

        var decision = await new PartySuspensionGate(users).CheckAsync(new Dictionary<string, object>
        {
            { "escrowId", "esc-1" }, { "buyerId", "buyer-1" }, { "farmerId", UserId }
        });

        decision.HoldReason.Should().Be("fraud_confirmed");
        decision.Party.Should().Be("farmer");
    }

    [Fact]
    public async Task Reject_WhenCognitoFails_TheSuspensionStands_AndTheResponseSaysSessionsMayLive()
    {
        _cognito.AdminGlobalSignOutAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(503, "AUTH_008", "Unavailable."));

        var response = await ActAsync("reject");

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("suspended").GetBoolean().Should().BeTrue();
        body.GetProperty("sessionsEnded").GetBoolean().Should().BeFalse();
        body.GetProperty("message").GetString().Should().Contain("sign-in service update failed");
        UserUpdates.Should().ContainSingle();
    }

    [Fact]
    public async Task Reject_TheUserRowIsMissing_Returns404Admin007_TheSignalStaysPending()
    {
        _ddb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == UsersTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("no user"));

        var response = await ActAsync("reject");

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_007");
        Updates.Should().NotContain(u => u.TableName == FraudTable);
        Queries.Should().BeEmpty();
        AuditActions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("phone")]
    public async Task Reject_ASuspendThatFails_LeavesTheSignalPending_AndTheRetryCompletesIt(string failingStep)
    {
        // A throttle on the profile write, or a timeout on the phone read Cognito needs.
        if (failingStep == "profile")
        {
            _failNextUserUpdate = new ProvisionedThroughputExceededException("throttled");
        }
        else
        {
            _failNextUserRead = new AmazonDynamoDBException("timeout");
        }

        var first = await ActAsync("reject");

        first.StatusCode.Should().Be(500);
        ErrorCode(first).Should().Be("ADMIN_500");
        // Still pending, so Fraud Review offers the buttons again; nothing says it was reviewed.
        Updates.Should().NotContain(u => u.TableName == FraudTable);
        // FUP-95: a profile row that was written has its audit row, even when the phone read after it failed.
        var suspendedFirst = failingStep == "phone";
        AuditActions.Should().Equal(suspendedFirst ? new[] { "user_suspend" } : Array.Empty<string>());

        var retry = await ActAsync("reject");

        retry.StatusCode.Should().Be(200, retry.Body);
        Json(retry).GetProperty("suspended").GetBoolean().Should().BeTrue();
        Updates.Count(u => u.TableName == FraudTable).Should().Be(1);
        // The same suspension written again: the retry is idempotent.
        UserUpdates.Should().HaveCount(2).And.OnlyContain(u =>
            u.ExpressionAttributeValues[":suspended"].BOOL == true
            && u.ExpressionAttributeValues[":reason"].S == "fraud_confirmed");
        await _cognito.Received(1).AdminDisableUserAsync(Phone);
        await _cognito.Received(1).AdminGlobalSignOutAsync(Phone);
        AuditActions.Should().Equal(suspendedFirst
            ? new[] { "user_suspend", "user_suspend", "fraud_reject", "outcome_label" }
            : new[] { "user_suspend", "fraud_reject", "outcome_label" });
    }

    [Fact]
    public async Task Reject_TheRowGoesAwayAfterTheSuspend_IsAnsweredAsDone_TheSuspensionStands()
    {
        // FUP-103: the TTL removed the row between the read and the stamp. The user is suspended by then, so
        // the admin is told that, not that the signal was not found.
        _escrowPages["BuyerStatusIndex"] = new(new[] { Page(null, EscrowRow("esc-1", "escrowed", "buyer", 900m)) });
        _ddb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == FraudTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("gone"));

        var response = await ActAsync("reject", notes: "Seen in the dispute evidence");

        response.StatusCode.Should().Be(200, response.Body);
        var body = Json(response);
        body.GetProperty("resolution").GetString().Should().Be("reject");
        body.GetProperty("suspended").GetBoolean().Should().BeTrue();
        body.GetProperty("suspendReason").GetString().Should().Be("fraud_confirmed");
        body.GetProperty("message").GetString().Should().Contain("expired");
        body.GetProperty("openEscrows").EnumerateArray().Select(e => e.GetProperty("escrowId").GetString())
            .Should().Equal("esc-1");
        UserUpdates.Should().ContainSingle();
        AuditActions.Should().Equal("user_suspend", "fraud_reject", "outcome_label");
        AuditMetadata("fraud_reject").Should().Be("Seen in the dispute evidence");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("FRAUD_REJECT_SIGNAL_GONE");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("whitelist")]
    public async Task TheRowGoesAwayBeforeTheStamp_Answers404_AndLeavesNothingBehind(string action)
    {
        // approve has no side effect; the whitelist row travels in the stamp's transaction (FUP-103).
        _ddb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == FraudTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("gone"));
        _failNextWhitelistWrite = new TransactionCanceledException("gone")
        {
            CancellationReasons = new List<CancellationReason>
            {
                new() { Code = "ConditionalCheckFailed" },
                new() { Code = "None" }
            }
        };

        var response = await ActAsync(action);

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_006");
        AuditActions.Should().BeEmpty();
        Puts.Should().NotContain(p => p.TableName == FraudTable);
    }

    [Fact]
    public async Task Reject_OpenEscrowReadFails_TheSuspensionStands_AndTheListIsMarkedIncomplete()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var response = await ActAsync("reject");

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("suspended").GetBoolean().Should().BeTrue();
        body.GetProperty("openEscrowsComplete").GetBoolean().Should().BeFalse();
        body.GetProperty("openEscrows").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Reject_OpenEscrowsBeyondThePageBudget_AreMarkedIncomplete()
    {
        // Every page of the buyer index is filtered empty and points at another.
        _escrowPages["BuyerStatusIndex"] = new(Enumerable.Range(0, 10).Select(_ => Page(MorePages)));

        var response = await ActAsync("reject");

        Json(response).GetProperty("openEscrowsComplete").GetBoolean().Should().BeFalse();
        Queries.Count(q => q.IndexName == "BuyerStatusIndex").Should().Be(5);
        Queries.Should().Contain(q => q.IndexName == "TruckerStatusIndex");
    }

    [Fact]
    public async Task Reject_ListsAtMostFiftyEscrows_AndSaysWhenItStopped()
    {
        var rows = Enumerable.Range(1, 60).Select(i => EscrowRow($"esc-{i}", "escrowed", "buyer", 100m)).ToArray();
        _escrowPages["BuyerStatusIndex"] = new(new[] { Page(null, rows) });

        var body = Json(await ActAsync("reject"));

        body.GetProperty("openEscrows").GetArrayLength().Should().Be(50);
        body.GetProperty("openEscrowsComplete").GetBoolean().Should().BeFalse();
    }

    // ──────────────────────────────────────────────
    // whitelist
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Whitelist_WritesTheWhitelistRow_WithItsExpiryAndTtl()
    {
        var before = DateTime.UtcNow;

        var response = await ActAsync("whitelist", notes: "Known GPS drift in Jimma");

        response.StatusCode.Should().Be(200);
        var row = WhitelistRow!;
        row.Should().NotBeNull();
        row["userId"].S.Should().Be(UserId);
        row["signalKey"].S.Should().Be("~whitelist");
        row["recordType"].S.Should().Be("whitelist");
        row["reviewedBy"].S.Should().Be("admin-1");
        row["reviewNotes"].S.Should().Be("Known GPS drift in Jimma");
        row.Should().NotContainKey("status"); // a sparse status index (FRD-07) must never list it
        var until = DateTime.Parse(row["whitelistUntil"].S, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        until.Should().BeCloseTo(before.AddDays(30), TimeSpan.FromMinutes(1));
        long.Parse(row["expiresAt"].N, CultureInfo.InvariantCulture)
            .Should().Be(new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeSeconds());

        var body = Json(response);
        body.GetProperty("whitelistUntil").GetString().Should().Be(row["whitelistUntil"].S);
        SignalUpdate.ExpressionAttributeValues[":resolution"].S.Should().Be("whitelist");
        UserUpdates.Should().BeEmpty();
        Trace.Should().ContainInOrder(
            $"GetItemAsync:{FraudTable}", $"TransactWriteItemsAsync:{FraudTable}", $"PutItemAsync:{TxAuditTable}");
        AuditActions.Should().Equal("fraud_whitelist", "outcome_label");
        AuditMetadata("fraud_whitelist").Should().Be($"whitelistUntil={row["whitelistUntil"].S};notes=Known GPS drift in Jimma");
    }

    [Fact]
    public async Task Whitelist_TheRowAndTheStampAreOneTransaction_SoNeitherLandsAlone()
    {
        // FUP-103: written first and alone, a whitelist row outlived a failed stamp, and an approve or a reject
        // chosen instead kept the soft bypass until whitelistUntil.
        var response = await ActAsync("whitelist");

        response.StatusCode.Should().Be(200, response.Body);
        Puts.Should().NotContain(p => p.TableName == FraudTable);
        Updates.Should().NotContain(u => u.TableName == FraudTable);
        var items = Transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(2);
        items[0].Update.Key["signalKey"].S.Should().Be(SignalUpdate.Key["signalKey"].S);
        items[0].Update.ConditionExpression.Should().Be(
            "attribute_exists(userId) AND (attribute_not_exists(#recordType) OR #recordType = :signalRecord)");
        items[1].Put.TableName.Should().Be(FraudTable);
        items[1].Put.Item["signalKey"].S.Should().Be("~whitelist");
        items[1].Put.ConditionExpression.Should().BeNullOrEmpty(); // a new whitelist replaces the old one
    }

    [Fact]
    public async Task Whitelist_AWriteThatFails_LeavesNoWhitelistRowAndTheSignalPending_AndTheRetryCompletesIt()
    {
        // A throttle or a conflict cancels the whole transaction: neither the row nor the stamp is written.
        _failNextWhitelistWrite = new TransactionCanceledException("throttled")
        {
            CancellationReasons = new List<CancellationReason>
            {
                new() { Code = "ThrottlingError" },
                new() { Code = "None" }
            }
        };

        var first = await ActAsync("whitelist");

        first.StatusCode.Should().Be(500);
        ErrorCode(first).Should().Be("ADMIN_500");
        AuditActions.Should().BeEmpty();

        var retry = await ActAsync("whitelist");

        retry.StatusCode.Should().Be(200, retry.Body);
        // The same transaction sent again: the row is written in place, so the retry is idempotent.
        Transactions.Should().HaveCount(2);
        WhitelistRows.Should().HaveCount(2).And.OnlyContain(r =>
            r["userId"].S == UserId && r["signalKey"].S == "~whitelist" && r["recordType"].S == "whitelist");
        SignalStamps.Should().HaveCount(2).And.OnlyContain(st => st.ExpressionAttributeValues[":resolution"].S == "whitelist");
        AuditActions.Should().Equal("fraud_whitelist", "outcome_label");
    }

    [Fact]
    public async Task Whitelist_SoftWeightsDoNotCountUntilItEnds_AndCountAgainAfter()
    {
        await ActAsync("whitelist", SignalKey("GPS_ANOMALY"));
        var written = WhitelistRow!;
        var until = DateTime.Parse(written["whitelistUntil"].S, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var whitelistRow = new Dictionary<string, AttributeValue>
        {
            { "signalKey", written["signalKey"] },
            { "recordType", written["recordType"] },
            { "whitelistUntil", written["whitelistUntil"] }
        };
        // Signals of the last day of the whitelist: soft PRICE_ANOMALY 10, hard FRAUD_DUPLICATE_LISTING 20.
        var soft = PendingSignal("PRICE_ANOMALY", 10, until.AddDays(-1));
        var hard = PendingSignal("FRAUD_DUPLICATE_LISTING", 20, until.AddDays(-1).AddMinutes(-90));

        (await ScoreAsync(until.AddHours(-1), whitelistRow, soft, hard)).Score.Should().Be(20);
        (await ScoreAsync(until.AddSeconds(1), whitelistRow, soft, hard)).Score.Should().Be(30);

        // The whitelisted signal itself stops counting, whatever its class (section 14.2).
        (await ScoreAsync(DateTime.UtcNow, whitelistRow, SignalRowAfter("GPS_ANOMALY", 25))).Score.Should().Be(0);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("45", 45)]
    [InlineData("1", 1)]
    [InlineData("365", 365)]
    [InlineData("0", 30)]
    [InlineData("366", 30)]
    [InlineData("-5", 30)]
    [InlineData("ten", 30)]
    public void WhitelistDays_ComeFromTheEnvironment_InRange(string? value, int expected)
    {
        FraudDetectionService.WhitelistDaysFrom(name => name == "FRAUD_WHITELIST_DAYS" ? value : null)
            .Should().Be(expected);
    }

    // The list and the dashboard count (FRD-07, a Query on the status index): AdminFraudReviewQueueTests.

    // ──────────────────────────────────────────────
    // The user action route and the reserved reason
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("fraud_confirmed")]
    [InlineData(" Fraud_Confirmed ")]
    public async Task UserRoute_TheReservedReason_Returns400Admin017_NothingWritten(string reason)
    {
        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", new { action = "suspend", reason }), _context);

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("ADMIN_017");
        Updates.Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDisableUserAsync(default!);
    }

    [Fact]
    public async Task UserRoute_SuspendWithAnotherReason_StoresItAsBefore()
    {
        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", new { action = "suspend", reason = "fraud reported by phone" }), _context);

        response.StatusCode.Should().Be(200);
        var update = UserUpdates.Single();
        update.ExpressionAttributeValues[":reason"].S.Should().Be("fraud reported by phone");
        // FUP-103: written only over no reason or a free-text one, never over fraud_confirmed.
        update.ConditionExpression.Should().Be(
            "attribute_exists(userId) AND (attribute_not_exists(#suspendReason) OR #suspendReason <> :fraudReason)");
        update.ExpressionAttributeValues[":fraudReason"].S.Should().Be("fraud_confirmed");
        update.ReturnValuesOnConditionCheckFailure.Should().Be(ReturnValuesOnConditionCheckFailure.ALL_OLD);
        AuditMetadata("user_suspend").Should().Be("fraud reported by phone");
        Json(response).GetProperty("message").GetString().Should().NotContain("fraud_confirmed");
    }

    [Fact]
    public async Task UserRoute_AFreeTextSuspendOfAUserSuspendedAsFraudConfirmed_KeepsThatReason()
    {
        // FUP-103: the reason is what the sweep holds the escrows as (fraud_confirmed, not account_suspended)
        // and what the outcome labels record, so a later free-text suspend must not replace it.
        _ddb.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(u => u.TableName == UsersTable && u.ConditionExpression.Contains(":fraudReason")),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("fraud_confirmed")
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "userId", new AttributeValue { S = UserId } },
                    { "suspended", new AttributeValue { BOOL = true } },
                    { "suspendReason", new AttributeValue { S = "fraud_confirmed" } }
                }
            });

        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", new { action = "suspend", reason = "chargeback reported" }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).GetProperty("message").GetString().Should().Contain("stays fraud_confirmed");
        var updates = UserUpdates.ToList();
        updates.Should().HaveCount(2);
        // The second write suspends again without touching the reason.
        var kept = updates[1];
        kept.UpdateExpression.Should().NotContain("suspendReason");
        kept.ExpressionAttributeNames.Should().NotContainKey("#suspendReason");
        kept.ExpressionAttributeValues.Should().NotContainKeys(":reason", ":fraudReason");
        kept.ExpressionAttributeValues[":suspended"].BOOL.Should().BeTrue();
        kept.ConditionExpression.Should().Be("attribute_exists(userId)");
        await _cognito.Received(1).AdminDisableUserAsync(Phone);
        AuditMetadata("user_suspend").Should().Be("keptReason=fraud_confirmed;reason=chargeback reported");
    }

    [Fact]
    public async Task UserRoute_AFreeTextSuspendOfAMissingUser_Returns404Admin007()
    {
        // No old row comes back with the failed condition: the user is missing, not suspended as fraud_confirmed.
        _ddb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == UsersTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("no user"));

        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", new { action = "suspend", reason = "chargeback reported" }), _context);

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_007");
        UserUpdates.Should().ContainSingle();
        AuditActions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("unsuspend")]
    [InlineData("change_role")]
    public async Task UserRoute_APhoneReadThatFailsAfterTheProfileWrite_StillWritesTheAuditRow(string action)
    {
        // FUP-95: the profile row is written by then; the 500 must not leave it without its audit row.
        _failNextUserRead = new AmazonDynamoDBException("timeout");
        _cognito.AdminSetRoleAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));

        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", action == "change_role"
                ? (object)new { action, newRole = "buyer" }
                : new { action, reason = "chargeback reported" }), _context);

        response.StatusCode.Should().Be(500);
        UserUpdates.Should().ContainSingle();
        AuditActions.Should().Equal($"user_{action}");
        await _cognito.DidNotReceiveWithAnyArgs().AdminDisableUserAsync(default!);
    }

    [Fact]
    public async Task UserRoute_Unsuspend_RemovesTheSuspendReason()
    {
        var response = await Function().FunctionHandler(
            Post($"/admin/users/{UserId}/action", new { action = "unsuspend" }), _context);

        response.StatusCode.Should().Be(200);
        var update = UserUpdates.Single();
        update.UpdateExpression.Should().EndWith(" REMOVE #suspendReason");
        update.ExpressionAttributeNames["#suspendReason"].Should().Be("suspendReason");
        update.ExpressionAttributeValues[":suspended"].BOOL.Should().BeFalse();
    }

    // ──────────────────────────────────────────────
    // FRD-18: the outcome label of each decision (section 20.1)
    // ──────────────────────────────────────────────

    private const string SignalCreatedAt = "2026-11-01T09:12:00.0000000Z";

    /// <summary>A pending signal on an order escrow, as the consistent read returns it.</summary>
    private void SignalRowIs(string? status = null, string? resolution = null, string? reviewedAt = null)
    {
        _signalRow = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = UserId } },
            { "signalType", new AttributeValue { S = "SELF_TRADE" } },
            { "entityType", new AttributeValue { S = "escrow" } },
            { "entityId", new AttributeValue { S = "escrow-order-ord-7" } },
            { "createdAt", new AttributeValue { S = SignalCreatedAt } },
            { "status", new AttributeValue { S = status ?? "pending" } }
        };
        if (resolution != null)
        {
            _signalRow["resolution"] = new AttributeValue { S = resolution };
        }

        if (reviewedAt != null)
        {
            _signalRow["reviewedAt"] = new AttributeValue { S = reviewedAt };
        }
    }

    [Theory]
    [InlineData("approve", "false_positive", 1)]
    [InlineData("reject", "fraud_confirmed", 2)]
    [InlineData("whitelist", "whitelisted", 3)]
    public async Task EveryDecision_WritesExactlyOneLabel_LinkedToItsSignalUserAndTransaction(
        string action, string outcome, int slot)
    {
        SignalRowIs();
        var key = SignalKey("SELF_TRADE");
        var signalId = FraudDetectionService.BuildSignalId(UserId, key);

        var response = await ActAsync(action, key, notes: "Checked the device ids");

        response.StatusCode.Should().Be(200, response.Body);
        var label = LabelPuts.Should().ContainSingle().Subject;
        label.ConditionExpression.Should().Be("attribute_not_exists(transactionId)");
        var item = label.Item;
        item["transactionId"].S.Should().Be(signalId);
        // Pending until now, so the key is the signal's own first time plus the outcome's slot.
        item["timestamp"].S.Should().Be(DateTime.Parse(SignalCreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime().AddTicks(slot).ToString("o", CultureInfo.InvariantCulture));
        item["entityId"].S.Should().Be("outcome-labels");
        item["labelCase"].S.Should().Be("fraud_signal");
        item["labelCaseId"].S.Should().Be(signalId);
        item["labelOutcome"].S.Should().Be(outcome);
        item["labelSource"].S.Should().Be($"fraud_{action}");
        item["labelReason"].S.Should().Be("Checked the device ids");
        item["actor"].S.Should().Be("admin-1");
        item["subjectUserId"].S.Should().Be(UserId);
        item["signalIds"].L.Select(v => v.S).Should().Equal(signalId);
        item["signalType"].S.Should().Be("SELF_TRADE");
        item["signalEntityType"].S.Should().Be("escrow");
        item["escrowId"].S.Should().Be("escrow-order-ord-7");
        item["orderId"].S.Should().Be("ord-7");
        item["labelledAt"].S.Should().Be(SignalUpdate.ExpressionAttributeValues[":reviewedAt"].S);
        item.ContainsKey("whitelistUntil").Should().Be(action == "whitelist");
        // After the stamp (with the whitelist row, one transaction) and its audit row.
        var stampCall = action == "whitelist" ? "TransactWriteItemsAsync" : "UpdateItemAsync";
        Trace.Should().ContainInOrder($"{stampCall}:{FraudTable}", $"PutItemAsync:{TxAuditTable}", $"PutItemAsync:{TxAuditTable}");
    }

    [Fact]
    public async Task TheReadAskedForWhatTheLabelLinks()
    {
        await ActAsync("approve");

        var read = Requests<GetItemRequest>().Single(r => r.TableName == FraudTable);
        read.ConsistentRead.Should().BeTrue();
        read.ExpressionAttributeNames.Values.Should().Contain(new[]
        {
            "userId", "recordType", "status", "resolution", "reviewedAt", "signalType", "entityType", "entityId", "createdAt"
        });
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("whitelist")]
    public async Task TheDecisionTheSignalAlreadyCarries_WritesNoLabel(string action)
    {
        // A retry after a lost answer, or the same verb again: the first decision has its label.
        SignalRowIs("reviewed", action, "2026-11-02T08:00:00.0000000Z");

        var response = await ActAsync(action);

        response.StatusCode.Should().Be(200, response.Body);
        LabelPuts.Should().BeEmpty();
        AuditActions.Should().Contain($"fraud_{action}");
    }

    [Fact]
    public async Task AChangeOfMind_WritesItsOwnLabel_KeyedOnThePreviousDecision()
    {
        SignalRowIs("reviewed", "reject", "2026-11-02T08:00:00.0000000Z");

        var response = await ActAsync("approve");

        response.StatusCode.Should().Be(200, response.Body);
        var item = LabelPuts.Should().ContainSingle().Subject.Item;
        item["labelOutcome"].S.Should().Be("false_positive");
        item["timestamp"].S.Should().Be("2026-11-02T08:00:00.0000001Z");
    }

    [Fact]
    public async Task TwoIdenticalDecisionsInFlight_BuildOneKey_SoTheConditionalPutKeepsOne()
    {
        // Both read the signal pending before either stamped it.
        SignalRowIs();
        var key = SignalKey("SELF_TRADE");

        await ActAsync("reject", key);
        await ActAsync("reject", key);

        var labels = LabelPuts;
        labels.Should().HaveCount(2);
        labels[1].Item["transactionId"].S.Should().Be(labels[0].Item["transactionId"].S);
        labels[1].Item["timestamp"].S.Should().Be(labels[0].Item["timestamp"].S);
        labels.Should().OnlyContain(p => p.ConditionExpression == "attribute_not_exists(transactionId)");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("whitelist")]
    public async Task ALabelThatFailsOrIsAlreadyThere_DoesNotFailTheDecision(string action)
    {
        SignalRowIs();
        _failNextLabelPut = new ProvisionedThroughputExceededException("throttled");

        var failed = await ActAsync(action);

        failed.StatusCode.Should().Be(200, failed.Body);
        Json(failed).GetProperty("resolution").GetString().Should().Be(action);
        SignalUpdate.ExpressionAttributeValues[":status"].S.Should().Be("reviewed");

        _failNextLabelPut = new ConditionalCheckFailedException("already labelled");
        (await ActAsync(action)).StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ALegacyRowWithoutCreatedAt_IsKeyedOnTheHourInItsKey()
    {
        // The default row carries no createdAt, no type and no entity.
        var key = SignalKey("PRICE_ANOMALY", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));

        await ActAsync("approve", key);

        var item = LabelPuts.Single().Item;
        item["timestamp"].S.Should().Be("2026-11-01T09:00:00.0000001Z");
        item["signalType"].S.Should().Be("PRICE_ANOMALY");
        item.Should().NotContainKeys("orderId", "escrowId", "deliveryId");
    }
}
