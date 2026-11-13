using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-36 (spec 4.5 T8 and T9, 4.8, E3): the pure rules of the drop-off judgement: which deadline is due
/// and when, the decision at T8 and T9, the stamp at pickup, the trucker and escrow checks of the
/// auto-confirm, and the deadline index entry (<see cref="OrderDeadlines"/>).
/// </summary>
public class DropoffJudgementTests
{
    private static readonly DateTime PickedUp = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T8 = PickedUp.AddHours(72);

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static Dictionary<string, object> Order(string status = "in_transit", Dictionary<string, object>? checkIn = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "ord-1" }, { "status", status }, { "version", "5" }, { "truckerId", "trucker-1" }, { "tripId", "trip-1" },
            { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
            { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending },
            { DropoffJudgement.DeadlineField, Iso(T8) }
        };
        if (checkIn != null)
        {
            order[DropoffJudgement.CheckInField] = checkIn;
        }

        return order;
    }

    private static Dictionary<string, object> CheckIn(DateTime received, bool measured = true, bool late = false) => new()
    {
        { PickupJudgement.EntryReceivedAt, Iso(received) },
        { PickupJudgement.EntryDistanceChecked, measured },
        { PickupJudgement.EntryLate, late }
    };

    // ===== which deadline =====

    [Fact]
    public void WithoutACheckIn_T8IsDue_AfterTheSettleMargin()
    {
        var due = DropoffJudgement.Due(Order(), OrderTimings.Defaults);

        due.Should().Be(new OrderDeadline(T8 + PickupJudgement.SettleMargin, OrderDeadlines.Kind.DeliveryOverdue));
        OrderDeadlines.Next(Order(), OrderTimings.Defaults).Should().Be(due);
        OrderDeadlines.IsSwept(OrderDeadlines.Kind.DeliveryOverdue).Should().BeTrue();
    }

    [Fact]
    public void ACountedCheckIn_MakesT9Due_48HoursAfterItsReceipt()
    {
        var received = T8.AddHours(-10);
        var order = Order(checkIn: CheckIn(received));

        var due = DropoffJudgement.Due(order, OrderTimings.Defaults);

        due.Should().Be(new OrderDeadline(received.AddHours(48) + PickupJudgement.SettleMargin, OrderDeadlines.Kind.SilentBuyer));
        OrderDeadlines.Next(order, OrderTimings.Defaults).Should().Be(due);
    }

    [Fact]
    public void T9_FollowsTheSsmWindow()
    {
        var received = T8.AddHours(-10);
        var timings = OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(30) };

        DropoffJudgement.Due(Order(checkIn: CheckIn(received)), timings)!.Value.At
            .Should().Be(received.AddHours(30) + PickupJudgement.SettleMargin);
    }

    [Theory]
    [InlineData(true, false)] // stored as late
    [InlineData(false, true)] // received after T8
    public void ALateCheckIn_NeverCounts_T8StaysDue(bool storedLate, bool afterT8)
    {
        var order = Order(checkIn: CheckIn(afterT8 ? T8.AddMinutes(5) : T8.AddHours(-1), late: storedLate));

        DropoffJudgement.CountedCheckIn(order).Should().BeNull();
        DropoffJudgement.Due(order, OrderTimings.Defaults)!.Value.Kind.Should().Be(OrderDeadlines.Kind.DeliveryOverdue);
    }

    [Theory]
    [InlineData(DropoffJudgement.Outcome.Closed)]
    [InlineData(DropoffJudgement.Outcome.Queued)]
    [InlineData(DropoffJudgement.Outcome.SilentBuyer)]
    public void AJudgedOrder_HasNoDropoffDeadline(string outcome)
    {
        var order = Order();
        order[DropoffJudgement.Field] = outcome;

        OrderDeadlines.Next(order, OrderTimings.Defaults).Should().BeNull();
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public void OnlyAnOrderOnTheRoad_IsJudged(string status)
    {
        var order = Order(status);
        order.Remove(PickupDeadline.Field);

        OrderDeadlines.Next(order, OrderTimings.Defaults).Should().BeNull();
    }

    // ===== the decision =====

    [Fact]
    public void NoCountedCheckIn_IsAlwaysT8_WhateverElseHolds()
        => DropoffJudgement.Decide(Order(), arrivalPhoto: true, truckerHeld: false, escrowPlain: true)
            .Should().Be(DropoffJudgement.Decision.DeliveryOverdue);

    [Fact]
    public void CompleteEvidence_AGoodTrucker_PlainMoney_IsTheAutoConfirm()
        => DropoffJudgement.Decide(Order(checkIn: CheckIn(T8.AddHours(-5))), true, false, true)
            .Should().Be(DropoffJudgement.Decision.AutoConfirm);

    [Theory]
    [InlineData(false, true, false, true)] // unmeasured
    [InlineData(true, false, false, true)] // no arrival photo
    [InlineData(true, true, true, true)] // low or restricted trucker
    [InlineData(true, true, false, false)] // the money not plainly held
    public void AnythingMissing_IsTheSilentBuyerDispute(bool measured, bool photo, bool held, bool plain)
        => DropoffJudgement.Decide(Order(checkIn: CheckIn(T8.AddHours(-5), measured)), photo, held, plain)
            .Should().Be(DropoffJudgement.Decision.SilentBuyerDispute);

    [Fact]
    public void FUP204_ABuyerWhoWithholdsTheScanAfterTheCheckIn_MakesAT9_NeverAT8()
    {
        // Whatever the buyer's silence leaves missing, a counted check-in rules T8 out.
        var order = Order(checkIn: CheckIn(T8.AddHours(-1)));
        foreach (var photo in new[] { true, false })
        {
            foreach (var plain in new[] { true, false })
            {
                DropoffJudgement.Decide(order, photo, truckerHeld: true, plain)
                    .Should().NotBe(DropoffJudgement.Decision.DeliveryOverdue);
            }
        }
    }

    [Fact]
    public void TheTrucker_HoldsTheAutoConfirm_WhenLowRestrictedOrUnderAnActiveRestriction()
    {
        var now = PickedUp;
        DropoffJudgement.HoldsAutoConfirm(null, now).Should().BeFalse();
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot { Tier = ReputationTiers.Good }, now).Should().BeFalse();
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot { Tier = ReputationTiers.Warning }, now).Should().BeFalse();
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot { Tier = ReputationTiers.Low }, now).Should().BeTrue();
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot { Tier = ReputationTiers.Restricted }, now).Should().BeTrue();
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot
        {
            Tier = ReputationTiers.Good,
            HasRestriction = true,
            RestrictionSince = now.AddDays(-1)
        }, now).Should().BeTrue("an open DELIVERY_OVERDUE review has no end date");
        DropoffJudgement.HoldsAutoConfirm(new ReputationSnapshot
        {
            Tier = ReputationTiers.Good,
            HasRestriction = true,
            RestrictionSince = now.AddDays(-9),
            RestrictionUntil = now.AddDays(-1)
        }, now).Should().BeFalse("an ended restriction holds nothing");
    }

    [Fact]
    public void OnlyPlainlyHeldMoney_IsTheSystemsToConfirm()
    {
        Dictionary<string, object> Escrow(params (string Key, string Value)[] extra)
        {
            var row = new Dictionary<string, object> { { "escrowId", "escrow-order-ord-1" }, { "status", "escrowed" } };
            foreach (var (key, value) in extra)
            {
                row[key] = value;
            }

            return row;
        }

        const string delivery = "del-1";
        DropoffJudgement.IsPlainForConfirm(Escrow(), delivery).Should().BeTrue();
        DropoffJudgement.IsPlainForConfirm(Escrow(("deliveryId", delivery)), delivery).Should().BeTrue();
        DropoffJudgement.IsPlainForConfirm(null, delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow(("status", "disputed")), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow(("status", "held")), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow(("refundReason", "order_cancelled")), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow((ManualPayoutFields.Marker, ManualPayoutFields.MarkerOpen)), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow((SettlementLockFields.Lock, "release")), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow(("deliveryConfirmedAt", Iso(PickedUp))), delivery).Should().BeFalse();
        DropoffJudgement.IsPlainForConfirm(Escrow(("deliveryId", "del-other")), delivery).Should().BeFalse("another delivery's money");
    }

    // ===== the stamp at pickup =====

    [Fact]
    public void ThePickupStamp_CarriesT8AndThePointsKnown()
    {
        var stamp = DropoffJudgement.PickupStamp(PickedUp, new TrackPoint(9.03, 38.74), new TrackPoint(9.01, 38.76), OrderTimings.Defaults);

        stamp[DropoffJudgement.Field].Should().Be(DropoffJudgement.Outcome.Pending);
        stamp[DropoffJudgement.DeadlineField].Should().Be(Iso(T8));
        stamp[DropoffJudgement.OrderLatitudeField].Should().Be(9.03);
        stamp[DropoffJudgement.TripLongitudeField].Should().Be(38.76);

        DropoffJudgement.PickupStamp(PickedUp, null, null, OrderTimings.Defaults)
            .Keys.Should().BeEquivalentTo(new[] { DropoffJudgement.Field, DropoffJudgement.DeadlineField });
    }

    [Fact]
    public void OnlyATruckedOrdersFirstMoveOutOfAccepted_TakesTheStamp()
    {
        var order = Order("accepted");
        order.Remove(DropoffJudgement.Field);

        DropoffJudgement.TakesPickupStamp(order, "accepted", "pickup_confirmed").Should().BeTrue();
        DropoffJudgement.TakesPickupStamp(order, "accepted", "in_transit").Should().BeTrue();
        DropoffJudgement.TakesPickupStamp(order, "pickup_confirmed", "in_transit").Should().BeFalse("picked up before the flag");
        DropoffJudgement.TakesPickupStamp(order, "accepted", "delivered").Should().BeFalse("a self-pickup jumps to delivered");

        var selfPickup = new Dictionary<string, object>(order) { [OrderCommitment.FulfillmentField] = OrderCommitment.Fulfillment.SelfPickup };
        DropoffJudgement.TakesPickupStamp(selfPickup, "accepted", "pickup_confirmed").Should().BeFalse();

        DropoffJudgement.TakesPickupStamp(Order("accepted"), "accepted", "pickup_confirmed").Should().BeFalse("stamped once");
    }

    [Fact]
    public void OnlyTheSystemsOwnDispute_IsFinishedByTheCron()
    {
        var delivery = new Dictionary<string, object>
        {
            { "status", "disputed" }, { "disputedBy", DropoffJudgement.SystemActor }, { "disputeReason", DropoffJudgement.OverdueReason }
        };
        DropoffJudgement.SystemDisputeReasonOf(delivery).Should().Be(DropoffJudgement.OverdueReason);

        delivery["disputedBy"] = "buyer-1";
        DropoffJudgement.SystemDisputeReasonOf(delivery).Should().BeNull();

        delivery["disputedBy"] = DropoffJudgement.SystemActor;
        delivery["disputeReason"] = "anything a party typed";
        DropoffJudgement.SystemDisputeReasonOf(delivery).Should().BeNull();
    }

    [Fact]
    public void TheNearestPoint_Measures()
    {
        var points = new[] { new TrackPoint(9.03, 38.74), new TrackPoint(9.20, 38.74) };

        DropoffJudgement.NearestKm(9.031, 38.74, points)!.Value.Should().BeLessThan(0.2);
        DropoffJudgement.NearestKm(9.031, 38.74, Array.Empty<TrackPoint>()).Should().BeNull();
    }
}
