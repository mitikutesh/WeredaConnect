using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-17 (design 4.10): when a trip is finished (every delivery done or cancelled, at least one
/// done) and which orders still keep a trip that left from closing.
/// </summary>
public class TripLifecycleTests
{
    [Theory]
    [InlineData("delivered")]
    [InlineData("cancelled,delivered")]
    [InlineData("delivered,disputed")]
    [InlineData("disputed,cancelled,cancelled")]
    public void AllDeliveriesDone_EveryDeliveryDoneOrCancelled_AndOneDone(string statuses)
        => TripLifecycle.AllDeliveriesDone(statuses.Split(',')).Should().BeTrue();

    [Fact]
    public void AllDeliveriesDone_NoDeliveries_IsNotDone()
        => TripLifecycle.AllDeliveriesDone(Array.Empty<string>()).Should().BeFalse();

    [Fact]
    public void AllDeliveriesDone_OnlyCancelled_CarriedNothing()
        => TripLifecycle.AllDeliveriesDone(new[] { "cancelled", "cancelled" }).Should().BeFalse();

    [Theory]
    [InlineData("pending")]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    [InlineData("")]
    public void AllDeliveriesDone_AnyDeliveryStillActive_IsNotDone(string active)
        => TripLifecycle.AllDeliveriesDone(new[] { "delivered", "cancelled", active }).Should().BeFalse();

    [Theory]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    public void FirstOrderAwaitingCarriage_AnOrderWithoutADoneDelivery_IsReturned(string status)
        => TripLifecycle.FirstOrderAwaitingCarriage(
                new[] { ("order-1", "delivered"), ("order-2", status) },
                new[] { ("delivered", "order-1"), ("cancelled", "order-2") })
            .Should().Be("order-2");

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public void FirstOrderAwaitingCarriage_ADoneDeliveryCarriesItsLaggingOrder(string deliveryStatus)
        => TripLifecycle.FirstOrderAwaitingCarriage(
                new[] { ("order-1", "in_transit") },
                new[] { (deliveryStatus, "order-1") })
            .Should().BeNull();

    [Theory]
    [InlineData("created")]
    [InlineData("payment_submitted")]
    [InlineData("cancelled")]
    [InlineData("awaiting_transport")]
    [InlineData("delivered")]
    [InlineData("settled")]
    public void FirstOrderAwaitingCarriage_OrdersNotWaitingForTheTruck_AreIgnored(string status)
        => TripLifecycle.FirstOrderAwaitingCarriage(
                new[] { ("order-1", status) },
                new[] { ("delivered", "order-2") })
            .Should().BeNull();

    [Theory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("payment_failed")]
    [InlineData("awaiting_transport")]
    public void OrderStillOnTrip_ClosedOrTransportFailed_IsOff(string status)
        => TripLifecycle.OrderStillOnTrip(status).Should().BeFalse();

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_submitted")]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("settled")]
    [InlineData("disputed")]
    public void OrderStillOnTrip_LiveOrUsedTheTruck_IsOn(string status)
        => TripLifecycle.OrderStillOnTrip(status).Should().BeTrue();
}
