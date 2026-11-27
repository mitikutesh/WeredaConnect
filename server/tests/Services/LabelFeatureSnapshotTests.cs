using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-20 P1 (fraud-detection-architecture.md section 20.1): the feature snapshot of a fraud label. The
/// counted units per type follow the calculator's rules (hour dedup, per-entity counting, resolutions, the
/// whitelist) and are uncapped; the read covers the whole window where the score read stops early; it is
/// bounded by the score's page cap and says when it was cut; the controls come from the users row and one
/// counted Query of the role's escrow index; a read that fails gives no snapshot and never throws.
/// </summary>
public class LabelFeatureSnapshotTests
{
    private const string FraudTable = "fraud-signals-test";
    private const string UsersTable = "users-test";
    private const string EscrowsTable = "escrows-test";
    private const string UserId = "user-snap-1";

    private static readonly DateTime Now = new(2026, 11, 11, 12, 30, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<QueryRequest> _queries = new();
    private readonly List<GetItemRequest> _gets = new();
    private readonly List<string> _warnings = new();
    private readonly Queue<QueryResponse> _signalPages = new();

    private Dictionary<string, AttributeValue>? _userRow = UserRow("farmer", Now.AddDays(-400), fayda: true);
    private QueryResponse _escrowCount = new() { Count = 7, LastEvaluatedKey = new Dictionary<string, AttributeValue>() };
    private Exception? _failSignals;
    private Exception? _failUser;
    private Exception? _failEscrows;

    public LabelFeatureSnapshotTests()
    {
        _ddb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<QueryRequest>(0);
                if (query.TableName == EscrowsTable)
                {
                    return _failEscrows != null ? Task.FromException<QueryResponse>(_failEscrows) : Task.FromResult(_escrowCount);
                }

                if (_failSignals != null)
                {
                    return Task.FromException<QueryResponse>(_failSignals);
                }

                return Task.FromResult(_signalPages.Count > 0 ? _signalPages.Dequeue() : Page(null));
            });
        _ddb.GetItemAsync(Arg.Do<GetItemRequest>(_gets.Add), Arg.Any<CancellationToken>())
            .Returns(_ => _failUser != null
                ? Task.FromException<GetItemResponse>(_failUser)
                : Task.FromResult(new GetItemResponse { Item = _userRow }));
    }

    // ── builders ───────────────────────────────────────────

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Signal(
        string type, DateTime at, int? weight = null, string? resolution = null, string? entityId = null)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = $"{Iso(at)}#{type}" },
            ["signalType"] = new() { S = type }
        };
        if (weight != null)
        {
            row["weight"] = new AttributeValue { N = weight.Value.ToString(CultureInfo.InvariantCulture) };
        }

        if (resolution != null)
        {
            row["resolution"] = new AttributeValue { S = resolution };
        }

        if (entityId != null)
        {
            row["entityId"] = new AttributeValue { S = entityId };
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> Credit(DateTime at, int weight = -5) => new()
    {
        ["signalKey"] = new() { S = $"{Iso(at)}#CLEAN_DELIVERY" },
        ["signalType"] = new() { S = "CLEAN_DELIVERY" },
        ["recordType"] = new() { S = "credit" },
        ["weight"] = new() { N = weight.ToString(CultureInfo.InvariantCulture) }
    };

    private static Dictionary<string, AttributeValue> Whitelist(DateTime until) => new()
    {
        ["signalKey"] = new() { S = "~whitelist" },
        ["recordType"] = new() { S = "whitelist" },
        ["whitelistUntil"] = new() { S = Iso(until) }
    };

    private static Dictionary<string, AttributeValue> UserRow(string role, DateTime createdAt, bool fayda) => new()
    {
        ["role"] = new() { S = role },
        ["createdAt"] = new() { S = Iso(createdAt) },
        ["faydaVerified"] = new() { BOOL = fayda }
    };

    private static readonly Dictionary<string, AttributeValue> More = new()
    {
        ["userId"] = new() { S = UserId },
        ["signalKey"] = new() { S = "more" }
    };

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private static RiskFeatureUnits Units(params Dictionary<string, AttributeValue>[] rows)
    {
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, Now);
        foreach (var row in rows)
        {
            calculator.Add(row);
        }

        return calculator.FeatureUnits(truncated: false);
    }

    private FraudDetectionService Service() =>
        new(_ddb, "crops", FraudTable, Substitute.For<IRateLimitRepository>()) { AlarmLog = _ => { } };

    private LabelFeatureSnapshotReader Reader(string usersTable = UsersTable, string escrowsTable = EscrowsTable) =>
        new(_ddb, FraudTable, usersTable, escrowsTable);

    private List<QueryRequest> SignalQueries => _queries.Where(q => q.TableName == FraudTable).ToList();

    private List<QueryRequest> EscrowQueries => _queries.Where(q => q.TableName == EscrowsTable).ToList();

    // ── units (the calculator's rules, uncapped) ───────────

    [Fact]
    public void Units_CountPerType_OncePerHour_AndOncePerEntity()
    {
        var units = Units(
            // Two in one hour count once, a third in another hour counts again.
            Signal("PRICE_ANOMALY", Now.AddMinutes(-5)),
            Signal("PRICE_ANOMALY", Now.AddMinutes(-20)),
            Signal("PRICE_ANOMALY", Now.AddHours(-5)),
            // A per-entity type counts once per refused entity, whatever the hour.
            Signal("ROLE_FORBIDDEN", Now.AddHours(-1), entityId: "crop-1"),
            Signal("ROLE_FORBIDDEN", Now.AddHours(-9), entityId: "crop-1"),
            Signal("ROLE_FORBIDDEN", Now.AddHours(-10), entityId: "crop-2"),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-3)));

        units.UnitsByType.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["PRICE_ANOMALY"] = 2,
            ["ROLE_FORBIDDEN"] = 2,
            ["FRAUD_DUPLICATE_LISTING"] = 1
        });
        units.CreditUnits.Should().Be(0);
        units.WindowDays.Should().Be(30);
    }

    [Fact]
    public void Units_AreUncapped_WhereTheScoreCapsThePoints()
    {
        // Five refused entities at 25 points: the score stops at the type cap of 50, the units do not.
        var rows = Enumerable.Range(1, 5)
            .Select(i => Signal("ROLE_FORBIDDEN", Now.AddHours(-i), entityId: $"crop-{i}"))
            .ToArray();
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, Now);
        foreach (var row in rows)
        {
            calculator.Add(row);
        }

        calculator.Result(truncated: false).Score.Should().Be(RiskScoreSettings.Default.RejectTypeCap);
        calculator.FeatureUnits(truncated: false).UnitsByType["ROLE_FORBIDDEN"].Should().Be(5);
    }

    [Fact]
    public void Units_LeaveOutWhatAnAdminCleared_AndWhatLiesOutsideTheWindow()
    {
        var units = Units(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-1), resolution: "approve"),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-3), resolution: "reject"),
            // An approved refusal clears its whole entity, the newer row of it included.
            Signal("ROLE_FORBIDDEN", Now.AddHours(-4), entityId: "crop-1"),
            Signal("ROLE_FORBIDDEN", Now.AddHours(-8), entityId: "crop-1", resolution: "approve"),
            Signal("PRICE_ANOMALY", Now.AddDays(-31)));

        units.UnitsByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["FRAUD_DUPLICATE_LISTING"] = 1 });
    }

    [Fact]
    public void Units_AnActiveWhitelistSilencesTheSoftTypes_AsItStoodBeforeTheDecision()
    {
        var rows = new[]
        {
            Signal("PRICE_ANOMALY", Now.AddHours(-2)),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-3))
        };

        Units(rows).UnitsByType.Should().ContainKeys("PRICE_ANOMALY", "FRAUD_DUPLICATE_LISTING");
        Units(rows.Prepend(Whitelist(Now.AddDays(10))).ToArray()).UnitsByType
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["FRAUD_DUPLICATE_LISTING"] = 1 });
        Units(rows.Prepend(Whitelist(Now.AddDays(-1))).ToArray()).UnitsByType.Should().HaveCount(2);
    }

    [Fact]
    public void Units_CountTheCreditRowsAndTheirPoints_BeforeTheCreditCap()
    {
        var rows = Enumerable.Range(1, 8).Select(i => Credit(Now.AddDays(-i))).Append(Credit(Now.AddDays(-40))).ToArray();

        var units = Units(rows);

        units.CreditUnits.Should().Be(8);
        units.CreditPoints.Should().Be(40, "the cap of 30 points is the score's, not the snapshot's");
        units.UnitsByType.Should().BeEmpty();
    }

    // ── the read: whole window, bounded ────────────────────

    [Fact]
    public async Task TheScoreReadStopsEarlyAtCritical_TheSnapshotReadCountsTheWholeWindow()
    {
        // The first page alone makes CRITICAL certain (4 x 40 = 160, at least 100 + the credit cap of 30).
        QueryResponse[] TwoPages() => new[]
        {
            Page(More, Enumerable.Range(1, 4).Select(i => Signal("GPS_SPOOFING", Now.AddHours(-i), weight: 40)).ToArray()),
            Page(null, Signal("PRICE_ANOMALY", Now.AddDays(-2)), Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-3)),
                Credit(Now.AddDays(-4)))
        };

        foreach (var page in TwoPages())
        {
            _signalPages.Enqueue(page);
        }

        var score = await Service().GetUserRiskScoreAsync(UserId, Now, RiskScoreSettings.Default);
        score.Tier.Should().Be(RiskTier.Critical);
        SignalQueries.Should().HaveCount(1, "the score read stops once the tier is certain");

        _queries.Clear();
        _signalPages.Clear();
        foreach (var page in TwoPages())
        {
            _signalPages.Enqueue(page);
        }

        var units = await Service().ReadFeatureUnitsAsync(UserId, Now, RiskScoreSettings.Default);

        SignalQueries.Should().HaveCount(2);
        SignalQueries[1].ExclusiveStartKey.Should().BeSameAs(More);
        units.UnitsByType.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["GPS_SPOOFING"] = 4,
            ["PRICE_ANOMALY"] = 1,
            ["FRAUD_DUPLICATE_LISTING"] = 1
        });
        units.CreditUnits.Should().Be(1);
        units.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task TheSnapshotRead_UsesTheScoresKeyCondition_AndProjectsNoText()
    {
        await Service().ReadFeatureUnitsAsync(UserId, Now, RiskScoreSettings.Default);

        var query = SignalQueries.Single();
        query.KeyConditionExpression.Should().Be("#u = :uid AND #k >= :since");
        query.ExpressionAttributeValues[":uid"].S.Should().Be(UserId);
        query.ExpressionAttributeValues[":since"].S.Should().Be(Iso(Now.AddDays(-30)));
        query.IndexName.Should().BeNull();
        query.ExpressionAttributeNames.Values.Should().NotContain(new[] { "description", "metadata", "reviewNotes" });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheSnapshotRead_StopsAtThePageCap_AndSaysItWasCut(int maxPages)
    {
        // An attacker with thousands of rows: every page has a next key.
        for (var i = 0; i < 20; i++)
        {
            _signalPages.Enqueue(Page(More, Signal("PRICE_ANOMALY", Now.AddHours(-i - 1))));
        }

        var units = await Service().ReadFeatureUnitsAsync(
            UserId, Now, RiskScoreSettings.Default with { MaxPages = maxPages });

        // The pages, then one key-only probe of one row.
        SignalQueries.Should().HaveCount(maxPages + 1);
        SignalQueries.Last().Limit.Should().Be(1);
        units.Truncated.Should().BeTrue();
        units.UnitsByType["PRICE_ANOMALY"].Should().Be(maxPages);
    }

    [Fact]
    public async Task ALastPageThatEndsOnTheLastRow_IsNotTruncated_AndAProbeThatFailsIs()
    {
        _signalPages.Enqueue(Page(More, Signal("PRICE_ANOMALY", Now.AddHours(-1))));
        _signalPages.Enqueue(Page(More, Signal("PRICE_ANOMALY", Now.AddHours(-2))));
        _signalPages.Enqueue(Page(null)); // the probe: nothing left

        (await Service().ReadFeatureUnitsAsync(UserId, Now, RiskScoreSettings.Default)).Truncated.Should().BeFalse();

        _signalPages.Clear();
        var calls = 0;
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls <= 2
                ? Task.FromResult(Page(More, Signal("PRICE_ANOMALY", Now.AddHours(-calls))))
                : Task.FromException<QueryResponse>(new ProvisionedThroughputExceededException("throttled")));

        (await Service().ReadFeatureUnitsAsync(UserId, Now, RiskScoreSettings.Default)).Truncated.Should().BeTrue();
    }

    // ── the reader: signals plus the controls ──────────────

    [Fact]
    public async Task Reader_ReturnsTheUnitsAndTheControls_FromThreeBoundedReads()
    {
        _signalPages.Enqueue(Page(null, Signal("PRICE_ANOMALY", Now.AddHours(-2)), Credit(Now.AddDays(-1))));

        var snapshot = await Reader().ReadAsync(UserId, Now, _warnings.Add);

        snapshot.Should().NotBeNull();
        snapshot!.SignalUnits.Should().BeEquivalentTo(new Dictionary<string, int> { ["PRICE_ANOMALY"] = 1 });
        snapshot.CreditUnits.Should().Be(1);
        snapshot.CreditPoints.Should().Be(5);
        snapshot.Truncated.Should().BeFalse();
        snapshot.WindowDays.Should().Be(30);
        snapshot.AccountAgeDays.Should().Be(400);
        snapshot.FaydaVerified.Should().BeTrue();
        snapshot.Role.Should().Be("farmer");
        snapshot.CompletedEscrows.Should().Be(7);
        snapshot.CompletedEscrowsPartial.Should().BeFalse();
        _warnings.Should().BeEmpty();

        // The users row: a key read of three attributes, nothing that names a person.
        var get = _gets.Single();
        get.TableName.Should().Be(UsersTable);
        get.Key["userId"].S.Should().Be(UserId);
        get.ExpressionAttributeNames.Values.Should().BeEquivalentTo("createdAt", "faydaVerified", "role");

        // The completed escrows: one counted Query of the role's index, the read FRD-13 makes. No Scan.
        var escrows = EscrowQueries.Single();
        escrows.IndexName.Should().Be("FarmerStatusIndex");
        escrows.KeyConditionExpression.Should().Be("#party = :userId");
        escrows.ExpressionAttributeNames["#party"].Should().Be("farmerId");
        escrows.ExpressionAttributeValues[":completed"].S.Should().Be("completed");
        escrows.Select.Should().Be(Select.COUNT);
        escrows.Limit.Should().Be(50);
        escrows.ScanIndexForward.Should().BeFalse();
        await _ddb.DidNotReceiveWithAnyArgs().ScanAsync(default(ScanRequest)!, default);
    }

    [Theory]
    [InlineData("farmer", "FarmerStatusIndex", "farmerId")]
    [InlineData("trucker", "TruckerStatusIndex", "truckerId")]
    [InlineData("buyer", "BuyerStatusIndex", "buyerId")]
    public async Task Reader_CountsTheCompletedEscrows_OnTheIndexOfTheUsersRole(string role, string index, string field)
    {
        _userRow = UserRow(role, Now.AddDays(-3).AddHours(-1), fayda: false);
        _escrowCount = new QueryResponse { Count = 50, LastEvaluatedKey = More };

        var snapshot = await Reader().ReadAsync(UserId, Now, _warnings.Add);

        var escrows = EscrowQueries.Single();
        escrows.IndexName.Should().Be(index);
        escrows.ExpressionAttributeNames["#party"].Should().Be(field);
        snapshot!.Role.Should().Be(role);
        snapshot.AccountAgeDays.Should().Be(3);
        snapshot.FaydaVerified.Should().BeFalse();
        snapshot.CompletedEscrows.Should().Be(50);
        snapshot.CompletedEscrowsPartial.Should().BeTrue("the user has more escrows than the Query read");
    }

    [Fact]
    public async Task Reader_ARoleWithoutAnEscrowIndex_IsOther_AndReadsNoEscrows()
    {
        _userRow = UserRow("admin", Now.AddDays(-10), fayda: false);

        var snapshot = await Reader().ReadAsync(UserId, Now, _warnings.Add);

        snapshot!.Role.Should().Be("other");
        snapshot.CompletedEscrows.Should().BeNull();
        snapshot.CompletedEscrowsPartial.Should().BeNull();
        EscrowQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_AMissingUsersRow_KeepsTheUnits_WithoutTheControls()
    {
        _userRow = null;
        _signalPages.Enqueue(Page(null, Signal("PRICE_ANOMALY", Now.AddHours(-2))));

        var snapshot = await Reader().ReadAsync(UserId, Now, _warnings.Add);

        snapshot!.SignalUnits.Should().ContainKey("PRICE_ANOMALY");
        snapshot.AccountAgeDays.Should().BeNull();
        snapshot.FaydaVerified.Should().BeNull();
        snapshot.Role.Should().BeNull();
        snapshot.CompletedEscrows.Should().BeNull();
        EscrowQueries.Should().BeEmpty();
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_ACreatedAtThatDoesNotParse_LeavesTheAgeOut()
    {
        _userRow!["createdAt"] = new AttributeValue { S = "not a date" };

        (await Reader().ReadAsync(UserId, Now, _warnings.Add))!.AccountAgeDays.Should().BeNull();
    }

    [Theory]
    [InlineData("signals")]
    [InlineData("user")]
    [InlineData("escrows")]
    public async Task Reader_AReadThatFails_GivesNoSnapshot_LogsTheToken_AndNeverThrows(string failing)
    {
        var failure = new ProvisionedThroughputExceededException("throttled");
        _failSignals = failing == "signals" ? failure : null;
        _failUser = failing == "user" ? failure : null;
        _failEscrows = failing == "escrows" ? failure : null;

        var snapshot = await Reader().ReadAsync("user/with spaces", Now, _warnings.Add);

        snapshot.Should().BeNull();
        _warnings.Should().ContainSingle().Which.Should()
            .StartWith("LABEL_SNAPSHOT_FAILED userId=").And.EndWith("error=ProvisionedThroughputExceededException");
        _warnings[0].Should().NotContain("user/with spaces", "ids are made log safe");
    }

    [Fact]
    public async Task Reader_WithoutAFraudTableOrAUser_ReadsNothing()
    {
        (await new LabelFeatureSnapshotReader(_ddb, "", UsersTable, EscrowsTable).ReadAsync(UserId, Now, _warnings.Add))
            .Should().BeNull();
        (await Reader().ReadAsync("", Now, _warnings.Add)).Should().BeNull();

        _ddb.ReceivedCalls().Should().BeEmpty();
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_WithoutUsersOrEscrowsTables_KeepsWhatItCanRead()
    {
        var noUsers = await Reader(usersTable: "").ReadAsync(UserId, Now, _warnings.Add);
        noUsers!.Role.Should().BeNull();
        _gets.Should().BeEmpty();

        var noEscrows = await Reader(escrowsTable: "").ReadAsync(UserId, Now, _warnings.Add);
        noEscrows!.Role.Should().Be("farmer");
        noEscrows.CompletedEscrows.Should().BeNull();
        EscrowQueries.Should().BeEmpty();
    }
}
