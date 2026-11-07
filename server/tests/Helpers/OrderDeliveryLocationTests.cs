using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// LT-14: the order's own drop-off point's field rules (both or neither, JSON numbers inside
/// Ethiopia's box, a pair of nulls is no point), its reading from a stored row, and who reads it (the
/// readers of the drop-off note).
/// </summary>
public class OrderDeliveryLocationTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";

    // ===== Field rules =====

    [Fact]
    public void NoFields_AreFine_AndNoPoint()
    {
        var data = new Dictionary<string, object> { ["status"] = "created" };

        OrderDeliveryLocation.Sent(data).Should().BeFalse();
        OrderDeliveryLocation.ValidationError(data).Should().BeNull();
        OrderDeliveryLocation.FromPayload(data).Should().BeNull();
    }

    [Fact]
    public void JsonNumbers_InsideTheBox_AreAPoint()
    {
        var data = Payload("{\"deliveryLatitude\": 8.5423, \"deliveryLongitude\": 39.2687}");

        OrderDeliveryLocation.Sent(data).Should().BeTrue();
        OrderDeliveryLocation.ValidationError(data).Should().BeNull();
        OrderDeliveryLocation.FromPayload(data).Should().Be(new TrackPoint(8.5423, 39.2687));
    }

    [Fact]
    public void APairOfNulls_IsValid_AndNoPoint()
    {
        var data = Payload("{\"deliveryLatitude\": null, \"deliveryLongitude\": null}");

        OrderDeliveryLocation.Sent(data).Should().BeTrue();
        OrderDeliveryLocation.ValidationError(data).Should().BeNull();
        OrderDeliveryLocation.FromPayload(data).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"deliveryLatitude\": 8.5}", "deliveryLatitude and deliveryLongitude must be sent together.")]
    [InlineData("{\"deliveryLatitude\": 8.5, \"deliveryLongitude\": null}", "deliveryLatitude and deliveryLongitude must be sent together.")]
    [InlineData("{\"deliveryLatitude\": \"8.5\", \"deliveryLongitude\": 39.2}", "deliveryLatitude must be a number between 3 and 15.")]
    [InlineData("{\"deliveryLatitude\": 15.01, \"deliveryLongitude\": 39.2}", "deliveryLatitude must be a number between 3 and 15.")]
    [InlineData("{\"deliveryLatitude\": 8.5, \"deliveryLongitude\": 32.49}", "deliveryLongitude must be a number between 32.5 and 48.5.")]
    [InlineData("{\"deliveryLatitude\": true, \"deliveryLongitude\": {}}", "deliveryLatitude must be a number between 3 and 15. deliveryLongitude must be a number between 32.5 and 48.5.")]
    public void BadPoints_AreRefused(string json, string error)
    {
        OrderDeliveryLocation.ValidationError(Payload(json)).Should().Be(error);
    }

    // ===== The stored row =====

    [Fact]
    public void Of_ReadsTheStoredText_AndNeedsBothFields()
    {
        OrderDeliveryLocation.Of(new Dictionary<string, object> { ["deliveryLatitude"] = "8.5423", ["deliveryLongitude"] = "39.2687" })
            .Should().Be(new TrackPoint(8.5423, 39.2687));
        OrderDeliveryLocation.Of(new Dictionary<string, object> { ["deliveryLatitude"] = "8.5423" }).Should().BeNull();
        OrderDeliveryLocation.Of(null).Should().BeNull();
    }

    [Fact]
    public void Same_ComparesByValue_AndTwoNonesAreTheSame()
    {
        OrderDeliveryLocation.Same(new TrackPoint(8.5423, 39.2687), new TrackPoint(8.5423, 39.2687)).Should().BeTrue();
        OrderDeliveryLocation.Same(new TrackPoint(8.5423, 39.2687), new TrackPoint(8.5424, 39.2687)).Should().BeFalse();
        OrderDeliveryLocation.Same(null, null).Should().BeTrue();
        OrderDeliveryLocation.Same(new TrackPoint(8.5, 39.2), null).Should().BeFalse();
    }

    // ===== Who reads it =====

    [Theory]
    [InlineData(BuyerId, "funded", "pending", true)]
    [InlineData(FarmerId, "funded", "pending", true)]
    [InlineData(BuyerId, "created", "none", true)]
    // The trucker reads it while the money is held and the trucker is still on the order: not asked
    // yet, asked or committed (the note waits for the commitment).
    [InlineData(TruckerId, "funded", "pending", true)]
    [InlineData(TruckerId, "funded", "not_asked", true)]
    [InlineData(TruckerId, "accepted", "accepted", true)]
    // An order from before the commitments counts by its status (TruckerResponseOf).
    [InlineData(TruckerId, "accepted", "", true)]
    [InlineData(TruckerId, "funded", "", true)]
    // A trucker who left the order reads nothing more, though it keeps the truckerId.
    [InlineData(TruckerId, "awaiting_transport", "declined", false)]
    [InlineData(TruckerId, "awaiting_transport", "expired", false)]
    [InlineData(TruckerId, "awaiting_transport", "cancelled", false)]
    [InlineData(TruckerId, "awaiting_transport", "failed_capacity", false)]
    [InlineData(TruckerId, "created", "none", false)]
    [InlineData(TruckerId, "pending_payment", "none", false)]
    [InlineData(TruckerId, "cancelled", "accepted", false)]
    [InlineData("someone-else", "accepted", "accepted", false)]
    [InlineData(null, "accepted", "accepted", false)]
    public void For_TheBuyerAndFarmer_AndTheTruckerWhileTheMoneyIsHeld(
        string? caller, string status, string truckerResponse, bool reads)
    {
        var order = new Dictionary<string, object>
        {
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["status"] = status,
            ["truckerResponse"] = truckerResponse,
            ["deliveryLatitude"] = "8.5423",
            ["deliveryLongitude"] = "39.2687"
        };

        var point = OrderDeliveryLocation.For(order, caller);

        if (reads)
        {
            point.Should().Be(new TrackPoint(8.5423, 39.2687));
        }
        else
        {
            point.Should().BeNull();
        }
    }

    [Fact]
    public void ForCaller_KeepsThePointForAnAskedTrucker_AndScrubsOnlyTheNote()
    {
        var row = new Dictionary<string, object>
        {
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["status"] = "funded",
            ["truckerResponse"] = "pending",
            ["dropoffNote"] = "Merkato",
            ["deliveryLatitude"] = "8.5423",
            ["deliveryLongitude"] = "39.2687"
        };
        var result = new SyncOperationResult { OperationId = "op", Status = "conflict", ServerData = row };

        var trucker = OrderDeliveryLocation.ForCaller(OrderDropoffNote.ForCaller(result, TruckerId), TruckerId).ServerData!;
        trucker["deliveryLatitude"].Should().Be("8.5423");
        trucker.Should().NotContainKey("dropoffNote");
    }

    [Fact]
    public void ForCaller_ScrubsThePoint_ForATruckerOfAnOrderWhoseMoneyIsNotHeld()
    {
        var row = new Dictionary<string, object>
        {
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["status"] = "pending_payment",
            ["deliveryLatitude"] = "8.5423",
            ["deliveryLongitude"] = "39.2687"
        };
        var result = new SyncOperationResult { OperationId = "op", Status = "conflict", ServerData = row };

        var trucker = OrderDeliveryLocation.ForCaller(result, TruckerId).ServerData!;
        trucker.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
        trucker["status"].Should().Be("pending_payment");

        OrderDeliveryLocation.ForCaller(result, BuyerId).ServerData!["deliveryLatitude"].Should().Be("8.5423");
    }

    [Theory]
    [InlineData("trip-1", null, null, true)]
    [InlineData(null, "trucker-1", null, true)]
    [InlineData("trip-1", "trucker-1", "trucker", true)]
    [InlineData("trip-1", "trucker-1", "self_pickup", false)]
    [InlineData(null, null, null, false)]
    public void HasTrip_OnlyATruckedOrderHasADropoff(string? tripId, string? truckerId, string? fulfillment, bool has)
    {
        var order = new Dictionary<string, object> { ["orderId"] = "o-1" };
        if (tripId != null)
        {
            order["tripId"] = tripId;
        }

        if (truckerId != null)
        {
            order["truckerId"] = truckerId;
        }

        if (fulfillment != null)
        {
            order["fulfillment"] = fulfillment;
        }

        OrderDeliveryLocation.HasTrip(order).Should().Be(has);
    }

    private static Dictionary<string, object> Payload(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
}
