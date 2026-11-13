using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-36 against real DynamoDB (LocalStack in CI), what the mocked tests cannot show: the drop-off
/// check-in's conditional write and the deadline it moves from T8 to T9 without a version bump, the
/// delivery's system confirm and dispute writes and their conditions, and the order cron's T8 and T9 on the
/// real orders, deliveries, escrows and users tables (the freeze before the dispute; the auto-confirm with
/// the escrow stamped as a buyer's confirm stamps it).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DropoffJudgementDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T09:00:00.0000000Z";
    private const string EscrowCreatedAt = "2026-11-10T09:05:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public DropoffJudgementDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private DeliveryRepository Deliveries => new(Ddb, _db.Deliveries);

    private string Id(string name) => $"{name}-esc36-{_run}";

    private string OrderId => Id("order");

    private string DeliveryId => Id("delivery");

    private string TripId => Id("trip");

    /// <summary>An order picked up with the flag on: the stamp written by the order's own advance.</summary>
    private async Task SeedOrderAsync(DateTime t8)
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = OrderId },
                ["createdAt"] = new() { S = CreatedAt },
                ["status"] = new() { S = "accepted" },
                ["version"] = new() { N = "6" },
                ["buyerId"] = new() { S = Id("buyer") },
                ["farmerId"] = new() { S = Id("farmer") },
                ["truckerId"] = new() { S = Id("trucker") },
                ["tripId"] = new() { S = TripId },
                ["listingId"] = new() { S = Id("listing") },
                ["quantity"] = new() { N = "500" },
                ["activeDeliveryId"] = new() { S = DeliveryId },
                [OrderCommitment.FulfillmentField] = new() { S = OrderCommitment.Fulfillment.Trucker },
                [OrderCommitment.FarmerResponseField] = new() { S = OrderCommitment.Response.Accepted },
                [OrderCommitment.TruckerResponseField] = new() { S = OrderCommitment.Response.Accepted }
            }
        });

        var advance = DropoffJudgement.PickupStamp(t8 - OrderTimings.Defaults.DeliveryOverdue,
            new TrackPoint(9.03, 38.74), new TrackPoint(9.01, 38.76), OrderTimings.Defaults);
        advance["status"] = "in_transit";
        (await Repo.UpdateAsync(OrderId, advance, 6)).IsSuccess.Should().BeTrue();
    }

    private async Task SeedDeliveryAsync(bool arrivalPhoto = true)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = new() { S = DeliveryId },
            ["tripId"] = new() { S = TripId },
            ["orderId"] = new() { S = OrderId },
            ["truckerId"] = new() { S = Id("trucker") },
            ["buyerId"] = new() { S = Id("buyer") },
            ["farmerId"] = new() { S = Id("farmer") },
            ["deliveryType"] = new() { S = "trucker" },
            ["status"] = new() { S = "inTransit" },
            ["version"] = new() { N = "3" },
            ["createdAt"] = new() { S = CreatedAt },
            ["pickedUpAt"] = new() { S = CreatedAt }
        };
        if (arrivalPhoto)
        {
            item["arrivalPhotoRef"] = new() { S = $"uploads/photos/{DeliveryId}/arrival-1763000000000.webp" };
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = item });
    }

    private async Task SeedEscrowAsync()
        => await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Escrows,
            Item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = new() { S = $"escrow-order-{OrderId}" },
                ["createdAt"] = new() { S = EscrowCreatedAt },
                ["orderId"] = new() { S = OrderId },
                ["deliveryId"] = new() { S = DeliveryId },
                ["buyerId"] = new() { S = Id("buyer") },
                ["farmerId"] = new() { S = Id("farmer") },
                ["status"] = new() { S = "escrowed" },
                ["amount"] = new() { N = "1000" },
                ["version"] = new() { N = "1" },
                ["source"] = new() { S = "order" }
            }
        });

    private async Task SeedTruckerAsync()
        => await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Users,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new() { S = Id("trucker") },
                ["role"] = new() { S = "trucker" },
                ["phoneNumber"] = new() { S = "+251900100002" },
                ["woreda"] = new() { S = "Adama" },
                ["createdAt"] = new() { S = CreatedAt },
                ["updatedAt"] = new() { S = CreatedAt },
                ["version"] = new() { N = "1" }
            }
        });

    private Dictionary<string, object> Entry(DateTime received, bool late = false) => new()
    {
        { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(received) },
        { PickupJudgement.EntryBy, Id("trucker") },
        { PickupJudgement.EntryDistanceChecked, true },
        { PickupJudgement.EntryLate, late },
        { PickupJudgement.EntryLatitude, 9.031 },
        { PickupJudgement.EntryLongitude, 38.74 },
        { PickupJudgement.EntryDistanceKm, 0.11 }
    };

    private async Task<Dictionary<string, object>> ReadOrderAsync()
        => (await Repo.GetByIdAsync(OrderId, consistentRead: true))!;

    private OrderCronFunction Cron() => new(Repo, new TransactionAuditRepository(Ddb, _db.TransactionAudit), inboxRepo: null,
        escrowService: new EscrowService(new EscrowRepository(Ddb, _db.Escrows), new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            idempotencyRepo: null, telebirrGateway: null, orderRepo: Repo, deliveryRepo: Deliveries),
        cropRepo: new CropRepository(Ddb, _db.Crops), tripRepo: new TripRepository(Ddb, _db.Trips), deliveryRepo: Deliveries)
    {
        CommitmentsEnabled = true,
        DropoffClassificationEnabled = true,
        EscrowRepo = new EscrowRepository(Ddb, _db.Escrows),
        UserRepo = new UserRepository(Ddb, _db.Users),
        PayoutDelay = new PayoutDelayPolicy(null, new UserRepository(Ddb, _db.Users), new EscrowRepository(Ddb, _db.Escrows),
            settings: PayoutDelaySettings.Default)
    };

    private static Task<APIGatewayProxyResponse> RunAsync(OrderCronFunction cron)
        => cron.FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, new TestLambdaContext());

    // ===== the repository writes =====

    [LocalDynamoDbFact]
    public async Task ADropoffCheckIn_IsKeptOnce_MovesTheDeadlineFromT8ToT9_AndNotTheVersion()
    {
        var t8 = DateTime.UtcNow.AddHours(30);
        await SeedOrderAsync(t8);
        var before = await ReadOrderAsync();
        OrderDeadlines.Stored(before)!.Value.Kind.Should().Be(OrderDeadlines.Kind.DeliveryOverdue);
        before[OrderDeadlines.ShardField].Should().Be(OrderDeadlines.Shard, "the judgement is the sweep's");

        var received = DateTime.UtcNow;
        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(received), whileOpen: true)).Should().BeTrue();
        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(received.AddMinutes(1)), whileOpen: true))
            .Should().BeFalse("the first drop-off check-in is kept");

        var after = await ReadOrderAsync();
        after["version"].Should().Be(before["version"]);
        after["updatedAt"].Should().Be(before["updatedAt"]);
        OrderDeadlines.Stored(after).Should().Be(new OrderDeadline(
            DateTime.Parse(OrderCommitment.Iso(received), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
            + OrderTimings.Defaults.SilentBuyerWindow + PickupJudgement.SettleMargin,
            OrderDeadlines.Kind.SilentBuyer));
        PickupJudgement.ViewOf(after)!.Keys.Should().BeEquivalentTo(PickupJudgement.Kind.ArrivedDropoff);
    }

    [LocalDynamoDbFact]
    public async Task OnceJudged_AnOpenCheckInIsRefused_ButTheLateOneIsKept()
    {
        await SeedOrderAsync(DateTime.UtcNow.AddHours(-2));
        (await Repo.TryStampAsync(OrderId,
            new Dictionary<string, object>
            {
                { DropoffJudgement.Field, DropoffJudgement.Outcome.Queued },
                { DropoffJudgement.JudgedAtField, OrderCommitment.Iso(DateTime.UtcNow) }
            },
            new Dictionary<string, string> { { "status", "in_transit" }, { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending } }))
            .Should().BeTrue();
        OrderDeadlines.Stored(await ReadOrderAsync()).Should().BeNull("a judged order leaves the index");

        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(DateTime.UtcNow), whileOpen: true)).Should().BeFalse();
        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(DateTime.UtcNow, late: true), whileOpen: false)).Should().BeTrue();

        var row = await ReadOrderAsync();
        DropoffJudgement.CheckInOf(row)![PickupJudgement.EntryLate].Should().Be(true);
        OrderDeadlines.Stored(row).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task TheSystemsConfirm_NeedsTheStatusAndVersionRead_AndStampsWhoConfirmed()
    {
        await SeedDeliveryAsync();

        (await Deliveries.TryAutoConfirmAsync(DeliveryId, TripId, "inTransit", 2, OrderCommitment.Iso(DateTime.UtcNow)))
            .Should().BeFalse("the version moved");
        (await Deliveries.TryAutoConfirmAsync(DeliveryId, TripId, "pickedUp", 3, OrderCommitment.Iso(DateTime.UtcNow)))
            .Should().BeFalse("the status moved");
        var at = OrderCommitment.Iso(DateTime.UtcNow);
        (await Deliveries.TryAutoConfirmAsync(DeliveryId, TripId, "inTransit", 3, at)).Should().BeTrue();

        var row = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        row["status"].Should().Be("delivered");
        row["confirmedAt"].Should().Be(at);
        row[DropoffJudgement.AutoConfirmedAtField].Should().Be(at);
        row["version"].Should().Be("4");
    }

    [LocalDynamoDbFact]
    public async Task TheSystemsDispute_NeedsTheStatusRead_AndAddsToTheVersion()
    {
        await SeedDeliveryAsync();

        (await Deliveries.TryMarkDropoffDisputedAsync(DeliveryId, TripId, "pickedUp", DropoffJudgement.OverdueReason,
            OrderCommitment.Iso(DateTime.UtcNow))).Should().BeFalse();
        (await Deliveries.TryMarkDropoffDisputedAsync(DeliveryId, TripId, "inTransit", DropoffJudgement.OverdueReason,
            OrderCommitment.Iso(DateTime.UtcNow))).Should().BeTrue();

        var row = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        row["status"].Should().Be("disputed");
        row["disputedBy"].Should().Be(DropoffJudgement.SystemActor);
        row["disputeReason"].Should().Be(DropoffJudgement.OverdueReason);
        row["version"].Should().Be("4");
        DropoffJudgement.SystemDisputeReasonOf(row).Should().Be(DropoffJudgement.OverdueReason);
    }

    // ===== the cron on the real tables =====

    [LocalDynamoDbFact]
    public async Task T8_FreezesTheEscrow_ThenDisputesTheDeliveryAndTheOrder()
    {
        await SeedOrderAsync(DateTime.UtcNow.AddMinutes(-30));
        await SeedDeliveryAsync(arrivalPhoto: false);
        await SeedEscrowAsync();

        var response = await RunAsync(Cron());

        response.Body.Should().Contain("\"dropoffJudged\":\"1\"");
        var escrow = (await new EscrowRepository(Ddb, _db.Escrows).GetByIdAsync($"escrow-order-{OrderId}", consistentRead: true))!;
        escrow["status"].Should().Be("disputed");
        escrow["disputedBy"].Should().Be(DropoffJudgement.SystemActor);
        var delivery = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        delivery["status"].Should().Be("disputed");
        delivery["disputeReason"].Should().Be(DropoffJudgement.OverdueReason);
        var order = await ReadOrderAsync();
        order["status"].Should().Be("disputed");
        order[DropoffJudgement.DisputeReasonField].Should().Be(DropoffJudgement.OverdueReason);
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DeliveryOverdue);
        OrderDeadlines.Stored(order).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task T9_ConfirmsForTheSilentBuyer_AndStampsTheEscrowAsABuyersConfirm()
    {
        await SeedOrderAsync(DateTime.UtcNow.AddHours(20));
        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(DateTime.UtcNow.AddHours(-49)), whileOpen: true)).Should().BeTrue();
        await SeedDeliveryAsync();
        await SeedEscrowAsync();
        await SeedTruckerAsync();

        var response = await RunAsync(Cron());

        response.Body.Should().Contain("\"dropoffJudged\":\"1\"");
        var delivery = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        delivery["status"].Should().Be("delivered");
        delivery.Should().ContainKey(DropoffJudgement.AutoConfirmedAtField);
        var order = await ReadOrderAsync();
        order["status"].Should().Be("delivered");
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.AutoConfirmed);
        order.Should().NotContainKey(DropoffJudgement.DisputeReasonField);
        var escrow = (await new EscrowRepository(Ddb, _db.Escrows).GetByIdAsync($"escrow-order-{OrderId}", consistentRead: true))!;
        escrow["status"].Should().Be("escrowed", "the release waits for the dispute window and the escrow cron");
        escrow["deliveryConfirmedAt"].Should().Be(delivery["confirmedAt"]);
        escrow.Should().ContainKey("disputeWindowEndsAt").And.ContainKey(PayoutDelayPolicy.ReleaseNotBeforeField);
    }
}
