using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-15 (fraud-detection-architecture.md section 15.2): the hourly relationship scan over seeded orders and
/// users. Each pattern fires from its fixture, each signal lands on both parties with the order as the entity,
/// a rerun writes nothing twice, and the run stays inside its caps.
/// </summary>
public class RelationshipPatternServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc);

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();
    private readonly Dictionary<string, List<Dictionary<string, object>>> _byStatus = new();
    private readonly Dictionary<string, List<Dictionary<string, object>>> _byBuyer = new();

    public RelationshipPatternServiceTests()
    {
        _orders.QueryByStatusCreatedSinceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(call => (
                (_byStatus.GetValueOrDefault(call.ArgAt<string>(0)) ?? new List<Dictionary<string, object>>())
                    // The key condition: createdAt >= since (ISO strings of one format compare as times).
                    .Where(o => string.CompareOrdinal((string)o["createdAt"], call.ArgAt<string>(1)) >= 0)
                    .ToList(),
                (Dictionary<string, AttributeValue>?)null));
        _orders.QueryOrdersAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(call => (_byBuyer.GetValueOrDefault(call.ArgAt<string?>(0) ?? "") ?? new List<Dictionary<string, object>>(),
                (Dictionary<string, AttributeValue>?)null));
    }

    private RelationshipPatternService Service(RelationshipPatternSettings? settings = null) =>
        new(_orders, _users, _fraud, settings ?? RelationshipPatternSettings.Default);

    // The run's slice counts from the run start, as the order cron's 4 minute share of the Lambda does.
    private static CronRunSlice Slice(TimeSpan? elapsed = null) =>
        new CronRunBudget(() => elapsed ?? TimeSpan.Zero).SliceUntil(TimeSpan.FromMinutes(4));

    private Task<RelationshipScanSummary> RunAsync(RelationshipPatternSettings? settings = null, TimeSpan? elapsed = null) =>
        Service(settings).RunAsync(Now, Slice(elapsed), _context);

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private Dictionary<string, object> SeedOrder(
        string orderId, string buyerId, string farmerId, double hoursAgo, string status = "accepted", bool funded = true)
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = orderId,
            ["buyerId"] = buyerId,
            ["farmerId"] = farmerId,
            ["status"] = status,
            ["createdAt"] = Iso(Now.AddHours(-hoursAgo))
        };
        if (funded)
        {
            order["paymentVerifiedAt"] = Iso(Now.AddHours(-hoursAgo).AddMinutes(3));
        }

        (_byStatus.TryGetValue(status, out var list) ? list : _byStatus[status] = new()).Add(order);
        (_byBuyer.TryGetValue(buyerId, out var mine) ? mine : _byBuyer[buyerId] = new()).Insert(0, order);
        return order;
    }

    private void SeedUser(string userId, string phone, string? fayda = null, params string[] devices) =>
        _users.GetUserByIdAsync(userId).Returns(new UserDto
        {
            UserId = userId,
            PhoneNumber = phone,
            Role = "buyer",
            Woreda = "Ada'a",
            FaydaId = fayda,
            IsPhoneVerified = true,
            KnownDevices = devices.ToList(),
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

    private Task SignalWritten(string userId, string type, string orderId) =>
        _fraud.Received(1).RecordFraudSignalAsync(userId, type, orderId, "order", Arg.Any<string?>());

    // ── Each pattern from its fixture ────────────────────────

    [Fact]
    public async Task SelfTrader_OnePersonTwoAccountsOneDevice_FlagsBothAccounts()
    {
        // The FRD-17 persona: a farmer account and a buyer account signed in on one install, a paid self-pickup.
        SeedUser("self-farmer", "+251911000001", null, "install-1");
        SeedUser("self-buyer", "+251911000002", null, "install-1", "install-2");
        SeedOrder("ord-self", "self-buyer", "self-farmer", 2, status: "delivered");

        var summary = await RunAsync();

        await SignalWritten("self-buyer", "SHARED_DEVICE_TRADE", "ord-self");
        await SignalWritten("self-farmer", "SHARED_DEVICE_TRADE", "ord-self");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "SELF_TRADE", Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string?>());
        summary.Hits.Should().Be(2);
        summary.Written.Should().Be(2);
        summary.Examined.Should().Be(1);
    }

    [Fact]
    public async Task SamePhoneOnBothSides_IsSelfTrade()
    {
        SeedUser("b-1", "+251911000009", null, "install-1");
        SeedUser("f-1", "+251911000009", null, "install-1");
        SeedOrder("ord-1", "b-1", "f-1", 1);

        await RunAsync();

        await SignalWritten("b-1", "SELF_TRADE", "ord-1");
        await SignalWritten("f-1", "SELF_TRADE", "ord-1");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "SHARED_DEVICE_TRADE", Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SameFaydaIdOnBothSides_IsSelfTrade()
    {
        SeedUser("b-1", "+251911000001", "FAYDA-77");
        SeedUser("f-1", "+251911000002", "FAYDA-77");
        SeedOrder("ord-1", "b-1", "f-1", 1);

        await RunAsync();

        await SignalWritten("b-1", "SELF_TRADE", "ord-1");
        await SignalWritten("f-1", "SELF_TRADE", "ord-1");
    }

    [Fact]
    public async Task RepeatedPair_ThreeFundedOrdersInAWeek_FlagsBothParties()
    {
        SeedUser("b-1", "+251911000001");
        SeedUser("f-1", "+251911000002");
        SeedOrder("ord-old-1", "b-1", "f-1", 24 * 5, status: "settled");   // before the 48 h lookback, inside 7 days
        SeedOrder("ord-old-2", "b-1", "f-1", 24 * 3, status: "settled");
        SeedOrder("ord-new", "b-1", "f-1", 3, status: "funded");

        var summary = await RunAsync();

        await SignalWritten("b-1", "REPEATED_PAIR", "ord-new");
        await SignalWritten("f-1", "REPEATED_PAIR", "ord-new");
        summary.Examined.Should().Be(1);
    }

    [Fact]
    public async Task RepeatedPair_TwoOrders_IsNoHit()
    {
        SeedUser("b-1", "+251911000001");
        SeedUser("f-1", "+251911000002");
        SeedOrder("ord-1", "b-1", "f-1", 24 * 3, status: "settled");
        SeedOrder("ord-2", "b-1", "f-1", 3);

        var summary = await RunAsync();

        summary.Hits.Should().Be(0);
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task AnHonestTrade_WritesNothing()
    {
        SeedUser("b-1", "+251911000001", "FAYDA-1", "install-b");
        SeedUser("f-1", "+251911000002", "FAYDA-2", "install-f");
        SeedOrder("ord-1", "b-1", "f-1", 1);

        var summary = await RunAsync();

        summary.Examined.Should().Be(1);
        summary.Hits.Should().Be(0);
        await _fraud.DidNotReceiveWithAnyArgs().HasSignalAsync(default!, default!, default!);
    }

    // ── What is read ─────────────────────────────────────────

    [Fact]
    public async Task ReadsEveryFundedStatusOnTheIndex_SinceTheLookback()
    {
        await RunAsync();

        var since = Iso(Now.AddHours(-48));
        foreach (var status in RelationshipPatternRules.ScannedOrderStatuses)
        {
            await _orders.Received(1).QueryByStatusCreatedSinceAsync(status, since, 100, null);
        }

        await _orders.DidNotReceive().QueryByStatusCreatedSinceAsync("created", Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnfundedOrders_AreNotChecked()
    {
        SeedUser("b-1", "+251911000009");
        SeedUser("f-1", "+251911000009");
        SeedOrder("ord-1", "b-1", "f-1", 1, status: "cancelled", funded: false);

        var summary = await RunAsync();

        summary.OrdersRead.Should().Be(1);
        summary.Funded.Should().Be(0);
        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);
    }

    [Fact]
    public async Task FollowsTheStatusPages()
    {
        var page2Key = new Dictionary<string, AttributeValue> { ["orderId"] = new() { S = "x" } };
        _orders.QueryByStatusCreatedSinceAsync("delivered", Arg.Any<string>(), 100, null)
            .Returns((new List<Dictionary<string, object>>(), page2Key));
        SeedUser("b-1", "+251911000009");
        SeedUser("f-1", "+251911000009");
        var order = SeedOrder("ord-1", "b-1", "f-1", 1, status: "funded");
        _orders.QueryByStatusCreatedSinceAsync("delivered", Arg.Any<string>(), 100, page2Key)
            .Returns((new List<Dictionary<string, object>> { order }, (Dictionary<string, AttributeValue>?)null));

        var summary = await RunAsync();

        // Seen on the second delivered page and in the funded partition: checked once.
        summary.Examined.Should().Be(1);
        await SignalWritten("b-1", "SELF_TRADE", "ord-1");
    }

    [Fact]
    public async Task ReadsEachUserAndEachBuyersHistoryOncePerRun()
    {
        SeedUser("b-1", "+251911000001");
        SeedUser("f-1", "+251911000002");
        SeedOrder("ord-1", "b-1", "f-1", 1);
        SeedOrder("ord-2", "b-1", "f-1", 2);

        await RunAsync();

        await _users.Received(1).GetUserByIdAsync("b-1");
        await _users.Received(1).GetUserByIdAsync("f-1");
        await _orders.Received(1).QueryOrdersAsync("b-1", null, null, null, null, 50, null);
    }

    [Fact]
    public async Task TheBuyerHistory_StopsAtTheFirstOrderOlderThanThePairWindow()
    {
        var next = new Dictionary<string, AttributeValue> { ["orderId"] = new() { S = "more" } };
        SeedUser("b-1", "+251911000001");
        SeedUser("f-1", "+251911000002");
        SeedOrder("ord-1", "b-1", "f-1", 1);
        _byBuyer["b-1"].Add(new Dictionary<string, object>
        {
            ["orderId"] = "ord-ancient",
            ["farmerId"] = "f-1",
            ["createdAt"] = Iso(Now.AddDays(-30)),
            ["paymentVerifiedAt"] = "x"
        });
        _orders.QueryOrdersAsync("b-1", null, null, null, null, 50, null)
            .Returns((_byBuyer["b-1"], next));

        await RunAsync();

        await _orders.DidNotReceive().QueryOrdersAsync("b-1", null, null, null, null, 50, next);
    }

    // ── Idempotent ───────────────────────────────────────────

    [Fact]
    public async Task ALaterRun_FindsTheRowAndWritesNothing()
    {
        SeedUser("self-farmer", "+251911000001", null, "install-1");
        SeedUser("self-buyer", "+251911000002", null, "install-1");
        SeedOrder("ord-self", "self-buyer", "self-farmer", 2);
        _fraud.HasSignalAsync(Arg.Any<string>(), "SHARED_DEVICE_TRADE", "ord-self").Returns(true);

        var summary = await RunAsync();

        summary.AlreadyRecorded.Should().Be(2);
        summary.Written.Should().Be(0);
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task ASecondHitOfTheSameUserAndType_IsDeferredToTheNextRun_ThenWritten()
    {
        // Two self-trades of the same farmer in one run: the writer keeps one row per user, type and hour.
        SeedUser("f-1", "+251911000001", null, "install-1");
        SeedUser("b-1", "+251911000002", null, "install-1");
        SeedUser("b-2", "+251911000003", null, "install-1");
        SeedOrder("ord-1", "b-1", "f-1", 1);
        SeedOrder("ord-2", "b-2", "f-1", 2);

        var first = await RunAsync();

        first.Written.Should().Be(3);   // b-1, f-1 for ord-1; b-2 for ord-2
        first.Deferred.Should().Be(1);  // f-1 for ord-2
        await SignalWritten("f-1", "SHARED_DEVICE_TRADE", "ord-1");
        await _fraud.DidNotReceive().RecordFraudSignalAsync("f-1", "SHARED_DEVICE_TRADE", "ord-2", "order", Arg.Any<string?>());

        // The next hour: the rows of the first run are found, the deferred one is written.
        _fraud.HasSignalAsync("b-1", "SHARED_DEVICE_TRADE", "ord-1").Returns(true);
        _fraud.HasSignalAsync("f-1", "SHARED_DEVICE_TRADE", "ord-1").Returns(true);
        _fraud.HasSignalAsync("b-2", "SHARED_DEVICE_TRADE", "ord-2").Returns(true);
        _fraud.ClearReceivedCalls();

        var second = await RunAsync();

        second.Written.Should().Be(1);
        second.AlreadyRecorded.Should().Be(3);
        await SignalWritten("f-1", "SHARED_DEVICE_TRADE", "ord-2");
    }

    // ── Caps and failures ────────────────────────────────────

    [Fact]
    public async Task TheOrderCap_TruncatesTheRunAndLogsIt()
    {
        SeedUser("b-1", "+251911000001");
        SeedUser("f-1", "+251911000002");
        SeedOrder("ord-1", "b-1", "f-1", 1);
        SeedOrder("ord-2", "b-1", "f-1", 2);
        SeedOrder("ord-3", "b-1", "f-1", 3);

        var summary = await RunAsync(RelationshipPatternSettings.Default with { MaxOrders = 2 });

        summary.Truncated.Should().BeTrue();
        summary.Funded.Should().Be(2);
        summary.Examined.Should().Be(2);
        Logs.Should().Contain("FRAUD_RELATIONSHIP_SCAN_TRUNCATED funded=2 cap=2");
    }

    [Fact]
    public async Task ARunOutOfTime_StopsBeforeTheNextRead()
    {
        SeedUser("b-1", "+251911000009");
        SeedUser("f-1", "+251911000009");
        SeedOrder("ord-1", "b-1", "f-1", 1);

        var summary = await RunAsync(elapsed: TimeSpan.FromMinutes(5));

        summary.StopReason.Should().Be("time");
        summary.Examined.Should().Be(0);
        await _orders.DidNotReceiveWithAnyArgs().QueryByStatusCreatedSinceAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task AFailedUserRead_SkipsThatOrderOnly()
    {
        _users.GetUserByIdAsync("b-bad").ThrowsAsync(new AmazonDynamoDbTestException());
        SeedUser("f-1", "+251911000009");
        SeedUser("b-1", "+251911000009");
        SeedOrder("ord-bad", "b-bad", "f-1", 1);
        SeedOrder("ord-1", "b-1", "f-1", 2);

        var summary = await RunAsync();

        summary.Failed.Should().Be(1);
        await SignalWritten("b-1", "SELF_TRADE", "ord-1");
        Logs.Should().Contain("FRAUD_RELATIONSHIP_CHECK_FAILED orderId=ord-bad");
    }

    [Fact]
    public async Task AFailedWrite_IsCounted_AndTheOtherPartyIsStillWritten()
    {
        SeedUser("b-1", "+251911000009");
        SeedUser("f-1", "+251911000009");
        SeedOrder("ord-1", "b-1", "f-1", 1);
        _fraud.RecordFraudSignalAsync("b-1", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDbTestException());

        var summary = await RunAsync();

        summary.Failed.Should().Be(1);
        summary.Written.Should().Be(1);
        await SignalWritten("f-1", "SELF_TRADE", "ord-1");
        Logs.Should().Contain("FRAUD_RELATIONSHIP_SIGNAL_FAILED type=SELF_TRADE");
    }

    [Fact]
    public async Task AFailedStatusQuery_Throws()
    {
        _orders.QueryByStatusCreatedSinceAsync("funded", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new AmazonDynamoDbTestException());

        var act = () => RunAsync();

        await act.Should().ThrowAsync<AmazonDynamoDbTestException>();
    }

    [Fact]
    public async Task TheSummaryLine_CarriesTheCounts()
    {
        SeedUser("b-1", "+251911000009");
        SeedUser("f-1", "+251911000009");
        SeedOrder("ord-1", "b-1", "f-1", 1);

        await RunAsync();

        Logs.Should().Contain(
            "FRAUD_RELATIONSHIP_SCAN ordersRead=1 funded=1 examined=1 hits=2 written=2 alreadyRecorded=0 deferred=0 failed=0 truncated=false stop=-");
    }

    private sealed class AmazonDynamoDbTestException : Exception;
}
