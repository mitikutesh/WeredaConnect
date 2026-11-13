using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
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

    /// <summary>
    /// An order picked up with the flag on: the stamp written by the order's own advance. Without
    /// <paramref name="tripPoint"/> the buyer's own pin is the only point (the trip was read, it had none).
    /// </summary>
    private async Task SeedOrderAsync(DateTime t8, bool tripPoint = true)
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

        var pickedUpAt = t8 - OrderTimings.Defaults.DeliveryOverdue;
        var advance = DropoffJudgement.PickupStamp(pickedUpAt, new TrackPoint(9.03, 38.74),
            tripPoint ? new TrackPoint(9.01, 38.76) : null, tripRead: true, OrderTimings.Defaults, nowUtc: pickedUpAt);
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
    public async Task FUP204_TheBuyersPinTheOnlyPoint_ATruckerWhoCameOutOfRange_GetsTheOutOfRangeDispute_NotTheOverdueOne()
    {
        // The trip has no destination, so the buyer's own pin is the only point; the trucker's photo-backed
        // check-in more than 5 km from it is kept as an attempt, never as a check-in.
        await SeedOrderAsync(DateTime.UtcNow.AddMinutes(-30), tripPoint: false);
        var before = await ReadOrderAsync();
        var attempt = Entry(DateTime.UtcNow.AddHours(-5));
        attempt[PickupJudgement.EntryDistanceChecked] = false;
        attempt[PickupJudgement.EntryOutOfRange] = true;
        (await Repo.TryRecordDropoffAttemptAsync(OrderId, attempt)).Should().BeTrue();
        (await Repo.TryRecordDropoffAttemptAsync(OrderId, attempt)).Should().BeFalse("the first attempt is kept");
        var withAttempt = await ReadOrderAsync();
        withAttempt["version"].Should().Be(before["version"]);
        OrderDeadlines.Stored(withAttempt).Should().Be(OrderDeadlines.Stored(before), "an attempt is no check-in");
        await SeedDeliveryAsync();
        await SeedEscrowAsync();

        var response = await RunAsync(Cron());

        response.Body.Should().Contain("\"dropoffJudged\":\"1\"");
        // The system's dispute with the reason R12 does not read: the money frozen, an admin decides the verdict.
        var order = await ReadOrderAsync();
        order["status"].Should().Be("disputed");
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DropoffOutOfRange);
        order[DropoffJudgement.DisputeReasonField].Should().Be(DropoffJudgement.OutOfRangeReason, "not delivery_overdue: no R12");
        order.Should().NotContainKey(AdminQueueFields.Reason);
        OrderDeadlines.Stored(order).Should().BeNull();
        (await new EscrowRepository(Ddb, _db.Escrows).GetByIdAsync($"escrow-order-{OrderId}", consistentRead: true))!["status"]
            .Should().Be("disputed");
        var delivery = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        delivery["status"].Should().Be("disputed");
        delivery["disputeReason"].Should().Be(DropoffJudgement.OutOfRangeReason);
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

    // ===== FUP-373: a check-in made offline before T8 and synced after the T8 run =====

    private const string Bucket = "test-uploads";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string ArrivalKey => $"uploads/photos/{DeliveryId}/arrival-1763000000000.webp";

    /// <summary>
    /// The sync Lambda on the real orders, deliveries and trips tables with <c>dropoffClassification</c> on; the
    /// uploads bucket's HEAD answers for the arrival photo (S3 is not what this test is about).
    /// </summary>
    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var s3 = Substitute.For<IAmazonS3>();
        var head = new GetObjectMetadataResponse { ContentLength = 120 * 1024, VersionId = "v-late" };
        head.Headers.ContentType = DeliveryPhotoKeys.ContentTypeOf(ArrivalKey);
        s3.GetObjectMetadataAsync(Bucket, ArrivalKey, Arg.Any<CancellationToken>()).Returns(head);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            fraudService: Substitute.For<IFraudDetectionService>(), orderRepo: Repo)
        {
            UploadsBucket = Bucket,
            DeliveryPhotoS3 = s3,
            CommitmentsEnabled = true,
            DropoffClassificationEnabled = true
        };
    }

    /// <summary>The phone's queue as one batch from the trucker, in order; the results in the same order.</summary>
    private async Task<List<JsonElement>> SyncAsTruckerAsync(params (string EntityType, string EntityId, Dictionary<string, object> Data)[] operations)
    {
        var trucker = Id("trucker");
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = $"batch-fup373-{_run}",
                ClientId = "device-fup373",
                Operations = operations.Select((op, i) => new SyncOperationRequest
                {
                    OperationId = $"op-fup373-{i}",
                    IdempotencyKey = $"{trucker}:{op.EntityType}:{op.EntityId}:create:{Guid.NewGuid():N}",
                    EntityType = op.EntityType,
                    EntityId = op.EntityId,
                    Action = "create",
                    Data = op.Data
                }).ToList()
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", trucker }, { "custom:role", "trucker" } }
            }
        };

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    private (string, string, Dictionary<string, object>) ArrivalPhotoOp()
        => (DeliveryPhotoKeys.EntityType, $"{DeliveryId}#{DeliveryPhotoKeys.Arrival}",
            new Dictionary<string, object> { { DeliveryPhotoKeys.PhotoRefField, ArrivalKey } });

    /// <summary>
    /// The trucker's "Arrived at drop-off", tapped an hour before T8: 0.1 km from the buyer's pin, or (<paramref name="latitude"/>
    /// 9.2) about 19 km from both points.
    /// </summary>
    private (string, string, Dictionary<string, object>) DropoffCheckInOp(DateTime t8, double latitude = 9.031)
        => (PickupJudgement.EntityType, $"{OrderId}#{PickupJudgement.Kind.ArrivedDropoff}", new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "kind", PickupJudgement.Kind.ArrivedDropoff }, { "capturedAt", OrderCommitment.Iso(t8.AddHours(-1)) },
            { "latitude", latitude }, { "longitude", 38.74 }, { "accuracyM", 20 }
        });

    /// <summary>T8 ran with no check-in on the server: the delivery and the order are the system's overdue dispute.</summary>
    private async Task<DateTime> JudgedOverdueAsync()
    {
        var t8 = DateTime.UtcNow.AddMinutes(-30);
        await SeedOrderAsync(t8);
        await SeedDeliveryAsync(arrivalPhoto: false);
        await SeedEscrowAsync();
        (await RunAsync(Cron())).Body.Should().Contain("\"dropoffJudged\":\"1\"");
        (await ReadOrderAsync())[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DeliveryOverdue);
        DropoffJudgement.SystemDisputeReasonOf((await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!)
            .Should().Be(DropoffJudgement.OverdueReason);
        return t8;
    }

    [LocalDynamoDbFact]
    public async Task FUP373_ALateOfflineCheckIn_SyncedAfterTheT8Run_KeepsItsPhotoAndItsLateEntry_AndMovesNothing()
    {
        var t8 = await JudgedOverdueAsync();
        var orderBefore = await ReadOrderAsync();
        var deliveryBefore = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;

        var results = await SyncAsTruckerAsync(ArrivalPhotoOp(), DropoffCheckInOp(t8));

        results[0].GetProperty("status").GetString().Should().Be("success", results[0].ToString());
        results[1].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.TooLateCode}: ");

        // The photo is evidence on the dispute: written with its S3 version and marked late with the server's
        // time (the admin tells it from one taken on the road), the status and version untouched.
        var delivery = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        delivery["arrivalPhotoRef"].Should().Be(ArrivalKey);
        delivery["arrivalPhotoVersionId"].Should().Be("v-late");
        OrderCommitment.TryParseUtc((string)delivery[DropoffJudgement.ArrivalPhotoLateAtField], out var lateAt).Should().BeTrue();
        lateAt.Should().BeAfter(t8);
        DeliveryFunction.MapToDeliveryDto(delivery).ArrivalPhotoLateAt.Should().Be((string)delivery[DropoffJudgement.ArrivalPhotoLateAtField]);
        delivery["status"].Should().Be("disputed");
        delivery["version"].Should().Be(deliveryBefore["version"]);
        delivery["updatedAt"].Should().Be(deliveryBefore["updatedAt"]);

        // The check-in is kept late, never counted: the judgement and the order stand as they were.
        var order = await ReadOrderAsync();
        var kept = DropoffJudgement.CheckInOf(order)!;
        kept[PickupJudgement.EntryLate].Should().Be(true);
        kept[PickupJudgement.EntryDistanceChecked].Should().Be(true);
        DropoffJudgement.CountedCheckIn(order).Should().BeNull();
        DropoffJudgement.ViewOf(order)!.Late.Should().BeTrue();
        order["status"].Should().Be("disputed");
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DeliveryOverdue);
        order["version"].Should().Be(orderBefore["version"]);
        OrderDeadlines.Stored(order).Should().BeNull();

        // The same batch again (the phone did not hear back) gets the same answers and writes nothing new.
        var again = await SyncAsTruckerAsync(ArrivalPhotoOp(), DropoffCheckInOp(t8));
        again[0].GetProperty("status").GetString().Should().Be("success");
        again[1].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        DropoffJudgement.CheckInOf(await ReadOrderAsync())![PickupJudgement.EntryReceivedAt]
            .Should().Be(kept[PickupJudgement.EntryReceivedAt]);
    }

    [LocalDynamoDbFact]
    public async Task FUP373_ALateCheckIn_WhosePhotoNeverReachedTheServer_IsStillKeptLate()
    {
        var t8 = await JudgedOverdueAsync();

        var results = await SyncAsTruckerAsync(DropoffCheckInOp(t8));

        results[0].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        var order = await ReadOrderAsync();
        DropoffJudgement.CheckInOf(order)![PickupJudgement.EntryLate].Should().Be(true);
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DeliveryOverdue);
        (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!.Should().NotContainKey("arrivalPhotoRef");
    }

    [LocalDynamoDbFact]
    public async Task FUP373_ALateFarCheckIn_BeforeTheT8Run_IsKeptAsALateAttempt_AndT8StaysOverdue()
    {
        // T8 passed, the cron has not run yet: the far check-in (the buyer's pin may be wrong) is kept as a late
        // attempt for the admin, but received after T8 it cannot turn the overdue dispute into the out-of-range one.
        var t8 = DateTime.UtcNow.AddMinutes(-30);
        await SeedOrderAsync(t8);
        await SeedDeliveryAsync(arrivalPhoto: false);
        await SeedEscrowAsync();

        var results = await SyncAsTruckerAsync(DropoffCheckInOp(t8, latitude: 9.2));

        results[0].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        var attempt = DropoffJudgement.AttemptOf(await ReadOrderAsync())!;
        attempt[PickupJudgement.EntryLate].Should().Be(true);
        attempt[PickupJudgement.EntryOutOfRange].Should().Be(true);
        DropoffJudgement.CheckInOf(await ReadOrderAsync()).Should().BeNull();

        (await RunAsync(Cron())).Body.Should().Contain("\"dropoffJudged\":\"1\"");

        var order = await ReadOrderAsync();
        order[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.DeliveryOverdue, "a late attempt never counts");
        order[DropoffJudgement.DisputeReasonField].Should().Be(DropoffJudgement.OverdueReason);
    }

    [LocalDynamoDbFact]
    public async Task FUP373_OnceAnAdminDecided_TheLatePhotoIsRefused_AndALateCheckInIsNotOpen()
    {
        var t8 = await JudgedOverdueAsync();
        (await Deliveries.TryRecordDisputeResolutionAsync(DeliveryId, "farmer_wins", OrderCommitment.Iso(DateTime.UtcNow)))
            .Should().BeTrue();
        var deliveryBefore = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;

        var results = await SyncAsTruckerAsync(ArrivalPhotoOp(), DropoffCheckInOp(t8), DropoffCheckInOp(t8, latitude: 9.2));

        results[0].GetProperty("message").GetString().Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        results[1].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        results[2].GetProperty("message").GetString().Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        var delivery = (await Deliveries.GetByIdAsync(DeliveryId, consistentRead: true))!;
        delivery.Should().NotContainKey("arrivalPhotoRef").And.NotContainKey(DropoffJudgement.ArrivalPhotoLateAtField);
        delivery["version"].Should().Be(deliveryBefore["version"]);
        var order = await ReadOrderAsync();
        DropoffJudgement.CheckInOf(order).Should().BeNull();
        DropoffJudgement.AttemptOf(order).Should().BeNull();
    }
}
