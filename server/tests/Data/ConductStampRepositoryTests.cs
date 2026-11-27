using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// GCR-03: the conduct stamps as DynamoDB sees them. The event query filters them out before its cap counts; a
/// stamp goes only into a free slot (or, re-evaluated, only once) under a check that the user is not being deleted;
/// the row round-trips.
/// </summary>
public class ConductStampRepositoryTests
{
    private const string Events = "test-reputation-events";
    private const string Users = "test-users";
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ReputationEventRepository _repo;

    public ConductStampRepositoryTests()
    {
        _repo = new ReputationEventRepository(_dynamoDb, Events, Users);
    }

    internal static ConductStamp Stamp(string userId = "buyer-1", string party = "buyer") => new()
    {
        UserId = userId,
        OrderId = "order-1",
        Party = party,
        FarmerId = "farmer-1",
        TruckerId = "trucker-1",
        BuyerId = "buyer-1",
        Offered = 6,
        Earned = 4,
        Steps = new[]
        {
            new ConductStep("dropoff_scanned", "earned"),
            new ConductStep("dropoff_fix", "left_out", "no_signal"),
            new ConductStep("rated_parties", "missed")
        },
        Counted = false,
        NotCountedReason = "pair_cap",
        OrderTotal = 1250.5m,
        OccurredAt = T0,
        RecordedAt = T0.AddSeconds(3),
        PurgeAt = 1_800_000_000
    };

    [Fact]
    public void TheEventsQuery_LeavesOutTheStamps()
    {
        var request = _repo.BuildEventsQuery("farmer-1");
        request.KeyConditionExpression.Should().Be("userId = :u");
        request.FilterExpression.Should().Be("attribute_not_exists(#code) OR #code <> :stamp");
        request.ExpressionAttributeNames["#code"].Should().Be("code");
        request.ExpressionAttributeValues[":stamp"].S.Should().Be("HANDOFF_QUALITY");
        request.ConsistentRead.Should().BeTrue();
    }

    [Fact]
    public void TheStampsQuery_ReadsTheHandoffSlotsOnly()
    {
        var request = _repo.BuildStampsQuery("buyer-1");
        request.TableName.Should().Be(Events);
        request.KeyConditionExpression.Should().Be("userId = :u AND begins_with(eventId, :slot)");
        request.ExpressionAttributeValues[":slot"].S.Should().Be("HANDOFF#");
        request.ConsistentRead.Should().BeTrue();

        // One stamp: a Query on the full key (the writers hold no GetItem on the table).
        var one = _repo.BuildStampQuery("buyer-1", "order-1");
        one.KeyConditionExpression.Should().Be("userId = :u AND eventId = :e");
        one.ExpressionAttributeValues[":e"].S.Should().Be("HANDOFF#order-1");
        one.ConsistentRead.Should().BeTrue();
    }

    [Fact]
    public void AFirstStamp_GoesIntoAFreeSlot_UnderTheUsersCheck()
    {
        var items = _repo.BuildStampWrite(Stamp(), replace: false).TransactItems;
        items.Should().HaveCount(2);
        items[0].Put.TableName.Should().Be(Events);
        items[0].Put.ConditionExpression.Should().Be("attribute_not_exists(eventId)");
        items[0].Put.Item["eventId"].S.Should().Be("HANDOFF#order-1");
        items[0].Put.Item["userId"].S.Should().Be("buyer-1");
        items[1].ConditionCheck.TableName.Should().Be(Users);
        items[1].ConditionCheck.Key["userId"].S.Should().Be("buyer-1");
        items[1].ConditionCheck.ConditionExpression.Should().Be("attribute_exists(userId) AND attribute_not_exists(#ds)");
        items[1].ConditionCheck.ExpressionAttributeNames["#ds"].Should().Be("deletionState");
    }

    [Fact]
    public void TheReevaluation_ReplacesOnlyAStampNotYetReevaluated()
    {
        var put = _repo.BuildStampWrite(Stamp() with { ReevaluatedAt = T0.AddDays(2), Version = 2 }, replace: true).TransactItems[0].Put;
        put.ConditionExpression.Should().Be("attribute_exists(eventId) AND attribute_not_exists(#re)");
        put.ExpressionAttributeNames["#re"].Should().Be("reevaluatedAt");
        put.Item["reevaluatedAt"].S.Should().Be("2026-11-22T09:00:00.000Z");
        put.Item["version"].N.Should().Be("2");
    }

    [Fact]
    public void TheRow_RoundTrips_WithNoDueDayOrAppealStatus()
    {
        var item = ReputationEventRepository.StampToItem(Stamp());
        item["code"].S.Should().Be("HANDOFF_QUALITY");
        item["counted"].BOOL.Should().BeFalse();
        item["steps"].L.Should().HaveCount(3);
        item["steps"].L[1].M["reason"].S.Should().Be("no_signal");
        item["steps"].L[0].M.Should().NotContainKey("reason");
        item.Should().NotContainKey("dueDay").And.NotContainKey("appealStatus").And.NotContainKey("reevaluatedAt");

        ReputationEventRepository.StampFromItem(item).Should().BeEquivalentTo(Stamp());
    }

    private void CancelWith(params string[] codes)
        => _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("cancelled")
            {
                CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
            });

    [Fact]
    public async Task Outcomes_ATakenSlotIsAReplay_ADeletingUserIsSkipped_AThrottleThrows()
    {
        (await _repo.PutNewAsync(Stamp())).Should().Be(ConductWriteOutcome.Written);

        CancelWith("ConditionalCheckFailed", "None");
        (await _repo.PutNewAsync(Stamp())).Should().Be(ConductWriteOutcome.Exists);
        (await _repo.ReplaceOnceAsync(Stamp())).Should().Be(ConductWriteOutcome.Exists);

        CancelWith("None", "ConditionalCheckFailed");
        (await _repo.PutNewAsync(Stamp())).Should().Be(ConductWriteOutcome.Skipped);

        CancelWith("None", "ThrottlingError");
        var act = () => _repo.PutNewAsync(Stamp());
        await act.Should().ThrowAsync<TransactionCanceledException>();
    }

    // ===== GCR-04: the figure's read =====

    [Fact]
    public void TheCountedStampsQuery_ReadsTheHandoffSlots_CountedAndInTheWindow_EventuallyConsistent()
    {
        var request = _repo.BuildCountedStampsQuery("farmer-1", T0.AddDays(-90));
        request.TableName.Should().Be(Events);
        request.KeyConditionExpression.Should().Be("userId = :u AND begins_with(eventId, :slot)");
        request.FilterExpression.Should().Be("#code = :stamp AND #counted = :yes AND #at > :since");
        request.ExpressionAttributeNames.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["#code"] = "code",
            ["#counted"] = "counted",
            ["#at"] = "occurredAt"
        });
        request.ExpressionAttributeValues[":u"].S.Should().Be("farmer-1");
        request.ExpressionAttributeValues[":slot"].S.Should().Be("HANDOFF#");
        request.ExpressionAttributeValues[":stamp"].S.Should().Be("HANDOFF_QUALITY");
        request.ExpressionAttributeValues[":yes"].BOOL.Should().BeTrue();
        // The stored occurredAt is the same ISO text, so the string comparison is the time order.
        request.ExpressionAttributeValues[":since"].S.Should().Be("2026-08-22T09:00:00.000Z");
        request.ConsistentRead.Should().BeFalse("a read path; the reputation Lambda's Query grant covers either");
        request.IndexName.Should().BeNull();
    }

    [Fact]
    public async Task TheCountedStampsQuery_PagesToTheEnd_AndSaysWhenTheCapCutIt()
    {
        var first = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = "farmer-1" }, ["eventId"] = new() { S = "HANDOFF#o-2" } };
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.ExclusiveStartKey == null), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    ReputationEventRepository.StampToItem(Stamp("farmer-1", "farmer") with { OrderId = "o-1", Counted = true }),
                    ReputationEventRepository.StampToItem(Stamp("farmer-1", "farmer") with { OrderId = "o-2", Counted = true })
                },
                LastEvaluatedKey = first
            });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.ExclusiveStartKey == first), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    ReputationEventRepository.StampToItem(Stamp("farmer-1", "farmer") with { OrderId = "o-3", Counted = true })
                }
            });

        var (all, truncated) = await _repo.QueryCountedStampsAsync("farmer-1", T0.AddDays(-90), 10);
        all.Select(s => s.OrderId).Should().Equal("o-1", "o-2", "o-3");
        all.Should().OnlyContain(s => s.Counted && s.Offered == 6 && s.Earned == 4);
        truncated.Should().BeFalse();

        var (capped, cut) = await _repo.QueryCountedStampsAsync("farmer-1", T0.AddDays(-90), 2);
        capped.Select(s => s.OrderId).Should().Equal("o-1", "o-2");
        cut.Should().BeTrue();
    }
}
