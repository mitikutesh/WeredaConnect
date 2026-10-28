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
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-05: the <c>location_ping</c> sync entity (live-trip-api.md 1.0.0 section 1, sync-protocol.md
/// 1.31.0). One test per acceptance rule and per code, the idempotent repeats, and late offline
/// pings kept after the delivery ended.
/// </summary>
public class SyncFunctionLocationPingTests
{
    private const string TruckerId = "00000000-0000-0000-0000-000000000001";
    private const string OtherTruckerId = "00000000-0000-0000-0000-000000000002";
    private const string BuyerId = "00000000-0000-0000-0000-000000000003";
    private const string TripId = "00000000-0000-0000-0000-000000000005";
    private const string TripsTable = "test-trips";
    private const string DeliveriesTable = "test-deliveries";

    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Captured = new(2026, 10, 27, 8, 42, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITripLocationRepository _locations = Substitute.For<ITripLocationRepository>();
    private readonly List<TripLocationRow> _written = new();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    public SyncFunctionLocationPingTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        TripOwnedBy(TruckerId);
        DeliveriesOnTrip(Delivery("pickedUp"));
        _locations.ListSortKeysAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new List<string>());
        _locations.TryPutAsync(Arg.Do<TripLocationRow>(row => _written.Add(row))).Returns(true);
        _function = Build(_locations);
    }

    // ===== Accepted =====

    [Fact]
    public async Task Fix_ByTheTripsTrucker_WhilePickedUp_IsStoredRoundedWithItsTtl()
    {
        var result = await SendAsync(Ping("fix", data: d =>
        {
            d["latitude"] = 8.75234;
            d["longitude"] = 38.97856;
            d["accuracyM"] = 35.5;
            d["mocked"] = true;
        }));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(1);
        var row = _written.Should().ContainSingle().Subject;
        row.TripId.Should().Be(TripId);
        row.SortKey.Should().Be("2026-10-27T08:42:00.000Z#fix");
        row.TruckerId.Should().Be(TruckerId);
        row.Latitude.Should().Be(8.7523);
        row.Longitude.Should().Be(38.9786);
        row.AccuracyM.Should().Be(35.5);
        row.Source.Should().Be("hourly");
        row.Mocked.Should().BeTrue();
        row.PausedUntil.Should().BeNull();
        row.ReceivedAt.Should().Be("2026-10-27T09:00:00.000Z");
        row.ExpiresAt.Should().Be(new DateTimeOffset(Captured.AddDays(60)).ToUnixTimeSeconds());
        // The rate limit reads the capture's clock hour.
        await _locations.Received(1).ListSortKeysAsync(TripId, "2026-10-27T08:");
        // The ping is an ordinary success: its idempotency key is stored like every entity's.
        await _syncMeta.Received(1).StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Is("op-ping"), Arg.Is("location_ping"), Arg.Is("success"));
    }

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    public async Task Fix_WhileADeliveryIsOnTheRoad_IsAccepted(string status)
    {
        DeliveriesOnTrip(Delivery("delivered", confirmedAt: "2026-10-27T07:00:00Z"), Delivery(status));

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle();
    }

    [Fact]
    public async Task Paused_StoresPausedUntil_WithoutCoordinates_AndSkipsTheFixLimit()
    {
        var result = await SendAsync(Ping("paused", data: d => d["pausedUntil"] = "2026-10-27T10:12:00.000Z"));

        result.GetProperty("status").GetString().Should().Be("success");
        var row = _written.Should().ContainSingle().Subject;
        row.SortKey.Should().Be("2026-10-27T08:42:00.000Z#paused");
        row.PausedUntil.Should().Be("2026-10-27T10:12:00.000Z");
        row.Latitude.Should().BeNull();
        row.Longitude.Should().BeNull();
        row.AccuracyM.Should().BeNull();
        row.Source.Should().BeNull();
        row.Mocked.Should().BeNull();
        await _locations.DidNotReceive().ListSortKeysAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Paused_Overnight_UpTo18Hours_IsAccepted()
    {
        var result = await SendAsync(Ping("paused", data: d => d["pausedUntil"] = "2026-10-28T02:42:00Z"));

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("declined")]
    public async Task SharingEvents_AreStoredWithoutCoordinates(string kind)
    {
        var result = await SendAsync(Ping(kind));

        result.GetProperty("status").GetString().Should().Be("success");
        var row = _written.Should().ContainSingle().Subject;
        row.SortKey.Should().Be($"2026-10-27T08:42:00.000Z#{kind}");
        row.Latitude.Should().BeNull();
    }

    [Fact]
    public async Task Event_WithFieldsOfAFix_IsStored_AndTheFieldsAreDropped()
    {
        var result = await SendAsync(Ping("resumed", data: d =>
        {
            d["source"] = "manual";
            d["latitude"] = 99.0;
            d["mocked"] = "yes";
        }));

        result.GetProperty("status").GetString().Should().Be("success");
        var row = _written.Should().ContainSingle().Subject;
        row.Source.Should().BeNull();
        row.Latitude.Should().BeNull();
        row.Mocked.Should().BeNull();
    }

    [Fact]
    public async Task Fix_WithoutMocked_StoresNoMockedFlag()
    {
        var result = await SendAsync(Ping("fix", data: d => d.Remove("mocked")));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle().Which.Mocked.Should().BeNull();
    }

    [Fact]
    public async Task CapturedAt_WithAnOffset_IsReadAsUtc()
    {
        var op = Ping("fix", data: d => d["capturedAt"] = "2026-10-27T11:42:00+03:00");

        var result = await SendAsync(op);

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle().Which.SortKey.Should().Be("2026-10-27T08:42:00.000Z#fix");
    }

    // ===== Late offline pings =====

    [Theory]
    [InlineData("delivered", "confirmedAt")]
    [InlineData("disputed", "confirmedAt")]
    [InlineData("cancelled", "cancelledAt")]
    public async Task LateOfflinePing_CapturedBeforeTheDeliveryLeftTheRoad_IsKept(string status, string leftField)
    {
        // The truck delivered at 08:50; the 08:42 check-in waited offline and arrives at 09:00.
        var delivery = Delivery(status, pickedUpAt: "2026-10-27T06:00:00Z");
        delivery[leftField] = "2026-10-27T08:50:00Z";
        DeliveriesOnTrip(delivery);

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle();
    }

    [Fact]
    public async Task LateOfflinePing_TwoDaysLate_IsKept()
    {
        var captured = Now.AddDays(-2);
        DeliveriesOnTrip(Delivery("delivered", confirmedAt: Now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture)));

        var result = await SendAsync(Ping("fix", captured));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle().Which.SortKey.Should().Be(
            captured.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "#fix");
    }

    [Fact]
    public async Task Deliveries_AreReadOverEveryPage()
    {
        var page2 = new Dictionary<string, AttributeValue> { ["deliveryId"] = new() { S = "d-1" } };
        _deliveries.QueryDeliveriesAsync(Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Is<Dictionary<string, AttributeValue>?>(k => k == null))
            .Returns((new List<Dictionary<string, object>> { Delivery("pending") }, page2));
        _deliveries.QueryDeliveriesAsync(Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Is<Dictionary<string, AttributeValue>?>(k => k == page2))
            .Returns((new List<Dictionary<string, object>> { Delivery("inTransit") }, (Dictionary<string, AttributeValue>?)null));

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.Received(2).QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Is(50), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ===== LOCATION_NOT_ACTIVE (deferred) =====

    public static IEnumerable<object[]> NoDeliveryOnTheRoad()
    {
        yield return new object[] { "no delivery", Array.Empty<string>() };
        yield return new object[] { "pending", new[] { "pending" } };
        yield return new object[] { "cancelled before pickup", new[] { "cancelled-before-pickup" } };
        yield return new object[] { "one delivered earlier, the next still pending", new[] { "delivered-earlier", "pending" } };
    }

    [Theory]
    [MemberData(nameof(NoDeliveryOnTheRoad))]
    public async Task NoDeliveryOnTheRoad_IsDeferred_NotAnError(string because, string[] shapes)
    {
        DeliveriesOnTrip(shapes.Select(Shape).ToArray());

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("deferred", because);
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_ACTIVE: ");
        await NothingWritten();
        // A deferred ping is processed again later: its idempotency key is not stored.
        await _syncMeta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ===== LOCATION_SHARING_ENDED =====

    [Fact]
    public async Task EveryDeliveryLeftTheRoadBeforeTheCapture_IsSharingEnded()
    {
        DeliveriesOnTrip(
            Delivery("delivered", confirmedAt: "2026-10-27T08:00:00Z"),
            Delivery("disputed", pickedUpAt: "2026-10-27T05:00:00Z", confirmedAt: "2026-10-27T08:30:00Z"),
            Shape("cancelled-before-pickup"));

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_SHARING_ENDED: ");
        await NothingWritten();
    }

    [Fact]
    public async Task SharingEnded_InABatchWithADeliveryWrite_IsDeferred()
    {
        // The deliveries-by-trip index may not show a delivery written earlier in the same batch yet,
        // so the permanent answer waits for the next sync.
        DeliveriesOnTrip(Delivery("delivered", confirmedAt: "2026-10-27T08:00:00Z"));
        var cancelled = CancellableDelivery();

        var results = await SendBatchAsync(TruckerId, CancelOp(cancelled), Ping("fix"));

        results[0].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("status").GetString().Should().Be("deferred");
        results[1].GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_ACTIVE: ");
        await NothingWritten();
    }

    // ===== LOCATION_NOT_TRUCKER =====

    [Fact]
    public async Task AnotherTruckersTrip_IsNotTrucker()
    {
        TripOwnedBy(OtherTruckerId);

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_TRUCKER: ");
        await NothingWritten();
        await _deliveries.DidNotReceive().QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task ABuyer_IsNotTrucker()
    {
        var result = await SendAsync(Ping("fix"), BuyerId);

        result.GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_TRUCKER: ");
        await NothingWritten();
    }

    [Fact]
    public async Task AnUnknownTrip_IsNotTrucker()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("message").GetString().Should().StartWith("LOCATION_NOT_TRUCKER: ");
        await NothingWritten();
    }

    // ===== LOCATION_RATE_LIMITED =====

    [Fact]
    public async Task TheTwentyFirstFixOfTheHour_IsRateLimited()
    {
        HourHolds(Enumerable.Range(0, 20).Select(i => $"2026-10-27T08:{i:00}:00.000Z#fix"));

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_RATE_LIMITED: ");
        await NothingWritten();
    }

    [Fact]
    public async Task TheTwentiethFix_AndEventsBeyondTheLimit_AreAccepted()
    {
        // 19 fixes plus events: only fixes count.
        HourHolds(Enumerable.Range(0, 19).Select(i => $"2026-10-27T08:{i:00}:00.000Z#fix")
            .Concat(new[] { "2026-10-27T08:30:00.000Z#paused", "2026-10-27T08:35:00.000Z#resumed" }));

        var fix = await SendAsync(Ping("fix"));

        fix.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task AnEvent_InAFullHour_IsAccepted()
    {
        HourHolds(Enumerable.Range(0, 20).Select(i => $"2026-10-27T08:{i:00}:00.000Z#fix"));

        var result = await SendAsync(Ping("resumed"));

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== Idempotent repeats =====

    [Fact]
    public async Task ARepeatedFix_IsAnIdempotentSuccess_EvenInAFullHour()
    {
        HourHolds(Enumerable.Range(23, 20).Select(i => $"2026-10-27T08:{i:00}:00.000Z#fix"));

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("message").GetString().Should().Be("Location already stored.");
        await NothingWritten();
    }

    [Fact]
    public async Task ARepeatedEvent_IsAnIdempotentSuccess()
    {
        _locations.TryPutAsync(Arg.Any<TripLocationRow>()).Returns(false);

        var result = await SendAsync(Ping("paused", data: d => d["pausedUntil"] = "2026-10-27T09:12:00Z"));

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("message").GetString().Should().Be("Location already stored.");
    }

    [Fact]
    public async Task TheSameCaptureTime_WithAnotherKind_IsANewRow()
    {
        HourHolds(new[] { "2026-10-27T08:42:00.000Z#paused" });

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle().Which.SortKey.Should().Be("2026-10-27T08:42:00.000Z#fix");
    }

    [Fact]
    public async Task ARepeatedIdempotencyKey_IsDuplicate()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);

        var result = await SendAsync(Ping("fix"));

        result.GetProperty("status").GetString().Should().Be("duplicate");
        await NothingWritten();
    }

    // ===== LOCATION_STALE =====

    [Theory]
    [InlineData(-7 * 24 * 60 - 1)]
    [InlineData(3)]
    public async Task ACaptureOutsideTheWindow_IsStale(int minutesFromNow)
    {
        var result = await SendAsync(Ping("fix", Now.AddMinutes(minutesFromNow)));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_STALE: ");
        await NothingWritten();
    }

    [Theory]
    [InlineData(-7 * 24 * 60 + 1)]
    [InlineData(2)]
    public async Task ACaptureAtTheEdgeOfTheWindow_IsAccepted(int minutesFromNow)
    {
        DeliveriesOnTrip(Delivery("inTransit"));

        var result = await SendAsync(Ping("fix", Now.AddMinutes(minutesFromNow)));

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task AStalePing_IsStale_EvenWhenNoDeliveryIsOnTheRoad()
    {
        DeliveriesOnTrip(Delivery("pending"));

        var result = await SendAsync(Ping("fix", Now.AddDays(-8)));

        result.GetProperty("message").GetString().Should().StartWith("LOCATION_STALE: ");
    }

    // ===== LOCATION_VALIDATION =====

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task UpdateAndDelete_AreRefused(string action)
    {
        var result = await SendAsync(Ping("fix", action: action));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("LOCATION_VALIDATION: ");
        await NothingWritten();
    }

    public static IEnumerable<object[]> InvalidPayloads()
    {
        object[] Case(string because, string kind, Action<Dictionary<string, object>> change, string text)
            => new object[] { because, kind, change, text };

        yield return Case("no tripId", "fix", d => d.Remove("tripId"), "tripId is required.");
        yield return Case("a numeric tripId", "fix", d => d["tripId"] = 42, "tripId must be a string.");
        yield return Case("an unknown kind", "fix", d => d["kind"] = "teleport", "kind must be one of");
        yield return Case("no capturedAt", "fix", d => d.Remove("capturedAt"), "capturedAt is required.");
        yield return Case("an unreadable capturedAt", "fix", d => d["capturedAt"] = "yesterday", "capturedAt must be an ISO 8601");
        yield return Case("an unknown source", "fix", d => d["source"] = "satellite", "source must be one of");
        yield return Case("no source on a fix", "fix", d => d.Remove("source"), "source must be one of");
        yield return Case("no latitude", "fix", d => d.Remove("latitude"), "latitude is required for fix.");
        yield return Case("a latitude south of the bounds", "fix", d => d["latitude"] = 2.99, "latitude must be between 3 and 15.");
        yield return Case("a latitude north of the bounds", "fix", d => d["latitude"] = 15.01, "latitude must be between 3 and 15.");
        yield return Case("a latitude as text", "fix", d => d["latitude"] = "8.75", "latitude must be a number.");
        yield return Case("no longitude", "fix", d => d.Remove("longitude"), "longitude is required for fix.");
        yield return Case("a longitude west of the bounds", "fix", d => d["longitude"] = 32.4, "longitude must be between 32.5 and 48.5.");
        yield return Case("a longitude east of the bounds", "fix", d => d["longitude"] = 48.6, "longitude must be between 32.5 and 48.5.");
        yield return Case("no accuracy", "fix", d => d.Remove("accuracyM"), "accuracyM is required for fix.");
        yield return Case("a negative accuracy", "fix", d => d["accuracyM"] = -1, "accuracyM must be between 0 and 5000.");
        yield return Case("an accuracy above 5 km", "fix", d => d["accuracyM"] = 5000.5, "accuracyM must be between 0 and 5000.");
        yield return Case("mocked as text", "fix", d => d["mocked"] = "false", "mocked must be a boolean.");
        yield return Case("an unknown field", "fix", d => d["speedKmh"] = 80, "Unknown fields: speedKmh.");
        yield return Case("an entityId for another capture time", "fix",
            d => d["capturedAt"] = "2026-10-27T08:41:00.000Z", "entityId must be '{tripId}#{capturedAt in epoch ms}'.");
        yield return Case("an entityId for another trip", "fix",
            d => d["tripId"] = "00000000-0000-0000-0000-000000000099", "entityId must be");
        yield return Case("no pausedUntil on a pause", "paused", d => d.Remove("pausedUntil"), "pausedUntil is required.");
        yield return Case("a pause ending before it starts", "paused",
            d => d["pausedUntil"] = "2026-10-27T08:41:00Z", "pausedUntil must be after capturedAt and at most 18 hours later.");
        yield return Case("a pause longer than 18 hours", "paused",
            d => d["pausedUntil"] = "2026-10-28T02:42:01Z", "pausedUntil must be after capturedAt and at most 18 hours later.");
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task AnInvalidPayload_IsValidation(string because, string kind, Action<Dictionary<string, object>> change, string text)
    {
        var op = Ping(kind, data: d =>
        {
            if (kind == "paused")
            {
                d["pausedUntil"] = "2026-10-27T09:12:00Z";
            }

            change(d);
        });

        var result = await SendAsync(op);

        result.GetProperty("status").GetString().Should().Be("error", because);
        var message = result.GetProperty("message").GetString();
        message.Should().StartWith("LOCATION_VALIDATION: ", because);
        message.Should().Contain(text, because);
        await NothingWritten();
    }

    [Fact]
    public async Task AnEmptyPayload_IsValidation()
    {
        var op = Ping("fix");
        op.Data!.Clear();

        var result = await SendAsync(op);

        result.GetProperty("message").GetString().Should().StartWith("LOCATION_VALIDATION: ");
    }

    // ===== Wiring =====

    [Fact]
    public async Task WithoutTheTable_APingAnswersARetryableError()
    {
        var function = Build(locations: null);

        var result = await SendAsync(Ping("fix"), function: function);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("LOCATION_");
    }

    [Fact]
    public async Task APing_NeverWritesTheTripRow()
    {
        await SendAsync(Ping("fix"));

        _written.Should().ContainSingle();
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    // ===== Helpers =====

    private SyncFunction Build(ITripLocationRepository? locations) =>
        new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            orderRepo: _orders, deliveryRepo: _deliveries, tripLocationRepo: locations)
        {
            LocationClock = () => Now
        };

    private static SyncOperationRequest Ping(
        string kind, DateTime? capturedAt = null, string action = "create", Action<Dictionary<string, object>>? data = null)
    {
        var captured = capturedAt ?? Captured;
        var ms = new DateTimeOffset(captured).ToUnixTimeMilliseconds();
        var payload = new Dictionary<string, object>
        {
            ["tripId"] = TripId,
            ["kind"] = kind,
            ["capturedAt"] = captured.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        };
        if (kind == "fix")
        {
            payload["source"] = "hourly";
            payload["latitude"] = 8.7523;
            payload["longitude"] = 38.9785;
            payload["accuracyM"] = 35;
            payload["mocked"] = false;
        }

        data?.Invoke(payload);
        var entityId = $"{TripId}#{ms.ToString(CultureInfo.InvariantCulture)}";
        return new SyncOperationRequest
        {
            OperationId = "op-ping",
            IdempotencyKey = $"{TruckerId}:location_ping:{entityId}:{action}:{ms.ToString(CultureInfo.InvariantCulture)}",
            EntityType = "location_ping",
            EntityId = entityId,
            Action = action,
            ClientVersion = 1,
            Data = payload,
            Timestamp = "2026-10-27T08:42:00.000Z"
        };
    }

    private void TripOwnedBy(string truckerId) =>
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["tripId"] = new AttributeValue { S = TripId },
                        ["truckerId"] = new AttributeValue { S = truckerId },
                        ["status"] = new AttributeValue { S = "inTransit" }
                    }
                }
            });

    private void DeliveriesOnTrip(params Dictionary<string, object>[] rows) =>
        _deliveries.QueryDeliveriesAsync(
                Arg.Any<string?>(), Arg.Is<string?>(TripId), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((rows.ToList(), (Dictionary<string, AttributeValue>?)null));

    private void HourHolds(IEnumerable<string> sortKeys) =>
        _locations.ListSortKeysAsync(TripId, "2026-10-27T08:").Returns(sortKeys.ToList());

    /// <summary>A delivery row as the deliveries repository returns it (strings).</summary>
    private static Dictionary<string, object> Delivery(
        string status, string? pickedUpAt = "2026-10-27T05:00:00Z", string? confirmedAt = null)
    {
        var row = new Dictionary<string, object>
        {
            ["deliveryId"] = Guid.NewGuid().ToString(),
            ["tripId"] = TripId,
            ["truckerId"] = TruckerId,
            ["status"] = status,
            ["createdAt"] = "2026-10-27T04:00:00Z",
            ["updatedAt"] = "2026-10-27T08:59:00Z"
        };
        if (status != "pending" && pickedUpAt != null)
        {
            row["pickedUpAt"] = pickedUpAt;
        }

        if (confirmedAt != null)
        {
            row["confirmedAt"] = confirmedAt;
        }

        return row;
    }

    private static Dictionary<string, object> Shape(string shape) => shape switch
    {
        "pending" => Delivery("pending"),
        "cancelled-before-pickup" => Delivery("cancelled", pickedUpAt: null),
        "delivered-earlier" => Delivery("delivered", confirmedAt: "2026-10-27T07:00:00Z"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    /// <summary>A pending delivery of the trucker's that a cancel can take to cancelled (a delivery write).</summary>
    private Dictionary<string, AttributeValue> CancellableDelivery()
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = new() { S = "del-2" },
            ["tripId"] = new() { S = TripId },
            ["orderId"] = new() { S = "order-2" },
            ["truckerId"] = new() { S = TruckerId },
            ["buyerId"] = new() { S = BuyerId },
            ["status"] = new() { S = "pending" },
            ["createdAt"] = new() { S = "2026-10-27T04:00:00Z" },
            ["version"] = new() { N = "1" }
        };
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable
                && r.ExpressionAttributeValues.ContainsKey(":id")
                && r.ExpressionAttributeValues[":id"].S == "del-2"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { row } });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(_ => new UpdateItemResponse
        {
            Attributes = new Dictionary<string, AttributeValue>(row) { ["status"] = new() { S = "cancelled" } }
        });
        return row;
    }

    private static SyncOperationRequest CancelOp(Dictionary<string, AttributeValue> delivery) => new()
    {
        OperationId = "op-cancel",
        IdempotencyKey = $"{TruckerId}:delivery:{delivery["deliveryId"].S}:update:1",
        EntityType = "delivery",
        EntityId = delivery["deliveryId"].S,
        Action = "update",
        Data = new Dictionary<string, object> { ["status"] = "cancelled", ["version"] = 1 },
        Timestamp = "2026-10-27T08:40:00.000Z"
    };

    private Task NothingWritten() => _locations.DidNotReceive().TryPutAsync(Arg.Any<TripLocationRow>());

    private async Task<JsonElement> SendAsync(SyncOperationRequest operation, string userId = TruckerId, SyncFunction? function = null)
        => (await SendWithAsync(function, userId, operation))[0];

    private Task<List<JsonElement>> SendBatchAsync(string userId, params SyncOperationRequest[] operations)
        => SendWithAsync(null, userId, operations);

    private async Task<List<JsonElement>> SendWithAsync(SyncFunction? function, string userId, params SyncOperationRequest[] operations)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-lt-05",
                ClientId = "device-001",
                Operations = operations.ToList()
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", "trucker" } }
            }
        };

        var response = await (function ?? _function).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")
            .EnumerateArray().Select(r => r.Clone()).ToList();
    }
}
