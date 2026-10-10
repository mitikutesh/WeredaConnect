using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-02: fail-closed live payouts and the manual payout queue. A gateway that cannot
/// pay out (PAYOUT_NOT_SUPPORTED) or reports a payout without a reference must never let
/// an escrow reach released, completed or refunded; the settlement waits in the queue
/// until an admin records the Telebirr merchant-portal reference for every leg.
/// </summary>
public class EscrowServiceManualPayoutTests
{
    private const string EscrowId = "escrow-order-ord-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string BuyerId = "buyer-1";
    private const string AdminId = "admin-1";
    private const string FarmerPhone = "+251911000001";
    private const string TruckerPhone = "+251911000002";
    private const string BuyerPhone = "+251911000003";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly List<string> _alarmLines = new();
    private readonly EscrowService _service;

    public EscrowServiceManualPayoutTests()
    {
        _escrowRepo.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(true);
        _escrowRepo.TryMarkManualLegPaidAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        _escrowRepo.TryCloseManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>())
            .Returns(true);
        _idempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        // The live gateway of this phase: no payout API.
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(NotSupportedRelease());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(NotSupportedRefund());

        _userRepo.GetUserByIdAsync(FarmerId).Returns(User(FarmerId, "farmer", FarmerPhone));
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone));
        _userRepo.GetUserByIdAsync(BuyerId).Returns(User(BuyerId, "buyer", BuyerPhone));

        _service = new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _userRepo)
        {
            AlarmLog = _alarmLines.Add
        };
    }

    // ──────────────────────────────────────────────
    // Builders
    // ──────────────────────────────────────────────

    private static ReleaseFundsResponse NotSupportedRelease() => new()
    {
        Success = false,
        ErrorCode = TelebirrGatewayErrors.PayoutNotSupported,
        ErrorMessage = "not integrated"
    };

    private static RefundFundsResponse NotSupportedRefund() => new()
    {
        Success = false,
        ErrorCode = TelebirrGatewayErrors.PayoutNotSupported,
        ErrorMessage = "not integrated"
    };

    private static UserDto User(string id, string role, string phone, bool phoneVerified = true, bool fayda = true) => new()
    {
        UserId = id,
        PhoneNumber = phone,
        DisplayName = $"{role} name",
        Role = role,
        Woreda = "test-woreda",
        IsPhoneVerified = phoneVerified,
        FaydaVerified = fayda,
        FaydaId = fayda ? $"FAYDA-{id}" : null,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    /// <summary>
    /// An order-funded escrow of 20,000 ETB (above the Fayda tier): farmer 16,500,
    /// trucker 3,000 (when present), commission 500.
    /// </summary>
    private static Dictionary<string, object> Escrow(
        string status = "escrowed",
        bool withTrucker = true,
        string? queuedOp = null,
        string farmerLeg = "pending",
        string truckerLeg = "pending",
        string buyerLeg = "pending")
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["orderId"] = "ord-1",
            ["deliveryId"] = "del-1",
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["amount"] = 20_000m,
            ["commission"] = 500m,
            ["commissionRate"] = 0.025m,
            ["farmerPayout"] = 16_500m,
            ["currency"] = "ETB",
            ["status"] = status,
            ["telebirrHoldRef"] = "HOLD-1",
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["expiresAt"] = "2026-10-08T10:00:00.0000000Z",
            ["version"] = 3
        };
        if (withTrucker)
        {
            item["truckerPayout"] = 3_000m;
            item["truckerId"] = TruckerId;
        }

        if (queuedOp != null)
        {
            item["settlementLock"] = queuedOp;
            item[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
            item[ManualPayoutFields.QueuedAt] = "2026-10-02T09:00:00.0000000Z";
            item[ManualPayoutFields.Op] = queuedOp;
            item[ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported;
            item[ManualPayoutFields.FromStatus] = status;
            if (queuedOp == ManualPayoutFields.OpRelease)
            {
                item[ManualPayoutFields.StatusAttr("farmer")] = farmerLeg;
                if (farmerLeg == "paid")
                {
                    item[ManualPayoutFields.RefAttr("farmer")] = "TB-REF-FARMER";
                    item[ManualPayoutFields.PaidByAttr("farmer")] = AdminId;
                    item[ManualPayoutFields.PaidAtAttr("farmer")] = "2026-10-02T10:00:00.0000000Z";
                }

                if (withTrucker)
                {
                    item[ManualPayoutFields.StatusAttr("trucker")] = truckerLeg;
                    if (truckerLeg == "paid")
                    {
                        item[ManualPayoutFields.RefAttr("trucker")] = "TB-REF-TRUCKER";
                        item[ManualPayoutFields.PaidByAttr("trucker")] = AdminId;
                        item[ManualPayoutFields.PaidAtAttr("trucker")] = "2026-10-02T10:05:00.0000000Z";
                    }
                }
            }
            else
            {
                item[ManualPayoutFields.StatusAttr("buyer")] = buyerLeg;
                if (buyerLeg == "paid")
                {
                    item[ManualPayoutFields.RefAttr("buyer")] = "TB-REF-BUYER";
                    item[ManualPayoutFields.PaidByAttr("buyer")] = AdminId;
                    item[ManualPayoutFields.PaidAtAttr("buyer")] = "2026-10-02T10:00:00.0000000Z";
                }
            }
        }

        return item;
    }

    private static ReleaseEscrowRequest ReleaseRequest(string key = "system:escrow-order-ord-1:auto_release:2026100212") => new()
    {
        EscrowId = EscrowId,
        Trigger = "delivery_confirmed",
        DeliveryId = "del-1",
        IdempotencyKey = key
    };

    private static RefundEscrowRequest RefundRequest(string key = "system:escrow-order-ord-1:auto_refund:2026100212") => new()
    {
        EscrowId = EscrowId,
        Trigger = "delivery_timeout",
        Reason = "expired",
        IdempotencyKey = key
    };

    private async Task AssertNothingSettled()
    {
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<Dictionary<string, object>?>());
        await _escrowRepo.DidNotReceive().TryCloseManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    private static void AssertWaiting<T>(EscrowResult<T> result)
    {
        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(202);
        result.Error.Code.Should().Be("ESCROW_034");
    }

    private Task<bool> AuditWritten(string action, string? metadataContains = null)
        => Task.FromResult(AuditEntries().Any(entry =>
            (string)entry["action"] == action
            && (string)entry["action"] == action
            && (metadataContains == null
                || (entry.TryGetValue("metadata", out var m) && ((string)m).Contains(metadataContains)))));

    /// <summary>Every audit entry written, plain appends and the idempotent (deterministic key) ones.</summary>
    private List<Dictionary<string, object>> AuditEntries()
        => _auditRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is nameof(IEscrowAuditRepository.AppendAsync)
                or nameof(IEscrowAuditRepository.AppendIfAbsentAsync))
            .Select(c => c.GetArguments()[0])
            .OfType<Dictionary<string, object>>()
            .ToList();

    // ──────────────────────────────────────────────
    // Release: queueing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_PayoutNotSupported_QueuesFarmerAndTruckerLegs_KeepsLock()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonPayoutNotSupported, "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs =>
                legs.Count == 2
                && legs[0].Leg == "farmer" && legs[0].Status == "pending" && legs[0].Ref == null
                && legs[1].Leg == "trucker" && legs[1].Status == "pending"), Arg.Any<string>());
        // The lock is kept on purpose, nothing settled, the trucker leg was never attempted.
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await AssertNothingSettled();
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _idempotencyRepo.Received(1).UpdateAsync(ReleaseRequest().IdempotencyKey, "manual_payout");
        await _idempotencyRepo.DidNotReceive().DeleteAsync(Arg.Any<string>());
        (await AuditWritten("manual_payout_queued", "legs=farmer,trucker")).Should().BeTrue();
        (await AuditWritten("release_failed")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_PayoutNotSupported_NoTruckerPayout_QueuesFarmerOnly()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: false));

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, Arg.Any<string>(), "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs => legs.Count == 1 && legs[0].Leg == "farmer"), Arg.Any<string>());
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Release_SuccessWithEmptyRef_QueuesWithGatewayRefMissing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: false));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "  " });

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonGatewayRefMissing, "escrowed",
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Release_FarmerPaid_TruckerNeedsManual_QueuesTruckerWithFarmerPaidByGateway()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(NotSupportedRelease());

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonPayoutNotSupported, "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs =>
                legs.Count == 2
                && legs[0].Leg == "farmer" && legs[0].Status == "paid" && legs[0].Ref == "GW-FARMER-1"
                && legs[0].PaidBy == ManualPayoutFields.PaidByGateway && legs[0].PaidAt != null
                && legs[1].Leg == "trucker" && legs[1].Status == "pending"), Arg.Any<string>());
        await AssertNothingSettled();
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Release_QueueWriteFails_NothingMoved_ReleasesLockAndReturns502()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(502);
        result.Error.Code.Should().Be("ESCROW_013");
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
        await _idempotencyRepo.Received(1).DeleteAsync(ReleaseRequest().IdempotencyKey);
        await AssertNothingSettled();
        (await AuditWritten("release_failed", "MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED")).Should().BeTrue();
        (await AuditWritten("manual_payout_queued")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_QueueWriteThrows_NothingMoved_ReleasesLockAndReturns502()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.Error!.Code.Should().Be("ESCROW_013");
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Release_TruckerQueueWriteFails_AfterFarmerPaid_StaysReleasedWithTheTruckerLegPending()
    {
        // The farmer leg is paid, so the escrow must still advance rather than strand under a
        // held lock. The owed trucker leg is flagged and (ESC-04) the escrow stays released for
        // the hourly trucker retry, never completed with the leg unpaid.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(NotSupportedRelease());
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("released");
        result.Value.TelebirrRef.Should().Be("GW-FARMER-1");
        result.Value.TruckerTelebirrRef.Should().BeNull();
        await _escrowRepo.Received(4).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "GW-FARMER-1" && (bool)f["truckerPayoutPending"]
                && f.ContainsKey("truckerPayoutPendingSince") && !f.ContainsKey("truckerPayoutBlockReason")));
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "completed",
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        (await AuditWritten("trucker_release_failed", "MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED")).Should().BeTrue();
        (await AuditWritten("complete")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_TruckerSuccessWithEmptyRef_QueuesTruckerLeg()
    {
        // The gateway says the trucker leg went out but gives no reference: fail closed,
        // never complete with an empty truckerTelebirrRef.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "" });

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonGatewayRefMissing, "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs =>
                legs.Count == 2
                && legs[0].Leg == "farmer" && legs[0].Status == "paid" && legs[0].Ref == "GW-FARMER-1"
                && legs[1].Leg == "trucker" && legs[1].Status == "pending"), Arg.Any<string>());
        await AssertNothingSettled();
        (await AuditWritten("trucker_release")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_QueueWriteThrowsButCommitted_TreatedAsQueued_LockKept()
    {
        // A timeout after the write committed: the strongly consistent re-read shows the
        // queue entry, so nothing is unlocked and no failure is audited.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .ThrowsAsync(new AmazonDynamoDBException("timeout"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).GetByIdAsync(EscrowId, true);
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _idempotencyRepo.DidNotReceive().DeleteAsync(Arg.Any<string>());
        await AssertNothingSettled();
        (await AuditWritten("manual_payout_queued")).Should().BeTrue();
        (await AuditWritten("release_failed")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_QueueWriteFailed_ReReadShowsOtherOp_IsNotTreatedAsQueued()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.Error!.Code.Should().Be("ESCROW_013");
        (await AuditWritten("manual_payout_queued")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_TruckerQueueWriteAmbiguous_DoesNotCompleteWithMarkerOpen()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true)
            .Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(NotSupportedRelease());
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await AssertNothingSettled();
        (await AuditWritten("trucker_release_failed")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_UnverifiedTrucker_StaysReleased_WithTheBlockReasonOnTheRowAndInTheAudit()
    {
        // ESC-04 owns the KYC-blocked trucker leg: no gateway call, no queue entry, and the escrow
        // waits in released (farmer paid) for the trucker retry with the reason kept.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone, phoneVerified: false));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("released");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (bool)f["truckerPayoutPending"]
                && (string)f["truckerPayoutBlockReason"] == "phone_unverified"
                && (string)f["truckerPayoutBlockReasonAt"] == (string)f["truckerPayoutPendingSince"]));
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "completed",
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("trucker_release_blocked_kyc", "code=ESCROW_029;reason=phone_unverified")).Should().BeTrue();
        (await AuditWritten("trucker_release_failed")).Should().BeFalse();
        _alarmLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "no_trucker")]
    [InlineData("trucker-gone", "profile_missing")]
    public async Task Release_TruckerWithoutPayableProfile_IsPendingWithItsReason(string truckerId, string reason)
    {
        var escrow = Escrow();
        if (truckerId.Length == 0)
        {
            escrow.Remove("truckerId");
        }
        else
        {
            escrow["truckerId"] = truckerId;
        }

        _escrowRepo.GetByIdAsync(EscrowId).Returns(escrow);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.Value!.Status.Should().Be("released");
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["truckerPayoutBlockReason"] == reason));
    }

    [Fact]
    public async Task Release_TruckerAboveTheFaydaTierWithoutFayda_IsPendingAsFaydaRequired()
    {
        var escrow = Escrow();
        escrow["truckerPayout"] = 12_000m;
        _escrowRepo.GetByIdAsync(EscrowId).Returns(escrow);
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone, fayda: false));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });

        await _service.ReleaseAsync(ReleaseRequest(), "system");

        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["truckerPayoutBlockReason"] == "fayda_required"));
    }

    [Fact]
    public async Task Release_ManualPayoutOpen_Returns202_NoGatewayNoLockNoAudit()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _idempotencyRepo.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Release_LockLostToQueuedSettlement_Returns202_NoGateway()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(), Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed").Returns(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Refund: queueing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Refund_PayoutNotSupported_QueuesBuyerLeg_NotRefunded()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());

        var result = await _service.RefundAsync(RefundRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRefund, ManualPayoutFields.ReasonPayoutNotSupported, "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs => legs.Count == 1 && legs[0].Leg == "buyer" && legs[0].Status == "pending"), Arg.Any<string>());
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await AssertNothingSettled();
        await _idempotencyRepo.Received(1).UpdateAsync(RefundRequest().IdempotencyKey, "manual_payout");
        (await AuditWritten("manual_payout_queued", "op=refund;legs=buyer")).Should().BeTrue();
    }

    [Fact]
    public async Task Refund_SuccessWithEmptyRef_Queues()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = null });

        var result = await _service.RefundAsync(RefundRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRefund, ManualPayoutFields.ReasonGatewayRefMissing, "escrowed",
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Refund_QueueWriteFails_ReleasesLockAndReturns502()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.RefundAsync(RefundRequest(), "system");

        result.Error!.HttpStatus.Should().Be(502);
        result.Error.Code.Should().Be("ESCROW_013");
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
        await _idempotencyRepo.Received(1).DeleteAsync(RefundRequest().IdempotencyKey);
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Refund_QueueWriteFailsButCommitted_TreatedAsQueued()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

        var result = await _service.RefundAsync(RefundRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        (await AuditWritten("refund_failed")).Should().BeFalse();
        (await AuditWritten("manual_payout_queued", "op=refund")).Should().BeTrue();
    }

    [Fact]
    public async Task Refund_ManualPayoutOpen_Returns202_NoGatewayNoLockNoAudit()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await _service.RefundAsync(RefundRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Refund_OtherGatewayFailure_KeepsRetryablePath()
    {
        // A definite refusal (ESC-05: a timeout is not one, see the unknown-outcome tests).
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = "TELEBIRR_REFUND_FAILED" });

        var result = await _service.RefundAsync(RefundRequest(), "system");

        result.Error!.Code.Should().Be("ESCROW_013");
        await _escrowRepo.DidNotReceive().TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
    }

    // ──────────────────────────────────────────────
    // ESC-13: money alarm lines. A queued payout (202 ESCROW_034) is not a settlement
    // failure; a failed queue write is, and after a reference-less "success" its outcome
    // is unknown because the gateway may have paid.
    // ──────────────────────────────────────────────

    private void QueueWritesFail()
        => _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(false);

    private void FarmerPaidTruckerGets(ReleaseFundsResponse trucker)
    {
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(trucker);
    }

    [Fact]
    public async Task Alarm_ReleaseQueued_LogsNothing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());

        AssertWaiting(await _service.ReleaseAsync(ReleaseRequest(), "system"));

        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Alarm_ReleaseFarmerPaidTruckerQueued_LogsNothing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        FarmerPaidTruckerGets(NotSupportedRelease());

        AssertWaiting(await _service.ReleaseAsync(ReleaseRequest(), "system"));

        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Alarm_RefundQueued_LogsNothing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());

        AssertWaiting(await _service.RefundAsync(RefundRequest(), "system"));

        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Alarm_QueueWriteThrowsButCommitted_LogsNothing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .ThrowsAsync(new AmazonDynamoDBException("timeout"));

        AssertWaiting(await _service.ReleaseAsync(ReleaseRequest(), "system"));

        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Alarm_AlreadyQueued_Returns202_LogsNothing()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));
        AssertWaiting(await _service.ReleaseAsync(ReleaseRequest(), "system"));

        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        AssertWaiting(await _service.RefundAsync(RefundRequest(), "system"));

        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Alarm_ReleaseQueueWriteFails_LogsFailedWithTheQueueCode()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        QueueWritesFail();

        await _service.ReleaseAsync(ReleaseRequest(), "system");

        // PAYOUT_NOT_SUPPORTED is a definite refusal: nothing moved.
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=failed code=MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED");
    }

    [Fact]
    public async Task Alarm_ReleaseRefMissingAndQueueWriteFails_LogsUnknownOutcome()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: false));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "" });
        QueueWritesFail();

        await _service.ReleaseAsync(ReleaseRequest(), "system");

        // The gateway said it paid: the operator must check the portal before paying by hand.
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING");
    }

    [Fact]
    public async Task Alarm_RefundRefMissingAndQueueWriteFails_LogsUnknownOutcome()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = null });
        QueueWritesFail();

        await _service.RefundAsync(RefundRequest(), "system");

        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=refund_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING");
    }

    [Fact]
    public async Task Alarm_TruckerQueueWriteFails_AfterFarmerPaid_LogsTruckerLegOnly()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        FarmerPaidTruckerGets(NotSupportedRelease());
        QueueWritesFail();

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.IsSuccess.Should().BeTrue();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=trucker_release_failed outcome=failed code=MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED");
    }

    [Fact]
    public async Task Alarm_TruckerRefMissingAndQueueWriteFails_LogsUnknownOutcome()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        FarmerPaidTruckerGets(new ReleaseFundsResponse { Success = true, TelebirrRef = "" });
        QueueWritesFail();

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        // ESC-05: the gateway said it paid the trucker, so the escrow never completes with
        // truckerPayoutPending (which ESC-04 retries): the lock stays and the recovery step parks it.
        result.Error!.HttpStatus.Should().Be(502);
        result.Error.Code.Should().Be("ESCROW_013");
        await AssertNothingSettled();
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=trucker_release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING");
    }

    // ──────────────────────────────────────────────
    // Complete (admin records a leg)
    // ──────────────────────────────────────────────

    private Task<EscrowResult<CompleteManualPayoutResponse>> Complete(string leg, string reference, bool adminGroup = true)
        => _service.CompleteManualPayoutAsync(EscrowId, leg, reference, AdminId, adminGroup);

    [Fact]
    public async Task Complete_FirstReleaseLeg_MarksPaid_EscrowStaysEscrowed()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var result = await Complete("farmer", "TB-REF-0001");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("escrowed");
        result.Value.LegStatus.Should().Be("paid");
        result.Value.RemainingLegs.Should().Equal("trucker");
        await _escrowRepo.Received(1).TryMarkManualLegPaidAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "farmer", "TB-REF-0001", AdminId, Arg.Any<string>(), "trucker");
        await AssertNothingSettled();
        (await AuditWritten("manual_payout_paid", "leg=farmer;amount=16500.00")).Should().BeTrue();
        (await AuditWritten("release")).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_LastReleaseLeg_ClosesReleasedThenCompleted_WithAdminRefs()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));

        var result = await Complete("trucker", "TB-REF-0002");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("completed");
        result.Value.RemainingLegs.Should().BeEmpty();
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "trucker", "TB-REF-0002", AdminId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(others => others.Count == 1 && others[0] == "farmer"),
            "released",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "TB-REF-FARMER"
                && (string)f["truckerTelebirrRef"] == "TB-REF-0002"
                && f.ContainsKey("releasedAt")));
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released",
            Arg.Is<Dictionary<string, object>>(f => f.ContainsKey("completedAt")));
        await _escrowRepo.DidNotReceive().TryMarkManualLegPaidAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        (await AuditWritten("manual_payout_paid", "leg=trucker;amount=3000.00")).Should().BeTrue();
        (await AuditWritten("release", "manual")).Should().BeTrue();
        (await AuditWritten("complete", "manual")).Should().BeTrue();
        // The gateway is never part of a manual completion.
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task Complete_OnlyFarmerLeg_ClosesWithFarmerRefAndNoTruckerRef()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(withTrucker: false, queuedOp: ManualPayoutFields.OpRelease));

        var result = await Complete("farmer", "TB-REF-0001");

        result.Value!.EscrowStatus.Should().Be("completed");
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "farmer", "TB-REF-0001", AdminId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(others => others.Count == 0), "released",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "TB-REF-0001" && !f.ContainsKey("truckerTelebirrRef")));
    }

    [Fact]
    public async Task Complete_BuyerLeg_ClosesRefunded_WithAdminRef()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await Complete("buyer", "TB-REF-0009");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("refunded");
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRefund, "buyer", "TB-REF-0009", AdminId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(others => others.Count == 0), "refunded",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrRefundRef"] == "TB-REF-0009" && f.ContainsKey("refundedAt")));
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("refund", "manual")).Should().BeTrue();
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task Complete_OtherLegPaidConcurrently_RetriesAndCloses()
    {
        // First read: both legs pending, so the mark write runs; it fails because another
        // admin paid the trucker meanwhile. Second read shows that, and the close write runs.
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(
            Escrow(queuedOp: ManualPayoutFields.OpRelease),
            Escrow(queuedOp: ManualPayoutFields.OpRelease, truckerLeg: "paid"));
        _escrowRepo.TryMarkManualLegPaidAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var result = await Complete("farmer", "TB-REF-0001");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("completed");
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "farmer", "TB-REF-0001", AdminId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(others => others.Count == 1 && others[0] == "trucker"), "released",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "TB-REF-0001" && (string)f["truckerTelebirrRef"] == "TB-REF-TRUCKER"));
    }

    [Fact]
    public async Task Complete_WriteKeepsFailing_Returns409AfterThreeAttempts()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _escrowRepo.TryCloseManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>())
            .Returns(false);

        var result = await Complete("buyer", "TB-REF-0009");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_036");
        await _escrowRepo.Received(3).GetByIdAsync(EscrowId, true);
        (await AuditWritten("manual_payout_paid")).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_SameRefReplay_IsIdempotent()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));

        var result = await Complete("farmer", "TB-REF-FARMER");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Message.Should().Be("Already recorded.");
        result.Value.RemainingLegs.Should().Equal("trucker");
        await _escrowRepo.DidNotReceive().TryMarkManualLegPaidAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Complete_SameRefReplay_AfterCrashBetweenReleasedAndCompleted_Finishes()
    {
        var stuck = Escrow(status: "released", queuedOp: null, farmerLeg: "paid");
        stuck[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
        stuck[ManualPayoutFields.StatusAttr("farmer")] = "paid";
        stuck[ManualPayoutFields.RefAttr("farmer")] = "TB-REF-FARMER";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(stuck);

        var result = await Complete("farmer", "TB-REF-FARMER");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("completed");
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released",
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Complete_DifferentRefOnPaidLeg_Returns409Escrow036()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));

        var result = await Complete("farmer", "TB-REF-OTHER");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_036");
    }

    [Fact]
    public async Task Complete_DifferentRefOnPaidLeg_AuditsTheRefusal()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));

        await Complete("farmer", "TB-REF-OTHER");

        (await AuditWritten("manual_payout_refused", "leg=farmer;code=ESCROW_036;attemptedRef=TB-REF-OTHER")).Should().BeTrue();
        (await AuditWritten("manual_payout_paid")).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_RefusalAuditFails_StillAnswersTheRefusal()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));
        _auditRepo.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new AmazonDynamoDBException("down"));

        var result = await Complete("farmer", "TB-REF-OTHER");

        result.Error!.Code.Should().Be("ESCROW_036");
    }

    [Fact]
    public async Task Complete_ReadsTheEscrowStronglyConsistent()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await Complete("buyer", "TB-REF-0009");

        result.IsSuccess.Should().BeTrue();
        await _escrowRepo.Received(1).GetByIdAsync(EscrowId, true);
        await _escrowRepo.DidNotReceive().GetByIdAsync(EscrowId);
    }

    [Fact]
    public async Task Complete_SameRefForFarmerAndTrucker_Returns409Escrow036()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));

        var result = await Complete("trucker", "TB-REF-FARMER");

        result.Error!.Code.Should().Be("ESCROW_036");
        await AssertNothingSettled();
    }

    [Fact]
    public async Task Complete_NoOpenPayout_Returns404Escrow035()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow());

        var result = await Complete("farmer", "TB-REF-0001");

        result.Error!.HttpStatus.Should().Be(404);
        result.Error.Code.Should().Be("ESCROW_035");
    }

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    [InlineData("platform")]
    public async Task Complete_LegNotInOpOrUnknown_Returns404(string leg)
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await Complete(leg, "TB-REF-0001");

        result.Error!.HttpStatus.Should().Be(404);
        result.Error.Code.Should().Be("ESCROW_035");
    }

    [Fact]
    public async Task Complete_EscrowMissing_Returns404Escrow016()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns((Dictionary<string, object>?)null);

        var result = await Complete("farmer", "TB-REF-0001");

        result.Error!.Code.Should().Be("ESCROW_016");
        // No escrow row to hang an audit entry on.
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("<script>")]
    [InlineData("-leading-dash")]
    [InlineData("TB;drop")]
    public async Task Complete_InvalidExternalRef_Returns400Escrow037(string reference)
    {
        var result = await Complete("farmer", reference);

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Code.Should().Be("ESCROW_037");
        result.Error.Field.Should().Be("externalRef");
        await _escrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Complete_ExternalRefTooLong_Returns400Escrow037()
    {
        var result = await Complete("farmer", new string('A', 65));

        result.Error!.Code.Should().Be("ESCROW_037");
    }

    [Fact]
    public async Task Complete_ExternalRefIsTrimmed_AndAllowedPunctuationAccepted()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await Complete("buyer", "  TB.2026_10:06/0001-x  ");

        result.IsSuccess.Should().BeTrue();
        result.Value!.ExternalRef.Should().Be("TB.2026_10:06/0001-x");
    }

    [Fact]
    public async Task Complete_UnverifiedPayee_Returns409Escrow029_NoWrite()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone, phoneVerified: false));

        var result = await Complete("trucker", "TB-REF-0002");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_029");
        await AssertNothingSettled();
        await _escrowRepo.DidNotReceive().TryMarkManualLegPaidAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Complete_FarmerWithoutFaydaAboveThreshold_Returns409Escrow029()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(withTrucker: false, queuedOp: ManualPayoutFields.OpRelease));
        _userRepo.GetUserByIdAsync(FarmerId).Returns(User(FarmerId, "farmer", FarmerPhone, fayda: false));

        var result = await Complete("farmer", "TB-REF-0001");

        result.Error!.Code.Should().Be("ESCROW_029");
        result.Error.Message.Should().Contain("Fayda");
    }

    [Fact]
    public async Task Complete_BuyerWithoutProfile_Returns409Escrow029()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _userRepo.GetUserByIdAsync(BuyerId).Returns((UserDto?)null);

        var result = await Complete("buyer", "TB-REF-0009");

        result.Error!.Code.Should().Be("ESCROW_029");
    }

    [Fact]
    public async Task Complete_NotAdminGroup_Returns403Escrow015()
    {
        _userRepo.GetUserByIdAsync(AdminId).Returns((UserDto?)null);

        var result = await Complete("farmer", "TB-REF-0001", adminGroup: false);

        result.Error!.HttpStatus.Should().Be(403);
        result.Error.Code.Should().Be("ESCROW_015");
        await _escrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Complete_RoleAdminInUsersTableButNoGroup_Returns403Escrow015()
    {
        // The users-table role is not enough for this money action: admin group only.
        _userRepo.GetUserByIdAsync(AdminId).Returns(User(AdminId, "admin", "+251911000009"));

        var result = await Complete("farmer", "TB-REF-0001", adminGroup: false);

        result.Error!.HttpStatus.Should().Be(403);
        result.Error.Code.Should().Be("ESCROW_015");
        await _escrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Complete_EscrowAlreadySettled_Returns409InvalidTransition()
    {
        var item = Escrow(status: "completed", queuedOp: ManualPayoutFields.OpRelease);
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(item);

        var result = await Complete("farmer", "TB-REF-0001");

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
    }

    [Fact]
    public async Task Complete_CloseWriteSucceededButAuditFailed_ReplayFillsTheRowsOnce()
    {
        // First call: the close write lands, then the audit table fails, so the admin sees an
        // error. The replay with the same reference must write the missing rows at the same
        // deterministic keys and report alreadyRecorded.
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _auditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .Returns<bool>(_ => throw new AmazonDynamoDBException("audit down"));

        var first = async () => await Complete("buyer", "TB-REF-0009");
        await first.Should().ThrowAsync<AmazonDynamoDBException>();
        var closeCall = _escrowRepo.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEscrowRepository.TryCloseManualPayoutAsync));
        var paidAt = (string)closeCall.GetArguments()[6]!;

        var closed = Escrow(status: "refunded", queuedOp: ManualPayoutFields.OpRefund, buyerLeg: "paid");
        closed.Remove(ManualPayoutFields.Marker);
        closed[ManualPayoutFields.RefAttr("buyer")] = "TB-REF-0009";
        closed[ManualPayoutFields.PaidAtAttr("buyer")] = paidAt;
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(closed);
        _auditRepo.ClearReceivedCalls();
        _auditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);

        var replay = await Complete("buyer", "TB-REF-0009");

        replay.IsSuccess.Should().BeTrue();
        replay.Value!.AlreadyRecorded.Should().BeTrue();
        replay.Value.EscrowStatus.Should().Be("refunded");
        var entries = AuditEntries();
        entries.Should().ContainSingle(e => (string)e["action"] == "manual_payout_paid"
                                            && (string)e["timestamp"] == paidAt);
        entries.Should().ContainSingle(e => (string)e["action"] == "refund" && (string)e["metadata"] == "manual"
                                            && string.CompareOrdinal((string)e["timestamp"], paidAt) > 0);
        entries.Should().ContainSingle(e => (string)e["action"] == "manual_payout_queued"
                                            && (string)e["timestamp"] == "2026-10-02T09:00:00.0000000Z");
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Complete_CompleteAuditFailsAfterTheStatusWrite_ResponseStillSaysCompleted()
    {
        // The released to completed write landed; only its audit row failed. The answer must
        // match the row (completed), and the error must not surface as a failed payout.
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));
        _auditRepo.AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "complete"))
            .Returns<bool>(_ => throw new AmazonDynamoDBException("audit down"));

        var result = await Complete("trucker", "TB-REF-0002");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("completed");
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released",
            Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("released")]
    public async Task Complete_CompletedWriteRefused_AnswersTheRowsRealStatus(string rowStatus)
    {
        // ESC-04: a refused released to completed write means a parallel completion or (the
        // repository guard) a leg still owed; the answer follows the row, never assumes completed.
        var after = Escrow(status: rowStatus, queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid");
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"), after);
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("guarded"));

        var result = await Complete("trucker", "TB-REF-0002");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be(rowStatus);
    }

    [Fact]
    public async Task Complete_ReplayOnACompletedEscrow_ReEnsuresTheCompleteRowAtCompletedAt()
    {
        var completed = Escrow(status: "completed", queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid", truckerLeg: "paid");
        completed.Remove(ManualPayoutFields.Marker);
        completed["completedAt"] = "2026-10-02T10:06:00.0000000Z";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(completed);

        var result = await Complete("trucker", "TB-REF-TRUCKER");

        result.Value!.AlreadyRecorded.Should().BeTrue();
        result.Value.EscrowStatus.Should().Be("completed");
        var complete = AuditEntries().Single(e => (string)e["action"] == "complete");
        ((string)complete["timestamp"]).Should().Be("2026-10-02T10:06:00.0000000Z");
        ((string)complete["auditId"]).Should().Be(EscrowAuditRepository.DeterministicAuditId("complete", "2026-10-02T10:06:00.0000000Z"));
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Complete_PaidAuditUsesTheLegPaidAtAsItsKey()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var result = await Complete("farmer", "TB-REF-0001");

        result.Value!.AlreadyRecorded.Should().BeFalse();
        var paid = AuditEntries().Single(e => (string)e["action"] == "manual_payout_paid");
        ((string)paid["timestamp"]).Should().Be(result.Value.PaidAt);
    }

    [Fact]
    public async Task Release_QueuedAudit_IsKeyedOnTheQueuedAtWrittenToTheRow()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());

        await _service.ReleaseAsync(ReleaseRequest(), "system");

        var queueCall = _escrowRepo.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEscrowRepository.TryQueueManualPayoutAsync));
        var queuedAt = (string)queueCall.GetArguments()[6]!;
        var queuedAudit = AuditEntries().Single(e => (string)e["action"] == "manual_payout_queued");
        ((string)queuedAudit["timestamp"]).Should().Be(queuedAt);
        await _auditRepo.Received(1).AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ──────────────────────────────────────────────
    // Disputes on a queued escrow
    // ──────────────────────────────────────────────

    private static DisputeEscrowRequest DisputeRequest() => new()
    {
        EscrowId = EscrowId,
        DisputedBy = BuyerId,
        Reason = "not_delivered",
        IdempotencyKey = $"{BuyerId}:escrow:{EscrowId}:1791500000"
    };

    [Fact]
    public async Task Dispute_QueuedEscrow_Returns409Escrow038_NoWrite()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRefund));

        var result = await _service.DisputeAsync(DisputeRequest(), BuyerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_038");
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _idempotencyRepo.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Dispute_QueuedBetweenReadAndWrite_Returns409Escrow038_AndFreesTheKey()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("queued"));

        var result = await _service.DisputeAsync(DisputeRequest(), BuyerId);

        result.Error!.Code.Should().Be("ESCROW_038");
        await _idempotencyRepo.Received(1).DeleteAsync(DisputeRequest().IdempotencyKey);
        (await AuditWritten("dispute")).Should().BeFalse();
    }

    [Fact]
    public async Task Dispute_SettlementInFlight_Returns409_AndFreesTheKey()
    {
        // ESC-03: the disputed write requires no settlement lock, so a release in flight wins
        // and the escrow is never left disputed behind a held lock.
        var locked = Escrow();
        locked["settlementLock"] = "release";
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(locked);
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));

        var result = await _service.DisputeAsync(DisputeRequest(), BuyerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Contain("settlement is in progress");
        await _idempotencyRepo.Received(1).DeleteAsync(DisputeRequest().IdempotencyKey);
        (await AuditWritten("dispute")).Should().BeFalse();
    }

    [Fact]
    public async Task DeliveryDispute_QueuedEscrow_IsNotFrozen_AndTheRefusalIsAudited()
    {
        _escrowRepo.GetByDeliveryIdAsync("del-1").Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var frozen = await _service.MarkDisputedFromDeliveryAsync("del-1", BuyerId, "never arrived");

        frozen.Should().Be(DeliveryDisputeFreeze.RefusedManualPayout);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("dispute_refused_manual_payout", "deliveryId=del-1;op=release")).Should().BeTrue();
    }

    [Fact]
    public async Task DeliveryDispute_StaleLockOnTheIndexCopy_DoesNotRefuseAValidDispute()
    {
        // ESC-03: the DeliveryIndex copy can be stale. Only the write's lock condition decides,
        // so a lock left on the copy (released on the table since) never refuses the dispute.
        var staleCopy = Escrow();
        staleCopy["settlementLock"] = "refund";
        _escrowRepo.GetByDeliveryIdAsync("del-1").Returns(staleCopy);

        var frozen = await _service.MarkDisputedFromDeliveryAsync("del-1", BuyerId, "never arrived");

        frozen.Should().Be(DeliveryDisputeFreeze.Frozen);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, CreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("dispute_refused_settlement_in_flight")).Should().BeFalse();
    }

    [Fact]
    public async Task DeliveryDispute_LockTakenBetweenReadAndWrite_IsNotFrozen()
    {
        var locked = Escrow();
        locked["settlementLock"] = "release";
        _escrowRepo.GetByDeliveryIdAsync("del-1").Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(locked);
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));

        var frozen = await _service.MarkDisputedFromDeliveryAsync("del-1", BuyerId, "never arrived");

        frozen.Should().Be(DeliveryDisputeFreeze.RefusedSettlementInFlight);
        (await AuditWritten("dispute_refused_settlement_in_flight", "deliveryId=del-1")).Should().BeTrue();
        (await AuditWritten("dispute")).Should().BeFalse();
    }

    // ──────────────────────────────────────────────
    // ESC-04: a trucker leg queued on a released escrow
    // ──────────────────────────────────────────────

    private const string FarmerGatewayRef = "GW-FARMER-1";
    private const string ReleasedAt = "2026-10-02T08:00:00.0000000Z";
    private const string TruckerLockAt = "2026-10-02T08:30:00.0000000Z";

    /// <summary>The farmer was paid by the release; the trucker retry queued the trucker leg.</summary>
    private static Dictionary<string, object> ReleasedTruckerQueue(bool withTruckerId = true)
    {
        var item = Escrow(status: "released", queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid");
        item["telebirrReleaseRef"] = FarmerGatewayRef;
        item["releasedAt"] = ReleasedAt;
        item[ManualPayoutFields.RefAttr("farmer")] = FarmerGatewayRef;
        item[ManualPayoutFields.PaidByAttr("farmer")] = ManualPayoutFields.PaidByGateway;
        item[ManualPayoutFields.PaidAtAttr("farmer")] = ReleasedAt;
        item[TruckerPayoutFields.Pending] = true;
        item[TruckerPayoutFields.LockId] = "trucker-lock-1";
        item[TruckerPayoutFields.LockAt] = TruckerLockAt;
        if (!withTruckerId)
        {
            item.Remove("truckerId");
        }

        return item;
    }

    private (EscrowService Service, IOrderRepository Orders, IDeliveryRepository Deliveries) ServiceWithOrderAndDelivery()
    {
        var orders = Substitute.For<IOrderRepository>();
        var deliveries = Substitute.For<IDeliveryRepository>();
        return (new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _userRepo, orders, deliveries), orders, deliveries);
    }

    [Fact]
    public async Task Complete_TruckerLegOfAReleasedEscrow_ClosesStraightToCompleted_AndKeepsTheRelease()
    {
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(ReleasedTruckerQueue());
        Dictionary<string, object>? fields = null;
        _escrowRepo.TryCloseManualPayoutAsync(EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "trucker", "TB-REF-0002",
                AdminId, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), "completed",
                Arg.Do<Dictionary<string, object>>(f => fields = f))
            .Returns(true);

        var result = await Complete("trucker", "TB-REF-0002");

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("completed");
        result.Value.RemainingLegs.Should().BeEmpty();
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "trucker", "TB-REF-0002", AdminId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(others => others.Count == 1 && others[0] == "farmer"), "completed",
            Arg.Any<Dictionary<string, object>>());
        // R3: the release already happened; its time and reference stay as the release wrote them.
        fields!.Should().ContainKey("truckerTelebirrRef").WhoseValue.Should().Be("TB-REF-0002");
        fields.Should().ContainKey("truckerPaidAt");
        fields.Should().NotContainKey("releasedAt").And.NotContainKey("telebirrReleaseRef");
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("manual_payout_paid", "leg=trucker;amount=3000.00")).Should().BeTrue();
        (await AuditWritten("release")).Should().BeFalse();
        var complete = AuditEntries().Single(e => (string)e["action"] == "complete");
        complete["timestamp"].Should().Be(fields!["completedAt"]);
        complete["metadata"].Should().Be("manual");
        complete["fromState"].Should().Be("released");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task Complete_ReplayOnAClosedReleasedTruckerQueue_ReEnsuresTheCompleteRow_NeverARelease()
    {
        var closed = ReleasedTruckerQueue();
        closed["status"] = "completed";
        closed.Remove(ManualPayoutFields.Marker);
        closed[ManualPayoutFields.StatusAttr("trucker")] = ManualPayoutFields.LegPaid;
        closed[ManualPayoutFields.RefAttr("trucker")] = "TB-REF-0002";
        closed[ManualPayoutFields.PaidByAttr("trucker")] = AdminId;
        closed[ManualPayoutFields.PaidAtAttr("trucker")] = "2026-10-02T11:00:00.0000000Z";
        closed["completedAt"] = "2026-10-02T11:00:00.0000001Z";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(closed);

        var result = await Complete("trucker", "TB-REF-0002");

        result.Value!.AlreadyRecorded.Should().BeTrue();
        result.Value.EscrowStatus.Should().Be("completed");
        (await AuditWritten("release")).Should().BeFalse();
        AuditEntries().Should().Contain(e => (string)e["action"] == "complete"
                                             && (string)e["timestamp"] == "2026-10-02T11:00:00.0000001Z");
        AuditEntries().Should().Contain(e => (string)e["action"] == "manual_payout_paid"
                                             && (string)e["fromState"] == "released");
    }

    [Fact]
    public async Task Complete_TruckerLegWithoutTruckerId_TakesTheOrdersTrucker_ThenRecords()
    {
        var (service, orders, deliveries) = ServiceWithOrderAndDelivery();
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(ReleasedTruckerQueue(withTruckerId: false));
        orders.GetByIdAsync("ord-1").Returns(new Dictionary<string, object> { ["orderId"] = "ord-1", ["truckerId"] = TruckerId });
        deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-1", ["truckerId"] = TruckerId });
        _escrowRepo.TryBackfillTruckerIdAsync(EscrowId, CreatedAt, TruckerId).Returns(true);

        var result = await service.CompleteManualPayoutAsync(EscrowId, "trucker", "TB-REF-0002", AdminId, adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        await _escrowRepo.Received(1).TryBackfillTruckerIdAsync(EscrowId, CreatedAt, TruckerId);
        (await AuditWritten("trucker_backfilled", $"truckerId={TruckerId}")).Should().BeTrue();
        (await AuditWritten("manual_payout_paid", $"payee={TruckerId}")).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_TruckerLegWithoutTruckerId_AndADeliveryForAnotherTrucker_Returns409Escrow029()
    {
        var (service, orders, deliveries) = ServiceWithOrderAndDelivery();
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(ReleasedTruckerQueue(withTruckerId: false));
        orders.GetByIdAsync("ord-1").Returns(new Dictionary<string, object> { ["orderId"] = "ord-1", ["truckerId"] = TruckerId });
        deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-1", ["truckerId"] = "trucker-other" });

        var result = await service.CompleteManualPayoutAsync(EscrowId, "trucker", "TB-REF-0002", AdminId, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_029");
        result.Error.Message.Should().Contain("does not match");
        await _escrowRepo.DidNotReceive().TryBackfillTruckerIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await AssertNothingSettled();
    }

    [Fact]
    public async Task List_ReleasedTruckerQueue_ShowsTheTruckerCallTime_AndTheOrdersTruckerForAMissingTruckerId()
    {
        var (service, orders, deliveries) = ServiceWithOrderAndDelivery();
        var row = ReleasedTruckerQueue(withTruckerId: false);
        row[SettlementLockFields.LockAt] = "2026-10-01T12:00:00.0000000Z";
        _escrowRepo.QueryOpenManualPayoutsAsync(20, null).Returns((new List<Dictionary<string, object>> { row }, null));
        orders.GetByIdAsync("ord-1").Returns(new Dictionary<string, object> { ["orderId"] = "ord-1", ["truckerId"] = TruckerId });
        deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-1", ["truckerId"] = TruckerId });

        var payout = (await service.ListManualPayoutsAsync(20, null)).Value!.Payouts.Single();

        payout.EscrowStatus.Should().Be("released");
        payout.FromStatus.Should().Be("released");
        payout.SettlementLockedAt.Should().Be(TruckerLockAt);
        payout.PendingAmount.Should().Be(3_000m);
        var trucker = payout.Legs.Single(l => l.Ref == "trucker");
        trucker.PayeeId.Should().Be(TruckerId);
        trucker.PayeePhone.Should().Be(TruckerPhone);
        trucker.PayeeVerified.Should().BeTrue();
        // Listing writes nothing; recording the leg keeps the truckerId on the escrow.
        await _escrowRepo.DidNotReceive().TryBackfillTruckerIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task List_TruckerLegWithoutTruckerId_AndADeliveryForAnotherTrucker_ShowsItBlocked()
    {
        var (service, orders, deliveries) = ServiceWithOrderAndDelivery();
        _escrowRepo.QueryOpenManualPayoutsAsync(20, null)
            .Returns((new List<Dictionary<string, object>> { ReleasedTruckerQueue(withTruckerId: false) }, null));
        orders.GetByIdAsync("ord-1").Returns(new Dictionary<string, object> { ["orderId"] = "ord-1", ["truckerId"] = TruckerId });
        deliveries.GetByIdAsync("del-1").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-1", ["truckerId"] = "trucker-other" });

        var trucker = (await service.ListManualPayoutsAsync(20, null)).Value!.Payouts.Single().Legs.Single(l => l.Ref == "trucker");

        trucker.PayeeVerified.Should().BeFalse();
        trucker.PayeeId.Should().BeEmpty();
        trucker.BlockReason.Should().Contain("does not match");
    }

    // ──────────────────────────────────────────────
    // List
    // ──────────────────────────────────────────────

    [Fact]
    public async Task List_MapsLegsAmountsPayeesAndVerification()
    {
        var release = Escrow(queuedOp: ManualPayoutFields.OpRelease);
        var refund = Escrow(status: "disputed", withTrucker: false, queuedOp: ManualPayoutFields.OpRefund);
        refund["escrowId"] = "escrow-order-ord-2";
        refund["orderId"] = "ord-2";
        refund[ManualPayoutFields.FromStatus] = "escrowed";
        _escrowRepo.QueryOpenManualPayoutsAsync(20, null).Returns((new List<Dictionary<string, object>> { release, refund }, null));
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone, phoneVerified: false));

        var result = await _service.ListManualPayoutsAsync(20, null);

        result.IsSuccess.Should().BeTrue();
        var payouts = result.Value!.Payouts;
        payouts.Should().HaveCount(2);
        result.Value.NextToken.Should().BeNull();

        var first = payouts[0];
        first.EscrowId.Should().Be(EscrowId);
        first.Op.Should().Be("release");
        first.Reason.Should().Be("PAYOUT_NOT_SUPPORTED");
        first.PendingAmount.Should().Be(19_500m);
        first.Legs.Should().HaveCount(2);
        first.Legs[0].Ref.Should().Be("farmer");
        first.Legs[0].Amount.Should().Be(16_500m);
        first.Legs[0].PayeePhone.Should().Be(FarmerPhone);
        first.Legs[0].PayeeName.Should().Be("farmer name");
        first.Legs[0].PayeeVerified.Should().BeTrue();
        first.Legs[1].Ref.Should().Be("trucker");
        first.Legs[1].Amount.Should().Be(3_000m);
        first.Legs[1].PayeeVerified.Should().BeFalse();
        first.Legs[1].BlockReason.Should().Contain("phone");

        var second = payouts[1];
        second.Op.Should().Be("refund");
        second.EscrowStatus.Should().Be("disputed");
        second.FromStatus.Should().Be("escrowed");
        second.Legs.Should().ContainSingle();
        second.Legs[0].Ref.Should().Be("buyer");
        second.Legs[0].Amount.Should().Be(20_000m);
        second.Legs[0].PayeePhone.Should().Be(BuyerPhone);
        second.PendingAmount.Should().Be(20_000m);

        // One profile read per distinct payee, not per leg per row.
        await _userRepo.Received(1).GetUserByIdAsync(FarmerId);
        await _userRepo.Received(1).GetUserByIdAsync(BuyerId);
    }

    [Fact]
    public async Task List_InvalidToken_Returns400()
    {
        var result = await _service.ListManualPayoutsAsync(20, "not-a-token");

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Code.Should().Be("ESCROW_023");
    }

    // ──────────────────────────────────────────────
    // Done-when: the real live gateway never settles anything by itself
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NoEscrowSettlesWithoutReference_LiveGateway()
    {
        var liveGateway = new TelebirrSandboxGateway(
            new HttpClient(new RefusingHandler()),
            Substitute.For<ITelebirrSignatureService>(),
            new TelebirrConfig
            {
                FabricAppId = "f",
                AppSecret = "s",
                MerchantAppId = "m",
                MerchantCode = "220100",
                ShortCode = "220100",
                BaseUrl = "http://telebirr.local",
                WebUrl = "http://telebirr.local/pay",
                MerchantPrivateKeyPem = "k",
                TelebirrPublicKeyPem = "k",
                ReturnUrl = "wc://done",
                NotifyUrl = "http://server.local/webhooks/telebirr",
            });
        var service = new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, liveGateway, _userRepo);

        // Release with and without a trucker leg, and a refund, each from every settleable status
        // (a disputed escrow settles only through a recorded verdict since ESC-03).
        foreach (var status in new[] { "escrowed", "dispute_resolved" })
        {
            _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(status: status));
            AssertWaiting(await service.ReleaseAsync(ReleaseRequest($"system:{EscrowId}:r:{status.Length}1"), "system"));
            _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(status: status, withTrucker: false));
            AssertWaiting(await service.ReleaseAsync(ReleaseRequest($"system:{EscrowId}:r:{status.Length}2"), "system"));
            _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(status: status));
            AssertWaiting(await service.RefundAsync(RefundRequest($"system:{EscrowId}:f:{status.Length}3"), "system"));
        }

        await AssertNothingSettled();
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _escrowRepo.Received(6).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());

        // Only the admin's references settle the queued escrow.
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease, farmerLeg: "paid"));
        var done = await service.CompleteManualPayoutAsync(EscrowId, "trucker", "TB-REF-0002", AdminId, true);
        done.IsSuccess.Should().BeTrue();
        done.Value!.EscrowStatus.Should().Be("completed");
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "trucker", "TB-REF-0002", AdminId, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), "released",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "TB-REF-FARMER" && (string)f["truckerTelebirrRef"] == "TB-REF-0002"));
    }

    /// <summary>Any HTTP call would prove the gateway tried a payout; none may happen.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException($"unexpected HTTP call to {request.RequestUri}");
    }
}
