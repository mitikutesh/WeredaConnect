using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-218 against real DynamoDB (LocalStack in CI): the correction of a payout reference recorded by mistake, with
/// its transaction's conditions as DynamoDB evaluates them. The case it exists for: leg A recorded with the portal id
/// of leg B's payment refuses B (<c>ESCROW_047</c> names A); after A's correction B records. The new reference held by
/// another leg refuses the correction and writes nothing; an old item another leg holds stays; a replay writes
/// nothing twice; a stale read writes nothing; a close that read the old reference cannot copy it over the correction;
/// a ledger row moves its escrow's settlement field with it.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PayoutReferenceCorrectionDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string AdminA = "admin-fup218-a";
    private const string AdminB = "admin-fup218-b";
    private const string Reason = "Recorded with the portal id of another payout.";

    private readonly LocalDynamoDbFixture _db;

    public PayoutReferenceCorrectionDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowAuditRepository Audit => new(Ddb, _db.EscrowAudit);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);
    private PayoutReferenceCorrectionRepository Corrections => new(Ddb, _db.Escrows, _db.EscrowLedger, _db.Idempotency, _db.EscrowAudit);

    private EscrowService Service(ITelebirrGateway? gateway = null) => new(
        Escrows, Audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway)
    {
        AlarmLog = _ => { },
        LedgerRepo = Ledger,
        ManualPayoutClaims = new ManualPayoutClaimRepository(Ddb, _db.Escrows, _db.EscrowLedger, _db.Idempotency),
        PayoutReferenceTable = _db.Idempotency,
        PayoutReferenceCorrections = Corrections
    };

    private Task<EscrowResult<CorrectPayoutReferenceResponse>> Correct(
        string escrowId, string leg, string oldRef, string newRef, string admin = AdminB)
        => Service().CorrectPayoutReferenceAsync(escrowId, leg, oldRef, newRef, Reason, admin, adminGroupVerified: true);

    private static string NewId(string label) => $"escrow-order-f218{label}-{Guid.NewGuid():N}"[..48];

    private static string NewRef(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    /// <summary>A 1.x refund waiting in the manual queue: one buyer leg, pending.</summary>
    private async Task<string> SeedQueuedRefundAsync(string label)
    {
        var escrowId = NewId(label);
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f218",
            ["farmerId"] = "farmer-f218",
            ["amount"] = 2_000m,
            ["farmerPayout"] = 1_950m,
            ["commission"] = 50m,
            ["currency"] = "ETB",
            ["version"] = 3,
            ["settlementLock"] = "refund",
            [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
            [ManualPayoutFields.QueuedAt] = "2026-11-02T09:00:00.0000000Z",
            [ManualPayoutFields.Op] = ManualPayoutFields.OpRefund,
            [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported,
            [ManualPayoutFields.FromStatus] = "escrowed",
            [ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending
        });
        return escrowId;
    }

    /// <summary>A 1.x release still in the queue: the farmer leg recorded by admin A with a reference item, the trucker leg pending.</summary>
    private async Task<string> SeedOpenReleaseAsync(string label, string farmerRef)
    {
        var escrowId = NewId(label);
        var paidAt = "2026-11-03T09:00:00.0000000Z";
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f218",
            ["farmerId"] = "farmer-f218",
            ["truckerId"] = "trucker-f218",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["version"] = 4,
            ["settlementLock"] = "release",
            [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
            [ManualPayoutFields.QueuedAt] = "2026-11-02T09:00:00.0000000Z",
            [ManualPayoutFields.Op] = ManualPayoutFields.OpRelease,
            [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported,
            [ManualPayoutFields.FromStatus] = "escrowed",
            [ManualPayoutFields.StatusAttr("farmer")] = ManualPayoutFields.LegPaid,
            [ManualPayoutFields.RefAttr("farmer")] = farmerRef,
            [ManualPayoutFields.PaidByAttr("farmer")] = AdminA,
            [ManualPayoutFields.PaidAtAttr("farmer")] = paidAt,
            [ManualPayoutFields.StatusAttr("trucker")] = ManualPayoutFields.LegPending
        });
        await PutReferenceItemAsync(farmerRef, escrowId, "farmer", paidAt);
        return escrowId;
    }

    private Task PutReferenceItemAsync(string externalRef, string escrowId, string reference, string recordedAt)
        => Ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                PayoutReferenceFields.BuildPut(new PayoutReferenceRecord(_db.Idempotency, externalRef, escrowId, reference, AdminA, recordedAt))
            }
        });

    private async Task<Dictionary<string, object>> EscrowAsync(string escrowId) => (await Escrows.GetByIdAsync(escrowId, consistentRead: true))!;

    private async Task<Dictionary<string, AttributeValue>?> ReferenceItemAsync(string externalRef)
    {
        var response = await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = new() { S = PayoutReferenceFields.Key(externalRef) } },
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private async Task<List<Dictionary<string, object>>> CorrectionAuditsAsync(string escrowId)
        => (await Audit.GetAuditTrailAsync(escrowId, consistentRead: true))
            .Where(a => (string)a["action"] == EscrowService.ReferenceCorrectedAction)
            .ToList();

    private static void HeldBy(Dictionary<string, AttributeValue>? item, string escrowId, string reference)
    {
        item.Should().NotBeNull();
        item!["escrowId"].S.Should().Be(escrowId);
        item["ref"].S.Should().Be(reference);
    }

    [LocalDynamoDbFact]
    public async Task ALegRecordedWithAnotherLegsPortalId_IsCorrected_AndThatOtherLegThenRecords()
    {
        var escrowA = await SeedQueuedRefundAsync("a");
        var escrowB = await SeedQueuedRefundAsync("b");
        var paymentOfA = NewRef("TBA");
        var paymentOfB = NewRef("TBB");

        // Admin A records leg A with the portal id of B's refund: A closes, and B's own record is refused naming A.
        (await Service().CompleteManualPayoutAsync(escrowA, "buyer", paymentOfB, AdminA, adminGroupVerified: true))
            .Value!.EscrowStatus.Should().Be("refunded");
        var refused = await Service().CompleteManualPayoutAsync(escrowB, "buyer", paymentOfB, AdminB, adminGroupVerified: true);
        refused.Error!.Code.Should().Be("ESCROW_047");
        refused.Error.Message.Should().Contain(escrowA);
        var before = await EscrowAsync(escrowA);

        var corrected = await Correct(escrowA, "buyer", paymentOfB, paymentOfA);

        corrected.IsSuccess.Should().BeTrue(corrected.Error?.Message);
        corrected.Value!.AlreadyCorrected.Should().BeFalse();
        var after = await EscrowAsync(escrowA);
        after[ManualPayoutFields.RefAttr("buyer")].Should().Be(paymentOfA);
        after["telebirrRefundRef"].Should().Be(paymentOfA);
        // No money moved: status, amount, paid time and recorder stay; only the version (1.38.0) and updatedAt move.
        after["status"].Should().Be("refunded");
        after["amount"].Should().Be(before["amount"]);
        after[ManualPayoutFields.PaidByAttr("buyer")].Should().Be(AdminA);
        after[ManualPayoutFields.PaidAtAttr("buyer")].Should().Be(before[ManualPayoutFields.PaidAtAttr("buyer")]);
        int.Parse((string)after["version"]).Should().Be(int.Parse((string)before["version"]) + 1);
        (await ReferenceItemAsync(paymentOfB)).Should().BeNull();
        var newItem = await ReferenceItemAsync(paymentOfA);
        HeldBy(newItem, escrowA, "buyer");
        newItem!["recordedBy"].S.Should().Be(AdminB);
        var audit = (await CorrectionAuditsAsync(escrowA)).Should().ContainSingle().Subject;
        audit["actor"].Should().Be(AdminB);
        audit["telebirrRef"].Should().Be(paymentOfA);
        audit["metadata"].Should().Be($"leg=buyer;oldRef={paymentOfB};newRef={paymentOfA};reason={Reason}");

        // B records now, and its item names B.
        (await Service().CompleteManualPayoutAsync(escrowB, "buyer", paymentOfB, AdminB, adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();
        HeldBy(await ReferenceItemAsync(paymentOfB), escrowB, "buyer");

        // The same correction again is answered from the leg: nothing is written twice.
        var replay = await Correct(escrowA, "buyer", paymentOfB, paymentOfA);
        replay.Value!.AlreadyCorrected.Should().BeTrue();
        (await CorrectionAuditsAsync(escrowA)).Should().ContainSingle();
        (await EscrowAsync(escrowA))["version"].Should().Be(after["version"]);
    }

    [LocalDynamoDbFact]
    public async Task ANewReferenceHeldByAnotherLeg_IsRefusedNamingIt_AndNothingIsWritten()
    {
        var escrowA = await SeedQueuedRefundAsync("ta");
        var escrowC = await SeedQueuedRefundAsync("tc");
        var wrong = NewRef("TBW");
        var taken = NewRef("TBT");
        (await Service().CompleteManualPayoutAsync(escrowA, "buyer", wrong, AdminA, adminGroupVerified: true)).IsSuccess.Should().BeTrue();
        (await Service().CompleteManualPayoutAsync(escrowC, "buyer", taken, AdminA, adminGroupVerified: true)).IsSuccess.Should().BeTrue();
        var before = await EscrowAsync(escrowA);

        var result = await Correct(escrowA, "buyer", wrong, taken);

        result.Error!.Code.Should().Be("ESCROW_047");
        result.Error.Message.Should().Be($"This reference is already recorded on escrow {escrowC}, leg buyer.");
        var after = await EscrowAsync(escrowA);
        after[ManualPayoutFields.RefAttr("buyer")].Should().Be(wrong);
        after["version"].Should().Be(before["version"]);
        HeldBy(await ReferenceItemAsync(wrong), escrowA, "buyer");
        HeldBy(await ReferenceItemAsync(taken), escrowC, "buyer");
        (await CorrectionAuditsAsync(escrowA)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task AnOldItemAnotherLegHolds_StaysWithIt()
    {
        // FUP-215: one portal id on two legs before 2.12.0, the backfill gave its item to the other leg.
        var escrowId = await SeedOpenReleaseAsync("bf", NewRef("TBX"));
        var shared = NewRef("TBS");
        var other = NewId("bo");
        var escrow = await EscrowAsync(escrowId);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = new() { S = escrowId }, ["createdAt"] = new() { S = CreatedAt } },
            UpdateExpression = "SET #ref = :shared",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#ref"] = ManualPayoutFields.RefAttr("farmer") },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":shared"] = new() { S = shared } }
        });
        await PutReferenceItemAsync(shared, other, "farmer", "2026-09-01T09:00:00.0000000Z");
        var right = NewRef("TBR");

        var result = await Correct(escrowId, "farmer", shared, right);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        HeldBy(await ReferenceItemAsync(shared), other, "farmer");
        HeldBy(await ReferenceItemAsync(right), escrowId, "farmer");
        // The queue is still open: no settlement field yet; the close copies the corrected reference.
        (await EscrowAsync(escrowId)).Should().NotContainKey("telebirrReleaseRef");
        escrow[ManualPayoutFields.Marker].Should().Be(ManualPayoutFields.MarkerOpen);
    }

    [LocalDynamoDbFact]
    public async Task AStaleRead_WritesNothing()
    {
        var wrong = NewRef("TBO");
        var escrowId = await SeedOpenReleaseAsync("st", wrong);
        var right = NewRef("TBN");
        var now = DateTime.UtcNow.ToString("o");
        PayoutReferenceCorrection Plan(int version, string paidBy) => new()
        {
            EscrowId = escrowId,
            Ref = "farmer",
            OldRef = wrong,
            NewRef = right,
            PaidBy = paidBy,
            Now = now,
            Legacy = new LegacyLegRead(CreatedAt, "escrowed", version),
            DeleteOldItem = true,
            NewItem = new PayoutReferenceRecord(_db.Idempotency, right, escrowId, "farmer", AdminB, now),
            Audit = new LedgerAudit(escrowId, now, EscrowService.ReferenceCorrectedAction, "escrowed", "escrowed", AdminB, "x", right)
        };

        // Another write moved the version since the read, or the leg was recorded by someone else.
        (await Corrections.CorrectAsync(Plan(version: 3, paidBy: AdminA))).Should().Be(PayoutReferenceCorrectionWrite.Changed);
        (await Corrections.CorrectAsync(Plan(version: 4, paidBy: AdminB))).Should().Be(PayoutReferenceCorrectionWrite.Changed);

        (await EscrowAsync(escrowId))[ManualPayoutFields.RefAttr("farmer")].Should().Be(wrong);
        HeldBy(await ReferenceItemAsync(wrong), escrowId, "farmer");
        (await ReferenceItemAsync(right)).Should().BeNull();
        (await CorrectionAuditsAsync(escrowId)).Should().BeEmpty();

        (await Corrections.CorrectAsync(Plan(version: 4, paidBy: AdminA))).Should().Be(PayoutReferenceCorrectionWrite.Written);
    }

    [LocalDynamoDbFact]
    public async Task ACloseThatReadTheOldReference_CannotCopyItOverTheCorrection()
    {
        var wrong = NewRef("TBO");
        var escrowId = await SeedOpenReleaseAsync("cl", wrong);
        var right = NewRef("TBN");
        var truckerRef = NewRef("TBK");
        var now = DateTime.UtcNow.ToString("o");
        Dictionary<string, object> Fields(string farmerRef) => new()
        {
            ["telebirrReleaseRef"] = farmerRef,
            ["truckerTelebirrRef"] = truckerRef,
            ["releasedAt"] = now
        };

        // The close of the trucker leg read the farmer's old reference; the correction lands before its write.
        (await Correct(escrowId, "farmer", wrong, right)).IsSuccess.Should().BeTrue();
        var stale = await Escrows.TryCloseManualPayoutAsync(escrowId, CreatedAt, "release", "trucker", truckerRef, AdminA, now,
            new[] { "farmer" }, "released", Fields(wrong));

        stale.Should().BeFalse();
        (await EscrowAsync(escrowId))[ManualPayoutFields.Marker].Should().Be(ManualPayoutFields.MarkerOpen);

        // Read again, the close copies the corrected reference.
        (await Escrows.TryCloseManualPayoutAsync(escrowId, CreatedAt, "release", "trucker", truckerRef, AdminA, now,
            new[] { "farmer" }, "released", Fields(right))).Should().BeTrue();
        var closed = await EscrowAsync(escrowId);
        closed["telebirrReleaseRef"].Should().Be(right);
        closed["status"].Should().Be("released");
    }

    [LocalDynamoDbFact]
    public async Task ALedgerRow_IsCorrectedWithItsEscrowsSettlementField()
    {
        var escrowId = NewId("l");
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f218",
            ["farmerId"] = "farmer-f218",
            ["truckerId"] = "trucker-f218",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-F218",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z",
            ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        });
        var gateway = new ScriptedGateway();
        gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        gateway.Script.Enqueue((false, TelebirrGatewayErrors.PayoutNotSupported, null));
        await Service(gateway).ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = "",
            IdempotencyKey = $"system:{escrowId}:release:1"
        }, "system");
        var wrong = NewRef("TBL");
        (await Service().CompleteManualPayoutAsync(escrowId, "release%23farmer", wrong, AdminA, adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();
        var rowBefore = (await Ledger.GetRowAsync(escrowId, LedgerRefs.ReleaseFarmer))!;
        var escrowBefore = await EscrowAsync(escrowId);
        escrowBefore["telebirrReleaseRef"].Should().Be(wrong);
        var right = NewRef("TBM");

        var result = await Correct(escrowId, "release%23farmer", wrong, right);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        var row = (await Ledger.GetRowAsync(escrowId, LedgerRefs.ReleaseFarmer))!;
        row.TelebirrRef.Should().Be(right);
        row.Version.Should().Be(rowBefore.Version + 1);
        row.Status.Should().Be(LedgerFields.StatusDone);
        row.PaidBy.Should().Be(AdminA);
        row.Amount.Should().Be(rowBefore.Amount);
        row.DoneAt.Should().Be(rowBefore.DoneAt);
        var escrowAfter = await EscrowAsync(escrowId);
        escrowAfter["telebirrReleaseRef"].Should().Be(right);
        escrowAfter["status"].Should().Be(escrowBefore["status"]);
        int.Parse((string)escrowAfter["version"]).Should().Be(int.Parse((string)escrowBefore["version"]) + 1);
        (await ReferenceItemAsync(wrong)).Should().BeNull();
        HeldBy(await ReferenceItemAsync(right), escrowId, LedgerRefs.ReleaseFarmer);
        (await CorrectionAuditsAsync(escrowId)).Should().ContainSingle()
            .Which["metadata"].Should().Be($"ref=release#farmer;oldRef={wrong};newRef={right};reason={Reason}");
    }
}
