using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-494 and FUP-495 against real DynamoDB (LocalStack in CI), next to FUP-152
/// (<see cref="CropUpdateOnDeletedDynamoDbTests"/>):
/// <list type="bullet">
/// <item><see cref="CropRepository.DeleteAsync"/> never writes onto a listing already deleted, by the
/// stamp or by the status, even at the stored version: the first delete's <c>deletedAt</c> and version
/// stay. A sold listing is still deleted.</item>
/// <item><see cref="CropRepository.UpdateAsync"/> never writes onto a sold listing, even at the stored
/// version. Marking a live listing sold is written as before, and the order flow's stock writes,
/// which do not go through this update, still reach a sold listing.</item>
/// </list>
/// The refusals come from the writes' own conditions, and the row each condition returns names the
/// reason (<see cref="CropRepository.AlreadyDeletedCode"/>, <see cref="CropRepository.SoldCode"/>).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CropTerminalWritesDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T08:00:00.0000000Z";
    private const string UpdatedAt = "2026-11-02T08:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _listingId = $"crop-fup494-{Guid.NewGuid():N}";

    public CropTerminalWritesDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

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
            ["farmerId"] = S("farmer-fup494"),
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

    // ===== FUP-494: a delete of a deleted listing =====

    [LocalDynamoDbTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteAtTheStoredVersion_OfADeletedListing_IsRefused_NothingMoves(bool stamped)
    {
        await SeedAsync("deleted", 4, stamped);

        var result = await Crops.DeleteAsync(_listingId, 4);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(Seed("deleted", 4, stamped));
    }

    [LocalDynamoDbFact]
    public async Task SecondDelete_AtTheVersionTheFirstLeft_IsRefused_TheFirstStampStays()
    {
        await SeedAsync("available", 4, stamped: false);
        (await Crops.DeleteAsync(_listingId, 4)).IsSuccess.Should().BeTrue();
        var deleted = await ListingAsync();
        deleted["version"].N.Should().Be("5");
        deleted.Should().ContainKey("deletedAt");

        var result = await Crops.DeleteAsync(_listingId, 5);

        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(deleted);
    }

    [LocalDynamoDbFact]
    public async Task DeleteOfAListingStampedWithAnotherStatus_IsRefusedToo()
    {
        // Account deletion stamps a sold listing and keeps its status.
        await SeedAsync("sold", 3, stamped: true);

        var result = await Crops.DeleteAsync(_listingId, 3);

        result.Error!.Code.Should().Be(CropRepository.AlreadyDeletedCode);
        (await ListingAsync()).Should().BeEquivalentTo(Seed("sold", 3, stamped: true));
    }

    [LocalDynamoDbFact]
    public async Task DeleteOfALiveListingAtAnotherVersion_IsAVersionConflict()
    {
        await SeedAsync("available", 6, stamped: false);

        var result = await Crops.DeleteAsync(_listingId, 4);

        result.Error!.Code.Should().Be("SYNC_CONFLICT");
        (await ListingAsync()).Should().BeEquivalentTo(Seed("available", 6, stamped: false));
    }

    [LocalDynamoDbFact]
    public async Task DeleteOfASoldListing_IsWritten()
    {
        await SeedAsync("sold", 4, stamped: false);

        var result = await Crops.DeleteAsync(_listingId, 4);

        result.IsSuccess.Should().BeTrue();
        var listing = await ListingAsync();
        listing["status"].S.Should().Be("deleted");
        listing["version"].N.Should().Be("5");
        listing.Should().ContainKey("deletedAt");
    }

    // ===== FUP-495: an update of a sold listing =====

    [LocalDynamoDbFact]
    public async Task UpdateAtTheStoredVersion_OfASoldListing_IsRefused_NothingMoves()
    {
        await SeedAsync("sold", 4, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(CropRepository.SoldCode);
        (await ListingAsync()).Should().BeEquivalentTo(Seed("sold", 4, stamped: false));
    }

    [LocalDynamoDbFact]
    public async Task MarkingALiveListingSold_IsWritten_AndAnEditAfterIt_IsRefused()
    {
        await SeedAsync("available", 4, stamped: false);

        var marked = await Crops.UpdateAsync(_listingId, new Dictionary<string, object> { { "status", "sold" } }, 4);

        marked.IsSuccess.Should().BeTrue();
        var sold = await ListingAsync();
        sold["status"].S.Should().Be("sold");
        sold["version"].N.Should().Be("5");

        var edit = await Crops.UpdateAsync(_listingId, PriceEdit(), 5);

        edit.Error!.Code.Should().Be(CropRepository.SoldCode);
        (await ListingAsync()).Should().BeEquivalentTo(sold);
    }

    [LocalDynamoDbFact]
    public async Task EditOfAReservedListing_IsWrittenAsBefore()
    {
        await SeedAsync("reserved", 4, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.IsSuccess.Should().BeTrue();
        var listing = await ListingAsync();
        listing["version"].N.Should().Be("5");
        listing["pricePerKg"].N.Should().Be("60");
        listing["status"].S.Should().Be("reserved");
    }

    [LocalDynamoDbFact]
    public async Task SoldListingAtAnotherVersion_IsAnsweredSold()
    {
        // The row the condition returns decides: sold, whatever version the client sent.
        await SeedAsync("sold", 6, stamped: false);

        var result = await Crops.UpdateAsync(_listingId, PriceEdit(), 4);

        result.Error!.Code.Should().Be(CropRepository.SoldCode);
        (await ListingAsync())["version"].N.Should().Be("6");
    }

    [LocalDynamoDbFact]
    public async Task StockRestoreOnASoldListing_IsNotBlocked_AndKeepsItSold()
    {
        // The order flow's restore (a cancelled or refunded order) has its own write: FUP-495 guards
        // the /sync update only.
        await SeedAsync("sold", 4, stamped: false);

        var restored = await Crops.RestoreStockAsync(_listingId, "order-fup495", 1.5);

        restored.Should().Be(StockRestoreResult.Restored);
        var listing = await ListingAsync();
        decimal.Parse(listing["quantity"].N, System.Globalization.CultureInfo.InvariantCulture).Should().Be(7.5m);
        listing["status"].S.Should().Be("sold");
        listing["version"].N.Should().Be("5");
    }
}
