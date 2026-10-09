using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-03: the dispute verdict is stamped on a disputed delivery. The guard is the status and
/// the missing verdict, not the version, so the version bump of the disputed write just
/// before can never fail the stamp. The real expression runs in the LocalStack tests.
/// </summary>
public class DeliveryRepositoryDisputeTests
{
    private const string Table = "deliveries-test";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly DeliveryRepository _repo;
    private UpdateItemRequest? _update;
    private QueryRequest? _query;

    public DeliveryRepositoryDisputeTests()
    {
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => _query = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items =
                [
                    new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = "delivery-1" } },
                        { "tripId", new AttributeValue { S = "trip-1" } },
                        { "status", new AttributeValue { S = "disputed" } },
                        { "version", new AttributeValue { N = "4" } }
                    }
                ]
            });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _repo = new DeliveryRepository(_dynamoDb, Table);
    }

    [Fact]
    public async Task RecordResolution_StampsTheVerdict_GuardedOnStatusAndNoVerdict()
    {
        var ok = await _repo.TryRecordDisputeResolutionAsync("delivery-1", "buyer_wins", "2026-10-09T10:00:00Z");

        ok.Should().BeTrue();
        _query!.ConsistentRead.Should().BeTrue();
        _update!.TableName.Should().Be(Table);
        _update.Key["deliveryId"].S.Should().Be("delivery-1");
        _update.Key["tripId"].S.Should().Be("trip-1");
        _update.ConditionExpression.Should().Be("#status = :disputed AND attribute_not_exists(#resolution)");
        _update.ExpressionAttributeNames["#resolution"].Should().Be("disputeResolution");
        _update.ExpressionAttributeNames["#resolvedAt"].Should().Be("disputeResolvedAt");
        _update.ExpressionAttributeValues[":resolution"].S.Should().Be("buyer_wins");
        _update.ExpressionAttributeValues[":resolvedAt"].S.Should().Be("2026-10-09T10:00:00Z");
        _update.UpdateExpression.Should().EndWith("ADD #v :one");
        _update.ExpressionAttributeValues.Should().NotContainKey(":expectedVersion");
    }

    [Fact]
    public async Task RecordResolution_ConditionFails_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("stamped"));

        var ok = await _repo.TryRecordDisputeResolutionAsync("delivery-1", "buyer_wins", "2026-10-09T10:00:00Z");

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task RecordResolution_MissingDelivery_ReturnsFalseWithoutAWrite()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var ok = await _repo.TryRecordDisputeResolutionAsync("delivery-1", "buyer_wins", "2026-10-09T10:00:00Z");

        ok.Should().BeFalse();
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelPending_IsTheSoftDelete_GuardedOnPending()
    {
        // A buyer_wins refund before pickup cancels the delivery that never left the farm.
        var ok = await _repo.TryCancelPendingAsync("delivery-1");

        ok.Should().BeTrue();
        _query!.ConsistentRead.Should().BeTrue();
        _update!.Key["tripId"].S.Should().Be("trip-1");
        _update.ConditionExpression.Should().Be("#status = :pending");
        _update.ExpressionAttributeValues[":cancelled"].S.Should().Be("cancelled");
        _update.ExpressionAttributeNames["#deleted"].Should().Be("deletedAt");
        _update.UpdateExpression.Should().EndWith("ADD #v :one");
    }

    [Fact]
    public async Task CancelPending_NotPendingAnyMore_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("picked up"));

        (await _repo.TryCancelPendingAsync("delivery-1")).Should().BeFalse();
    }

    [Fact]
    public async Task GetById_Consistent_SetsConsistentRead()
    {
        await _repo.GetByIdAsync("delivery-1", consistentRead: true);

        _query!.ConsistentRead.Should().BeTrue();
    }
}
