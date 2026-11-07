using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services.Ledger;

/// <summary>
/// ESC-20: the pure rules of the escrow ledger (escrow-protocol.md 2.2.0): every plan keeps I1 to I4, the
/// split rules of P.8, and the escrow status the rows call for (P.5).
/// </summary>
public class EscrowLedgerBookTests
{
    // The P.8 example: 15,000 crop, 1,500 transport, 750 commission (5% of the crop), 17,250 paid.
    private static LedgerEscrow Escrow(
        decimal crop = 15_000m, decimal transport = 1_500m, decimal commission = 750m,
        Dictionary<string, string>? legs = null, decimal refunded = 0m, decimal released = 0m, string? truckerId = "trucker-1")
        => new()
        {
            EscrowId = "escrow-order-o1",
            CreatedAt = "2026-11-01T10:00:00.0000000Z",
            Version = 3,
            Status = "escrowed",
            Amount = 17_250m,
            FarmerPayout = crop,
            TruckerPayout = transport,
            Commission = commission,
            RefundedTotal = refunded,
            ReleasedTotal = released,
            Legs = legs ?? new Dictionary<string, string>
            {
                ["crop"] = "held", ["transport"] = "held", ["commission"] = "held"
            },
            BuyerId = "buyer-1",
            FarmerId = "farmer-1",
            TruckerId = truckerId,
            OrderId = "o1",
            HoldRef = "HOLD-1"
        };

    private static IEnumerable<(string, decimal)> Rows(LedgerPlan plan) => plan.Rows.Select(r => (r.Ref, r.Amount));

    private static void AssertInvariants(LedgerPlan plan, IEnumerable<(string, decimal)>? earlier = null)
    {
        EscrowLedgerBook.CheckTransition(plan.Before, plan.After).Should().BeNull();
        EscrowLedgerBook.CheckInvariants(plan.After, (earlier ?? Array.Empty<(string, decimal)>()).Concat(Rows(plan))).Should().BeNull();
    }

    private static LedgerRow Row(string reference, decimal amount, string status)
        => new()
        {
            EscrowId = "escrow-order-o1", Ref = reference, Kind = "release", Amount = amount, Status = status,
            GatewayRef = "WCL", Reason = "delivered", Actor = "system", CreatedAt = "t", UpdatedAt = "t"
        };

    [Fact]
    public void BirthLegs_HoldEveryLegWithMoney_AndLeaveOutAnEmptyTransportLeg()
    {
        EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m).Keys.Should().BeEquivalentTo("crop", "transport", "commission");
        EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m).Keys.Should().BeEquivalentTo("crop", "commission");
        EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m).Values.Should().AllBeEquivalentTo("held");
    }

    [Fact]
    public void PlanRelease_BothLegs_ReleasesCropAndTransport_KeepsTheCommission()
    {
        var plan = EscrowLedgerBook.PlanRelease(Escrow(), payFarmer: true, payTrucker: true, "delivery_confirmed")!;

        plan.Rows.Select(r => (r.Ref, r.Amount, r.PayeeRole, r.PayeeId)).Should().BeEquivalentTo(new[]
        {
            ("release#farmer", 15_000m, "farmer", "farmer-1"),
            ("release#trucker#trucker-1", 1_500m, "trucker", "trucker-1")
        });
        plan.After.Legs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
        });
        plan.After.ReleasedTotal.Should().Be(16_500m);
        plan.After.RefundedTotal.Should().Be(0m);
        AssertInvariants(plan);
    }

    [Fact]
    public void PlanRelease_FarmerBlocked_PaysOnlyTheTrucker_CropAndCommissionStayHeld()
    {
        var plan = EscrowLedgerBook.PlanRelease(Escrow(), payFarmer: false, payTrucker: true, "delivery_confirmed")!;

        plan.Rows.Should().ContainSingle().Which.Ref.Should().Be("release#trucker#trucker-1");
        plan.After.Legs["crop"].Should().Be("held");
        plan.After.Legs["commission"].Should().Be("held");
        AssertInvariants(plan);

        // The farmer later: only the crop leg moves; the trucker row is not written twice.
        var later = EscrowLedgerBook.PlanRelease(plan.After, payFarmer: true, payTrucker: true, "delivery_confirmed")!;
        later.Rows.Should().ContainSingle().Which.Ref.Should().Be("release#farmer");
        later.After.ReleasedTotal.Should().Be(16_500m);
        AssertInvariants(later, Rows(plan));
    }

    [Fact]
    public void PlanRelease_NothingHeld_IsNull()
    {
        var settled = Escrow(legs: new() { ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept" }, released: 16_500m);
        EscrowLedgerBook.PlanRelease(settled, true, true, "x").Should().BeNull();
        EscrowLedgerBook.PlanRelease(Escrow(), false, false, "x").Should().BeNull();
    }

    [Fact]
    public void PlanRelease_SelfPickup_HasNoTransportRow()
    {
        var selfPickup = Escrow(transport: 0m, legs: new() { ["crop"] = "held", ["commission"] = "held" }) with { Amount = 15_750m };
        var plan = EscrowLedgerBook.PlanRelease(selfPickup, true, true, "x")!;

        plan.Rows.Should().ContainSingle().Which.Ref.Should().Be("release#farmer");
        AssertInvariants(plan);
    }

    [Fact]
    public void PlanRefundAll_RefundsEveryHeldLeg_InOneRow_AndZeroesThem()
    {
        var plan = EscrowLedgerBook.PlanRefundAll(Escrow(), "farmer_declined")!;

        plan.Rows.Should().ContainSingle();
        plan.Rows[0].Should().Match<LedgerRowSeed>(r => r.Ref == "refund#all" && r.Amount == 17_250m && r.PayeeRole == "buyer");
        plan.After.Legs.Values.Should().AllBeEquivalentTo("refunded");
        (plan.After.FarmerPayout, plan.After.TruckerPayout, plan.After.Commission).Should().Be((0m, 0m, 0m));
        plan.After.RefundedTotal.Should().Be(17_250m);
        AssertInvariants(plan);
    }

    [Fact]
    public void PlanRefundAll_AfterTheTruckerWasPaid_RefundsOnlyWhatIsStillHeld()
    {
        var truckerPaid = Escrow(legs: new() { ["crop"] = "held", ["transport"] = "released", ["commission"] = "held" }, released: 1_500m);
        var plan = EscrowLedgerBook.PlanRefundAll(truckerPaid, "verdict:buyer_wins")!;

        plan.Rows[0].Amount.Should().Be(15_750m);
        plan.After.Legs["transport"].Should().Be("released");
        plan.After.TruckerPayout.Should().Be(1_500m);
        AssertInvariants(plan, new[] { ("release#trucker#trucker-1", 1_500m) });
    }

    [Fact]
    public void PlanSplit_TheContractExample_KeepsTheCommission()
    {
        var (plan, refusal) = EscrowLedgerBook.PlanSplit(Escrow(), 9_000m, 1_500m, 6_000m, "verdict:partial_refund");

        refusal.Should().BeNull();
        plan!.Rows.Select(r => (r.Ref, r.Amount)).Should().BeEquivalentTo(new[]
        {
            ("verdict#farmer", 9_000m), ("verdict#trucker", 1_500m), ("verdict#buyer", 6_000m)
        });
        plan.After.Legs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
        });
        (plan.After.FarmerPayout, plan.After.TruckerPayout, plan.After.Commission).Should().Be((9_000m, 1_500m, 750m));
        plan.After.RefundedTotal.Should().Be(6_000m);
        plan.After.ReleasedTotal.Should().Be(10_500m);
        plan.Rows.Single(r => r.Ref == "verdict#buyer").Legs.Should().BeEquivalentTo("crop");
        AssertInvariants(plan);
    }

    [Fact]
    public void PlanSplit_NothingForTheFarmer_RefundsTheCommissionToo()
    {
        var (plan, refusal) = EscrowLedgerBook.PlanSplit(Escrow(), 0m, 1_500m, 15_750m, "verdict:partial_refund");

        refusal.Should().BeNull();
        plan!.Rows.Select(r => r.Ref).Should().BeEquivalentTo("verdict#trucker", "verdict#buyer");
        plan.After.Legs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "refunded", ["transport"] = "released", ["commission"] = "refunded"
        });
        plan.After.Commission.Should().Be(0m);
        AssertInvariants(plan);
    }

    [Theory]
    [InlineData(-1, 1_500, 7_000, "farmerAmount")]
    [InlineData(9_000.001, 1_500, 5_999.999, "farmerAmount")]
    [InlineData(15_001, 1_500, 0, "farmerAmount")]
    [InlineData(9_000, 1_501, 5_999, "truckerAmount")]
    [InlineData(9_000, 1_500, 6_750, "buyerRefund")] // the commission is kept when the farmer is paid
    [InlineData(0, 1_500, 15_000, "buyerRefund")] // with nothing for the farmer, the commission goes back too
    public void PlanSplit_RefusesASplitThatBreaksARule_NamingTheField(
        double farmer, double trucker, double buyer, string field)
    {
        var (plan, refusal) = EscrowLedgerBook.PlanSplit(Escrow(), (decimal)farmer, (decimal)trucker, (decimal)buyer, "v");

        plan.Should().BeNull();
        refusal!.Field.Should().Be(field);
    }

    [Fact]
    public void PlanSplit_TransportAlreadyPaid_TruckerAmountMustBeZero()
    {
        var truckerPaid = Escrow(legs: new() { ["crop"] = "held", ["transport"] = "released", ["commission"] = "held" }, released: 1_500m);

        EscrowLedgerBook.PlanSplit(truckerPaid, 9_000m, 500m, 5_500m, "v").Refusal!.Field.Should().Be("truckerAmount");
        var (plan, refusal) = EscrowLedgerBook.PlanSplit(truckerPaid, 9_000m, 0m, 6_000m, "v");
        refusal.Should().BeNull();
        plan!.After.Legs["transport"].Should().Be("released");
        AssertInvariants(plan, new[] { ("release#trucker#trucker-1", 1_500m) });
    }

    [Fact]
    public void PlanSplit_CropNoLongerHeld_IsRefused()
    {
        var released = Escrow(legs: new() { ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept" }, released: 15_000m);
        EscrowLedgerBook.PlanSplit(released, 0m, 0m, 1_500m, "v").Refusal.Should().NotBeNull();
    }

    [Fact]
    public void CheckInvariants_FindsEachBrokenRule()
    {
        EscrowLedgerBook.CheckInvariants(Escrow(), Array.Empty<(string, decimal)>()).Should().BeNull();
        // I1: the legs and the refunds no longer add up to what was paid.
        EscrowLedgerBook.CheckInvariants(Escrow(crop: 15_000.01m), Array.Empty<(string, decimal)>()).Should().Be("I1");
        // I2: a released leg not counted in releasedTotal.
        var released = Escrow(legs: new() { ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept" });
        EscrowLedgerBook.CheckInvariants(released, new[] { ("release#farmer", 15_000m) }).Should().Be("I2");
        // I3: the rows of amount disagree with the totals; a duplicate refund is outside amount.
        var decided = released with { ReleasedTotal = 15_000m };
        EscrowLedgerBook.CheckInvariants(decided, new[] { ("release#farmer", 15_000m) }).Should().BeNull();
        EscrowLedgerBook.CheckInvariants(decided, new[] { ("release#farmer", 15_000m), ("refund#duplicate#P2", 17_250m) }).Should().BeNull();
        EscrowLedgerBook.CheckInvariants(decided, Array.Empty<(string, decimal)>()).Should().Be("I3");
    }

    [Fact]
    public void CheckTransition_ALegLeavesHeldOnlyOnce()
    {
        var released = Escrow(legs: new() { ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept" }, released: 15_000m);
        // Moving a released leg again (or changing its amount) is I4.
        EscrowLedgerBook.CheckTransition(released,
            released with { Legs = new Dictionary<string, string> { ["crop"] = "refunded", ["transport"] = "held", ["commission"] = "kept" } })
            .Should().Be("I4");
        EscrowLedgerBook.CheckTransition(released, released with { FarmerPayout = 1m }).Should().Be("I4");
        EscrowLedgerBook.CheckTransition(released, released with { Amount = 1m }).Should().Be("I4");
        EscrowLedgerBook.CheckTransition(released, released with { ReleasedTotal = 0m }).Should().Be("I4");
        EscrowLedgerBook.CheckTransition(released, released).Should().BeNull();
    }

    [Fact]
    public void StatusFor_FollowsTheRows()
    {
        var both = EscrowLedgerBook.PlanRelease(Escrow(), true, true, "x")!.After;
        // Rows decided, nothing done: unchanged.
        EscrowLedgerBook.StatusFor(both, new[] { Row("release#farmer", 15_000m, "pending"), Row("release#trucker#trucker-1", 1_500m, "pending") })
            .Should().BeNull();
        // The trucker paid first keeps escrowed.
        EscrowLedgerBook.StatusFor(both, new[] { Row("release#farmer", 15_000m, "manual"), Row("release#trucker#trucker-1", 1_500m, "done") })
            .Should().BeNull();
        // The farmer paid, the trucker open: released.
        EscrowLedgerBook.StatusFor(both, new[] { Row("release#farmer", 15_000m, "done"), Row("release#trucker#trucker-1", 1_500m, "sent") })
            .Should().Be("released");
        // Every row done: completed.
        EscrowLedgerBook.StatusFor(both, new[] { Row("release#farmer", 15_000m, "done"), Row("release#trucker#trucker-1", 1_500m, "done") })
            .Should().Be("completed");
        // The farmer paid, the transport leg still held (the trucker blocked): released.
        var farmerOnly = EscrowLedgerBook.PlanRelease(Escrow(), true, false, "x")!.After;
        EscrowLedgerBook.StatusFor(farmerOnly, new[] { Row("release#farmer", 15_000m, "done") }).Should().Be("released");

        var refund = EscrowLedgerBook.PlanRefundAll(Escrow(), "x")!.After;
        EscrowLedgerBook.StatusFor(refund, new[] { Row("refund#all", 17_250m, "pending") }).Should().BeNull();
        EscrowLedgerBook.StatusFor(refund, new[] { Row("refund#all", 17_250m, "done") }).Should().Be("refunded");
        // An open duplicate refund never holds back the status.
        EscrowLedgerBook.StatusFor(refund, new[] { Row("refund#all", 17_250m, "done"), Row("refund#duplicate#P2", 17_250m, "manual") })
            .Should().Be("refunded");
    }

    [Fact]
    public void StatusFor_TheCropLegNamesTheOutcome_ACropRefundIsRefundedEvenWithTheTruckerPaid()
    {
        // A split with nothing for the farmer: the trucker is paid, the crop and commission go back.
        var after = EscrowLedgerBook.PlanSplit(Escrow(), 0m, 1_500m, 15_750m, "v").Plan!.After;
        EscrowLedgerBook.StatusFor(after with { Status = "dispute_resolved" },
                new[] { Row("verdict#trucker", 1_500m, "done"), Row("verdict#buyer", 15_750m, "done") })
            .Should().Be("refunded");
        // The trucker paid first (the farmer blocked), then refund#all of what is still held.
        var truckerPaid = Escrow(legs: new() { ["crop"] = "held", ["transport"] = "released", ["commission"] = "held" }, released: 1_500m);
        var refunded = EscrowLedgerBook.PlanRefundAll(truckerPaid, "verdict:buyer_wins")!.After;
        EscrowLedgerBook.StatusFor(refunded,
                new[] { Row("release#trucker#trucker-1", 1_500m, "done"), Row("refund#all", 15_750m, "done") })
            .Should().Be("refunded");
        var all = EscrowLedgerBook.PlanSplit(Escrow(), 0m, 0m, 17_250m, "v").Plan!.After;
        EscrowLedgerBook.StatusFor(all with { Status = "dispute_resolved" }, new[] { Row("verdict#buyer", 17_250m, "done") })
            .Should().Be("refunded");
    }

    [Fact]
    public void GatewayRef_IsAStableShortAlphanumericMappingOfEscrowAndRef()
    {
        var reference = LedgerRefs.GatewayRef("escrow-order-0b4f3c2e-1111-2222-3333-444455556666", "release#trucker#a7c1e6d2-9999-8888-7777-666655554444");

        reference.Should().MatchRegex("^[A-Za-z0-9]{30}$").And.StartWith("WCL");
        reference.Should().Be(LedgerRefs.GatewayRef("escrow-order-0b4f3c2e-1111-2222-3333-444455556666", "release#trucker#a7c1e6d2-9999-8888-7777-666655554444"));
        LedgerRefs.GatewayRef("escrow-order-x", "release#farmer").Should().NotBe(LedgerRefs.GatewayRef("escrow-order-x", "refund#all"));
    }

    [Fact]
    public void AuditTimestamps_OfTwoRefsAtTheSameInstant_Differ()
    {
        const string now = "2026-11-07T10:00:00.0000000Z";
        LedgerRefs.AuditTimestamp(now, "release#farmer", "ledger_pending")
            .Should().NotBe(LedgerRefs.AuditTimestamp(now, "release#trucker#t1", "ledger_pending"));
        LedgerRefs.AuditTimestamp(now, "release#farmer", "ledger_pending")
            .Should().NotBe(LedgerRefs.AuditTimestamp(now, "release#farmer", "ledger_done"));
        LedgerRefs.AuditTimestamp(now, "release#farmer", "ledger_done")
            .Should().Be(LedgerRefs.AuditTimestamp(now, "release#farmer", "ledger_done"));
    }
}
