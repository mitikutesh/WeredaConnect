using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-05 (escrow-protocol.md 2.12.0): an admin claims a manual payout leg before paying it in the portal, so a
/// second admin is refused while the claim lives (it ends on its own after 30 minutes, when its holder releases it,
/// or when the leg is recorded), and one portal reference records one leg. Both settlement models: 1.x legs on the
/// escrow row and ledger rows. The DynamoDB conditions themselves are covered by EscrowRepositoryManualPayoutTests
/// and, against real DynamoDB, by ManualPayoutClaimDynamoDbTests.
/// </summary>
public class EscrowServiceManualPayoutClaimTests
{
    private const string EscrowId = "escrow-order-ord-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string BuyerId = "buyer-1";
    private const string AdminA = "admin-a";
    private const string AdminB = "admin-b";
    private const string ReferenceTable = "escrow-idempotency-test";

    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private const string NowIso = "2026-11-12T10:00:00.0000000Z";
    private const string ExpiresIso = "2026-11-12T10:30:00.0000000Z";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly FakeClaims _claims = new();
    private readonly List<Dictionary<string, object>> _audits = new();
    private Dictionary<string, object> _escrow = QueuedRefund();

    public EscrowServiceManualPayoutClaimTests()
    {
        _escrowRepo.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<string>(0) == EscrowId ? new Dictionary<string, object>(_escrow) : null);
        _escrowRepo.GetByIdAsync(Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(0) == EscrowId ? new Dictionary<string, object>(_escrow) : null);
        _escrowRepo.TryCloseManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<PayoutReferenceRecord?>())
            .Returns(true);
        _escrowRepo.TryMarkManualLegPaidAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<PayoutReferenceRecord?>())
            .Returns(true);
        _auditRepo.AppendAsync(Arg.Do<Dictionary<string, object>>(_audits.Add)).Returns(Task.CompletedTask);
        _auditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        _auditRepo.GetAuditTrailAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(new List<Dictionary<string, object>>());
        _userRepo.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => User(ci.ArgAt<string>(0)));
        _claims.Escrow = () => _escrow;
    }

    private EscrowService Service(InMemoryLedger? ledger = null) => new(
        ledger?.EscrowRepo ?? _escrowRepo, _auditRepo, idempotencyRepo: null, telebirrGateway: null, userRepo: _userRepo)
    {
        ManualPayoutClaims = _claims,
        PayoutReferenceTable = ReferenceTable,
        ClaimClock = () => Now,
        LedgerRepo = ledger,
        LedgerClock = () => Now
    };

    private static UserDto User(string id) => new()
    {
        UserId = id,
        PhoneNumber = "+251911000001",
        DisplayName = id,
        Role = id.Split('-')[0],
        Woreda = "w",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaId = "FAYDA-" + id,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    /// <summary>A refund of 20,000 ETB waiting in the 1.x queue: one buyer leg, pending.</summary>
    private static Dictionary<string, object> QueuedRefund() => new()
    {
        ["escrowId"] = EscrowId,
        ["orderId"] = "ord-1",
        ["buyerId"] = BuyerId,
        ["farmerId"] = FarmerId,
        ["amount"] = 20_000m,
        ["farmerPayout"] = 19_500m,
        ["commission"] = 500m,
        ["currency"] = "ETB",
        ["status"] = "escrowed",
        ["createdAt"] = CreatedAt,
        ["version"] = 3,
        ["settlementLock"] = "refund",
        [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
        [ManualPayoutFields.QueuedAt] = "2026-11-12T08:00:00.0000000Z",
        [ManualPayoutFields.Op] = ManualPayoutFields.OpRefund,
        [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported,
        [ManualPayoutFields.FromStatus] = "escrowed",
        [ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending
    };

    private void ClaimedBy(string admin, string expiresAt, string leg = "buyer")
    {
        _escrow[ManualPayoutClaimFields.ClaimedByAttr(leg)] = admin;
        _escrow[ManualPayoutClaimFields.ClaimedAtAttr(leg)] = "2026-11-12T09:50:00.0000000Z";
        _escrow[ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)] = expiresAt;
    }

    private List<string> AuditActions => _audits.Select(a => (string)a["action"]).ToList();

    private string AuditMetadata(string action) => (string)_audits.Single(a => (string)a["action"] == action)["metadata"];

    private Task RecordNothing() => _escrowRepo.DidNotReceive().TryCloseManualPayoutAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
        Arg.Any<Dictionary<string, object>>(), Arg.Any<PayoutReferenceRecord?>());

    // ── 1.x legs: claim and release ─────────────────────────────────────────

    [Fact]
    public async Task Claim_APendingLeg_WritesTheClaimForThirtyMinutes_AndAuditsIt()
    {
        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Claimed.Should().BeTrue();
        result.Value.Renewed.Should().BeFalse();
        result.Value.ClaimedBy.Should().Be(AdminA);
        result.Value.ClaimedAt.Should().Be(NowIso);
        result.Value.ClaimExpiresAt.Should().Be(ExpiresIso);
        result.Value.OrderId.Should().Be("ord-1");
        _claims.Writes.Should().Equal($"claim:{EscrowId}:buyer:{AdminA}:{ExpiresIso}");
        AuditMetadata("manual_payout_claimed").Should().Be($"leg=buyer;expiresAt={ExpiresIso};renewed=false");
    }

    [Fact]
    public async Task Claim_AgainByTheHolder_RenewsIt()
    {
        ClaimedBy(AdminA, "2026-11-12T10:10:00.0000000Z");

        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);

        result.Value!.Renewed.Should().BeTrue();
        result.Value.ClaimExpiresAt.Should().Be(ExpiresIso);
        AuditMetadata("manual_payout_claimed").Should().EndWith("renewed=true");
    }

    [Fact]
    public async Task Claim_ALiveClaimOfAnotherAdmin_IsRefused_AndNeverTakenOver()
    {
        ClaimedBy(AdminA, "2026-11-12T10:10:00.0000000Z");

        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminB, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_046");
        result.Error.Message.Should().Contain(AdminA).And.Contain("2026-11-12T10:10:00.0000000Z");
        _claims.Writes.Should().BeEmpty();
        _audits.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_AnExpiredClaimOfAnotherAdmin_CanBeTaken()
    {
        ClaimedBy(AdminA, "2026-11-12T09:59:59.0000000Z");

        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminB, adminGroupVerified: true);

        result.Value!.ClaimedBy.Should().Be(AdminB);
        result.Value.Renewed.Should().BeFalse();
        _escrow[ManualPayoutClaimFields.ClaimedByAttr("buyer")].Should().Be(AdminB);
    }

    [Fact]
    public async Task Claim_LostRace_ReadsAgain_AndNamesTheAdminWhoWon()
    {
        // Admin A's claim lands between admin B's read and write: B's condition fails and B is told who holds it.
        _claims.BeforeNextWrite = () => ClaimedBy(AdminA, ExpiresIso);

        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminB, adminGroupVerified: true);

        result.Error!.Code.Should().Be("ESCROW_046");
        result.Error.Message.Should().Contain(AdminA);
        _escrow[ManualPayoutClaimFields.ClaimedByAttr("buyer")].Should().Be(AdminA);
    }

    [Fact]
    public async Task Claim_WithoutTheAdminGroup_IsRefused()
    {
        var result = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminA, adminGroupVerified: false);

        result.Error!.HttpStatus.Should().Be(403);
        result.Error.Code.Should().Be("ESCROW_015");
        _claims.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_ARecordedLeg_OrNoQueue_IsRefused()
    {
        _escrow[ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPaid;
        var recorded = await Service().ClaimManualPayoutAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);
        var otherOp = await Service().ClaimManualPayoutAsync(EscrowId, "farmer", AdminA, adminGroupVerified: true);
        var missing = await Service().ClaimManualPayoutAsync("escrow-none", "buyer", AdminA, adminGroupVerified: true);

        recorded.Error!.Code.Should().Be("ESCROW_035");
        recorded.Error.HttpStatus.Should().Be(409);
        otherOp.Error!.Code.Should().Be("ESCROW_035");
        otherOp.Error.HttpStatus.Should().Be(404);
        missing.Error!.Code.Should().Be("ESCROW_016");
        _claims.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_WithoutTheClaimRepository_Is503()
    {
        var service = new EscrowService(_escrowRepo, _auditRepo, null, null, _userRepo);

        var result = await service.ClaimManualPayoutAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(503);
        result.Error.Code.Should().Be("ADMIN_004");
    }

    [Fact]
    public async Task Release_ByTheHolder_RemovesTheClaim_AndAuditsIt()
    {
        ClaimedBy(AdminA, ExpiresIso);

        var result = await Service().ReleaseManualPayoutClaimAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);

        result.Value!.Released.Should().BeTrue();
        result.Value.Claimed.Should().BeFalse();
        _escrow.Should().NotContainKey(ManualPayoutClaimFields.ClaimedByAttr("buyer"));
        AuditMetadata("manual_payout_claim_released").Should().Be("leg=buyer");
    }

    [Fact]
    public async Task Release_ALiveClaimOfAnotherAdmin_IsRefused()
    {
        ClaimedBy(AdminA, ExpiresIso);

        var result = await Service().ReleaseManualPayoutClaimAsync(EscrowId, "buyer", AdminB, adminGroupVerified: true);

        result.Error!.Code.Should().Be("ESCROW_046");
        _escrow[ManualPayoutClaimFields.ClaimedByAttr("buyer")].Should().Be(AdminA);
    }

    [Fact]
    public async Task Release_WithNoClaimOfTheCaller_ReleasesNothing()
    {
        var none = await Service().ReleaseManualPayoutClaimAsync(EscrowId, "buyer", AdminA, adminGroupVerified: true);
        ClaimedBy(AdminA, "2026-11-12T09:00:00.0000000Z");
        var expiredOfOther = await Service().ReleaseManualPayoutClaimAsync(EscrowId, "buyer", AdminB, adminGroupVerified: true);

        none.Value!.Released.Should().BeFalse();
        expiredOfOther.Value!.Released.Should().BeFalse();
        _claims.Writes.Should().BeEmpty();
        _audits.Should().BeEmpty();
    }

    // ── 1.x legs: recording ─────────────────────────────────────────────────

    [Fact]
    public async Task Complete_ALegAnotherAdminClaimed_IsRefused_NothingRecorded_AndAudited()
    {
        ClaimedBy(AdminA, ExpiresIso);

        var result = await Service().CompleteManualPayoutAsync(EscrowId, "buyer", "TB-REF-0001", AdminB, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_046");
        await RecordNothing();
        AuditMetadata("manual_payout_refused").Should().Be("leg=buyer;code=ESCROW_046;attemptedRef=TB-REF-0001");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(AdminA, ExpiresIso)]
    [InlineData(AdminB, "2026-11-12T09:59:00.0000000Z")]
    public async Task Complete_AnUnclaimedLeg_AnOwnClaim_OrAnExpiredClaim_RecordsWithTheReferenceItem(
        string? holder, string? expiresAt)
    {
        if (holder != null)
        {
            ClaimedBy(holder, expiresAt!);
        }

        var result = await Service().CompleteManualPayoutAsync(EscrowId, "buyer", " TB-REF-0001 ", AdminA, adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        result.Value!.EscrowStatus.Should().Be("refunded");
        // The record and the reference item are one write; the record's own condition takes the claim.
        await _escrowRepo.Received(1).TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, "refund", "buyer", "TB-REF-0001", AdminA, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), "refunded", Arg.Any<Dictionary<string, object>>(),
            Arg.Is<PayoutReferenceRecord?>(r => r != null && r.Table == ReferenceTable && r.ExternalRef == "TB-REF-0001"
                                                && r.EscrowId == EscrowId && r.Ref == "buyer" && r.RecordedBy == AdminA));
    }

    [Fact]
    public async Task Complete_AReferenceRecordedOnAnotherLeg_IsRefused_NamingWhere_AndAudited()
    {
        _claims.References[PayoutReferenceFields.Key("TB-REF-0001")] =
            new PayoutReferenceHolder("escrow-order-ord-9", "farmer", AdminB, "2026-11-11T08:00:00.0000000Z");
        _escrowRepo.TryCloseManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<PayoutReferenceRecord?>())
            .ThrowsAsync(new PayoutReferenceTakenException("TB-REF-0001"));

        var result = await Service().CompleteManualPayoutAsync(EscrowId, "buyer", "TB-REF-0001", AdminA, adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_047");
        result.Error.Field.Should().Be("externalRef");
        result.Error.Message.Should().Be("This reference is already recorded on escrow escrow-order-ord-9, leg farmer.");
        AuditMetadata("manual_payout_refused").Should()
            .Be("leg=buyer;code=ESCROW_047;attemptedRef=TB-REF-0001;usedOn=escrow-order-ord-9/farmer");
        AuditActions.Should().NotContain("refund");
    }

    [Fact]
    public async Task Complete_AReferenceThisVeryLegTookInBetween_IsAnsweredAsTheReplay()
    {
        // Another tab of the same admin recorded the leg with this reference between the read and the write.
        _claims.References[PayoutReferenceFields.Key("TB-REF-0001")] = new PayoutReferenceHolder(EscrowId, "buyer", AdminA, NowIso);
        _escrowRepo.TryCloseManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<PayoutReferenceRecord?>())
            .Returns<bool>(_ =>
            {
                _escrow.Remove(ManualPayoutFields.Marker);
                _escrow["status"] = "refunded";
                _escrow[ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPaid;
                _escrow[ManualPayoutFields.RefAttr("buyer")] = "TB-REF-0001";
                _escrow[ManualPayoutFields.PaidByAttr("buyer")] = AdminA;
                _escrow[ManualPayoutFields.PaidAtAttr("buyer")] = NowIso;
                throw new PayoutReferenceTakenException("TB-REF-0001");
            });

        var result = await Service().CompleteManualPayoutAsync(EscrowId, "buyer", "TB-REF-0001", AdminA, adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AlreadyRecorded.Should().BeTrue();
        result.Value.EscrowStatus.Should().Be("refunded");
    }

    [Fact]
    public async Task List_ShowsALiveClaim_WithWhetherTheViewerHoldsIt_AndHidesAnExpiredOne()
    {
        ClaimedBy(AdminA, ExpiresIso);
        _escrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?>())
            .Returns(_ => (new List<Dictionary<string, object>> { new(_escrow) }, (Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?)null));

        var asHolder = (await Service().ListManualPayoutsAsync(20, null, AdminA)).Value!.Payouts.Single().Legs.Single();
        var asOther = (await Service().ListManualPayoutsAsync(20, null, AdminB)).Value!.Payouts.Single().Legs.Single();
        ClaimedBy(AdminA, "2026-11-12T09:00:00.0000000Z");
        var expired = (await Service().ListManualPayoutsAsync(20, null, AdminB)).Value!.Payouts.Single().Legs.Single();

        asHolder.ClaimedBy.Should().Be(AdminA);
        asHolder.ClaimExpiresAt.Should().Be(ExpiresIso);
        asHolder.ClaimedByMe.Should().BeTrue();
        asOther.ClaimedBy.Should().Be(AdminA);
        asOther.ClaimedByMe.Should().BeFalse();
        expired.ClaimedBy.Should().BeNull();
        expired.ClaimedByMe.Should().BeNull();
    }

    // ── Ledger rows ─────────────────────────────────────────────────────────

    /// <summary>A ledger escrow whose release went to the manual queue: farmer and trucker rows, both manual.</summary>
    private async Task<InMemoryLedger> LedgerWithManualRowsAsync()
    {
        var ledger = new InMemoryLedger();
        ledger.EscrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?)null));
        ledger.Escrows[EscrowId] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["orderId"] = "ord-1",
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["status"] = "escrowed",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-1",
            ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        var gateway = new ScriptedGateway();
        gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        var releasing = new EscrowService(ledger.EscrowRepo, _auditRepo, Substitute.For<IEscrowIdempotencyRepository>(), gateway, _userRepo)
        {
            LedgerRepo = ledger,
            LedgerClock = () => Now
        };
        await releasing.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = EscrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = "",
            IdempotencyKey = $"system:{EscrowId}:release:1"
        }, "system");
        ledger.Row(EscrowId, LedgerRefs.ReleaseFarmer).Status.Should().Be(LedgerFields.StatusManual);
        _claims.Ledger = ledger;
        return ledger;
    }

    private const string TruckerRef = "release#trucker#trucker-1";

    [Fact]
    public async Task Ledger_AClaimedRow_IsRefusedToAnotherAdmin_UntilItsHolderRecordsIt_WhichEndsTheClaim()
    {
        var ledger = await LedgerWithManualRowsAsync();

        var claim = await Service(ledger).ClaimManualPayoutAsync(EscrowId, "release%23farmer", AdminA, adminGroupVerified: true);
        var otherClaim = await Service(ledger).ClaimManualPayoutAsync(EscrowId, "release%23farmer", AdminB, adminGroupVerified: true);
        var otherRecord = await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminB, adminGroupVerified: true);

        claim.Value!.ClaimedBy.Should().Be(AdminA);
        claim.Value.Ref.Should().Be(LedgerRefs.ReleaseFarmer);
        otherClaim.Error!.Code.Should().Be("ESCROW_046");
        otherRecord.Error!.Code.Should().Be("ESCROW_046");
        ledger.Row(EscrowId, LedgerRefs.ReleaseFarmer).Status.Should().Be(LedgerFields.StatusManual);
        ledger.References.Should().BeEmpty();

        var record = await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminA, adminGroupVerified: true);

        record.Value!.EscrowStatus.Should().Be("released");
        var row = ledger.Row(EscrowId, LedgerRefs.ReleaseFarmer);
        row.Status.Should().Be(LedgerFields.StatusDone);
        row.PaidBy.Should().Be(AdminA);
        row.ClaimedBy.Should().BeNull();
        ledger.References.Should().ContainKey("payout-ref#TBR-0001")
            .WhoseValue.Should().Match<PayoutReferenceRecord>(r => r.EscrowId == EscrowId && r.Ref == LedgerRefs.ReleaseFarmer);
    }

    [Fact]
    public async Task Ledger_AnUnclaimedRow_IsRecordedDirectly_TheRecordClaimsIt()
    {
        var ledger = await LedgerWithManualRowsAsync();

        var record = await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminB, adminGroupVerified: true);

        record.IsSuccess.Should().BeTrue();
        ledger.Row(EscrowId, LedgerRefs.ReleaseFarmer).PaidBy.Should().Be(AdminB);
    }

    [Fact]
    public async Task Ledger_AReferenceUsedOnAnotherRow_IsRefused_TheRowStaysManual()
    {
        var ledger = await LedgerWithManualRowsAsync();
        (await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminA, adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();

        // The same portal id, typed in lower case, on the trucker row.
        var result = await Service(ledger).CompleteManualPayoutAsync(EscrowId, TruckerRef, "tbr-0001", AdminA, adminGroupVerified: true);

        result.Error!.Code.Should().Be("ESCROW_047");
        result.Error.Message.Should().Be($"This reference is already recorded on escrow {EscrowId}, leg release#farmer.");
        ledger.Row(EscrowId, TruckerRef).Status.Should().Be(LedgerFields.StatusManual);
        AuditMetadata("manual_payout_refused").Should()
            .Be($"ref={TruckerRef};code=ESCROW_047;attemptedRef=tbr-0001;usedOn={EscrowId}/release#farmer");

        // The replay of the first record is still answered as recorded.
        (await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminA, adminGroupVerified: true))
            .Value!.AlreadyRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task Ledger_ReleaseAndList_ShowTheClaim()
    {
        var ledger = await LedgerWithManualRowsAsync();
        await Service(ledger).ClaimManualPayoutAsync(EscrowId, TruckerRef, AdminA, adminGroupVerified: true);

        var leg = (await Service(ledger).ListManualPayoutsAsync(20, null, AdminB)).Value!.Payouts.Single()
            .Legs.Single(l => l.Ref == TruckerRef);
        var notHolder = await Service(ledger).ReleaseManualPayoutClaimAsync(EscrowId, TruckerRef, AdminB, adminGroupVerified: true);
        var release = await Service(ledger).ReleaseManualPayoutClaimAsync(EscrowId, TruckerRef, AdminA, adminGroupVerified: true);

        leg.ClaimedBy.Should().Be(AdminA);
        leg.ClaimedByMe.Should().BeFalse();
        leg.ClaimExpiresAt.Should().Be(ExpiresIso);
        notHolder.Error!.Code.Should().Be("ESCROW_046");
        release.Value!.Released.Should().BeTrue();
        ledger.Row(EscrowId, TruckerRef).ClaimedBy.Should().BeNull();
        AuditMetadata("manual_payout_claim_released").Should().Be($"ref={TruckerRef}");
    }

    [Fact]
    public async Task Ledger_ARowThatIsNotManual_CannotBeClaimed()
    {
        var ledger = await LedgerWithManualRowsAsync();
        await Service(ledger).CompleteManualPayoutAsync(EscrowId, "release%23farmer", "TBR-0001", AdminA, adminGroupVerified: true);

        var done = await Service(ledger).ClaimManualPayoutAsync(EscrowId, "release%23farmer", AdminA, adminGroupVerified: true);
        var unknown = await Service(ledger).ClaimManualPayoutAsync(EscrowId, "refund%23all", AdminA, adminGroupVerified: true);

        done.Error!.Code.Should().Be("ESCROW_035");
        done.Error.HttpStatus.Should().Be(409);
        unknown.Error!.HttpStatus.Should().Be(404);
    }

    /// <summary>
    /// The claim writes with the conditions of <see cref="ManualPayoutClaimRepository"/>, on the test's 1.x escrow row
    /// and on the in-memory ledger's rows; the reference reads from the test's items and the ledger's.
    /// </summary>
    private sealed class FakeClaims : IManualPayoutClaimRepository
    {
        public Func<Dictionary<string, object>> Escrow { get; set; } = () => new();
        public InMemoryLedger? Ledger { get; set; }
        public Dictionary<string, PayoutReferenceHolder> References { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = new();
        public Action? BeforeNextWrite { get; set; }

        private void Hook()
        {
            var hook = BeforeNextWrite;
            BeforeNextWrite = null;
            hook?.Invoke();
        }

        private static string? Str(Dictionary<string, object> item, string key) => item.TryGetValue(key, out var v) ? v as string : null;

        public Task<bool> TryClaimLegAsync(string escrowId, string createdAt, string op, string leg, string actor, string now, string expiresAt)
        {
            Hook();
            var e = Escrow();
            if (Str(e, ManualPayoutFields.Marker) != ManualPayoutFields.MarkerOpen || Str(e, ManualPayoutFields.Op) != op
                || Str(e, ManualPayoutFields.StatusAttr(leg)) != ManualPayoutFields.LegPending
                || ManualPayoutClaimFields.IsHeldByOther(Str(e, ManualPayoutClaimFields.ClaimedByAttr(leg)),
                    Str(e, ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)), actor, now))
            {
                return Task.FromResult(false);
            }

            e[ManualPayoutClaimFields.ClaimedByAttr(leg)] = actor;
            e[ManualPayoutClaimFields.ClaimedAtAttr(leg)] = now;
            e[ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)] = expiresAt;
            Writes.Add($"claim:{escrowId}:{leg}:{actor}:{expiresAt}");
            return Task.FromResult(true);
        }

        public Task<bool> TryReleaseLegAsync(string escrowId, string createdAt, string leg, string actor)
        {
            var e = Escrow();
            if (Str(e, ManualPayoutClaimFields.ClaimedByAttr(leg)) != actor)
            {
                return Task.FromResult(false);
            }

            e.Remove(ManualPayoutClaimFields.ClaimedByAttr(leg));
            e.Remove(ManualPayoutClaimFields.ClaimedAtAttr(leg));
            e.Remove(ManualPayoutClaimFields.ClaimExpiresAtAttr(leg));
            Writes.Add($"release:{escrowId}:{leg}:{actor}");
            return Task.FromResult(true);
        }

        public Task<bool> TryClaimLedgerRowAsync(string escrowId, string reference, string actor, string now, string expiresAt)
        {
            Hook();
            if (!Ledger!.Rows.TryGetValue((escrowId, reference), out var row) || row.Status != LedgerFields.StatusManual
                || ManualPayoutClaimFields.IsHeldByOther(row.ClaimedBy, row.ClaimExpiresAt, actor, now))
            {
                return Task.FromResult(false);
            }

            Ledger.Rows[(escrowId, reference)] = row with { ClaimedBy = actor, ClaimedAt = now, ClaimExpiresAt = expiresAt };
            Writes.Add($"claim:{escrowId}:{reference}:{actor}:{expiresAt}");
            return Task.FromResult(true);
        }

        public Task<bool> TryReleaseLedgerRowAsync(string escrowId, string reference, string actor)
        {
            if (!Ledger!.Rows.TryGetValue((escrowId, reference), out var row) || row.ClaimedBy != actor)
            {
                return Task.FromResult(false);
            }

            Ledger.Rows[(escrowId, reference)] = row with { ClaimedBy = null, ClaimedAt = null, ClaimExpiresAt = null };
            Writes.Add($"release:{escrowId}:{reference}:{actor}");
            return Task.FromResult(true);
        }

        public Task<PayoutReferenceHolder?> GetReferenceHolderAsync(string externalRef)
        {
            var key = PayoutReferenceFields.Key(externalRef);
            if (References.TryGetValue(key, out var holder))
            {
                return Task.FromResult<PayoutReferenceHolder?>(holder);
            }

            return Task.FromResult(Ledger != null && Ledger.References.TryGetValue(key, out var record)
                ? new PayoutReferenceHolder(record.EscrowId, record.Ref, record.RecordedBy, record.RecordedAt)
                : null);
        }
    }
}
