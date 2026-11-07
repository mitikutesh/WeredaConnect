using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-28: the reputation transaction and reads as DynamoDB sees them (reputation-architecture.md
/// 6.1, 6.2): the items in order with their real condition expressions, the cancellation reasons
/// mapped to a replay or a conflict, strongly consistent reads, the users row written only on its
/// <c>reputation</c> path.
/// </summary>
public class ReputationEventRepositoryTests
{
    private const string Events = "test-reputation-events";
    private const string Users = "test-users";
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ReputationEventRepository _repo;

    public ReputationEventRepositoryTests()
    {
        _repo = new ReputationEventRepository(_dynamoDb, Events, Users);
    }

    private static ReputationEvent Penalty() => new()
    {
        UserId = "farmer-1",
        EventId = "CANCEL#order-1",
        Code = ReputationCodes.CancelledAfterAccept,
        Party = "farmer",
        BasePoints = 3,
        Points = 3,
        BuyerId = "buyer-1",
        OrderId = "order-1",
        OccurredAt = T0,
        RecordedAt = T0.AddSeconds(4),
        PointsExpireAt = T0.AddDays(90),
        DueDay = "2027-02-05",
        DueAt = "2027-02-05T09:00:00.000Z",
        ActivePointsAfter = 3,
        PurgeAt = 1_800_000_000
    };

    private static ReputationWrite FullWrite(int? expectedVersion) => new()
    {
        UserId = "farmer-1",
        ExpectedMapVersion = expectedVersion,
        Map = new ReputationMapWrite
        {
            Aggregate = new ReputationAggregate
            {
                ActivePoints = 10,
                Tier = ReputationTiers.Restricted,
                EffectiveRating = 3.8m,
                Restriction = new ReputationEvent
                {
                    UserId = "farmer-1",
                    EventId = "RESTRICTION#x",
                    Cause = ReputationCauses.Points,
                    Since = T0,
                    Until = T0.AddDays(14)
                }
            },
            Version = (expectedVersion ?? 0) + 1,
            UpdatedAt = T0,
            LastEventId = "CANCEL#order-1",
            LastEventRecordedAt = T0
        },
        NewEvent = Penalty(),
        NewSystemRows = new[]
        {
            new ReputationEvent { UserId = "farmer-1", EventId = "RESTRICTION#2026-11-07T09:00:00.000Z", Code = ReputationCodes.Restriction }
        },
        EarnBack = new ReputationEarnBackUpdate("REQUEST#order-0", 2),
        HealAt = T0
    };

    [Fact]
    public void TheTransaction_ItemsInOrder_WithTheirConditions()
    {
        var request = _repo.BuildTransaction(FullWrite(expectedVersion: 6));
        var items = request.TransactItems;

        items.Should().HaveCount(5);

        items[0].Put.TableName.Should().Be(Events);
        items[0].Put.ConditionExpression.Should().Be("attribute_not_exists(eventId)");
        items[0].Put.Item["eventId"].S.Should().Be("CANCEL#order-1");
        items[0].Put.Item["points"].N.Should().Be("3");
        items[0].Put.Item["dueDay"].S.Should().Be("2027-02-05");
        items[0].Put.Item["activePointsAfter"].N.Should().Be("3");
        items[0].Put.Item["purgeAt"].N.Should().Be("1800000000");

        var users = items[1].Update;
        users.TableName.Should().Be(Users);
        users.UpdateExpression.Should().Be("SET #rep = :map");
        users.ConditionExpression.Should().Be("attribute_exists(userId) AND attribute_not_exists(#ds) AND #rep.#v = :v");
        users.ExpressionAttributeValues[":v"].N.Should().Be("6");
        var map = users.ExpressionAttributeValues[":map"].M;
        map["version"].N.Should().Be("7");
        map["tier"].S.Should().Be("restricted");
        map["effectiveRating"].N.Should().Be("3.8");
        map["restriction"].M["until"].S.Should().Be("2026-11-21T09:00:00.000Z");
        map["lastEventId"].S.Should().Be("CANCEL#order-1");
        // The row's own version and updatedAt are never written (user-entity.md invariant 12).
        users.UpdateExpression.Should().NotContain("updatedAt").And.NotContain("version");

        items[2].Put.ConditionExpression.Should().Be("attribute_not_exists(eventId)");
        items[2].Put.Item["eventId"].S.Should().StartWith("RESTRICTION#");

        var earnBack = items[3].Update;
        earnBack.Key["eventId"].S.Should().Be("REQUEST#order-0");
        earnBack.UpdateExpression.Should().Be("SET #eb = #eb + :one, #v = #v + :one");
        earnBack.ConditionExpression.Should().Be("#v = :seen AND #st = :active");
        earnBack.ExpressionAttributeValues[":seen"].N.Should().Be("2");

        var heal = items[4].Update;
        heal.Key["eventId"].S.Should().Be("HEAL#2026-11-07");
        heal.UpdateExpression.Should().Be("SET #code = :heal, #dd = :day, #da = if_not_exists(#da, :at), #pa = :purge");
        heal.ConditionExpression.Should().BeNull();
    }

    [Fact]
    public void TheFirstWrite_RequiresNoMapYet()
    {
        var users = _repo.BuildTransaction(FullWrite(expectedVersion: null)).TransactItems[1].Update;

        users.ConditionExpression.Should().Be("attribute_exists(userId) AND attribute_not_exists(#ds) AND attribute_not_exists(#rep)");
        users.ExpressionAttributeValues[":map"].M["version"].N.Should().Be("1");
    }

    [Fact]
    public void ADailyWrite_TransitionsAndDeletes()
    {
        var request = _repo.BuildTransaction(new ReputationWrite
        {
            UserId = "farmer-1",
            ExpectedMapVersion = 3,
            Map = new ReputationMapWrite { Aggregate = new ReputationAggregate(), Version = 4, UpdatedAt = T0 },
            Transitions = new[]
            {
                new ReputationRowTransition("CANCEL#order-1", ReputationStatuses.Active, ReputationStatuses.Expired),
                new ReputationRowTransition("CANCEL#order-2", ReputationStatuses.Excused, null)
            },
            Deletes = new[] { "HEAL#2026-11-06" }
        });

        var items = request.TransactItems;
        items.Should().HaveCount(4);
        items[1].Update.UpdateExpression.Should().Be("SET #st = :to, #v = if_not_exists(#v, :one) + :one REMOVE #dd, #da");
        items[1].Update.ConditionExpression.Should().Be("#st = :from");
        items[1].Update.ExpressionAttributeValues[":to"].S.Should().Be("expired");
        items[2].Update.UpdateExpression.Should().Be("REMOVE #dd, #da");
        items[2].Update.ExpressionAttributeValues[":from"].S.Should().Be("excused");
        items[3].Delete.Key["eventId"].S.Should().Be("HEAL#2026-11-06");
    }

    private void CancelWith(params string[] codes)
        => _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
            });

    [Fact]
    public async Task TheEventsCondition_IsAReplay()
    {
        CancelWith("ConditionalCheckFailed", "None", "None", "None", "None");
        (await _repo.CommitAsync(FullWrite(6))).Should().Be(ReputationCommitOutcome.EventExists);
    }

    [Theory]
    [InlineData("None", "ConditionalCheckFailed")]
    [InlineData("None", "TransactionConflict")]
    [InlineData("None", "None", "None", "ConditionalCheckFailed")]
    public async Task TheUsersOrARowsCondition_IsAConflict(params string[] codes)
    {
        CancelWith(codes);
        (await _repo.CommitAsync(FullWrite(6))).Should().Be(ReputationCommitOutcome.Conflict);
    }

    [Fact]
    public async Task AThrottle_Throws()
    {
        CancelWith("None", "ThrottlingError");
        var act = () => _repo.CommitAsync(FullWrite(6));
        await act.Should().ThrowAsync<TransactionCanceledException>();
    }

    [Fact]
    public async Task TheEventsQuery_IsStronglyConsistent_AndStopsAtTheCap()
    {
        var requests = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => requests.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = Enumerable.Range(0, 3).Select(i => ReputationEventRepository.ToItem(Penalty() with { EventId = $"CANCEL#o{i}" })).ToList(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = "farmer-1" } }
            });

        var (rows, truncated) = await _repo.QueryEventsAsync("farmer-1", cap: 5);

        rows.Should().HaveCount(5);
        truncated.Should().BeTrue();
        requests.Should().OnlyContain(r => r.ConsistentRead == true && r.TableName == Events && r.IndexName == null);
        rows[0].Points.Should().Be(3);
        rows[0].OccurredAt.Should().Be(T0);
        rows[0].PointsExpireAt.Should().Be(T0.AddDays(90));
    }

    [Fact]
    public async Task TheUsersRead_IsStronglyConsistent_AndParsesTheMap()
    {
        GetItemRequest? sent = null;
        _dynamoDb.GetItemAsync(Arg.Do<GetItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["userId"] = new() { S = "farmer-1" },
                    ["role"] = new() { S = "farmer" },
                    ["suspended"] = new() { BOOL = true, IsBOOLSet = true },
                    ["ratingSum"] = new() { N = "58" },
                    ["ratingCount"] = new() { N = "12" },
                    ["reputation"] = new()
                    {
                        M = new Dictionary<string, AttributeValue>
                        {
                            ["version"] = new() { N = "7" },
                            ["tier"] = new() { S = "warning" },
                            ["effectiveRating"] = new() { N = "4.4" },
                            ["lastEventId"] = new() { S = "CANCEL#o1" },
                            ["lastEventRecordedAt"] = new() { S = "2026-11-07T09:00:00.000Z" }
                        }
                    }
                }
            });

        var state = await _repo.GetUserStateAsync("farmer-1");

        sent!.ConsistentRead.Should().BeTrue();
        state!.Suspended.Should().BeTrue();
        state.MapVersion.Should().Be(7);
        state.StoredTier.Should().Be("warning");
        state.StoredEffectiveRating.Should().Be(4.4m);
        state.RatingSum.Should().Be(58);
        state.RatingCount.Should().Be(12);
        state.LastEventRecordedAt.Should().Be(T0);
    }

    [Fact]
    public async Task DueKeys_ComeFromOneDayPartition()
    {
        QueryRequest? sent = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { ["userId"] = new() { S = "farmer-1" }, ["eventId"] = new() { S = "CANCEL#o1" } }
                }
            });

        var keys = await _repo.QueryDueAsync("2026-11-07", "2026-11-07T09:00:00.000Z");

        keys.Should().Equal(("farmer-1", "CANCEL#o1"));
        sent!.IndexName.Should().Be("DueIndex");
        sent.KeyConditionExpression.Should().Be("#dd = :day AND #da <= :at");
    }

    [Fact]
    public void ARowRoundTrips()
    {
        var row = Penalty() with { EarnedBack = 1, EarnBackEventId = null, AppealStatus = "pending", AppealedAt = "2026-11-08T00:00:00.000Z" };

        var back = ReputationEventRepository.FromItem(ReputationEventRepository.ToItem(row));

        back.Should().BeEquivalentTo(row);
    }
}
