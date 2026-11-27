using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-160 (reputation-architecture.md 12.3): the order-cron's one-off owner stamp. It finds the
/// owners on the active partitions of the crop and trip status indexes (Query, every page, no
/// Scan), stamps each owner once in user id order, skips an owner without a users row, counts a
/// failure and goes on, and a run out of time names the next owner so <c>startAt</c> goes on from
/// there. Behind <c>reputationPenalties</c> in the order-cron. The real requests run in
/// <c>OwnerCopyStampDynamoDbTests</c>.
/// </summary>
public class OwnerCopyStampJobTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly FakeReputationRepository _repo = new();
    private readonly IReputationCopyWriter _copies = Substitute.For<IReputationCopyWriter>();
    private readonly List<string> _stamped = new();
    private readonly List<QueryRequest> _queries = new();
    private readonly TestLambdaContext _context = new();

    public OwnerCopyStampJobTests()
    {
        _repo.AddUser("f-1");
        _repo.AddUser("f-2");
        _repo.AddUser("t-1", "trucker");
        // f-3 has a listing but no users row.

        // available: two pages (f-1 on both); reserved: f-3; open: t-1; matched: t-1 and f-1.
        Partition("crops", "StatusCreatedAtIndex", "available",
            new[] { ("farmerId", "f-2"), ("farmerId", "f-1") }, new[] { ("farmerId", "f-1") });
        Partition("crops", "StatusCreatedAtIndex", "reserved", new[] { ("farmerId", "f-3") });
        Partition("trips", "StatusDepartureDateIndex", "open", new[] { ("truckerId", "t-1") });
        Partition("trips", "StatusDepartureDateIndex", "matched", new[] { ("truckerId", "t-1"), ("truckerId", "f-1") });

        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .Returns(call =>
            {
                _stamped.Add(call.ArgAt<string>(0));
                return 2;
            });
    }

    /// <summary>One status partition, served in pages; the second page is asked for with the first one's last key.</summary>
    private void Partition(string table, string index, string status, params (string Attribute, string Owner)[][] pages)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            var page = i;
            var response = new QueryResponse
            {
                Items = pages[i].Select(p => new Dictionary<string, AttributeValue> { [p.Attribute] = new() { S = p.Owner } }).ToList(),
                LastEvaluatedKey = page < pages.Length - 1
                    ? new Dictionary<string, AttributeValue> { ["status"] = new() { S = status }, ["page"] = new() { S = (page + 1).ToString() } }
                    : new Dictionary<string, AttributeValue>()
            };
            _dynamoDb.QueryAsync(
                    Arg.Is<QueryRequest>(r => r.TableName == table && r.IndexName == index
                                              && r.ExpressionAttributeValues[":status"].S == status
                                              && PageOf(r) == page),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.ArgAt<QueryRequest>(0);
                    _queries.Add(new QueryRequest
                    {
                        TableName = request.TableName,
                        IndexName = request.IndexName,
                        KeyConditionExpression = request.KeyConditionExpression,
                        ProjectionExpression = request.ProjectionExpression,
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(request.ExpressionAttributeValues)
                    });
                    return response;
                });
        }
    }

    private static int PageOf(QueryRequest request)
        => request.ExclusiveStartKey is { Count: > 0 } key && key.TryGetValue("page", out var p) ? int.Parse(p.S) : 0;

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, null, new ManualClock(T0), _ => Task.CompletedTask, _copies);

    private OwnerCopyStampJob Job() => new(Service(), _dynamoDb, "crops", "trips");

    private static CronRunSlice FullSlice() => new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4));

    /// <summary>A slice with room for exactly one owner: each read of the clock moves it a second.</summary>
    private static CronRunSlice OneOwnerSlice()
    {
        var ticks = 0;
        return new CronRunBudget(() => TimeSpan.FromSeconds(ticks++)).Slice(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WalksTheActivePartitions_StampsEachOwnerOnce_InUserIdOrder()
    {
        var summary = await Job().RunAsync(null, FullSlice(), _context);

        _stamped.Should().Equal("f-1", "f-2", "t-1");
        summary["outcome"].Should().Be("ok");
        summary["owners"].Should().Be("4");
        summary["users"].Should().Be("3");
        summary["rows"].Should().Be("6");
        summary["skipped"].Should().Be("1", "f-3 has no users row");
        summary["leftover"].Should().Be("0");
        summary["failed"].Should().Be("0");
        summary.Should().NotContainKey("next");
        _queries.Select(q => (q.TableName, q.IndexName, q.ExpressionAttributeValues[":status"].S)).Should().BeEquivalentTo(new[]
        {
            ("crops", "StatusCreatedAtIndex", "available"), ("crops", "StatusCreatedAtIndex", "available"),
            ("crops", "StatusCreatedAtIndex", "reserved"), ("trips", "StatusDepartureDateIndex", "open"),
            ("trips", "StatusDepartureDateIndex", "matched")
        });
        _queries.Should().OnlyContain(q => q.KeyConditionExpression == "#status = :status");
        await _dynamoDb.DidNotReceiveWithAnyArgs().ScanAsync(default(ScanRequest)!, default);
        _repo.Committed.Should().BeEmpty("the stamp writes no reputation map");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(
            $"{OwnerCopyStampJob.SummaryToken} owners=4 users=3 rows=6 skipped=1 leftover=0 failed=0 from=start");
    }

    [Fact]
    public async Task OutOfTime_NamesTheNextOwner_AndStartAtGoesOnFromThere()
    {
        var first = await Job().RunAsync(null, OneOwnerSlice(), _context);

        _stamped.Should().Equal("f-1");
        first["outcome"].Should().Be("partial");
        first["leftover"].Should().Be("3");
        first["next"].Should().Be("f-2");

        _stamped.Clear();
        var second = await Job().RunAsync(first["next"], FullSlice(), _context);

        _stamped.Should().Equal("f-2", "t-1");
        second["outcome"].Should().Be("ok");
        second["users"].Should().Be("2");
        second["skipped"].Should().Be("1");
        second.Should().NotContainKey("next");
    }

    [Fact]
    public async Task AFailedOwner_IsCountedAndLogged_AndTheRunGoesOn()
    {
        _copies.RefreshOwnerRowsAsync("f-2", Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var summary = await Job().RunAsync(null, FullSlice(), _context);

        summary["failed"].Should().Be("1");
        summary["users"].Should().Be("2");
        summary["outcome"].Should().Be("partial");
        _stamped.Should().Equal("f-1", "t-1");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("Owner stamp failed for user")
            .And.Contain("ProvisionedThroughputExceededException");
    }

    [Theory]
    [InlineData("f-1", true)]
    [InlineData("8c1f2a5e-0d5b-4c7e-9a43-2f0b6c1d7e90", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a\nb", false)]
    [InlineData(null, false)]
    public void StartAt_IsAUserId(string? value, bool valid)
        => OwnerCopyStampJob.IsValidStartAt(value).Should().Be(valid);

    [Fact]
    public void StartAt_LongerThanAUserId_IsRefused()
        => OwnerCopyStampJob.IsValidStartAt(new string('a', OwnerCopyStampJob.MaxStartAtLength + 1)).Should().BeFalse();

    // ===== the order-cron action =====

    private static APIGatewayProxyRequest Body(string json) => new() { Body = json };

    [Fact]
    public async Task OrderCron_FlagOff_ReadsAndWritesNothing()
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>())
        {
            ReputationPenaltiesEnabled = false,
            OwnerCopyStamp = Job()
        };

        var response = await cron.FunctionHandler(Body("{\"action\":\"reputation_stamp_owner_copies\"}"), new TestLambdaContext());

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"disabled\"");
        await _dynamoDb.DidNotReceiveWithAnyArgs().QueryAsync(default(QueryRequest)!, default);
        _stamped.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderCron_FlagOn_Unconfigured_SaysSo()
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { ReputationPenaltiesEnabled = true };

        var response = await cron.FunctionHandler(Body("{\"action\":\"reputation_stamp_owner_copies\"}"), new TestLambdaContext());

        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
    }

    [Fact]
    public async Task OrderCron_FlagOn_RunsTheStamp_FromStartAt()
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>())
        {
            ReputationPenaltiesEnabled = true,
            OwnerCopyStamp = Job()
        };

        var response = await cron.FunctionHandler(
            Body("{\"action\":\"reputation_stamp_owner_copies\",\"startAt\":\"f-2\"}"), new TestLambdaContext());

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"action\":\"reputation_stamp_owner_copies\"").And.Contain("\"users\":\"2\"");
        _stamped.Should().Equal("f-2", "t-1");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task OrderCron_ABadStartAt_Is400(string value)
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>())
        {
            ReputationPenaltiesEnabled = true,
            OwnerCopyStamp = Job()
        };

        var response = await cron.FunctionHandler(
            Body($"{{\"action\":\"reputation_stamp_owner_copies\",\"startAt\":{value}}}"), new TestLambdaContext());

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ORDER_CRON_VALIDATION");
        _stamped.Should().BeEmpty();
    }
}
