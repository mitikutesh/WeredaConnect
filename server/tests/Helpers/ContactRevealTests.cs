using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
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

    [Fact]
    public void LockedMessage_StartsWithItsCode()
    {
        Assert.StartsWith("MESSAGE_CONTACT_LOCKED: ", ContactReveal.LockedMessage);
    }
}
