using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-14 (delivery-verification-api.md 2.1.0, GPS Validation; order-lifecycle.md 2.9.0): a trucker
/// delivery's <c>gpsDropoff</c> passes within 5 km of EITHER the trip's destination point OR the order's
/// own drop-off point (read strongly consistent); with neither it is not checked. Uses the delivery rig
/// of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private const string GpsOrderId = "order-lt14";

    // Adama (the order's point) and Addis Ababa (the trip's destination), about 75 km apart.
    private const double AdamaLat = 8.5423;
    private const double AdamaLng = 39.2687;
    private const double AddisLat = 9.03;
    private const double AddisLng = 38.74;

    [Fact]
    public async Task Delivered_NearTheOrdersOwnPoint_PassesTheGpsCheck_FarFromTheTripsDestination()
    {
        var body = await ConfirmWithGpsAsync(AdamaLat + 0.01, AdamaLng, OrderPoint(AdamaLat, AdamaLng), (AddisLat, AddisLng));

        body.Should().Contain("\"status\":\"success\"", body);
        // The order decides, read strongly consistent.
        await _mockOrderRepo.Received().GetByIdAsync(GpsOrderId, true);
    }

    [Fact]
    public async Task Delivered_AtTheTripsDestination_FarFromTheOrdersPoint_StillPasses()
    {
        // The order's point defaults to the buyer's registered place; a buyer who met the truck at its
        // destination is not stranded by it.
        var body = await ConfirmWithGpsAsync(AddisLat, AddisLng, OrderPoint(AdamaLat, AdamaLng), (AddisLat, AddisLng));

        body.Should().Contain("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task Delivered_FarFromBothPoints_IsRefused_WithTheDistanceToTheNearer()
    {
        // Mojo: about 16 km from Adama (the order's point), about 60 km from Addis Ababa (the trip's).
        var body = await ConfirmWithGpsAsync(8.5872, 39.1213, OrderPoint(AdamaLat, AdamaLng), (AddisLat, AddisLng));

        var result = JsonDocument.Parse(body).RootElement.GetProperty("results")[0];
        result.GetProperty("status").GetString().Should().Be("error");
        var message = result.GetProperty("message").GetString()!;
        message.Should().MatchRegex(@"^DELIVERY_GPS_PROXIMITY: Dropoff GPS is \d+\.\dkm from the drop-off point \(max 5km\)\.$");
        var km = double.Parse(message.Split(" is ")[1].Split("km")[0], CultureInfo.InvariantCulture);
        km.Should().BeInRange(10, 25);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }

    [Fact]
    public async Task Delivered_OnAnOrderWithoutAPoint_IsCheckedAgainstTheTripsDestination()
    {
        var far = await ConfirmWithGpsAsync(AdamaLat, AdamaLng, OrderPoint(null, null), (AddisLat, AddisLng));
        JsonDocument.Parse(far).RootElement.GetProperty("results")[0].GetProperty("message").GetString().Should()
            .StartWith("DELIVERY_GPS_PROXIMITY: Dropoff GPS is ").And.EndWith("km from the drop-off point (max 5km).");

        var near = await ConfirmWithGpsAsync(AddisLat + 0.01, AddisLng, OrderPoint(null, null), (AddisLat, AddisLng));
        near.Should().Contain("\"status\":\"success\"", near);
    }

    [Fact]
    public async Task Delivered_WithNeitherPoint_IsNotChecked()
    {
        var body = await ConfirmWithGpsAsync(AdamaLat, AdamaLng, OrderPoint(null, null), tripDestination: null);

        body.Should().Contain("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task Delivered_WhenTheOrderCannotBeRead_IsARetryableError_NeverTheTripsPoint()
    {
        _mockOrderRepo.GetByIdAsync(GpsOrderId, true).ThrowsAsync(new Amazon.DynamoDBv2.AmazonDynamoDBException("throttled"));

        // Far from the trip's destination, so only the order could pass it.
        var body = await ConfirmWithGpsAsync(AdamaLat, AdamaLng, order: null, (AddisLat, AddisLng), stubOrder: false);

        var result = JsonDocument.Parse(body).RootElement.GetProperty("results")[0];
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("DELIVERY_");
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }

    /// <summary>
    /// The buyer confirms delivery of del-001 (picked up, its order <see cref="GpsOrderId"/>) with a scan
    /// and <c>gpsDropoff</c> at (<paramref name="latitude"/>, <paramref name="longitude"/>).
    /// </summary>
    private async Task<string> ConfirmWithGpsAsync(
        double latitude,
        double longitude,
        Dictionary<string, object>? order,
        (double Lat, double Lng)? tripDestination,
        bool stubOrder = true)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-lt14");
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "orderId", new AttributeValue { S = GpsOrderId } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "qrNonce", new AttributeValue { S = "nonce-lt14" } },
            { "status", new AttributeValue { S = "pickedUp" } },
            { "pickedUpAt", new AttributeValue { S = DateTime.UtcNow.AddHours(-3).ToString("o", CultureInfo.InvariantCulture) } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = "1" } }
        };
        SetupDeliveryQuery(item);

        var trip = new Dictionary<string, AttributeValue> { { "tripId", new AttributeValue { S = "trip-001" } } };
        if (tripDestination is { } dest)
        {
            trip["destinationLatitude"] = new AttributeValue { N = dest.Lat.ToString(CultureInfo.InvariantCulture) };
            trip["destinationLongitude"] = new AttributeValue { N = dest.Lng.ToString(CultureInfo.InvariantCulture) };
        }

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { trip } });
        if (stubOrder)
        {
            _mockOrderRepo.GetByIdAsync(GpsOrderId, true).Returns(order);
        }

        var op = MakeDeliveryUpdateOp(new Dictionary<string, object>
        {
            { "status", "delivered" },
            { "version", 1 },
            { "scannedQrNonce", "nonce-lt14" },
            { "gpsDropoff", new Dictionary<string, object> { { "latitude", latitude }, { "longitude", longitude } } }
        });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);
        response.StatusCode.Should().Be(200, response.Body);
        return response.Body;
    }

    /// <summary>The order of del-001, in transit, with its own drop-off point when given (stored as invariant text).</summary>
    private static Dictionary<string, object> OrderPoint(double? latitude, double? longitude)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", GpsOrderId },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "truckerId", "trucker-001" },
            { "status", "in_transit" },
            { "version", "4" }
        };
        if (latitude is { } lat && longitude is { } lng)
        {
            order["deliveryLatitude"] = lat.ToString(CultureInfo.InvariantCulture);
            order["deliveryLongitude"] = lng.ToString(CultureInfo.InvariantCulture);
        }

        return order;
    }
}
