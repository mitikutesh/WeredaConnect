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
    private readonly EscrowService _service;

    public EscrowServiceManualPayoutTests()
    {
        _escrowRepo.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
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

        _service = new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _userRepo);
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
    public async Task Release_TruckerQueueWriteFails_AfterFarmerPaid_FallsBackToTruckerPayoutPending()
    {
        // The farmer leg is paid, so the escrow must still advance (today's behaviour)
        // rather than strand under a held lock. The owed trucker leg is flagged.
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
        result.Value!.Status.Should().Be("completed");
        result.Value.TelebirrRef.Should().Be("GW-FARMER-1");
        await _escrowRepo.Received(4).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["telebirrReleaseRef"] == "GW-FARMER-1" && (bool)f["truckerPayoutPending"]));
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        (await AuditWritten("trucker_release_failed", "MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED")).Should().BeTrue();
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
    public async Task Release_UnverifiedTrucker_KeepsTruckerPayoutPendingBehaviour()
    {
        // ESC-04 owns the KYC-blocked trucker leg: no gateway call, no queue entry.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", TruckerPhone, phoneVerified: false));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-1" });

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("completed");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (bool)f["truckerPayoutPending"]));
    }

    [Fact]
    public async Task Release_ManualPayoutOpen_Returns202_NoGatewayNoLockNoAudit()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var result = await _service.ReleaseAsync(ReleaseRequest(), "system");

        AssertWaiting(result);
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _idempotencyRepo.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Release_LockLostToQueuedSettlement_Returns202_NoGateway()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(), Escrow(queuedOp: ManualPayoutFields.OpRefund));
        _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release").Returns(false);

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
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Refund_OtherGatewayFailure_KeepsRetryablePath()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = "TELEBIRR_TIMEOUT" });

        var result = await _service.RefundAsync(RefundRequest(), "system");

        result.Error!.Code.Should().Be("ESCROW_013");
        await _escrowRepo.DidNotReceive().TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
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
    public async Task DeliveryDispute_QueuedEscrow_IsNotFrozen_AndTheRefusalIsAudited()
    {
        _escrowRepo.GetByDeliveryIdAsync("del-1").Returns(Escrow(queuedOp: ManualPayoutFields.OpRelease));

        var frozen = await _service.MarkDisputedFromDeliveryAsync("del-1", BuyerId, "never arrived");

        frozen.Should().Be(DeliveryDisputeFreeze.RefusedManualPayout);
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        (await AuditWritten("dispute_refused_manual_payout", "deliveryId=del-1;op=release")).Should().BeTrue();
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

        // Release with and without a trucker leg, and a refund, each from every settleable status.
        foreach (var status in new[] { "escrowed", "disputed", "dispute_resolved" })
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
        await _escrowRepo.Received(9).TryQueueManualPayoutAsync(
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
