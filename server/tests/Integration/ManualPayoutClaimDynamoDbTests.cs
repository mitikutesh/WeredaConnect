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
/// FUP-05 against real DynamoDB (LocalStack in CI): the claim on a manual payout leg and the one-leg-per-reference
/// item, with the conditions as DynamoDB evaluates them and the races they exist for. Two admins claiming one leg
/// at once: one holds it. A record under another admin's live claim: refused by the write's own condition, with no
/// reference item left behind. Two records of one portal reference on two legs at once: one lands, the other is
/// refused naming it, and its leg stays queued. Both settlement models.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ManualPayoutClaimDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string AdminA = "admin-fup05-a";
    private const string AdminB = "admin-fup05-b";

    private readonly LocalDynamoDbFixture _db;

    public ManualPayoutClaimDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);
    private ManualPayoutClaimRepository Claims => new(Ddb, _db.Escrows, _db.EscrowLedger, _db.Idempotency);

    private EscrowService Service(DateTime? now = null, ITelebirrGateway? gateway = null) => new(
        Escrows, new EscrowAuditRepository(Ddb, _db.EscrowAudit), new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway)
    {
        AlarmLog = _ => { },
        LedgerRepo = Ledger,
        LedgerClock = () => now ?? DateTime.UtcNow,
        ManualPayoutClaims = Claims,
        PayoutReferenceTable = _db.Idempotency,
        ClaimClock = () => now ?? DateTime.UtcNow
    };

    private static string NewId(string label) => $"escrow-order-f05{label}-{Guid.NewGuid():N}"[..48];

    private static string NewRef() => $"TBF05{Guid.NewGuid():N}"[..20].ToUpperInvariant();

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
            ["buyerId"] = "buyer-f05",
            ["farmerId"] = "farmer-f05",
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

    /// <summary>A ledger escrow whose release the live gateway could not make: both rows wait as manual.</summary>
    private async Task<string> SeedLedgerManualRowsAsync(string label)
    {
        var escrowId = NewId(label);
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f05",
            ["farmerId"] = "farmer-f05",
            ["truckerId"] = "trucker-f05",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-F05",
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
        await Service(gateway: gateway).ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "delivery_confirmed",
            DeliveryId = "",
            IdempotencyKey = $"system:{escrowId}:release:1"
        }, "system");
        (await Ledger.GetRowsAsync(escrowId)).Select(r => r.Status).Should().AllBe(LedgerFields.StatusManual);
        return escrowId;
    }

    private static string TruckerRef => LedgerRefs.ReleaseTrucker("trucker-f05");

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

    [LocalDynamoDbFact]
    public async Task TwoAdminsClaimingOneLegAtOnce_OneHoldsIt_TheOtherIsRefused()
    {
        for (var round = 0; round < 5; round++)
        {
            var escrowId = await SeedQueuedRefundAsync($"c{round}");

            var results = await Task.WhenAll(
                Service().ClaimManualPayoutAsync(escrowId, "buyer", AdminA, adminGroupVerified: true),
                Service().ClaimManualPayoutAsync(escrowId, "buyer", AdminB, adminGroupVerified: true));

            results.Count(r => r.IsSuccess).Should().Be(1, "exactly one admin holds the claim in round {0}", round);
            var winner = results.Single(r => r.IsSuccess).Value!.ClaimedBy;
            results.Single(r => !r.IsSuccess).Error!.Code.Should().Be("ESCROW_046");
            (await EscrowAsync(escrowId))[ManualPayoutClaimFields.ClaimedByAttr("buyer")].Should().Be(winner);
        }
    }

    [LocalDynamoDbFact]
    public async Task TwoAdminsClaimingOneLedgerRowAtOnce_OneHoldsIt_TheOtherIsRefused()
    {
        for (var round = 0; round < 3; round++)
        {
            var escrowId = await SeedLedgerManualRowsAsync($"l{round}");

            var results = await Task.WhenAll(
                Service().ClaimManualPayoutAsync(escrowId, "release%23farmer", AdminA, adminGroupVerified: true),
                Service().ClaimManualPayoutAsync(escrowId, "release%23farmer", AdminB, adminGroupVerified: true));

            results.Count(r => r.IsSuccess).Should().Be(1, "exactly one admin holds the claim in round {0}", round);
            var winner = results.Single(r => r.IsSuccess).Value!.ClaimedBy;
            results.Single(r => !r.IsSuccess).Error!.Code.Should().Be("ESCROW_046");
            var row = (await Ledger.GetRowAsync(escrowId, LedgerRefs.ReleaseFarmer))!;
            row.ClaimedBy.Should().Be(winner);
            // Bookkeeping only: the row's own compare-and-set counter does not move with a claim.
            row.Status.Should().Be(LedgerFields.StatusManual);
        }
    }

    [LocalDynamoDbFact]
    public async Task ARecordUnderAnotherAdminsLiveClaim_IsRefusedByTheWriteItself_AndLeavesNoReferenceItem()
    {
        var escrowId = await SeedQueuedRefundAsync("cond");
        (await Service().ClaimManualPayoutAsync(escrowId, "buyer", AdminA, adminGroupVerified: true)).IsSuccess.Should().BeTrue();
        var reference = NewRef();
        var now = DateTime.UtcNow.ToString("o");
        PayoutReferenceRecord Record(string admin) => new(_db.Idempotency, reference, escrowId, "buyer", admin, now);
        var fields = new Dictionary<string, object> { ["telebirrRefundRef"] = reference, ["refundedAt"] = now };

        // Admin B passed the service's own check before A's claim landed: the write's condition still refuses it.
        var byB = await Escrows.TryCloseManualPayoutAsync(
            escrowId, CreatedAt, "refund", "buyer", reference, AdminB, now, Array.Empty<string>(), "refunded", fields, Record(AdminB));

        byB.Should().BeFalse();
        (await ReferenceItemAsync(reference)).Should().BeNull();
        var escrow = await EscrowAsync(escrowId);
        escrow[ManualPayoutFields.StatusAttr("buyer")].Should().Be(ManualPayoutFields.LegPending);
        escrow[ManualPayoutFields.Marker].Should().Be(ManualPayoutFields.MarkerOpen);

        // The holder records it: the leg, the close and the reference item land together, and the claim ends.
        var byA = await Service().CompleteManualPayoutAsync(escrowId, "buyer", reference, AdminA, adminGroupVerified: true);

        byA.IsSuccess.Should().BeTrue();
        byA.Value!.EscrowStatus.Should().Be("refunded");
        escrow = await EscrowAsync(escrowId);
        escrow.Should().NotContainKey(ManualPayoutClaimFields.ClaimedByAttr("buyer"));
        escrow.Should().NotContainKey(ManualPayoutClaimFields.ClaimExpiresAtAttr("buyer"));
        var item = (await ReferenceItemAsync(reference))!;
        item["escrowId"].S.Should().Be(escrowId);
        item["ref"].S.Should().Be("buyer");
        item["recordedBy"].S.Should().Be(AdminA);
        long.Parse(item["expiresAt"].N).Should().BeGreaterThan(DateTimeOffset.UtcNow.AddDays(399).ToUnixTimeSeconds());
    }

    [LocalDynamoDbFact]
    public async Task AnExpiredOrReleasedClaim_CanBeTakenByAnotherAdmin()
    {
        var escrowId = await SeedQueuedRefundAsync("exp");
        var t0 = DateTime.UtcNow;
        (await Service(t0).ClaimManualPayoutAsync(escrowId, "buyer", AdminA, adminGroupVerified: true)).IsSuccess.Should().BeTrue();

        (await Service(t0.AddMinutes(29)).ClaimManualPayoutAsync(escrowId, "buyer", AdminB, adminGroupVerified: true))
            .Error!.Code.Should().Be("ESCROW_046");
        var afterExpiry = await Service(t0.AddMinutes(31)).ClaimManualPayoutAsync(escrowId, "buyer", AdminB, adminGroupVerified: true);

        afterExpiry.IsSuccess.Should().BeTrue();
        (await EscrowAsync(escrowId))[ManualPayoutClaimFields.ClaimedByAttr("buyer")].Should().Be(AdminB);
        // A cannot release B's claim; B can.
        (await Service(t0.AddMinutes(32)).ReleaseManualPayoutClaimAsync(escrowId, "buyer", AdminA, adminGroupVerified: true))
            .Error!.Code.Should().Be("ESCROW_046");
        (await Service(t0.AddMinutes(32)).ReleaseManualPayoutClaimAsync(escrowId, "buyer", AdminB, adminGroupVerified: true))
            .Value!.Released.Should().BeTrue();
        (await Service(t0.AddMinutes(33)).ClaimManualPayoutAsync(escrowId, "buyer", AdminA, adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();
    }

    [LocalDynamoDbFact]
    public async Task TwoRecordsOfOneReferenceOnTwoEscrowsAtOnce_OneLands_TheOtherIsRefusedNamingIt()
    {
        for (var round = 0; round < 3; round++)
        {
            var first = await SeedQueuedRefundAsync($"d{round}a");
            var second = await SeedQueuedRefundAsync($"d{round}b");
            var reference = NewRef();

            var results = await Task.WhenAll(
                Service().CompleteManualPayoutAsync(first, "buyer", reference, AdminA, adminGroupVerified: true),
                Service().CompleteManualPayoutAsync(second, "buyer", reference.ToLowerInvariant(), AdminB, adminGroupVerified: true));

            results.Count(r => r.IsSuccess).Should().Be(1, "one reference records one leg (round {0})", round);
            var winner = results[0].IsSuccess ? first : second;
            var loser = winner == first ? second : first;
            var refused = results.Single(r => !r.IsSuccess).Error!;
            refused.Code.Should().Be("ESCROW_047");
            refused.Message.Should().Be($"This reference is already recorded on escrow {winner}, leg buyer.");
            (await ReferenceItemAsync(reference))!["escrowId"].S.Should().Be(winner);
            (await EscrowAsync(winner))["status"].Should().Be("refunded");
            var queued = await EscrowAsync(loser);
            queued["status"].Should().Be("escrowed");
            queued[ManualPayoutFields.Marker].Should().Be(ManualPayoutFields.MarkerOpen);
            queued[ManualPayoutFields.StatusAttr("buyer")].Should().Be(ManualPayoutFields.LegPending);
            var refusals = await new EscrowAuditRepository(Ddb, _db.EscrowAudit).GetAuditTrailAsync(loser, consistentRead: true);
            refusals.Should().Contain(r => (string)r["action"] == "manual_payout_refused"
                                           && ((string)r["metadata"]).EndsWith($";usedOn={winner}/buyer"));
        }
    }

    [LocalDynamoDbFact]
    public async Task TwoRecordsOfOneReferenceOnTwoLedgerRowsAtOnce_OneLands_TheOtherRowStaysManual()
    {
        var escrowId = await SeedLedgerManualRowsAsync("dup");
        var reference = NewRef();

        var results = await Task.WhenAll(
            Service().CompleteManualPayoutAsync(escrowId, "release%23farmer", reference, AdminA, adminGroupVerified: true),
            Service().CompleteManualPayoutAsync(escrowId, TruckerRef, reference, AdminB, adminGroupVerified: true));

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => !r.IsSuccess).Error!.Code.Should().Be("ESCROW_047");
        var rows = await Ledger.GetRowsAsync(escrowId);
        rows.Count(r => r.Status == LedgerFields.StatusDone).Should().Be(1);
        rows.Count(r => r.Status == LedgerFields.StatusManual).Should().Be(1);
        var done = rows.Single(r => r.Status == LedgerFields.StatusDone);
        done.TelebirrRef.Should().Be(reference);
        (await ReferenceItemAsync(reference))!["ref"].S.Should().Be(done.Ref);

        // The other row records with its own reference, and the escrow completes.
        var other = rows.Single(r => r.Status == LedgerFields.StatusManual);
        var last = await Service().CompleteManualPayoutAsync(escrowId, Uri.EscapeDataString(other.Ref), NewRef(), AdminB, adminGroupVerified: true);
        last.Value!.EscrowStatus.Should().Be("completed");
    }

    [LocalDynamoDbFact]
    public async Task AReplayOfTheSameLegAndReference_IsIdempotent()
    {
        var escrowId = await SeedQueuedRefundAsync("replay");
        var reference = NewRef();

        var first = await Service().CompleteManualPayoutAsync(escrowId, "buyer", reference, AdminA, adminGroupVerified: true);
        var item = await ReferenceItemAsync(reference);
        var again = await Service().CompleteManualPayoutAsync(escrowId, "buyer", reference, AdminB, adminGroupVerified: true);

        first.Value!.AlreadyRecorded.Should().BeFalse();
        again.Value!.AlreadyRecorded.Should().BeTrue();
        again.Value.EscrowStatus.Should().Be("refunded");
        (await ReferenceItemAsync(reference)).Should().BeEquivalentTo(item);
    }
}
