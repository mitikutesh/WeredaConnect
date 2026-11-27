using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-152 against real DynamoDB (LocalStack in CI): <see cref="CropRepository.UpdateAsync"/> never
/// writes onto a deleted listing, deleted by the stamp or by the status alone, even at the stored
/// version. The refusal comes from the write's own condition, so nothing moves (version, price,
/// <c>updatedAt</c>), and the condition's returned row tells it apart from a version conflict. A
/// live listing's update, an update to <c>deleted</c> included, is written as before.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CropUpdateOnDeletedDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T08:00:00.0000000Z";
    private const string UpdatedAt = "2026-11-02T08:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _listingId = $"crop-fup152-{Guid.NewGuid():N}";

    public CropUpdateOnDeletedDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;
    private CropRepository Crops => new(Ddb, _db.Crops);

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private Task SeedAsync(string status, int version, bool stamped) => Ddb.PutItemAsync(new PutItemRequest
    {
        TableName = _db.Crops,
        Item = Seed(status, version, stamped)
    });

    private Dictionary<string, AttributeValue> Seed(string status, int version, bool stamped)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S(_listingId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(UpdatedAt),
            ["farmerId"] = S("farmer-fup152"),
            ["cropType"] = S("teff"),
            ["status"] = S(status),
            ["quantity"] = N("6"),
            ["pricePerKg"] = N("45.5"),
            ["version"] = N(version.ToString())
        };
        if (stamped)
        {
            item["deletedAt"] = S(UpdatedAt);
        }

        return item;
    }

    private async Task<Dictionary<string, AttributeValue>> ListingAsync()
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Crops,
            Key = new Dictionary<string, AttributeValue> { ["cropId"] = S(_listingId), ["createdAt"] = S(CreatedAt) },
            ConsistentRead = true
        })).Item;

    private static Dictionary<string, object> PriceEdit() => new()
    {
        { "pricePerKg", 60.0 }, { "quantity", 2.0 }, { "description", "Fresh teff" }
    };

    [LocalDynamoDbTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateAtTheStoredVersion_OfADeletedListing_IsRefused_NothingMoves(bool stamped)
    {
        await SeedAsync("deleted", 4, stamped);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(Seed("deleted", 4, stamped));
    }

    [LocalDynamoDbFact]
    public async Task ListingDeletedWithItsStampButAnotherStatus_IsRefusedToo()
    {
        // Account deletion stamps a sold listing and keeps its status (step 7).
        await SeedAsync("sold", 3, stamped: true);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 3);

        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(Seed("sold", 3, stamped: true));
    }

    [LocalDynamoDbFact]
    public async Task UpdateAfterADelete_AtTheVersionTheDeleteLeft_IsRefused()
    {
        await SeedAsync("available", 4, stamped: false);
        (await Crops.DeleteAsync(_listingId, 4)).IsSuccess.Should().BeTrue();
        var deleted = await ListingAsync();
        deleted["version"].N.Should().Be("5");

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 5);

        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(deleted);
    }

    [LocalDynamoDbFact]
    public async Task UpdateOfALiveListing_IsWrittenAsBefore()
    {
        await SeedAsync("available", 4, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.IsSuccess.Should().BeTrue();
        var listing = await ListingAsync();
        listing["version"].N.Should().Be("5");
        listing["pricePerKg"].N.Should().Be("60");
        listing.Should().NotContainKey("deletedAt");
    }

    [LocalDynamoDbFact]
    public async Task UpdateToDeleted_OfALiveListing_StillDeletesIt()
    {
        // The status is named twice (the field's alias and the condition's): DynamoDB takes both.
        await SeedAsync("reserved", 4, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, new Dictionary<string, object> { { "status", "deleted" } }, 4);

        result.IsSuccess.Should().BeTrue();
        var listing = await ListingAsync();
        listing["status"].S.Should().Be("deleted");
        listing["version"].N.Should().Be("5");
        listing.Should().ContainKey("deletedAt");
    }

    [LocalDynamoDbFact]
    public async Task LiveListingAtAnotherVersion_IsAVersionConflict_NotAlreadyDeleted()
    {
        await SeedAsync("available", 6, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.Error!.Code.Should().Be("SYNC_CONFLICT");
        (await ListingAsync())["version"].N.Should().Be("6");
    }
}
