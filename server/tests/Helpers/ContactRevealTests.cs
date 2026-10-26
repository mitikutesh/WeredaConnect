using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>ESC-48: when the parties of an order may reach each other.</summary>
public class ContactRevealTests
{
    [Fact]
    public void EveryOrderStatus_IsEitherOpenOrClosed_NeverBoth()
    {
        // A new order status must be placed on purpose: it can neither open contact by accident
        // nor silently stay closed without a decision.
        var statuses = SyncFunction.OrderTransitions.Keys
            .Concat(SyncFunction.OrderTransitions.Values.SelectMany(v => v))
            .Distinct()
            .ToList();

        foreach (var status in statuses)
        {
            var open = ContactReveal.OpenOrderStatuses.Contains(status);
            var closed = ContactReveal.ClosedOrderStatuses.Contains(status);
            Assert.True(open ^ closed, $"order status '{status}' must be in exactly one contact set");
        }

        Assert.Empty(ContactReveal.OpenOrderStatuses.Concat(ContactReveal.ClosedOrderStatuses).Except(statuses));
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("pickup_confirmed", true)]
    [InlineData("in_transit", true)]
    [InlineData("delivered", true)]
    [InlineData("disbursing", true)]
    [InlineData("settled", true)]
    [InlineData("disputed", true)]
    [InlineData("created", false)]
    [InlineData("pending_payment", false)]
    [InlineData("payment_submitted", false)]
    [InlineData("payment_failed", false)]
    [InlineData("funded", false)]
    [InlineData("awaiting_transport", false)]
    [InlineData("cancelled", false)]
    [InlineData("rejected", false)]
    [InlineData("dispute_resolved", false)]
    [InlineData("Accepted", false)]
    [InlineData("", false)]
    public void IsOpen_FollowsTheStatus(string status, bool expected)
    {
        Assert.Equal(expected, ContactReveal.IsOpen(new Dictionary<string, object> { ["status"] = status }));
    }

    [Fact]
    public void IsOpen_WithoutStatus_IsClosed()
    {
        Assert.False(ContactReveal.IsOpen(new Dictionary<string, object>()));
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public void IsOpen_AdminOverrideRefund_ClosesAnOpenStatus(string flag)
    {
        var order = new Dictionary<string, object> { ["status"] = "in_transit", [flag] = "2026-10-10T10:00:00Z" };

        Assert.False(ContactReveal.IsOpen(order));
    }

    [Fact]
    public void IsOpen_EmptyOverrideFlag_DoesNotClose()
    {
        var order = new Dictionary<string, object> { ["status"] = "accepted", ["refundOverrideAt"] = "" };

        Assert.True(ContactReveal.IsOpen(order));
    }

    // ===== ESC-15: contact per party =====

    private static Dictionary<string, object> Order(
        string status, string? farmer = null, string? trucker = null, bool trip = true)
    {
        var order = new Dictionary<string, object>
        {
            ["status"] = status,
            ["buyerId"] = "buyer-1",
            ["farmerId"] = "farmer-1"
        };
        if (trip)
        {
            order["tripId"] = "trip-1";
            order["truckerId"] = "trucker-1";
        }

        if (farmer != null)
        {
            order[OrderCommitment.FarmerResponseField] = farmer;
        }

        if (trucker != null)
        {
            order[OrderCommitment.TruckerResponseField] = trucker;
        }

        return order;
    }

    [Fact]
    public void Funded_FarmerAccepted_TruckerStillDeciding_OpensBuyerAndFarmerOnly()
    {
        var order = Order("funded", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.Pending);

        Assert.True(ContactReveal.IsOpen(order));
        Assert.False(ContactReveal.IsTruckerOpen(order));
        Assert.True(ContactReveal.IsOpenFor(order, "buyer-1"));
        Assert.True(ContactReveal.IsOpenFor(order, "farmer-1"));
        Assert.False(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void Funded_FarmerPending_IsClosedForEveryone()
    {
        var order = Order("funded", farmer: OrderCommitment.Response.Pending, trucker: OrderCommitment.Response.NotAsked);

        Assert.False(ContactReveal.IsOpen(order));
        Assert.False(ContactReveal.IsTruckerOpen(order));
        Assert.False(ContactReveal.IsOpenFor(order, "buyer-1"));
        Assert.False(ContactReveal.IsOpenFor(order, "farmer-1"));
        Assert.False(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void Accepted_BothCommitted_OpensEveryParty()
    {
        var order = Order("accepted", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.Accepted);

        Assert.True(ContactReveal.IsOpen(order));
        Assert.True(ContactReveal.IsTruckerOpen(order));
        Assert.True(ContactReveal.IsOpenFor(order, "buyer-1"));
        Assert.True(ContactReveal.IsOpenFor(order, "farmer-1"));
        Assert.True(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void AwaitingTransport_TruckerDeclined_KeepsBuyerAndFarmer_DropsTheTrucker()
    {
        var order = Order("awaiting_transport", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.Declined);
        order[OrderCommitment.TransportFailureField] = OrderCommitment.TransportFailure.TruckerDeclined;

        Assert.True(ContactReveal.IsOpen(order));
        Assert.False(ContactReveal.IsTruckerOpen(order));
        Assert.True(ContactReveal.IsOpenFor(order, "buyer-1"));
        Assert.True(ContactReveal.IsOpenFor(order, "farmer-1"));
        Assert.False(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void AwaitingTransport_TripFullBeforeTheFarmerAnswered_IsClosed()
    {
        // The buyer must not get the farmer's phone from a transport failure alone.
        var order = Order("awaiting_transport", farmer: OrderCommitment.Response.Pending, trucker: OrderCommitment.Response.FailedCapacity);
        order[OrderCommitment.TransportFailureField] = OrderCommitment.TransportFailure.TripFull;

        Assert.False(ContactReveal.IsOpen(order));
        Assert.False(ContactReveal.IsTruckerOpen(order));
        Assert.False(ContactReveal.IsOpenFor(order, "buyer-1"));
    }

    [Fact]
    public void LegacyRow_Accepted_WithoutResponseFields_OpensEveryParty()
    {
        // A trip order from before ESC-15 counts as farmer- and trucker-accepted from accepted on.
        var order = Order("accepted");

        Assert.True(ContactReveal.IsOpen(order));
        Assert.True(ContactReveal.IsTruckerOpen(order));
        Assert.True(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void SelfPickup_Accepted_HasNoTruckerContact()
    {
        var order = Order("accepted", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.None, trip: false);

        Assert.True(ContactReveal.IsOpen(order));
        Assert.False(ContactReveal.IsTruckerOpen(order));
    }

    [Theory]
    [InlineData("stranger-1")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOpenFor_AnyoneElse_IsFalse(string? userId)
    {
        var order = Order("accepted", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.Accepted);

        Assert.False(ContactReveal.IsOpenFor(order, userId));
    }

    [Fact]
    public void IsTruckerOpen_AdminOverrideRefund_ClosesIt()
    {
        var order = Order("accepted", farmer: OrderCommitment.Response.Accepted, trucker: OrderCommitment.Response.Accepted);
        order["refundOverrideAt"] = "2026-10-10T10:00:00Z";

        Assert.False(ContactReveal.IsTruckerOpen(order));
        Assert.False(ContactReveal.IsOpenFor(order, "trucker-1"));
    }

    [Fact]
    public void LockedMessage_StartsWithItsCode()
    {
        Assert.StartsWith("MESSAGE_CONTACT_LOCKED: ", ContactReveal.LockedMessage);
    }
}
