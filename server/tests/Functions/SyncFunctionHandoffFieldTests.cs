using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-246 (sync-protocol.md 1.54.0): a delivery operation writes each handoff field only with the status of its
/// own handoff. <c>gpsPickup</c> goes with the trucker's <c>pickedUp</c>, <c>gpsDropoff</c> with the buyer's
/// <c>delivered</c>, <c>disputeReason</c> with <c>disputed</c>; anywhere else the field is dropped (not refused)
/// and logged. Before, a buyer's confirm could move the trucker's pickup point (and so write
/// <c>IMPOSSIBLE_DELIVERY_TIME</c> on an honest trucker) and a dispute could rewrite both points the admin reads as
/// dispute evidence. Uses the delivery, pattern and dispute rigs of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private static Dictionary<string, object> GpsData(double lat, double lng)
        => new() { { "latitude", lat }, { "longitude", lng } };

    /// <summary>Captures the delivery write (the deliveries table's UpdateItem) of the next operation.</summary>
    private Func<UpdateItemRequest?> CaptureDeliveryWrite()
    {
        UpdateItemRequest? write = null;
        _mockDynamoDb.When(d => d.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table")))
            .Do(ci => write = ci.Arg<UpdateItemRequest>());
        return () => write;
    }

    private static bool Writes(UpdateItemRequest write, string field)
        => write.ExpressionAttributeNames.Values.Contains(field);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    [Fact]
    public async Task BuyerConfirm_WithAFarGpsPickup_KeepsTheStoredPickup_AndDoesNotFlagTheTrucker()
    {
        // An honest 5 km trip in 20 minutes near Addis Ababa. The buyer's confirm also sends a pickup point in
        // Jimma, 259 km away: written, it made the trip read as impossible and flagged the trucker.
        var confirmedAt = DateTime.UtcNow;
        var stored = new Dictionary<string, AttributeValue>
        {
            { "gpsPickup", GpsPoint(9.00, 38.70) },
            { "pickedUpAt", new AttributeValue { S = IsoUtc(confirmedAt.AddMinutes(-20)) } },
            { TransactionPatternRules.PickupCapturedAtField, new AttributeValue { S = IsoUtc(confirmedAt.AddMinutes(-21)) } }
        };
        // The row a write that kept the moved point would answer with: the pattern rule must not read its pickup.
        var written = DeliveredRow(confirmedAt, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));
        written["gpsDropoff"] = GpsPoint(9.03, 38.74);
        var write = CaptureDeliveryWrite();

        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(), written, stored: stored, extraData: new()
        {
            { "gpsPickup", GpsData(7.67, 36.83) }, { "gpsDropoff", GpsData(9.03, 38.74) }
        });

        Assert.Contains("\"status\":\"success\"", body);
        Assert.NotNull(write());
        Assert.False(Writes(write()!, "gpsPickup"));
        Assert.True(Writes(write()!, "gpsDropoff"));
        await _patternFraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), "IMPOSSIBLE_DELIVERY_TIME",
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        Assert.Contains("DELIVERY_FIELD_DROPPED field=gpsPickup status=delivered deliveryId=del-001", Logs);
    }

    [Fact]
    public async Task BuyerConfirm_HonestPointsTooFastForTheDistance_StoresTheDropoff_AndStillFlagsTheTrucker()
    {
        // The trucker's own pickup in Jimma, the buyer's drop-off in Addis Ababa 20 minutes later.
        var written = DeliveredRow(DateTime.UtcNow, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25));
        var write = CaptureDeliveryWrite();

        var body = await ConfirmDeliveryAsync(DeliveryPatternFunction(), written,
            extraData: new() { { "gpsDropoff", GpsData(9.03, 38.74) } });

        Assert.Contains("\"status\":\"success\"", body);
        Assert.True(Writes(write()!, "gpsDropoff"));
        Assert.False(Writes(write()!, "gpsPickup"));
        await _patternFraud.Received(1).RecordFraudSignalAsync("trucker-001", "IMPOSSIBLE_DELIVERY_TIME", "del-001",
            "delivery", Arg.Is<string>(d => d.Contains("258.9 km apart")));
        Assert.DoesNotContain("DELIVERY_FIELD_DROPPED", Logs);
    }

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("farmer-001", "farmer")]
    [InlineData("trucker-001", "trucker")]
    public async Task Dispute_CarryingBothPoints_LeavesBothUnchanged(string userId, string role)
    {
        var rig = DisputeFunction(OrderEscrowRow());
        var write = CaptureDeliveryWrite();

        var response = await rig.Function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "disputed" }, { "disputeReason", "wet bags" }, { "version", 1 },
                { "gpsPickup", GpsData(7.67, 36.83) }, { "gpsDropoff", GpsData(9.03, 38.74) }
            })), userId, role), _context);

        Assert.Contains("success", response.Body);
        Assert.NotNull(write());
        Assert.False(Writes(write()!, "gpsPickup"));
        Assert.False(Writes(write()!, "gpsDropoff"));
        Assert.True(Writes(write()!, "disputeReason"));
        Assert.Contains("DELIVERY_FIELD_DROPPED field=gpsPickup status=disputed", Logs);
        Assert.Contains("DELIVERY_FIELD_DROPPED field=gpsDropoff status=disputed", Logs);
    }

    [Fact]
    public async Task TruckerPickup_StoresGpsPickup_AndDropsTheOtherHandoffFields()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-pickup");
        var write = CaptureDeliveryWrite();

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "pickedUp" }, { "version", 1 }, { "scannedQrNonce", "nonce-pickup" },
                { "gpsPickup", GpsData(9.00, 38.70) }, { "gpsDropoff", GpsData(9.03, 38.74) },
                { "disputeReason", "not a dispute" }
            })), "trucker-001", "trucker"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.True(Writes(write()!, "gpsPickup"));
        Assert.False(Writes(write()!, "gpsDropoff"));
        Assert.False(Writes(write()!, "disputeReason"));
    }

    [Fact]
    public async Task TruckerUpdateWithoutAStatus_OnADisputedDelivery_WritesNoHandoffField()
    {
        // The trucker owns status-less updates: it could rewrite both points and the buyer's dispute reason.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "disputed", 3, "nonce-any");
        var write = CaptureDeliveryWrite();

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "version", 3 }, { "gpsPickup", GpsData(9.00, 38.70) }, { "gpsDropoff", GpsData(9.03, 38.74) },
                { "disputeReason", "the buyer is lying" }
            })), "trucker-001", "trucker"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.False(Writes(write()!, "gpsPickup"));
        Assert.False(Writes(write()!, "gpsDropoff"));
        Assert.False(Writes(write()!, "disputeReason"));
        Assert.Contains("DELIVERY_FIELD_DROPPED field=disputeReason status=none", Logs);
    }

    [Fact]
    public async Task Create_CarryingHandoffFields_StoresNone()
    {
        SetupTruckOrder();
        var op = MakeDeliveryCreateOp();
        op.Data!["gpsPickup"] = GpsData(9.00, 38.70);
        op.Data["gpsDropoff"] = GpsData(9.03, 38.74);
        op.Data["disputeReason"] = "planted";

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await ReceivedDeliveryWrite(item =>
            !item.ContainsKey("gpsPickup") && !item.ContainsKey("gpsDropoff") && !item.ContainsKey("disputeReason"));
        Assert.Contains("DELIVERY_FIELD_DROPPED field=gpsPickup status=pending", Logs);
    }
}
