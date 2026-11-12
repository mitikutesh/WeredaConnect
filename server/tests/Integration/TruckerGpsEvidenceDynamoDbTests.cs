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

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var fraud = new FraudDetectionService(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _ => { }
        };
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            fraudService: fraud, orderRepo: new OrderRepository(Ddb, _db.Orders), tripLocationRepo: Locations)
        {
            GpsAnomalyOnTrucker = true
        };
    }

    private sealed record Trade(string TripId, string DeliveryId, string BuyerId, string TruckerId);

    /// <summary>An inter-woreda trip to <paramref name="destination"/> and its delivery, picked up four hours ago at <paramref name="pickup"/>.</summary>
    private async Task<Trade> SeedTradeAsync(TrackPoint pickup, TrackPoint destination)
    {
        var trade = new Trade(Id($"trip-{Guid.NewGuid():N}"), Id($"delivery-{Guid.NewGuid():N}"),
            Id($"buyer-{Guid.NewGuid():N}"), Id($"trucker-{Guid.NewGuid():N}"));
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
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
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
                ["pickedUpAt"] = S(Iso(DateTime.UtcNow.AddHours(-4))),
                ["gpsPickup"] = new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue> { ["latitude"] = N(pickup.Latitude), ["longitude"] = N(pickup.Longitude) }
                },
                ["createdAt"] = S(CreatedAt),
                ["updatedAt"] = S(CreatedAt),
                ["version"] = new AttributeValue { N = "1" }
            }
        });
        return trade;
    }

    /// <summary>A check-in as the ingest stores it (LT-05), captured <paramref name="ago"/> before now.</summary>
    private Task CheckInAsync(string tripId, string truckerId, TimeSpan ago, TrackPoint at, string source = "hourly")
    {
        var captured = DateTime.UtcNow - ago;
        return Locations.TryPutAsync(new TripLocationRow(tripId, $"{LocationPingRules.FormatUtc(captured)}#fix", truckerId,
            LocationPingRules.RoundCoordinate(at.Latitude), LocationPingRules.RoundCoordinate(at.Longitude), 35, source, false,
            null, LocationPingRules.FormatUtc(captured.AddSeconds(3)), LocationPingRules.ExpiresAt(captured)));
    }

    private async Task<JsonElement> ConfirmAsync(Trade trade, TrackPoint gpsDropoff)
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

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
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
}
