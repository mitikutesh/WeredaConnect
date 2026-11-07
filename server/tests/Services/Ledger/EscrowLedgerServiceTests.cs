using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services.Ledger;

/// <summary>
/// ESC-20: every path of a ledger escrow (escrow-protocol.md 2.1.0) against the in-memory ledger, with the
/// money invariants checked after each one: released plus refunded never exceeds what was funded, a leg
/// moves at most once, an unknown gateway outcome is never retried blindly (the sweep asks for the status
/// first), every movement writes an audit row, and every write is a compare-and-set.
/// </summary>
public class EscrowLedgerServiceTests
{
    private const string EscrowId = "escrow-order-o1";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string BuyerId = "buyer-1";

    private readonly InMemoryLedger _ledger = new();
    private readonly ScriptedGateway _gateway = new();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly List<string> _alarms = new();
    private DateTime _now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    public EscrowLedgerServiceTests()
    {
        _auditRepo.GetAuditTrailAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(new List<Dictionary<string, object>>());
        _ledger.EscrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?)null));
        Verified(FarmerId, "+251911000001");
        Verified(TruckerId, "+251911000002");
        Verified(BuyerId, "+251911000003");
    }

    private void Verified(string id, string phone, bool phoneVerified = true, bool fayda = true)
        => _users.GetUserByIdAsync(id).Returns(new UserDto
        {
            UserId = id, PhoneNumber = phone, DisplayName = id, Role = "farmer", Woreda = "w",
            IsPhoneVerified = phoneVerified, FaydaVerified = fayda, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, Version = 1
        });

    private EscrowService Service() => new(_ledger.EscrowRepo, _auditRepo, _idempotency, _gateway, _users)
    {
        AlarmLog = _alarms.Add,
        LedgerRepo = _ledger,
        LedgerClock = () => _now
    };

    /// <summary>A funded ledger escrow: 15,000 crop, 1,500 transport, 750 commission.</summary>
    private void Seed(string status = "escrowed", bool withOrder = true, Action<Dictionary<string, object>>? change = null)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["status"] = status,
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-1",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        if (withOrder)
        {
            item["orderId"] = "o1";
        }

        change?.Invoke(item);
        _ledger.Escrows[EscrowId] = item;
    }

    private static ReleaseEscrowRequest Release(string trigger = "delivery_confirmed") => new()
    {
        EscrowId = EscrowId, Trigger = trigger, DeliveryId = "", IdempotencyKey = $"system:{EscrowId}:release:1"
    };

    private static RefundEscrowRequest Refund() => new()
    {
        EscrowId = EscrowId, Trigger = "delivery_timeout", Reason = "cancelled", IdempotencyKey = $"system:{EscrowId}:refund:1"
    };

    private string Status => (string)_ledger.Escrow(EscrowId)["status"];

    /// <summary>I1 to I3 on the escrow and its rows, and released plus refunded never above what was funded.</summary>
    private void AssertMoneyInvariants()
    {
        var state = _ledger.State(EscrowId);
        var rows = _ledger.RowsOf(EscrowId);
        EscrowLedgerBook.CheckInvariants(state, rows.Select(r => (r.Ref, r.Amount))).Should().BeNull();
        (state.ReleasedTotal + state.RefundedTotal).Should().BeLessThanOrEqualTo(state.Amount);
        rows.Where(r => r.IsOfAmount).Sum(r => r.Amount).Should().BeLessThanOrEqualTo(state.Amount);
        // A leg moves at most once: every row that moves it was written by one decision (a split moves the
        // crop leg to the farmer and the buyer in the same write).
        foreach (var leg in rows.Where(r => r.IsOfAmount).SelectMany(r => r.Legs).Distinct())
        {
            rows.Where(r => r.IsOfAmount && r.Legs.Contains(leg)).Select(r => r.CreatedAt).Distinct().Should().ContainSingle();
        }
        _alarms.Should().NotContain(l => l.Contains("ESCROW_LEDGER_INVARIANT"));
    }

    private int CallsFor(string reference) => _gateway.Calls.Count(c => c.TransactionRef == LedgerRefs.GatewayRef(EscrowId, reference));

    private Task<EscrowService.LedgerSweepCounts> SweepAsync() => Service().SweepLedgerAsync(() => true);

    // ── Release ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_DecidesBothLegsInOneWrite_PaysEach_AndCompletes()
    {
        Seed();

        var result = await Service().ReleaseAsync(Release(), "system");

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("completed");
        _ledger.DecideWrites.Should().Be(1);
        _ledger.RowsOf(EscrowId).Should().OnlyContain(r => r.Status == "done" && r.PaidBy == "gateway" && r.CallingSince == null);
        _gateway.Calls.Select(c => (c.Kind, c.Amount, c.Destination)).Should().BeEquivalentTo(new[]
        {
            ("release", 15_000m, "+251911000001"), ("release", 1_500m, "+251911000002")
        });
        _gateway.Calls.Should().OnlyContain(c => c.TransactionRef.StartsWith("WCL") && c.HoldRef == "HOLD-1");
        var state = _ledger.State(EscrowId);
        state.Legs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
        });
        state.ReleasedTotal.Should().Be(16_500m);
        _ledger.Escrow(EscrowId)["releaseTrigger"].Should().Be("delivery_confirmed");
        AssertMoneyInvariants();

        // Every movement is audited: pending and done per row, and the status move.
        var actions = _ledger.AuditsOf(EscrowId).Select(a => a.Action).ToList();
        actions.Count(a => a == "ledger_pending").Should().Be(2);
        actions.Count(a => a == "ledger_done").Should().Be(2);
        actions.Should().Contain("complete");
        _ledger.AuditsOf(EscrowId).Select(a => a.Timestamp).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Release_Replayed_NeverPaysAgain()
    {
        Seed();
        await Service().ReleaseAsync(Release(), "system");

        var replay = await Service().ReleaseAsync(Release(), "system");

        replay.IsSuccess.Should().BeFalse(); // the escrow is completed: nothing left to release
        _gateway.Calls.Should().HaveCount(2);
        _ledger.DecideWrites.Should().Be(1);
    }

    [Fact]
    public async Task Release_OnTheLiveGateway_QueuesBothRowsForAnAdmin_WithoutAnAlarm()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.HttpStatus.Should().Be(202);
        result.Error.Code.Should().Be("ESCROW_034");
        _ledger.RowsOf(EscrowId).Should().OnlyContain(r => r.Status == "manual" && r.ManualReason == "PAYOUT_NOT_SUPPORTED");
        Status.Should().Be("escrowed"); // no money left the platform account
        _alarms.Should().BeEmpty();
        AssertMoneyInvariants();

        // A second release finds nothing held and calls nobody.
        (await Service().ReleaseAsync(Release(), "system")).Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        _gateway.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Release_FarmerBlocked_PaysTheTrucker_TheCropLegWaits_ThenTheFarmerIsPaid()
    {
        Seed();
        Verified(FarmerId, "+251911000001", phoneVerified: false);

        var first = await Service().ReleaseAsync(Release(), "system");

        first.Error!.Code.Should().Be("ESCROW_029");
        _ledger.RowsOf(EscrowId).Should().ContainSingle(r => r.Ref == "release#trucker#trucker-1" && r.Status == "done");
        _ledger.State(EscrowId).Legs["crop"].Should().Be("held");
        Status.Should().Be("escrowed"); // a trucker payout first keeps escrowed
        AssertMoneyInvariants();

        Verified(FarmerId, "+251911000001");
        var second = await Service().ReleaseAsync(Release(), "system");

        second.IsSuccess.Should().BeTrue();
        second.Value!.Status.Should().Be("completed");
        CallsFor("release#trucker#trucker-1").Should().Be(1);
        CallsFor("release#farmer").Should().Be(1);
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task Release_TruckerBlocked_PaysTheFarmer_ThenTheTransportRetryPaysTheTrucker()
    {
        Seed();
        Verified(TruckerId, "+251911000002", phoneVerified: false);

        var first = await Service().ReleaseAsync(Release(), "system");

        first.Value!.Status.Should().Be("released");
        _ledger.State(EscrowId).Legs["transport"].Should().Be("held");
        AssertMoneyInvariants();

        (await Service().RetryLedgerTransportLegAsync(_ledger.Copy(EscrowId)!)).Should().Be(EscrowService.LedgerCallOutcome.Blocked);
        Verified(TruckerId, "+251911000002");
        (await Service().RetryLedgerTransportLegAsync(_ledger.Copy(EscrowId)!)).Should().Be(EscrowService.LedgerCallOutcome.Done);

        Status.Should().Be("completed");
        CallsFor("release#trucker#trucker-1").Should().Be(1);
        AssertMoneyInvariants();
    }

    // ── Refund ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refund_ReturnsEveryHeldLegToTheBuyer_InOneRow()
    {
        Seed(change: e => e["refundReason"] = "farmer_declined");

        var result = await Service().RefundAsync(Refund(), "system");

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("refunded");
        result.Value.RefundAmount.Should().Be(17_250m);
        var row = _ledger.Row(EscrowId, "refund#all");
        (row.Status, row.Amount, row.PayeeId, row.Reason).Should().Be(("done", 17_250m, BuyerId, "farmer_declined"));
        _gateway.Calls.Should().ContainSingle(c => c.Kind == "refund" && c.Destination == "+251911000003");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task Refund_ToABuyerWhosePhoneIsNotVerified_GoesToTheManualQueue()
    {
        Seed();
        Verified(BuyerId, "+251911000003", phoneVerified: false);

        var result = await Service().RefundAsync(Refund(), "system");

        result.Error!.Code.Should().Be("ESCROW_034");
        _ledger.Row(EscrowId, "refund#all").ManualReason.Should().Be("PAYEE_NOT_VERIFIED");
        _gateway.Calls.Should().BeEmpty();
        AssertMoneyInvariants();
    }

    // ── Failures and unknown outcomes ───────────────────────────────────────

    [Fact]
    public async Task DefiniteRefusals_AreRetriedWithTheSameReference_AndTheThirdGoesManual()
    {
        Seed(change: e => { e["truckerPayout"] = 0m; e["amount"] = 15_750m; e[LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m); });
        for (var i = 0; i < 3; i++)
        {
            _gateway.Script.Enqueue((false, "TELEBIRR_RELEASE_FAILED", null));
        }

        (await Service().ReleaseAsync(Release(), "system")).Error!.Code.Should().Be("ESCROW_013");
        var row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.Attempts).Should().Be(("failed", 1));

        // Not due yet: the sweep leaves it.
        (await SweepAsync()).Failed.Should().Be(0);
        _now += TimeSpan.FromMinutes(16);
        (await SweepAsync()).Failed.Should().Be(1);
        _now += TimeSpan.FromMinutes(16);
        (await SweepAsync()).Manual.Should().Be(1);

        row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.Attempts, row.ManualReason).Should().Be(("manual", 3, "GATEWAY_REFUSED_3"));
        _gateway.Calls.Select(c => c.TransactionRef).Distinct().Should().ContainSingle();
        Status.Should().Be("escrowed");
        AssertMoneyInvariants();
    }

    [Theory]
    [InlineData(TelebirrGatewayErrors.Timeout)]
    [InlineData(TelebirrGatewayErrors.Network)]
    [InlineData("GATEWAY_EXCEPTION:HttpRequestException")]
    public async Task UnknownOutcome_IsParkedAsSent_AndNeverCalledAgainBlindly(string code)
    {
        Seed();
        _gateway.Script.Enqueue((false, code, null)); // the farmer: no answer
        // the trucker succeeds

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.Message.Should().Contain("may already have been made");
        _ledger.Row(EscrowId, "release#farmer").Status.Should().Be("sent");
        _alarms.Should().Contain(l => l.Contains("ESCROW_SETTLEMENT_FAILED") && l.Contains("outcome=unknown"));

        // Sweeps before and after the recheck time: no second release call, a status check first.
        await SweepAsync();
        _now += TimeSpan.FromMinutes(16);
        _gateway.StatusAnswer = r => new TransactionStatusResponse { TransactionRef = r, Status = "unknown", Amount = 0m, UpdatedAt = "" };
        await SweepAsync();

        CallsFor("release#farmer").Should().Be(1);
        _gateway.StatusChecks.Should().ContainSingle();
        var row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.ManualReason).Should().Be(("manual", "GATEWAY_OUTCOME_UNKNOWN"));
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task UnknownOutcome_TheStatusCheckSaysPaid_IsRecordedDone_WithoutASecondCall()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.Timeout, null));
        await Service().ReleaseAsync(Release(), "system");
        _gateway.StatusAnswer = r => new TransactionStatusResponse
        {
            TransactionRef = r, Status = "completed", TelebirrRef = "TB-LATE-1", Amount = 15_000m, UpdatedAt = ""
        };

        _now += TimeSpan.FromMinutes(16);
        var counts = await SweepAsync();

        counts.Done.Should().Be(1);
        var row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.TelebirrRef).Should().Be(("done", "TB-LATE-1"));
        CallsFor("release#farmer").Should().Be(1);
        Status.Should().Be("completed");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task UnknownOutcome_TheStatusCheckSaysItNeverHappened_IsReopened_AndCalledWithTheSameReference()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.Timeout, null));
        await Service().ReleaseAsync(Release(), "system");
        _gateway.StatusAnswer = r => new TransactionStatusResponse { TransactionRef = r, Status = "not_found", Amount = 0m, UpdatedAt = "" };

        _now += TimeSpan.FromMinutes(16);
        await SweepAsync(); // the status check first: reopened as failed and due now, then the call again

        _gateway.StatusChecks.Should().ContainSingle();
        _ledger.AuditsOf(EscrowId).Should().Contain(a => a.Action == "ledger_failed" && a.Metadata.Contains("code=STATUS_NOT_FOUND"));
        CallsFor("release#farmer").Should().Be(2);
        _gateway.Calls.Where(c => c.Kind == "release" && c.Amount == 15_000m).Select(c => c.TransactionRef).Distinct().Should().ContainSingle();
        _ledger.Row(EscrowId, "release#farmer").Status.Should().Be("done");
        Status.Should().Be("completed");
        AssertMoneyInvariants();
    }

    // ── Crashes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CrashBetweenTheWriteAheadAndTheCall_TheSweepCallsEachRowOnce()
    {
        Seed();
        // The decision lands, then the Lambda dies before any call.
        var decided = await Service().DecideLedgerAsync(_ledger.Copy(EscrowId)!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, true, "delivery_confirmed"), "system", callNow: true);
        decided.Outcome.Should().Be(EscrowService.LedgerDecideOutcome.Written);
        _gateway.Calls.Should().BeEmpty();

        await SweepAsync(); // not due: the writer was going to call
        _gateway.Calls.Should().BeEmpty();

        _now += TimeSpan.FromMinutes(16);
        var counts = await SweepAsync();

        counts.Done.Should().Be(2);
        CallsFor("release#farmer").Should().Be(1);
        CallsFor("release#trucker#trucker-1").Should().Be(1);
        Status.Should().Be("completed");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task CrashAfterTheCall_BeforeItsResultIsRecorded_TheSweepAsksTheGatewayFirst()
    {
        Seed(change: e => { e["truckerPayout"] = 0m; e["amount"] = 15_750m; e[LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m); });
        _ledger.ThrowOnNextTransition = new InvalidOperationException("the Lambda died");

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_034"); // unknown outcome answer
        var row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.CallingSince).Should().Be(("pending", "2026-11-07T10:00:00.0000000Z"));

        await SweepAsync(); // a caller may still be on it: left alone
        _gateway.StatusChecks.Should().BeEmpty();

        _now += TimeSpan.FromMinutes(16);
        _gateway.StatusAnswer = r => new TransactionStatusResponse { TransactionRef = r, Status = "completed", TelebirrRef = "TB-1", Amount = 0m, UpdatedAt = "" };
        await SweepAsync();

        CallsFor("release#farmer").Should().Be(1);
        _gateway.StatusChecks.Should().ContainSingle();
        row = _ledger.Row(EscrowId, "release#farmer");
        (row.Status, row.Attempts, row.TelebirrRef).Should().Be(("done", 1, "TB-1"));
        Status.Should().Be("completed");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task TheRecoverySweep_RunTwice_ChangesNothingTheSecondTime()
    {
        Seed();
        _gateway.Script.Enqueue((false, "TELEBIRR_RELEASE_FAILED", null));
        await Service().ReleaseAsync(Release(), "system");
        _now += TimeSpan.FromMinutes(16);

        await SweepAsync();
        var calls = _gateway.Calls.Count;
        var audits = _ledger.Audits.Count;
        var rows = _ledger.RowsOf(EscrowId);
        var second = await SweepAsync();

        second.Seen.Should().Be(0);
        _gateway.Calls.Should().HaveCount(calls);
        _ledger.Audits.Should().HaveCount(audits);
        _ledger.RowsOf(EscrowId).Should().BeEquivalentTo(rows);
        AssertMoneyInvariants();
    }

    // ── Races ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoReleasesAtOnce_DecideOnce_AndPayEachLegOnce()
    {
        Seed();
        var service = Service();
        // The second worker decides and pays between the first worker's read and its write.
        _ledger.BeforeNextDecide = () => service.ReleaseAsync(Release(), "system").GetAwaiter().GetResult();

        var first = await service.ReleaseAsync(Release(), "system");

        first.IsDuplicate.Should().BeTrue();
        _ledger.DecideWrites.Should().Be(1);
        CallsFor("release#farmer").Should().Be(1);
        CallsFor("release#trucker#trucker-1").Should().Be(1);
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task AReleaseAndARefundAtOnce_OnlyOneDecides()
    {
        Seed();
        var service = Service();
        _ledger.BeforeNextDecide = () => service.RefundAsync(Refund(), "system").GetAwaiter().GetResult();

        var release = await service.ReleaseAsync(Release(), "system");

        release.IsSuccess.Should().BeFalse();
        Status.Should().Be("refunded");
        _gateway.Calls.Should().ContainSingle(c => c.Kind == "refund");
        _ledger.RowsOf(EscrowId).Select(r => r.Ref).Should().BeEquivalentTo("refund#all");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task TwoWorkersOnOneRow_OnlyTheOneThatStampsItCalls()
    {
        Seed();
        var decided = await Service().DecideLedgerAsync(_ledger.Copy(EscrowId)!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, false, "x"), "system", callNow: false);
        var row = decided.Rows.Single();

        var first = await Service().CallLedgerRowAsync(row, "system");
        var second = await Service().CallLedgerRowAsync(row, "system"); // the same stale read

        first.Outcome.Should().Be(EscrowService.LedgerCallOutcome.Done);
        second.Outcome.Should().Be(EscrowService.LedgerCallOutcome.Busy);
        CallsFor("release#farmer").Should().Be(1);
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task TwoRowsFinishingAtOnce_TheEscrowStillEndsCompleted()
    {
        Seed();
        var service = Service();
        var decided = await service.DecideLedgerAsync(_ledger.Copy(EscrowId)!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, true, "x"), "system", callNow: false);
        var farmer = decided.Rows.Single(r => r.Ref == "release#farmer");
        var trucker = decided.Rows.Single(r => r.Ref != "release#farmer");

        // The trucker row completes between the farmer row's escrow read and its write.
        _ledger.BeforeNextTransition = _ =>
            service.CompleteLedgerRowAsync(trucker, "TB-T", "gateway", 1, "system").GetAwaiter().GetResult();
        var done = await service.CompleteLedgerRowAsync(farmer, "TB-F", "gateway", 1, "system");

        done.EscrowStatus.Should().Be("completed");
        Status.Should().Be("completed");
        _ledger.AuditsOf(EscrowId).Count(a => a.Action == "complete").Should().Be(1);
        AssertMoneyInvariants();
    }

    // ── Invariant guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnEscrowWhoseLegsDoNotAddUp_IsNeverSettled_AndAlarms()
    {
        Seed(change: e => e["farmerPayout"] = 15_100m); // I1 broken

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.HttpStatus.Should().Be(503);
        result.Error.Code.Should().Be("ESCROW_012");
        _ledger.RowsOf(EscrowId).Should().BeEmpty();
        _gateway.Calls.Should().BeEmpty();
        _alarms.Should().Contain(l => l.StartsWith("ESCROW_LEDGER_INVARIANT") && l.Contains("check=I1"));
    }

    [Fact]
    public async Task WithoutTheLedgerTable_ALedgerEscrowFailsClosed_AndNeverTakesTheLock()
    {
        Seed();
        var service = new EscrowService(_ledger.EscrowRepo, _auditRepo, _idempotency, _gateway, _users) { AlarmLog = _alarms.Add };

        var result = await service.ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_012");
        await _ledger.EscrowRepo.DidNotReceiveWithAnyArgs().TryAcquireSettlementLockAsync(default!, default!, default!, default!);
        _gateway.Calls.Should().BeEmpty();
    }

    // ── Split verdict ───────────────────────────────────────────────────────

    private void SeedSplit(decimal farmer, decimal trucker, decimal buyer)
        => Seed("dispute_resolved", withOrder: false, change: e =>
        {
            e["disputeResolution"] = "partial_refund";
            e["disputeResolvedAt"] = "2026-11-06T10:00:00.0000000Z";
            e[LedgerEscrowFields.VerdictAmounts] = new VerdictAmounts(farmer, trucker, buyer).ToMap();
        });

    [Fact]
    public async Task SplitVerdict_PaysEachPartyItsShare_AndCompletes()
    {
        SeedSplit(9_000m, 1_500m, 6_000m);

        var result = await Service().SettleSplitVerdictAsync(EscrowId);

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("completed");
        _gateway.Calls.Select(c => (c.Kind, c.Amount)).Should().BeEquivalentTo(new[]
        {
            ("release", 9_000m), ("release", 1_500m), ("refund", 6_000m)
        });
        var state = _ledger.State(EscrowId);
        (state.ReleasedTotal, state.RefundedTotal, state.FarmerPayout, state.Commission).Should().Be((10_500m, 6_000m, 9_000m, 750m));
        AssertMoneyInvariants();

        // Settled once: the next sweep run finds nothing to split.
        (await Service().SettleSplitVerdictAsync(EscrowId)).IsSuccess.Should().BeFalse();
        _gateway.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task SplitVerdict_ABlockedPayee_SplitsNothing_UntilTheGatePasses()
    {
        SeedSplit(9_000m, 1_500m, 6_000m);
        Verified(TruckerId, "+251911000002", phoneVerified: false);

        (await Service().SettleSplitVerdictAsync(EscrowId)).Error!.Code.Should().Be("ESCROW_029");

        _ledger.RowsOf(EscrowId).Should().BeEmpty();
        _ledger.State(EscrowId).Legs.Values.Should().AllBeEquivalentTo("held");
        _gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ASplitVerdict_IsNeverSettledByAPlainReleaseOrRefund()
    {
        SeedSplit(9_000m, 1_500m, 6_000m);

        (await Service().ReleaseAsync(Release("dispute_resolved"), "system")).Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        (await Service().RefundAsync(Refund() with { Trigger = "dispute_resolved" }, "system")).Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        _ledger.RowsOf(EscrowId).Should().BeEmpty();
    }

    // ── Duplicate payment ───────────────────────────────────────────────────

    [Fact]
    public async Task ADuplicatePayment_IsRefundedBySweep_OutsideTheEscrowAmount()
    {
        Seed();
        var versionBefore = _ledger.State(EscrowId).Version;

        (await Service().RecordDuplicatePaymentRefundAsync("o1", BuyerId, "PAY-2", 17_250m)).Should().BeTrue();
        (await Service().RecordDuplicatePaymentRefundAsync("o1", BuyerId, "PAY-2", 17_250m)).Should().BeFalse();

        _ledger.State(EscrowId).Version.Should().Be(versionBefore); // the escrow row is not written
        var counts = await SweepAsync();

        counts.Done.Should().Be(1);
        _gateway.Calls.Should().ContainSingle(c => c.Kind == "refund" && c.Amount == 17_250m && c.HoldRef == "PAY-2");
        _ledger.Row(EscrowId, "refund#duplicate#PAY-2").Status.Should().Be("done");
        _ledger.AuditsOf(EscrowId).Should().Contain(a => a.Action == UnappliedPayments.RefundedManual
                                                           && a.Metadata.Contains("paymentOrderId=PAY-2")
                                                           && a.Metadata.Contains("amount=17250"));
        Status.Should().Be("escrowed");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task ADuplicatePaymentAlreadyRefundedByHand_GetsNoLedgerRow()
    {
        Seed();
        _auditRepo.GetAuditTrailAsync(EscrowId, true).Returns(new List<Dictionary<string, object>>
        {
            new() { ["action"] = UnappliedPayments.RefundedManual, ["metadata"] = "paymentOrderId=PAY-2;amount=17250" }
        });

        (await Service().RecordDuplicatePaymentRefundAsync("o1", BuyerId, "PAY-2", 17_250m)).Should().BeFalse();
        _ledger.RowsOf(EscrowId).Should().BeEmpty();
    }

    // ── The manual queue ────────────────────────────────────────────────────

    [Fact]
    public async Task ManualRows_AreListed_AndRecordedOneByOne_ThenTheEscrowCompletes()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        await Service().ReleaseAsync(Release(), "system");

        var list = await Service().ListManualPayoutsAsync(20, null);
        var entry = list.Value!.Payouts.Should().ContainSingle().Subject;
        entry.Legs.Select(l => l.Ref).Should().BeEquivalentTo("release#farmer", "release#trucker#trucker-1");
        entry.Op.Should().Be("release");
        entry.PendingAmount.Should().Be(16_500m);

        var farmer = await Service().CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", "admin-1", adminGroupVerified: true);
        farmer.Value!.EscrowStatus.Should().Be("released");
        farmer.Value.RemainingLegs.Should().BeEquivalentTo("release#trucker#trucker-1");
        _ledger.Row(EscrowId, "release#farmer").PaidBy.Should().Be("admin-1");

        var trucker = await Service().CompleteManualPayoutAsync(EscrowId, "release#trucker#trucker-1", "TBR-0002", "admin-1", adminGroupVerified: true);
        trucker.Value!.EscrowStatus.Should().Be("completed");
        AssertMoneyInvariants();

        var replay = await Service().CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", "admin-1", adminGroupVerified: true);
        replay.Value!.AlreadyRecorded.Should().BeTrue();
        var other = await Service().CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-9999", "admin-1", adminGroupVerified: true);
        other.Error!.Code.Should().Be("ESCROW_036");
        _gateway.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ARowThatIsNotManual_CannotBeRecordedByHand()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.Timeout, null));
        await Service().ReleaseAsync(Release(), "system"); // the farmer row is sent: the outcome is unknown

        var result = await Service().CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", "admin-1", adminGroupVerified: true);

        result.Error!.Code.Should().Be("ESCROW_035");
        _ledger.Row(EscrowId, "release#farmer").Status.Should().Be("sent");
    }

    [Fact]
    public async Task AManualRowOpenFor24Hours_LogsTheOverdueLine_WithItsLedgerRef()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        await Service().ReleaseAsync(Release(), "system");

        _now += TimeSpan.FromHours(25);
        var counts = await SweepAsync();

        counts.ManualOverdue.Should().Be(2);
        _alarms.Should().Contain(l => l.StartsWith("MANUAL_PAYOUT_OVERDUE") && l.Contains("ref=release.farmer"));
    }

    // ── Disputes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADisputeRefusedOnADecidedLedgerEscrow_SaysThePaymentIsBeingSettled()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        await Service().ReleaseAsync(Release(), "system"); // decided, waiting in the manual queue: still escrowed

        var refusal = EscrowService.DisputeRaceRefusal(_ledger.Copy(EscrowId));

        refusal.Error!.HttpStatus.Should().Be(409);
        refusal.Error.Message.Should().Contain("already being settled");
    }

    // ── Birth and reads ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ANewOrderEscrow_IsBornOnTheLedger_OnlyBehindTheFlag(bool flag, bool expected)
    {
        Dictionary<string, object>? created = null;
        _ledger.EscrowRepo.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        await _ledger.EscrowRepo.CreateAsync(Arg.Do<Dictionary<string, object>>(d => created = d));
        var service = new EscrowService(_ledger.EscrowRepo, _auditRepo, _idempotency, _gateway, _users)
        {
            LedgerRepo = _ledger, LedgerBirthEnabled = flag
        };

        await service.CreateFromFundedOrderAsync(new Dictionary<string, object>
        {
            ["orderId"] = "o9", ["buyerId"] = BuyerId, ["farmerId"] = FarmerId, ["truckerId"] = TruckerId,
            ["orderTotal"] = 17_250m, ["cropCost"] = 15_000m, ["transportCost"] = 1_500m,
            ["paymentVerifiedAt"] = CreatedAt
        }, "system");

        created.Should().NotBeNull();
        created!.ContainsKey(LedgerEscrowFields.SettlementModel).Should().Be(expected);
        if (expected)
        {
            ((Dictionary<string, object>)created[LedgerEscrowFields.LegStatus]).Should().BeEquivalentTo(
                new Dictionary<string, object> { ["crop"] = "held", ["transport"] = "held", ["commission"] = "held" });
            created[LedgerEscrowFields.RefundedTotal].Should().Be(0m);
        }
    }

    [Fact]
    public async Task TheEscrowRead_ShowsTheLegsAndTotals_AndARefundOnItsWay()
    {
        Seed();
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        await Service().RefundAsync(Refund(), "system");

        var dto = EscrowService.MapToEscrowDto(_ledger.Copy(EscrowId)!);

        dto.LegStatus.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "refunded", ["transport"] = "refunded", ["commission"] = "refunded"
        });
        dto.RefundedTotal.Should().Be(17_250m);
        dto.ReleasedTotal.Should().Be(0m);
        dto.RefundPending.Should().BeTrue();
        dto.Status.Should().Be("escrowed");
    }
}
