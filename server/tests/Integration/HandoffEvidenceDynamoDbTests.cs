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
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-440, GCR-01 and GCR-02 against real DynamoDB (LocalStack in CI): the pickup and the drop-off write their
/// evidence (GPS status, the fix's time and accuracy, QR or code, the app, the drop-off's phone time) on the delivery
/// row in the transition's own update, the stored points keep only latitude and longitude, and no stamp is in what
/// the client gets back. An old client's handoff is stored as before, with only the proven method.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HandoffEvidenceDynamoDbTests
{
    private const string CreatedAt = "2026-11-26T06:00:00.0000000Z";
    private const string Nonce = "nonce-fup440";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly string _truckerId = $"trucker-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HandoffEvidenceDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    /// <summary>The sync Lambda over the real tables; no QR secret store, so the nonce on the row is checked.</summary>
    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            userRepo: Substitute.For<IUserRepository>(), orderRepo: new OrderRepository(Ddb, _db.Orders),
            transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit));
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private async Task<string> SeedPendingAsync()
    {
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId),
                ["tripId"] = S($"trip-{Guid.NewGuid():N}"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["participantId"] = S(_buyerId),
                ["status"] = S("pending"),
                ["qrNonce"] = S(Nonce),
                ["createdAt"] = S(CreatedAt),
                ["version"] = new AttributeValue { N = "1" }
            }
        });
        return deliveryId;
    }

    private async Task<Dictionary<string, AttributeValue>> RowAsync(string deliveryId)
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

    private async Task<JsonElement> SendAsync(string deliveryId, string userId, Dictionary<string, object> data,
        string? platform = null, string? timestamp = null)
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
                        IdempotencyKey = $"{userId}:delivery:{deliveryId}:update:{Guid.NewGuid():N}",
                        EntityType = "delivery",
                        EntityId = deliveryId,
                        Action = "update",
                        Timestamp = timestamp,
                        Platform = platform,
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static void AssertNoStampIn(JsonElement result)
        => result.GetProperty("serverData").EnumerateObject().Select(p => p.Name)
            .Should().NotContain(HandoffEvidence.ServerOnlyFields);

    [LocalDynamoDbFact]
    public async Task BothLegs_StoreTheirEvidenceOnTheRow_AndNoneOfItReachesTheClient()
    {
        var deliveryId = await SeedPendingAsync();
        var fixAt = Iso(DateTime.UtcNow.AddMinutes(-3));
        var dropoffPhoneAt = Iso(DateTime.UtcNow.AddMinutes(-90));

        var pickup = await SendAsync(deliveryId, _truckerId, new Dictionary<string, object>
        {
            ["status"] = "pickedUp",
            ["version"] = 1,
            ["scannedQrNonce"] = Nonce,
            ["gpsStatus"] = "fix",
            ["gpsPickup"] = Json($"{{\"latitude\":9.0,\"longitude\":38.7,\"capturedAt\":\"{fixAt}\",\"accuracyM\":14.5}}")
        }, platform: "android", timestamp: Iso(DateTime.UtcNow.AddMinutes(-2)));
        pickup.GetProperty("status").GetString().Should().Be("success", pickup.ToString());
        AssertNoStampIn(pickup);

        var dropoff = await SendAsync(deliveryId, _buyerId, new Dictionary<string, object>
        {
            ["status"] = "delivered",
            ["version"] = 2,
            ["scannedQrNonce"] = Nonce,
            ["gpsStatus"] = "services_off"
        }, platform: "web", timestamp: dropoffPhoneAt);
        dropoff.GetProperty("status").GetString().Should().Be("success", dropoff.ToString());
        AssertNoStampIn(dropoff);

        var row = await RowAsync(deliveryId);
        row["status"].S.Should().Be("delivered");
        row["pickupGpsStatus"].S.Should().Be("fix");
        row["pickupGpsCapturedAt"].S.Should().Be(fixAt);
        row["pickupGpsAccuracyM"].N.Should().Be("14.5");
        row["pickupMethod"].S.Should().Be("qr");
        row["pickupPlatform"].S.Should().Be("android");
        row["gpsPickup"].M.Keys.Should().BeEquivalentTo("latitude", "longitude");
        row["dropoffGpsStatus"].S.Should().Be("services_off");
        row["dropoffMethod"].S.Should().Be("qr");
        row["dropoffPlatform"].S.Should().Be("web");
        row[HandoffEvidence.DropoffCapturedAtField].S.Should().Be(dropoffPhoneAt);
        row.Should().NotContainKeys("gpsStatus", "gpsDropoff", "dropoffGpsCapturedAt", "dropoffGpsAccuracyM");
    }

    [LocalDynamoDbFact]
    public async Task AnOldClientsPickup_IsStoredAsBefore_WithOnlyTheProvenMethod()
    {
        var deliveryId = await SeedPendingAsync();

        var pickup = await SendAsync(deliveryId, _truckerId, new Dictionary<string, object>
        {
            ["status"] = "pickedUp",
            ["version"] = 1,
            ["scannedQrNonce"] = Nonce,
            ["gpsPickup"] = Json("{\"latitude\":9.0,\"longitude\":38.7}")
        });

        pickup.GetProperty("status").GetString().Should().Be("success", pickup.ToString());
        var row = await RowAsync(deliveryId);
        row["status"].S.Should().Be("pickedUp");
        row.Keys.Where(HandoffEvidence.ServerOnlyFields.Contains).Should().BeEquivalentTo("pickupMethod");
    }
}
