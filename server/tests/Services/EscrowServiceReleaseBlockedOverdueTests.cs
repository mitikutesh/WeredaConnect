using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-41: a release the payout identity gate blocks was audited once (ESC-10, <c>release_blocked_kyc</c>) and then
/// waited silently for as long as the payee did not verify. The hourly sweep's release now logs
/// <c>RELEASE_BLOCKED_OVERDUE</c> on every run that finds the same stored reason a day old or more (the
/// <c>releases-blocked-overdue</c> alarm), from the 1.x release, the ledger release and the sweep's split verdict.
/// An admin's own release, a reason that just changed and a risk block (FUP-29, never stored) do not.
/// </summary>
public class EscrowServiceReleaseBlockedOverdueTests
{
    private const string EscrowId = "escrow-001";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-41";
    private const string BuyerId = "buyer-41";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly List<string> _alarms = new();

    public EscrowServiceReleaseBlockedOverdueTests()
    {
        _escrowRepo.TrySetSettlementBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        Farmer(phoneVerified: false);
    }

    private EscrowService Service() => new(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _users)
    {
        AlarmLog = _alarms.Add
    };

    private void Farmer(bool phoneVerified = true, bool fayda = true, bool suspended = false)
        => _users.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251911000041",
            DisplayName = FarmerId,
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = phoneVerified,
            FaydaVerified = fayda,
            Suspended = suspended,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });

    private static string HoursAgo(double hours) => DateTime.UtcNow.AddHours(-hours).ToString("o");

    /// <summary>A 1.x escrow of 20,000 ETB (above the Fayda threshold) the sweep is about to release.</summary>
    private Dictionary<string, object> Escrow(string? reason = null, string? reasonAt = null, string status = "escrowed")
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["deliveryId"] = "del-41",
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["amount"] = 20_000m,
            ["commission"] = 500m,
            ["farmerPayout"] = 19_500m,
            ["currency"] = "ETB",
            ["status"] = status,
            ["telebirrHoldRef"] = "HOLD-41",
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 3
        };
        if (status == "dispute_resolved")
        {
            item["disputeResolution"] = "farmer_wins";
        }

        if (reason != null)
        {
            item[SettlementBlockFields.Reason] = reason;
            item[SettlementBlockFields.ReasonAt] = reasonAt!;
        }

        _escrowRepo.GetByIdAsync(EscrowId).Returns(item);
        _escrowRepo.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(item);
        return item;
    }

    private static ReleaseEscrowRequest Release(string trigger = "delivery_confirmed") => new()
    {
        EscrowId = EscrowId,
        Trigger = trigger,
        DeliveryId = "del-41",
        IdempotencyKey = $"system:{EscrowId}:auto_release:2026111310"
    };

    private IEnumerable<string> OverdueLines => _alarms.Where(l => l.StartsWith(MoneyAlarmLog.ReleaseBlockedOverdueToken + " "));

    [Fact]
    public async Task SystemRelease_SameReasonStoredADayAgo_LogsTheOverdueLine_AndStillAuditsOnlyOnce()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(30));

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_029");
        OverdueLines.Should().ContainSingle()
            .Which.Should().Be("RELEASE_BLOCKED_OVERDUE escrowId=escrow-001 leg=farmer reason=phone_unverified ageHours=30");
        await _escrowRepo.DidNotReceiveWithAnyArgs().TrySetSettlementBlockReasonAsync(default!, default!, default!, default!, default!);
        await _gateway.DidNotReceiveWithAnyArgs().ReleaseFundsAsync(default!);
    }

    [Fact]
    public async Task SystemRelease_EveryRunWhileTheBlockLasts_LogsOneLinePerRun()
    {
        Escrow("release:payout:fayda_required", HoursAgo(50));
        Farmer(fayda: false);

        await Service().ReleaseAsync(Release(), "system");
        await Service().ReleaseAsync(Release() with { IdempotencyKey = $"system:{EscrowId}:auto_release:2026111311" }, "system");

        OverdueLines.Should().HaveCount(2).And.AllSatisfy(l => l.Should().Contain("leg=farmer reason=fayda_required ageHours=50"));
    }

    [Fact]
    public async Task SystemRelease_BlockYoungerThanADay_LogsNothing()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(23));

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemRelease_TheReasonChanged_StoresTheNewOne_AndTheClockStartsAgain()
    {
        // Blocked on the phone for three days; the farmer verified it, and now the Fayda rule blocks.
        Escrow("release:payout:phone_unverified", HoursAgo(72));
        Farmer(fayda: false);

        await Service().ReleaseAsync(Release(), "system");

        await _escrowRepo.Received(1).TrySetSettlementBlockReasonAsync(EscrowId, CreatedAt, "escrowed",
            "release:payout:fayda_required", Arg.Any<string>());
        OverdueLines.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemRelease_ReasonStoredByAnotherRunMeanwhile_UsesItsTime()
    {
        // The row read had no reason; the compare-and-set lost to a run that stored the same reason a day ago.
        Escrow();
        _escrowRepo.TrySetSettlementBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        var latest = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["status"] = "escrowed",
            ["createdAt"] = CreatedAt,
            [SettlementBlockFields.Reason] = "release:payout:phone_unverified",
            [SettlementBlockFields.ReasonAt] = HoursAgo(26)
        };
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(latest);

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().ContainSingle().Which.Should().EndWith("leg=farmer reason=phone_unverified ageHours=26");
    }

    [Fact]
    public async Task SystemRelease_UnreadableStoredTime_CountsAsOverdue()
    {
        Escrow("release:payout:profile_missing", "not-a-time");
        _users.GetUserByIdAsync(FarmerId).Returns((UserDto?)null);

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().ContainSingle().Which.Should().EndWith("leg=farmer reason=profile_missing ageHours=24");
    }

    [Fact]
    public async Task SystemDisputeRelease_FarmerWinsVerdictBlockedADay_LogsTheLine()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(40), status: "dispute_resolved");

        // No order or delivery repository: the verdict mirror has nothing to move.
        await Service().ReleaseAsync(Release("dispute_resolved"), "system");

        OverdueLines.Should().ContainSingle().Which.Should().Contain("leg=farmer reason=phone_unverified ageHours=40");
    }

    [Fact]
    public async Task AdminRelease_BlockedByTheSameOldReason_LogsNothing()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(30));

        var result = await Service().ReleaseAsync(
            Release("admin_override") with { IdempotencyKey = $"admin-1:{EscrowId}:release:1" },
            "admin-1", role: "admin", adminGroupVerified: true);

        result.Error!.Code.Should().Be("ESCROW_029");
        OverdueLines.Should().BeEmpty();
    }

    // ── FUP-436: the edge-triggered RELEASE_BLOCKED_NEWLY_OVERDUE, once per stored block ─────────────────

    private IEnumerable<string> NewlyLines => _alarms.Where(l => l.StartsWith(MoneyAlarmLog.ReleaseBlockedNewlyOverdueToken + " "));

    [Fact]
    public async Task NewlyOverdue_TheFirstOverdueRun_MarksTheBlockOnItsStoredTime_AndAnnouncesIt()
    {
        var reasonAt = HoursAgo(30);
        Escrow("release:payout:phone_unverified", reasonAt);
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(EscrowId, CreatedAt, "release:payout:phone_unverified", reasonAt)
            .Returns(true);

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().ContainSingle();
        NewlyLines.Should().ContainSingle()
            .Which.Should().Be("RELEASE_BLOCKED_NEWLY_OVERDUE escrowId=escrow-001 leg=farmer reason=phone_unverified ageHours=30");
        await _escrowRepo.Received(1).TryMarkSettlementBlockOverdueLoggedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NewlyOverdue_ABlockAlreadyAnnounced_RepeatsOnlyTheOverdueLine_WithoutAWrite()
    {
        var reasonAt = HoursAgo(50);
        Escrow("release:payout:fayda_required", reasonAt)[SettlementBlockFields.OverdueLoggedFor] = reasonAt;
        Farmer(fayda: false);

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().ContainSingle();
        NewlyLines.Should().BeEmpty();
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryMarkSettlementBlockOverdueLoggedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task NewlyOverdue_AMarkerOfAnEarlierBlock_IsWrittenAgain_AndARefusedConditionAnnouncesNothing()
    {
        // The marker names the time of an earlier block on this escrow; the write is refused (another run marked
        // it, or the escrow was released or the reason cleared since the read).
        var reasonAt = HoursAgo(26);
        Escrow("release:payout:phone_unverified", reasonAt)[SettlementBlockFields.OverdueLoggedFor] = HoursAgo(200);
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        await Service().ReleaseAsync(Release(), "system");

        OverdueLines.Should().ContainSingle();
        NewlyLines.Should().BeEmpty();
        await _escrowRepo.Received(1).TryMarkSettlementBlockOverdueLoggedAsync(
            EscrowId, CreatedAt, "release:payout:phone_unverified", reasonAt);
    }

    [Fact]
    public async Task NewlyOverdue_TheMarkerWriteThrows_AnnouncesAnyway_AndTheBlockIsStillAudited()
    {
        var reasonAt = HoursAgo(30);
        Escrow("release:payout:phone_unverified", reasonAt);
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new Amazon.DynamoDBv2.Model.ProvisionedThroughputExceededException("throttled"));

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_029");
        NewlyLines.Should().ContainSingle().Which.Should().EndWith("leg=farmer reason=phone_unverified ageHours=30");
        // The audit row of the block is still ensured after the marker failed.
        await _auditRepo.ReceivedWithAnyArgs().AppendIfAbsentAsync(default!);
    }

    [Fact]
    public async Task NewlyOverdue_ABlockYoungerThanADay_IsNeitherMarkedNorAnnounced()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(23));
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        await Service().ReleaseAsync(Release(), "system");

        NewlyLines.Should().BeEmpty();
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryMarkSettlementBlockOverdueLoggedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task NewlyOverdue_AnAdminsRelease_NeverMarksOrAnnounces()
    {
        Escrow("release:payout:phone_unverified", HoursAgo(30));
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        await Service().ReleaseAsync(
            Release("admin_override") with { IdempotencyKey = $"admin-1:{EscrowId}:release:1" },
            "admin-1", role: "admin", adminGroupVerified: true);

        NewlyLines.Should().BeEmpty();
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryMarkSettlementBlockOverdueLoggedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task NewlyOverdue_AReasonStoredByAnotherRunMeanwhile_IsKeyedOnThatRunsTime()
    {
        Escrow();
        _escrowRepo.TrySetSettlementBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        var latestAt = HoursAgo(26);
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["status"] = "escrowed",
            ["createdAt"] = CreatedAt,
            [SettlementBlockFields.Reason] = "release:payout:phone_unverified",
            [SettlementBlockFields.ReasonAt] = latestAt
        });
        _escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(EscrowId, CreatedAt, "release:payout:phone_unverified", latestAt)
            .Returns(true);

        await Service().ReleaseAsync(Release(), "system");

        NewlyLines.Should().ContainSingle().Which.Should().EndWith("leg=farmer reason=phone_unverified ageHours=26");
    }

    [Fact]
    public async Task SystemRelease_ARiskBlockAfterAnOldIdentityBlock_LogsOnlyTheRiskLine()
    {
        // The farmer verified the phone but is suspended now: the stored identity reason is stale and is not reported.
        Escrow("release:payout:phone_unverified", HoursAgo(30));
        Farmer(suspended: true);

        var result = await Service().ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_029");
        OverdueLines.Should().BeEmpty();
        _alarms.Should().ContainSingle(l => l.StartsWith(MoneyAlarmLog.RiskSettlementSkippedToken));
    }

    // ── Ledger escrows (ESC-20) ─────────────────────────────────────────────

    private const string LedgerEscrowId = "escrow-order-o41";
    private const string TruckerId = "trucker-41";

    private readonly InMemoryLedger _ledger = new();
    private readonly ScriptedGateway _ledgerGateway = new();

    private EscrowService LedgerService() => new(_ledger.EscrowRepo, _auditRepo, _idempotencyRepo, _ledgerGateway, _users)
    {
        AlarmLog = _alarms.Add,
        LedgerRepo = _ledger
    };

    private void Trucker(bool phoneVerified = true)
        => _users.GetUserByIdAsync(TruckerId).Returns(new UserDto
        {
            UserId = TruckerId,
            PhoneNumber = "+251911000042",
            DisplayName = TruckerId,
            Role = "trucker",
            Woreda = "w",
            IsPhoneVerified = phoneVerified,
            FaydaVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });

    private void SeedLedger(Action<Dictionary<string, object>>? change = null)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = LedgerEscrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["orderId"] = "o41",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["status"] = "escrowed",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-41",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        change?.Invoke(item);
        _ledger.Escrows[LedgerEscrowId] = item;
    }

    [Fact]
    public async Task LedgerRelease_FarmerBlockedADay_LogsTheLine_WhileTheTruckerIsPaid()
    {
        Trucker();
        SeedLedger(e =>
        {
            e[SettlementBlockFields.Reason] = "release:payout:phone_unverified";
            e[SettlementBlockFields.ReasonAt] = HoursAgo(28);
        });

        var result = await LedgerService().ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = LedgerEscrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = "",
            IdempotencyKey = $"system:{LedgerEscrowId}:auto_release:1"
        }, "system");

        result.Error!.Code.Should().Be("ESCROW_029");
        OverdueLines.Should().ContainSingle()
            .Which.Should().Be("RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-o41 leg=farmer reason=phone_unverified ageHours=28");
    }

    private void SeedSplit(string reason, double hoursAgo)
        => SeedLedger(e =>
        {
            e["status"] = "dispute_resolved";
            e["disputeResolution"] = "partial_refund";
            e["disputeResolvedAt"] = "2026-11-06T10:00:00.0000000Z";
            e[LedgerEscrowFields.VerdictAmounts] = new VerdictAmounts(9_000m, 1_500m, 6_000m).ToMap();
            e[SettlementBlockFields.Reason] = reason;
            e[SettlementBlockFields.ReasonAt] = HoursAgo(hoursAgo);
        });

    [Fact]
    public async Task SweepSplitVerdict_TruckerBlockedADay_LogsTheLineForTheTrucker()
    {
        Farmer();
        Trucker(phoneVerified: false);
        SeedSplit("release:trucker:phone_unverified", 25);

        (await LedgerService().SettleSplitVerdictAsync(LedgerEscrowId)).Error!.Code.Should().Be("ESCROW_029");

        OverdueLines.Should().ContainSingle()
            .Which.Should().Be("RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-o41 leg=trucker reason=phone_unverified ageHours=25");
        _ledgerGateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepSplitVerdict_TruckerBlockedADay_AnnouncesTheTruckerOnce()
    {
        // FUP-436: the split's trucker block is keyed on its own stored time, like the farmer's.
        Farmer();
        Trucker(phoneVerified: false);
        SeedSplit("release:trucker:phone_unverified", 25);
        var reasonAt = (string)_ledger.Escrows[LedgerEscrowId][SettlementBlockFields.ReasonAt];
        _ledger.EscrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(
                LedgerEscrowId, CreatedAt, "release:trucker:phone_unverified", reasonAt)
            .Returns(true, false);

        await LedgerService().SettleSplitVerdictAsync(LedgerEscrowId);
        await LedgerService().SettleSplitVerdictAsync(LedgerEscrowId);

        OverdueLines.Should().HaveCount(2);
        NewlyLines.Should().ContainSingle()
            .Which.Should().Be("RELEASE_BLOCKED_NEWLY_OVERDUE escrowId=escrow-order-o41 leg=trucker reason=phone_unverified ageHours=25");
    }

    [Fact]
    public async Task SweepSplitVerdict_FarmerBlockedADay_LogsTheLineForTheFarmer()
    {
        Trucker();
        SeedSplit("release:payout:phone_unverified", 25);

        (await LedgerService().SettleSplitVerdictAsync(LedgerEscrowId)).Error!.Code.Should().Be("ESCROW_029");

        OverdueLines.Should().ContainSingle().Which.Should().Contain("leg=farmer reason=phone_unverified");
    }

    [Fact]
    public async Task AdminHeldExitOfASplit_BlockedByTheSameOldReason_LogsNothing()
    {
        Farmer();
        Trucker(phoneVerified: false);
        SeedSplit("release:trucker:phone_unverified", 25);
        _ledger.Escrows[LedgerEscrowId]["status"] = EscrowHoldFields.Status;
        _ledger.Escrows[LedgerEscrowId][EscrowHoldFields.HeldFromStatus] = "dispute_resolved";

        (await LedgerService().SettleSplitVerdictAsync(LedgerEscrowId, "admin-1", heldExit: true)).Error!.Code
            .Should().Be("ESCROW_029");

        OverdueLines.Should().BeEmpty();
    }
}
