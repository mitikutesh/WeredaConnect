using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-15: trip bookings. Order quantity is kg, trip capacity is quintals; a booking is reserved
/// at funding, confirmed at the trucker's accept and released when the order leaves the trip.
/// DynamoDB conditions cannot do arithmetic, so the atomic guard is on <c>remainingQuintals</c>,
/// and a refused write is classified from a strongly consistent re-read. These tests assert the
/// request shapes and the classification paths; no DynamoDB integration test runs the expressions yet.
/// </summary>
public class TripRepositoryBookingTests
{
    private const string Table = "trips-test";
    private const string TripId = "trip-1";
    private const string CreatedAt = "2026-10-20T08:00:00Z";
    private const string OrderId = "order-1";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly TripRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly List<QueryRequest> _queries = new();

    public TripRepositoryBookingTests()
    {
        _repo = new TripRepository(_dynamoDb, Table);
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row()] });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(remaining: "147.5", booked: "2.5", bookedIds: [OrderId]) });
    }

    // --- TryReserveCapacityAsync ---

    [Fact]
    public async Task Reserve_TakesTheRoomInOneGuardedWrite()
    {
        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Reserved, 147.5));
        _updates.Should().ContainSingle();
        var update = _updates[0];
        update.TableName.Should().Be(Table);
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be(
            "SET #remaining = if_not_exists(#remaining, #capacity) - :q, #booked = if_not_exists(#booked, :zero) + :q, #updated = :now ADD #bookedIds :ids");
        update.ConditionExpression.Should().Be(
            "((attribute_not_exists(#remaining) AND #capacity >= :q) OR #remaining >= :q)"
            + " AND (attribute_not_exists(#bookedIds) OR NOT contains(#bookedIds, :orderId))"
            + " AND #status IN (:open, :matched) AND attribute_not_exists(#deletedAt)");
        update.ExpressionAttributeNames.Should().Contain(new Dictionary<string, string>
        {
            { "#remaining", "remainingQuintals" },
            { "#capacity", "capacity" },
            { "#booked", "bookedQuintals" },
            { "#bookedIds", "bookedOrderIds" },
            { "#updated", "updatedAt" },
            { "#status", "status" },
            { "#deletedAt", "deletedAt" }
        });
        update.ExpressionAttributeValues[":q"].N.Should().Be("2.5");
        update.ExpressionAttributeValues[":zero"].N.Should().Be("0");
        update.ExpressionAttributeValues[":ids"].SS.Should().Equal(OrderId);
        update.ExpressionAttributeValues[":orderId"].S.Should().Be(OrderId);
        update.ExpressionAttributeValues[":open"].S.Should().Be("open");
        update.ExpressionAttributeValues[":matched"].S.Should().Be("matched");
        update.ExpressionAttributeValues[":now"].S.Should().NotBeNullOrEmpty();
        update.ReturnValues.Should().Be(ReturnValue.ALL_NEW);
    }

    [Fact]
    public async Task Reserve_ThatFillsTheTrip_MarksItMatched()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new UpdateItemResponse { Attributes = Row(status: "open", remaining: "0", booked: "150", bookedIds: [OrderId]) },
                new UpdateItemResponse());

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 150);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Reserved, 0));
        _updates.Should().HaveCount(2);
        var status = _updates[1];
        status.Key["tripId"].S.Should().Be(TripId);
        status.Key["createdAt"].S.Should().Be(CreatedAt);
        status.UpdateExpression.Should().Be("SET #status = :to, #updated = :now ADD #v :one");
        status.ConditionExpression.Should().Be("#status = :from AND attribute_not_exists(#deletedAt)");
        status.ExpressionAttributeValues[":from"].S.Should().Be("open");
        status.ExpressionAttributeValues[":to"].S.Should().Be("matched");
        status.ExpressionAttributeValues[":one"].N.Should().Be("1");
        status.ExpressionAttributeNames["#v"].Should().Be("version");
        // The sort key came from the first read: no second lookup for the status write.
        _queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Reserve_ThatFillsTheTrip_LosingTheStatusRace_IsStillReserved()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new UpdateItemResponse { Attributes = Row(status: "open", remaining: "0", bookedIds: [OrderId]) },
                _ => throw new ConditionalCheckFailedException("already matched"));

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 150);

        result.Outcome.Should().Be(TripReserveOutcome.Reserved);
        _updates.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reserve_ThatFillsAnAlreadyMatchedTrip_WritesNoStatus()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(status: "matched", remaining: "0", bookedIds: [OrderId]) });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 10);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Reserved, 0));
        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Reserve_WithRoomLeft_WritesNoStatus()
    {
        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Outcome.Should().Be(TripReserveOutcome.Reserved);
        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Reserve_MissingTrip_IsNotFound_WithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.NotFound, null));
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Reserve_Refused_OrderAlreadyBooked_IsAlreadyReserved_WithTheRowsRoom()
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [Row(status: "matched", remaining: "0", booked: "150", bookedIds: ["order-9", OrderId])] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.AlreadyReserved, 0));
        _updates.Should().ContainSingle();
        _queries.Should().HaveCount(2);
        _queries[1].ConsistentRead.Should().BeTrue();
    }

    [Fact]
    public async Task Reserve_Refused_OrderAlreadyBooked_WithoutARemainingAttribute_ReportsTheCapacity()
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [Row(bookedIds: [OrderId])] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.AlreadyReserved, 150));
    }

    [Fact]
    public async Task Reserve_Refused_RowGone_IsNotFound()
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.NotFound, null));
    }

    [Fact]
    public async Task Reserve_Refused_DeletedTrip_IsUnavailable()
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [Row(status: "open", remaining: "100", deleted: true)] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Unavailable, null));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    public async Task Reserve_Refused_TripNoLongerTakingBookings_IsUnavailable(string status)
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [Row(status: status, remaining: "100")] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Unavailable, null));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("matched")]
    public async Task Reserve_Refused_LiveTripWithoutEnoughRoom_IsFull_WithTheRoomLeft(string status)
    {
        RefuseTheWrite();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [Row()] },
                new QueryResponse { Items = [Row(status: status, remaining: "1", booked: "149", bookedIds: ["order-9"])] });

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Full, 1));
    }

    [Fact]
    public async Task Reserve_Refused_LegacyTripWithoutBookings_IsFull_WithItsCapacity()
    {
        // Capacity 150, order wants 200: the guard refuses on capacity and the row has no booking attributes.
        RefuseTheWrite();

        var result = await _repo.TryReserveCapacityAsync(TripId, OrderId, 200);

        result.Should().Be(new TripReserveResult(TripReserveOutcome.Full, 150));
    }

    [Fact]
    public async Task Reserve_AnyOtherError_Propagates()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var act = () => _repo.TryReserveCapacityAsync(TripId, OrderId, 2.5);

        await act.Should().ThrowAsync<ProvisionedThroughputExceededException>();
        _queries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2.5)]
    [InlineData(double.NaN)]
    public async Task Reserve_NonPositiveQuintals_IsRefusedBeforeAnyRead(double quintals)
    {
        var act = () => _repo.TryReserveCapacityAsync(TripId, OrderId, quintals);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _queries.Should().BeEmpty();
        _updates.Should().BeEmpty();
    }

    // --- TryReleaseCapacityAsync ---

    [Fact]
    public async Task Release_GivesTheRoomBack_GuardedOnTheBooking()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(remaining: "100", booked: "50", bookedIds: [OrderId, "order-9"])] });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(remaining: "125", booked: "25", bookedIds: ["order-9"]) });

        var ok = await _repo.TryReleaseCapacityAsync(TripId, OrderId, 25);

        ok.Should().BeTrue();
        _queries.Should().ContainSingle();
        _queries[0].ConsistentRead.Should().BeTrue();
        _updates.Should().ContainSingle();
        var update = _updates[0];
        update.TableName.Should().Be(Table);
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be(
            "SET #remaining = #remaining + :q, #booked = #booked - :q, #updated = :now DELETE #bookedIds :ids");
        update.ConditionExpression.Should().Be("contains(#bookedIds, :orderId)");
        update.ExpressionAttributeNames.Should().NotContainKey("#acceptedIds");
        update.ExpressionAttributeNames["#remaining"].Should().Be("remainingQuintals");
        update.ExpressionAttributeNames["#booked"].Should().Be("bookedQuintals");
        update.ExpressionAttributeNames["#bookedIds"].Should().Be("bookedOrderIds");
        update.ExpressionAttributeValues[":q"].N.Should().Be("25");
        update.ExpressionAttributeValues[":ids"].SS.Should().Equal(OrderId);
        update.ExpressionAttributeValues[":orderId"].S.Should().Be(OrderId);
        update.ReturnValues.Should().Be(ReturnValue.ALL_NEW);
    }

    [Fact]
    public async Task Release_OfAnAcceptedOrder_LeavesTheAcceptedSetToo()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [Row(remaining: "100", booked: "50", bookedIds: [OrderId], acceptedIds: [OrderId])]
            });

        var ok = await _repo.TryReleaseCapacityAsync(TripId, OrderId, 50);

        ok.Should().BeTrue();
        _updates[0].UpdateExpression.Should().EndWith("DELETE #bookedIds :ids, #acceptedIds :ids");
        _updates[0].ExpressionAttributeNames["#acceptedIds"].Should().Be("acceptedOrderIds");
    }

    [Fact]
    public async Task Release_WhenTheAcceptedSetHoldsOtherOrders_LeavesItAlone()
    {
        // DELETE on a set is fine only when the set exists; here it does, but this order is not in it.
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [Row(remaining: "100", booked: "50", bookedIds: [OrderId, "order-9"], acceptedIds: ["order-9"])]
            });

        await _repo.TryReleaseCapacityAsync(TripId, OrderId, 25);

        _updates[0].UpdateExpression.Should().EndWith("DELETE #bookedIds :ids");
        _updates[0].ExpressionAttributeNames.Should().NotContainKey("#acceptedIds");
    }

    [Fact]
    public async Task Release_ThatGivesAMatchedTripRoom_ReopensIt()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(status: "matched", remaining: "0", booked: "150", bookedIds: [OrderId])] });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new UpdateItemResponse { Attributes = Row(status: "matched", remaining: "150", booked: "0") },
                new UpdateItemResponse());

        var ok = await _repo.TryReleaseCapacityAsync(TripId, OrderId, 150);

        ok.Should().BeTrue();
        _updates.Should().HaveCount(2);
        var status = _updates[1];
        status.Key["createdAt"].S.Should().Be(CreatedAt);
        status.UpdateExpression.Should().Be("SET #status = :to, #updated = :now ADD #v :one");
        status.ConditionExpression.Should().Be("#status = :from AND attribute_not_exists(#deletedAt)");
        status.ExpressionAttributeValues[":from"].S.Should().Be("matched");
        status.ExpressionAttributeValues[":to"].S.Should().Be("open");
        _queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_OnAMatchedTripThatIsStillFull_WritesNoStatus()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(status: "matched", remaining: "0", bookedIds: [OrderId])] });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(status: "matched", remaining: "0") });

        (await _repo.TryReleaseCapacityAsync(TripId, OrderId, 10)).Should().BeTrue();

        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_OnAnOpenTrip_WritesNoStatus()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(status: "open", remaining: "100", bookedIds: [OrderId])] });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(status: "open", remaining: "125") });

        (await _repo.TryReleaseCapacityAsync(TripId, OrderId, 25)).Should().BeTrue();

        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_OnADeletedMatchedTrip_WritesNoStatus()
    {
        // A cancelled trip gives its bookings back but never comes back on the browse list.
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(status: "matched", remaining: "0", bookedIds: [OrderId], deleted: true)] });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = Row(status: "matched", remaining: "150", deleted: true) });

        (await _repo.TryReleaseCapacityAsync(TripId, OrderId, 150)).Should().BeTrue();

        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_Refused_ReturnsFalse()
    {
        RefuseTheWrite();

        (await _repo.TryReleaseCapacityAsync(TripId, OrderId, 25)).Should().BeFalse();

        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_MissingTrip_ReturnsFalseWithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        (await _repo.TryReleaseCapacityAsync(TripId, OrderId, 25)).Should().BeFalse();

        _updates.Should().BeEmpty();
    }

    // --- TrySetStatusAsync ---

    [Fact]
    public async Task SetStatus_IsOneWriteGuardedOnTheStatusAndNoDeletion()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var ok = await _repo.TrySetStatusAsync(TripId, "open", "matched");

        ok.Should().BeTrue();
        _updates.Should().ContainSingle();
        var update = _updates[0];
        update.TableName.Should().Be(Table);
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be("SET #status = :to, #updated = :now ADD #v :one");
        update.ConditionExpression.Should().Be("#status = :from AND attribute_not_exists(#deletedAt)");
        update.ExpressionAttributeNames.Should().Contain(new Dictionary<string, string>
        {
            { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" }, { "#deletedAt", "deletedAt" }
        });
        update.ExpressionAttributeValues[":from"].S.Should().Be("open");
        update.ExpressionAttributeValues[":to"].S.Should().Be("matched");
        update.ExpressionAttributeValues[":one"].N.Should().Be("1");
        update.ExpressionAttributeValues.Should().NotContainKey(":expectedVersion");
    }

    [Fact]
    public async Task SetStatus_Refused_ReturnsFalse()
    {
        RefuseTheWrite();

        (await _repo.TrySetStatusAsync(TripId, "matched", "open")).Should().BeFalse();
    }

    [Fact]
    public async Task SetStatus_MissingTrip_ReturnsFalseWithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        (await _repo.TrySetStatusAsync(TripId, "open", "matched")).Should().BeFalse();

        _updates.Should().BeEmpty();
    }

    // --- BuildConfirmBookingItem ---

    [Fact]
    public void ConfirmBookingItem_IsTheTripLegOfTheAcceptTransaction()
    {
        var item = _repo.BuildConfirmBookingItem(TripId, CreatedAt, OrderId, "2026-10-26T09:00:00Z");

        item.Update.Should().NotBeNull();
        item.Put.Should().BeNull();
        item.ConditionCheck.Should().BeNull();
        var update = item.Update;
        update.TableName.Should().Be(Table);
        update.Key["tripId"].S.Should().Be(TripId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be("SET #updated = :now ADD #acceptedIds :ids");
        update.ConditionExpression.Should().Be(
            "contains(#bookedIds, :orderId) AND #status IN (:open, :matched) AND attribute_not_exists(#deletedAt)");
        update.ExpressionAttributeNames.Should().Contain(new Dictionary<string, string>
        {
            { "#updated", "updatedAt" },
            { "#acceptedIds", "acceptedOrderIds" },
            { "#bookedIds", "bookedOrderIds" },
            { "#status", "status" },
            { "#deletedAt", "deletedAt" }
        });
        update.ExpressionAttributeValues[":now"].S.Should().Be("2026-10-26T09:00:00Z");
        update.ExpressionAttributeValues[":ids"].SS.Should().Equal(OrderId);
        update.ExpressionAttributeValues[":orderId"].S.Should().Be(OrderId);
        update.ExpressionAttributeValues[":open"].S.Should().Be("open");
        update.ExpressionAttributeValues[":matched"].S.Should().Be("matched");
        // Building the item touches nothing.
        _queries.Should().BeEmpty();
        _updates.Should().BeEmpty();
    }

    // --- GetRawItemAsync ---

    [Fact]
    public async Task GetRawItem_ReadsTheRowStronglyConsistent_WithItsSets()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [Row(bookedIds: [OrderId], acceptedIds: [OrderId])] });

        var row = await _repo.GetRawItemAsync(TripId);

        row.Should().NotBeNull();
        row!["createdAt"].S.Should().Be(CreatedAt);
        row["bookedOrderIds"].SS.Should().Equal(OrderId);
        row["acceptedOrderIds"].SS.Should().Equal(OrderId);
        _queries.Should().ContainSingle();
        _queries[0].TableName.Should().Be(Table);
        _queries[0].KeyConditionExpression.Should().Be("tripId = :id");
        _queries[0].ExpressionAttributeValues[":id"].S.Should().Be(TripId);
        _queries[0].Limit.Should().Be(1);
        _queries[0].ConsistentRead.Should().BeTrue();
    }

    [Fact]
    public async Task GetRawItem_MissingTrip_ReturnsNull()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        (await _repo.GetRawItemAsync(TripId)).Should().BeNull();
    }

    // --- helpers ---

    private void RefuseTheWrite()
        => _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("condition failed"));

    private static Dictionary<string, AttributeValue> Row(
        string status = "open",
        string? remaining = null,
        string? booked = null,
        string[]? bookedIds = null,
        string[]? acceptedIds = null,
        bool deleted = false)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "tripId", S(TripId) },
            { "createdAt", S(CreatedAt) },
            { "truckerId", S("trucker-1") },
            { "status", S(status) },
            { "capacity", N("150") },
            { "pricePerQuintal", N("120") },
            { "version", N("2") }
        };
        if (remaining != null)
        {
            row["remainingQuintals"] = N(remaining);
        }

        if (booked != null)
        {
            row["bookedQuintals"] = N(booked);
        }

        if (bookedIds != null)
        {
            row["bookedOrderIds"] = new AttributeValue { SS = [.. bookedIds] };
        }

        if (acceptedIds != null)
        {
            row["acceptedOrderIds"] = new AttributeValue { SS = [.. acceptedIds] };
        }

        if (deleted)
        {
            row["deletedAt"] = S("2026-10-25T12:00:00Z");
        }

        return row;
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };
}
