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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-35: the trucker's own drop-off nonce. The trucker's phone registers it after pickup
/// (<c>dropoffQrNonce</c>, an update without a status, stored in the secret store and nowhere else);
/// the buyer's <c>delivered</c> is then checked against it, by scan or by its typed code, with its
/// own freshness, the drop-off leg's single use and a lock the trucker's new code clears. Without
/// one, the 1.22.0 path (the farmer's nonce) is unchanged; a scanned drop-off QR whose nonce has not
/// synced yet waits for it.
/// </summary>
public sealed class SyncFunctionDropoffNonceTests
{
    private const string DeliveriesTable = "test-deliveries-table";
    private const string DeliveryId = "del-esc35";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string FarmerNonce = "farmer-nonce-1";
    private const string DropoffNonce = "d.trucker-nonce-1";
    private const string OperationId = "op-esc35-1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonElement Gps =
        JsonSerializer.SerializeToElement(new { latitude = 9.0, longitude = 38.7 });

    private static readonly string DropoffCode = HandoffCode.Derive(DropoffNonce, DeliveryId);
    private static readonly string FarmerCode = HandoffCode.Derive(FarmerNonce, DeliveryId);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IEscrowRepository _escrow = Substitute.For<IEscrowRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryQrSecretRepository _qrSecrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionDropoffNonceTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _qrSecrets.PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _qrSecrets.TryTakeCodeAttemptAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<bool>())
            .Returns(new CodeAttempt(CodeAttemptOutcome.Taken, 1));
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(e => _auditRows.Add(e))).Returns(Task.CompletedTask);
    }

    // ---- the trucker registers the drop-off nonce ----

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    public async Task TheTruckersNonce_AfterPickup_IsStored_AndTheDeliveryIsNotWritten(string status)
    {
        SetupDelivery(TruckDelivery(status, version: 3));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        await _qrSecrets.Received(1).PutDropoffNonceAsync(DeliveryId, DropoffNonce);
        await _qrSecrets.DidNotReceive().PutNonceAsync(Arg.Any<string>(), Arg.Any<string>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        // The secret never rides back to a client.
        result.GetProperty("serverData").ToString().Should().NotContain(DropoffNonce);
    }

    [Fact]
    public async Task TheTruckersNonce_StoresNothingElse()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(TruckerId, "trucker", null,
            ("dropoffQrNonce", DropoffNonce), ("gpsDropoff", Gps), ("notes", "late"));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.Received(1).PutDropoffNonceAsync(DeliveryId, DropoffNonce);
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheTruckersNonce_BeforeThePickupSynced_WaitsForIt()
    {
        // The same batch can carry a pickup deferred for the farmer's nonce, then this.
        SetupDelivery(TruckDelivery("pending"));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_PENDING: Waiting for the pickup to sync. Will retry automatically.");
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TheTruckersNonce_StillPendingAfter14Days_IsRefused()
    {
        SetupDelivery(TruckDelivery("pending", createdDaysAgo: 20));

        var result = await SendAsync(TruckerId, "trucker", null, OperationId, DateTime.UtcNow.AddDays(-20),
            ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_VALIDATION: ");
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task TheTruckersNonce_AfterTheDropOff_StoresNothing(string status)
    {
        SetupDelivery(TruckDelivery(status));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("trucker-nonce-without-tag")]
    [InlineData("d.")]
    public async Task AMalformedNonce_IsRefused_AndNotStored(string nonce)
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", nonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_VALIDATION: dropoffQrNonce ");
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ATooLongNonce_IsRefused()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", "d." + new string('x', 99)));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_VALIDATION: dropoffQrNonce ");
    }

    [Theory]
    [InlineData(FarmerId, "farmer")]
    [InlineData(BuyerId, "buyer")]
    public async Task OnlyTheTrucker_MayRegisterIt(string userId, string role)
    {
        // Anyone else's value is dropped (SIM-41's rule); what is left is an update they may not make.
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(userId, role, null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_UNAUTHORIZED: ");
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ANonceRidingOnAStatusChange_IsDropped_AndTheChangeGoesOn()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(TruckerId, "trucker", "inTransit", ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ARowWithoutTheFarmersNonce_StoresNothing_AndAnswersSuccess()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        _qrSecrets.PutDropoffNonceAsync(DeliveryId, DropoffNonce).Returns(false);

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutTheSecretStore_NothingIsStored()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendRawAsync(null, TruckerId, "trucker", OperationId, null, null,
            ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ---- the buyer's drop-off with the trucker's nonce stored ----

    [Fact]
    public async Task TheTruckersQr_ConfirmsTheDelivery_AndConsumesTheDropOffLeg()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "delivered", OperationId);
        _auditRows.Should().Contain(e => (string)e["entityType"] == "delivery"
            && (string)e["toState"] == "delivered" && (string)e["metadata"] == "handoff=qr");
    }

    [Fact]
    public async Task OnceTheTruckerHasANonce_TheFarmersTokenNoLongerConfirms()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", FarmerNonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_MISMATCH: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheTruckersTypedCode_ConfirmsTheDelivery_CountedAgainstTheTruckersNonce()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(dropoffNonce: DropoffNonce);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", DropoffCode), ("gpsDropoff", Gps));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var attemptId = HandoffCode.AttemptId(OperationId, DropoffCode);
        await _qrSecrets.Received(1).TryTakeCodeAttemptAsync(
            DeliveryId, "delivered", attemptId, DropoffNonce, HandoffCode.MaxMisses, true);
        await _qrSecrets.Received(1).ReturnCodeAttemptAsync(DeliveryId, "delivered", attemptId);
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "delivered", OperationId);
    }

    [Fact]
    public async Task TheFarmersCode_IsAMismatch_OnceTheTruckerHasANonce()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(dropoffNonce: DropoffNonce);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", FarmerCode), ("gpsDropoff", Gps));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_MISMATCH: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ALockedTruckersCode_IsRenewable_SoItIsNotTheDropOffLock()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(dropoffNonce: DropoffNonce, missesDelivered: HandoffCode.MaxMisses);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", DropoffCode), ("gpsDropoff", Gps));

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_CODE_LOCKED: Too many wrong codes for this drop-off. Scan the trucker's drop-off QR, or ask the trucker for a new code.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task OnceLocked_TheTruckersQrStillWorks()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(dropoffNonce: DropoffNonce, missesDelivered: HandoffCode.MaxMisses);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task TheTruckersNonce_HasItsOwnFreshness()
    {
        // The farmer's nonce is three weeks old; the trucker's code was made yesterday.
        SetupDelivery(TruckDelivery("inTransit", createdDaysAgo: 22));
        SetupSecret(dropoffNonce: DropoffNonce, issuedAt: DateTime.UtcNow.AddDays(-21),
            dropoffIssuedAt: DateTime.UtcNow.AddDays(-1));

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task AStaleTruckersNonce_IsExpired_AndSendsTheBuyerToTheTrucker()
    {
        SetupDelivery(TruckDelivery("inTransit", createdDaysAgo: 16));
        SetupSecret(dropoffNonce: DropoffNonce, issuedAt: DateTime.UtcNow.AddDays(-1),
            dropoffIssuedAt: DateTime.UtcNow.AddDays(-15));

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("message").GetString().Should()
            .Be("DELIVERY_NONCE_EXPIRED: QR code has expired. Ask the trucker for a new drop-off code.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AConsumedDropOff_IsAReplay_WithTheTruckersNonceToo()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce, consumedDelivered: "op-earlier");

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_REPLAY: ");
        await AssertNothingWrittenAsync();
    }

    // ---- the buyer's drop-off without a trucker's nonce ----

    [Fact]
    public async Task WithoutATruckersNonce_TheFarmersTokenStillConfirms()
    {
        // 1.22.0: a trucker app that issues none shows the farmer's token it kept from the pickup.
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret();

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", FarmerNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "delivered", OperationId);
    }

    [Fact]
    public async Task WithoutATruckersNonce_ALockedCode_KeepsTheDropOffLock()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(missesDelivered: HandoffCode.MaxMisses);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", FarmerCode), ("gpsDropoff", Gps));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_LOCKED_DROPOFF: ");
    }

    [Fact]
    public async Task ATruckersQrScannedBeforeItsNonceSynced_WaitsForIt()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret();

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_NONCE_PENDING: Waiting for the trucker's drop-off QR code to sync. Will retry automatically.");
        await AssertNothingWrittenAsync();
        await _qrSecrets.DidNotReceive().TryTakeCodeAttemptAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task ATruckersQrWhoseNonceNeverCame_IsRefusedAfter14Days()
    {
        SetupDelivery(TruckDelivery("inTransit", createdDaysAgo: 20));
        SetupSecret();

        var result = await SendAsync(BuyerId, "buyer", "delivered", OperationId, DateTime.UtcNow.AddDays(-15),
            ("scannedQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith(
            "DELIVERY_NONCE_MISSING: The trucker's drop-off QR code never reached the server.");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AnUntaggedWrongScan_IsStillAMismatch_NotAWait()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret();

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrNonce", "someone-elses-nonce"));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_MISMATCH: ");
    }

    // ---- legs and deliveries the trucker's nonce does not touch ----

    [Fact]
    public async Task ThePickup_IsAlwaysCheckedAgainstTheFarmersNonce()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(dropoffNonce: DropoffNonce, consumedPickedUp: null);

        var result = await SendAsync(TruckerId, "trucker", "pickedUp", ("scannedQrNonce", FarmerNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, "pickedUp", OperationId);
    }

    [Fact]
    public async Task ASelfPickup_IsAlwaysCheckedAgainstTheFarmersNonce()
    {
        var item = TruckDelivery("pending");
        item["truckerId"] = new AttributeValue { S = "" };
        item["deliveryType"] = new AttributeValue { S = "self_pickup" };
        SetupDelivery(item);
        SetupSecret(dropoffNonce: DropoffNonce, missesDelivered: HandoffCode.MaxMisses);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", FarmerCode), ("gpsDropoff", Gps));

        // The farmer's lock and its way out, not the trucker's.
        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_CODE_LOCKED: Too many wrong codes for this delivery. Scan the QR code, or ask the farmer for a new code.");
    }

    // ---- helpers ----

    private void SetupSecret(
        string? dropoffNonce = null,
        int missesDelivered = 0,
        DateTime? issuedAt = null,
        DateTime? dropoffIssuedAt = null,
        string? consumedDelivered = null,
        string? consumedPickedUp = "op-pickup") =>
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = FarmerNonce,
            IssuedAt = (issuedAt ?? DateTime.UtcNow.AddDays(-1)).ToString("o"),
            ConsumedPickedUpOp = consumedPickedUp,
            ConsumedDeliveredOp = consumedDelivered,
            DropoffNonce = dropoffNonce,
            DropoffIssuedAt = dropoffNonce == null ? null : (dropoffIssuedAt ?? DateTime.UtcNow.AddHours(-2)).ToString("o"),
            CodeMissesDelivered = missesDelivered
        });

    private void SetupDelivery(Dictionary<string, AttributeValue> item)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        // The drop-off GPS check reads the trip: none here, so no proximity rule applies.
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = item });
    }

    private static Dictionary<string, AttributeValue> TruckDelivery(string status, int version = 1, int createdDaysAgo = 1)
    {
        var created = DateTime.UtcNow.AddDays(-createdDaysAgo).ToString("o");
        return new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-esc35" } },
            { "listingId", new AttributeValue { S = "listing-esc35" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = version.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
        };
    }

    private async Task AssertNothingWrittenAsync()
    {
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _qrSecrets.DidNotReceive().MarkConsumedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    private Task<JsonElement> SendAsync(string userId, string role, string? status,
        params (string Key, object Value)[] extra) =>
        SendRawAsync(_qrSecrets, userId, role, OperationId, status, null, extra);

    private Task<JsonElement> SendAsync(string userId, string role, string? status, string operationId,
        DateTime timestamp, params (string Key, object Value)[] extra) =>
        SendRawAsync(_qrSecrets, userId, role, operationId, status, timestamp, extra);

    private async Task<JsonElement> SendRawAsync(IDeliveryQrSecretRepository? store, string userId, string role,
        string operationId, string? status, DateTime? timestamp, params (string Key, object Value)[] extra)
    {
        var function = new SyncFunction(_syncMeta, Substitute.For<ICropRepository>(), _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(),
            orderRepo: _orders, escrowRepo: _escrow, qrSecretRepo: store,
            transactionAuditRepo: _audit);

        var data = new Dictionary<string, object> { { "version", 1 } };
        if (status != null)
        {
            data["status"] = status;
        }

        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc35-1",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = operationId,
                        IdempotencyKey = $"{userId}:delivery:{DeliveryId}:update:{Guid.NewGuid():N}",
                        EntityType = "delivery",
                        EntityId = DeliveryId,
                        Action = "update",
                        Timestamp = (timestamp ?? DateTime.UtcNow).ToString("o"),
                        Data = data
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
