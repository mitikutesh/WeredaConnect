using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-18 through <c>/sync</c>, for the farmer: a listing that orders depend on cannot be deleted
/// (a <c>delete</c>, or an <c>update</c> to <c>status: deleted</c>) while one of its orders is being
/// paid, held, waiting on transport or accepted: <c>CROP_HAS_ACTIVE_ORDERS</c>, nothing written.
/// Orders that are done (or never paid) do not block. The quantity a farmer edits is the free
/// stock: what funded orders hold already left it at funding, so a quantity edit cannot reach it.
/// </summary>
public class SyncFunctionListingGuardTests
{
    private const string FarmerId = "farmer-001";
    private const string ListingId = "listing-001";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly List<Dictionary<string, object>> _ordersOnListing = new();

    public SyncFunctionListingGuardTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _orders.QueryByListingAsync(ListingId).Returns(_ => _ordersOnListing.ToList());
        _crops.GetByIdAsync(ListingId).Returns(_ => new Dictionary<string, object>
        {
            { "cropId", ListingId }, { "farmerId", FarmerId }, { "cropType", "teff" },
            { "quantity", 6.0 }, { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "3" }
        });
        _crops.DeleteAsync(ListingId, 3).Returns(AuthResult<Dictionary<string, object>>.Success(
            new Dictionary<string, object> { { "cropId", ListingId }, { "status", "deleted" }, { "version", "4" } }));
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 3).Returns(
            AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", ListingId }, { "version", "4" } }));
    }

    private void OrderOnListing(string status)
        => _ordersOnListing.Add(new Dictionary<string, object>
        {
            { "orderId", $"order-{_ordersOnListing.Count + 1}" }, { "listingId", ListingId }, { "status", status }
        });

    // ===== delete =====

    [Theory]
    [InlineData("payment_submitted")]
    [InlineData("funded")]
    [InlineData(OrderCommitment.AwaitingTransportStatus)]
    [InlineData("accepted")]
    public async Task Delete_WithAnOrderThatStillNeedsTheListing_IsRefused_NothingWritten(string status)
    {
        OrderOnListing("settled");
        OrderOnListing(status);

        var result = await DeleteAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("CROP_HAS_ACTIVE_ORDERS:").And.Contain("1 order(s)");
        await _crops.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("settled")]
    public async Task Delete_WithOrdersThatNoLongerNeedTheListing_GoesThrough(string status)
    {
        OrderOnListing(status);

        var result = await DeleteAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        await _crops.Received(1).DeleteAsync(ListingId, 3);
    }

    [Fact]
    public async Task Delete_WithNoOrders_GoesThrough()
    {
        (await DeleteAsync()).GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).QueryByListingAsync(ListingId);
    }

    [Fact]
    public async Task Delete_OrdersCannotBeRead_RefusedForNow_WithARetryableMessage()
    {
        _orders.QueryByListingAsync(ListingId).ThrowsAsync(new InvalidOperationException("index down"));

        var result = await DeleteAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        var message = result.GetProperty("message").GetString();
        message.Should().Be("The listing's orders could not be checked. Please try again later.");
        message.Should().NotContain(":"); // unprefixed: the client keeps it and sends it again
        await _crops.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Delete_FlagOff_IsNotGuarded()
    {
        // Like TRIP_HAS_BOOKINGS, the code reaches clients only with COMMITMENTS_ENABLED (spec K1).
        OrderOnListing("funded");

        var result = await DeleteAsync(enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Delete_SomeoneElsesListing_IsRefusedBeforeAnyOrderIsRead()
    {
        var result = await SendAsync("farmer-002", "farmer", Op("delete", new Dictionary<string, object> { { "version", 3 } }));

        result.GetProperty("message").GetString().Should().StartWith("CROP_UNAUTHORIZED");
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
    }

    // ===== update to deleted =====

    [Fact]
    public async Task UpdateToDeleted_WithAnActiveOrder_IsRefusedLikeADelete()
    {
        OrderOnListing("funded");

        var result = await UpdateAsync(new Dictionary<string, object> { { "status", "deleted" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("CROP_HAS_ACTIVE_ORDERS:");
        await _crops.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UpdateToDeleted_WithOnlyFinishedOrders_GoesThrough()
    {
        OrderOnListing("settled");

        var result = await UpdateAsync(new Dictionary<string, object> { { "status", "deleted" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== quantity: the free stock =====

    [Fact]
    public async Task QuantityEdit_WithFundedOrders_ChangesOnlyTheFreeStock_NoOrderRead()
    {
        // 6 quintals are free; a funded order already took its quintals at funding, so lowering the
        // free stock to 2 leaves that order whole. Nothing to guard, nothing to read.
        OrderOnListing("funded");

        var result = await UpdateAsync(new Dictionary<string, object> { { "quantity", 2.0 }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _crops.Received(1).UpdateAsync(ListingId,
            Arg.Is<Dictionary<string, object>>(d => d.ContainsKey("quantity")), 3);
        await _orders.DidNotReceive().QueryByListingAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public async Task QuantityEdit_BelowTheMinimum_IsRefused(double quantity)
    {
        // The free stock never goes negative, so it can never eat into what orders hold.
        var result = await UpdateAsync(new Dictionary<string, object> { { "quantity", quantity }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("CROP_VALIDATION:");
        await _crops.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task QuantityEdit_FromBeforeAFunding_ConflictsInsteadOfOverwritingTheTakenStock()
    {
        // The farmer edited version 2; a funding has since taken stock (version 3). The stale write
        // is refused by the version check, so it can never put back stock an order holds.
        _crops.UpdateAsync(ListingId, Arg.Any<Dictionary<string, object>>(), 2).Returns(
            AuthResult<Dictionary<string, object>>.Failure(new AuthResultError
            {
                HttpStatus = 409,
                Code = "SYNC_CONFLICT",
                Message = "Version conflict: server has version 3, client expected 2."
            }));

        var result = await UpdateAsync(new Dictionary<string, object> { { "quantity", 10.0 }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
    }

    // ===== helpers =====

    private Task<JsonElement> DeleteAsync(bool enabled = true)
        => SendAsync(FarmerId, "farmer", Op("delete", new Dictionary<string, object> { { "version", 3 } }), enabled);

    private Task<JsonElement> UpdateAsync(Dictionary<string, object> data)
        => SendAsync(FarmerId, "farmer", Op("update", data));

    private static SyncOperationRequest Op(string action, Dictionary<string, object> data) => new()
    {
        OperationId = $"op-crop-{action}",
        IdempotencyKey = $"{FarmerId}:crop_listing:{ListingId}:{action}:{Guid.NewGuid():N}",
        EntityType = "crop_listing",
        EntityId = ListingId,
        Action = action,
        Data = data
    };

    private async Task<JsonElement> SendAsync(string userId, string role, SyncOperationRequest operation, bool enabled = true)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-18",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } }
            }
        };

        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb, "test-trips-table", "test-deliveries-table", orderRepo: _orders)
        {
            CommitmentsEnabled = enabled
        };
        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
