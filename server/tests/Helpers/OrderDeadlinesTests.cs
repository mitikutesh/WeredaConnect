using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-16: the next deadline of an order, one kind at a time (T3, T4, T5, their reminders and T7),
/// with the ESC-15 rules it mirrors: only a stamped farmer deadline counts, a reminder only for a
/// window of 12 hours or more and only once, and nothing for the statuses the sweep does not own.
/// </summary>
public class OrderDeadlinesTests
{
    private static readonly DateTime Funded = new(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static Dictionary<string, object> Row(string status, params (string Key, object Value)[] fields)
    {
        var row = new Dictionary<string, object>
        {
            { "orderId", "ord-1" },
            { "createdAt", Iso(Funded.AddHours(-2)) },
            { "updatedAt", Iso(Funded) },
            { "status", status },
            { "version", "4" },
            { "paymentVerifiedAt", Iso(Funded) }
        };
        foreach (var (key, value) in fields)
        {
            row[key] = value;
        }

        return row;
    }

    private static Dictionary<string, object> FarmerPending(DateTime deadline)
        => Row("funded",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.FarmerDeadlineAtField, Iso(deadline)),
            ("tripId", "trip-1"),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.NotAsked));

    private static Dictionary<string, object> TruckerPending(DateTime askedAt, DateTime? deadline)
    {
        var row = Row("funded",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            ("tripId", "trip-1"),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.TruckerAskedAtField, Iso(askedAt)));
        if (deadline is { } d)
        {
            row[OrderCommitment.TruckerDeadlineAtField] = Iso(d);
        }

        return row;
    }

    // ===== T3: the farmer's answer =====

    [Fact]
    public void FarmerPending_TheReminderComesFirst_SixHoursBeforeTheDeadline()
    {
        var deadline = Funded.AddHours(24);

        OrderDeadlines.Next(FarmerPending(deadline), OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline.AddHours(-6), OrderDeadlines.Kind.FarmerReminder));
    }

    [Fact]
    public void FarmerPending_Reminded_TheDeadlineItself()
    {
        var deadline = Funded.AddHours(24);
        var row = FarmerPending(deadline);
        row[OrderCommitment.FarmerReminderAtField] = Iso(deadline.AddHours(-6));

        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline, OrderDeadlines.Kind.FarmerResponse));
    }

    [Fact]
    public void FarmerPending_WindowUnder12Hours_NoReminder()
    {
        var deadline = Funded.AddHours(10);

        OrderDeadlines.Next(FarmerPending(deadline), OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline, OrderDeadlines.Kind.FarmerResponse));
    }

    [Fact]
    public void FarmerPending_ReminderLeadFromTheTimings()
    {
        var deadline = Funded.AddHours(24);
        var timings = OrderTimings.Defaults with { ReminderLead = TimeSpan.FromHours(2) };

        OrderDeadlines.Next(FarmerPending(deadline), timings)
            .Should().Be(new OrderDeadline(deadline.AddHours(-2), OrderDeadlines.Kind.FarmerReminder));
    }

    [Fact]
    public void FundedRowFromBeforeTheStamps_HasNoDeadline()
    {
        // No farmerDeadlineAt and no farmerResponse: left to the 7-day escrow sweep (ESC-15 rule).
        OrderDeadlines.Next(Row("funded"), OrderTimings.Defaults).Should().BeNull();
        OrderDeadlines.Next(Row("funded", ("tripId", "trip-1")), OrderTimings.Defaults).Should().BeNull();
    }

    [Fact]
    public void FarmerAnswered_NoFarmerDeadline()
    {
        var row = FarmerPending(Funded.AddHours(24));
        row[OrderCommitment.FarmerResponseField] = OrderCommitment.Response.Accepted;
        row[OrderCommitment.TruckerResponseField] = OrderCommitment.Response.Cancelled;

        OrderDeadlines.Next(row, OrderTimings.Defaults).Should().BeNull();
    }

    // ===== T4: the trucker's answer =====

    [Fact]
    public void TruckerPending_ReminderThenDeadline()
    {
        var asked = Funded.AddHours(3);
        var deadline = asked.AddHours(24);
        var row = TruckerPending(asked, deadline);

        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline.AddHours(-6), OrderDeadlines.Kind.TruckerReminder));

        row[OrderCommitment.TruckerReminderAtField] = Iso(deadline.AddHours(-6));
        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline, OrderDeadlines.Kind.TruckerResponse));
    }

    [Fact]
    public void TruckerPending_WithoutTheStamp_AskedPlusTheWindow()
    {
        var asked = Funded.AddHours(3);
        var row = TruckerPending(asked, deadline: null);
        row[OrderCommitment.TruckerReminderAtField] = Iso(asked.AddHours(18));

        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(asked.AddHours(24), OrderDeadlines.Kind.TruckerResponse));
        OrderDeadlines.Next(row, OrderTimings.Defaults with { ResponseWindow = TimeSpan.FromHours(30) })
            .Should().Be(new OrderDeadline(asked.AddHours(30), OrderDeadlines.Kind.TruckerResponse));
    }

    [Fact]
    public void TruckerPending_ShortWindowNearDeparture_NoReminder()
    {
        var asked = Funded.AddHours(3);
        var deadline = asked.AddHours(5);

        OrderDeadlines.Next(TruckerPending(asked, deadline), OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline, OrderDeadlines.Kind.TruckerResponse));
    }

    // ===== T5: the buyer's choice =====

    [Fact]
    public void AwaitingTransport_ReminderThenDeadline_ThenNothingOnceDecided()
    {
        var deadline = Funded.AddHours(48);
        var row = Row(OrderCommitment.AwaitingTransportStatus,
            (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(deadline)));

        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline.AddHours(-6), OrderDeadlines.Kind.BuyerChoiceReminder));

        row[OrderCommitment.BuyerChoiceReminderAtField] = Iso(deadline.AddHours(-6));
        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(deadline, OrderDeadlines.Kind.BuyerChoice));

        row["status"] = "cancelled";
        OrderDeadlines.Next(row, OrderTimings.Defaults).Should().BeNull();
    }

    [Fact]
    public void AwaitingTransport_WithoutTheStamp_UpdatedAtPlusTheWindow()
    {
        var row = Row(OrderCommitment.AwaitingTransportStatus, (OrderCommitment.BuyerChoiceReminderAtField, Iso(Funded)));

        OrderDeadlines.Next(row, OrderTimings.Defaults)
            .Should().Be(new OrderDeadline(Funded.AddHours(48), OrderDeadlines.Kind.BuyerChoice));
    }

    // ===== T7: the pickup =====

    [Fact]
    public void Accepted_PickupDeadline_UntilTheMarkerIsStamped_ThenTheWaitInTheWatchPartition()
    {
        var pickupBy = Funded.AddDays(3);
        var row = Row("accepted", (PickupDeadline.Field, PickupDeadline.Format(pickupBy)));

        var overdue = OrderDeadlines.Next(row, OrderTimings.Defaults);
        overdue.Should().Be(new OrderDeadline(pickupBy, OrderDeadlines.Kind.PickupOverdue));
        overdue!.Value.Shard.Should().Be(OrderDeadlines.Shard, "the sweep stamps the marker");

        // FUP-170: after the notice the order stays indexed at its pickup deadline, where the sweep never reads.
        row[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(pickupBy.AddMinutes(10));
        var waiting = OrderDeadlines.Next(row, OrderTimings.Defaults);
        waiting.Should().Be(new OrderDeadline(pickupBy, OrderDeadlines.Kind.PickupWaiting));
        waiting!.Value.Shard.Should().Be(OrderDeadlines.WatchShard);
    }

    [Fact]
    public void ESC34_AJudgeableOrder_IsSweptAtItsJudgement_AndWaitsInTheWatchPartitionOnlyAfterIt()
    {
        // FUP-170 and ESC-34 together: the judgement is the sweep's (shard 0) even with a notice on the row;
        // the judgement stamps the notice, so an order still accepted after it waits like any notified order.
        var pickupBy = Funded.AddDays(3);
        var row = Row("accepted",
            (PickupDeadline.Field, PickupDeadline.Format(pickupBy)),
            (PickupJudgement.Field, PickupJudgement.Outcome.Pending));

        var judgement = OrderDeadlines.Next(row, OrderTimings.Defaults);
        judgement.Should().Be(new OrderDeadline(pickupBy + PickupJudgement.SettleMargin, OrderDeadlines.Kind.PickupJudgement));
        judgement!.Value.Shard.Should().Be(OrderDeadlines.Shard);
        OrderDeadlines.IsSwept(OrderDeadlines.Kind.PickupJudgement).Should().BeTrue();

        row[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(pickupBy.AddMinutes(10));
        OrderDeadlines.Next(row, OrderTimings.Defaults)!.Value.Kind
            .Should().Be(OrderDeadlines.Kind.PickupJudgement, "a pending judgement is never parked in watch");

        row[PickupJudgement.Field] = PickupJudgement.Outcome.BothPresentNoScan;
        var waiting = OrderDeadlines.Next(row, OrderTimings.Defaults);
        waiting.Should().Be(new OrderDeadline(pickupBy, OrderDeadlines.Kind.PickupWaiting));
        waiting!.Value.Shard.Should().Be(OrderDeadlines.WatchShard);

        // The flag turned off: the stamp is cleared with the notice, and the order waits as in FUP-170.
        row.Remove(PickupJudgement.Field);
        OrderDeadlines.Next(row, OrderTimings.Defaults).Should().Be(new OrderDeadline(pickupBy, OrderDeadlines.Kind.PickupWaiting));
    }

    [Fact]
    public void OnlyThePickupWait_IsKeptOutOfTheSweepsPartition()
    {
        OrderDeadlines.ShardOf(OrderDeadlines.Kind.PickupWaiting).Should().Be(OrderDeadlines.WatchShard);
        OrderDeadlines.IsSwept(OrderDeadlines.Kind.PickupWaiting).Should().BeFalse();
        foreach (var kind in new[]
                 {
                     OrderDeadlines.Kind.FarmerReminder, OrderDeadlines.Kind.FarmerResponse, OrderDeadlines.Kind.TruckerReminder,
                     OrderDeadlines.Kind.TruckerResponse, OrderDeadlines.Kind.BuyerChoiceReminder, OrderDeadlines.Kind.BuyerChoice,
                     OrderDeadlines.Kind.PickupOverdue
                 })
        {
            OrderDeadlines.ShardOf(kind).Should().Be(OrderDeadlines.Shard, kind);
            OrderDeadlines.IsSwept(kind).Should().BeTrue(kind);
        }
    }

    [Fact]
    public void Accepted_WithoutAPickupDeadline_HasNone()
    {
        OrderDeadlines.Next(Row("accepted"), OrderTimings.Defaults).Should().BeNull();
    }

    // ===== statuses the sweep does not own =====

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_submitted")]
    [InlineData("payment_failed")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public void OtherStatuses_HaveNoDeadline(string status)
    {
        // Even with every stamp an earlier status left behind.
        var row = FarmerPending(Funded.AddHours(24));
        row["status"] = status;
        row[PickupDeadline.Field] = PickupDeadline.Format(Funded.AddDays(2));
        row[OrderCommitment.BuyerChoiceDeadlineAtField] = Iso(Funded.AddDays(2));

        OrderDeadlines.Next(row, OrderTimings.Defaults).Should().BeNull();
    }

    // ===== stored attributes =====

    [Fact]
    public void IsStored_ComparesTheThreeAttributes()
    {
        var deadline = new OrderDeadline(Funded.AddHours(18), OrderDeadlines.Kind.FarmerReminder);
        var row = Row("funded");

        OrderDeadlines.IsStored(row, null).Should().BeTrue();
        OrderDeadlines.IsStored(row, deadline).Should().BeFalse();

        row[OrderDeadlines.AtField] = deadline.AtIso;
        row[OrderDeadlines.KindField] = deadline.Kind;
        OrderDeadlines.IsStored(row, deadline).Should().BeFalse("the shard is part of the index key");

        row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
        OrderDeadlines.IsStored(row, deadline).Should().BeTrue();
        OrderDeadlines.Stored(row).Should().Be(deadline);
        OrderDeadlines.IsStored(row, deadline with { Kind = OrderDeadlines.Kind.FarmerResponse }).Should().BeFalse();
        OrderDeadlines.IsStored(row, deadline with { At = deadline.At.AddSeconds(1) }).Should().BeFalse();
        OrderDeadlines.IsStored(row, null).Should().BeFalse();
    }

    [Fact]
    public void IsStored_ThePickupWait_OnlyInTheWatchPartition()
    {
        var pickupBy = Funded.AddDays(3);
        var waiting = new OrderDeadline(pickupBy, OrderDeadlines.Kind.PickupWaiting);
        var row = Row("accepted",
            (OrderDeadlines.AtField, waiting.AtIso), (OrderDeadlines.KindField, waiting.Kind), (OrderDeadlines.ShardField, OrderDeadlines.Shard));

        OrderDeadlines.IsStored(row, waiting).Should().BeFalse("an entry the sweep would read is moved");

        row[OrderDeadlines.ShardField] = OrderDeadlines.WatchShard;
        OrderDeadlines.IsStored(row, waiting).Should().BeTrue();
    }

    [Fact]
    public void AtIso_SortsAsText()
    {
        var early = new OrderDeadline(new DateTime(2026, 11, 7, 9, 5, 0, DateTimeKind.Utc), OrderDeadlines.Kind.BuyerChoice);
        var late = new OrderDeadline(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc), OrderDeadlines.Kind.BuyerChoice);

        string.CompareOrdinal(early.AtIso, late.AtIso).Should().BeNegative();
        early.AtIso.Should().Be("2026-11-07T09:05:00.0000000Z");
    }
}
