using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-440, GCR-01 and GCR-02 (delivery-verification-api.md 2.19.0, sync-protocol.md 1.71.0): the trucker's pickup,
/// the buyer's drop-off and the self-pickup confirm stamp their evidence server-only on the delivery, in the
/// transition's own write: the GPS status, the fix's own time and accuracy, the method the nonce check proved, the
/// operation's platform and, for the drop-off, the phone's time of the operation. A bad value is left out and the
/// handoff goes through; an old client is accepted with only the method stamped; no stamp ever reaches a client, and a
/// client cannot write one.
/// </summary>
public sealed class SyncFunctionHandoffEvidenceTests
{
    private const string DeliveriesTable = "test-deliveries-table";
    private const string DeliveryId = "del-fup440";
    private const string OrderId = "order-fup440";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string Nonce = "farmer-nonce-440";
    private const string OperationId = "op-evidence-1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IEscrowRepository _escrow = Substitute.For<IEscrowRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryQrSecretRepository _qrSecrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly List<UpdateItemRequest> _writes = new();
    private readonly TestLambdaContext _context = new();

    private static readonly string Code = HandoffCode.Derive(Nonce, DeliveryId);

    public SyncFunctionHandoffEvidenceTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _qrSecrets.TryTakeCodeAttemptAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new CodeAttempt(CodeAttemptOutcome.Taken, 1));
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = Nonce,
            IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o")
        });
        // The listing's registered place, which the pickup and a self-pickup are measured against.
        _crops.GetByIdAsync("listing-fup440").Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-fup440" }, { "farmerId", FarmerId }, { "latitude", "9.0" }, { "longitude", "38.7" }
        });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _dynamoDb.When(d => d.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>()))
            .Do(ci => _writes.Add(ci.Arg<UpdateItemRequest>()));
    }

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>A point at the farm (inside the listing's 5 km), with the new keys as the apps send them.</summary>
    private static JsonElement Point(string extra = "")
        => Json("{\"latitude\":9.0,\"longitude\":38.7" + (extra.Length > 0 ? "," + extra : "") + "}");

    private static JsonElement Text(string value) => Json(JsonSerializer.Serialize(value));

    // ──────────────────────────────────────────────
    // Each leg
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ThePickup_StampsItsEvidence_InTheTransitionsWrite_AndTheStoredPointStaysLatitudeAndLongitude()
    {
        SetupDelivery(TruckDelivery("pending"));
        var fixAt = Iso(DateTime.UtcNow.AddMinutes(-2));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", platform: "android",
            timestamp: Iso(DateTime.UtcNow.AddMinutes(-1)),
            data: new()
            {
                ["scannedQrNonce"] = Text(Nonce),
                ["gpsStatus"] = Text("fix"),
                ["gpsPickup"] = Point($"\"capturedAt\":\"{fixAt}\",\"accuracyM\":12.5")
            });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var write = TheWrite();
        var stamped = Stamped(write);
        stamped["pickupGpsStatus"].S.Should().Be("fix");
        stamped["pickupGpsCapturedAt"].S.Should().Be(fixAt);
        stamped["pickupGpsAccuracyM"].N.Should().Be("12.5");
        stamped["pickupMethod"].S.Should().Be("qr");
        stamped["pickupPlatform"].S.Should().Be("android");
        // The transition, the existing pickup stamps and the evidence are one write.
        stamped.Should().ContainKeys("status", "pickedUpAt", TransactionPatternRules.PickupCapturedAtField);
        stamped.Keys.Should().NotContain(k => k.StartsWith("dropoff", StringComparison.Ordinal));
        // The fix's time and accuracy are not kept inside the point, and gpsStatus is never stored raw.
        stamped["gpsPickup"].M.Keys.Should().BeEquivalentTo("latitude", "longitude");
        stamped.Should().NotContainKey("gpsStatus");
    }

    [Fact]
    public async Task TheDropoff_StampsTheCauseOfNoFix_ThePlatform_AndThePhonesTimeOfTheConfirm()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        var phoneAt = Iso(DateTime.UtcNow.AddHours(-5));

        var result = await SendAsync("delivered", BuyerId, "buyer", platform: "web", timestamp: phoneAt,
            data: new() { ["scannedQrNonce"] = Text(Nonce), ["gpsStatus"] = Text("denied_forever") });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var stamped = Stamped(TheWrite());
        stamped["dropoffGpsStatus"].S.Should().Be("denied_forever");
        stamped["dropoffMethod"].S.Should().Be("qr");
        stamped["dropoffPlatform"].S.Should().Be("web");
        stamped[HandoffEvidence.DropoffCapturedAtField].S.Should().Be(phoneAt);
        stamped.Should().ContainKey("confirmedAt");
        stamped.Should().NotContainKeys("dropoffGpsCapturedAt", "dropoffGpsAccuracyM", "gpsDropoff");
        stamped.Keys.Should().NotContain(k => k.StartsWith("pickup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSelfPickupConfirm_IsStampedAsTheDropoffLeg_WhereItsPointIsStored()
    {
        SetupDelivery(SelfPickupDelivery());
        var fixAt = Iso(DateTime.UtcNow.AddSeconds(-20));
        var phoneAt = Iso(DateTime.UtcNow.AddSeconds(-10));

        var result = await SendAsync("delivered", BuyerId, "buyer", platform: "android", timestamp: phoneAt,
            data: new()
            {
                ["scannedQrNonce"] = Text(Nonce),
                ["gpsStatus"] = Text("approximate"),
                ["gpsDropoff"] = Point($"\"capturedAt\":\"{fixAt}\",\"accuracyM\":1800")
            });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var stamped = Stamped(TheWrite());
        stamped["dropoffGpsStatus"].S.Should().Be("approximate");
        stamped["dropoffGpsCapturedAt"].S.Should().Be(fixAt);
        stamped["dropoffGpsAccuracyM"].N.Should().Be("1800");
        stamped["dropoffMethod"].S.Should().Be("qr");
        stamped["dropoffPlatform"].S.Should().Be("android");
        stamped[HandoffEvidence.DropoffCapturedAtField].S.Should().Be(phoneAt);
        stamped["gpsDropoff"].M.Keys.Should().BeEquivalentTo("latitude", "longitude");
        stamped.Keys.Should().NotContain(k => k.StartsWith("pickup", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pending", "pickedUp", TruckerId, "trucker", "pickupMethod", "scannedQrNonce", "qr")]
    [InlineData("pending", "pickedUp", TruckerId, "trucker", "pickupMethod", "scannedQrCode", "code")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer", "dropoffMethod", "scannedQrNonce", "qr")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer", "dropoffMethod", "scannedQrCode", "code")]
    public async Task TheMethod_IsTheOneTheNonceCheckProved(
        string from, string to, string userId, string role, string field, string sent, string method)
    {
        SetupDelivery(TruckDelivery(from));

        var result = await SendAsync(to, userId, role,
            data: new() { [sent] = Text(sent == "scannedQrCode" ? Code : Nonce) });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Stamped(TheWrite())[field].S.Should().Be(method);
    }

    [Theory]
    [InlineData("android", "android")]
    [InlineData("web", "web")]
    [InlineData(null, null)]
    [InlineData("ios", null)]
    [InlineData("WEB", null)]
    [InlineData("android; drop table", null)]
    public async Task ThePlatform_IsStampedOnlyWhenKnown_AndAnyOtherIsIgnored(string? sent, string? stamped)
    {
        SetupDelivery(TruckDelivery("pending"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", platform: sent,
            data: new() { ["scannedQrNonce"] = Text(Nonce) });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Stamped(TheWrite()).TryGetValue("pickupPlatform", out var value).Should().Be(stamped != null);
        value?.S.Should().Be(stamped);
    }

    // ──────────────────────────────────────────────
    // Bad values are dropped, never refused
    // ──────────────────────────────────────────────

    public static IEnumerable<object[]> BadEvidence()
    {
        // A status outside the six values, or one that does not fit the point that came with it.
        yield return new object[] { "gpsStatus", "\"gps_off\"", "", "dropoffGpsStatus" };
        yield return new object[] { "gpsStatus", "\"fix\"", "", "dropoffGpsStatus" };
        yield return new object[] { "gpsStatus", "\"denied\"", "{\"latitude\":9.0,\"longitude\":38.7}", "dropoffGpsStatus" };
        yield return new object[] { "gpsStatus", "7", "", "dropoffGpsStatus" };
        // A fix's accuracy that is negative, too large, not finite or not a number.
        yield return new object[] { "accuracyM", "-3", "", "dropoffGpsAccuracyM" };
        yield return new object[] { "accuracyM", "250000", "", "dropoffGpsAccuracyM" };
        yield return new object[] { "accuracyM", "1e400", "", "dropoffGpsAccuracyM" };
        yield return new object[] { "accuracyM", "\"12\"", "", "dropoffGpsAccuracyM" };
        // A fix's time that cannot be read, is ahead of the server or is older than 30 days.
        yield return new object[] { "capturedAt", "\"soon\"", "", "dropoffGpsCapturedAt" };
        yield return new object[] { "capturedAt", JsonSerializer.Serialize(Iso(DateTime.UtcNow.AddMinutes(30))), "", "dropoffGpsCapturedAt" };
        yield return new object[] { "capturedAt", JsonSerializer.Serialize(Iso(DateTime.UtcNow.AddDays(-40))), "", "dropoffGpsCapturedAt" };
    }

    [Theory]
    [MemberData(nameof(BadEvidence))]
    public async Task ABadEvidenceValue_IsLeftOut_AndTheHandoffGoesThrough(
        string key, string json, string pointJson, string field)
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        var data = new Dictionary<string, object> { ["scannedQrNonce"] = Text(Nonce) };
        if (key == "gpsStatus")
        {
            data["gpsStatus"] = Json(json);
            if (pointJson.Length > 0)
            {
                data["gpsDropoff"] = Json(pointJson);
            }
        }
        else
        {
            data["gpsStatus"] = Text("fix");
            data["gpsDropoff"] = Point($"\"{key}\":{json}");
        }

        var result = await SendAsync("delivered", BuyerId, "buyer", data: data);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var stamped = Stamped(TheWrite());
        stamped.Should().NotContainKey(field);
        stamped.Should().ContainKeys("confirmedAt", "dropoffMethod");
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("+10m")]
    [InlineData("-40d")]
    public async Task AnImplausibleDropoffPhoneTime_IsLeftOut(string timestamp)
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        var sent = timestamp switch
        {
            "+10m" => Iso(DateTime.UtcNow.AddMinutes(10)),
            "-40d" => Iso(DateTime.UtcNow.AddDays(-40)),
            _ => timestamp
        };

        var result = await SendAsync("delivered", BuyerId, "buyer", timestamp: sent,
            data: new() { ["scannedQrNonce"] = Text(Nonce) });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Stamped(TheWrite()).Should().NotContainKey(HandoffEvidence.DropoffCapturedAtField);
    }

    // ──────────────────────────────────────────────
    // Old clients, other updates
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("pending", "pickedUp", TruckerId, "trucker", "gpsPickup", "pickupMethod")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer", "gpsDropoff", "dropoffMethod")]
    public async Task AnOldClientsHandoff_IsAccepted_WithOnlyTheProvenMethodStamped(
        string from, string to, string userId, string role, string pointField, string methodField)
    {
        SetupDelivery(TruckDelivery(from));

        // No gpsStatus, no point keys, no platform, no timestamp: what the shipped apps send.
        var result = await SendAsync(to, userId, role,
            data: new() { ["scannedQrNonce"] = Text(Nonce), [pointField] = Point() });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var evidence = Stamped(TheWrite()).Keys.Where(HandoffEvidence.ServerOnlyFields.Contains).ToList();
        evidence.Should().BeEquivalentTo(methodField);
    }

    [Fact]
    public async Task AGpsStatusOnAnUpdateThatConfirmsNoHandoff_IsAccepted_AndNothingIsStamped()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync("inTransit", TruckerId, "trucker", platform: "android",
            timestamp: Iso(DateTime.UtcNow), data: new() { ["gpsStatus"] = Text("timeout") });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var stamped = Stamped(TheWrite());
        stamped.Keys.Should().NotContain(HandoffEvidence.ServerOnlyFields);
        stamped.Should().NotContainKey("gpsStatus");
    }

    // ──────────────────────────────────────────────
    // Server-only
    // ──────────────────────────────────────────────

    public static IEnumerable<object[]> StampFields() => HandoffEvidence.ServerOnlyFields.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(StampFields))]
    public async Task AClientWritingAStamp_IsRefusedAsAnUnknownField_AndNothingIsWritten(string field)
    {
        SetupDelivery(TruckDelivery("pending"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker",
            data: new() { ["scannedQrNonce"] = Text(Nonce), [field] = Text("qr") });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be($"DELIVERY_VALIDATION: Unknown fields: {field}");
        _writes.Should().BeEmpty();
        await _qrSecrets.DidNotReceive().MarkConsumedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TheStamps_NeverRideBackToTheClient_OnSuccess()
    {
        var stored = WithEveryStamp(TruckDelivery("delivered"));
        SetupDelivery(TruckDelivery("pickedUp"), writtenRow: stored);

        var result = await SendAsync("delivered", BuyerId, "buyer", platform: "web", timestamp: Iso(DateTime.UtcNow),
            data: new() { ["scannedQrNonce"] = Text(Nonce), ["gpsStatus"] = Text("timeout") });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        AssertNoStamp(result.GetProperty("serverData"));
    }

    [Fact]
    public async Task TheStamps_NeverRideBackToTheClient_OnAConflict()
    {
        SetupDelivery(WithEveryStamp(TruckDelivery("pickedUp")));
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("version moved"));

        var result = await SendAsync("delivered", BuyerId, "buyer",
            data: new() { ["scannedQrNonce"] = Text(Nonce) });

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        AssertNoStamp(result.GetProperty("serverData"));
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static void AssertNoStamp(JsonElement serverData)
    {
        serverData.ValueKind.Should().Be(JsonValueKind.Object);
        var names = serverData.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().Contain("deliveryId");
        names.Should().NotContain(HandoffEvidence.ServerOnlyFields);
    }

    private static Dictionary<string, AttributeValue> WithEveryStamp(Dictionary<string, AttributeValue> row)
    {
        foreach (var field in HandoffEvidence.ServerOnlyFields)
        {
            row[field] = field.EndsWith("AccuracyM", StringComparison.Ordinal)
                ? new AttributeValue { N = "12" }
                : new AttributeValue { S = "stamped" };
        }

        return row;
    }

    private UpdateItemRequest TheWrite() => _writes.Should().ContainSingle().Subject;

    /// <summary>The written attributes of a delivery write, by name: each <c>#fN</c> with its <c>:fN</c>.</summary>
    private static Dictionary<string, AttributeValue> Stamped(UpdateItemRequest write)
        => write.ExpressionAttributeNames
            .Where(n => n.Key.StartsWith("#f", StringComparison.Ordinal))
            .ToDictionary(n => n.Value, n => write.ExpressionAttributeValues[":" + n.Key[1..]]);

    private void SetupDelivery(Dictionary<string, AttributeValue> item, Dictionary<string, AttributeValue>? writtenRow = null)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = writtenRow ?? item });
    }

    private static Dictionary<string, AttributeValue> TruckDelivery(string status)
    {
        var created = DateTime.UtcNow.AddDays(-1).ToString("o");
        return new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-fup440" } },
            { "listingId", new AttributeValue { S = "listing-fup440" } },
            { "orderId", new AttributeValue { S = OrderId } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = "1" } }
        };
    }

    private static Dictionary<string, AttributeValue> SelfPickupDelivery()
    {
        var item = TruckDelivery("pending");
        item["truckerId"] = new AttributeValue { S = "" };
        item["tripId"] = new AttributeValue { S = DeliveryId };
        item["deliveryType"] = new AttributeValue { S = "self_pickup" };
        return item;
    }

    private async Task<JsonElement> SendAsync(string? status, string userId, string role,
        Dictionary<string, object> data, string? platform = null, string? timestamp = null)
    {
        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(),
            orderRepo: _orders, escrowRepo: _escrow, qrSecretRepo: _qrSecrets);

        var payload = new Dictionary<string, object>(data) { ["version"] = 1 };
        if (status != null)
        {
            payload["status"] = status;
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-evidence-1",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = OperationId,
                        IdempotencyKey = $"{userId}:delivery:{DeliveryId}:update:{Guid.NewGuid():N}",
                        EntityType = "delivery",
                        EntityId = DeliveryId,
                        Action = "update",
                        Timestamp = timestamp,
                        Platform = platform,
                        Data = payload
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId },
                    { "custom:role", role }
                }
            }
        };

        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
