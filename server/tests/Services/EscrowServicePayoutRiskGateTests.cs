using System.Reflection;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-29 (fraud-detection-architecture.md section 17.6, escrow-protocol.md 2.6.0) on the 1.x paths: the
/// shared payout gate refuses a system payout to a payee who is suspended or at risk tier CRITICAL at the
/// moment of payment. The trucker retry and the release are covered here; the ledger paths in
/// <c>Ledger/EscrowLedgerPayoutRiskTests</c>. A block moves no money, writes nothing on the escrow, tells no
/// party and logs the alarm line; the other leg, an admin's own release and a healthy payee are unchanged.
/// </summary>
public class EscrowServicePayoutRiskGateTests
{
    private const string EscrowId = "esc-29";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-29";
    private const string TruckerId = "trucker-29";
    private const string FarmerPhone = "+251911002901";
    private const string TruckerPhone = "+251911002902";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly List<string> _alarms = new();

    public EscrowServicePayoutRiskGateTests()
    {
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(call => new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-" + call.Arg<ReleaseFundsRequest>().TransactionRef });
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _escrows.TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("lock-29");
        _escrows.TryRecordTruckerPayoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrows.TrySetTruckerBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _escrows.TrySetSettlementBlockReasonAsync(default!, default!, default!, default!, default!).ReturnsForAnyArgs(true);
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        Payee(FarmerId, FarmerPhone);
        Payee(TruckerId, TruckerPhone);
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low));
    }

    private EscrowService Service(bool readsTier = true) => new(_escrows, _audit, _idempotency, _gateway, _users)
    {
        AlarmLog = _alarms.Add,
        PayoutRiskService = readsTier ? _fraud : null
    };

    private void Payee(string id, string phone, bool suspended = false, string? suspendReason = null, bool phoneVerified = true)
        => _users.GetUserByIdAsync(id).Returns(new UserDto
        {
            UserId = id,
            PhoneNumber = phone,
            Role = id == FarmerId ? "farmer" : "trucker",
            Woreda = "w",
            IsPhoneVerified = phoneVerified,
            FaydaVerified = true,
            Suspended = suspended,
            SuspendReason = suspendReason,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });

    private static RiskScoreResult Scored(RiskTier tier, bool truncated = false)
        => new() { Score = 0, EnforcementScore = 0, Tier = tier, Truncated = truncated };

    private Dictionary<string, object> Escrow(string status = "escrowed")
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", EscrowId },
            { "createdAt", CreatedAt },
            { "updatedAt", CreatedAt },
            { "status", status },
            { "deliveryId", "del-29" },
            { "buyerId", "buyer-29" },
            { "farmerId", FarmerId },
            { "truckerId", TruckerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 7_300m },
            { "truckerPayout", 500m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-29" },
            { "expiresAt", "2026-10-30T10:00:00.0000000Z" },
            { "deliveryConfirmedAt", "2026-10-03T12:00:00.0000000Z" },
            { "disputeWindowEndsAt", "2026-10-05T12:00:00.0000000Z" },
            { "version", 2 }
        };
        _escrows.GetByIdAsync(EscrowId).Returns(escrow);
        _escrows.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(escrow);
        return escrow;
    }

    /// <summary>A released escrow whose trucker leg is owed, as the hourly retry reads it.</summary>
    private Dictionary<string, object> PendingLeg()
    {
        var row = Escrow("released");
        row["releasedAt"] = "2026-10-06T10:00:00.0000000Z";
        row["telebirrReleaseRef"] = "GW-FARMER";
        row[TruckerPayoutFields.Pending] = true;
        row[TruckerPayoutFields.PendingSince] = "2026-10-06T10:00:00.0000000Z";
        return row;
    }

    private Task<EscrowService.TruckerRetryResult> RetryAsync(EscrowService service, bool mayCallGateway = true)
        => service.RetryTruckerLegAsync(PendingLeg(), DateTime.UtcNow, mayCallGateway);

    private static ReleaseEscrowRequest Release(string trigger = "delivery_confirmed") => new()
    {
        EscrowId = EscrowId,
        Trigger = trigger,
        DeliveryId = "del-29",
        IdempotencyKey = $"system:{EscrowId}:release:1"
    };

    private List<string> AuditActions() => _audit.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name is nameof(IEscrowAuditRepository.AppendAsync)
            or nameof(IEscrowAuditRepository.AppendIfAbsentAsync))
        .Select(c => c.GetArguments()[0])
        .OfType<Dictionary<string, object>>()
        .Select(e => (string)e["action"])
        .ToList();

    /// <summary>Nothing moved and nothing was written for the trucker leg: no lock, no call, no stored reason, no audit row.</summary>
    private async Task AssertTruckerLegUntouchedAsync()
    {
        await _escrows.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrows.DidNotReceive().TrySetTruckerBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _escrows.DidNotReceive().TryRecordTruckerPayoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        AuditActions().Should().BeEmpty();
    }

    // ── The trucker retry ─────────────────────────────────────────────

    [Theory]
    [InlineData(null, "account_suspended")]
    [InlineData("chargeback pattern", "account_suspended")]
    [InlineData("fraud_confirmed", "fraud_confirmed")]
    public async Task TruckerRetry_ATruckerSuspendedSinceTheRelease_IsNotPaid(string? suspendReason, string expected)
    {
        Payee(TruckerId, TruckerPhone, suspended: true, suspendReason: suspendReason);

        var result = await RetryAsync(Service());

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, expected));
        await AssertTruckerLegUntouchedAsync();
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-trucker reason=payee_flagged holdReason={expected}");
        // The suspension is on the row the identity rules loaded: no score read.
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // a truncated read is CRITICAL (section 16)
    public async Task TruckerRetry_ACriticalTrucker_IsNotPaid(bool truncated)
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical, truncated));

        var result = await RetryAsync(Service());

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "risk_critical"));
        await AssertTruckerLegUntouchedAsync();
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-trucker reason=payee_flagged holdReason=risk_critical");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruckerRetry_AScoreThatCannotBeRead_FailsClosed(bool throws)
    {
        // Mirrors PartySuspensionGate: anything that is not a computed score (and not an unconfigured
        // table) must not settle.
        if (throws)
        {
            _fraud.GetUserRiskScoreAsync(TruckerId).ThrowsAsync(new TimeoutException());
        }
        else
        {
            _fraud.GetUserRiskScoreAsync(TruckerId).Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));
        }

        var result = await RetryAsync(Service());

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "score_unavailable"));
        await AssertTruckerLegUntouchedAsync();
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-trucker reason=score_unavailable holdReason=-");
    }

    [Fact]
    public async Task TruckerRetry_AnUnconfiguredSignalsTable_IsLow_AndPays()
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(RiskScoreResult.Unscored(RiskScoreSource.TableNotConfigured));

        (await RetryAsync(Service())).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task TruckerRetry_WithoutAFraudService_StillBlocksASuspendedTrucker_AndPaysAnyOther()
    {
        (await RetryAsync(Service(readsTier: false))).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);

        Payee(TruckerId, TruckerPhone, suspended: true);
        (await RetryAsync(Service(readsTier: false))).Reason.Should().Be("account_suspended");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)] // HIGH at payout keeps today's money behaviour (section 17)
    public async Task TruckerRetry_AHealthyTrucker_IsPaidExactlyAsBefore(RiskTier tier)
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(tier));

        var result = await RetryAsync(Service());

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r =>
            r.TransactionRef == $"release:{EscrowId}:trucker" && r.FarmerPhone == TruckerPhone && r.Amount == 500m
            && r.HoldRef == "HOLD-29" && r.PlatformCommission == 0m));
        await _escrows.Received(1).TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, "lock-29",
            $"GW-release:{EscrowId}:trucker", Arg.Any<string>(), Arg.Any<string>());
        AuditActions().Should().Equal("trucker_release", "complete");
        _alarms.Should().BeEmpty();
        // One bounded score read for the payee, none for the farmer (already paid).
        await _fraud.Received(1).GetUserRiskScoreAsync(TruckerId);
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(FarmerId);
    }

    [Fact]
    public async Task TruckerRetry_PaysOnceTheSuspensionIsLifted()
    {
        Payee(TruckerId, TruckerPhone, suspended: true);
        var service = Service();
        (await RetryAsync(service)).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Blocked);
        (await RetryAsync(service)).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Blocked);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());

        Payee(TruckerId, TruckerPhone);
        (await RetryAsync(service)).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);

        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{EscrowId}:trucker"));
        _alarms.Should().HaveCount(2); // one line per blocked run
    }

    [Fact]
    public async Task TruckerRetry_PaysOnceTheTierDrops()
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical), Scored(RiskTier.High));
        var service = Service();

        (await RetryAsync(service)).Reason.Should().Be("risk_critical");
        (await RetryAsync(service)).Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);

        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task TruckerRetry_ALegThatIsNotPaidThisRunAnyway_CostsNoScoreReadAndNoAlarm()
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical));

        var result = await RetryAsync(Service(), mayCallGateway: false);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Deferred, "deferred"));
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(Arg.Any<string>());
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task TruckerRetry_TheIdentityRulesStillSpeakFirst()
    {
        // An unverified trucker hears what to fix as before, suspended or not: the identity reason is the
        // one stored and audited, and the risk rules are not reached.
        Payee(TruckerId, TruckerPhone, suspended: true, phoneVerified: false);

        var result = await RetryAsync(Service());

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "phone_unverified"));
        await _escrows.Received(1).TrySetTruckerBlockReasonAsync(EscrowId, CreatedAt, "phone_unverified", Arg.Any<string>());
        _alarms.Should().BeEmpty();
    }

    // ── The release (1.x) ─────────────────────────────────────────────

    [Fact]
    public async Task SystemRelease_ASuspendedFarmer_IsNotPaid_AndNothingIsStoredOrAudited()
    {
        // The sweep's gate holds or skips this escrow first; this is the race where the farmer is
        // suspended between the gate and the payout.
        Escrow();
        Payee(FarmerId, FarmerPhone, suspended: true);

        var result = await Service().ReleaseAsync(Release(), EscrowService.SystemActor);

        result.Error!.Code.Should().Be("ESCROW_029");
        result.Error.Message.Should().Be("Payout blocked: the farmer's account is under review.");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrows.DidNotReceiveWithAnyArgs().TryAcquireSettlementLockAsync(default!, default!, default!, default!, default);
        // settlementBlockReason feeds the farmer's "payout blocked" push: a risk block never writes it.
        await _escrows.DidNotReceiveWithAnyArgs().TrySetSettlementBlockReasonAsync(default!, default!, default!, default!, default!);
        AuditActions().Should().BeEmpty();
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"RISK_SETTLEMENT_SKIPPED escrowId={EscrowId} sweep=payout-farmer reason=payee_flagged holdReason=account_suspended");
    }

    [Fact]
    public async Task SystemRelease_ACriticalFarmer_IsNotPaid()
    {
        Escrow();
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(RiskTier.Critical));

        var result = await Service().ReleaseAsync(Release(), EscrowService.SystemActor);

        result.Error!.Code.Should().Be("ESCROW_029");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        _alarms.Should().ContainSingle().Which.Should().EndWith("sweep=payout-farmer reason=payee_flagged holdReason=risk_critical");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SystemRelease_ABlockedTrucker_DoesNotStopTheFarmer_AndTheLegStaysOwed(bool suspended)
    {
        Escrow();
        if (suspended)
        {
            Payee(TruckerId, TruckerPhone, suspended: true);
        }
        else
        {
            _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical));
        }

        Dictionary<string, object>? written = null;
        _escrows.WhenForAnyArgs(r => r.UpdateStatusAsync(default!, default!, default!, default!, default))
            .Do(call => written = call.Args().OfType<Dictionary<string, object>>().FirstOrDefault());

        var result = await Service().ReleaseAsync(Release(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Status.Should().Be("released");
        result.Value.TruckerTelebirrRef.Should().BeNull();
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r =>
            r.TransactionRef == $"release:{EscrowId}" && r.FarmerPhone == FarmerPhone && r.Amount == 7_300m));
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")));
        // The leg is owed and retried, with no reason on the row (it feeds the trucker's push) and no
        // identity-block or failure audit row.
        written.Should().NotBeNull();
        written![TruckerPayoutFields.Pending].Should().Be(true);
        written.Should().ContainKey(TruckerPayoutFields.PendingSince);
        written.Should().NotContainKey(TruckerPayoutFields.BlockReason);
        written.Should().NotContainKey(TruckerPayoutFields.BlockReasonAt);
        AuditActions().Should().Equal("release");
        _alarms.Should().ContainSingle().Which.Should().Contain("sweep=payout-trucker reason=payee_flagged");
    }

    [Fact]
    public async Task AdminRelease_IsNotGated_TheAdminDecides()
    {
        // Section 17: the admin routes are not gated. A suspended farmer and a CRITICAL trucker are both
        // paid by an admin's own release, with no score read and no alarm line.
        Escrow();
        Payee(FarmerId, FarmerPhone, suspended: true, suspendReason: "fraud_confirmed");
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.Critical));

        var result = await Service().ReleaseAsync(Release("admin_override") with { IdempotencyKey = "admin:esc-29:release:1" },
            "admin-7", role: "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{EscrowId}"));
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{EscrowId}:trucker"));
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(Arg.Any<string>());
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemRelease_HealthyPayees_ArePaidExactlyAsBefore()
    {
        Escrow();

        var result = await Service().ReleaseAsync(Release(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r =>
            r.TransactionRef == $"release:{EscrowId}" && r.FarmerPhone == FarmerPhone && r.Amount == 7_300m
            && r.PlatformCommission == 200m && r.HoldRef == "HOLD-29"));
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r =>
            r.TransactionRef == $"release:{EscrowId}:trucker" && r.FarmerPhone == TruckerPhone && r.Amount == 500m));
        await _gateway.Received(2).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        _alarms.Should().BeEmpty();
        await _fraud.Received(1).GetUserRiskScoreAsync(FarmerId);
        await _fraud.Received(1).GetUserRiskScoreAsync(TruckerId);
    }
}
