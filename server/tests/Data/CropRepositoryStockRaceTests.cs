using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-18: the funding decrement never takes more than the listing holds. The write requires
/// <c>quantity &gt;= q</c> next to the version; a funding that finds too little stock takes nothing,
/// is recorded in <c>stockRefusedFor</c> (decided against the version it read) and reports
/// <see cref="StockDecrementResult.InsufficientStock"/>; a later restore for that order gives
/// nothing back. The real conditional writes run in <c>ListingStockRaceDynamoDbTests</c>.
/// </summary>
public class CropRepositoryStockRaceTests
{
    private const string TableName = "test-crops";
    private const string CropId = "crop-001";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly CropRepository _repository;
    private readonly List<UpdateItemRequest> _updates = new();

    public CropRepositoryStockRaceTests()
    {
        _repository = new CropRepository(_dynamoDb, TableName);
    }

    private static Dictionary<string, AttributeValue> Listing(
        string quantity, int version, bool numericQuantity = true,
        string[]? decrementedFor = null, string[]? refusedFor = null, string[]? restoredFor = null, string status = "available")
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = CropId } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } },
            { "status", new AttributeValue { S = status } },
            { "quantity", numericQuantity ? new AttributeValue { N = quantity } : new AttributeValue { S = quantity } }
        };
        if (decrementedFor != null)
        {
            item["stockDecrementedFor"] = new AttributeValue { SS = decrementedFor.ToList() };
        }

        if (refusedFor != null)
        {
            item["stockRefusedFor"] = new AttributeValue { SS = refusedFor.ToList() };
        }

        if (restoredFor != null)
        {
            item["stockRestoredFor"] = new AttributeValue { SS = restoredFor.ToList() };
        }

        return item;
    }

    private void Reads(params Dictionary<string, AttributeValue>[] items)
        => _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [items[0]] },
                items.Skip(1).Select(i => new QueryResponse { Items = [i] }).ToArray());

    private void WritesSucceed()
        => _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

    // --- the write requires the stock ---

    [Fact]
    public async Task Decrement_RequiresTheStockInTheWriteItself()
    {
        Reads(Listing("60", version: 1));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.Decremented);
        var write = _updates.Should().ContainSingle().Subject;
        write.ConditionExpression.Should().Contain("#v = :expectedVersion").And.Contain("#q >= :need");
        write.ExpressionAttributeValues[":need"].N.Should().Be("5");
        write.ExpressionAttributeValues[":q"].N.Should().Be("55.00");
    }

    [Fact]
    public async Task Decrement_ExactlyTheStockLeft_Wins_AndReservesTheListing()
    {
        // Boundary: quantity == q is enough.
        Reads(Listing("5.00", version: 3));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.Decremented);
        var write = _updates.Should().ContainSingle().Subject;
        write.ExpressionAttributeValues[":q"].N.Should().Be("0.00");
        write.ExpressionAttributeValues[":reserved"].S.Should().Be("reserved");
    }

    [Theory]
    [InlineData("0.30", 30.0)] // 30 kg, a value binary floating point cannot hold exactly
    [InlineData("1.10", 110.0)]
    [InlineData("0.07", 7.0)]
    public async Task Decrement_TheLastKilograms_AreEnough_WithNoRoundingLoss(string stock, double kilograms)
    {
        Reads(Listing(stock, version: 2));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", kilograms / 100.0);

        result.Should().Be(StockDecrementResult.Decremented);
        _updates.Single().ExpressionAttributeValues[":q"].N.Should().Be("0.00");
    }

    [Fact]
    public async Task Decrement_OneKilogramShort_IsRefused()
    {
        Reads(Listing("0.29", version: 2));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 30.0 / 100.0);

        result.Should().Be(StockDecrementResult.InsufficientStock);
    }

    [Fact]
    public async Task Decrement_OldStringQuantity_ReliesOnTheVersionAlone()
    {
        // A number compared with a string attribute fails every time, so the stock clause is
        // only added for a numeric quantity; the version still fences the read.
        Reads(Listing("60.00", version: 2, numericQuantity: false));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.Decremented);
        var write = _updates.Single();
        write.ConditionExpression.Should().NotContain(":need");
        write.ExpressionAttributeValues.Should().NotContainKey(":need");
        write.ExpressionAttributeValues[":q"].N.Should().Be("55.00");
    }

    // --- not enough stock ---

    [Fact]
    public async Task Decrement_NotEnoughStock_TakesNothing_AndRecordsTheRefusal()
    {
        Reads(Listing("4", version: 7));
        WritesSucceed();

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.InsufficientStock);
        var write = _updates.Should().ContainSingle().Subject;
        write.UpdateExpression.Should().Be("ADD #refused :orderIdSet");
        write.ExpressionAttributeNames["#refused"].Should().Be("stockRefusedFor");
        write.ExpressionAttributeValues[":orderIdSet"].SS.Should().Equal("order-9");
        write.ConditionExpression.Should().Contain("#v = :expectedVersion")
            .And.Contain("NOT contains(#decremented, :orderId)");
        write.ExpressionAttributeValues[":expectedVersion"].N.Should().Be("7");
        // Nothing else moves: no quantity, no version bump, no status.
        write.ExpressionAttributeValues.Should().NotContainKey(":q").And.NotContainKey(":newVersion");
    }

    [Fact]
    public async Task Decrement_SoldOutListing_IsRefused()
    {
        Reads(Listing("0.00", version: 4, status: "reserved"));
        WritesSucceed();

        (await _repository.TryDecrementStockAsync(CropId, "order-9", 0.5)).Should().Be(StockDecrementResult.InsufficientStock);
    }

    [Fact]
    public async Task Decrement_ConcurrentFundingTookTheStock_TheLoserIsRefusedOnTheReRead()
    {
        // The CAS race of spec A2: 10 quintals, this order wants 8. Between the read and the write
        // another funding took 8 (version 1 -> 2); the write fails its condition, the re-read shows
        // 2 left, and this order is refused instead of driving the listing below zero.
        Reads(Listing("10", version: 1), Listing("2.00", version: 2, decrementedFor: ["order-winner"]));
        var calls = 0;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new ConditionalCheckFailedException("version moved")
                : new UpdateItemResponse());

        var result = await _repository.TryDecrementStockAsync(CropId, "order-loser", 8);

        result.Should().Be(StockDecrementResult.InsufficientStock);
        _updates.Should().HaveCount(2);
        _updates[0].ExpressionAttributeValues[":q"].N.Should().Be("2.00");
        _updates[1].UpdateExpression.Should().Be("ADD #refused :orderIdSet");
        _updates[1].ExpressionAttributeValues[":expectedVersion"].N.Should().Be("2");
    }

    [Fact]
    public async Task Decrement_RefusalRacedByARestore_DecidesAgain_AndTakesTheReturnedStock()
    {
        // The refusal is itself version-checked: a cancelled order gave stock back between the read
        // and the refusal, so the funding reads again and now fits.
        Reads(Listing("4", version: 5), Listing("9.00", version: 6, restoredFor: ["order-gone"]));
        var calls = 0;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new ConditionalCheckFailedException("version moved")
                : new UpdateItemResponse());

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.Decremented);
        _updates[0].UpdateExpression.Should().Be("ADD #refused :orderIdSet");
        _updates[1].ExpressionAttributeValues[":q"].N.Should().Be("4.00");
        _updates[1].ExpressionAttributeValues[":expectedVersion"].N.Should().Be("6");
    }

    [Fact]
    public async Task Decrement_AlreadyTakenForThatOrder_StaysIdempotent_EvenIfTheListingIsNowEmpty()
    {
        // A payment redelivery after this order took the last stock: AlreadyApplied, not refused.
        Reads(Listing("0.00", version: 4, decrementedFor: ["order-9"], status: "reserved"));

        var result = await _repository.TryDecrementStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockDecrementResult.AlreadyApplied);
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Decrement_NoUsableQuantity_FailsWithoutReading(double quintals)
    {
        (await _repository.TryDecrementStockAsync(CropId, "order-9", quintals)).Should().Be(StockDecrementResult.Failed);

        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    // --- a refused order gives nothing back ---

    [Fact]
    public async Task Restore_ForARefusedOrder_GivesNothingBack()
    {
        Reads(Listing("2.00", version: 3, decrementedFor: ["order-winner"], refusedFor: ["order-loser"]));

        var result = await _repository.RestoreStockAsync(CropId, "order-loser", 8);

        result.Should().Be(StockRestoreResult.AlreadyRestored);
        (await _repository.TryRestoreStockAsync(CropId, "order-loser", 8)).Should().BeFalse();
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_StaleReadMissedTheRefusal_TheWriteItselfRefuses_ThenNothingIsGivenBack()
    {
        // The restore's read is eventually consistent and the refusal bumps no version, so the
        // write carries the refusal check too: it fails, the re-read sees the refusal, nothing moves.
        Reads(Listing("2.00", version: 3, decrementedFor: ["order-winner"]),
            Listing("2.00", version: 3, decrementedFor: ["order-winner"], refusedFor: ["order-loser"]));
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("refused order"));

        var result = await _repository.RestoreStockAsync(CropId, "order-loser", 8);

        result.Should().Be(StockRestoreResult.AlreadyRestored);
        var write = _updates.Should().ContainSingle().Subject;
        write.ConditionExpression.Should()
            .Contain("(attribute_not_exists(#refused) OR NOT contains(#refused, :orderId) OR contains(#decremented, :orderId))");
        write.ExpressionAttributeNames["#refused"].Should().Be("stockRefusedFor");
        write.ExpressionAttributeNames["#decremented"].Should().Be("stockDecrementedFor");
    }

    [Fact]
    public async Task Restore_ForAnOrderRefusedOnceButFundedLater_GivesItsStockBack()
    {
        // A repair that found stock after the first refusal: the order did take it.
        Reads(Listing("2.00", version: 3, decrementedFor: ["order-9"], refusedFor: ["order-9"]));
        WritesSucceed();

        var result = await _repository.RestoreStockAsync(CropId, "order-9", 5);

        result.Should().Be(StockRestoreResult.Restored);
        _updates.Single().ExpressionAttributeValues[":q"].N.Should().Be("7.00");
    }

    [Fact]
    public async Task Restore_ForTheWinner_IsUnchanged()
    {
        Reads(Listing("2.00", version: 3, decrementedFor: ["order-winner"], refusedFor: ["order-loser"]));
        WritesSucceed();

        var result = await _repository.RestoreStockAsync(CropId, "order-winner", 8);

        result.Should().Be(StockRestoreResult.Restored);
        _updates.Single().ExpressionAttributeValues[":q"].N.Should().Be("10.00");
    }
}
