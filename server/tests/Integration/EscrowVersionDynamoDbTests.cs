using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// SIM-29 against real DynamoDB (LocalStack in CI): the escrow's <c>version</c> moves on every write
/// that changes what the escrow reads return, the delivery link and the confirmation stamp included,
/// and a settlement write that still carries a version computed from an earlier read never moves it
/// back.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EscrowVersionDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;

    public EscrowVersionDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);

    private EscrowService Service() => new(
        EscrowRepo, new EscrowAuditRepository(Ddb, _db.EscrowAudit), new EscrowIdempotencyRepository(Ddb, _db.Idempotency),
        Substitute.For<ITelebirrGateway>(), userRepo: null, new OrderRepository(Ddb, _db.Orders),
        new DeliveryRepository(Ddb, _db.Deliveries), orderCheckoutRepo: null,
        transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit))
    {
        AlarmLog = _ => { }
    };

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private async Task<string> SeedEscrowAsync(string escrowId, string status, int version, string? orderId = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(CreatedAt),
            ["status"] = S(status),
            ["buyerId"] = S("buyer-sim29"),
            ["farmerId"] = S("farmer-sim29"),
            ["amount"] = N("1995"),
            ["commission"] = N("95"),
            ["farmerPayout"] = N("1900"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-SIM29"),
            ["expiresAt"] = S("2026-10-08T10:00:00.0000000Z"),
            ["version"] = N(version.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
            item["source"] = S("order");
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private async Task<Dictionary<string, AttributeValue>> RowAsync(string escrowId)
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(escrowId), ["createdAt"] = S(CreatedAt) },
            ConsistentRead = true
        })).Item;

    private async Task<string> VersionAsync(string escrowId) => (await RowAsync(escrowId))["version"].N;

    [LocalDynamoDbFact]
    public async Task Link_Stamp_Release_Complete_EachAddOne_AndAStaleReadNeverMovesItBack()
    {
        var orderId = $"order-sim29-{Guid.NewGuid():N}";
        var escrowId = await SeedEscrowAsync($"escrow-order-{orderId}", "escrowed", 3, orderId);
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId }, { "status", "accepted" }, { "buyerId", "buyer-sim29" },
            { "farmerId", "farmer-sim29" }, { "truckerId", "trucker-sim29" }
        };

        // The delivery link (with the SIM-15 truckerId stamp).
        (await Service().LinkDeliveryToOrderEscrowAsync(orderId, "delivery-sim29", order, "system")).Should().BeTrue();
        var linked = await RowAsync(escrowId);
        linked["deliveryId"].S.Should().Be("delivery-sim29");
        linked["truckerId"].S.Should().Be("trucker-sim29");
        linked["version"].N.Should().Be("4");

        // The confirmation stamp (sync at delivery confirm, or the escrow-cron backstop).
        await EscrowRepo.UpdateStatusAsync(escrowId, CreatedAt, "escrowed", "escrowed", new Dictionary<string, object>
        {
            { "deliveryConfirmedAt", "2026-10-02T10:00:00.0000000Z" },
            { "disputeWindowEndsAt", "2026-10-04T10:00:00.0000000Z" }
        });
        (await VersionAsync(escrowId)).Should().Be("5");

        // A release whose read came before the stamp, still carrying read + 1 as the old code did.
        await EscrowRepo.UpdateStatusAsync(escrowId, CreatedAt, "released", "escrowed",
            new Dictionary<string, object> { { "releasedAt", "2026-10-05T10:00:00.0000000Z" }, { "version", 4 } });
        (await VersionAsync(escrowId)).Should().Be("6", "the version only ever grows");

        await EscrowRepo.UpdateStatusAsync(escrowId, CreatedAt, "completed", "released",
            new Dictionary<string, object> { { "completedAt", "2026-10-05T10:00:01.0000000Z" } });
        var completed = await RowAsync(escrowId);
        completed["status"].S.Should().Be("completed");
        completed["version"].N.Should().Be("7");
    }

    [LocalDynamoDbFact]
    public async Task Extend_Backfill_RefundArm_EachAddOne_ARefusedWriteAddsNothing()
    {
        var escrowId = await SeedEscrowAsync($"escrow-sim29-{Guid.NewGuid():N}", "escrowed", 3);

        (await EscrowRepo.TryExtendExpiryAsync(escrowId, CreatedAt, "2026-10-15T10:00:00.0000000Z")).Should().BeTrue();
        (await VersionAsync(escrowId)).Should().Be("4");

        (await EscrowRepo.TryBackfillTruckerIdAsync(escrowId, CreatedAt, "trucker-sim29")).Should().BeTrue();
        (await VersionAsync(escrowId)).Should().Be("5");
        (await EscrowRepo.TryBackfillTruckerIdAsync(escrowId, CreatedAt, "trucker-other")).Should().BeFalse();
        (await VersionAsync(escrowId)).Should().Be("5");

        await Ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                EscrowRepo.BuildRefundArm(escrowId, CreatedAt, "order_cancelled", "2026-10-03T10:00:00.0000000Z")
            }
        });
        var armed = await RowAsync(escrowId);
        armed["refundReason"].S.Should().Be("order_cancelled");
        armed["version"].N.Should().Be("6");

        // A scheduled refund keeps its expiresAt: the extension is refused and moves nothing.
        (await EscrowRepo.TryExtendExpiryAsync(escrowId, CreatedAt, "2026-10-20T10:00:00.0000000Z")).Should().BeFalse();
        (await VersionAsync(escrowId)).Should().Be("6");
    }

    [LocalDynamoDbFact]
    public async Task HoldSuccess_FromInitiated_IsVersionTwo()
    {
        var escrowId = await SeedEscrowAsync($"escrow-sim29-{Guid.NewGuid():N}", "initiated", 1);

        await EscrowRepo.UpdateStatusAsync(escrowId, CreatedAt, "escrowed", "initiated",
            new Dictionary<string, object> { { "telebirrRef", "TB-SIM29" }, { "telebirrHoldRef", "TB-SIM29" } });

        var held = await RowAsync(escrowId);
        held["status"].S.Should().Be("escrowed");
        held["version"].N.Should().Be("2");
    }
}
