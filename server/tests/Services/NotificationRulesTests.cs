using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;
using P = WeredaConnect.Server.Services.NotificationParams;
using T = WeredaConnect.Server.Services.NotificationTypes;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-21: the event matrix of notifications-api.md section 3 as a table. One test per row at
/// least: who is told, which type, push or inbox only, the deadline and the key params; plus the
/// actor exclusion and the "strongest row wins" rule.
/// </summary>
public class NotificationRulesTests
{
    private static readonly DateTime RecordTime = new(2026, 10, 26, 8, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime FarmerDeadline = new(2026, 10, 27, 8, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime TruckerDeadline = new(2026, 10, 27, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BuyerChoiceDeadline = new(2026, 10, 28, 8, 15, 0, DateTimeKind.Utc);
    private const string PickupIso = "2026-10-29T06:00:00.0000000Z";

    private const string Buyer = "buyer-1";
    private const string Farmer = "farmer-1";
    private const string Trucker = "trucker-1";

    // ===== image helper =====

    /// <summary>
    /// A DynamoDB-JSON stream image from plain values: a string is <c>S</c>, a number <c>N</c>, a
    /// bool <c>BOOL</c>; a null value leaves the attribute out.
    /// </summary>
    internal static StreamImage Image(IReadOnlyDictionary<string, object?> attributes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in attributes)
            {
                if (value == null)
                {
                    continue;
                }

                writer.WriteStartObject(key);
                switch (value)
                {
                    case string s:
                        writer.WriteString("S", s);
                        break;
                    case bool b:
                        writer.WriteBoolean("BOOL", b);
                        break;
                    case IFormattable number:
                        writer.WriteString("N", number.ToString(null, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new ArgumentException($"Unsupported attribute type for {key}");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        return new StreamImage(doc.RootElement.Clone());
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> row, params (string Key, object? Value)[] changes)
    {
        var copy = new Dictionary<string, object?>(row, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>A trip order: the buyer, the farmer and the trucker, 500 kg teff for 42,500 ETB.</summary>
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
            ["buyerPhone"] = "+251911000001",
            ["farmerName"] = "Chaltu G.",
            ["farmerPhone"] = "+251911000002",
            ["truckerName"] = "Dawit K.",
            ["cropType"] = "teff",
            ["quantity"] = 500,
            ["cropCost"] = 40000,
            ["transportCost"] = 1500,
            ["orderTotal"] = 42500,
            [OrderCommitment.FulfillmentField] = OrderCommitment.Fulfillment.Trucker
        }, changes);

    /// <summary>A self-pickup order: no trip, no trucker.</summary>
    private static Dictionary<string, object?> SelfPickupOrder(string status, params (string Key, object? Value)[] changes)
        => With(TripOrder(status, ("truckerId", null), ("tripId", null), ("truckerName", null), ("transportCost", 0),
            ("orderTotal", 41000), (OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.SelfPickup)), changes);

    private static IReadOnlyList<NotificationEvent> OrderChange(Dictionary<string, object?> old, Dictionary<string, object?> row)
        => NotificationRules.Derive("orders", "MODIFY", Image(old), Image(row), RecordTime);

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static NotificationEvent For(IReadOnlyList<NotificationEvent> events, string recipient)
    {
        events.Where(e => e.RecipientUserId == recipient).Should().ContainSingle($"exactly one event for {recipient}");
        return events.Single(e => e.RecipientUserId == recipient);
    }

    private static void Tells(IReadOnlyList<NotificationEvent> events, params (string Recipient, string Type)[] expected)
        => events.Select(e => (e.RecipientUserId, e.Type)).Should().BeEquivalentTo(expected);

    // ===== orders: payment and the farmer's answer =====

    [Fact]
    public void Funded_TellsBuyerPaymentConfirmed_AndAsksFarmerUntilTheDeadline()
    {
        var events = OrderChange(
            TripOrder("payment_submitted"),
            TripOrder("funded",
                (OrderCommitment.FarmerResponseField, "pending"),
                (OrderCommitment.TruckerResponseField, "not_asked"),
                (OrderCommitment.FarmerDeadlineAtField, Iso(FarmerDeadline))));

        Tells(events, (Buyer, T.PaymentConfirmed), (Farmer, T.OrderRequest));

        var buyer = For(events, Buyer);
        buyer.Push.Should().BeTrue();
        buyer.DeadlineUtc.Should().BeNull();
        buyer.Params[P.AmountEtb].Should().Be("42500.00");

        var farmer = For(events, Farmer);
        farmer.Entity.Should().Be("order");
        farmer.EntityId.Should().Be("order-1");
        farmer.OrderId.Should().Be("order-1");
        farmer.Push.Should().BeTrue();
        farmer.DeadlineUtc.Should().Be(FarmerDeadline);
        farmer.Params[P.DeadlineAt].Should().Be(Iso(FarmerDeadline));
        farmer.Params[P.BuyerName].Should().Be("Abebe T.");
        farmer.Params[P.CropType].Should().Be("teff");
        farmer.Params[P.QuantityKg].Should().Be("500");
        farmer.Params[P.AmountEtb].Should().Be("42500.00");
    }

    [Fact]
    public void FarmerAccept_OnTripOrder_TellsBuyer_AndAsksTrucker_NeverTheFarmer()
    {
        var old = TripOrder("funded",
            (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "not_asked"));
        var row = With(old,
            (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "pending"),
            (OrderCommitment.TruckerDeadlineAtField, Iso(TruckerDeadline)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.FarmerAccepted), (Trucker, T.TransportRequest));
        For(events, Buyer).DeadlineUtc.Should().BeNull();
        var trucker = For(events, Trucker);
        trucker.DeadlineUtc.Should().Be(TruckerDeadline);
        trucker.Params[P.DeadlineAt].Should().Be(Iso(TruckerDeadline));
        trucker.Params[P.FarmerName].Should().Be("Chaltu G.");
    }

    [Fact]
    public void FarmerAccept_WhileTruckerNotAsked_TellsOnlyTheBuyer()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "not_asked"));
        var row = With(old, (OrderCommitment.FarmerResponseField, "accepted"));

        Tells(OrderChange(old, row), (Buyer, T.FarmerAccepted));
    }

    [Fact]
    public void Accepted_TripOrder_TellsBuyerAndFarmer_NotTheTruckerWhoAccepted_WithPickupDate()
    {
        var old = TripOrder("funded",
            (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "pending"));
        var row = With(old,
            ("status", "accepted"),
            (OrderCommitment.TruckerResponseField, "accepted"),
            (OrderCommitment.PickupDateField, PickupIso),
            ("pickupDeadlineAt", "2026-10-31T06:00:00.0000000Z"));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderConfirmed), (Farmer, T.OrderConfirmed));
        events.Should().OnlyContain(e => e.Params[P.PickupDate] == PickupIso && e.DeadlineUtc == null && e.Push);
    }

    [Fact]
    public void Accepted_SelfPickup_FarmerAccept_TellsOnlyTheBuyer_OrderConfirmed()
    {
        var old = SelfPickupOrder("funded",
            (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "none"));
        var row = With(old,
            ("status", "accepted"),
            (OrderCommitment.FarmerResponseField, "accepted"),
            ("pickupDeadlineAt", "2026-11-02T08:15:00.0000000Z"));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderConfirmed));
        events.Should().NotContain(e => e.Type == T.FarmerAccepted || e.Type == T.TransportRequest);
        For(events, Buyer).Params[P.PickupDate].Should().Be("2026-11-02T08:15:00.0000000Z");
    }

    [Fact]
    public void Accepted_LegacyTripOrderWithoutResponseFields_TellsBuyerAndTrucker_NotTheFarmerWhoAccepted()
    {
        var events = OrderChange(TripOrder("funded"), TripOrder("accepted"));

        Tells(events, (Buyer, T.OrderConfirmed), (Trucker, T.OrderConfirmed));
    }

    [Fact]
    public void Accepted_WhileTruckerHasNotAccepted_LeavesTheTruckerOut()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "not_asked"));
        var row = With(old, ("status", "accepted"), (OrderCommitment.FarmerResponseField, "accepted"));

        Tells(OrderChange(old, row), (Buyer, T.OrderConfirmed));
    }

    [Fact]
    public void Rejected_TellsOnlyTheBuyer_WithTheDeclineCode()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "pending"));
        var row = With(old, ("status", "rejected"),
            (OrderCommitment.FarmerResponseField, "declined"),
            (OrderCommitment.DeclineReasonField, "price"));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderRejected));
        For(events, Buyer).Params[P.Reason].Should().Be("price");
    }

    // ===== orders: transport failure =====

    [Fact]
    public void AwaitingTransport_TruckerDeclined_TellsBuyerToDecide_AndFarmerToKeepCrops()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "pending"));
        var row = With(old, ("status", OrderCommitment.AwaitingTransportStatus),
            (OrderCommitment.TruckerResponseField, "declined"),
            (OrderCommitment.TransportFailureField, "trucker_declined"),
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(BuyerChoiceDeadline)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.TransportFailed), (Farmer, T.TransportFailedKeepCrops));
        var buyer = For(events, Buyer);
        buyer.DeadlineUtc.Should().Be(BuyerChoiceDeadline);
        buyer.Params[P.Reason].Should().Be("trucker_declined");
        buyer.Params[P.DeadlineAt].Should().Be(Iso(BuyerChoiceDeadline));
        var farmer = For(events, Farmer);
        farmer.DeadlineUtc.Should().BeNull();
        farmer.Params[P.Reason].Should().Be("trucker_declined");
        farmer.Params.Should().NotContainKey(P.DeadlineAt);
    }

    [Fact]
    public void AwaitingTransport_TruckerLetItExpire_TellsTheTruckerRequestExpired()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "pending"));
        var row = With(old, ("status", OrderCommitment.AwaitingTransportStatus),
            (OrderCommitment.TruckerResponseField, "expired"),
            (OrderCommitment.TransportFailureField, "trucker_expired"),
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(BuyerChoiceDeadline)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.TransportFailed), (Farmer, T.TransportFailedKeepCrops), (Trucker, T.RequestExpired));
        For(events, Trucker).Params[P.Reason].Should().Be("trucker_expired");
    }

    [Fact]
    public void AwaitingTransport_TripFullRightAfterFunding_TellsTheBuyer_AndClosesTheFarmersRequest()
    {
        // PR 1 funds first (the farmer got order_request), then the capacity check moves the
        // order on in a second write.
        var old = TripOrder("funded",
            (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "not_asked"),
            (OrderCommitment.FarmerDeadlineAtField, Iso(FarmerDeadline)));
        var row = With(old,
            ("status", OrderCommitment.AwaitingTransportStatus),
            (OrderCommitment.TruckerResponseField, "failed_capacity"),
            (OrderCommitment.TransportFailureField, "trip_full"),
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(BuyerChoiceDeadline)));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.TransportFailed), (Farmer, T.RequestOnHold));
        For(events, Farmer).Params[P.Reason].Should().Be("trip_full");
        For(events, Farmer).DeadlineUtc.Should().BeNull();
    }

    // ===== orders: cancel =====

    [Theory]
    [InlineData("pending_payment", "Auto-cancelled: no payment within 24 hours.")]
    [InlineData("created", "unpaid_expired")]
    public void Cancelled_UnpaidByTheCron_TellsTheBuyerOrderExpiredUnpaid(string from, string cancelReason)
    {
        var events = OrderChange(TripOrder(from), TripOrder("cancelled", ("cancelReason", cancelReason)));

        Tells(events, (Buyer, T.OrderExpiredUnpaid));
        For(events, Buyer).Params[P.Reason].Should().Be("unpaid_expired");
        For(events, Buyer).DeadlineUtc.Should().BeNull();
    }

    [Fact]
    public void Cancelled_BuyersOwnUnpaidCancel_TellsNobody()
    {
        var events = OrderChange(TripOrder("pending_payment"),
            TripOrder("cancelled", ("cancelReason", "buyer_withdrew"), (OrderCommitment.CancelledByField, "buyer")));

        events.Should().BeEmpty();
    }

    [Fact]
    public void Cancelled_FarmerLetTheRequestExpire_TellsBuyerCancelled_AndFarmerExpired()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.TruckerResponseField, "not_asked"));
        var row = With(old, ("status", "cancelled"),
            ("cancelReason", "farmer_response_expired"),
            (OrderCommitment.FarmerResponseField, "expired"),
            (OrderCommitment.CancelledByField, "system"));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderCancelled), (Farmer, T.RequestExpired));
        For(events, Buyer).Params[P.Reason].Should().Be("farmer_response_expired");
        For(events, Buyer).Params[P.CancelledBy].Should().Be("system");
    }

    [Fact]
    public void Cancelled_ByTheFarmer_FromAwaitingTransport_LeavesTheFarmerAndTheDecliningTruckerOut()
    {
        var old = TripOrder(OrderCommitment.AwaitingTransportStatus,
            (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "declined"));
        var row = With(old, ("status", "cancelled"), ("cancelReason", "farmer_cancelled"),
            (OrderCommitment.CancelledByField, "farmer"));

        var events = OrderChange(old, row);

        Tells(events, (Buyer, T.OrderCancelled));
        For(events, Buyer).Params[P.CancelledBy].Should().Be("farmer");
    }

    [Fact]
    public void Cancelled_ByTheBuyer_AfterATransportFailure_TellsFarmerAndCommittedTrucker_NotTheBuyer()
    {
        var old = TripOrder("accepted", (OrderCommitment.FarmerResponseField, "accepted"),
            (OrderCommitment.TruckerResponseField, "accepted"));
        var row = With(old, ("status", "cancelled"), ("cancelReason", "transport_failed_buyer_cancelled"),
            (OrderCommitment.CancelledByField, "buyer"));

        var events = OrderChange(old, row);

        Tells(events, (Farmer, T.OrderCancelled), (Trucker, T.OrderCancelled));
        For(events, Farmer).Params[P.Reason].Should().Be("transport_failed_buyer_cancelled");
    }

    [Fact]
    public void Cancelled_LegacyBuyerCancelWithOwnText_IsTheBuyers_AndTheTextNeverBecomesAReason()
    {
        var events = OrderChange(TripOrder("funded"),
            TripOrder("cancelled", ("cancelReason", "Found cheaper teff, call me on 0911223344")));

        Tells(events, (Farmer, T.OrderCancelled));
        var farmer = For(events, Farmer);
        farmer.Params.Should().NotContainKey(P.Reason);
        farmer.Params[P.CancelledBy].Should().Be("buyer");
    }

    [Fact]
    public void Cancelled_PaidOrderWithTheUnpaidCronsSentence_IsStillTheBuyers()
    {
        // Only the unpaid cron writes "Auto-cancelled..."; on a paid order it is a client's text.
        var events = OrderChange(TripOrder("funded"),
            TripOrder("cancelled", ("cancelReason", "Auto-cancelled: no payment within 24 hours.")));

        Tells(events, (Farmer, T.OrderCancelled));
        For(events, Farmer).Params[P.CancelledBy].Should().Be("buyer");
        For(events, Farmer).Params.Should().NotContainKey(P.Reason);
    }

    [Fact]
    public void Names_AreStoredWithoutPhoneNumbers()
    {
        var old = TripOrder("payment_submitted", ("buyerName", "Abebe 0911 223 344"));
        var row = TripOrder("funded", ("buyerName", "Abebe 0911 223 344"),
            (OrderCommitment.FarmerResponseField, "pending"),
            (OrderCommitment.FarmerDeadlineAtField, Iso(FarmerDeadline)));

        var events = OrderChange(old, row);

        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e => e.Params[P.BuyerName] == "Abebe");
    }

    [Fact]
    public void Cancelled_BySweepRefund_WithoutCancelledBy_TellsEveryPaidParty()
    {
        var events = OrderChange(TripOrder("funded", (OrderCommitment.TruckerResponseField, "pending")),
            TripOrder("cancelled", ("cancelReason", "escrow_refunded"), (OrderCommitment.TruckerResponseField, "pending")));

        Tells(events, (Buyer, T.OrderCancelled), (Farmer, T.OrderCancelled), (Trucker, T.OrderCancelled));
        events.Should().OnlyContain(e => e.Params[P.CancelledBy] == "system" && e.Params[P.Reason] == "escrow_refunded");
    }

    [Fact]
    public void Cancelled_ByTheTrucker_LeavesTheTruckerOut()
    {
        var events = OrderChange(TripOrder("accepted", (OrderCommitment.TruckerResponseField, "accepted")),
            TripOrder("cancelled", (OrderCommitment.TruckerResponseField, "accepted"),
                (OrderCommitment.CancelledByField, "trucker"), ("cancelReason", "escrow_refunded")));

        Tells(events, (Buyer, T.OrderCancelled), (Farmer, T.OrderCancelled));
    }

    // ===== orders: payment failure, logistics, money =====

    [Fact]
    public void PaymentFailed_TellsTheBuyer()
    {
        var events = OrderChange(TripOrder("payment_submitted"), TripOrder("payment_failed"));

        Tells(events, (Buyer, T.PaymentFailed));
        For(events, Buyer).Push.Should().BeTrue();
    }

    [Fact]
    public void PickupConfirmed_TellsBuyerPickedUp_AndFarmerHandoffRecorded()
    {
        Tells(OrderChange(TripOrder("accepted"), TripOrder("pickup_confirmed")),
            (Buyer, T.PickedUp), (Farmer, T.HandoffRecorded));
    }

    [Fact]
    public void InTransit_TellsTheBuyer()
    {
        Tells(OrderChange(TripOrder("pickup_confirmed"), TripOrder("in_transit")), (Buyer, T.InTransit));
    }

    [Fact]
    public void Delivered_OpensTheBuyersWindow_AndTellsThePayeesTheirOwnLeg()
    {
        var events = OrderChange(TripOrder("in_transit", (OrderCommitment.TruckerResponseField, "accepted")),
            TripOrder("delivered", (OrderCommitment.TruckerResponseField, "accepted")));

        Tells(events, (Buyer, T.Delivered), (Farmer, T.DeliveredPayoutPending), (Trucker, T.DeliveredPayoutPending));
        var buyer = For(events, Buyer);
        buyer.DeadlineUtc.Should().Be(RecordTime.AddHours(48));
        buyer.Params[P.DeadlineAt].Should().Be(Iso(RecordTime.AddHours(48)));
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00");
        For(events, Trucker).Params[P.AmountEtb].Should().Be("1500.00");
        For(events, Farmer).DeadlineUtc.Should().BeNull();
    }

    [Fact]
    public void Disbursing_TellsNobody_ThePayeesHearFromTheEscrow()
    {
        OrderChange(TripOrder("delivered"), TripOrder("disbursing")).Should().BeEmpty();
        OrderChange(SelfPickupOrder("delivered"), SelfPickupOrder("disbursing")).Should().BeEmpty();
    }

    [Fact]
    public void Settled_TellsOnlyTheBuyer()
    {
        var events = OrderChange(TripOrder("disbursing"), TripOrder("settled"));

        Tells(events, (Buyer, T.OrderSettled));
        For(events, Buyer).Params[P.AmountEtb].Should().Be("42500.00");
    }

    [Theory]
    [InlineData("delivered", "disputed")]
    [InlineData("disputed", "dispute_resolved")]
    public void DisputeStatusesOnTheOrder_TellNobody_TheEscrowCarriesThem(string from, string to)
    {
        OrderChange(TripOrder(from), TripOrder(to)).Should().BeEmpty();
    }

    [Fact]
    public void UnchangedOrder_TellsNobody()
    {
        OrderChange(TripOrder("accepted"), TripOrder("accepted", ("updatedAt", "2026-10-26T09:00:00Z"))).Should().BeEmpty();
    }

    // ===== orders: markers =====

    [Fact]
    public void ReminderMarkers_TellThePartyWhoseAnswerIsDue_UntilItsDeadline()
    {
        var farmerOld = TripOrder("funded", (OrderCommitment.FarmerDeadlineAtField, Iso(FarmerDeadline)));
        var farmerEvents = OrderChange(farmerOld, With(farmerOld, (OrderCommitment.FarmerReminderAtField, Iso(RecordTime))));
        Tells(farmerEvents, (Farmer, T.RequestReminder));
        For(farmerEvents, Farmer).DeadlineUtc.Should().Be(FarmerDeadline);
        For(farmerEvents, Farmer).Params[P.DeadlineAt].Should().Be(Iso(FarmerDeadline));

        var truckerOld = TripOrder("funded", (OrderCommitment.TruckerDeadlineAtField, Iso(TruckerDeadline)));
        var truckerEvents = OrderChange(truckerOld, With(truckerOld, (OrderCommitment.TruckerReminderAtField, Iso(RecordTime))));
        Tells(truckerEvents, (Trucker, T.RequestReminder));
        For(truckerEvents, Trucker).DeadlineUtc.Should().Be(TruckerDeadline);

        var buyerOld = TripOrder(OrderCommitment.AwaitingTransportStatus,
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(BuyerChoiceDeadline)));
        var buyerEvents = OrderChange(buyerOld, With(buyerOld, (OrderCommitment.BuyerChoiceReminderAtField, Iso(RecordTime))));
        Tells(buyerEvents, (Buyer, T.RequestReminder));
        For(buyerEvents, Buyer).DeadlineUtc.Should().Be(BuyerChoiceDeadline);
    }

    [Fact]
    public void AMarkerAlreadyStamped_DoesNotFireAgain()
    {
        var old = TripOrder("funded", (OrderCommitment.FarmerReminderAtField, Iso(RecordTime)));

        OrderChange(old, With(old, ("updatedAt", "2026-10-26T10:00:00Z"))).Should().BeEmpty();
    }

    [Fact]
    public void PickupOverdueMarker_TellsBuyerOverdue_AndFarmerAndTruckerMissed()
    {
        var old = TripOrder("accepted");
        var events = OrderChange(old, With(old, (OrderCommitment.PickupOverdueNotifiedAtField, Iso(RecordTime))));

        Tells(events, (Buyer, T.PickupOverdue), (Farmer, T.PickupMissed), (Trucker, T.PickupMissed));
        For(events, Buyer).Push.Should().BeTrue();

        var selfOld = SelfPickupOrder("accepted");
        Tells(OrderChange(selfOld, With(selfOld, (OrderCommitment.PickupOverdueNotifiedAtField, Iso(RecordTime)))),
            (Buyer, T.PickupOverdue), (Farmer, T.PickupMissed));
    }

    [Fact]
    public void OneWrite_TellsEachPartyOnce_TheStrongestRowWins()
    {
        var old = TripOrder("accepted");
        var row = TripOrder("pickup_confirmed", (OrderCommitment.PickupOverdueNotifiedAtField, Iso(RecordTime)));

        Tells(OrderChange(old, row), (Buyer, T.PickedUp), (Farmer, T.HandoffRecorded), (Trucker, T.PickupMissed));
    }

    [Fact]
    public void OrderParams_NeverCarryAPhoneNumber()
    {
        var events = OrderChange(TripOrder("in_transit"), TripOrder("delivered"));

        events.Should().NotBeEmpty();
        events.SelectMany(e => e.Params.Values).Should().NotContain(v => v.Contains("+251"));
    }

    [Theory]
    [InlineData("INSERT")]
    [InlineData("REMOVE")]
    public void OrdersOtherThanModify_AreIgnored(string eventName)
    {
        NotificationRules.Derive("orders", eventName, Image(TripOrder("payment_submitted")), Image(TripOrder("funded")), RecordTime)
            .Should().BeEmpty();
    }

    [Fact]
    public void OrderModifyWithoutTheOldImage_IsIgnored()
    {
        NotificationRules.Derive("orders", "MODIFY", new StreamImage(null), Image(TripOrder("funded")), RecordTime)
            .Should().BeEmpty();
    }

    // ===== deliveries =====

    private static Dictionary<string, object?> Delivery(string? deliveryType, params (string Key, object? Value)[] changes)
        => With(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deliveryId"] = "delivery-1",
            ["orderId"] = "order-1",
            ["status"] = "pending",
            ["deliveryType"] = deliveryType,
            ["buyerId"] = Buyer,
            ["farmerId"] = Farmer,
            ["truckerId"] = deliveryType == "self_pickup" ? null : Trucker,
            ["buyerName"] = "Abebe T.",
            ["farmerName"] = "Chaltu G.",
            ["truckerName"] = deliveryType == "self_pickup" ? null : "Dawit K.",
            ["truckerPhone"] = deliveryType == "self_pickup" ? null : "+251911000003"
        }, changes);

    [Fact]
    public void DeliveryCreatedByTheTrucker_TellsBuyerAndFarmer_DeliveryScheduled()
    {
        var events = NotificationRules.Derive("deliveries", "INSERT", new StreamImage(null),
            Image(Delivery("trucker", (OrderCommitment.PickupDateField, PickupIso))), RecordTime);

        Tells(events, (Buyer, T.DeliveryScheduled), (Farmer, T.DeliveryScheduled));
        var buyer = For(events, Buyer);
        buyer.Entity.Should().Be("delivery");
        buyer.EntityId.Should().Be("delivery-1");
        buyer.OrderId.Should().Be("order-1");
        buyer.Params[P.TruckerName].Should().Be("Dawit K.");
        buyer.Params[P.PickupDate].Should().Be(PickupIso);
        events.SelectMany(e => e.Params.Values).Should().NotContain(v => v.Contains("+251"));
    }

    [Fact]
    public void DeliveryWithoutAType_IsATruckerDelivery()
    {
        var events = NotificationRules.Derive("deliveries", "INSERT", new StreamImage(null), Image(Delivery(null)), RecordTime);

        Tells(events, (Buyer, T.DeliveryScheduled), (Farmer, T.DeliveryScheduled));
        For(events, Buyer).Params.Should().NotContainKey(P.PickupDate);
    }

    [Fact]
    public void SelfPickupCreatedByTheBuyer_TellsOnlyTheFarmer_BuyerCollecting()
    {
        var events = NotificationRules.Derive("deliveries", "INSERT", new StreamImage(null),
            Image(Delivery("self_pickup")), RecordTime);

        Tells(events, (Farmer, T.BuyerCollecting));
        For(events, Farmer).Params[P.BuyerName].Should().Be("Abebe T.");
    }

    [Fact]
    public void DeliveryModify_IsIgnored()
    {
        var old = Delivery("trucker");
        NotificationRules.Derive("deliveries", "MODIFY", Image(old), Image(With(old, ("status", "pickedUp"))), RecordTime)
            .Should().BeEmpty();
    }

    // ===== deliveries: the check-in stamp (LT-07) =====

    private static IReadOnlyList<NotificationEvent> NearbyStamp(params (string Key, object? Value)[] stamp)
    {
        var old = Delivery("trucker", ("status", "inTransit"), ("cropType", "teff"), ("quantity", 500));
        return NotificationRules.Derive("deliveries", "MODIFY", Image(old), Image(With(old, stamp)), RecordTime);
    }

    [Fact]
    public void NearbyStamp_TellsOnlyTheBuyer_TruckNearby_WithTheDistanceAndTheMinutes()
    {
        var events = NearbyStamp(
            (CheckInEffects.NearbyNotifiedAtField, "2026-10-26T08:14:58.000Z"),
            (CheckInEffects.NearbyDistanceKmField, 3.2),
            (CheckInEffects.NearbyMinutesField, 25));

        Tells(events, (Buyer, T.TruckNearby));
        var buyer = For(events, Buyer);
        buyer.Push.Should().BeTrue();
        buyer.Entity.Should().Be("delivery");
        buyer.EntityId.Should().Be("delivery-1");
        buyer.OrderId.Should().Be("order-1");
        buyer.DeadlineUtc.Should().BeNull();
        buyer.Params[P.TruckerName].Should().Be("Dawit K.");
        buyer.Params[P.DistanceKm].Should().Be("3.2");
        buyer.Params[P.Minutes].Should().Be("25");
        buyer.Params[P.CropType].Should().Be("teff");
        buyer.Params[P.QuantityKg].Should().Be("500");
        buyer.Params.Values.Should().NotContain(v => v.Contains("+251"));
    }

    [Fact]
    public void NearbyStamp_AWindowOpeningNow_LeavesTheMinutesOut()
    {
        var events = NearbyStamp(
            (CheckInEffects.NearbyNotifiedAtField, "2026-10-26T08:14:58.000Z"),
            (CheckInEffects.NearbyDistanceKmField, 4),
            (CheckInEffects.NearbyMinutesField, 0));

        var buyer = For(events, Buyer);
        buyer.Params[P.DistanceKm].Should().Be("4.0");
        buyer.Params.Should().NotContainKey(P.Minutes);
    }

    [Fact]
    public void NearbyStamp_WithoutItsNumbers_StillTellsTheBuyer()
    {
        var buyer = For(NearbyStamp((CheckInEffects.NearbyNotifiedAtField, "2026-10-26T08:14:58.000Z")), Buyer);

        buyer.Type.Should().Be(T.TruckNearby);
        buyer.Params.Should().NotContainKey(P.DistanceKm).And.NotContainKey(P.Minutes);
    }

    [Fact]
    public void NearbyStamp_IsTold_OnlyWhenItAppears()
    {
        // A later write (the delivery confirmed) keeps the stamp: nobody is told again.
        var stamped = Delivery("trucker", ("status", "inTransit"),
            (CheckInEffects.NearbyNotifiedAtField, "2026-10-26T08:14:58.000Z"), (CheckInEffects.NearbyDistanceKmField, 3.2));

        NotificationRules.Derive("deliveries", "MODIFY", Image(stamped), Image(With(stamped, ("status", "delivered"))), RecordTime)
            .Should().BeEmpty();
        NotificationRules.Derive("deliveries", "MODIFY", Image(stamped), Image(stamped), RecordTime)
            .Should().BeEmpty();
    }

    [Fact]
    public void NearbyStamp_OnAnInsert_IsNotTruckNearby()
    {
        var events = NotificationRules.Derive("deliveries", "INSERT", new StreamImage(null),
            Image(Delivery("trucker", (CheckInEffects.NearbyNotifiedAtField, "2026-10-26T08:14:58.000Z"))), RecordTime);

        events.Should().NotContain(e => e.Type == T.TruckNearby);
    }

    // ===== escrows =====

    private const string EscrowId = "escrow-order-order-1";

    private static Dictionary<string, object?> Escrow(string status, params (string Key, object? Value)[] changes)
        => With(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId,
            ["orderId"] = "order-1",
            ["deliveryId"] = "delivery-1",
            ["status"] = status,
            ["buyerId"] = Buyer,
            ["farmerId"] = Farmer,
            ["truckerId"] = Trucker,
            ["buyerName"] = "Abebe T.",
            ["farmerName"] = "Chaltu G.",
            ["amount"] = 42500,
            ["farmerPayout"] = 40000,
            ["truckerPayout"] = 1500,
            ["commission"] = 1000
        }, changes);

    private static IReadOnlyList<NotificationEvent> EscrowChange(Dictionary<string, object?> old, Dictionary<string, object?> row)
        => NotificationRules.Derive("escrows", "MODIFY", Image(old), Image(row), RecordTime);

    [Fact]
    public void EscrowHoldOrDispute_BeforeADelivery_LeavesTheNotYetCommittedTruckerOut()
    {
        var held = EscrowChange(Escrow("escrowed", ("deliveryId", null)), Escrow(EscrowHoldFields.Status, ("deliveryId", null)));
        var disputed = EscrowChange(Escrow("escrowed", ("deliveryId", null)),
            Escrow("disputed", ("deliveryId", null), ("disputedBy", Buyer)));

        held.Should().NotContain(e => e.RecipientUserId == Trucker);
        disputed.Should().NotContain(e => e.RecipientUserId == Trucker);
        Tells(disputed, (Buyer, T.DisputeReceived), (Farmer, T.DisputeOpened));
    }

    [Fact]
    public void EscrowRefunded_TellsTheBuyer_TheRefundedAmount()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("refunded"));

        Tells(events, (Buyer, T.RefundSent));
        var buyer = For(events, Buyer);
        buyer.Entity.Should().Be("escrow");
        buyer.EntityId.Should().Be(EscrowId);
        buyer.OrderId.Should().Be("order-1");
        buyer.Params[P.AmountEtb].Should().Be("42500.00");
    }

    [Fact]
    public void EscrowReleased_TellsTheFarmer_TheirPayout()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("released"));

        Tells(events, (Farmer, T.PayoutSent));
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00");
    }

    [Fact]
    public void EscrowReleased_WithTheTruckerLegHeldByAPayoutCheck_TellsTheTruckerPayoutBlocked()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("released",
            (TruckerPayoutFields.Pending, true), (TruckerPayoutFields.BlockReason, "fayda_required")));

        Tells(events, (Farmer, T.PayoutSent), (Trucker, T.PayoutBlocked));
        var trucker = For(events, Trucker);
        trucker.Params[P.Reason].Should().Be("fayda_required");
        trucker.Params[P.AmountEtb].Should().Be("1500.00");
        trucker.Push.Should().BeTrue();
        trucker.DeadlineUtc.Should().BeNull();
    }

    [Fact]
    public void EscrowReleased_WithTheTruckerLegPendingOnTheGateway_DoesNotTellTheTruckerBlocked()
    {
        Tells(EscrowChange(Escrow("escrowed"), Escrow("released", (TruckerPayoutFields.Pending, true))),
            (Farmer, T.PayoutSent));
    }

    [Fact]
    public void TruckerBlockReason_StampedByTheRetry_TellsTheTruckerPayoutBlocked()
    {
        var old = Escrow("released", (TruckerPayoutFields.Pending, true));
        var events = EscrowChange(old, With(old, (TruckerPayoutFields.BlockReason, "phone_unverified")));

        Tells(events, (Trucker, T.PayoutBlocked));
        For(events, Trucker).Params[P.Reason].Should().Be("phone_unverified");
    }

    [Fact]
    public void EscrowCompleted_TellsTheTrucker_TheTransportLeg()
    {
        var events = EscrowChange(Escrow("released"), Escrow("completed"));

        Tells(events, (Trucker, T.PayoutSent));
        For(events, Trucker).Params[P.AmountEtb].Should().Be("1500.00");

        EscrowChange(Escrow("released", ("truckerPayout", null), ("truckerId", null)),
            Escrow("completed", ("truckerPayout", null), ("truckerId", null))).Should().BeEmpty();
    }

    [Fact]
    public void LegacyCompletedBackToReleased_DoesNotPayTheFarmerAgain()
    {
        EscrowChange(Escrow("completed"), Escrow("released")).Should().BeEmpty();
    }

    [Fact]
    public void FarmerPayoutCheckBlock_TellsTheFarmerPayoutBlocked_ATradeBlockTellsNobody()
    {
        var events = EscrowChange(Escrow("escrowed"),
            Escrow("escrowed", (SettlementBlockFields.Reason, "release:payout:phone_unverified")));

        Tells(events, (Farmer, T.PayoutBlocked));
        For(events, Farmer).Params[P.Reason].Should().Be("phone_unverified");
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00");

        EscrowChange(Escrow("escrowed"), Escrow("escrowed", (SettlementBlockFields.Reason, "release:trade:order_not_delivered")))
            .Should().BeEmpty();
        var stamped = Escrow("escrowed", (SettlementBlockFields.Reason, "release:payout:phone_unverified"));
        EscrowChange(stamped, With(stamped, ("updatedAt", "2026-10-26T10:00:00Z"))).Should().BeEmpty();
    }

    [Fact]
    public void ManualPayoutQueueOpened_TellsEachPayeeWhoseLegWaits()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("escrowed",
            (ManualPayoutFields.Marker, "open"), (ManualPayoutFields.Op, "release"),
            ("manualFarmerStatus", "pending"), ("manualTruckerStatus", "pending")));

        Tells(events, (Farmer, T.PayoutProcessing), (Trucker, T.PayoutProcessing));
        For(events, Farmer).Params[P.AmountEtb].Should().Be("40000.00");
        For(events, Trucker).Params[P.AmountEtb].Should().Be("1500.00");

        Tells(EscrowChange(Escrow("escrowed"), Escrow("escrowed",
                (ManualPayoutFields.Marker, "open"), (ManualPayoutFields.Op, "release"),
                ("manualFarmerStatus", "paid"), ("manualTruckerStatus", "pending"))),
            (Trucker, T.PayoutProcessing));

        EscrowChange(Escrow("escrowed"), Escrow("escrowed",
                (ManualPayoutFields.Marker, "open"), (ManualPayoutFields.Op, "refund"), ("manualBuyerStatus", "pending")))
            .Should().BeEmpty();
    }

    [Fact]
    public void Disputed_TellsTheOtherPartiesDisputeOpened_AndTheDisputerAnInboxOnlyReceipt()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("disputed", ("disputedBy", Buyer)));

        Tells(events, (Buyer, T.DisputeReceived), (Farmer, T.DisputeOpened), (Trucker, T.DisputeOpened));
        For(events, Buyer).Push.Should().BeFalse();
        For(events, Farmer).Push.Should().BeTrue();
        For(events, Farmer).Params[P.Party].Should().Be("buyer");
        For(events, Trucker).Params[P.Party].Should().Be("buyer");
    }

    [Fact]
    public void Disputed_ByTheSystem_TellsEveryParty()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("disputed", ("disputedBy", "system")));

        Tells(events, (Buyer, T.DisputeOpened), (Farmer, T.DisputeOpened), (Trucker, T.DisputeOpened));
        events.Should().OnlyContain(e => !e.Params.ContainsKey(P.Party));
    }

    [Fact]
    public void DisputeResolved_TellsEveryParty_TheVerdict()
    {
        var events = EscrowChange(Escrow("disputed"), Escrow("dispute_resolved", ("disputeResolution", "farmer_wins")));

        Tells(events, (Buyer, T.DisputeResolved), (Farmer, T.DisputeResolved), (Trucker, T.DisputeResolved));
        events.Should().OnlyContain(e => e.Params[P.Resolution] == "farmer_wins" && e.Push);
    }

    [Fact]
    public void Held_TellsEveryParty_UnderReview_InboxOnly()
    {
        var events = EscrowChange(Escrow("escrowed"), Escrow("held"));

        Tells(events, (Buyer, T.UnderReview), (Farmer, T.UnderReview), (Trucker, T.UnderReview));
        events.Should().OnlyContain(e => !e.Push);
    }

    [Fact]
    public void EscrowWithoutATrucker_SkipsTheEmptyParty()
    {
        Tells(EscrowChange(Escrow("escrowed", ("truckerId", null)), Escrow("held", ("truckerId", null))),
            (Buyer, T.UnderReview), (Farmer, T.UnderReview));
    }

    [Fact]
    public void EscrowWithoutAnOrderId_HasNoOrderId()
    {
        var events = EscrowChange(Escrow("escrowed", ("orderId", null)), Escrow("refunded", ("orderId", null)));

        For(events, Buyer).OrderId.Should().BeNull();
    }

    [Fact]
    public void EscrowInsert_IsIgnored()
    {
        NotificationRules.Derive("escrows", "INSERT", new StreamImage(null), Image(Escrow("escrowed")), RecordTime)
            .Should().BeEmpty();
    }

    // ===== anything else =====

    [Fact]
    public void UnknownTable_IsEmpty()
    {
        NotificationRules.Derive("trips", "MODIFY", Image(TripOrder("funded")), Image(TripOrder("accepted")), RecordTime)
            .Should().BeEmpty();
    }

    [Fact]
    public void OnlyKnownTypesAreEverEmitted()
    {
        var all = new[]
        {
            OrderChange(TripOrder("payment_submitted"), TripOrder("funded")),
            OrderChange(TripOrder("funded"), TripOrder("accepted")),
            OrderChange(TripOrder("in_transit"), TripOrder("delivered")),
            EscrowChange(Escrow("escrowed"), Escrow("disputed", ("disputedBy", Farmer)))
        }.SelectMany(e => e);

        all.Should().OnlyContain(e => T.All.Contains(e.Type));
    }
}
