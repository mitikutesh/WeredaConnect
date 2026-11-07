using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-17: the trip moves the server makes on its own. The departed open trips are read from the
/// StatusDepartureDateIndex below the browse cutoff (no Scan); closing one and finishing one are
/// single writes conditioned on the version read and the status, and closing also on no booking,
/// so a reservation that lands after the read refuses the close. These tests assert the request
/// shapes; no DynamoDB integration test runs the expressions.
/// </summary>
public class TripRepositoryLifecycleTests
{
    private const string Table = "trips-test";
    private const string TripId = "trip-1";
    private const string CreatedAt = "2026-10-20T08:00:00.000Z";
    private const string Cutoff = "2026-11-06T21:00:00.000Z";
    private const string Now = "2026-11-07T08:00:00.000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly TripRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly List<QueryRequest> _queries = new();

    public TripRepositoryLifecycleTests()
    {
        _repo = new TripRepository(_dynamoDb, Table);
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [new() { { "tripId", new AttributeValue { S = TripId } } }],
                LastEvaluatedKey = new() { { "tripId", new AttributeValue { S = TripId } } }
            });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
    }

    // --- QueryDepartedOpenAsync ---

    [Fact]
    public async Task DepartedOpen_ReadsTheOpenPartitionBelowTheCutoff_OldestFirst()
    {
        var startKey = new Dictionary<string, AttributeValue> { { "tripId", new AttributeValue { S = "trip-0" } } };

        var (items, next) = await _repo.QueryDepartedOpenAsync(Cutoff, 100, startKey);

        items.Should().ContainSingle();
        next.Should().NotBeNull();
        var query = _queries.Should().ContainSingle().Subject;
        query.TableName.Should().Be(Table);
        query.IndexName.Should().Be("StatusDepartureDateIndex");
        query.KeyConditionExpression.Should().Be("#status = :open AND #departureDate < :cutoff");
        query.FilterExpression.Should().Be("attribute_not_exists(#deletedAt)");
        query.ExpressionAttributeValues[":open"].S.Should().Be("open");
        query.ExpressionAttributeValues[":cutoff"].S.Should().Be(Cutoff);
        query.Limit.Should().Be(100);
        query.ScanIndexForward.Should().BeTrue();
        query.ExclusiveStartKey.Should().BeSameAs(startKey);
    }

    [Fact]
    public async Task DepartedOpen_LastPage_HasNoNextKey()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [], LastEvaluatedKey = new() });

        var (items, next) = await _repo.QueryDepartedOpenAsync(Cutoff, 100);

        items.Should().BeEmpty();
        next.Should().BeNull();
    }

    // --- TryExpireUnbookedAsync ---

    [Fact]
    public async Task Expire_ClosesTheTrip_OnlyIfStillOpenUnbookedDepartedAndUnchanged()
    {
        var closed = await _repo.TryExpireUnbookedAsync(TripId, CreatedAt, 3, Cutoff, Now);

        closed.Should().BeTrue();
        var update = _updates.Should().ContainSingle().Subject;
        update.TableName.Should().Be(Table);
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be(
            "SET #status = :cancelled, #cancelReason = :expired, #cancelledAt = :now, #updated = :now, #v = :next");
        update.ConditionExpression.Should().Be(
            "#v = :expected AND #status = :open AND attribute_not_exists(#deletedAt)"
            + " AND #departureDate < :cutoff AND attribute_not_exists(#bookedIds)"
            + " AND (attribute_not_exists(#booked) OR #booked <= :zero)");
        update.ExpressionAttributeNames.Should().Contain(new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#cancelReason", "cancelReason" },
            { "#cancelledAt", "cancelledAt" },
            { "#updated", "updatedAt" },
            { "#v", "version" },
            { "#deletedAt", "deletedAt" },
            { "#departureDate", "departureDate" },
            { "#bookedIds", "bookedOrderIds" },
            { "#booked", "bookedQuintals" }
        });
        update.ExpressionAttributeValues[":cancelled"].S.Should().Be("cancelled");
        update.ExpressionAttributeValues[":expired"].S.Should().Be("expired");
        update.ExpressionAttributeValues[":now"].S.Should().Be(Now);
        update.ExpressionAttributeValues[":expected"].N.Should().Be("3");
        update.ExpressionAttributeValues[":next"].N.Should().Be("4");
        update.ExpressionAttributeValues[":open"].S.Should().Be("open");
        update.ExpressionAttributeValues[":cutoff"].S.Should().Be(Cutoff);
        update.ExpressionAttributeValues[":zero"].N.Should().Be("0");
    }

    [Fact]
    public async Task Expire_RefusedCondition_IsFalseNotThrown()
    {
        // A funding reserved the trip, the trucker edited it, or a re-run finds it closed already.
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("condition failed"));

        var closed = await _repo.TryExpireUnbookedAsync(TripId, CreatedAt, 3, Cutoff, Now);

        closed.Should().BeFalse();
    }

    // --- TryMarkDeliveredAsync ---

    [Fact]
    public async Task MarkDelivered_FinishesTheTrip_OnlyFromInTransitAtTheVersionRead()
    {
        var marked = await _repo.TryMarkDeliveredAsync(TripId, CreatedAt, 5, Now);

        marked.Should().BeTrue();
        var update = _updates.Should().ContainSingle().Subject;
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be("SET #status = :delivered, #updated = :now, #v = :next");
        update.ConditionExpression.Should().Be(
            "#v = :expected AND #status = :inTransit AND attribute_not_exists(#deletedAt)");
        update.ExpressionAttributeValues[":delivered"].S.Should().Be("delivered");
        update.ExpressionAttributeValues[":inTransit"].S.Should().Be("inTransit");
        update.ExpressionAttributeValues[":expected"].N.Should().Be("5");
        update.ExpressionAttributeValues[":next"].N.Should().Be("6");
        update.ExpressionAttributeValues[":now"].S.Should().Be(Now);
    }

    [Fact]
    public async Task MarkDelivered_TruckerWasFirst_IsFalseNotThrown()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("condition failed"));

        var marked = await _repo.TryMarkDeliveredAsync(TripId, CreatedAt, 5, Now);

        marked.Should().BeFalse();
    }
}
