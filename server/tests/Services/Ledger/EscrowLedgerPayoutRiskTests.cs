using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services.Ledger;

/// <summary>
/// FUP-29 on the ledger paths (escrow-protocol.md 2.6.0): the system never calls the gateway for a payout
/// row whose payee is suspended or at risk tier CRITICAL at the moment of the call. The row stays open and
/// is asked again an hour later, the other payee's row is paid, nothing is recorded on the escrow, an
/// admin's own release is not gated, and a refund to a buyer is not a payout in this sense.
/// </summary>
public class EscrowLedgerPayoutRiskTests
{
    private const string EscrowId = "escrow-order-o29";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string BuyerId = "buyer-1";
    private const string FarmerRef = "release#farmer";
    private const string TruckerRef = "release#trucker#trucker-1";

    private readonly InMemoryLedger _ledger = new();
    private readonly ScriptedGateway _gateway = new();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly List<string> _alarms = new();
    private DateTime _now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    public EscrowLedgerPayoutRiskTests()
    {
        _auditRepo.GetAuditTrailAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(new List<Dictionary<string, object>>());
        User(FarmerId, "+251911000001");
        User(TruckerId, "+251911000002");
        User(BuyerId, "+251911000003");
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low));
    }

    private void User(string id, string phone, bool suspended = false)
        => _users.GetUserByIdAsync(id).Returns(new UserDto
        {
            UserId = id,
            PhoneNumber = phone,
            DisplayName = id,
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            FaydaVerified = true,
            Suspended = suspended,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });

    private static RiskScoreResult Scored(RiskTier tier) => new() { Score = 0, EnforcementScore = 0, Tier = tier };

    private EscrowService Service() => new(_ledger.EscrowRepo, _auditRepo, _idempotency, _gateway, _users)
    {
        AlarmLog = _alarms.Add,
        LedgerRepo = _ledger,
        LedgerClock = () => _now,
        PayoutRiskService = _fraud
    };

    /// <summary>A funded ledger escrow: 15,000 crop, 1,500 transport, 750 commission.</summary>
    private void Seed(Action<Dictionary<string, object>>? change = null)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["orderId"] = "o29",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["status"] = "escrowed",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-1",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        change?.Invoke(item);
        _ledger.Escrows[EscrowId] = item;
    }

    private static ReleaseEscrowRequest Release(string trigger = "delivery_confirmed") => new()
    {
        EscrowId = EscrowId,
        Trigger = trigger,
        DeliveryId = "",
        IdempotencyKey = $"system:{EscrowId}:release:1"
    };

    private string Status => (string)_ledger.Escrow(EscrowId)["status"];

    private int CallsFor(string reference) => _gateway.Calls.Count(c => c.TransactionRef == LedgerRefs.GatewayRef(EscrowId, reference));

    private Task<EscrowService.LedgerSweepCounts> SweepAsync() => Service().SweepLedgerAsync(() => true);

    private void AssertMoneyInvariants()
    {
        var state = _ledger.State(EscrowId);
        var rows = _ledger.RowsOf(EscrowId);
        EscrowLedgerBook.CheckInvariants(state, rows.Select(r => (r.Ref, r.Amount))).Should().BeNull();
        (state.ReleasedTotal + state.RefundedTotal).Should().BeLessThanOrEqualTo(state.Amount);
        _alarms.Should().NotContain(l => l.Contains("ESCROW_LEDGER_INVARIANT"));
    }

    private void AssertNothingRecordedOnTheEscrow()
    {
        // settlementBlockReason feeds the payee's "payout blocked" push: a risk block never writes it.
        _ledger.Escrow(EscrowId).Should().NotContainKey(SettlementBlockFields.Reason);
        _ledger.AuditsOf(EscrowId).Select(a => a.Action).Should().NotContain(new[] { "release_blocked_kyc", "trucker_release_blocked_kyc" });
    }

    // ── The release ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemRelease_ASuspendedFarmer_KeepsTheCropLegHeld_PaysTheTrucker_ThenPaysOnceUnsuspended()
    {
        Seed();
        User(FarmerId, "+251911000001", suspended: true);

        var first = await Service().ReleaseAsync(Release(), "system");

        first.Error!.Code.Should().Be("ESCROW_029");
        _ledger.RowsOf(EscrowId).Should().ContainSingle(r => r.Ref == TruckerRef && r.Status == "done");
        _ledger.State(EscrowId).Legs["crop"].Should().Be("held");
        CallsFor(FarmerRef).Should().Be(0);
        AssertNothingRecordedOnTheEscrow();
        _alarms.Should().Contain(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-farmer reason=payee_flagged holdReason=account_suspended");
        AssertMoneyInvariants();

        User(FarmerId, "+251911000001");
        var second = await Service().ReleaseAsync(Release(), "system");

        second.IsSuccess.Should().BeTrue(second.Error?.Message);
        second.Value!.Status.Should().Be("completed");
        CallsFor(FarmerRef).Should().Be(1);
        CallsFor(TruckerRef).Should().Be(1);
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task SystemRelease_ACriticalTrucker_PaysTheFarmer_AndTheTransportRetryWaitsForTheTier()
    {
        Seed();
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical));

        var first = await Service().ReleaseAsync(Release(), "system");

        first.Value!.Status.Should().Be("released");
        CallsFor(FarmerRef).Should().Be(1);
        _ledger.State(EscrowId).Legs["transport"].Should().Be("held");
        AssertNothingRecordedOnTheEscrow();

        var blocked = await Service().RetryLedgerTransportLegAsync(_ledger.Copy(EscrowId)!);
        (blocked.Outcome, blocked.BlockReason).Should().Be((EscrowService.LedgerCallOutcome.Blocked, "risk_critical"));
        CallsFor(TruckerRef).Should().Be(0);
        AssertNothingRecordedOnTheEscrow();

        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.High));
        (await Service().RetryLedgerTransportLegAsync(_ledger.Copy(EscrowId)!)).Outcome.Should().Be(EscrowService.LedgerCallOutcome.Done);

        Status.Should().Be("completed");
        CallsFor(TruckerRef).Should().Be(1);
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task AdminRelease_IsNotGated_TheAdminDecides()
    {
        Seed();
        User(FarmerId, "+251911000001", suspended: true);
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical));

        var result = await Service().ReleaseAsync(Release("admin_override"), "admin-1", "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("completed");
        CallsFor(FarmerRef).Should().Be(1);
        CallsFor(TruckerRef).Should().Be(1);
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(Arg.Any<string>());
        _alarms.Should().NotContain(l => l.Contains("RISK_SETTLEMENT_SKIPPED"));
    }

    [Fact]
    public async Task SystemRelease_HealthyPayees_ArePaidExactlyAsBefore()
    {
        Seed();

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Value!.Status.Should().Be("completed");
        _gateway.Calls.Select(c => (c.Kind, c.Amount, c.Destination)).Should().BeEquivalentTo(new[]
        {
            ("release", 15_000m, "+251911000001"), ("release", 1_500m, "+251911000002")
        });
        _ledger.DecideWrites.Should().Be(1);
        _ledger.RowsOf(EscrowId).Should().OnlyContain(r => r.Status == "done" && r.LastCode == null);
        _alarms.Should().BeEmpty();
        AssertMoneyInvariants();
    }

    // ── The ledger sweep ────────────────────────────────────────────────────

    /// <summary>A system release whose farmer row Telebirr refused once: the row is failed and due in 15 minutes.</summary>
    private async Task SeedAFailedFarmerRowAsync()
    {
        Seed(e => { e["truckerPayout"] = 0m; e["amount"] = 15_750m; e[LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m); });
        _gateway.Script.Enqueue((false, "TELEBIRR_RELEASE_FAILED", null));
        (await Service().ReleaseAsync(Release(), "system")).Error!.Code.Should().Be("ESCROW_013");
        _ledger.Row(EscrowId, FarmerRef).Status.Should().Be("failed");
        _alarms.Clear();
        _now = _now.AddMinutes(20);
    }

    [Theory]
    [InlineData(true, "account_suspended")]
    [InlineData(false, "risk_critical")]
    public async Task Sweep_ARowWhosePayeeWasFlaggedAfterTheDecision_IsNotCalled_AndPaysOnceTheFlagIsGone(bool suspended, string reason)
    {
        await SeedAFailedFarmerRowAsync();
        if (suspended)
        {
            User(FarmerId, "+251911000001", suspended: true);
        }
        else
        {
            _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(RiskTier.Critical));
        }

        var first = await SweepAsync();

        first.Blocked.Should().Be(1);
        CallsFor(FarmerRef).Should().Be(1); // the refused call of the release, none since
        var row = _ledger.Row(EscrowId, FarmerRef);
        (row.Status, row.LastCode, row.Attempts, row.CallingSince).Should().Be(("failed", $"PAYEE_BLOCKED:{reason}", 1, null));
        row.DueAt.Should().Be(_now.AddHours(1).ToString("o"));
        _ledger.AuditsOf(EscrowId).Count(a => a.Action == "ledger_payee_blocked").Should().Be(1);
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-farmer reason=payee_flagged holdReason={reason}");
        Status.Should().Be("escrowed");
        AssertNothingRecordedOnTheEscrow();

        // Not due for an hour, then asked again: still blocked, alarmed again, audited once.
        (await SweepAsync()).Blocked.Should().Be(0);
        _now = _now.AddMinutes(61);
        (await SweepAsync()).Blocked.Should().Be(1);
        _ledger.AuditsOf(EscrowId).Count(a => a.Action == "ledger_payee_blocked").Should().Be(1);
        _alarms.Should().HaveCount(2);
        CallsFor(FarmerRef).Should().Be(1);

        User(FarmerId, "+251911000001");
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(RiskTier.Low));
        _now = _now.AddMinutes(61);
        (await SweepAsync()).Done.Should().Be(1);

        CallsFor(FarmerRef).Should().Be(2); // the same reference, called once more
        _ledger.Row(EscrowId, FarmerRef).Status.Should().Be("done");
        Status.Should().Be("completed");
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task Sweep_AScoreThatCannotBeRead_FailsClosed_ForThatRun()
    {
        await SeedAFailedFarmerRowAsync();
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));

        (await SweepAsync()).Blocked.Should().Be(1);

        CallsFor(FarmerRef).Should().Be(1);
        _ledger.Row(EscrowId, FarmerRef).LastCode.Should().Be("PAYEE_BLOCKED:score_unavailable");
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-farmer reason=score_unavailable holdReason=-");
    }

    [Fact]
    public async Task Sweep_AHealthyPayeesFailedRow_IsRetriedExactlyAsBefore()
    {
        await SeedAFailedFarmerRowAsync();

        (await SweepAsync()).Done.Should().Be(1);

        CallsFor(FarmerRef).Should().Be(2);
        _ledger.Row(EscrowId, FarmerRef).Status.Should().Be("done");
        _alarms.Should().BeEmpty();
        AssertMoneyInvariants();
    }

    // ── The split verdict and the refund ────────────────────────────────────

    [Fact]
    public async Task SplitVerdict_ASuspendedPayee_SplitsNothing_UntilTheSuspensionIsLifted()
    {
        Seed(e =>
        {
            e["status"] = "dispute_resolved";
            e["disputeResolution"] = "partial_refund";
            e["disputeResolvedAt"] = "2026-11-06T10:00:00.0000000Z";
            e[LedgerEscrowFields.VerdictAmounts] = new VerdictAmounts(9_000m, 1_500m, 6_000m).ToMap();
        });
        User(TruckerId, "+251911000002", suspended: true);

        (await Service().SettleSplitVerdictAsync(EscrowId)).Error!.Code.Should().Be("ESCROW_029");

        _ledger.RowsOf(EscrowId).Should().BeEmpty();
        _gateway.Calls.Should().BeEmpty();
        AssertNothingRecordedOnTheEscrow();
        _alarms.Should().ContainSingle().Which.Should().Contain("sweep=payout-trucker reason=payee_flagged holdReason=account_suspended");

        User(TruckerId, "+251911000002");
        var settled = await Service().SettleSplitVerdictAsync(EscrowId);

        settled.IsSuccess.Should().BeTrue(settled.Error?.Code + " " + settled.Error?.Message);
        _gateway.Calls.Select(c => (c.Kind, c.Amount)).Should().BeEquivalentTo(new[]
        {
            ("release", 9_000m), ("release", 1_500m), ("refund", 6_000m)
        });
        AssertMoneyInvariants();
    }

    [Fact]
    public async Task Refund_ToASuspendedCriticalBuyer_IsUnchanged()
    {
        // A refund gives the buyer their own money back: the sweeps' gate decides about it (section 17.2),
        // the payout gate does not.
        Seed();
        User(BuyerId, "+251911000003", suspended: true);
        _fraud.GetUserRiskScoreAsync(BuyerId).Returns(Scored(RiskTier.Critical));

        var result = await Service().RefundAsync(new RefundEscrowRequest
        {
            EscrowId = EscrowId,
            Trigger = "delivery_timeout",
            Reason = "cancelled",
            IdempotencyKey = $"system:{EscrowId}:refund:1"
        }, "system");

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("refunded");
        _gateway.Calls.Should().ContainSingle(c => c.Kind == "refund" && c.Amount == 17_250m && c.Destination == "+251911000003");
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(Arg.Any<string>());
        _alarms.Should().BeEmpty();
    }
}
