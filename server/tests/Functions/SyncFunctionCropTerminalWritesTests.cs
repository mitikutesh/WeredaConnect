using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-494 and FUP-495 through <c>/sync</c>, next to FUP-152 (<see cref="SyncFunctionCropUpdateOnDeletedTests"/>):
/// <list type="bullet">
/// <item>A <c>crop_listing</c> <c>delete</c> of a listing already deleted writes nothing and answers its
/// farmer <c>success</c> with the stored version, at any version. Before, one sent at the stored version
/// was written again (<c>deletedAt</c> and the version moved).</item>
/// <item>A <c>crop_listing</c> <c>update</c> of a sold listing writes nothing and answers its farmer
/// <c>conflict</c> with the stored row, message <c>CROP_SOLD: The listing is sold.</c>, with or without a
/// status and at any version. Before, one without a status wrote price, quantity or text onto it.</item>
/// </list>
/// Anyone else still gets <c>CROP_UNAUTHORIZED</c> and no row. A sold listing can still be deleted.
/// </summary>
public class SyncFunctionCropTerminalWritesTests
{
    private const string FarmerId = "farmer-001";
    private const string ListingId = "listing-494";
    private const string CurrentAppBuild = "3";
    private const string SoldMessage = "CROP_SOLD: The listing is sold.";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionCropTerminalWritesTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _orders.QueryByListingAsync(ListingId).Returns(new List<Dictionary<string, object>>());
    }

    private static Dictionary<string, object> Listing(string status, int version, bool stamped = false)
    {
        var row = new Dictionary<string, object>
        {
            { "cropId", ListingId }, { "farmerId", FarmerId }, { "cropType", "teff" },
            { "quantity", 6.0 }, { "pricePerKg", 45.5 }, { "status", status },
            { "createdAt", "2026-11-01T08:00:00.0000000Z" }, { "updatedAt", "2026-11-02T08:00:00.0000000Z" },
            { "version", version.ToString() }
        };
        if (stamped)
        {
            row["deletedAt"] = "2026-11-02T08:00:00.0000000Z";
        }

        return row;
    }

    // ===== FUP-494: a delete of a deleted listing =====

    [Theory]
    [InlineData(true, 4)] // deleted by a delete, sent again at the version that delete left
    [InlineData(true, 3)] // behind
    [InlineData(true, 9)] // ahead
    [InlineData(false, 4)] // deleted by an update before FUP-132 wrote the stamp
    public async Task DeleteOfADeletedListing_IsSuccessWithTheStoredVersion_NothingWritten(bool stamped, int sentVersion)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped));

        var result = await DeleteAsync(sentVersion);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        (result.TryGetProperty("serverData", out var data) && data.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("a delete's success carries no row");
        await _crops.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<int>());
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
        await _syncMeta.Received(1).StoreIdempotencyKeyAsync(
            Arg.Any<string>(), "op-crop", "crop_listing", "success", Arg.Any<string?>());
    }

    [Fact]
    public async Task DeleteWithoutAVersion_OfADeletedListing_IsTheSameSuccess()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped: true));

        var result = await SendAsync(FarmerId, "farmer", Op("delete", new Dictionary<string, object>()));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        await _crops.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("farmer-002", "farmer")]
    [InlineData("trucker-001", "trucker")]
    [InlineData("buyer-001", "buyer")]
    public async Task DeleteOfSomeoneElsesDeletedListing_IsStillUnauthorized(string userId, string role)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped: true));

        var result = await SendAsync(userId, role, Op("delete", new Dictionary<string, object> { { "version", 4 } }));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("CROP_UNAUTHORIZED");
        (result.TryGetProperty("serverData", out var data) && data.ValueKind != JsonValueKind.Null)
            .Should().BeFalse();
        await _crops.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task DeletedBetweenTheReadAndTheWrite_TheWriteRefuses_AndTheFarmerGetsSuccess()
    {
        // The read saw the live listing at version 4; the farmer's other device deleted it first.
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 4));
        _crops.DeleteAsync(ListingId, 4).Returns(
            AuthResult<Dictionary<string, object>>.Failure(
                409, CropRepository.AlreadyDeletedCode, "The listing is already deleted.", "status"));
        _crops.GetByIdConsistentAsync(ListingId).Returns(Listing("deleted", 5, stamped: true));

        var result = await DeleteAsync(4);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task DeleteOfALiveListing_IsWrittenAsBefore()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 4));
        _crops.DeleteAsync(ListingId, 4).Returns(
            AuthResult<Dictionary<string, object>>.Success(Listing("deleted", 5, stamped: true)));

        var result = await DeleteAsync(4);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        await _crops.Received(1).DeleteAsync(ListingId, 4);
    }

    [Fact]
    public async Task DeleteOfALiveListing_AtAnotherVersion_IsStillAConflictWithTheRow()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 6));
        _crops.DeleteAsync(ListingId, 4).Returns(
            AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict on delete.", "version"));

        var result = await DeleteAsync(4);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(6);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("available");
    }

    [Fact]
    public async Task DeleteOfASoldListing_IsStillWritten()
    {
        // FUP-495 guards the update only: a farmer may still take a sold listing down.
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4));
        _crops.DeleteAsync(ListingId, 4).Returns(
            AuthResult<Dictionary<string, object>>.Success(Listing("deleted", 5, stamped: true)));

        var result = await DeleteAsync(4);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        await _crops.Received(1).DeleteAsync(ListingId, 4);
    }

    // ===== FUP-495: an update of a sold listing =====

    [Theory]
    [InlineData(4)] // the stored version
    [InlineData(3)] // behind
    [InlineData(9)] // ahead
    public async Task UpdateWithoutAStatus_OfASoldListing_IsAConflictWithTheStoredRow_NothingWritten(int sentVersion)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4));

        var result = await UpdateAsync(new Dictionary<string, object>
        {
            { "pricePerKg", 60.0 }, { "quantity", 2.0 }, { "description", "Fresh teff" }, { "version", sentVersion }
        });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("sold");
        result.GetProperty("serverData").GetProperty("pricePerKg").GetDouble().Should().Be(45.5);
        result.GetProperty("serverData").GetProperty("quantity").GetDouble().Should().Be(6.0);
        result.GetProperty("message").GetString().Should().Be(SoldMessage);
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UpdateWithoutAVersion_OfASoldListing_IsTheSameConflict()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        result.GetProperty("message").GetString().Should().Be(SoldMessage);
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    [InlineData("sold")]
    [InlineData("deleted")]
    public async Task UpdateWithAStatus_OfASoldListing_IsTheSameConflict_NoOrderRead(string status)
    {
        // Before, the transition table answered CROP_INVALID_TRANSITION at the stored version.
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4));

        var result = await UpdateAsync(new Dictionary<string, object> { { "status", status }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("message").GetString().Should().Be(SoldMessage);
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task UpdateOfASoldListingThatIsAlsoStamped_IsAnsweredAsDeleted()
    {
        // Account deletion stamps a sold listing and keeps its status: deleted is checked first.
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4, stamped: true));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("message").GetString().Should().StartWith("CROP_ALREADY_DELETED:");
    }

    [Theory]
    [InlineData("farmer-002", "farmer")]
    [InlineData("trucker-001", "trucker")]
    [InlineData("buyer-001", "buyer")]
    public async Task UpdateOfSomeoneElsesSoldListing_IsStillUnauthorized_AndGetsNoRow(string userId, string role)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("sold", 4));

        var result = await SendAsync(userId, role, Op("update", new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } }));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("CROP_UNAUTHORIZED");
        (result.TryGetProperty("serverData", out var data) && data.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("only the listing's farmer gets the row");
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SoldBetweenTheReadAndTheWrite_TheWriteRefuses_AndTheFarmerGetsTheSoldRow()
    {
        // The read saw the live listing at version 4; the farmer's other device marked it sold first.
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 4));
        _crops.GetByIdConsistentAsync(ListingId).Returns(Listing("sold", 5));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Failure(409, CropRepository.SoldCode, "The listing is sold.", "status"));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("sold");
        result.GetProperty("message").GetString().Should().Be(SoldMessage);
        await _crops.Received(1).GetByIdConsistentAsync(ListingId);
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    public async Task MarkingALiveListingSold_IsWrittenAsBefore(string stored)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing(stored, 4));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Success(Listing("sold", 5)));

        var result = await UpdateAsync(new Dictionary<string, object> { { "status", "sold" }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        await _crops.Received(1).UpdateAsync(
            ListingId, Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "sold"), 4);
    }

    [Fact]
    public async Task EditOfAReservedListing_IsWrittenAsBefore()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("reserved", 4));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Success(Listing("reserved", 5)));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _crops.Received(1).UpdateAsync(
            ListingId, Arg.Is<Dictionary<string, object>>(d => d.ContainsKey("pricePerKg")), 4);
    }

    // ===== helpers =====

    private Task<JsonElement> DeleteAsync(int version)
        => SendAsync(FarmerId, "farmer", Op("delete", new Dictionary<string, object> { { "version", version } }));

    private Task<JsonElement> UpdateAsync(Dictionary<string, object> data)
        => SendAsync(FarmerId, "farmer", Op("update", data));

    private static SyncOperationRequest Op(string action, Dictionary<string, object> data) => new()
    {
        OperationId = "op-crop",
        IdempotencyKey = $"{FarmerId}:crop_listing:{ListingId}:{action}:{Guid.NewGuid():N}",
        EntityType = "crop_listing",
        EntityId = ListingId,
        Action = action,
        Data = data
    };

    private async Task<JsonElement> SendAsync(string userId, string role, SyncOperationRequest operation)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-494",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            Headers = new Dictionary<string, string> { { ClientAppBuild.Header, CurrentAppBuild } },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } }
            }
        };

        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb, "test-trips-table", "test-deliveries-table", orderRepo: _orders);
        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
