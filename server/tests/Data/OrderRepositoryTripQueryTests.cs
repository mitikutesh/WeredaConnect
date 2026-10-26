using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-15: the order repository's side of the three-party commitments. A trip's orders are read
/// through <c>TripIdIndex</c> (every page), a status move can carry the commitment stamps in the
/// same write, and a reminder marker is stamped without a version bump so it never makes a client
/// conflict. These tests assert the request shapes; no DynamoDB integration test runs them yet.
/// </summary>
public class OrderRepositoryTripQueryTests
{
    private const string Table = "orders-test";
    private const string OrderId = "order-1";
    private const string CreatedAt = "2026-10-20T08:00:00Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly OrderRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly List<QueryRequest> _queries = new();

    public OrderRepositoryTripQueryTests()
    {
        _repo = new OrderRepository(_dynamoDb, Table);
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [OrderRow(OrderId, "funded")] });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = OrderRow(OrderId, "accepted", version: "4") });
    }

    // --- QueryByTripAsync ---

    [Fact]
    public async Task QueryByTrip_FollowsEveryPageOfTheTripIndex()
    {
        var startKeys = new List<string?>();
        var lastKey = new Dictionary<string, AttributeValue>
        {
            { "orderId", S("order-1") }, { "createdAt", S(CreatedAt) }, { "tripId", S("trip-1") }
        };
        _dynamoDb.QueryAsync(
                Arg.Do<QueryRequest>(r => startKeys.Add(r.ExclusiveStartKey is { Count: > 0 } k ? k["orderId"].S : null)),
                Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [OrderRow("order-1", "funded"), OrderRow("order-2", "accepted")], LastEvaluatedKey = lastKey },
                new QueryResponse { Items = [OrderRow("order-3", "cancelled")], LastEvaluatedKey = new Dictionary<string, AttributeValue>() });

        var orders = await _repo.QueryByTripAsync("trip-1");

        orders.Select(o => o["orderId"]).Should().Equal("order-1", "order-2", "order-3");
        orders[2]["status"].Should().Be("cancelled");
        startKeys.Should().Equal(new string?[] { null, "order-1" });
        _queries.Should().HaveCount(2);
        var query = _queries[0];
        query.TableName.Should().Be(Table);
        query.IndexName.Should().Be("TripIdIndex");
        query.KeyConditionExpression.Should().Be("#tripId = :tripId");
        query.ExpressionAttributeNames["#tripId"].Should().Be("tripId");
        query.ExpressionAttributeValues[":tripId"].S.Should().Be("trip-1");
        query.ScanIndexForward.Should().BeTrue();
        query.FilterExpression.Should().BeNull();
    }

    [Fact]
    public async Task QueryByTrip_NoOrders_ReturnsEmpty()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        (await _repo.QueryByTripAsync("trip-1")).Should().BeEmpty();
        _queries.Should().ContainSingle();
    }

    // --- TryAdvanceStatusAsync ---

    [Fact]
    public async Task AdvanceStatus_WithoutExtras_IsTheStatusWriteAsBefore()
    {
        var updated = await _repo.TryAdvanceStatusAsync(OrderId, "funded", "accepted");

        updated.Should().NotBeNull();
        updated!["status"].Should().Be("accepted");
        _queries[0].ConsistentRead.Should().BeTrue();
        var update = _updates.Should().ContainSingle().Subject;
        update.TableName.Should().Be(Table);
        update.Key["orderId"].S.Should().Be(OrderId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be("SET #status = :to, #updated = :updated ADD #v :one");
        update.ConditionExpression.Should().Be("attribute_exists(orderId) AND #status = :from");
        update.ExpressionAttributeValues[":from"].S.Should().Be("funded");
        update.ExpressionAttributeValues[":to"].S.Should().Be("accepted");
        update.ExpressionAttributeValues[":one"].N.Should().Be("1");
        update.ExpressionAttributeValues.Should().NotContainKey(":expectedVersion");
        update.ReturnValues.Should().Be(ReturnValue.ALL_NEW);
    }

    [Fact]
    public async Task AdvanceStatus_WithExtras_SetsThemInTheSameWrite_SkippingProtectedKeys()
    {
        var extras = new Dictionary<string, object>
        {
            { "truckerResponse", "accepted" },
            { "truckerRespondedAt", "2026-10-26T09:00:00Z" },
            { "attempts", 2 },
            // Protected: the write owns these, a caller's copy is dropped.
            { "status", "settled" },
            { "version", 99 },
            { "updatedAt", "1999-01-01T00:00:00Z" },
            { "orderId", "other" },
            { "createdAt", "other" }
        };

        var updated = await _repo.TryAdvanceStatusAsync(OrderId, "funded", "accepted", extras);

        updated.Should().NotBeNull();
        var update = _updates.Should().ContainSingle().Subject;
        update.UpdateExpression.Should().Be(
            "SET #status = :to, #updated = :updated, #f0 = :f0, #f1 = :f1, #f2 = :f2 ADD #v :one");
        update.ConditionExpression.Should().Be("attribute_exists(orderId) AND #status = :from");
        update.ExpressionAttributeNames["#f0"].Should().Be("truckerResponse");
        update.ExpressionAttributeNames["#f1"].Should().Be("truckerRespondedAt");
        update.ExpressionAttributeNames["#f2"].Should().Be("attempts");
        update.ExpressionAttributeNames.Should().NotContainKey("#f3");
        update.ExpressionAttributeNames.Values.Should().NotContain("orderId").And.NotContain("createdAt");
        update.ExpressionAttributeValues[":f0"].S.Should().Be("accepted");
        update.ExpressionAttributeValues[":f1"].S.Should().Be("2026-10-26T09:00:00Z");
        update.ExpressionAttributeValues[":f2"].N.Should().Be("2");
        update.ExpressionAttributeValues[":to"].S.Should().Be("accepted");
        update.ExpressionAttributeValues[":updated"].S.Should().NotBe("1999-01-01T00:00:00Z");
    }

    [Fact]
    public async Task AdvanceStatus_WithEmptyExtras_IsTheStatusWriteAsBefore()
    {
        await _repo.TryAdvanceStatusAsync(OrderId, "funded", "accepted", new Dictionary<string, object>());

        _updates[0].UpdateExpression.Should().Be("SET #status = :to, #updated = :updated ADD #v :one");
    }

    [Fact]
    public async Task AdvanceStatus_Refused_ReturnsNull()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved on"));

        var updated = await _repo.TryAdvanceStatusAsync(OrderId, "funded", "accepted",
            new Dictionary<string, object> { { "truckerResponse", "accepted" } });

        updated.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceStatus_MissingOrder_ReturnsNullWithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        (await _repo.TryAdvanceStatusAsync(OrderId, "funded", "accepted")).Should().BeNull();

        _updates.Should().BeEmpty();
    }

    // --- TryStampAsync ---

    [Fact]
    public async Task Stamp_SetsTheFieldsWithoutVersionOrUpdatedAt_GuardedOnTheExpectations()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var ok = await _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object> { { "farmerReminderAt", "2026-10-26T09:00:00Z" } },
            new Dictionary<string, string> { { "status", "funded" }, { "farmerResponse", "pending" } });

        ok.Should().BeTrue();
        _queries[0].ConsistentRead.Should().BeTrue();
        var update = _updates.Should().ContainSingle().Subject;
        update.TableName.Should().Be(Table);
        update.Key["orderId"].S.Should().Be(OrderId);
        update.Key["createdAt"].S.Should().Be(CreatedAt);
        update.UpdateExpression.Should().Be("SET #f0 = :f0");
        update.ConditionExpression.Should().Be("attribute_exists(orderId) AND #c0 = :c0 AND #c1 = :c1");
        update.ExpressionAttributeNames["#f0"].Should().Be("farmerReminderAt");
        update.ExpressionAttributeNames["#c0"].Should().Be("status");
        update.ExpressionAttributeNames["#c1"].Should().Be("farmerResponse");
        update.ExpressionAttributeValues[":f0"].S.Should().Be("2026-10-26T09:00:00Z");
        update.ExpressionAttributeValues[":c0"].S.Should().Be("funded");
        update.ExpressionAttributeValues[":c1"].S.Should().Be("pending");
        update.ExpressionAttributeNames.Values.Should().NotContain("version").And.NotContain("updatedAt");
        update.UpdateExpression.Should().NotContain("ADD");
        update.ReturnValues.Should().BeNull();
    }

    [Fact]
    public async Task Stamp_WithNoExpectations_OnlyRequiresTheOrderToExist()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var ok = await _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object> { { "pickupOverdueNotifiedAt", "2026-10-26T09:00:00Z" }, { "reminders", 1 } },
            new Dictionary<string, string>());

        ok.Should().BeTrue();
        _updates[0].UpdateExpression.Should().Be("SET #f0 = :f0, #f1 = :f1");
        _updates[0].ConditionExpression.Should().Be("attribute_exists(orderId)");
        _updates[0].ExpressionAttributeValues[":f1"].N.Should().Be("1");
    }

    [Fact]
    public async Task Stamp_SkipsProtectedFields_SoAMarkerNeverMovesTheStatusOrVersion()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        await _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object>
            {
                { "status", "settled" }, { "version", 9 }, { "updatedAt", "x" }, { "orderId", "x" }, { "createdAt", "x" },
                { "truckerReminderAt", "2026-10-26T09:00:00Z" }
            },
            new Dictionary<string, string>());

        _updates[0].UpdateExpression.Should().Be("SET #f0 = :f0");
        _updates[0].ExpressionAttributeNames.Should().HaveCount(1);
        _updates[0].ExpressionAttributeNames["#f0"].Should().Be("truckerReminderAt");
    }

    [Fact]
    public async Task Stamp_WithOnlyProtectedFields_IsAProgrammingError_NothingRead()
    {
        var act = () => _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object> { { "status", "settled" } },
            new Dictionary<string, string>());

        await act.Should().ThrowAsync<ArgumentException>();
        _queries.Should().BeEmpty();
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Stamp_Refused_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("already stamped"));

        var ok = await _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object> { { "farmerReminderAt", "2026-10-26T09:00:00Z" } },
            new Dictionary<string, string> { { "farmerReminderAt", "" } });

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Stamp_MissingOrder_ReturnsFalseWithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var ok = await _repo.TryStampAsync(
            OrderId,
            new Dictionary<string, object> { { "farmerReminderAt", "2026-10-26T09:00:00Z" } },
            new Dictionary<string, string>());

        ok.Should().BeFalse();
        _updates.Should().BeEmpty();
    }

    // --- helpers ---

    private static Dictionary<string, AttributeValue> OrderRow(string orderId, string status, string version = "3") => new()
    {
        { "orderId", S(orderId) },
        { "createdAt", S(CreatedAt) },
        { "tripId", S("trip-1") },
        { "status", S(status) },
        { "version", new AttributeValue { N = version } }
    };

    private static AttributeValue S(string value) => new() { S = value };
}
