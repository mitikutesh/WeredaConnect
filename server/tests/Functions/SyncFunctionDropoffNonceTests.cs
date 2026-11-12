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
        _qrSecrets.PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(DropoffNonceOutcome.Stored);
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
        await _qrSecrets.Received(1).PutDropoffNonceAsync(DeliveryId, DropoffNonce, false);
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
        await _qrSecrets.Received(1).PutDropoffNonceAsync(DeliveryId, DropoffNonce, false);
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
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task TheTruckersNonce_StillPendingAfter14Days_IsRefused()
    {
        SetupDelivery(TruckDelivery("pending", createdDaysAgo: 20));

        var result = await SendAsync(TruckerId, "trucker", null, OperationId, DateTime.UtcNow.AddDays(-20),
            ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_VALIDATION: ");
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task TheTruckersNonce_AfterTheDropOff_StoresNothing(string status)
    {
        SetupDelivery(TruckDelivery(status));

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
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
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
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
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ANonceRidingOnAStatusChange_IsDropped_AndTheChangeGoesOn()
    {
        SetupDelivery(TruckDelivery("pickedUp"));

        var result = await SendAsync(TruckerId, "trucker", "inTransit", ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _qrSecrets.DidNotReceive().PutDropoffNonceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ARowWithoutTheFarmersNonce_StoresNothing_AndSaysSo()
    {
        SetupDelivery(TruckDelivery("pickedUp"));
        _qrSecrets.PutDropoffNonceAsync(DeliveryId, DropoffNonce, false).Returns(DropoffNonceOutcome.NoPickupNonce);

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        // Nothing to retry; web reads the message and says the code is not on the server.
        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("message").GetString().Should().StartWith("DROPOFF_CODE_NOT_STORED: ");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARegistration_NeverReplacesAStoredCode_AndSaysSo()
    {
        // Phone B (or a queued registration landing late) brings another code.
        SetupDelivery(TruckDelivery("inTransit"));
        _qrSecrets.PutDropoffNonceAsync(DeliveryId, DropoffNonce, false).Returns(DropoffNonceOutcome.OtherCodeStored);

        var result = await SendAsync(TruckerId, "trucker", null, ("dropoffQrNonce", DropoffNonce));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("message").GetString().Should().StartWith("DROPOFF_CODE_SUPERSEDED: ");
    }

    [Fact]
    public async Task OnlyTheRenewalFlag_AsksToReplace()
    {
        SetupDelivery(TruckDelivery("inTransit"));

        var result = await SendAsync(TruckerId, "trucker", null,
            ("dropoffQrNonce", DropoffNonce), ("dropoffQrRenew", true));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.TryGetProperty("message", out var message).Should().BeFalse(message.ToString());
        await _qrSecrets.Received(1).PutDropoffNonceAsync(DeliveryId, DropoffNonce, true);
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

    // ---- a stored code and a different tagged scan (review: no silent replacement) ----

    [Fact]
    public async Task ADifferentTaggedScan_TakenAfterTheStoredCode_Waits_WithoutASignal()
    {
        // The trucker's New drop-off code made offline: the buyer scanned it and synced first.
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce, dropoffIssuedAt: DateTime.UtcNow.AddHours(-2));

        var result = await SendAsync(BuyerId, "buyer", "delivered", OperationId, DateTime.UtcNow.AddMinutes(-1),
            ("scannedQrNonce", "d.renewed-offline"));

        result.GetProperty("status").GetString().Should().Be("deferred");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_PENDING: Waiting for the trucker's");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ADifferentTaggedScan_TakenBeforeTheStoredCode_IsAReplacedCode()
    {
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce, dropoffIssuedAt: DateTime.UtcNow.AddMinutes(-10));

        var result = await SendAsync(BuyerId, "buyer", "delivered", OperationId, DateTime.UtcNow.AddHours(-1),
            ("scannedQrNonce", "d.replaced"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_NONCE_MISMATCH: ");
    }

    [Fact]
    public async Task ATypedCodeMismatch_StillCounts_WhileACodeIsStored()
    {
        // Codes carry no tag: the brute-force cap stays meaningful.
        SetupDelivery(TruckDelivery("inTransit"));
        SetupSecret(dropoffNonce: DropoffNonce);
        var other = HandoffCode.Derive("d.renewed-offline", DeliveryId);

        var result = await SendAsync(BuyerId, "buyer", "delivered", ("scannedQrCode", other), ("gpsDropoff", Gps));

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CODE_MISMATCH: ");
        await _qrSecrets.Received(1).TryTakeCodeAttemptAsync(
            DeliveryId, "delivered", HandoffCode.AttemptId(OperationId, other), DropoffNonce, HandoffCode.MaxMisses, true);
    }

    [Fact]
    public async Task SecondDevice_ItsRegistrationDoesNotReplaceTheFirst_AndTheBuyersScanOfTheFirstPasses()
    {
        var store = new ScenarioQrSecrets();
        SetupDelivery(TruckDelivery("inTransit"));

        // Phone A at pickup, then phone B (or web) opening the drop-off QR with a code of its own.
        (await SendRawAsync(store, TruckerId, "trucker", "op-a", null, null, ("dropoffQrNonce", "d.phone-a")))
            .GetProperty("status").GetString().Should().Be("success");
        var second = await SendRawAsync(store, TruckerId, "trucker", "op-b", null, null, ("dropoffQrNonce", "d.phone-b"));
        second.GetProperty("message").GetString().Should().StartWith("DROPOFF_CODE_SUPERSEDED: ");
        store.Dropoff.Should().Be("d.phone-a");

        var confirm = await SendRawAsync(store, BuyerId, "buyer", "op-buyer", "delivered", null,
            ("scannedQrNonce", "d.phone-a"));

        confirm.GetProperty("status").GetString().Should().Be("success", confirm.ToString());
    }

    [Fact]
    public async Task ALateQueuedRegistration_DoesNotOverwriteARenewal()
    {
        var store = new ScenarioQrSecrets();
        SetupDelivery(TruckDelivery("inTransit"));

        await SendRawAsync(store, TruckerId, "trucker", "op-renew", null, null,
            ("dropoffQrNonce", "d.renewed"), ("dropoffQrRenew", true));
        // The first code's registration, queued behind a pickup that waited, lands now.
        var late = await SendRawAsync(store, TruckerId, "trucker", "op-late", null, null, ("dropoffQrNonce", "d.first"));

        late.GetProperty("message").GetString().Should().StartWith("DROPOFF_CODE_SUPERSEDED: ");
        store.Dropoff.Should().Be("d.renewed");
        (await SendRawAsync(store, BuyerId, "buyer", "op-buyer", "delivered", null, ("scannedQrNonce", "d.renewed")))
            .GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task AnOfflineRenewal_TheBuyerSyncsFirst_WaitsThenPasses()
    {
        var store = new ScenarioQrSecrets();
        SetupDelivery(TruckDelivery("inTransit"));
        await SendRawAsync(store, TruckerId, "trucker", "op-a", null, null, ("dropoffQrNonce", "d.first"));

        // The trucker pressed New drop-off code offline; the buyer scanned it and synced first.
        var early = await SendRawAsync(store, BuyerId, "buyer", "op-buyer", "delivered", DateTime.UtcNow,
            ("scannedQrNonce", "d.renewed"));
        early.GetProperty("status").GetString().Should().Be("deferred");
        store.ConsumedDelivered.Should().BeNull();

        await SendRawAsync(store, TruckerId, "trucker", "op-renew", null, null,
            ("dropoffQrNonce", "d.renewed"), ("dropoffQrRenew", true));
        var retry = await SendRawAsync(store, BuyerId, "buyer", "op-buyer", "delivered", DateTime.UtcNow,
            ("scannedQrNonce", "d.renewed"));

        retry.GetProperty("status").GetString().Should().Be("success", retry.ToString());
        store.ConsumedDelivered.Should().Be("op-buyer");
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

    /// <summary>
    /// The drop-off rules of <see cref="DeliveryQrSecretRepository"/> in memory: a registration is
    /// stored only while no code is, a renewal replaces it, the same code again changes nothing.
    /// </summary>
    private sealed class ScenarioQrSecrets : IDeliveryQrSecretRepository
    {
        public string? Dropoff { get; private set; }
        public DateTime? DropoffAt { get; private set; }
        public string? ConsumedDelivered { get; private set; }

        public Task PutNonceAsync(string deliveryId, string nonce) => Task.CompletedTask;

        public Task<DropoffNonceOutcome> PutDropoffNonceAsync(string deliveryId, string nonce, bool renew)
        {
            if (Dropoff == nonce)
            {
                return Task.FromResult(DropoffNonceOutcome.AlreadyStored);
            }

            if (Dropoff != null && !renew)
            {
                return Task.FromResult(DropoffNonceOutcome.OtherCodeStored);
            }

            Dropoff = nonce;
            DropoffAt = DateTime.UtcNow.AddSeconds(-1);
            return Task.FromResult(DropoffNonceOutcome.Stored);
        }

        public Task<DeliveryQrSecret?> GetAsync(string deliveryId) =>
            Task.FromResult<DeliveryQrSecret?>(new DeliveryQrSecret
            {
                DeliveryId = deliveryId,
                Nonce = FarmerNonce,
                IssuedAt = DateTime.UtcNow.AddDays(-1).ToString("o"),
                ConsumedPickedUpOp = "op-pickup",
                ConsumedDeliveredOp = ConsumedDelivered,
                DropoffNonce = Dropoff,
                DropoffIssuedAt = DropoffAt?.ToString("o")
            });

        public Task<CodeAttempt> TryTakeCodeAttemptAsync(
            string deliveryId, string targetStatus, string attemptId, string nonce, int maxAttempts,
            bool dropoffNonce = false) =>
            Task.FromResult(new CodeAttempt(CodeAttemptOutcome.Taken, 1));

        public Task ReturnCodeAttemptAsync(string deliveryId, string targetStatus, string attemptId) =>
            Task.CompletedTask;

        public Task MarkConsumedAsync(string deliveryId, string targetStatus, string operationId)
        {
            if (targetStatus == "delivered")
            {
                ConsumedDelivered = operationId;
            }

            return Task.CompletedTask;
        }
    }

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
