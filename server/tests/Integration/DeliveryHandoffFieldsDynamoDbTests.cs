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
/// FUP-246 against real DynamoDB (LocalStack in CI): the delivery write keeps each handoff point with its own
/// handoff. A buyer's confirm that also sends a pickup point far away leaves the trucker's stored pickup as it was
/// and writes no <c>IMPOSSIBLE_DELIVERY_TIME</c> on the trucker; a confirm of a really impossible trip from honest
/// points still writes it; a dispute that sends both points leaves both as the handoffs stored them. FUP-265: the
/// buyer's drop-off point is bounded by the trip's destination point the trucker set, so a confirm on a trip without
/// one writes no row, and a buyer's point stretched past the destination counts only as far as the destination.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryHandoffFieldsDynamoDbTests
{
    private const string CreatedAt = "2026-11-12T08:00:00.0000000Z";
    private const string Nonce = "nonce-fup246";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly string _truckerId = $"trucker-{Guid.NewGuid():N}";
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DeliveryHandoffFieldsDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = (string)ci[0],
            PhoneNumber = "+251900100000",
            DisplayName = "Party Name",
            Role = (string)ci[0] == _buyerId ? "buyer" : (string)ci[0] == _farmerId ? "farmer" : "trucker",
            Woreda = "Ada'a",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    /// <summary>The sync Lambda with fraud signals on, so the pattern rules run as deployed.</summary>
    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var fraud = new FraudDetectionService(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _ => { }
        };
        var escrowService = new EscrowService(EscrowRepo, new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            idempotencyRepo: null, telebirrGateway: null, orderRepo: OrderRepo, deliveryRepo: DeliveryRepo,
            transactionAuditRepo: TxRepo);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            fraudService: fraud, userRepo: _users, orderRepo: OrderRepo, escrowRepo: EscrowRepo,
            transactionAuditRepo: TxRepo, orderEscrowService: escrowService)
        {
            PatternRules = new TransactionPatternService(OrderRepo, EscrowRepo, TransactionPatternSettings.Default)
        };
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static AttributeValue Point(double lat, double lng) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            ["latitude"] = new() { N = lat.ToString(CultureInfo.InvariantCulture) },
            ["longitude"] = new() { N = lng.ToString(CultureInfo.InvariantCulture) }
        }
    };

    private static Dictionary<string, object> GpsData(double lat, double lng)
        => new() { { "latitude", lat }, { "longitude", lng } };

    // ──────────────────────────────────────────────
    // Seeding
    // ──────────────────────────────────────────────

    private static AttributeValue Nd(double value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    /// <summary>
    /// A delivery the trucker picked up <paramref name="pickedUpMinutesAgo"/> minutes ago at <paramref name="pickup"/>
    /// (the phone's scan a minute or so earlier, QR nonce on the row), on a trip with <paramref name="destination"/> as
    /// its destination point (none: a trip without one, as before trip-listing-api 2.1.0).
    /// </summary>
    private async Task<string> SeedPickedUpAsync(
        (double Lat, double Lng) pickup, (double Lat, double Lng)? destination = null,
        double pickedUpMinutesAgo = 20, double capturedMinutesAgo = 21, string? orderId = null)
    {
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        var tripId = $"trip-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var trip = new Dictionary<string, AttributeValue>
        {
            ["tripId"] = S(tripId),
            ["createdAt"] = S(CreatedAt),
            ["truckerId"] = S(_truckerId),
            ["status"] = S("inTransit"),
            ["fromWoreda"] = S("Jimma"),
            ["toWoreda"] = S("Addis Ketema"),
            ["version"] = N(1)
        };
        if (destination is { } point)
        {
            trip["destinationLatitude"] = Nd(point.Lat);
            trip["destinationLongitude"] = Nd(point.Lng);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Trips, Item = trip });
        var delivery = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S(tripId),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["truckerId"] = S(_truckerId),
            ["participantId"] = S(_buyerId),
            ["status"] = S("pickedUp"),
            ["gpsPickup"] = Point(pickup.Lat, pickup.Lng),
            ["pickedUpAt"] = S(Iso(now.AddMinutes(-pickedUpMinutesAgo))),
            [TransactionPatternRules.PickupCapturedAtField] = S(Iso(now.AddMinutes(-capturedMinutesAgo))),
            ["qrNonce"] = S(Nonce),
            ["createdAt"] = S(CreatedAt),
            ["version"] = N(1)
        };
        if (orderId != null)
        {
            delivery["orderId"] = S(orderId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = delivery });
        return deliveryId;
    }

    /// <summary>An order on the road whose buyer pinned their own drop-off point at <paramref name="buyerPoint"/>.</summary>
    private async Task<string> SeedOrderWithBuyerPointAsync((double Lat, double Lng) buyerPoint)
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("in_transit"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                [OrderDeliveryLocation.LatitudeField] = Nd(buyerPoint.Lat),
                [OrderDeliveryLocation.LongitudeField] = Nd(buyerPoint.Lng),
                ["orderTotal"] = new AttributeValue { N = "900" },
                ["version"] = N(1)
            }
        });
        return orderId;
    }

    private sealed record Trade(string OrderId, string DeliveryId, string EscrowId);

    /// <summary>A delivered order (confirmed two hours ago), its delivery with both points and its held escrow.</summary>
    private async Task<Trade> SeedDeliveredTradeAsync()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        var escrowId = $"escrow-order-{orderId}";
        var confirmedAt = Iso(DateTime.UtcNow.AddHours(-2));

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("delivered"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["activeDeliveryId"] = S(deliveryId),
                ["orderTotal"] = new AttributeValue { N = "900" },
                ["version"] = N(1)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId),
                ["tripId"] = S("trip-it"),
                ["orderId"] = S(orderId),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["participantId"] = S(_buyerId),
                ["status"] = S("delivered"),
                ["gpsPickup"] = Point(9.00, 38.70),
                ["gpsDropoff"] = Point(9.03, 38.74),
                ["confirmedAt"] = S(confirmedAt),
                ["createdAt"] = S(CreatedAt),
                ["version"] = N(1)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Escrows,
            Item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = S(escrowId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("escrowed"),
                ["orderId"] = S(orderId),
                ["deliveryId"] = S(deliveryId),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["amount"] = new AttributeValue { N = "900" },
                ["source"] = S("order"),
                ["deliveryConfirmedAt"] = S(confirmedAt),
                ["version"] = N(2)
            }
        });
        return new Trade(orderId, deliveryId, escrowId);
    }

    private async Task<Dictionary<string, AttributeValue>> DeliveryRowAsync(string deliveryId)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.Deliveries,
            KeyConditionExpression = "deliveryId = :d",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":d"] = S(deliveryId) },
            ConsistentRead = true
        });
        return response.Items.Single();
    }

    private static (double Lat, double Lng) PointOf(Dictionary<string, AttributeValue> row, string field)
        => (double.Parse(row[field].M["latitude"].N, CultureInfo.InvariantCulture),
            double.Parse(row[field].M["longitude"].N, CultureInfo.InvariantCulture));

    private async Task<List<string>> SignalTypesAsync(string userId)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = S(userId) },
            ConsistentRead = true
        });
        return response.Items.Select(i => i["signalKey"].S.Split('#').Last()).ToList();
    }

    // ──────────────────────────────────────────────
    // Sync calls
    // ──────────────────────────────────────────────

    private async Task<JsonElement> SendAsync(SyncFunction sync, string deliveryId, string userId,
        Dictionary<string, object> data)
    {
        var operation = new SyncOperationRequest
        {
            OperationId = Guid.NewGuid().ToString(),
            IdempotencyKey = $"{userId}:delivery:{deliveryId}:update:{Guid.NewGuid():N}",
            EntityType = "delivery",
            EntityId = deliveryId,
            Action = "update",
            Timestamp = Iso(DateTime.UtcNow),
            Data = data
        };
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

    // ──────────────────────────────────────────────
    // The buyer's confirm
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task BuyerConfirm_WithAFarPickupPoint_KeepsTheStoredPickup_AndWritesNoSignalOnTheTrucker()
    {
        // An honest 5 km trip near Addis Ababa in 20 minutes; the confirm also claims a pickup in Jimma (259 km).
        var deliveryId = await SeedPickedUpAsync((9.00, 38.70), destination: Addis);

        var result = await SendAsync(Sync(), deliveryId, _buyerId, new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", Nonce },
            { "gpsPickup", GpsData(7.67, 36.83) }, { "gpsDropoff", GpsData(9.03, 38.74) }
        });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await DeliveryRowAsync(deliveryId);
        row["status"].S.Should().Be("delivered");
        PointOf(row, "gpsPickup").Should().Be((9.00, 38.70));
        PointOf(row, "gpsDropoff").Should().Be((9.03, 38.74));
        (await SignalTypesAsync(_truckerId)).Should().NotContain("IMPOSSIBLE_DELIVERY_TIME");
    }

    [LocalDynamoDbFact]
    public async Task BuyerConfirm_HonestPointsTooFastForTheDistance_StillWritesTheSignalOnTheTrucker()
    {
        // The trucker's own pickup in Jimma, the buyer's drop-off in Addis Ababa 20 minutes later, at the trip's
        // destination (FUP-265: the point the trucker set bounds the buyer's).
        var deliveryId = await SeedPickedUpAsync(Jimma, destination: Addis);

        var result = await SendAsync(Sync(), deliveryId, _buyerId, new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", Nonce },
            { "gpsDropoff", GpsData(9.03, 38.74) }
        });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await DeliveryRowAsync(deliveryId);
        PointOf(row, "gpsPickup").Should().Be((7.67, 36.83));
        PointOf(row, "gpsDropoff").Should().Be((9.03, 38.74));
        (await SignalTypesAsync(_truckerId)).Should().Contain("IMPOSSIBLE_DELIVERY_TIME");
    }

    // ──────────────────────────────────────────────
    // FUP-265: never a row from a drop-off point the buyer alone controls
    // ──────────────────────────────────────────────

    private static readonly (double Lat, double Lng) Jimma = (7.67, 36.83);
    private static readonly (double Lat, double Lng) Addis = (9.03, 38.74);

    // 4.7 km past Addis Ababa seen from Jimma: inside the confirm's 5 km of the trip's destination, 263.5 km from the
    // pickup (at least 131.8 minutes at 120 km/h), where the destination itself is 258.9 km (129.4 minutes).
    private static readonly (double Lat, double Lng) StretchedPastAddis = (9.06, 38.77);

    private async Task<string?> SignalDescriptionAsync(string userId, string signalType)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = S(userId) },
            ConsistentRead = true
        });
        return response.Items.FirstOrDefault(i => i["signalKey"].S.EndsWith("#" + signalType, StringComparison.Ordinal))
            ?["description"].S;
    }

    private Task<JsonElement> ConfirmAsync(string deliveryId, (double Lat, double Lng) gpsDropoff)
        => SendAsync(Sync(), deliveryId, _buyerId, new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", Nonce },
            { "gpsDropoff", GpsData(gpsDropoff.Lat, gpsDropoff.Lng) }
        });

    [LocalDynamoDbFact]
    public async Task FUP265_AConfirmOnATripWithNoDestinationPoint_WritesNoRow_HoweverFarTheBuyersPoint()
    {
        // 259 km in 20 minutes by the buyer's point alone, which nothing bounds: the confirm passes, no row.
        var deliveryId = await SeedPickedUpAsync(Jimma, destination: null);

        var result = await ConfirmAsync(deliveryId, Addis);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await DeliveryRowAsync(deliveryId))["status"].S.Should().Be("delivered");
        (await SignalTypesAsync(_truckerId)).Should().NotContain("IMPOSSIBLE_DELIVERY_TIME");
    }

    [LocalDynamoDbFact]
    public async Task FUP265_AConfirmNearTheBuyersOwnOrderPoint_OnATripWithNoDestinationPoint_WritesNoRow()
    {
        // The buyer pinned the order's drop-off in Addis Ababa and confirms within 5 km of it: the confirm's check
        // passes on the buyer's own point, which never bounds the trucker's row.
        var orderId = await SeedOrderWithBuyerPointAsync(Addis);
        var deliveryId = await SeedPickedUpAsync(Jimma, destination: null, orderId: orderId);

        var result = await ConfirmAsync(deliveryId, (9.05, 38.75));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await SignalTypesAsync(_truckerId)).Should().NotContain("IMPOSSIBLE_DELIVERY_TIME");
    }

    [LocalDynamoDbFact]
    public async Task FUP265_ABuyerPointStretchedWithin5KmOfTheDestination_CannotTurnAPossibleTripIntoARow()
    {
        // About 130.4 minutes from the phone's pickup scan: enough for the trip's destination, not for the buyer's point.
        var deliveryId = await SeedPickedUpAsync(Jimma, destination: Addis, pickedUpMinutesAgo: 130, capturedMinutesAgo: 130.4);

        var result = await ConfirmAsync(deliveryId, StretchedPastAddis);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await DeliveryRowAsync(deliveryId);
        // The buyer's point alone would have flagged this trip (unless the confirm took over a minute to arrive).
        TransactionPatternRules.ImpossibleDeliveryTime(
                TransactionPatternRules.DeliveryTimeFactsFrom(row, row), TransactionPatternSettings.Default)
            .Should().NotBeNull();
        (await SignalTypesAsync(_truckerId)).Should().NotContain("IMPOSSIBLE_DELIVERY_TIME");
    }

    [LocalDynamoDbFact]
    public async Task FUP265_AReallyImpossibleTrip_WithAStretchedBuyerPoint_IsFlaggedFromTheTripsDestination()
    {
        var deliveryId = await SeedPickedUpAsync(Jimma, destination: Addis);

        var result = await ConfirmAsync(deliveryId, StretchedPastAddis);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        (await SignalDescriptionAsync(_truckerId, "IMPOSSIBLE_DELIVERY_TIME"))
            .Should().Contain("with the pickup scan and the trip's destination 258.9 km apart");
    }

    // ──────────────────────────────────────────────
    // A dispute
    // ──────────────────────────────────────────────

    [LocalDynamoDbTheory]
    [InlineData("buyer")]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task Dispute_SendingBothPoints_LeavesBothAsTheHandoffsStoredThem(string party)
    {
        var trade = await SeedDeliveredTradeAsync();
        var userId = party switch { "buyer" => _buyerId, "farmer" => _farmerId, _ => _truckerId };

        var result = await SendAsync(Sync(), trade.DeliveryId, userId, new Dictionary<string, object>
        {
            { "status", "disputed" }, { "disputeReason", "Half the sacks were wet." }, { "version", 1 },
            { "gpsPickup", GpsData(7.67, 36.83) }, { "gpsDropoff", GpsData(8.55, 39.27) }
        });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await DeliveryRowAsync(trade.DeliveryId);
        row["status"].S.Should().Be("disputed");
        row["disputeReason"].S.Should().Be("Half the sacks were wet.");
        PointOf(row, "gpsPickup").Should().Be((9.00, 38.70));
        PointOf(row, "gpsDropoff").Should().Be((9.03, 38.74));
    }
}
