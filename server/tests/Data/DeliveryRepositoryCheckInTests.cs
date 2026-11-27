using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// LT-07: the two delivery writes a check-in can make. The system move to <c>inTransit</c> is the
/// trucker's own write, conditional on the status and the version read. The <c>nearbyNotifiedAt</c>
/// stamp is guarded by the status and the missing stamp only, and leaves the version and
/// <c>updatedAt</c> alone, so a party's queued update never conflicts over it.
/// </summary>
public class DeliveryRepositoryCheckInTests
{
    private const string Table = "deliveries-test";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly DeliveryRepository _repo;
    private UpdateItemRequest? _update;

    public DeliveryRepositoryCheckInTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _repo = new DeliveryRepository(_dynamoDb, Table);
    }

    [Fact]
    public async Task MarkInTransit_IsConditionalOnPickedUpAndTheVersionRead_AndAddsOne()
    {
        var ok = await _repo.TryMarkInTransitAsync("delivery-1", "trip-1", 4);

        ok.Should().BeTrue();
        _update!.TableName.Should().Be(Table);
        _update.Key["deliveryId"].S.Should().Be("delivery-1");
        _update.Key["tripId"].S.Should().Be("trip-1");
        _update.UpdateExpression.Should().Be("SET #status = :inTransit, #updated = :now, #v = :next");
        _update.ConditionExpression.Should().Be("#status = :pickedUp AND #v = :expected");
        _update.ExpressionAttributeValues[":inTransit"].S.Should().Be("inTransit");
        _update.ExpressionAttributeValues[":pickedUp"].S.Should().Be("pickedUp");
        _update.ExpressionAttributeValues[":expected"].N.Should().Be("4");
        _update.ExpressionAttributeValues[":next"].N.Should().Be("5");
        DateTime.Parse(_update.ExpressionAttributeValues[":now"].S, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
            .ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        // No read first: the caller already read the row and passes its key.
        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkInTransit_AConditionThatFails_IsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved on"));

        (await _repo.TryMarkInTransitAsync("delivery-1", "trip-1", 4)).Should().BeFalse();
    }

    [Fact]
    public async Task StampNearby_IsWrittenOnce_OnTheRoad_WithoutTouchingTheVersion()
    {
        var ok = await _repo.TryStampNearbyAsync("delivery-1", "trip-1", "2026-10-27T09:00:00.000Z", 3.2, 25);

        ok.Should().BeTrue();
        _update!.Key["deliveryId"].S.Should().Be("delivery-1");
        _update.Key["tripId"].S.Should().Be("trip-1");
        _update.ConditionExpression.Should().Be("#status IN (:pickedUp, :inTransit) AND attribute_not_exists(#at)");
        _update.UpdateExpression.Should().Be("SET #at = :at, #km = :km, #min = :min");
        _update.ExpressionAttributeNames["#at"].Should().Be("nearbyNotifiedAt");
        _update.ExpressionAttributeNames["#km"].Should().Be("nearbyDistanceKm");
        _update.ExpressionAttributeNames["#min"].Should().Be("nearbyMinutes");
        _update.ExpressionAttributeValues[":at"].S.Should().Be("2026-10-27T09:00:00.000Z");
        _update.ExpressionAttributeValues[":km"].N.Should().Be("3.2");
        _update.ExpressionAttributeValues[":min"].N.Should().Be("25");
        _update.ExpressionAttributeNames.Values.Should().NotContain("version").And.NotContain("updatedAt");
    }

    [Fact]
    public async Task StampNearby_WithoutMinutes_WritesNone()
    {
        await _repo.TryStampNearbyAsync("delivery-1", "trip-1", "2026-10-27T09:00:00.000Z", 5, null);

        _update!.UpdateExpression.Should().Be("SET #at = :at, #km = :km");
        _update.ExpressionAttributeValues[":km"].N.Should().Be("5.0");
        _update.ExpressionAttributeNames.Should().NotContainKey("#min");
    }

    [Fact]
    public async Task StampNearby_AlreadyStampedOrOffTheRoad_IsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("stamped"));

        (await _repo.TryStampNearbyAsync("delivery-1", "trip-1", "2026-10-27T09:00:00.000Z", 3.2, 25)).Should().BeFalse();
    }
}
