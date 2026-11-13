using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-19 (spec E9): which deliveries an admin may cancel after pickup, which orders allow it, and that every escrow
/// behind the delivery must be settled first. The route and the transaction are in AdminDeliveryCancelRouteTests.
/// </summary>
public class AdminDeliveryCancelTests
{
    private static Dictionary<string, object> Row(params (string Key, object Value)[] fields)
        => fields.ToDictionary(f => f.Key, f => f.Value);

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    public void DeliveryRefusalOf_PickedUpAndNotDelivered_IsAllowed(string status)
        => AdminDeliveryCancel.DeliveryRefusalOf(Row(("status", status))).Should().BeNull();

    [Theory]
    [InlineData("pending", "any party can cancel it before pickup")]
    [InlineData("delivered", "settle it through the dispute verdict")]
    [InlineData("disputed", "settle it through the dispute verdict")]
    [InlineData("cancelled", "already cancelled")]
    [InlineData("lost", "only a delivery picked up and not delivered")]
    public void DeliveryRefusalOf_AnyOtherStatus_SaysWhy(string status, string why)
        => AdminDeliveryCancel.DeliveryRefusalOf(Row(("status", status))).Should().Contain(why);

    [Fact]
    public void OrderRefusalOf_ADeliveryFromBeforeOrders_HasNothingToCheck()
        => AdminDeliveryCancel.OrderRefusalOf(null, null).Should().BeNull();

    [Fact]
    public void OrderRefusalOf_AMissingOrderRow_IsRefused()
        => AdminDeliveryCancel.OrderRefusalOf("order-1", null).Should().Contain("cannot be found");

    [Theory]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("dispute_resolved")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public void OrderRefusalOf_GoodsPickedUpAndNeverDelivered_IsAllowed(string status)
        => AdminDeliveryCancel.OrderRefusalOf("order-1", Row(("status", status))).Should().BeNull();

    [Fact]
    public void OrderRefusalOf_AnOrderStillAcceptedBehindItsPickedUpDelivery_IsRefused()
        => AdminDeliveryCancel.OrderRefusalOf("order-1", Row(("status", "accepted"))).Should().Contain("must catch up with its delivery");

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    public void OrderRefusalOf_DeliveredOrNotPickedUp_IsRefused(string status)
        => AdminDeliveryCancel.OrderRefusalOf("order-1", Row(("status", status))).Should().Contain($"The order is {status}");

    [Fact]
    public void FirstHeldEscrow_NoEscrow_NothingHeld()
        => AdminDeliveryCancel.FirstHeldEscrow(Array.Empty<Dictionary<string, object>>()).Should().BeNull();

    [Fact]
    public void FirstHeldEscrow_EverySettled_NothingHeld()
        => AdminDeliveryCancel.FirstHeldEscrow(new[]
        {
            Row(("escrowId", "e1"), ("status", "refunded")),
            Row(("escrowId", "e2"), ("status", "completed")),
            Row(("escrowId", "e3"), ("status", "cancelled"))
        }).Should().BeNull();

    [Theory]
    [InlineData("initiated")]
    [InlineData("escrowed")]
    [InlineData("released")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("held")]
    public void FirstHeldEscrow_AnEscrowThatStillHoldsOrGovernsMoney_IsNamed(string status)
    {
        var held = AdminDeliveryCancel.FirstHeldEscrow(new[]
        {
            Row(("escrowId", "e1"), ("status", "refunded")),
            Row(("escrowId", "e2"), ("status", status))
        });

        held.Should().NotBeNull();
        AdminDeliveryCancel.MoneyHeldMessage(held!).Should().Contain($"escrow e2 is {status}").And.Contain("Nothing was written");
    }

    [Fact]
    public void IsReplay_OnlyTheAdminCancelFromTheVersionRead()
    {
        var cancelled = Row(("status", "cancelled"), ("cancelReason", "admin_cancelled"), ("version", 5));

        AdminDeliveryCancel.IsReplay(cancelled, 4).Should().BeTrue();
        AdminDeliveryCancel.IsReplay(cancelled, 3).Should().BeFalse();
        AdminDeliveryCancel.IsReplay(Row(("status", "cancelled"), ("cancelReason", "dispute_buyer_wins"), ("version", 5)), 4)
            .Should().BeFalse("a verdict closed it, not this admin cancel");
        AdminDeliveryCancel.IsReplay(Row(("status", "pickedUp"), ("version", 5)), 4).Should().BeFalse();
    }

    [Theory]
    [InlineData("cancelled", "admin_cancelled", true)]
    [InlineData("cancelled", "dispute_buyer_wins", false)]
    [InlineData("cancelled", null, false)]
    [InlineData("pickedUp", "admin_cancelled", false)]
    public void IsAdminCancelled_TheStatusAndTheReason(string status, string? reason, bool expected)
        => AdminDeliveryCancel.IsAdminCancelled(status, reason).Should().Be(expected);

    [Fact]
    public void AuditMetadata_NamesWhatWasChecked_AndEndsWithTheReason()
    {
        var metadata = AdminDeliveryCancel.AuditMetadata("in_transit", new[]
        {
            Row(("escrowId", "escrow-order-o1"), ("status", "refunded"))
        }, "Truck broke down; goods back with the farmer.");

        metadata.Should().Be(
            "orderStatus=in_transit;escrows=escrow-order-o1:refunded;reason=Truck broke down; goods back with the farmer.");
        AdminDeliveryCancel.AuditMetadata(null, Array.Empty<Dictionary<string, object>>(), "No order and no escrow at all.")
            .Should().Be("orderStatus=none;escrows=none;reason=No order and no escrow at all.");
    }
}
