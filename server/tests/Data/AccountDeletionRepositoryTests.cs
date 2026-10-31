using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>ACC-02: the DynamoDB requests of an account deletion: indexes, conditions, removal lists.</summary>
public class AccountDeletionRepositoryTests
{
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<QueryRequest> _queries = new();
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly AccountDeletionRepository _repo;

    private static readonly AccountDeletionTables Tables = new()
    {
        Users = "users",
        Crops = "crops",
        Trips = "trips",
        Orders = "orders",
        Deliveries = "deliveries",
        Escrows = "escrows",
        EscrowAudit = "escrow-audit",
        EscrowIdempotency = "escrow-idempotency",
        Ratings = "ratings",
        Messages = "messages",
        Intents = "intents",
        MatchEvents = "match-events",
        DeviceTokens = "device-tokens"
    };

    public AccountDeletionRepositoryTests()
    {
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => _queries.Add(Clone(r))), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _updates.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = new Dictionary<string, AttributeValue>() });
        _repo = new AccountDeletionRepository(_dynamoDb, Tables);
    }

    private static QueryRequest Clone(QueryRequest r) => new()
    {
        TableName = r.TableName,
        IndexName = r.IndexName,
        ExpressionAttributeNames = r.ExpressionAttributeNames,
        ExpressionAttributeValues = r.ExpressionAttributeValues
    };

    private static AttributeValue S(string value) => new() { S = value };

    private void GetItemReturns(string table, params Dictionary<string, AttributeValue>?[] items)
    {
        var queue = new Queue<Dictionary<string, AttributeValue>?>(items);
        _dynamoDb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == table), Arg.Any<CancellationToken>())
            .Returns(_ => new GetItemResponse
            {
                Item = (queue.Count > 1 ? queue.Dequeue() : queue.Peek()) ?? new Dictionary<string, AttributeValue>()
            });
    }

    [Fact]
    public async Task PartyQueries_UseTheBuyerFarmerAndTruckerIndexes_NeverAScan()
    {
        await _repo.QueryOrdersAsync("user-1");
        await _repo.QueryEscrowsAsync("user-1");
        await _repo.QueryDeliveriesAsync("user-1", new[] { new BuyerOrderRef("order-1", "crop-1") });
        await _repo.QueryCropKeysAsync("user-1");
        await _repo.QueryTripKeysAsync("user-1");
        await _repo.QueryRatingsReceivedAsync("user-1");
        await _repo.QueryMessageIdsAsync("user-1");

        _queries.Select(q => $"{q.TableName}/{q.IndexName}").Should().BeEquivalentTo(new[]
        {
            "orders/BuyerIdCreatedAtIndex", "orders/FarmerIdCreatedAtIndex", "orders/TruckerIdCreatedAtIndex",
            "escrows/BuyerStatusIndex", "escrows/FarmerStatusIndex", "escrows/TruckerStatusIndex",
            "deliveries/FarmerIdCreatedAtIndex", "deliveries/TruckerIdCreatedAtIndex", "deliveries/ParticipantIndex",
            "deliveries/ListingIndex",
            "crops/FarmerIdCreatedAtIndex", "trips/TruckerIdCreatedAtIndex",
            "ratings/RateeIdCreatedAtIndex",
            "messages/SenderIdCreatedAtIndex", "messages/RecipientIdCreatedAtIndex"
        });
        await _dynamoDb.DidNotReceiveWithAnyArgs().ScanAsync(default(ScanRequest)!, default);
    }

    [Fact]
    public async Task PartyQueries_ReadEveryPage()
    {
        var page2Key = new Dictionary<string, AttributeValue> { ["orderId"] = S("o-1") };
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == "BuyerIdCreatedAtIndex"), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse
                {
                    Items = new() { new() { ["orderId"] = S("o-1"), ["status"] = S("settled") } },
                    LastEvaluatedKey = page2Key
                },
                new QueryResponse { Items = new() { new() { ["orderId"] = S("o-2"), ["status"] = S("created") } } });

        var rows = await _repo.QueryOrdersAsync("user-1");

        rows.Where(r => r.Role == "buyer").Select(r => r.Item["orderId"]).Should().Equal("o-1", "o-2");
    }

    [Fact]
    public async Task LegacyDeliveries_AreTakenFromTheListing_OnlyForThisBuyerOrOrder_WithoutDuplicates()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == "ParticipantIndex"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new() { new() { ["deliveryId"] = S("d-1"), ["buyerId"] = S("user-1") } } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == "ListingIndex"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new()
                {
                    new() { ["deliveryId"] = S("d-1"), ["buyerId"] = S("user-1") },
                    new() { ["deliveryId"] = S("d-2"), ["orderId"] = S("order-1") },
                    new() { ["deliveryId"] = S("d-3"), ["buyerId"] = S("someone-else"), ["orderId"] = S("order-9") }
                }
            });

        var rows = await _repo.QueryDeliveriesAsync("user-1", new[] { new BuyerOrderRef("order-1", "crop-1") });

        rows.Select(r => (r.Role, r.Item["deliveryId"])).Should().BeEquivalentTo(new[] { ("buyer", (object)"d-1"), ("buyer", (object)"d-2") });
    }

    [Fact]
    public async Task LegacyDeliveries_AreQueriedOncePerListing()
    {
        await _repo.QueryDeliveriesAsync("user-1", new[]
        {
            new BuyerOrderRef("order-1", "crop-1"),
            new BuyerOrderRef("order-2", "crop-1"),
            new BuyerOrderRef("order-3", "crop-2"),
            new BuyerOrderRef("order-4", "")
        });

        _queries.Where(q => q.IndexName == "ListingIndex")
            .Select(q => q.ExpressionAttributeValues[":v"].S)
            .Should().Equal("crop-1", "crop-2");
    }

    [Fact]
    public async Task MarkInProgress_OnlyFromALiveUnsuspendedRow_AndKeepsTheFirstRequestTime()
    {
        // The constructor's Arg.Do still records the request.
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue> { ["deletionRequestedAt"] = S("2026-10-23T09:00:00Z") }
            });

        var stored = await _repo.TryMarkInProgressAsync("user-1", "2026-10-23T10:00:00Z");

        stored.Should().Be("2026-10-23T09:00:00Z");
        var update = _updates.Single();
        update.UpdateExpression.Should().Contain("if_not_exists(#dra, :now)");
        update.ConditionExpression.Should().Contain("attribute_not_exists(#ds) OR #ds = :inProgress")
            .And.Contain("attribute_not_exists(#susp) OR #susp = :false");
    }

    [Fact]
    public async Task MarkInProgress_LostCondition_ReturnsNull()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("no"));

        (await _repo.TryMarkInProgressAsync("user-1", "now")).Should().BeNull();
    }

    [Fact]
    public async Task Tombstone_RemovesEveryAttributeOffTheAllowlist_IncludingUnknownOnes()
    {
        GetItemReturns("users", new Dictionary<string, AttributeValue>
        {
            ["userId"] = S("user-1"),
            ["role"] = S("farmer"),
            ["phoneNumber"] = S("+251911000001"),
            ["displayName"] = S("Abebe"),
            ["faydaId"] = S("FAYDA-1"),
            ["woreda"] = S("Adama"),
            ["knownDevices"] = new AttributeValue { SS = new List<string> { "d-1" } },
            ["someFieldAddedNextYear"] = S("x"),
            ["deletionState"] = S("in_progress"),
            ["deletionRequestedAt"] = S("t0"),
            ["createdAt"] = S("c"),
            ["updatedAt"] = S("u"),
            ["version"] = new AttributeValue { N = "4" }
        });

        await _repo.TombstoneAsync("user-1", "t1", AccountDeletionPolicy.CognitoPending, requireInProgress: true);

        var update = _updates.Single();
        var removed = update.ExpressionAttributeNames
            .Where(kv => kv.Key.StartsWith("#r", StringComparison.Ordinal))
            .Select(kv => kv.Value);
        removed.Should().BeEquivalentTo(new[] { "phoneNumber", "displayName", "faydaId", "woreda", "knownDevices", "someFieldAddedNextYear" });
        update.UpdateExpression.Should().Contain("#ds = :next").And.Contain("if_not_exists(#da, :deletedAt)").And.Contain("#v = :newVersion");
        update.ExpressionAttributeValues[":next"].S.Should().Be("cognito_pending");
        update.ExpressionAttributeValues[":newVersion"].N.Should().Be("5");
        update.ConditionExpression.Should().Be("#v = :readVersion AND #ds = :inProgress");
    }

    [Fact]
    public async Task Tombstone_ReReadsAfterAConcurrentWrite()
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["userId"] = S("user-1"),
            ["role"] = S("farmer"),
            ["deletionState"] = S("in_progress"),
            ["phoneNumber"] = S("+251911000001"),
            ["version"] = new AttributeValue { N = "4" }
        };
        GetItemReturns("users", row);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new ConditionalCheckFailedException("version moved"),
                _ => new UpdateItemResponse { Attributes = new() { ["deletedAt"] = S("t1") } });

        var deletedAt = await _repo.TombstoneAsync("user-1", "t1", AccountDeletionPolicy.CognitoPending, requireInProgress: true);

        deletedAt.Should().Be("t1");
        await _dynamoDb.Received(2).GetItemAsync(Arg.Is<GetItemRequest>(r => r.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tombstone_AnotherRunGotThereFirst_ReturnsItsDeletedAt_WithoutWriting()
    {
        GetItemReturns("users", new Dictionary<string, AttributeValue>
        {
            ["userId"] = S("user-1"),
            ["role"] = S("farmer"),
            ["deletionState"] = S("cognito_pending"),
            ["deletedAt"] = S("t0")
        });

        (await _repo.TombstoneAsync("user-1", "t1", AccountDeletionPolicy.CognitoPending, requireInProgress: true))
            .Should().Be("t0");
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task WithdrawCrop_OnOffer_IsSoftDeleted_ConditionalOnStatusAndVersion()
    {
        GetItemReturns("crops", new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S("crop-1"),
            ["createdAt"] = S("c"),
            ["status"] = S("available"),
            ["farmerName"] = S("Abebe"),
            ["farmerPhone"] = S("+251911000001"),
            ["version"] = new AttributeValue { N = "3" }
        });

        (await _repo.WithdrawCropAsync(new ListingKey("crop-1", "c"), "now")).Should().BeTrue();

        var update = _updates.Single();
        update.UpdateExpression.Should().Contain("#s = :withdrawn").And.Contain("#da = :now").And.Contain("REMOVE");
        update.ExpressionAttributeValues[":withdrawn"].S.Should().Be("deleted");
        update.ExpressionAttributeValues[":sentinel"].S.Should().Be(AccountDeletionPolicy.Sentinel);
        update.ExpressionAttributeValues[":newVersion"].N.Should().Be("4");
        update.ConditionExpression.Should().Be("#s = :readStatus AND #v = :readVersion");
        update.ExpressionAttributeNames.Values.Should().Contain(new[]
        {
            "farmerPhone", "latitude", "longitude", "locationName", "description", "audioNoteRef", "photoIds"
        });
    }

    [Fact]
    public async Task WithdrawCrop_RemovesThePhotoIds_SoNoPhotoUrlIsSignedAgain()
    {
        GetItemReturns("crops", new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S("crop-1"),
            ["createdAt"] = S("c"),
            ["status"] = S("sold"),
            ["farmerName"] = S("Abebe"),
            ["version"] = new AttributeValue { N = "3" },
            ["photoIds"] = new AttributeValue
            {
                L = new List<AttributeValue> { S("7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11"), S("8e2d1b63-4a1f-4d5c-8b9f-1a4c7d3e0f22") }
            }
        });

        await _repo.WithdrawCropAsync(new ListingKey("crop-1", "c"), "now");

        var update = _updates.Single();
        var alias = update.ExpressionAttributeNames.Single(kv => kv.Value == "photoIds").Key;
        update.UpdateExpression.Should().MatchRegex($@"REMOVE .*{alias}\b");
    }

    [Fact]
    public async Task WithdrawCrop_Sold_KeepsItsStatus_SetsDeletedAtOnlyIfMissing()
    {
        GetItemReturns("crops", new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S("crop-1"),
            ["createdAt"] = S("c"),
            ["status"] = S("sold"),
            ["farmerName"] = S("Abebe"),
            ["version"] = new AttributeValue { N = "3" }
        });

        await _repo.WithdrawCropAsync(new ListingKey("crop-1", "c"), "now");

        var update = _updates.Single();
        update.UpdateExpression.Should().Contain("#da = if_not_exists(#da, :now)").And.NotContain(":withdrawn");
    }

    [Fact]
    public async Task WithdrawCrop_AlreadyScrubbed_WritesNothing()
    {
        GetItemReturns("crops", new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S("crop-1"),
            ["createdAt"] = S("c"),
            ["status"] = S("deleted"),
            ["deletedAt"] = S("t"),
            ["farmerName"] = S(AccountDeletionPolicy.Sentinel),
            ["version"] = new AttributeValue { N = "4" }
        });

        (await _repo.WithdrawCropAsync(new ListingKey("crop-1", "c"), "now")).Should().BeFalse();
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task WithdrawTrip_Open_IsCancelled()
    {
        GetItemReturns("trips", new Dictionary<string, AttributeValue>
        {
            ["tripId"] = S("trip-1"),
            ["createdAt"] = S("c"),
            ["status"] = S("matched"),
            ["truckerName"] = S("Kebede"),
            ["version"] = new AttributeValue { N = "1" }
        });

        await _repo.WithdrawTripAsync(new ListingKey("trip-1", "c"), "now");

        var update = _updates.Single();
        update.TableName.Should().Be("trips");
        update.ExpressionAttributeValues[":withdrawn"].S.Should().Be("cancelled");
        update.ExpressionAttributeNames.Values.Should().Contain(new[] { "truckerPhone", "vehiclePlate", "vehicleDescription" });
    }

    [Fact]
    public async Task CancelUnpaidOrder_IsConditionalOnAnUnpaidStatusAndTheVersionRead()
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = "order-1",
            ["createdAt"] = "c",
            ["status"] = "created",
            ["version"] = "2"
        };

        (await _repo.TryCancelUnpaidOrderAsync(order, "now")).Should().BeTrue();

        var update = _updates.Single();
        update.ConditionExpression.Should().Be("#s IN (:created, :pending, :failed) AND #v = :readVersion");
        update.ExpressionAttributeValues[":reason"].S.Should().Be("account_deleted");
        update.ExpressionAttributeValues[":readVersion"].N.Should().Be("2");
        update.ExpressionAttributeValues[":newVersion"].N.Should().Be("3");
    }

    [Fact]
    public async Task CancelUnpaidOrder_LostCondition_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("paid"));

        (await _repo.TryCancelUnpaidOrderAsync(new Dictionary<string, object>
        {
            ["orderId"] = "order-1",
            ["createdAt"] = "c",
            ["status"] = "created",
            ["version"] = "2"
        }, "now")).Should().BeFalse();
    }

    [Fact]
    public async Task ScrubDelivery_SetsTheSentinel_RemovesThePhone_OnlyForTheUsersRole()
    {
        var delivery = new Dictionary<string, object>
        {
            ["deliveryId"] = "d-1",
            ["tripId"] = "trip-1",
            ["farmerName"] = "Abebe",
            ["farmerPhone"] = "+251911000001",
            ["buyerName"] = "Sara",
            ["buyerPhone"] = "+251911000003"
        };

        (await _repo.ScrubDeliveryAsync(delivery, new[] { "farmer" }, "now")).Should().BeTrue();

        var update = _updates.Single();
        update.Key.Keys.Should().BeEquivalentTo(new[] { "deliveryId", "tripId" });
        update.UpdateExpression.Should().Be("SET #n0 = :sentinel, #ua = :now REMOVE #p0");
        update.ExpressionAttributeNames["#n0"].Should().Be("farmerName");
        update.ExpressionAttributeNames["#p0"].Should().Be("farmerPhone");
        update.ConditionExpression.Should().Be("attribute_exists(#pk)");
    }

    [Fact]
    public async Task ScrubEscrow_OnlyRewritesTheCopiedName()
    {
        var escrow = new Dictionary<string, object>
        {
            ["escrowId"] = "e-1",
            ["createdAt"] = "c",
            ["buyerName"] = "Sara",
            ["amount"] = "100",
            ["status"] = "completed"
        };

        await _repo.ScrubEscrowAsync(escrow, new[] { "buyer" });

        var update = _updates.Single();
        update.UpdateExpression.Should().Be("SET #n0 = :sentinel");
        update.ExpressionAttributeNames["#n0"].Should().Be("buyerName");
    }

    [Fact]
    public async Task Scrub_WithNothingToChange_WritesNothing()
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = "order-1",
            ["createdAt"] = "c",
            ["farmerName"] = AccountDeletionPolicy.Sentinel
        };

        (await _repo.ScrubOrderAsync(order, new[] { "farmer", "trucker" }, "now")).Should().BeFalse();
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task RatingComment_IsRemoved_StarsUntouched()
    {
        (await _repo.RemoveRatingCommentAsync("order-1:farmer-1")).Should().BeTrue();

        var update = _updates.Single();
        update.UpdateExpression.Should().Be("REMOVE #c");
        update.ExpressionAttributeNames["#c"].Should().Be("comment");
    }

    [Fact]
    public async Task TheSweepScan_FiltersStartedDeletionsOlderThanTheCutoff()
    {
        _dynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = new() { new() { ["userId"] = S("user-1") } } });

        var (ids, next) = await _repo.ScanIncompleteAsync("cutoff", null);

        ids.Should().Equal("user-1");
        next.Should().BeNull();
        await _dynamoDb.Received(1).ScanAsync(
            Arg.Is<ScanRequest>(r => r.TableName == "users"
                                     && r.FilterExpression == "#ds IN (:inProgress, :pending) AND #dra < :cutoff"
                                     && r.ExpressionAttributeValues[":cutoff"].S == "cutoff"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkCompleted_OnlyFromCognitoPendingOrCompleted()
    {
        (await _repo.MarkCompletedAsync("user-1")).Should().BeTrue();

        _updates.Single().ConditionExpression.Should().Be("#ds IN (:pending, :completed)");
    }
}
