using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-218 (escrow-protocol.md 2.18.0): an admin corrects the portal reference of a manual payout leg recorded by
/// mistake. What the service reads, decides and asks the transaction to write, for both settlement models: the
/// replay, every refusal before a write, the reference item that moves only when this leg holds it, the escrow's
/// settlement field only when it holds the old reference, and the retries. The transaction's own conditions are in
/// PayoutReferenceCorrectionRepositoryTests and, against real DynamoDB, PayoutReferenceCorrectionDynamoDbTests.
/// </summary>
public class EscrowServicePayoutReferenceCorrectionTests
{
    private const string EscrowId = "escrow-order-ord-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string AdminA = "admin-a";
    private const string AdminB = "admin-b";
    private const string ReferenceTable = "escrow-idempotency-test";
    private const string WrongRef = "TB-B-PAYMENT";
    private const string RightRef = "TB-A-PAYMENT";
    private const string Reason = "Typed the portal id of the other refund.";

    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private const string NowIso = "2026-11-12T10:00:00.0000000Z";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly FakeReferenceReads _reads = new();
    private readonly FakeCorrections _corrections = new();
    private Dictionary<string, object> _escrow = RecordedRefund();

    public EscrowServicePayoutReferenceCorrectionTests()
    {
        _escrowRepo.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<string>(0) == EscrowId ? new Dictionary<string, object>(_escrow) : null);
        _reads.Hold(WrongRef, EscrowId, "buyer");
    }

    private EscrowService Service(InMemoryLedger? ledger = null, bool wired = true) => new(
        ledger?.EscrowRepo ?? _escrowRepo, _auditRepo, idempotencyRepo: null, telebirrGateway: null)
    {
        ManualPayoutClaims = _reads,
        PayoutReferenceTable = ReferenceTable,
        PayoutReferenceCorrections = wired ? _corrections : null,
        ClaimClock = () => Now,
        LedgerRepo = ledger,
        LedgerClock = () => Now
    };

    private Task<EscrowResult<CorrectPayoutReferenceResponse>> Correct(
        string leg = "buyer", string? oldRef = WrongRef, string? newRef = RightRef, string? reason = Reason,
        bool adminGroup = true, InMemoryLedger? ledger = null)
        => Service(ledger).CorrectPayoutReferenceAsync(EscrowId, leg, oldRef, newRef, reason, AdminB, adminGroup);

    /// <summary>A 1.x refund closed by admin-a, who recorded the buyer leg with the portal id of another refund.</summary>
    private static Dictionary<string, object> RecordedRefund() => new()
    {
        ["escrowId"] = EscrowId,
        ["orderId"] = "ord-1",
        ["buyerId"] = "buyer-1",
        ["farmerId"] = "farmer-1",
        ["amount"] = 20_000m,
        ["status"] = "refunded",
        ["createdAt"] = CreatedAt,
        ["version"] = 5,
        ["telebirrRefundRef"] = WrongRef,
        [ManualPayoutFields.QueuedAt] = "2026-11-10T08:00:00.0000000Z",
        [ManualPayoutFields.Op] = ManualPayoutFields.OpRefund,
        [ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPaid,
        [ManualPayoutFields.RefAttr("buyer")] = WrongRef,
        [ManualPayoutFields.PaidByAttr("buyer")] = AdminA,
        [ManualPayoutFields.PaidAtAttr("buyer")] = "2026-11-10T09:00:00.0000000Z"
    };

    /// <summary>A 1.x release still in the queue: the farmer leg recorded by admin-a, the trucker leg pending.</summary>
    private static Dictionary<string, object> OpenRelease() => new()
    {
        ["escrowId"] = EscrowId,
        ["orderId"] = "ord-1",
        ["status"] = "escrowed",
        ["createdAt"] = CreatedAt,
        ["version"] = 4,
        [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
        [ManualPayoutFields.Op] = ManualPayoutFields.OpRelease,
        [ManualPayoutFields.StatusAttr("farmer")] = ManualPayoutFields.LegPaid,
        [ManualPayoutFields.RefAttr("farmer")] = WrongRef,
        [ManualPayoutFields.PaidByAttr("farmer")] = AdminA,
        [ManualPayoutFields.PaidAtAttr("farmer")] = "2026-11-10T09:00:00.0000000Z",
        [ManualPayoutFields.StatusAttr("trucker")] = ManualPayoutFields.LegPending
    };

    // ── 1.x legs ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARecordedLegOfAClosedRefund_IsCorrected_WithItsSettlementField_ItsItemAndOneAuditRow()
    {
        var result = await Correct(reason: "  " + Reason + "  ");

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.AlreadyCorrected.Should().BeFalse();
        result.Value.CorrectedBy.Should().Be(AdminB);
        result.Value.CorrectedAt.Should().Be(NowIso);
        result.Value.OrderId.Should().Be("ord-1");
        result.Value.Reason.Should().Be(Reason);

        var plan = _corrections.Plans.Should().ContainSingle().Subject;
        plan.EscrowId.Should().Be(EscrowId);
        plan.Ref.Should().Be("buyer");
        plan.OldRef.Should().Be(WrongRef);
        plan.NewRef.Should().Be(RightRef);
        // The leg keeps who recorded it: the write requires that admin, and the audit row names the corrector.
        plan.PaidBy.Should().Be(AdminA);
        plan.Legacy.Should().Be(new LegacyLegRead(CreatedAt, "refunded", 5));
        plan.Ledger.Should().BeNull();
        plan.SettlementRefField.Should().Be("telebirrRefundRef");
        plan.DeleteOldItem.Should().BeTrue();
        plan.NewItem.Should().Be(new PayoutReferenceRecord(ReferenceTable, RightRef, EscrowId, "buyer", AdminB, NowIso));
        plan.Audit.Action.Should().Be("manual_payout_ref_corrected");
        plan.Audit.Actor.Should().Be(AdminB);
        plan.Audit.FromState.Should().Be("refunded");
        plan.Audit.ToState.Should().Be("refunded");
        plan.Audit.TelebirrRef.Should().Be(RightRef);
        plan.Audit.Metadata.Should().Be($"leg=buyer;oldRef={WrongRef};newRef={RightRef};reason={Reason}");
    }

    [Fact]
    public async Task ALegOfAnOpenQueue_LeavesTheSettlementFieldToTheClose()
    {
        _escrow = OpenRelease();
        _reads.Hold(WrongRef, EscrowId, "farmer");

        var result = await Correct(leg: "farmer");

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        var plan = _corrections.Plans.Should().ContainSingle().Subject;
        plan.SettlementRefField.Should().BeNull();
        plan.Legacy!.Status.Should().Be("escrowed");
        plan.DeleteOldItem.Should().BeTrue();
    }

    [Fact]
    public async Task AnOldItemOfAnotherLeg_OrNoItem_IsLeftAlone()
    {
        // FUP-215: one portal reference recorded on two legs before 2.12.0; the backfill gave the item to the other.
        _reads.Hold(WrongRef, "escrow-order-ord-9", "buyer");

        (await Correct()).IsSuccess.Should().BeTrue();
        _corrections.Plans[^1].DeleteOldItem.Should().BeFalse();

        _reads.Holders.Clear();
        (await Correct()).IsSuccess.Should().BeTrue();
        _corrections.Plans[^1].DeleteOldItem.Should().BeFalse();
    }

    [Fact]
    public async Task ARowWithoutAVersion_IsWrittenOnTheVersionsAbsence()
    {
        _escrow.Remove("version");

        (await Correct()).IsSuccess.Should().BeTrue();

        _corrections.Plans.Single().Legacy!.Version.Should().BeNull();
    }

    [Fact]
    public async Task TheSameCorrectionAgain_AnswersFromTheLeg_AndWritesNothing()
    {
        _escrow[ManualPayoutFields.RefAttr("buyer")] = RightRef;

        var result = await Correct();

        result.IsSuccess.Should().BeTrue();
        result.Value!.AlreadyCorrected.Should().BeTrue();
        result.Value.CorrectedBy.Should().BeNull();
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task ACorrectionThatLosesToTheSameCorrection_AnswersTheReplay()
    {
        // Another click landed first: the new item names this leg, and the leg now carries the new reference.
        _corrections.Script.Enqueue(PayoutReferenceCorrectionWrite.ReferenceTaken);
        _corrections.OnEveryWrite = _ =>
        {
            _escrow[ManualPayoutFields.RefAttr("buyer")] = RightRef;
            _reads.Hold(RightRef, EscrowId, "buyer");
        };

        var result = await Correct();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.AlreadyCorrected.Should().BeTrue();
        _corrections.Plans.Should().ContainSingle();
    }

    [Fact]
    public async Task ANewReferenceHeldByAnotherLeg_IsRefusedNamingIt_AndNothingIsWritten()
    {
        _corrections.Script.Enqueue(PayoutReferenceCorrectionWrite.ReferenceTaken);
        _reads.Hold(RightRef, "escrow-order-ord-2", "release#farmer");

        var result = await Correct();

        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_047");
        result.Error.Field.Should().Be("externalRef");
        result.Error.Message.Should().Be("This reference is already recorded on escrow escrow-order-ord-2, leg release#farmer.");
        _corrections.Plans.Should().ContainSingle();
    }

    [Fact]
    public async Task ANewReferenceOnAnotherLegOfTheSameEscrow_IsRefusedBeforeAnyWrite()
    {
        _escrow = OpenRelease();
        _escrow[ManualPayoutFields.StatusAttr("trucker")] = ManualPayoutFields.LegPaid;
        _escrow[ManualPayoutFields.RefAttr("trucker")] = RightRef;

        var result = await Correct(leg: "farmer");

        result.Error!.Code.Should().Be("ESCROW_047");
        result.Error.Message.Should().Be($"This reference is already recorded on escrow {EscrowId}, leg trucker.");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task ALegThatKeepsChanging_StopsAfterThreeAttempts()
    {
        for (var i = 0; i < 3; i++)
        {
            _corrections.Script.Enqueue(PayoutReferenceCorrectionWrite.Changed);
        }

        var result = await Correct();

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_036");
        _corrections.Plans.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnOldItemThatCannotBeRead_StopsTheCorrection()
    {
        // Taking an unreadable item for "none" would leave it naming this leg and keep the other leg refused.
        _reads.ThrowOnRead = true;

        var act = () => Correct();

        await act.Should().ThrowAsync<InvalidOperationException>();
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheAdminGroup_Is403()
    {
        var result = await Correct(adminGroup: false);

        result.Error!.HttpStatus.Should().Be(403);
        result.Error.Code.Should().Be("ESCROW_015");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheCorrectionTransaction_Is503()
    {
        var result = await Service(wired: false).CorrectPayoutReferenceAsync(
            EscrowId, "buyer", WrongRef, RightRef, Reason, AdminB, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(503);
        result.Error.Code.Should().Be("ADMIN_004");
    }

    [Theory]
    [InlineData(WrongRef, "TB", "ESCROW_037", "externalRef")]
    [InlineData(WrongRef, null, "ESCROW_037", "externalRef")]
    [InlineData(WrongRef, "TB A PAYMENT", "ESCROW_037", "externalRef")]
    [InlineData(null, RightRef, "ESCROW_037", "oldExternalRef")]
    [InlineData(WrongRef, "tb-b-payment", "ESCROW_037", "externalRef")]
    public async Task AMalformedOrUnchangedReference_Is400(string? oldRef, string? newRef, string code, string field)
    {
        var result = await Correct(oldRef: oldRef, newRef: newRef);

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Code.Should().Be(code);
        result.Error.Field.Should().Be(field);
        _corrections.Plans.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   too short   ")]
    public async Task AReasonShorterThanTenCharacters_Is400(string? reason)
    {
        var result = await Correct(reason: reason);

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Code.Should().Be("ESCROW_048");
        result.Error.Field.Should().Be("reason");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task AReasonLongerThanFiveHundredCharacters_Is400_AndExactlyFiveHundredIsAccepted()
    {
        (await Correct(reason: new string('r', 501))).Error!.Code.Should().Be("ESCROW_048");
        (await Correct(reason: new string('r', 500))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task APendingLeg_Is409_RecordItInstead()
    {
        _escrow = OpenRelease();

        var result = await Correct(leg: "trucker");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_035");
        result.Error.Message.Should().Contain("not recorded yet");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task ALegTheGatewayPaid_Is409()
    {
        _escrow[ManualPayoutFields.PaidByAttr("buyer")] = ManualPayoutFields.PaidByGateway;

        var result = await Correct();

        result.Error!.Code.Should().Be("ESCROW_035");
        result.Error.Message.Should().Contain("gateway");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task ALegCarryingAnotherReferenceThanTheOneSent_Is409ToReload()
    {
        var result = await Correct(oldRef: "TB-SOMETHING-ELSE");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_036");
        result.Error.Field.Should().Be("oldExternalRef");
        _corrections.Plans.Should().BeEmpty();
    }

    [Theory]
    [InlineData("farmer", 404, "ESCROW_035")]
    [InlineData("seller", 404, "ESCROW_035")]
    public async Task ALegTheEscrowDoesNotHave_Is404(string leg, int status, string code)
    {
        var result = await Correct(leg: leg);

        result.Error!.HttpStatus.Should().Be(status);
        result.Error.Code.Should().Be(code);
    }

    [Fact]
    public async Task AnUnknownEscrow_Is404()
    {
        var result = await Service().CorrectPayoutReferenceAsync(
            "escrow-order-none", "buyer", WrongRef, RightRef, Reason, AdminB, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(404);
        result.Error.Code.Should().Be("ESCROW_016");
    }

    // ── Ledger rows ─────────────────────────────────────────────────────────

    /// <summary>A ledger escrow whose farmer row admin-a recorded with the wrong portal id.</summary>
    private static InMemoryLedger LedgerWithRecordedFarmerRow(string? escrowReleaseRef = WrongRef, string paidBy = AdminA,
        string status = LedgerFields.StatusDone)
    {
        var ledger = new InMemoryLedger();
        ledger.Escrows[EscrowId] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId,
            ["createdAt"] = CreatedAt,
            ["status"] = "completed",
            ["version"] = 6,
            ["amount"] = 17_250m,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger
        };
        if (escrowReleaseRef != null)
        {
            ledger.Escrows[EscrowId]["telebirrReleaseRef"] = escrowReleaseRef;
        }

        ledger.Rows[(EscrowId, LedgerRefs.ReleaseFarmer)] = new LedgerRow
        {
            EscrowId = EscrowId,
            Ref = LedgerRefs.ReleaseFarmer,
            Kind = "release",
            PayeeRole = LedgerFields.PayeeFarmer,
            Amount = 15_000m,
            Status = status,
            GatewayRef = "WCL-1",
            TelebirrRef = status == LedgerFields.StatusDone ? WrongRef : null,
            Reason = "auto_release",
            Actor = "system",
            PaidBy = status == LedgerFields.StatusDone ? paidBy : null,
            OrderId = "ord-1",
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            DoneAt = status == LedgerFields.StatusDone ? "2026-11-10T09:00:00.0000000Z" : null,
            Version = 3
        };
        return ledger;
    }

    [Fact]
    public async Task ALedgerRow_IsCorrectedOnItsVersion_WithTheEscrowFieldThatHoldsTheOldReference()
    {
        var ledger = LedgerWithRecordedFarmerRow();
        _reads.Hold(WrongRef, EscrowId, LedgerRefs.ReleaseFarmer);

        var result = await Correct(leg: "release%23farmer", ledger: ledger);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Ref.Should().Be(LedgerRefs.ReleaseFarmer);
        result.Value.OrderId.Should().Be("ord-1");
        var plan = _corrections.Plans.Should().ContainSingle().Subject;
        plan.Ref.Should().Be(LedgerRefs.ReleaseFarmer);
        plan.Legacy.Should().BeNull();
        plan.Ledger.Should().Be(new LedgerRowRead(3, CreatedAt, 6));
        plan.SettlementRefField.Should().Be("telebirrReleaseRef");
        plan.PaidBy.Should().Be(AdminA);
        plan.DeleteOldItem.Should().BeTrue();
        plan.NewItem.Ref.Should().Be(LedgerRefs.ReleaseFarmer);
        plan.Audit.FromState.Should().Be("completed");
        plan.Audit.Metadata.Should().StartWith($"ref=release#farmer;oldRef={WrongRef};newRef={RightRef};reason=");
    }

    [Fact]
    public async Task ALedgerRowWhoseEscrowFieldHoldsAnotherReference_LeavesTheEscrowAlone()
    {
        var ledger = LedgerWithRecordedFarmerRow(escrowReleaseRef: "TB-LATER-ROW");

        (await Correct(leg: "release%23farmer", ledger: ledger)).IsSuccess.Should().BeTrue();

        var plan = _corrections.Plans.Single();
        plan.Ledger.Should().Be(new LedgerRowRead(3));
        plan.SettlementRefField.Should().BeNull();
    }

    [Fact]
    public async Task ALedgerRowTheGatewayPaid_OrNotDone_Is409()
    {
        (await Correct(leg: "release%23farmer", ledger: LedgerWithRecordedFarmerRow(paidBy: LedgerFields.PaidByGateway)))
            .Error!.Code.Should().Be("ESCROW_035");
        (await Correct(leg: "release%23farmer", ledger: LedgerWithRecordedFarmerRow(status: LedgerFields.StatusManual)))
            .Error!.Code.Should().Be("ESCROW_035");
        _corrections.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownLedgerRow_Is404_AndNoLedger_Is503()
    {
        (await Correct(leg: "release%23trucker%23t9", ledger: LedgerWithRecordedFarmerRow())).Error!.HttpStatus.Should().Be(404);
        (await Correct(leg: "release%23farmer")).Error!.HttpStatus.Should().Be(503);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>The reference item reads of the claim repository (the only part the correction uses).</summary>
    private sealed class FakeReferenceReads : IManualPayoutClaimRepository
    {
        public Dictionary<string, PayoutReferenceHolder> Holders { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnRead { get; set; }

        public void Hold(string externalRef, string escrowId, string reference)
            => Holders[PayoutReferenceFields.Key(externalRef)] = new PayoutReferenceHolder(escrowId, reference, AdminA, null);

        public Task<PayoutReferenceHolder?> GetReferenceHolderAsync(string externalRef)
            => ThrowOnRead
                ? throw new InvalidOperationException("read failed")
                : Task.FromResult(Holders.GetValueOrDefault(PayoutReferenceFields.Key(externalRef)));

        public Task<bool> TryClaimLegAsync(string escrowId, string createdAt, string op, string leg, string actor, string now, string expiresAt)
            => throw new NotSupportedException();

        public Task<bool> TryReleaseLegAsync(string escrowId, string createdAt, string leg, string actor)
            => throw new NotSupportedException();

        public Task<bool> TryClaimLedgerRowAsync(string escrowId, string reference, string actor, string now, string expiresAt)
            => throw new NotSupportedException();

        public Task<bool> TryReleaseLedgerRowAsync(string escrowId, string reference, string actor)
            => throw new NotSupportedException();
    }

    /// <summary>Records each planned transaction and answers from a script (Written when it is empty).</summary>
    private sealed class FakeCorrections : IPayoutReferenceCorrectionRepository
    {
        public List<PayoutReferenceCorrection> Plans { get; } = new();
        public Queue<PayoutReferenceCorrectionWrite> Script { get; } = new();
        public Action<PayoutReferenceCorrection>? OnEveryWrite { get; set; }

        public Task<PayoutReferenceCorrectionWrite> CorrectAsync(PayoutReferenceCorrection correction)
        {
            Plans.Add(correction);
            OnEveryWrite?.Invoke(correction);
            return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : PayoutReferenceCorrectionWrite.Written);
        }
    }
}
