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
/// ESC-34 against real DynamoDB (LocalStack in CI), what the mocked tests cannot show: the check-in's
/// document-path write into the order's <c>checkIns</c> map and its conditions (the first check-in kept,
/// nothing once judged, the late write, a row without the map), that none of it moves the version or the
/// deadline index, the flag-off clear of the stamp, and a trucker no-show judged by the order cron on the
/// real orders, deliveries and trips tables.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PickupJudgementDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T09:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public PickupJudgementDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private string Id(string name) => $"{name}-esc34-{_run}";

    /// <summary>An order accepted with the flag on: the stamp written by the accept's own write.</summary>
    private async Task<string> SeedJudgeableAsync(DateTime deadline, bool withMap = true, string? deliveryId = null, string? tripId = null)
    {
        var orderId = Id("order");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = orderId },
                ["createdAt"] = new() { S = CreatedAt },
                ["status"] = new() { S = "funded" },
                ["version"] = new() { N = "3" },
                ["buyerId"] = new() { S = Id("buyer") },
                ["farmerId"] = new() { S = Id("farmer") },
                ["truckerId"] = new() { S = Id("trucker") },
                ["tripId"] = new() { S = tripId ?? Id("trip") },
                ["listingId"] = new() { S = Id("listing") },
                ["quantity"] = new() { N = "500" },
                [OrderCommitment.FulfillmentField] = new() { S = OrderCommitment.Fulfillment.Trucker },
                [OrderCommitment.FarmerResponseField] = new() { S = OrderCommitment.Response.Accepted },
                [OrderCommitment.TruckerResponseField] = new() { S = OrderCommitment.Response.Pending }
            }
        });

        var accept = new Dictionary<string, object>
        {
            { "status", "accepted" },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Accepted },
            { PickupDeadline.Field, PickupDeadline.Format(deadline) },
            { PickupJudgement.Field, PickupJudgement.Outcome.Pending },
            { PickupJudgement.PickupLatitudeField, 8.7523 },
            { PickupJudgement.PickupLongitudeField, 38.9785 }
        };
        if (withMap)
        {
            accept[PickupJudgement.CheckInsField] = new Dictionary<string, object>();
        }

        if (deliveryId != null)
        {
            accept["activeDeliveryId"] = deliveryId;
        }

        (await Repo.UpdateAsync(orderId, accept, 3)).IsSuccess.Should().BeTrue();
        return orderId;
    }

    private static Dictionary<string, object> Entry(string by, bool late = false) => new()
    {
        { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(DateTime.UtcNow) },
        { PickupJudgement.EntryBy, by },
        { PickupJudgement.EntryDistanceChecked, true },
        { PickupJudgement.EntryLate, late },
        { PickupJudgement.EntryLatitude, 8.76 },
        { PickupJudgement.EntryLongitude, 38.9785 }
    };

    private async Task<Dictionary<string, object>> ReadAsync(string orderId)
        => (await Repo.GetByIdAsync(orderId, consistentRead: true))!;

    [LocalDynamoDbFact]
    public async Task ACheckIn_GoesIntoTheMap_TheFirstIsKept_AndNothingElseMoves()
    {
        var orderId = await SeedJudgeableAsync(DateTime.UtcNow.AddHours(30));
        var before = await ReadAsync(orderId);
        OrderDeadlines.Stored(before)!.Value.Kind.Should().Be(OrderDeadlines.Kind.PickupJudgement);

        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.ArrivedPickup, Entry(Id("trucker")), whileOpen: true))
            .Should().BeTrue();
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.ArrivedPickup, Entry("someone-else"), whileOpen: true))
            .Should().BeFalse("the first check-in of a kind is kept");
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.CropsReady, Entry(Id("farmer")), whileOpen: true))
            .Should().BeTrue();

        var after = await ReadAsync(orderId);
        after["version"].Should().Be(before["version"]);
        after["updatedAt"].Should().Be(before["updatedAt"]);
        OrderDeadlines.Stored(after).Should().Be(OrderDeadlines.Stored(before));
        var arrived = PickupJudgement.EntryOf(after, PickupJudgement.Kind.ArrivedPickup)!;
        arrived[PickupJudgement.EntryBy].Should().Be(Id("trucker"));
        arrived[PickupJudgement.EntryDistanceChecked].Should().Be(true);
        PickupJudgement.ViewOf(after)!.Keys.Should().BeEquivalentTo(PickupJudgement.Kind.ArrivedPickup, PickupJudgement.Kind.CropsReady);
    }

    [LocalDynamoDbFact]
    public async Task OnceJudged_AnOpenCheckInIsRefused_ButTheLateOneIsKept()
    {
        var orderId = await SeedJudgeableAsync(DateTime.UtcNow.AddHours(-1));
        (await Repo.TryStampAsync(orderId,
            new Dictionary<string, object>
            {
                { PickupJudgement.Field, PickupJudgement.Outcome.BothPresentNoScan },
                { PickupJudgement.JudgedAtField, OrderCommitment.Iso(DateTime.UtcNow) },
                { OrderCommitment.PickupOverdueNotifiedAtField, OrderCommitment.Iso(DateTime.UtcNow) }
            },
            new Dictionary<string, string> { { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending } }))
            .Should().BeTrue();
        OrderDeadlines.Stored(await ReadAsync(orderId)).Should().BeNull("a judged order has nothing due");

        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.CropsReady, Entry(Id("farmer")), whileOpen: true))
            .Should().BeFalse();
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.CropsReady, Entry(Id("farmer"), late: true), whileOpen: false))
            .Should().BeTrue();

        var entry = PickupJudgement.EntryOf(await ReadAsync(orderId), PickupJudgement.Kind.CropsReady)!;
        entry[PickupJudgement.EntryLate].Should().Be(true);
    }

    [LocalDynamoDbFact]
    public async Task AnOrderWithoutTheMap_GetsTheMapWithTheFirstEntry()
    {
        var orderId = await SeedJudgeableAsync(DateTime.UtcNow.AddHours(30), withMap: false);

        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.ArrivedPickup, Entry(Id("trucker")), whileOpen: true))
            .Should().BeTrue();
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.CropsReady, Entry(Id("farmer")), whileOpen: true))
            .Should().BeTrue();

        PickupJudgement.ViewOf(await ReadAsync(orderId))!.Should().HaveCount(2);
    }

    [LocalDynamoDbFact]
    public async Task TheFlagOffClear_LeavesNoStampToRead()
    {
        var orderId = await SeedJudgeableAsync(DateTime.UtcNow.AddHours(-1));

        (await Repo.TryStampAsync(orderId,
            new Dictionary<string, object>
            {
                { OrderCommitment.PickupOverdueNotifiedAtField, OrderCommitment.Iso(DateTime.UtcNow) },
                { PickupJudgement.Field, null! }
            },
            new Dictionary<string, string> { { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending } }))
            .Should().BeTrue();

        var row = await ReadAsync(orderId);
        PickupJudgement.IsStamped(row).Should().BeFalse();
        OrderDeadlines.Stored(row).Should().BeNull();
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.ArrivedPickup, Entry(Id("trucker")), whileOpen: true))
            .Should().BeFalse("no check-in opens on a cleared order");
    }

    [LocalDynamoDbFact]
    public async Task TheCron_JudgesATruckerNoShow_OnTheRealTables()
    {
        var deliveryId = Id("delivery");
        var tripId = Id("trip");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = new() { S = deliveryId },
                ["tripId"] = new() { S = tripId },
                ["orderId"] = new() { S = Id("order") },
                ["status"] = new() { S = "pending" },
                ["version"] = new() { N = "1" },
                ["createdAt"] = new() { S = CreatedAt }
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = new() { S = tripId },
                ["createdAt"] = new() { S = CreatedAt },
                ["truckerId"] = new() { S = Id("trucker") },
                ["status"] = new() { S = "matched" },
                ["version"] = new() { N = "2" }
            }
        });
        var orderId = await SeedJudgeableAsync(DateTime.UtcNow.AddMinutes(-30), deliveryId: deliveryId, tripId: tripId);
        // The farmer said the crops were ready; the trucker never checked in.
        (await Repo.TryRecordCheckInAsync(orderId, CreatedAt, PickupJudgement.Kind.CropsReady,
            Entry(Id("farmer")), whileOpen: true)).Should().BeTrue();

        var deliveries = new DeliveryRepository(Ddb, _db.Deliveries);
        var cron = new OrderCronFunction(Repo, new TransactionAuditRepository(Ddb, _db.TransactionAudit), inboxRepo: null,
            escrowService: new EscrowService(new EscrowRepository(Ddb, _db.Escrows), new EscrowAuditRepository(Ddb, _db.EscrowAudit),
                idempotencyRepo: null, telebirrGateway: null, orderRepo: Repo, deliveryRepo: deliveries),
            cropRepo: new CropRepository(Ddb, _db.Crops), tripRepo: new TripRepository(Ddb, _db.Trips), deliveryRepo: deliveries)
        {
            CommitmentsEnabled = true,
            PickupClassificationEnabled = true
        };

        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, new TestLambdaContext());

        response.Body.Should().Contain("\"pickupJudged\":\"1\"");
        var judged = await ReadAsync(orderId);
        judged["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        judged[OrderCommitment.TransportFailureField].Should().Be(PickupJudgement.TruckerNoShowFailure);
        judged[OrderCommitment.TruckerResponseField].Should().Be(PickupJudgement.NoShowResponse);
        judged[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        judged.Should().ContainKey(PickupJudgement.JudgedAtField);
        judged["version"].Should().Be("5");
        OrderDeadlines.Stored(judged)!.Value.Kind.Should().Be(OrderDeadlines.Kind.BuyerChoiceReminder);
        (await deliveries.GetByIdAsync(deliveryId, consistentRead: true))!["status"].Should().Be("cancelled");
    }
}
