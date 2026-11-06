using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// SIM-28 (sync-protocol.md 1.38.0): a reused idempotency key with another payload is refused
/// instead of answered <c>duplicate</c>, and the loser of a status race (a status change invalid
/// against a row that moved past the version the client sent) gets <c>conflict</c> with the row
/// instead of an invalid-transition error.
/// </summary>
public partial class SyncFunctionTests
{
    private static JsonElement FirstResult(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("results")[0];

    // ===== Reused idempotency key =====

    [Fact]
    public async Task ReusedKey_WithAnotherPayload_IsRefused_AndNothingIsProcessedOrStored()
    {
        var first = MakeCropCreateOp();
        var second = MakeCropCreateOp();
        second.Data!["pricePerKg"] = 60;
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);
        _mockSyncMeta.GetPayloadFingerprintAsync(Arg.Any<string>()).Returns(SyncPayloadFingerprint.Of(first));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(second), "farmer-001", "farmer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(SyncFunction.IdempotencyKeyReusedMessage);
        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION");
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _mockSyncMeta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ReusedKey_WithTheSamePayload_IsADuplicate()
    {
        var op = MakeCropCreateOp();
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);
        _mockSyncMeta.GetPayloadFingerprintAsync(Arg.Any<string>()).Returns(SyncPayloadFingerprint.Of(op));

        // A resend may carry a new operation id and timestamp; they are not part of the payload.
        var resend = op with { OperationId = "op-resend", Timestamp = "2026-03-16T08:00:00Z" };
        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(resend), "farmer-001", "farmer"), _context);

        FirstResult(response.Body).GetProperty("status").GetString().Should().Be("duplicate");
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ReusedKey_RecordWithoutAFingerprint_StaysADuplicate(string? stored)
    {
        // Records written before SIM-28 carry no fingerprint: whatever comes, it is a duplicate.
        var op = MakeCropCreateOp();
        op.Data!["pricePerKg"] = 60;
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);
        _mockSyncMeta.GetPayloadFingerprintAsync(Arg.Any<string>()).Returns(stored);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstResult(response.Body).GetProperty("status").GetString().Should().Be("duplicate");
    }

    [Fact]
    public async Task ProcessedOperation_StoresTheFingerprintOfThePayloadAsSent()
    {
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "1" }
            }));
        var op = MakeCropCreateOp();
        var expected = SyncPayloadFingerprint.Of(op);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstResult(response.Body).GetProperty("status").GetString().Should().Be("success");
        await _mockSyncMeta.Received(1).StoreIdempotencyKeyAsync(
            Arg.Any<string>(), op.OperationId, "crop_listing", "success", expected);
    }

    // ===== Race losers =====

    [Fact]
    public async Task DeliveryRaceLoser_StaleVersion_GetsConflictWithTheScrubbedRow()
    {
        // The buyer's other phone confirmed first (version 4); this one sent version 3.
        SetupExistingDelivery("del-001", "trucker-001", "delivered", 4);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DeliveryStatusOp("delivered", 3)), "buyer-001", "buyer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        var row = result.GetProperty("serverData");
        row.GetProperty("status").GetString().Should().Be("delivered");
        row.GetProperty("version").GetInt32().Should().Be(4);
        row.TryGetProperty("buyerPhone", out _).Should().BeFalse();
        row.TryGetProperty("truckerPhone", out _).Should().BeFalse();
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task DeliveryInvalidTransition_AtTheCurrentVersion_IsStillAnError()
    {
        SetupExistingDelivery("del-001", "trucker-001", "delivered", 4);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DeliveryStatusOp("delivered", 4)), "buyer-001", "buyer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_INVALID_TRANSITION");
    }

    [Fact]
    public async Task DeliveryRaceLoser_NotAParty_GetsTheErrorWithoutTheRow()
    {
        SetupExistingDelivery("del-001", "trucker-001", "delivered", 4);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DeliveryStatusOp("delivered", 3)), "buyer-999", "buyer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("error");
        result.TryGetProperty("serverData", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeliveryStaleStatus_TheCallerMayNotAskFor_IsStillAnError()
    {
        // The farmer never confirms a delivery: not a race the farmer lost.
        SetupExistingDelivery("del-001", "trucker-001", "delivered", 4);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DeliveryStatusOp("delivered", 3)), "farmer-001", "farmer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_INVALID_TRANSITION");
        result.TryGetProperty("serverData", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TripRaceLoser_StaleVersion_GetsConflictWithTheRow()
    {
        // The trucker's other phone started the trip (version 3); this one sent version 2.
        SetupExistingTrip("trip-001", "trucker-001", "inTransit", 3);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(TripStatusOp("inTransit", 2))), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("inTransit");
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripInvalidTransition_AtTheCurrentVersion_IsStillAnError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "inTransit", 3);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(TripStatusOp("inTransit", 3))), _context);

        FirstResult(response.Body).GetProperty("message").GetString().Should().StartWith("TRIP_INVALID_TRANSITION");
    }

    [Fact]
    public async Task CropRaceLoser_StaleVersion_GetsConflictWithTheRow()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" }, { "farmerId", "farmer-001" }, { "status", "sold" }, { "version", "3" }
        });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(CropStatusOp("reserved", 2)), "farmer-001", "farmer"), _context);

        var result = FirstResult(response.Body);
        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("sold");
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CropInvalidTransition_WithoutAVersion_IsStillAnError()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" }, { "farmerId", "farmer-001" }, { "status", "sold" }, { "version", "3" }
        });
        var op = CropStatusOp("reserved", 2);
        op.Data!.Remove("version");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstResult(response.Body).GetProperty("message").GetString().Should().StartWith("CROP_INVALID_TRANSITION");
    }

    private static SyncOperationRequest DeliveryStatusOp(string status, int version) => new()
    {
        OperationId = "op-delivery-race",
        IdempotencyKey = $"buyer-001:delivery:del-001:update:{version}",
        EntityType = "delivery",
        EntityId = "del-001",
        Action = "update",
        Data = new Dictionary<string, object> { { "status", status }, { "version", version } }
    };

    private static SyncOperationRequest TripStatusOp(string status, int version) => new()
    {
        OperationId = "op-trip-race",
        IdempotencyKey = $"trucker-001:trip:trip-001:update:{version}",
        EntityType = "trip",
        EntityId = "trip-001",
        Action = "update",
        Data = new Dictionary<string, object> { { "status", status }, { "version", version } }
    };

    private static SyncOperationRequest CropStatusOp(string status, int version) => new()
    {
        OperationId = "op-crop-race",
        IdempotencyKey = $"farmer-001:crop_listing:crop-001:update:{version}",
        EntityType = "crop_listing",
        EntityId = "crop-001",
        Action = "update",
        Data = new Dictionary<string, object> { { "status", status }, { "version", version } }
    };
}
