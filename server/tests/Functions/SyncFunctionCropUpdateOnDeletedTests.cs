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
/// FUP-152 through <c>/sync</c>: a <c>crop_listing</c> <c>update</c> that reaches a deleted listing
/// (<c>deletedAt</c> set, or <c>status: deleted</c>) writes nothing, with or without a status, at any
/// version, and its farmer gets <c>conflict</c> with the stored row (server wins), as a trip's trucker
/// does. Before, an update without a status went through the version check alone and wrote price,
/// quantity or text onto the hidden row. Anyone else still gets <c>CROP_UNAUTHORIZED</c> and no row.
/// </summary>
public class SyncFunctionCropUpdateOnDeletedTests
{
    private const string FarmerId = "farmer-001";
    private const string ListingId = "listing-152";
    private const string CurrentAppBuild = "3";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionCropUpdateOnDeletedTests()
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

    [Theory]
    [InlineData(true, 4)] // deleted by a delete, the client at the stored version
    [InlineData(true, 3)] // behind the delete
    [InlineData(false, 4)] // deleted by an update before FUP-132 wrote the stamp
    [InlineData(true, 9)] // ahead of the stored version
    public async Task UpdateWithoutAStatus_OnADeletedListing_IsAConflictWithTheStoredRow_NothingWritten(
        bool stamped, int sentVersion)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped));

        var result = await UpdateAsync(new Dictionary<string, object>
        {
            { "pricePerKg", 60.0 }, { "quantity", 2.0 }, { "description", "Fresh teff" }, { "version", sentVersion }
        });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("deleted");
        result.GetProperty("serverData").GetProperty("pricePerKg").GetDouble().Should().Be(45.5);
        result.GetProperty("message").GetString().Should().Be("CROP_ALREADY_DELETED: The listing is already deleted.");
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UpdateWithoutAVersion_OnADeletedListing_IsTheSameConflict()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped: true));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    [InlineData("sold")]
    [InlineData("deleted")]
    public async Task UpdateWithAStatus_OnADeletedListing_IsTheSameConflict_NoOrderRead(string status)
    {
        // Before, the transition table answered CROP_INVALID_TRANSITION at the stored version, which
        // the apps retried. The deleted listing is answered before the table now.
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped: true));

        var result = await UpdateAsync(new Dictionary<string, object> { { "status", status }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("message").GetString().Should().StartWith("CROP_ALREADY_DELETED:");
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("farmer-002", "farmer")]
    [InlineData("trucker-001", "trucker")]
    [InlineData("buyer-001", "buyer")]
    public async Task UpdateOnSomeoneElsesDeletedListing_IsStillUnauthorized_AndGetsNoRow(string userId, string role)
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("deleted", 4, stamped: true));

        var result = await SendAsync(userId, role, Op(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } }));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("CROP_UNAUTHORIZED");
        (result.TryGetProperty("serverData", out var data) && data.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("only the listing's farmer gets the row");
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task DeletedBetweenTheReadAndTheWrite_TheWriteRefuses_AndTheFarmerGetsTheDeletedRow()
    {
        // The read saw the live listing at version 4; a delete from the farmer's other device landed
        // before the write, so the repository's condition refused it. An eventually consistent read
        // may still return the live row; the answer must come from the consistent read.
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 4));
        _crops.GetByIdConsistentAsync(ListingId).Returns(Listing("deleted", 5, stamped: true));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Failure(
                409, CropRepository.AlreadyDeletedCode, "The listing is already deleted.", "status"));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("deleted");
        result.GetProperty("message").GetString().Should().Be("CROP_ALREADY_DELETED: The listing is already deleted.");
    }

    [Fact]
    public async Task UpdateOnALiveListing_IsWrittenAsBefore()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 4));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Success(Listing("available", 5)));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        await _crops.Received(1).UpdateAsync(
            ListingId, Arg.Is<Dictionary<string, object>>(d => d.ContainsKey("pricePerKg")), 4);
    }

    [Fact]
    public async Task VersionConflictOnALiveListing_KeepsItsMessage()
    {
        _crops.GetByIdAsync(ListingId).Returns(Listing("available", 5));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 4).Returns(
            AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Version conflict: server has version 5, client expected 4.", "version"));

        var result = await UpdateAsync(new Dictionary<string, object> { { "pricePerKg", 60.0 }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("message").GetString().Should().StartWith("Version conflict");
    }

    // ===== helpers =====

    private Task<JsonElement> UpdateAsync(Dictionary<string, object> data)
        => SendAsync(FarmerId, "farmer", Op(data));

    private static SyncOperationRequest Op(Dictionary<string, object> data) => new()
    {
        OperationId = "op-crop-update",
        IdempotencyKey = $"{FarmerId}:crop_listing:{ListingId}:update:{Guid.NewGuid():N}",
        EntityType = "crop_listing",
        EntityId = ListingId,
        Action = "update",
        Data = data
    };

    private async Task<JsonElement> SendAsync(string userId, string role, SyncOperationRequest operation)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-152",
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
