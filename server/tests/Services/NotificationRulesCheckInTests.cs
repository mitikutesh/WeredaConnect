using FluentAssertions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;
using P = WeredaConnect.Server.Services.NotificationParams;
using T = WeredaConnect.Server.Services.NotificationTypes;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-286 (ESC-34) and FUP-338 (ESC-36), spec 4.12: the check-in pushes per outcome and per role
/// (notifications-api.md 1.6.0). The pickup: the counterpart told when the other side checks in, the
/// reminder to each party who has not, and the judged outcomes with their reason. The drop-off: the
/// buyer told when the trucker checks in, with T9 (the order cron's rule and the SSM window), the
/// reminder before T9, the system's confirm and the system's disputes to the parties they name.
/// </summary>
public class NotificationRulesCheckInTests
{
    private static readonly DateTime RecordTime = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PickupBy = new(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T8 = new(2026, 11, 23, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Received = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    private const string Buyer = "buyer-1";
    private const string Farmer = "farmer-1";
    private const string Trucker = "trucker-1";

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static Dictionary<string, object?> With(Dictionary<string, object?> row, params (string Key, object? Value)[] changes)
    {
        var copy = new Dictionary<string, object?>(row, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>A trip order: the buyer, the farmer and the trucker, 500 kg teff.</summary>
    private static Dictionary<string, object?> TripOrder(string status, params (string Key, object? Value)[] changes)
        => With(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["orderId"] = "order-1",
            ["status"] = status,
            ["buyerId"] = Buyer,
            ["farmerId"] = Farmer,
            ["truckerId"] = Trucker,
            ["tripId"] = "trip-1",
            ["buyerName"] = "Abebe T.",
            ["farmerName"] = "Chaltu G.",
            ["truckerName"] = "Dawit K.",
            ["cropType"] = "teff",
            ["quantity"] = 500,
            ["cropCost"] = 40000,
            ["transportCost"] = 1500,
            ["orderTotal"] = 42500,
            [OrderCommitment.FulfillmentField] = OrderCommitment.Fulfillment.Trucker,
            [OrderCommitment.FarmerResponseField] = OrderCommitment.Response.Accepted,
            [OrderCommitment.TruckerResponseField] = OrderCommitment.Response.Accepted
        }, changes);

    private static Dictionary<string, object?> SelfPickupOrder(string status, params (string Key, object? Value)[] changes)
        => With(TripOrder(status, ("truckerId", null), ("tripId", null), ("truckerName", null), ("transportCost", 0),
            ("orderTotal", 41000), (OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.SelfPickup),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.None)), changes);

    /// <summary>An accepted order waiting for its pickup judgement, with the check-ins given.</summary>
    private static Dictionary<string, object?> Judgeable(bool selfPickup = false, params string[] checkedIn)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kind in checkedIn)
        {
            map[kind] = Entry(PickupBy.AddHours(-10));
        }

        var fields = new (string, object?)[]
        {
            (PickupJudgement.Field, PickupJudgement.Outcome.Pending),
            (PickupDeadline.Field, Iso(PickupBy)),
            (PickupJudgement.CheckInsField, map)
        };
        return selfPickup ? SelfPickupOrder("accepted", fields) : TripOrder("accepted", fields);
    }

    private static Dictionary<string, object?> Entry(DateTime receivedAt, bool late = false) => new(StringComparer.Ordinal)
    {
        [PickupJudgement.EntryReceivedAt] = Iso(receivedAt),
        [PickupJudgement.EntryBy] = "someone",
        [PickupJudgement.EntryDistanceChecked] = true,
        [PickupJudgement.EntryLate] = late,
        [PickupJudgement.EntryLatitude] = 8.75,
        [PickupJudgement.EntryLongitude] = 38.97
    };

    private static Dictionary<string, object?> CheckIns(Dictionary<string, object?> row, string kind, bool late = false)
    {
        var map = new Dictionary<string, object?>((Dictionary<string, object?>)row[PickupJudgement.CheckInsField]!, StringComparer.Ordinal)
        {
            [kind] = Entry(PickupBy.AddHours(-2), late)
        };
        return With(row, (PickupJudgement.CheckInsField, map));
    }

    /// <summary>An order on the road waiting for its drop-off judgement (T8 stamped).</summary>
    private static Dictionary<string, object?> OnTheRoad(params (string Key, object? Value)[] changes)
        => With(TripOrder("in_transit",
            (DropoffJudgement.Field, DropoffJudgement.Outcome.Pending),
            (DropoffJudgement.DeadlineField, Iso(T8))), changes);

    private static IReadOnlyList<NotificationEvent> OrderChange(
        Dictionary<string, object?> old, Dictionary<string, object?> row, Func<OrderTimings>? timings = null)
        => NotificationRules.Derive("orders", "MODIFY",
            NotificationRulesTests.Image(old), NotificationRulesTests.Image(row), RecordTime, timings ?? (() => OrderTimings.Defaults));

    private static NotificationEvent For(IReadOnlyList<NotificationEvent> events, string recipient)
    {
        events.Where(e => e.RecipientUserId == recipient).Should().ContainSingle($"exactly one event for {recipient}");
        return events.Single(e => e.RecipientUserId == recipient);
    }

    private static void Tells(IReadOnlyList<NotificationEvent> events, params (string Recipient, string Type)[] expected)
        => events.Select(e => (e.RecipientUserId, e.Type)).Should().BeEquivalentTo(expected);

    // ===== the pickup: the counterpart (FUP-286) =====

    [Fact]
    public void TheTruckersArrival_TellsTheFarmer_Only()
    {
        var old = Judgeable();
        var events = OrderChange(old, CheckIns(old, PickupJudgement.Kind.ArrivedPickup));

        Tells(events, (Farmer, T.TruckerArrived));
        var told = For(events, Farmer);
        told.Push.Should().BeTrue();
        told.DeadlineUtc.Should().BeNull();
        told.Params[P.TruckerName].Should().Be("Dawit K.");
        told.Params.Keys.Should().NotContain(new[] { "latitude", "longitude", "distanceKm" }, "never where the check-in was made");
    }

    [Fact]
    public void ASelfPickupBuyersArrival_TellsTheFarmer_TheBuyerHasArrived()
    {
        var old = Judgeable(selfPickup: true);

        Tells(OrderChange(old, CheckIns(old, PickupJudgement.Kind.ArrivedPickup)), (Farmer, T.BuyerArrived));
    }

    [Fact]
    public void TheFarmersCropsReady_TellsTheOneWhoCollects()
    {
        var trip = Judgeable(false, PickupJudgement.Kind.ArrivedPickup);
        Tells(OrderChange(trip, CheckIns(trip, PickupJudgement.Kind.CropsReady)), (Trucker, T.CropsReady));

        var self = Judgeable(selfPickup: true);
        Tells(OrderChange(self, CheckIns(self, PickupJudgement.Kind.CropsReady)), (Buyer, T.CropsReady));
    }

    [Fact]
    public void ALateCheckIn_OrOneAlreadyStored_TellsNobody()
    {
        var old = Judgeable();
        OrderChange(old, CheckIns(old, PickupJudgement.Kind.ArrivedPickup, late: true)).Should().BeEmpty();

        var stored = Judgeable(false, PickupJudgement.Kind.ArrivedPickup);
        OrderChange(stored, With(stored, ("updatedAt", Iso(RecordTime)))).Should().BeEmpty();
    }

    // ===== the pickup: the reminder (FUP-286) =====

    [Fact]
    public void TheReminderMarker_TellsEachPartyWhoHasNotCheckedIn_UntilTheDeadline()
    {
        var none = Judgeable();
        var events = OrderChange(none, With(none, (PickupJudgement.ReminderAtField, Iso(PickupBy.AddHours(-6)))));

        Tells(events, (Trucker, T.PickupReminder), (Farmer, T.PickupReminder));
        events.Should().OnlyContain(e => e.DeadlineUtc == PickupBy && e.Params[P.DeadlineAt] == Iso(PickupBy) && e.Push);

        var truckerIn = Judgeable(false, PickupJudgement.Kind.ArrivedPickup);
        Tells(OrderChange(truckerIn, With(truckerIn, (PickupJudgement.ReminderAtField, Iso(PickupBy.AddHours(-6))))),
            (Farmer, T.PickupReminder));

        var self = Judgeable(true, PickupJudgement.Kind.CropsReady);
        Tells(OrderChange(self, With(self, (PickupJudgement.ReminderAtField, Iso(PickupBy.AddHours(-6))))),
            (Buyer, T.PickupReminder));
    }

    [Fact]
    public void AReminderStampedOnlyAfterTheDeadline_TellsNobody()
    {
        var old = Judgeable();

        OrderChange(old, With(old, (PickupJudgement.ReminderAtField, Iso(PickupBy.AddSeconds(30))))).Should().BeEmpty();
    }

    // ===== the pickup: the judged outcomes (FUP-286) =====

    private static (string, object?)[] Judged(string outcome)
        => new (string, object?)[]
        {
            (PickupJudgement.Field, outcome),
            (PickupJudgement.JudgedAtField, Iso(PickupBy.AddMinutes(1))),
            (OrderCommitment.PickupOverdueNotifiedAtField, Iso(PickupBy.AddMinutes(1)))
        };

    [Fact]
    public void TruckerNoShow_TellsTheBuyerAndTheFarmerWhy_AndTheTruckerTheirCheckInWasMissed()
    {
        var old = Judgeable();
        var row = With(old, Judged(PickupJudgement.Outcome.TruckerNoShow).Concat(new (string, object?)[]
        {
            ("status", OrderCommitment.AwaitingTransportStatus),
            (OrderCommitment.TransportFailureField, PickupJudgement.TruckerNoShowFailure),
            (OrderCommitment.TruckerResponseField, PickupJudgement.NoShowResponse),
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(PickupBy.AddHours(48)))
        }).ToArray());

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.TransportFailed), (Farmer, T.TransportFailedKeepCrops), (Trucker, T.PickupMissed));
        events.Should().OnlyContain(e => e.Params[P.Reason] == PickupJudgement.TruckerNoShowFailure && e.Push);
        For(events, Buyer).DeadlineUtc.Should().Be(PickupBy.AddHours(48));
    }

    [Fact]
    public void FarmerNoShow_TellsAllThreeTheOrderWasCancelled_WithTheReason()
    {
        var old = Judgeable(false, PickupJudgement.Kind.ArrivedPickup);
        var row = With(old, Judged(PickupJudgement.Outcome.FarmerNoShow).Concat(new (string, object?)[]
        {
            ("status", "cancelled"), ("cancelReason", PickupJudgement.FarmerNoShowReason),
            (OrderCommitment.CancelledByField, OrderCommitment.Party.System),
            (OrderCommitment.FarmerResponseField, PickupJudgement.NoShowResponse)
        }).ToArray());

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderCancelled), (Farmer, T.OrderCancelled), (Trucker, T.OrderCancelled));
        events.Should().OnlyContain(e => e.Params[P.Reason] == PickupJudgement.FarmerNoShowReason);
    }

    [Fact]
    public void BuyerNoShow_TellsTheBuyerAndTheFarmer_WithTheReason()
    {
        var old = Judgeable(selfPickup: true);
        var row = With(old, Judged(PickupJudgement.Outcome.BuyerNoShow).Concat(new (string, object?)[]
        {
            ("status", "cancelled"), ("cancelReason", PickupJudgement.BuyerNoShowReason),
            (OrderCommitment.CancelledByField, OrderCommitment.Party.System)
        }).ToArray());

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderCancelled), (Farmer, T.OrderCancelled));
        events.Should().OnlyContain(e => e.Params[P.Reason] == PickupJudgement.BuyerNoShowReason);
    }

    [Fact]
    public void APickupJudgedScanned_TellsNobody_TheCropsLeftTheFarm()
    {
        var old = Judgeable(false, PickupJudgement.Kind.ArrivedPickup);

        OrderChange(old, With(old, Judged(PickupJudgement.Outcome.Scanned))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(PickupJudgement.Outcome.BothPresentNoScan)]
    [InlineData(PickupJudgement.Outcome.UncheckedCheckIn)]
    public void APickupJudgedForAnAdmin_TellsAllThree_ThatItIsUnderReview(string outcome)
    {
        var old = Judgeable(false, PickupJudgement.Kind.ArrivedPickup, PickupJudgement.Kind.CropsReady);

        var events = OrderChange(old, With(old, Judged(outcome)));

        Tells(events, (Buyer, T.PickupOverdue), (Farmer, T.PickupMissed), (Trucker, T.PickupMissed));
        events.Should().OnlyContain(e => e.Params[P.Reason] == outcome);
    }

    [Fact]
    public void TheFlagTurnedOff_TheP1NoticeKeepsItsOwnWords()
    {
        var old = Judgeable();
        var row = With(old, (PickupJudgement.Field, null), (OrderCommitment.PickupOverdueNotifiedAtField, Iso(PickupBy)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.PickupOverdue), (Farmer, T.PickupMissed), (Trucker, T.PickupMissed));
        events.Should().OnlyContain(e => !e.Params.ContainsKey(P.Reason));
    }

    // ===== the drop-off: the trucker's check-in and the reminder (FUP-338) =====

    private static Dictionary<string, object?> DropoffEntry(DateTime received, bool late = false) => new(StringComparer.Ordinal)
    {
        [PickupJudgement.EntryReceivedAt] = Iso(received),
        [PickupJudgement.EntryBy] = Trucker,
        [PickupJudgement.EntryDistanceChecked] = true,
        [PickupJudgement.EntryLate] = late,
        [PickupJudgement.EntryLatitude] = 9.01,
        [PickupJudgement.EntryLongitude] = 38.76,
        [PickupJudgement.EntryDistanceKm] = 0.8
    };

    [Fact]
    public void TheTruckersDropoffCheckIn_TellsTheBuyer_WithT9()
    {
        var old = OnTheRoad();
        var events = OrderChange(old, With(old, (DropoffJudgement.CheckInField, DropoffEntry(Received))));

        Tells(events, (Buyer, T.DeliveryArrived));
        var told = For(events, Buyer);
        var t9 = Received.AddHours(48);
        told.DeadlineUtc.Should().Be(t9);
        told.Params[P.DeadlineAt].Should().Be(Iso(t9));
        told.Params[P.TruckerName].Should().Be("Dawit K.");
        told.Params.Keys.Should().NotContain(new[] { "latitude", "longitude", "distanceKm" }, "never where the check-in was made");
        told.Push.Should().BeTrue();
    }

    [Fact]
    public void T9_IsTheOrderCronsOwn_ReadFromTheSsmWindow()
    {
        var timings = OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(30) };
        var old = OnTheRoad();
        var row = With(old, (DropoffJudgement.CheckInField, DropoffEntry(Received)));

        var told = For(OrderChange(old, row, () => timings), Buyer);

        told.DeadlineUtc.Should().Be(Received.AddHours(30));
        var asDict = new Dictionary<string, object>
        {
            [DropoffJudgement.Field] = DropoffJudgement.Outcome.Pending,
            [DropoffJudgement.DeadlineField] = Iso(T8),
            [DropoffJudgement.CheckInField] = new Dictionary<string, object>
            {
                [PickupJudgement.EntryReceivedAt] = Iso(Received), [PickupJudgement.EntryLate] = false
            }
        };
        told.DeadlineUtc.Should().Be(DropoffJudgement.SilentBuyerDeadline(asDict, timings), "the same rule as the order cron");
    }

    [Fact]
    public void ACheckInThatDoesNotCount_TellsNobody()
    {
        var old = OnTheRoad();
        OrderChange(old, With(old, (DropoffJudgement.CheckInField, DropoffEntry(Received, late: true)))).Should().BeEmpty();
        OrderChange(old, With(old, (DropoffJudgement.CheckInField, DropoffEntry(T8.AddMinutes(5))))).Should().BeEmpty();

        // The out-of-range attempt is no check-in: T9 never starts.
        OrderChange(old, With(old, (DropoffJudgement.AttemptField, DropoffEntry(Received)))).Should().BeEmpty();
    }

    [Fact]
    public void TheReminderMarker_TellsTheBuyerAgain_BeforeT9()
    {
        var old = OnTheRoad((DropoffJudgement.CheckInField, DropoffEntry(Received)));
        var events = OrderChange(old, With(old, (DropoffJudgement.ReminderAtField, Iso(Received.AddHours(42)))));

        Tells(events, (Buyer, T.DeliveryConfirmReminder));
        For(events, Buyer).DeadlineUtc.Should().Be(Received.AddHours(48));
        For(events, Buyer).Params[P.DeadlineAt].Should().Be(Iso(Received.AddHours(48)));

        OrderChange(old, With(old, (DropoffJudgement.ReminderAtField, Iso(Received.AddHours(48).AddSeconds(20)))))
            .Should().BeEmpty("a reminder stamped only after T9 tells nobody");
    }

    [Fact]
    public void TheTimings_AreReadOnlyForADropoffCheckIn()
    {
        Func<OrderTimings> throwing = () => throw new InvalidOperationException("SSM is not read for this record");

        var old = Judgeable();
        Tells(OrderChange(old, CheckIns(old, PickupJudgement.Kind.ArrivedPickup), throwing), (Farmer, T.TruckerArrived));
        OrderChange(OnTheRoad(), OnTheRoad(("updatedAt", Iso(RecordTime))), throwing).Should().BeEmpty();
    }

    // ===== the drop-off: the judged outcomes (FUP-338) =====

    [Fact]
    public void AutoConfirmed_TellsTheBuyerAndTheFarmerWhy_TheTruckerAsAnyDelivery()
    {
        var old = OnTheRoad((DropoffJudgement.CheckInField, DropoffEntry(Received)));
        var row = With(old, ("status", "delivered"),
            (DropoffJudgement.Field, DropoffJudgement.Outcome.AutoConfirmed), (DropoffJudgement.JudgedAtField, Iso(RecordTime)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.Delivered), (Farmer, T.DeliveredPayoutPending), (Trucker, T.DeliveredPayoutPending));
        For(events, Buyer).Params[P.Reason].Should().Be(DropoffJudgement.Outcome.AutoConfirmed);
        For(events, Buyer).DeadlineUtc.Should().Be(RecordTime + NotificationRules.DisputeWindow, "the buyer's 48 hours to report a problem");
        For(events, Farmer).Params[P.Reason].Should().Be(DropoffJudgement.Outcome.AutoConfirmed);
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00", "the farmer's own leg");
        For(events, Trucker).Params.Should().NotContainKey(P.Reason);
    }

    [Fact]
    public void ABuyersOwnConfirm_KeepsTheDeliveredWords()
    {
        var old = OnTheRoad((DropoffJudgement.CheckInField, DropoffEntry(Received)));
        var row = With(old, ("status", "delivered"), (DropoffJudgement.Field, DropoffJudgement.Outcome.Closed));

        OrderChange(old, row).Should().OnlyContain(e => !e.Params.ContainsKey(P.Reason));
    }

    [Theory]
    [InlineData(DropoffJudgement.OverdueReason)]
    [InlineData(DropoffJudgement.SilentBuyerReason)]
    [InlineData(DropoffJudgement.OutOfRangeReason)]
    public void TheSystemsDropoffDispute_TellsAllThreeParties_WithTheReason(string reason)
    {
        var old = OnTheRoad();
        var row = With(old, ("status", "disputed"), (DropoffJudgement.DisputeReasonField, reason),
            (DropoffJudgement.Field, reason), (DropoffJudgement.JudgedAtField, Iso(RecordTime)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.DisputeOpened), (Farmer, T.DisputeOpened), (Trucker, T.DisputeOpened));
        events.Should().OnlyContain(e => e.Params[P.Reason] == reason && e.Push && !e.Params.ContainsKey(P.Party));
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00");
        For(events, Trucker).Params[P.AmountEtb].Should().Be("1500.00");
    }

    [Fact]
    public void TheSystemsDropoffDispute_IsNotToldASecondTime_FromItsEscrow()
    {
        foreach (var reason in new[] { DropoffJudgement.OverdueReason, DropoffJudgement.SilentBuyerReason, DropoffJudgement.OutOfRangeReason })
        {
            var escrow = new Dictionary<string, object?>
            {
                ["escrowId"] = "escrow-order-order-1", ["orderId"] = "order-1", ["status"] = "escrowed",
                ["buyerId"] = Buyer, ["farmerId"] = Farmer, ["truckerId"] = Trucker, ["deliveryId"] = "delivery-1",
                ["amount"] = 42500
            };
            var disputed = With(escrow, ("status", "disputed"), ("disputedBy", DropoffJudgement.SystemActor),
                ("disputeReason", "other"), ("disputeDescription", OrderCronFunction.DisputeDescription(reason)));

            NotificationRules.Derive("escrows", "MODIFY", NotificationRulesTests.Image(escrow), NotificationRulesTests.Image(disputed), RecordTime)
                .Should().BeEmpty(reason);

            // Any other system dispute (the hourly sweep's freeze) is still told from the escrow.
            var swept = With(disputed, ("disputeDescription", "Frozen by the hourly sweep: the delivery or order was already disputed."));
            NotificationRules.Derive("escrows", "MODIFY", NotificationRulesTests.Image(escrow), NotificationRulesTests.Image(swept), RecordTime)
                .Should().HaveCount(3);
        }
    }

    [Fact]
    public void APartysDisputeOnTheOrder_StillTellsNobodyFromTheOrder()
    {
        var old = OnTheRoad();

        OrderChange(old, With(old, ("status", "disputed"))).Should().BeEmpty("the escrow row tells a party's dispute");
    }
}
