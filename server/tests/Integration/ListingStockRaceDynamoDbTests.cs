using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-18 against real DynamoDB (LocalStack in CI): fundings of one listing race for its last stock
/// with the real conditional writes. The listing never goes below zero, exactly the fundings that fit
/// win, every loser takes nothing and is recorded as refused, a restore for a loser gives nothing
/// back, and the end-to-end funding of a loser ends its order and arms its escrow refund. The
/// listing guards read a listing's orders through <c>ListingIdIndex</c>.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ListingStockRaceDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _listingId = $"crop-esc18-{Guid.NewGuid():N}";

    public ListingStockRaceDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;
    private CropRepository Crops => new(Ddb, _db.Crops);
    private OrderRepository Orders => new(Ddb, _db.Orders);

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private Task SeedListingAsync(string quantity) => Ddb.PutItemAsync(new PutItemRequest
    {
        TableName = _db.Crops,
        Item = new Dictionary<string, AttributeValue>
        {
            ["cropId"] = S(_listingId),
            ["createdAt"] = S("2026-11-01T08:00:00.0000000Z"),
            ["farmerId"] = S("farmer-esc18"),
            ["status"] = S("available"),
            ["quantity"] = N(quantity),
            ["version"] = N("1")
        }
    });

    private async Task<Dictionary<string, AttributeValue>> ListingAsync()
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Crops,
            Key = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(_listingId),
                ["createdAt"] = S("2026-11-01T08:00:00.0000000Z")
            },
            ConsistentRead = true
        })).Item;

    private static decimal Quantity(Dictionary<string, AttributeValue> listing) => decimal.Parse(
        listing["quantity"].N, System.Globalization.CultureInfo.InvariantCulture);

    private static List<string> Set(Dictionary<string, AttributeValue> listing, string name)
        => listing.TryGetValue(name, out var set) ? set.SS : new List<string>();

    [LocalDynamoDbFact]
    public async Task TwoFundingsForMoreThanTheStock_ExactlyOneWins_TheListingNeverGoesBelowZero()
    {
        await SeedListingAsync("10");

        var results = await Task.WhenAll(
            Task.Run(() => Crops.TryDecrementStockAsync(_listingId, "order-a", 8)),
            Task.Run(() => Crops.TryDecrementStockAsync(_listingId, "order-b", 5)));

        results.Count(r => r == StockDecrementResult.Decremented).Should().Be(1);
        results.Count(r => r == StockDecrementResult.InsufficientStock).Should().Be(1);
        var winner = results[0] == StockDecrementResult.Decremented ? "order-a" : "order-b";
        var loser = winner == "order-a" ? "order-b" : "order-a";
        var listing = await ListingAsync();
        Quantity(listing).Should().Be(winner == "order-a" ? 2m : 5m);
        Set(listing, "stockDecrementedFor").Should().Equal(winner);
        Set(listing, "stockRefusedFor").Should().Equal(loser);
        listing["version"].N.Should().Be("2"); // the refusal bumps nothing

        // The loser took nothing, so it gives nothing back; the winner's restore still works.
        (await Crops.RestoreStockAsync(_listingId, loser, winner == "order-a" ? 5 : 8)).Should().Be(StockRestoreResult.AlreadyRestored);
        Quantity(await ListingAsync()).Should().Be(winner == "order-a" ? 2m : 5m);
        (await Crops.RestoreStockAsync(_listingId, winner, winner == "order-a" ? 8 : 5)).Should().Be(StockRestoreResult.Restored);
        Quantity(await ListingAsync()).Should().Be(10m);
    }

    [LocalDynamoDbTheory]
    [InlineData(33.6, 66.4)]
    [InlineData(49.5, 50.5)]
    public async Task TwoFundingsThatAddUpToTheStock_BothWinAtOnce_AndTheRestoresRoundTrip(double firstKg, double secondKg)
    {
        // Review blocker: a remainder rounded to 2 decimals lost up to half a kilogram per funding
        // and refused an order that fits. Exact decimals: both win, the listing ends at exactly 0.
        await SeedListingAsync("1.00");

        var results = await Task.WhenAll(
            Task.Run(() => Crops.TryDecrementStockAsync(_listingId, "order-a", firstKg / 100.0)),
            Task.Run(() => Crops.TryDecrementStockAsync(_listingId, "order-b", secondKg / 100.0)));

        results.Should().OnlyContain(r => r == StockDecrementResult.Decremented);
        var listing = await ListingAsync();
        Quantity(listing).Should().Be(0m);
        listing["status"].S.Should().Be("reserved");

        (await Crops.RestoreStockAsync(_listingId, "order-a", firstKg / 100.0)).Should().Be(StockRestoreResult.Restored);
        Quantity(await ListingAsync()).Should().Be((decimal)firstKg / 100m);
        (await Crops.RestoreStockAsync(_listingId, "order-b", secondKg / 100.0)).Should().Be(StockRestoreResult.Restored);
        Quantity(await ListingAsync()).Should().Be(1m);
    }

    [LocalDynamoDbFact]
    public async Task ThreeFundingsAtOnce_OnlyThoseThatFitWin()
    {
        await SeedListingAsync("10");

        var results = await Task.WhenAll(
            Enumerable.Range(1, 3).Select(i => Task.Run(() => Crops.TryDecrementStockAsync(_listingId, $"order-{i}", 4))));

        results.Count(r => r == StockDecrementResult.Decremented).Should().Be(2);
        results.Count(r => r == StockDecrementResult.InsufficientStock).Should().Be(1);
        var listing = await ListingAsync();
        Quantity(listing).Should().Be(2m);
        Set(listing, "stockDecrementedFor").Should().HaveCount(2);
        Set(listing, "stockRefusedFor").Should().HaveCount(1);
    }

    [LocalDynamoDbFact]
    public async Task ExactlyTheStockLeft_Wins_AndTheRepeatIsIdempotent()
    {
        await SeedListingAsync("5.00");

        (await Crops.TryDecrementStockAsync(_listingId, "order-a", 5)).Should().Be(StockDecrementResult.Decremented);
        (await Crops.TryDecrementStockAsync(_listingId, "order-a", 5)).Should().Be(StockDecrementResult.AlreadyApplied);
        (await Crops.TryDecrementStockAsync(_listingId, "order-b", 0.01)).Should().Be(StockDecrementResult.InsufficientStock);

        var listing = await ListingAsync();
        Quantity(listing).Should().Be(0m);
        listing["status"].S.Should().Be("reserved");
    }

    [LocalDynamoDbFact]
    public async Task FundingThatLostTheStock_EndsTheOrder_AndArmsItsRefund()
    {
        // End to end through the shared funding path with the real tables.
        await SeedListingAsync("3");
        var orderId = $"order-esc18-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S("2026-11-06T07:00:00.0000000Z"),
                ["status"] = S("funded"),
                ["listingId"] = S(_listingId),
                ["buyerId"] = S("buyer-esc18"),
                ["farmerId"] = S("farmer-esc18"),
                ["quantity"] = N("500"),
                ["orderTotal"] = N("2388.75"),
                ["cropCost"] = N("2275"),
                ["paymentVerifiedAt"] = S("2026-11-06T08:00:00.0000000Z"),
                ["version"] = N("3")
            }
        });
        var escrows = new EscrowRepository(Ddb, _db.Escrows);
        var escrowService = new EscrowService(escrows, new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            idempotencyRepo: null, telebirrGateway: null, orderRepo: Orders);
        var funding = new OrderFundingService(Orders, Crops, escrowService, tripRepo: null, commitmentsEnabled: false,
            txAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit));
        var order = await Orders.GetByIdAsync(orderId, consistentRead: true);

        var result = await funding.EnsureFundedSideEffectsAsync(order!, "telebirr", null, new TestLambdaContext());

        result.Stock.Should().Be(StockDecrementResult.InsufficientStock);
        result.EscrowCreated.Should().BeTrue();
        var ended = await Orders.GetByIdAsync(orderId, consistentRead: true);
        ended!["status"].Should().Be(OrderFundingService.OutOfStockStatus);
        ended["cancelReason"].Should().Be("listing_out_of_stock");
        ended["faultParty"].Should().Be("none");
        var escrow = await escrows.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
        escrow!["status"].Should().Be("escrowed");
        escrow["refundReason"].Should().Be("listing_out_of_stock");
        DateTime.Parse(escrow["expiresAt"].ToString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().BeBefore(DateTime.UtcNow.AddMinutes(1));
        Quantity(await ListingAsync()).Should().Be(3m);
    }

    [LocalDynamoDbTheory]
    [InlineData("accepted", false)]
    [InlineData("pending", true)]
    [InlineData(null, true)]
    public async Task OutOfStockCancel_NeverLandsOnAnOrderTheFarmerAccepted(string? farmerResponse, bool lands)
    {
        var orderId = $"order-esc18-{Guid.NewGuid():N}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S("2026-11-06T07:00:00.0000000Z"),
            ["status"] = S("funded"),
            ["version"] = N("3")
        };
        if (farmerResponse != null)
        {
            item["farmerResponse"] = S(farmerResponse);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });

        var ended = await Orders.TryAdvanceStatusUnlessAsync(orderId, "funded", OrderFundingService.OutOfStockStatus,
            new Dictionary<string, object> { { "cancelReason", OrderFundingService.OutOfStockReason } },
            "farmerResponse", "accepted");

        (ended != null).Should().Be(lands);
        var stored = await Orders.GetByIdAsync(orderId, consistentRead: true);
        stored!["status"].Should().Be(lands ? OrderFundingService.OutOfStockStatus : "funded");
    }

    [LocalDynamoDbFact]
    public async Task ListingOrders_AreReadThroughTheListingIndex()
    {
        var other = $"crop-other-{Guid.NewGuid():N}";
        foreach (var (orderId, listingId, status) in new[]
                 {
                     ($"o1-{_listingId}", _listingId, "funded"), ($"o2-{_listingId}", _listingId, "settled"),
                     ($"o3-{_listingId}", other, "accepted")
                 })
        {
            await Ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _db.Orders,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["orderId"] = S(orderId),
                    ["createdAt"] = S($"2026-11-06T0{orderId[1]}:00:00.0000000Z"),
                    ["listingId"] = S(listingId),
                    ["status"] = S(status),
                    ["version"] = N("1")
                }
            });
        }

        var orders = await Orders.QueryByListingAsync(_listingId);

        orders.Select(o => o["status"]).Should().Equal("funded", "settled");
    }
}
