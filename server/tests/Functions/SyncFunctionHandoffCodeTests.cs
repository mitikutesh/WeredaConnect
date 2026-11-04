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
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// T-1: the typed handoff code (<c>scannedQrCode</c>) in place of the scanned nonce. The server
/// derives the code from the stored nonce and compares it in fixed time, then applies the scan's
/// freshness, single-use and deferral rules; wrong codes are counted per leg and lock typed codes
/// at five, which the farmer's Regenerate (a new nonce) clears; a scanned QR still works when
/// locked; on a paid delivery a typed code needs the leg's GPS.
/// </summary>
public sealed class SyncFunctionHandoffCodeTests
{
    private const string DeliveriesTable = "test-deliveries-table";
    private const string DeliveryId = "del-fup42";
    private const string OrderId = "order-fup42";
    private const string OrderEscrowId = "escrow-order-" + OrderId;
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string Nonce = "farmer-nonce-1";
    private const string OperationId = "op-code-1";

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
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();

    private static readonly string Code = HandoffCode.Derive(Nonce, DeliveryId);

    /// <summary>A well-formed code (valid check digit) that is not this nonce's.</summary>
    private static readonly string WrongCode = HandoffCode.Derive("another-nonce", DeliveryId);

    private static readonly JsonElement Gps =
        JsonSerializer.SerializeToElement(new { latitude = 9.0, longitude = 38.7 });

    public SyncFunctionHandoffCodeTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        // An attempt is taken by default (the first one of the leg).
        _qrSecrets.TryTakeCodeAttemptAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new CodeAttempt(CodeAttemptOutcome.Taken, 1));
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(e => _auditRows.Add(e))).Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData("scannedQrCode", "handoff=code")]
    [InlineData("scannedQrNonce", "handoff=qr")]
    public async Task TheHandoffMethod_IsRecordedWithTheTransitionsAudit(string field, string metadata)
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();

        var result = await SendAsync("pickedUp", TruckerId, "trucker", (field, field == "scannedQrCode" ? Code : Nonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = _auditRows.Should().ContainSingle(e => (string)e["entityType"] == "delivery").Subject;
        row["toState"].Should().Be("pickedUp");
        row["metadata"].Should().Be(metadata);
    }

    [Theory]
    [InlineData("pending", "pickedUp", TruckerId, "trucker")]
    [InlineData("pickedUp", "delivered", BuyerId, "buyer")]
    public async Task TheRightCode_StandsInForTheScan_AndConsumesTheLeg(
        string from, string to, string userId, string role)
    {
        SetupDelivery(TruckDelivery(from));
        SetupSecret();

        var result = await SendAsync(to, userId, role, ("scannedQrCode", Code));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.Received(1).MarkConsumedAsync(DeliveryId, to, OperationId);
        // The attempt was taken before the comparison and given back: a right code is no miss.
        await _qrSecrets.Received(1).TryTakeCodeAttemptAsync(DeliveryId, to, OperationId, Nonce, 5);
        await _qrSecrets.Received(1).ReturnCodeAttemptAsync(DeliveryId, to, OperationId);
    }

    [Fact]
    public async Task AWrongCode_IsCountedForTheLeg_AndNothingIsWritten()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();
        Attempts(1);

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", WrongCode));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_MISMATCH: ");
        await _qrSecrets.Received(1).TryTakeCodeAttemptAsync(DeliveryId, "pickedUp", OperationId, Nonce, 5);
        // A wrong code keeps its attempt.
        await _qrSecrets.DidNotReceive().ReturnCodeAttemptAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheFifthWrongCode_LocksTypedCodes()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(missesPickedUp: 4);
        Attempts(5);

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", WrongCode));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_LOCKED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task OnceLocked_EvenTheRightCodeIsRefused_AndNotCountedAgain()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(missesPickedUp: HandoffCode.MaxMisses);

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_LOCKED: ");
        await AssertNoAttemptTakenAsync();
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AGuessRacingTheLock_LosesTheConditionalWrite_EvenWithTheRightCode()
    {
        // Parallel guesses all read four misses; the attempt write is conditional on the count,
        // so only the first gets the last attempt and this one is locked before any comparison.
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(missesPickedUp: 4);
        _qrSecrets.TryTakeCodeAttemptAsync(DeliveryId, "pickedUp", OperationId, Nonce, 5)
            .Returns(new CodeAttempt(CodeAttemptOutcome.Locked, 5));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_LOCKED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ARegenerateBetweenTheReadAndTheAttempt_IsAMismatch()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();
        _qrSecrets.TryTakeCodeAttemptAsync(DeliveryId, "pickedUp", OperationId, Nonce, 5)
            .Returns(new CodeAttempt(CodeAttemptOutcome.NonceChanged, 0));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_MISMATCH: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task OnceLocked_AScannedQrStillWorks()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(missesPickedUp: HandoffCode.MaxMisses);

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrNonce", Nonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task TheLockIsPerLeg_ThePickupsMissesDoNotLockTheDropOff()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret(missesPickedUp: HandoffCode.MaxMisses);

        var result = await SendAsync("delivered", BuyerId, "buyer", ("scannedQrCode", Code), ("gpsDropoff", Gps));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task AfterARegenerate_TheNewNoncesCodeIsChecked_AndTheOldOneMisses()
    {
        // The farmer's Regenerate stored a new nonce (and cleared the misses, see the repository
        // tests): the code of the old nonce no longer matches.
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(nonce: "farmer-nonce-2");

        var stale = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));
        stale.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_MISMATCH: ");

        var fresh = await SendAsync("pickedUp", TruckerId, "trucker",
            ("scannedQrCode", HandoffCode.Derive("farmer-nonce-2", DeliveryId)));
        fresh.GetProperty("status").GetString().Should().Be("success");
    }

    [Theory]
    [InlineData("75755597")]
    [InlineData("757555970")]
    [InlineData("75755597x")]
    public async Task AMalformedCode_IsAValidationError_AndNoMiss(string code)
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_VALIDATION: scannedQrCode");
        await AssertNoAttemptTakenAsync();
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AStaleNonce_IsExpiredForATypedCodeToo()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(issuedAt: DateTime.UtcNow.AddDays(-20));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_EXPIRED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AConsumedLeg_IsAReplayForATypedCodeToo()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret(consumedPickedUp: "op-earlier");

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_REPLAY: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheFarmersNonceNotSyncedYet_APaidDelivery_IsDeferred()
    {
        SetupDelivery(TruckDelivery("pending"));
        _qrSecrets.GetAsync(DeliveryId).Returns((DeliveryQrSecret?)null);
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow());

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_PENDING: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task APaidDelivery_ATypedCodeWithoutGps_IsRefused()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret();
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow());

        var result = await SendAsync("delivered", BuyerId, "buyer", ("scannedQrCode", Code));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_GPS_REQUIRED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task APaidDelivery_ATypedCodeWithGps_Succeeds()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        SetupSecret();
        _escrow.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow());

        var result = await SendAsync("delivered", BuyerId, "buyer", ("scannedQrCode", Code), ("gpsDropoff", Gps));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task APaidDelivery_TheEscrowCannotBeRead_IsDeferred()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();
        _escrow.GetByIdAsync(OrderEscrowId, true)
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("QR_GATE_UNDECIDED deliveryId=del-fup42");
    }

    [Fact]
    public async Task ACashDelivery_ATypedCodeWithoutGps_Succeeds()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();

        var result = await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task WithoutTheSecretStore_TypedCodesAreNotTaken()
    {
        // A legacy on-record nonce has no miss counter, so a typed code is never checked against it.
        var item = TruckDelivery("pending");
        item["qrNonce"] = new AttributeValue { S = Nonce };
        SetupDelivery(item);

        var result = await SendAsync("pickedUp", TruckerId, "trucker", qrSecrets: false, ("scannedQrCode", Code));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_LOCKED: ");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AScannedNonceWins_WhenBothAreSent()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();

        var result = await SendAsync("pickedUp", TruckerId, "trucker",
            ("scannedQrNonce", Nonce), ("scannedQrCode", WrongCode));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await AssertNoAttemptTakenAsync();
    }

    [Fact]
    public async Task TheCodeIsNeverStoredOnTheDelivery()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();
        UpdateItemRequest? write = null;
        _dynamoDb.When(d => d.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()))
            .Do(c => write = c.Arg<UpdateItemRequest>());

        await SendAsync("pickedUp", TruckerId, "trucker", ("scannedQrCode", Code));

        write.Should().NotBeNull();
        write!.ExpressionAttributeNames.Values.Should().NotContain("scannedQrCode");
        write.ExpressionAttributeValues.Values.Select(v => v.S).Should().NotContain(Code);
    }

    [Fact]
    public async Task OnlyTheTrucker_MayTypeThePickupCode_AndAStrangerBurnsNoMiss()
    {
        SetupDelivery(TruckDelivery("pending"));
        SetupSecret();

        var result = await SendAsync("pickedUp", BuyerId, "buyer", ("scannedQrCode", WrongCode));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_UNAUTHORIZED: ");
        await AssertNoAttemptTakenAsync();
    }

    // ---- helpers ----

    /// <summary>The leg's attempt count once this operation's attempt is taken.</summary>
    private void Attempts(int count) =>
        _qrSecrets.TryTakeCodeAttemptAsync(DeliveryId, "pickedUp", OperationId, Arg.Any<string>(), 5)
            .Returns(new CodeAttempt(CodeAttemptOutcome.Taken, count));

    private async Task AssertNoAttemptTakenAsync() =>
        await _qrSecrets.DidNotReceive().TryTakeCodeAttemptAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());

    private void SetupSecret(
        string nonce = Nonce,
        int missesPickedUp = 0,
        DateTime? issuedAt = null,
        string? consumedPickedUp = null) =>
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = nonce,
            IssuedAt = (issuedAt ?? DateTime.UtcNow.AddHours(-1)).ToString("o"),
            ConsumedPickedUpOp = consumedPickedUp,
            CodeMissesPickedUp = missesPickedUp
        });

    private static Dictionary<string, object> OrderEscrowRow() => new()
    {
        { "escrowId", OrderEscrowId },
        { "orderId", OrderId },
        { "source", "order" },
        { "status", "escrowed" },
        { "createdAt", "2026-10-20T08:00:00Z" }
    };

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

    private static Dictionary<string, AttributeValue> TruckDelivery(string status)
    {
        var created = DateTime.UtcNow.AddDays(-1).ToString("o");
        return new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-fup42" } },
            { "listingId", new AttributeValue { S = "listing-fup42" } },
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

    private async Task AssertNothingWrittenAsync()
    {
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _qrSecrets.DidNotReceive().MarkConsumedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    private Task<JsonElement> SendAsync(string status, string userId, string role,
        params (string Key, object Value)[] extra) => SendAsync(status, userId, role, qrSecrets: true, extra);

    private async Task<JsonElement> SendAsync(string status, string userId, string role, bool qrSecrets,
        params (string Key, object Value)[] extra)
    {
        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(),
            orderRepo: _orders, escrowRepo: _escrow, qrSecretRepo: qrSecrets ? _qrSecrets : null,
            transactionAuditRepo: _audit);

        var data = new Dictionary<string, object> { { "status", status }, { "version", 1 } };
        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-code-1",
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
