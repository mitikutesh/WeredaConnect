using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-370 (order-lifecycle 2.22.0): T9 as the parties read it, <c>dropoffConfirmAt</c>. It is the order cron's
/// own T9 without the settle margin (<see cref="DropoffJudgement.Due"/>), computed with the timings on every
/// read, only for the order's parties, only while the order is on the road and waits for its judgement with a
/// counted drop-off check-in, and never with the judgement off.
/// </summary>
public class DropoffConfirmAtTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";

    private static readonly DateTime PickedUp = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T8 = PickedUp.AddHours(72);
    private static readonly DateTime Received = T8.AddHours(-10);

    /// <summary>Not the default 48 hours: the value the parties must see.</summary>
    private static readonly OrderTimings Timings = OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(30) };

    private static Func<OrderTimings> Source => () => Timings;

    private static Dictionary<string, object> Order(
        string status = "in_transit", string judgement = DropoffJudgement.Outcome.Pending, Dictionary<string, object>? checkIn = null)
        => new()
        {
            { "orderId", "ord-1" }, { "status", status }, { "version", "5" },
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId }, { "tripId", "trip-1" },
            { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
            { DropoffJudgement.Field, judgement },
            { DropoffJudgement.DeadlineField, OrderCommitment.Iso(T8) },
            { DropoffJudgement.CheckInField, checkIn ?? CheckIn(Received) }
        };

    private static Dictionary<string, object> CheckIn(DateTime received, bool late = false) => new()
    {
        { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(received) },
        { PickupJudgement.EntryDistanceChecked, true },
        { PickupJudgement.EntryLate, late },
        { PickupJudgement.EntryLatitude, 9.03 },
        { PickupJudgement.EntryBy, TruckerId }
    };

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public void EachParty_ReadsT9_WithTheSsmWindow(string caller)
    {
        DropoffJudgement.ConfirmAtFor(Order(), caller, Source)
            .Should().Be(OrderCommitment.Iso(Received.AddHours(30)), "silent-buyer-hours is 30, not the default 48");
    }

    [Theory]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    public void IsTheCronsT9_WithoutTheSettleMargin(string status)
    {
        var order = Order(status);

        var due = DropoffJudgement.Due(order, Timings)!.Value;

        due.Kind.Should().Be(OrderDeadlines.Kind.SilentBuyer);
        DropoffJudgement.SilentBuyerDeadline(order, Timings).Should().Be(due.At - PickupJudgement.SettleMargin);
        DropoffJudgement.ConfirmAtFor(order, BuyerId, Source).Should().Be(OrderCommitment.Iso(due.At - PickupJudgement.SettleMargin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-else")]
    public void NotAParty_ReadsNone(string? caller)
    {
        DropoffJudgement.ConfirmAtFor(Order(), caller, Source).Should().BeNull();
    }

    [Fact]
    public void WithTheJudgementOff_NoneIsShown()
    {
        DropoffJudgement.ConfirmAtFor(Order(), BuyerId, timings: null).Should().BeNull();
    }

    [Theory]
    [InlineData(DropoffJudgement.Outcome.Closed)]
    [InlineData(DropoffJudgement.Outcome.AutoConfirmed)]
    [InlineData(DropoffJudgement.Outcome.SilentBuyer)]
    [InlineData(DropoffJudgement.Outcome.Queued)]
    public void AJudgedOrder_HasNone(string judgement)
    {
        DropoffJudgement.ConfirmAtFor(Order(judgement: judgement), BuyerId, Source).Should().BeNull();
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("accepted")]
    public void AnOrderNoLongerOnTheRoad_HasNone(string status)
    {
        // A party's confirm or dispute leaves the stamp pending until the cron follows it: nothing is decided then.
        DropoffJudgement.ConfirmAtFor(Order(status), BuyerId, Source).Should().BeNull();
    }

    [Fact]
    public void WithoutACountedCheckIn_NoneIsShown_AndTheTimingsAreNotRead()
    {
        var read = 0;
        Func<OrderTimings> counting = () =>
        {
            read++;
            return Timings;
        };
        var noCheckIn = Order();
        noCheckIn.Remove(DropoffJudgement.CheckInField);

        DropoffJudgement.ConfirmAtFor(noCheckIn, BuyerId, counting).Should().BeNull();
        DropoffJudgement.ConfirmAtFor(Order(checkIn: CheckIn(Received, late: true)), BuyerId, counting).Should().BeNull();
        DropoffJudgement.ConfirmAtFor(Order(checkIn: CheckIn(T8.AddMinutes(5))), BuyerId, counting).Should().BeNull();
        read.Should().Be(0);
    }

    // ===== the /sync order row =====

    [Fact]
    public void TheSyncRow_GainsT9_ThenLosesTheRawCheckIn()
    {
        var result = new SyncOperationResult { OperationId = "op-1", Status = "conflict", ServerData = Order() };

        var row = PickupJudgement.ForClient(DropoffJudgement.WithConfirmAt(result, BuyerId, Source)).ServerData!;

        row[DropoffJudgement.ConfirmAtField].Should().Be(OrderCommitment.Iso(Received.AddHours(30)));
        row.Should().NotContainKey(DropoffJudgement.CheckInField);
        row.Should().ContainKey(PickupJudgement.CheckInsField);
    }

    [Fact]
    public void TheSyncRow_IsUnchanged_WhenTheCallerReadsNone()
    {
        var result = new SyncOperationResult { OperationId = "op-1", Status = "conflict", ServerData = Order() };

        DropoffJudgement.WithConfirmAt(result, "someone-else", Source).Should().BeSameAs(result);
        DropoffJudgement.WithConfirmAt(result, BuyerId, timings: null).Should().BeSameAs(result);
        result.ServerData.Should().NotContainKey(DropoffJudgement.ConfirmAtField);

        var noRow = new SyncOperationResult { OperationId = "op-2", Status = "error" };
        DropoffJudgement.WithConfirmAt(noRow, BuyerId, Source).Should().BeSameAs(noRow);
    }
}
