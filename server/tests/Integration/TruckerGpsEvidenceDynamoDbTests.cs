using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-203 against real DynamoDB (LocalStack in CI), the requests the mocked tests cannot validate: the window read
/// of the trip-locations table (the BETWEEN key condition on the real sort keys, both ends, strongly consistent),
/// and a buyer's confirm through <see cref="SyncFunction"/> with the real trips, deliveries, trip-locations and
/// fraud-signals tables: a buyer who sends a fabricated drop-off gets their own row and the trucker none, and the
/// trucker's own check-in at the farm writes the trucker's row through the attribution guard (the guard's Query,
/// its slot rows and the put with <c>sourceUserId</c>).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TruckerGpsEvidenceDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TrackPoint Jimma = new(7.67, 36.83);
    private static readonly TrackPoint Addis = new(9.03, 38.74);

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public TruckerGpsEvidenceDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private TripLocationRepository Locations => new(Ddb, _db.TripLocations);

    private string Id(string name) => $"{name}-fup203-{_run}";

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(double value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>The sync Lambda; FUP-245: <paramref name="dropoffOn"/> sets <c>dropoffClassification</c> and the commitments it needs.</summary>
    private SyncFunction Sync(bool dropoffOn = false)
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var fraud = new FraudDetectionService(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _ => { }
        };
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            fraudService: fraud, orderRepo: Orders, tripLocationRepo: Locations)
        {
            GpsAnomalyOnTrucker = true,
            CommitmentsEnabled = dropoffOn,
            DropoffClassificationEnabled = dropoffOn
        };
    }

    private OrderRepository Orders => new(Ddb, _db.Orders);

    private sealed record Trade(string TripId, string DeliveryId, string BuyerId, string TruckerId, string? OrderId = null);

    /// <summary>
    /// An inter-woreda trip to <paramref name="destination"/> and its delivery, picked up four hours ago at <paramref name="pickup"/>.
    /// FUP-245: with <paramref name="withOrder"/>, the delivery's order too, stamped at pickup as ESC-36 stamps it
    /// (the order's advance to <c>in_transit</c>, the destination as the trip's point), with no drop-off check-in yet.
    /// </summary>
    private async Task<Trade> SeedTradeAsync(TrackPoint pickup, TrackPoint destination, bool withOrder = false)
    {
        var trade = new Trade(Id($"trip-{Guid.NewGuid():N}"), Id($"delivery-{Guid.NewGuid():N}"),
            Id($"buyer-{Guid.NewGuid():N}"), Id($"trucker-{Guid.NewGuid():N}"), withOrder ? Id($"order-{Guid.NewGuid():N}") : null);
        var pickedUpAt = DateTime.UtcNow.AddHours(-4);
        if (trade.OrderId != null)
        {
            await SeedStampedOrderAsync(trade, destination, pickedUpAt);
        }

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new Dictionary<string, AttributeValue>
            {
                ["tripId"] = S(trade.TripId),
                ["createdAt"] = S(CreatedAt),
                ["truckerId"] = S(trade.TruckerId),
                ["status"] = S("inTransit"),
                ["fromWoreda"] = S("Jimma"),
                ["toWoreda"] = S("Addis Ketema"),
                ["destinationLatitude"] = N(destination.Latitude),
                ["destinationLongitude"] = N(destination.Longitude),
                ["version"] = new AttributeValue { N = "1" }
            }
        });
        var delivery = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(trade.DeliveryId),
            ["tripId"] = S(trade.TripId),
            ["listingId"] = S(Id("listing")),
            ["buyerId"] = S(trade.BuyerId),
            ["farmerId"] = S(Id("farmer")),
            ["truckerId"] = S(trade.TruckerId),
            ["participantId"] = S(trade.BuyerId),
            ["qrToken"] = S("qr-token-it"),
            ["qrNonce"] = S("nonce-it"),
            ["pickupPhotoVersionId"] = S("photo-version-pickup-1"),
            ["dropoffPhotoVersionId"] = S("photo-version-dropoff-1"),
            ["status"] = S("pickedUp"),
            ["pickedUpAt"] = S(Iso(pickedUpAt)),
            ["gpsPickup"] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue> { ["latitude"] = N(pickup.Latitude), ["longitude"] = N(pickup.Longitude) }
            },
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(CreatedAt),
            ["version"] = new AttributeValue { N = "1" }
        };
        if (trade.OrderId != null)
        {
            delivery["orderId"] = S(trade.OrderId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = delivery });
        return trade;
    }

    /// <summary>FUP-245: the order accepted, then advanced to <c>in_transit</c> with ESC-36's stamp in the same write.</summary>
    private async Task SeedStampedOrderAsync(Trade trade, TrackPoint destination, DateTime pickedUpAt)
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(trade.OrderId!),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("accepted"),
                ["version"] = new AttributeValue { N = "6" },
                ["buyerId"] = S(trade.BuyerId),
                ["farmerId"] = S(Id("farmer")),
                ["truckerId"] = S(trade.TruckerId),
                ["tripId"] = S(trade.TripId),
                ["listingId"] = S(Id("listing")),
                ["quantity"] = new AttributeValue { N = "500" },
                ["activeDeliveryId"] = S(trade.DeliveryId),
                [OrderCommitment.FulfillmentField] = S(OrderCommitment.Fulfillment.Trucker)
            }
        });
        var advance = DropoffJudgement.PickupStamp(
            pickedUpAt, orderPoint: null, destination, tripRead: true, OrderTimings.Defaults, DateTime.UtcNow);
        advance["status"] = "in_transit";
        (await Orders.UpdateAsync(trade.OrderId!, advance, 6)).IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// FUP-245: the trucker's "Arrived at drop-off" as the check-in handler keeps it (the conditional write of
    /// ESC-36), received <paramref name="ago"/> before now: <paramref name="distanceKm"/> within 5 km is the
    /// drop-off check-in, beyond it the out-of-range attempt.
    /// </summary>
    private async Task DropoffCheckInAsync(Trade trade, TimeSpan ago, TrackPoint at, double distanceKm)
    {
        var receivedAt = DateTime.UtcNow - ago;
        var entry = PickupJudgement.Entry(
            new ParsedCheckIn(trade.OrderId!, PickupJudgement.Kind.ArrivedDropoff, receivedAt, at.Latitude, at.Longitude, 20),
            trade.TruckerId, receivedAt, distanceKm, late: false);
        var kept = distanceKm > PickupJudgement.RadiusKm
            ? await Orders.TryRecordDropoffAttemptAsync(trade.OrderId!, entry)
            : await Orders.TryRecordDropoffCheckInAsync(trade.OrderId!, entry, whileOpen: true);
        kept.Should().BeTrue();
    }

    /// <summary>A check-in as the ingest stores it (LT-05), captured <paramref name="ago"/> before now.</summary>
    private Task CheckInAsync(string tripId, string truckerId, TimeSpan ago, TrackPoint at, string source = "hourly")
    {
        var captured = DateTime.UtcNow - ago;
        return Locations.TryPutAsync(new TripLocationRow(tripId, $"{LocationPingRules.FormatUtc(captured)}#fix", truckerId,
            LocationPingRules.RoundCoordinate(at.Latitude), LocationPingRules.RoundCoordinate(at.Longitude), 35, source, false,
            null, LocationPingRules.FormatUtc(captured.AddSeconds(3)), LocationPingRules.ExpiresAt(captured)));
    }

    private async Task<JsonElement> ConfirmAsync(Trade trade, TrackPoint gpsDropoff, bool dropoffOn = false)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = Guid.NewGuid().ToString(),
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = Guid.NewGuid().ToString(),
                        IdempotencyKey = $"{trade.BuyerId}:delivery:{trade.DeliveryId}:update:{Guid.NewGuid():N}",
                        EntityType = "delivery",
                        EntityId = trade.DeliveryId,
                        Action = "update",
                        Data = new Dictionary<string, object>
                        {
                            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-it" },
                            {
                                "gpsDropoff", new Dictionary<string, object>
                                {
                                    { "latitude", gpsDropoff.Latitude }, { "longitude", gpsDropoff.Longitude }
                                }
                            }
                        }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", trade.BuyerId }, { "role", "buyer" } }
            }
        };

        var response = await Sync(dropoffOn).FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private async Task<List<Dictionary<string, AttributeValue>>> PartitionOf(string userId)
        => (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = S(userId) },
            ConsistentRead = true
        })).Items;

    [LocalDynamoDbFact]
    public async Task ListBetween_ReadsTheDeliverysWindowOnly_BothEndsIncluded_InCaptureOrder()
    {
        var tripId = Id($"trip-{Guid.NewGuid():N}");
        var pickedUpAt = new DateTime(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc);
        var confirmedAt = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        Task Put(string trip, DateTime captured, string kind = "fix") => Locations.TryPutAsync(new TripLocationRow(
            trip, $"{LocationPingRules.FormatUtc(captured)}#{kind}", "trucker-it",
            kind == "fix" ? 8.1 : null, kind == "fix" ? 38.8 : null, kind == "fix" ? 35 : null, kind == "fix" ? "hourly" : null,
            kind == "fix" ? false : null, null, LocationPingRules.FormatUtc(captured), LocationPingRules.ExpiresAt(captured)));
        await Put(tripId, pickedUpAt.AddMilliseconds(-1)); // just before the pickup
        await Put(tripId, pickedUpAt); // at the pickup
        await Put(tripId, pickedUpAt.AddHours(2), "paused");
        await Put(tripId, confirmedAt.AddMinutes(-10));
        await Put(tripId, confirmedAt + TruckerGpsEvidence.ClockSkew); // the last millisecond of the window
        await Put(tripId, confirmedAt + TruckerGpsEvidence.ClockSkew + TimeSpan.FromMilliseconds(1)); // just after it
        await Put(Id("another-trip"), confirmedAt.AddMinutes(-10)); // another trip's partition

        var (from, to) = TruckerGpsEvidence.WindowKeys(pickedUpAt, confirmedAt);
        var window = await Locations.ListBetweenAsync(tripId, from, to, TruckerGpsEvidence.MaxWindowPages);

        window.Complete.Should().BeTrue();
        window.Rows.Select(r => r.SortKey).Should().Equal(
            "2026-11-12T05:00:00.000Z#fix",
            "2026-11-12T07:00:00.000Z#paused",
            "2026-11-12T08:50:00.000Z#fix",
            "2026-11-12T09:02:00.000Z#fix");
        window.Rows.Should().OnlyContain(r => r.TripId == tripId && r.TruckerId == "trucker-it");
        window.Rows[0].Latitude.Should().Be(8.1);
        window.Rows[0].Source.Should().Be("hourly");
    }

    [LocalDynamoDbFact]
    public async Task Confirm_ABuyerSendingAFabricatedDropoff_WritesTheBuyersOwnRow_AndNoneOnTheTrucker()
    {
        // The destination is 3.3 km from the farm; the buyer's phone sends the pickup point itself as the drop-off
        // (accepted, within 5 km of the destination; Rule 4 flags it). The trucker's own check-ins show the truck at
        // the destination.
        var farm = new TrackPoint(8.0000, 38.0000);
        var destination = new TrackPoint(8.0300, 38.0000);
        var trade = await SeedTradeAsync(farm, destination);
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(40), new TrackPoint(8.0200, 38.0000), "near_stop");
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(5), destination, "arrived");

        var result = await ConfirmAsync(trade, gpsDropoff: farm);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var buyerRows = await PartitionOf(trade.BuyerId);
        buyerRows.Should().ContainSingle(r => r["signalType"].S == "GPS_ANOMALY" && r["entityId"].S == trade.DeliveryId);
        (await PartitionOf(trade.TruckerId)).Should().BeEmpty("the trucker's own check-in is 3.3 km from the pickup: no row, no slot");
    }

    [LocalDynamoDbFact]
    public async Task Confirm_TheTruckersOwnCheckInAtTheFarm_WritesTheTruckersRow_ThroughTheGuard()
    {
        // An honest drop-off from the buyer's phone in Addis Ababa; the trucker's own phone was at the farm near
        // Jimma, 259 km away, ten minutes before the confirmation.
        var trade = await SeedTradeAsync(Jimma, Addis);
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(70), new TrackPoint(7.6702, 36.8301));
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(10), new TrackPoint(7.6705, 36.8302));

        var result = await ConfirmAsync(trade, gpsDropoff: Addis);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await PartitionOf(trade.BuyerId)).Should().NotContain(r => r.ContainsKey("signalType") && r["signalType"].S == "GPS_ANOMALY");
        var truckerRows = await PartitionOf(trade.TruckerId);
        var signal = truckerRows.Should().ContainSingle(r => r["recordType"].S == "signal").Subject;
        signal["signalType"].S.Should().Be("GPS_ANOMALY");
        signal["entityId"].S.Should().Be(trade.DeliveryId);
        signal["entityType"].S.Should().Be("delivery");
        signal[FraudDetectionService.SourceUserIdAttribute].S.Should().Be(trade.BuyerId);
        signal["description"].S.Should().StartWith("Trucker check-in 10 min before the confirmation: 0.06 km from the pickup point");
        signal["weight"].N.Should().Be("25");
        truckerRows.Where(r => r["recordType"].S == FraudDetectionService.RecordTypeAttributionSlot)
            .Select(r => r["signalKey"].S).OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
                $"{FraudDetectionService.AttributionSlotPrefix}GPS_ANOMALY#slot#1",
                $"{FraudDetectionService.AttributionSlotPrefix}GPS_ANOMALY#source#{trade.BuyerId}");
    }

    // ── FUP-245: the trucker's drop-off check-in (ESC-36) first ──────

    /// <summary>The FUP-203 anomaly on the road: both of the trucker's check-ins at the farm, the last ten minutes ago.</summary>
    private async Task EveryRoadCheckInAtTheFarmAsync(Trade trade)
    {
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(70), new TrackPoint(7.6702, 36.8301));
        await CheckInAsync(trade.TripId, trade.TruckerId, TimeSpan.FromMinutes(10), new TrackPoint(7.6705, 36.8302));
    }

    [LocalDynamoDbFact]
    public async Task Confirm_FUP245_TheTruckersDropoffCheckIn_IsReadFirst_AndClears_WhateverTheRoadCheckInsSay()
    {
        // The drop-off check-in kept on the real orders table (a nested map) is read back strongly consistent at the
        // confirmation and decides: no row, although every road check-in is at the farm.
        var trade = await SeedTradeAsync(Jimma, Addis, withOrder: true);
        await EveryRoadCheckInAtTheFarmAsync(trade);
        await DropoffCheckInAsync(trade, TimeSpan.FromMinutes(30), Addis, distanceKm: 0.3);

        var result = await ConfirmAsync(trade, gpsDropoff: Addis, dropoffOn: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await PartitionOf(trade.TruckerId)).Should().BeEmpty("the trucker's own drop-off check-in clears: no row, no slot");
    }

    [LocalDynamoDbFact]
    public async Task Confirm_FUP245_AnOutOfRangeAttemptAlone_WritesNoRowOnTheTrucker()
    {
        // FUP-204: the attempt may have missed only the buyer's pin, so it never decides the row; the road check-ins
        // at the farm are not read in its place.
        var trade = await SeedTradeAsync(Jimma, Addis, withOrder: true);
        await EveryRoadCheckInAtTheFarmAsync(trade);
        await DropoffCheckInAsync(trade, TimeSpan.FromMinutes(15), new TrackPoint(7.6705, 36.8302), distanceKm: 258.8);

        var result = await ConfirmAsync(trade, gpsDropoff: Addis, dropoffOn: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await PartitionOf(trade.TruckerId)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task Confirm_FUP245_NoDropoffCheckInOnTheOrder_FallsBackToTheRoadCheckIns()
    {
        var trade = await SeedTradeAsync(Jimma, Addis, withOrder: true);
        await EveryRoadCheckInAtTheFarmAsync(trade);

        var result = await ConfirmAsync(trade, gpsDropoff: Addis, dropoffOn: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var signal = (await PartitionOf(trade.TruckerId)).Should().ContainSingle(r => r["recordType"].S == "signal").Subject;
        signal["signalType"].S.Should().Be("GPS_ANOMALY");
        signal[FraudDetectionService.SourceUserIdAttribute].S.Should().Be(trade.BuyerId);
        signal["description"].S.Should().StartWith("Trucker check-in 10 min before the confirmation: 0.06 km from the pickup point");
    }

    [LocalDynamoDbFact]
    public async Task Confirm_FUP245_DropoffClassificationOff_TheDropoffCheckInIsNotRead_AsBefore()
    {
        var trade = await SeedTradeAsync(Jimma, Addis, withOrder: true);
        await EveryRoadCheckInAtTheFarmAsync(trade);
        await DropoffCheckInAsync(trade, TimeSpan.FromMinutes(30), Addis, distanceKm: 0.3);

        var result = await ConfirmAsync(trade, gpsDropoff: Addis, dropoffOn: false);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await PartitionOf(trade.TruckerId)).Should().ContainSingle(r => r["recordType"].S == "signal");
    }
}
