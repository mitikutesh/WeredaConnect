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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-36 (delivery-verification-api.md P.4, sync-protocol.md): the trucker's drop-off check-in
/// (<c>handoff_check_in</c> kind <c>arrived_dropoff</c>) behind <c>dropoffClassification</c>, and the stamp the
/// order takes at pickup. The check-in goes into the order's <c>dropoffCheckIn</c> through
/// <see cref="IOrderRepository.TryRecordDropoffCheckInAsync"/> (no version bump); the server's receipt time
/// is the only time that counts.
/// </summary>
public sealed class SyncFunctionDropoffCheckInTests
{
    private const string OrderId = "ord-dc";
    private const string DeliveryId = "del-dc";
    private const string TripId = "trip-dc";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string TripsTable = "test-trips-table";
    private const string Nonce = "nonce-of-the-farmer-0123456789abcdef";
    private const string ArrivalKey = "uploads/photos/del-dc/arrival-1763000000000.webp";

    // The buyer's drop-off point, the trip's destination, and places relative to them.
    private const double OrderLat = 9.0300;
    private const double OrderLon = 38.7400;
    private const double TripLat = 9.0100;
    private const double TripLon = 38.7600;
    private const double NearLat = 9.0320; // about 0.2 km from the buyer's point
    private const double FarLat = 9.2000; // about 19 km away

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IDeliveryQrSecretRepository _secrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly TestLambdaContext _context = new();

    private Dictionary<string, object> _order;
    private readonly List<(Dictionary<string, object> Entry, bool WhileOpen)> _recorded = new();
    private bool _dropoff = true;
    private bool _pickup;
    private string _deliveryStatus = "inTransit";
    private bool _arrivalPhoto = true;

    public SyncFunctionDropoffCheckInTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _order = Order();
        _orders.GetByIdAsync(OrderId, true).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.TryRecordDropoffCheckInAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>())
            .Returns(call =>
            {
                _recorded.Add((call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<bool>(2)));
                return true;
            });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { DeliveryItem() } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { TripItem() } });
    }

    // ===== fixture =====

    /// <summary>A trip order picked up with the flag on: T8 two days on, both drop-off points stamped.</summary>
    private static Dictionary<string, object> Order(
        string status = "in_transit", string? judgement = DropoffJudgement.Outcome.Pending, DateTime? deadline = null, bool stamped = true)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "listingId", "listing-dc" }, { "createdAt", DateTime.UtcNow.AddDays(-4).ToString("o") },
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId }, { "tripId", TripId },
            { "status", status }, { "version", "9" }, { "activeDeliveryId", DeliveryId },
            { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
            { OrderDeliveryLocation.LatitudeField, Text(OrderLat) }, { OrderDeliveryLocation.LongitudeField, Text(OrderLon) }
        };
        if (judgement != null)
        {
            order[DropoffJudgement.Field] = judgement;
            order[DropoffJudgement.DeadlineField] = OrderCommitment.Iso(deadline ?? DateTime.UtcNow.AddHours(48));
        }

        if (stamped)
        {
            order[DropoffJudgement.OrderLatitudeField] = Text(OrderLat);
            order[DropoffJudgement.OrderLongitudeField] = Text(OrderLon);
            order[DropoffJudgement.TripLatitudeField] = Text(TripLat);
            order[DropoffJudgement.TripLongitudeField] = Text(TripLon);
        }

        return order;
    }

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private Dictionary<string, AttributeValue> DeliveryItem()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = TripId } },
            { "orderId", new AttributeValue { S = OrderId } },
            { "listingId", new AttributeValue { S = "listing-dc" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "deliveryType", new AttributeValue { S = "trucker" } },
            { "status", new AttributeValue { S = _deliveryStatus } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.AddDays(-2).ToString("o") } },
            { "pickedUpAt", new AttributeValue { S = DateTime.UtcNow.AddHours(-5).ToString("o") } },
            { "version", new AttributeValue { N = "3" } }
        };
        if (_arrivalPhoto)
        {
            item["arrivalPhotoRef"] = new AttributeValue { S = ArrivalKey };
        }

        return item;
    }

    private static Dictionary<string, AttributeValue> TripItem() => new()
    {
        { "tripId", new AttributeValue { S = TripId } },
        { "truckerId", new AttributeValue { S = TruckerId } },
        { "status", new AttributeValue { S = "inTransit" } },
        { "destinationLatitude", new AttributeValue { N = Text(TripLat) } },
        { "destinationLongitude", new AttributeValue { N = Text(TripLon) } }
    };

    private SyncFunction MakeFunction() => new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
        fraudService: _fraud, orderRepo: _orders, qrSecretRepo: _secrets)
    {
        CommitmentsEnabled = true,
        PickupClassificationEnabled = _pickup,
        DropoffClassificationEnabled = _dropoff
    };

    private static Dictionary<string, object> Arrived(double latitude = NearLat, double longitude = OrderLon) => new()
    {
        { "orderId", OrderId }, { "kind", PickupJudgement.Kind.ArrivedDropoff },
        { "latitude", latitude }, { "longitude", longitude }, { "accuracyM", 20 }
    };

    private Task<JsonElement> CheckInAsync(string userId, Dictionary<string, object> data)
        => SendAsync(userId, PickupJudgement.EntityType, $"{OrderId}#{data["kind"]}", "create", data);

    private async Task<JsonElement> SendAsync(string userId, string entityType, string entityId, string action, Dictionary<string, object> data)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-36",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-esc-36",
                        IdempotencyKey = $"{userId}:{entityType}:{entityId}:{action}:{Guid.NewGuid():N}",
                        EntityType = entityType,
                        EntityId = entityId,
                        Action = action,
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await MakeFunction().FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static string MessageOf(JsonElement result) => result.GetProperty("message").GetString() ?? "";

    private Task NoWriteAsync()
        => _orders.DidNotReceive().TryRecordDropoffCheckInAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>());

    // ===== on time =====

    [Fact]
    public async Task TheTrucker_ChecksInAtTheDropoff_TheEntryIsStoredWithTheServersTime()
    {
        var before = DateTime.UtcNow;

        var result = await CheckInAsync(TruckerId, Arrived());

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(9, "a check-in moves no version");
        _recorded.Should().ContainSingle();
        var (entry, whileOpen) = _recorded[0];
        whileOpen.Should().BeTrue();
        entry[PickupJudgement.EntryBy].Should().Be(TruckerId);
        ((bool)entry[PickupJudgement.EntryDistanceChecked]).Should().BeTrue();
        ((bool)entry[PickupJudgement.EntryLate]).Should().BeFalse();
        OrderCommitment.TryParseUtc((string)entry[PickupJudgement.EntryReceivedAt], out var received).Should().BeTrue();
        received.Should().BeOnOrAfter(before.AddSeconds(-1));
        ((double)entry[PickupJudgement.EntryDistanceKm]).Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task AFixNearTheTripsDestination_PassesToo()
    {
        var result = await CheckInAsync(TruckerId, Arrived(TripLat + 0.001, TripLon));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task AFixFarFromBothPoints_IsRefused_TooFar()
    {
        var result = await CheckInAsync(TruckerId, Arrived(FarLat));

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooFarCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task WithoutTheArrivalPhoto_TheCheckInIsRefused_PhotoRequired()
    {
        _arrivalPhoto = false;

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{DropoffJudgement.PhotoRequiredCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task WithoutGps_TheCheckInIsRefused_Validation()
    {
        var data = Arrived();
        data.Remove("latitude");
        data.Remove("longitude");

        var result = await CheckInAsync(TruckerId, data);

        MessageOf(result).Should().StartWith($"{PickupJudgement.ValidationCode}: ").And.Contain("arrived_dropoff");
        await NoWriteAsync();
    }

    [Fact]
    public async Task NoPointToMeasureAgainst_TheCheckInIsKeptUnmeasured()
    {
        _order = Order(stamped: false);
        _order.Remove(OrderDeliveryLocation.LatitudeField);
        _order.Remove(OrderDeliveryLocation.LongitudeField);
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var result = await CheckInAsync(TruckerId, Arrived());

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        ((bool)_recorded.Single().Entry[PickupJudgement.EntryDistanceChecked]).Should().BeFalse(
            "an unmeasured check-in is kept, and T9 then never confirms automatically");
    }

    // ===== who and when =====

    [Fact]
    public async Task TheBuyer_CannotCheckInForTheTrucker()
    {
        var result = await CheckInAsync(BuyerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotPartyCode}: ");
        await NoWriteAsync();
        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AStranger_IsRefused_WithTheRoleSignal()
    {
        var result = await CheckInAsync("stranger-9", Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotPartyCode}: ");
        await _fraud.Received(1).RecordFraudSignalAsync("stranger-9", FraudDetectionService.RoleForbiddenSignal,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await NoWriteAsync();
    }

    [Fact]
    public async Task AnOrderPickedUpBeforeTheFlag_TakesNoDropoffCheckIn()
    {
        _order = Order(judgement: null, stamped: false);

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task ADeliveryNoLongerOnTheRoad_TakesNoCheckIn()
    {
        _deliveryStatus = "delivered";

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task AfterT9_TheOrderTakesNoCheckIn()
    {
        _order = Order(status: "delivered", judgement: DropoffJudgement.Outcome.AutoConfirmed);

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task ACheckInAfterT8_IsKeptAsLateEvidence_AndAnsweredTooLate()
    {
        _order = Order(deadline: DateTime.UtcNow.AddMinutes(-5));

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        var (entry, whileOpen) = _recorded.Single();
        whileOpen.Should().BeFalse();
        ((bool)entry[PickupJudgement.EntryLate]).Should().BeTrue();
    }

    [Fact]
    public async Task ACheckInAfterTheOverdueJudgement_IsLateEvidence()
    {
        _order = Order(status: "disputed", judgement: DropoffJudgement.Outcome.DeliveryOverdue, deadline: DateTime.UtcNow.AddHours(-3));
        _deliveryStatus = "disputed";

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        _recorded.Single().WhileOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ASecondCheckIn_GetsTheFirstOnesAnswer_AndWritesNothing()
    {
        _order[DropoffJudgement.CheckInField] = new Dictionary<string, object>
        {
            { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(DateTime.UtcNow.AddHours(-1)) },
            { PickupJudgement.EntryDistanceChecked, true },
            { PickupJudgement.EntryLate, false }
        };

        var result = await CheckInAsync(TruckerId, Arrived(FarLat));

        result.GetProperty("status").GetString().Should().Be("success");
        await NoWriteAsync();
    }

    [Fact]
    public async Task TheJudgementLandingFirst_MakesTheCheckInLate()
    {
        var calls = 0;
        _orders.TryRecordDropoffCheckInAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>())
            .Returns(call =>
            {
                _recorded.Add((call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<bool>(2)));
                if (calls++ == 0)
                {
                    _order = Order(status: "disputed", judgement: DropoffJudgement.Outcome.DeliveryOverdue);
                    return false;
                }

                return true;
            });

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        _recorded.Should().HaveCount(2);
        _recorded[1].WhileOpen.Should().BeFalse();
    }

    // ===== the flags =====

    [Fact]
    public async Task WithTheFlagOff_TheKindDoesNotExist_NoReadNoSignal()
    {
        _dropoff = false;
        _pickup = true;

        var result = await CheckInAsync("stranger-9", Arrived());

        MessageOf(result).Should().StartWith($"{PickupJudgement.ValidationCode}: ").And.Contain("not taken yet");
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task WithBothFlagsOff_TheEntityIsUnsupported()
    {
        _dropoff = false;

        var result = await CheckInAsync(TruckerId, Arrived());

        MessageOf(result).Should().Contain("Unsupported entity type");
        await NoWriteAsync();
    }

    [Fact]
    public async Task WithOnlyTheDropoffFlag_ThePickupKindsDoNotExist()
    {
        var data = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "kind", PickupJudgement.Kind.ArrivedPickup }, { "latitude", NearLat }, { "longitude", OrderLon }
        };

        var result = await CheckInAsync(TruckerId, data);

        MessageOf(result).Should().StartWith($"{PickupJudgement.ValidationCode}: ").And.Contain("kind must be arrived_dropoff");
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    // ===== FUP-204: the buyer cannot push the drop-off away =====

    [Fact]
    public async Task FUP204_ABuyerWhoMovesTheDropoffAfterPickup_DoesNotFailTheTruckersCheckIn()
    {
        // The buyer's live point moved 19 km away after pickup; the points stamped at pickup stand.
        _order[OrderDeliveryLocation.LatitudeField] = Text(FarLat);

        var result = await CheckInAsync(TruckerId, Arrived());

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        ((bool)_recorded.Single().Entry[PickupJudgement.EntryDistanceChecked]).Should().BeTrue();
    }

    // ===== what the parties read =====

    [Fact]
    public void TheView_ShowsTheDropoffCheckIn_WithoutCoordinates_AndHidesTheStampedPoints()
    {
        var order = Order();
        order[DropoffJudgement.CheckInField] = new Dictionary<string, object>
        {
            { PickupJudgement.EntryReceivedAt, "2026-11-20T08:00:00.0000000Z" },
            { PickupJudgement.EntryLatitude, NearLat }, { PickupJudgement.EntryLongitude, OrderLon },
            { PickupJudgement.EntryBy, TruckerId }, { PickupJudgement.EntryDistanceChecked, true }, { PickupJudgement.EntryLate, false }
        };

        var view = PickupJudgement.ViewOf(order);
        view.Should().ContainKey(PickupJudgement.Kind.ArrivedDropoff);
        view![PickupJudgement.Kind.ArrivedDropoff].DistanceChecked.Should().BeTrue();

        var scrubbed = PickupJudgement.ForClient(new SyncOperationResult { OperationId = "x", Status = "success", ServerData = order })
            .ServerData!;
        foreach (var hidden in DropoffJudgement.HiddenFields)
        {
            scrubbed.Should().NotContainKey(hidden);
        }

        var checkIns = (Dictionary<string, object>)scrubbed[PickupJudgement.CheckInsField];
        ((Dictionary<string, object>)checkIns[PickupJudgement.Kind.ArrivedDropoff]).Should().NotContainKey(PickupJudgement.EntryLatitude)
            .And.NotContainKey(PickupJudgement.EntryBy);
    }

    // ===== the stamp at pickup =====

    private void ServePickup()
    {
        _order = Order(status: "accepted", judgement: null, stamped: false);
        _deliveryStatus = "pending";
        _secrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId, Nonce = Nonce, IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o")
        });
        _dynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _deliveryStatus = "pickedUp";
                return new UpdateItemResponse { Attributes = DeliveryItem() };
            });
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object> { { "orderId", OrderId } }));
    }

    private Task<JsonElement> PickUpAsync()
        => SendAsync(TruckerId, "delivery", DeliveryId, "update",
            new Dictionary<string, object> { { "status", "pickedUp" }, { "version", 3 }, { "scannedQrNonce", Nonce } });

    [Fact]
    public async Task WithTheFlagOn_ThePickupStampsTheOrder_T8AndBothDropoffPoints()
    {
        ServePickup();

        var result = await PickUpAsync();

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "pickup_confirmed"
            && (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Pending
            && f.ContainsKey(DropoffJudgement.DeadlineField)
            && (double)f[DropoffJudgement.OrderLatitudeField] == OrderLat
            && (double)f[DropoffJudgement.TripLatitudeField] == TripLat), 9);
    }

    [Fact]
    public async Task WithTheFlagOff_ThePickupStampsNothing()
    {
        _dropoff = false;
        ServePickup();

        await PickUpAsync();

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            f.Count == 1 && (string)f["status"] == "pickup_confirmed"), 9);
    }
}
