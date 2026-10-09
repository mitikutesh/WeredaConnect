using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-08 against real DynamoDB (LocalStack in CI): the order's delivery slot claim and the
/// delivery row are one transaction (no orphan <c>activeDeliveryId</c>, no row without its
/// claim), a dead holder is replaced only from the claim that was read, two creates racing for
/// one slot leave exactly one delivery, and a party's cancel before pickup frees the slot and
/// stays visible to every party.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeliverySlotDynamoDbTests
{
    private const string CreatedAt = "2026-10-09T08:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = Guid.NewGuid().ToString();
    private readonly string _farmerId = Guid.NewGuid().ToString();
    private readonly string _truckerId = Guid.NewGuid().ToString();
    private readonly string _listingId = Guid.NewGuid().ToString();
    private readonly string _tripId = Guid.NewGuid().ToString();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DeliverySlotDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = (string)ci[0],
            PhoneNumber = "+251900100000",
            DisplayName = "Party Name",
            Role = (string)ci[0] == _buyerId ? "buyer" : (string)ci[0] == _farmerId ? "farmer" : "trucker",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            userRepo: _users, orderRepo: OrderRepo, transactionAuditRepo: TxRepo);
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    // ──────────────────────────────────────────────
    // Seeding
    // ──────────────────────────────────────────────

    private async Task<string> SeedOrderAsync(string? holder = null, string? claimedAt = null)
    {
        var orderId = Guid.NewGuid().ToString();
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId), ["createdAt"] = S(CreatedAt), ["status"] = S("accepted"),
            ["buyerId"] = S(_buyerId), ["farmerId"] = S(_farmerId), ["truckerId"] = S(_truckerId),
            ["tripId"] = S(_tripId), ["listingId"] = S(_listingId),
            ["buyerName"] = S("Mekonnen"), ["farmerName"] = S("Abebe"), ["truckerName"] = S("Chaltu"),
            ["version"] = new AttributeValue { N = "1" }
        };
        if (holder != null) item["activeDeliveryId"] = S(holder);
        if (claimedAt != null) item["activeDeliveryClaimedAt"] = S(claimedAt);
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
        return orderId;
    }

    private async Task SeedListingAndTripAsync()
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(_listingId), ["createdAt"] = S(CreatedAt), ["farmerId"] = S(_farmerId),
                ["status"] = S("reserved"), ["version"] = new AttributeValue { N = "1" }
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = S(_tripId), ["createdAt"] = S(CreatedAt), ["truckerId"] = S(_truckerId),
                ["status"] = S("open"), ["version"] = new AttributeValue { N = "1" }
            }
        });
    }

    private Dictionary<string, AttributeValue> DeliveryRow(string deliveryId, string orderId, string status = "pending") => new()
    {
        ["deliveryId"] = S(deliveryId), ["tripId"] = S(_tripId), ["orderId"] = S(orderId),
        ["listingId"] = S(_listingId), ["buyerId"] = S(_buyerId), ["participantId"] = S(_buyerId),
        ["farmerId"] = S(_farmerId), ["truckerId"] = S(_truckerId),
        ["status"] = S(status), ["createdAt"] = S(CreatedAt), ["version"] = new AttributeValue { N = "1" }
    };

    private async Task SeedDeliveryAsync(string deliveryId, string orderId, string status)
        => await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = DeliveryRow(deliveryId, orderId, status) });

    private async Task<Dictionary<string, object>?> OrderAsync(string orderId) => await OrderRepo.GetByIdAsync(orderId, true);

    private async Task<Dictionary<string, object>?> DeliveryAsync(string deliveryId) => await DeliveryRepo.GetByIdAsync(deliveryId, true);

    private async Task<int> RowCountAsync(string deliveryId)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.Deliveries,
            KeyConditionExpression = "deliveryId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = S(deliveryId) },
            ConsistentRead = true
        });
        return response.Items.Count;
    }

    // ──────────────────────────────────────────────
    // Sync calls
    // ──────────────────────────────────────────────

    private async Task<JsonElement> SendAsync(SyncFunction sync, SyncOperationRequest operation, string userId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = Guid.NewGuid().ToString(),
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await sync.FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private SyncOperationRequest CreateOp(string deliveryId, string orderId) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        IdempotencyKey = $"{_truckerId}:delivery:{deliveryId}:create:{Guid.NewGuid():N}",
        EntityType = "delivery",
        EntityId = deliveryId,
        Action = "create",
        Data = new Dictionary<string, object>
        {
            { "tripId", _tripId }, { "orderId", orderId }, { "listingId", _listingId },
            { "farmerId", _farmerId }, { "farmerName", "Abebe" },
            { "truckerId", _truckerId }, { "truckerName", "Chaltu" },
            { "buyerId", _buyerId }, { "buyerName", "Mekonnen" },
            { "qrToken", "qr-token-it" }
        }
    };

    private static SyncOperationRequest CancelOp(string deliveryId, string action, string userId, int version) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        IdempotencyKey = $"{userId}:delivery:{deliveryId}:{action}:{Guid.NewGuid():N}",
        EntityType = "delivery",
        EntityId = deliveryId,
        Action = action,
        Data = new Dictionary<string, object> { { "status", "cancelled" }, { "version", version } }
    };

    // ──────────────────────────────────────────────
    // The claim and the row are one transaction
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task Claim_RowAlreadyExists_LeavesTheSlotUnclaimed()
    {
        // The crash window ESC-08 closes: the row write fails, so the claim must not land either.
        var orderId = await SeedOrderAsync();
        var deliveryId = Guid.NewGuid().ToString();
        await SeedDeliveryAsync(deliveryId, orderId, "pending");

        var result = await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId));

        result.Should().Be(DeliverySlotWriteResult.DeliveryExists);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().BeNull();
        Str(await OrderAsync(orderId), "activeDeliveryClaimedAt").Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task Claim_SlotTaken_WritesNoRow()
    {
        var holder = Guid.NewGuid().ToString();
        var orderId = await SeedOrderAsync(holder, CreatedAt);
        var deliveryId = Guid.NewGuid().ToString();

        var result = await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId));

        result.Should().Be(DeliverySlotWriteResult.SlotTaken);
        (await RowCountAsync(deliveryId)).Should().Be(0);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(holder);
    }

    [LocalDynamoDbFact]
    public async Task Claim_WritesBoth_AndOnlyTheHolderReleases()
    {
        var orderId = await SeedOrderAsync();
        var deliveryId = Guid.NewGuid().ToString();

        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId)))
            .Should().Be(DeliverySlotWriteResult.Written);
        (await RowCountAsync(deliveryId)).Should().Be(1);
        var claimed = await OrderAsync(orderId);
        Str(claimed, "activeDeliveryId").Should().Be(deliveryId);
        Str(claimed, "activeDeliveryClaimedAt").Should().NotBeNull();
        Str(claimed, "version").Should().Be("1");

        await OrderRepo.ReleaseDeliverySlotAsync(orderId, Guid.NewGuid().ToString());
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(deliveryId);

        await OrderRepo.ReleaseDeliverySlotAsync(orderId, deliveryId);
        var released = await OrderAsync(orderId);
        Str(released, "activeDeliveryId").Should().BeNull();
        Str(released, "activeDeliveryClaimedAt").Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task Replace_OnlyFromTheClaimThatWasRead()
    {
        var dead = Guid.NewGuid().ToString();
        var orderId = await SeedOrderAsync(dead, CreatedAt);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        // The holder's claim moved since it was read: nothing is taken, nothing written.
        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(first, orderId),
                new DeliverySlotHolder(dead, "2026-10-09T07:59:59.0000000Z")))
            .Should().Be(DeliverySlotWriteResult.SlotTaken);
        (await RowCountAsync(first)).Should().Be(0);

        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(first, orderId),
                new DeliverySlotHolder(dead, CreatedAt)))
            .Should().Be(DeliverySlotWriteResult.Written);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(first);

        // The same stale read cannot take the now live slot from the delivery that replaced it.
        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(second, orderId),
                new DeliverySlotHolder(dead, CreatedAt)))
            .Should().Be(DeliverySlotWriteResult.SlotTaken);
        (await RowCountAsync(second)).Should().Be(0);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(first);
    }

    [LocalDynamoDbFact]
    public async Task Replace_LegacyClaimWithoutTime()
    {
        var dead = Guid.NewGuid().ToString();
        var orderId = await SeedOrderAsync(dead);
        var deliveryId = Guid.NewGuid().ToString();

        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId),
                new DeliverySlotHolder(dead, CreatedAt)))
            .Should().Be(DeliverySlotWriteResult.SlotTaken);
        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId),
                new DeliverySlotHolder(dead, null)))
            .Should().Be(DeliverySlotWriteResult.Written);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(deliveryId);
    }

    // ──────────────────────────────────────────────
    // Races through the sync endpoint
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task ConcurrentCreatesOfOneDelivery_LeaveOneRowAndItsClaim()
    {
        await SeedListingAndTripAsync();
        var orderId = await SeedOrderAsync();
        var deliveryId = Guid.NewGuid().ToString();
        var sync = Sync();

        var results = await Task.WhenAll(
            SendAsync(sync, CreateOp(deliveryId, orderId), _truckerId),
            SendAsync(Sync(), CreateOp(deliveryId, orderId), _truckerId));

        results.Count(r => r.GetProperty("status").GetString() == "success").Should().Be(1);
        (await RowCountAsync(deliveryId)).Should().Be(1);
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(deliveryId);
    }

    [LocalDynamoDbFact]
    public async Task ConcurrentTakeoversOfADeadSlot_OnlyOneWins()
    {
        await SeedListingAndTripAsync();
        var dead = Guid.NewGuid().ToString();
        var orderId = await SeedOrderAsync(dead, CreatedAt);
        await SeedDeliveryAsync(dead, orderId, "cancelled");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(
            SendAsync(Sync(), CreateOp(first, orderId), _truckerId),
            SendAsync(Sync(), CreateOp(second, orderId), _truckerId));

        results.Count(r => r.GetProperty("status").GetString() == "success").Should().Be(1);
        var winner = Str(await OrderAsync(orderId), "activeDeliveryId");
        winner.Should().BeOneOf(first, second);
        var loser = winner == first ? second : first;
        (await RowCountAsync(winner!)).Should().Be(1);
        (await RowCountAsync(loser)).Should().Be(0);
        Str(await DeliveryAsync(dead), "status").Should().Be("cancelled");
    }

    [LocalDynamoDbFact]
    public async Task TimedClaimWithoutRow_IsNotTakenOver_UntimedOneIs()
    {
        // Since ESC-08 a timed claim is written with its row, so only an untimed (pre-ESC-08)
        // claim without a row counts as dead.
        await SeedListingAndTripAsync();
        var timedOrder = await SeedOrderAsync(Guid.NewGuid().ToString(), CreatedAt);
        var untimedHolder = Guid.NewGuid().ToString();
        var untimedOrder = await SeedOrderAsync(untimedHolder);
        var sync = Sync();

        var refused = await SendAsync(sync, CreateOp(Guid.NewGuid().ToString(), timedOrder), _truckerId);
        var newId = Guid.NewGuid().ToString();
        var taken = await SendAsync(sync, CreateOp(newId, untimedOrder), _truckerId);

        refused.GetProperty("message").GetString().Should().Be("DELIVERY_VALIDATION: The order already has an active delivery.");
        taken.GetProperty("status").GetString().Should().Be("success");
        Str(await OrderAsync(untimedOrder), "activeDeliveryId").Should().Be(newId);
    }

    // ──────────────────────────────────────────────
    // Cancel: before pickup only, slot freed, visible to every party
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task CancelBeforePickup_FreesTheSlot_AndAReplacementTakesIt()
    {
        await SeedListingAndTripAsync();
        var orderId = await SeedOrderAsync();
        var firstId = Guid.NewGuid().ToString();
        var sync = Sync();
        (await SendAsync(sync, CreateOp(firstId, orderId), _truckerId)).GetProperty("status").GetString().Should().Be("success");

        // The legacy mobile cancel (delete) from the farmer, refused before ESC-08.
        var cancel = await SendAsync(sync, CancelOp(firstId, "delete", _farmerId, version: 1), _farmerId);

        cancel.GetProperty("status").GetString().Should().Be("success");
        var cancelled = await DeliveryAsync(firstId);
        Str(cancelled, "status").Should().Be("cancelled");
        Str(cancelled, "deletedAt").Should().BeNull();
        Str(cancelled, "version").Should().Be("2");
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().BeNull();
        (await TxRepo.GetByTransactionAsync(orderId))
            .Where(r => Str(r, "action") == "delivery_cancelled")
            .Should().ContainSingle(r => Str(r, "metadata") == "via=delete" && Str(r, "actor") == _farmerId);

        var secondId = Guid.NewGuid().ToString();
        (await SendAsync(sync, CreateOp(secondId, orderId), _truckerId)).GetProperty("status").GetString().Should().Be("success");
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(secondId);
    }

    [LocalDynamoDbFact]
    public async Task CancelAfterPickup_IsRefused_AndTheSlotStays()
    {
        var orderId = await SeedOrderAsync();
        var deliveryId = Guid.NewGuid().ToString();
        (await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, DeliveryRow(deliveryId, orderId, "pickedUp")))
            .Should().Be(DeliverySlotWriteResult.Written);

        var result = await SendAsync(Sync(), CancelOp(deliveryId, "delete", _truckerId, version: 1), _truckerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CANCEL_NOT_ALLOWED");
        Str(await DeliveryAsync(deliveryId), "status").Should().Be("pickedUp");
        Str(await OrderAsync(orderId), "activeDeliveryId").Should().Be(deliveryId);
    }

    [LocalDynamoDbFact]
    public async Task ClientCancelledDelivery_StaysVisibleToEveryParty()
    {
        await SeedListingAndTripAsync();
        var orderId = await SeedOrderAsync();
        var deliveryId = Guid.NewGuid().ToString();
        var sync = Sync();
        (await SendAsync(sync, CreateOp(deliveryId, orderId), _truckerId)).GetProperty("status").GetString().Should().Be("success");
        (await SendAsync(sync, CancelOp(deliveryId, "update", _buyerId, version: 1), _buyerId))
            .GetProperty("status").GetString().Should().Be("success");

        static bool IsCancelled(List<Dictionary<string, object>> items, string id)
            => items.Any(d => Str(d, "deliveryId") == id && Str(d, "status") == "cancelled");
        IsCancelled((await DeliveryRepo.QueryByFarmerAsync(_farmerId)).Items, deliveryId).Should().BeTrue();
        IsCancelled((await DeliveryRepo.QueryByTruckerAsync(_truckerId)).Items, deliveryId).Should().BeTrue();
        IsCancelled((await DeliveryRepo.QueryByParticipantAsync(_buyerId)).Items, deliveryId).Should().BeTrue();

        var deliveries = new DeliveryFunction(DeliveryRepo);
        foreach (var (userId, expected) in new[]
                 {
                     (_farmerId, 200), (_truckerId, 200), (_buyerId, 200), (Guid.NewGuid().ToString(), 403)
                 })
        {
            var response = await deliveries.FunctionHandler(new APIGatewayProxyRequest
            {
                HttpMethod = "GET",
                Path = $"/deliveries/{deliveryId}",
                PathParameters = new Dictionary<string, string> { { "id", deliveryId } },
                RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
                }
            }, new TestLambdaContext());

            response.StatusCode.Should().Be(expected, response.Body);
        }
    }
}
