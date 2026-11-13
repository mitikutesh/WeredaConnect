using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-436 against real DynamoDB (LocalStack in CI): the markers behind <c>TRUCKER_PAYOUT_NEWLY_OVERDUE</c>,
/// <c>RELEASE_BLOCKED_NEWLY_OVERDUE</c> and <c>LEDGER_ROW_NEWLY_OVERDUE</c>, with the conditions as DynamoDB evaluates
/// them. Each lands once per item and since-time (the 1.x leg's pending time, the ledger escrow's release time, the
/// stored block's time, the open row's creation time), never on an item that moved on, moves no version or
/// <c>updatedAt</c>, and is read back through the index its report reads, so the next run skips it without a write.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EdgeOverdueMarkersDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string UpdatedAt = "2026-11-02T09:00:00.0000000Z";

    // The oldest expiresAt sorts first in StatusExpiresIndex, so the capped 1.x read finds these rows on the shared tables.
    private const string ExpiresAt = "2020-01-01T00:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;

    public EdgeOverdueMarkersDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private static string NewId(string label) => $"escrow-order-f436{label}-{Guid.NewGuid():N}"[..48];

    private static string HoursAgo(double hours) => DateTime.UtcNow.AddHours(-hours).ToString("o");

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<Dictionary<string, object>> EscrowAsync(string escrowId)
        => (await Escrows.GetByIdAsync(escrowId, consistentRead: true))!;

    private async Task SeedEscrowAsync(string escrowId, string status, Action<Dictionary<string, object>> change)
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = UpdatedAt,
            ["status"] = status,
            ["buyerId"] = "buyer-f436",
            ["farmerId"] = "farmer-f436",
            ["truckerId"] = "trucker-f436",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["expiresAt"] = ExpiresAt,
            ["version"] = 3
        };
        change(item);
        await Escrows.CreateAsync(item);
    }

    private Task UpdateAsync(string escrowId, string expression, Dictionary<string, AttributeValue> values,
        Dictionary<string, string>? names = null)
        => Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = new() { S = escrowId }, ["createdAt"] = new() { S = CreatedAt } },
            UpdateExpression = expression,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values.Count == 0 ? null : values
        });

    private static void AssertUntouched(Dictionary<string, object> escrow)
    {
        Convert.ToInt32(escrow["version"]).Should().Be(3);
        Str(escrow, "updatedAt").Should().Be(UpdatedAt);
    }

    // ── The 1.x trucker leg (truckerPayoutOverdueLoggedFor = truckerPayoutPendingSince) ─────────────────────

    [LocalDynamoDbFact]
    public async Task TheTruckerMarker_LandsOncePerPendingTime_AndTheRetryReadsItBack()
    {
        var escrowId = NewId("t1x");
        var since = HoursAgo(30);
        await SeedEscrowAsync(escrowId, "released", e =>
        {
            e["releasedAt"] = since;
            e[TruckerPayoutFields.Pending] = true;
            e[TruckerPayoutFields.PendingSince] = since;
        });

        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(escrowId, CreatedAt, HoursAgo(40))).Should().BeFalse(); // another time
        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(escrowId, CreatedAt, since)).Should().BeTrue();
        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(escrowId, CreatedAt, since)).Should().BeFalse();
        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(NewId("missing"), CreatedAt, since)).Should().BeFalse();

        var escrow = await EscrowAsync(escrowId);
        Str(escrow, TruckerPayoutFields.OverdueLoggedFor).Should().Be(since);
        AssertUntouched(escrow);

        // The hourly retry reads the pending legs through StatusExpiresIndex (projection ALL) and sees the marker.
        var pending = await Escrows.QueryPendingTruckerLegsAsync(500);
        var indexed = pending.FirstOrDefault(r => Str(r, "escrowId") == escrowId);
        indexed.Should().NotBeNull();
        Str(indexed!, TruckerPayoutFields.OverdueLoggedFor).Should().Be(since);
    }

    [LocalDynamoDbFact]
    public async Task TheTruckerMarker_IsRefusedOnAPaidLeg_AndLandsAgainForALegPendingFromAnotherTime()
    {
        var paid = NewId("tpaid");
        var since = HoursAgo(26);
        await SeedEscrowAsync(paid, "completed", e =>
        {
            e["releasedAt"] = since;
            e[TruckerPayoutFields.PendingSince] = since; // the flag went with the payout
        });
        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(paid, CreatedAt, since)).Should().BeFalse();
        Str(await EscrowAsync(paid), TruckerPayoutFields.OverdueLoggedFor).Should().BeNull();

        var again = NewId("tagain");
        await SeedEscrowAsync(again, "released", e =>
        {
            e[TruckerPayoutFields.Pending] = true;
            e[TruckerPayoutFields.PendingSince] = HoursAgo(80);
        });
        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(again, CreatedAt, Str(await EscrowAsync(again), TruckerPayoutFields.PendingSince)!))
            .Should().BeTrue();
        var later = HoursAgo(25);
        await UpdateAsync(again, "SET #since = :since", new Dictionary<string, AttributeValue> { [":since"] = new() { S = later } },
            new Dictionary<string, string> { ["#since"] = TruckerPayoutFields.PendingSince });

        (await Escrows.TryMarkTruckerPayoutOverdueLoggedAsync(again, CreatedAt, later)).Should().BeTrue();
        Str(await EscrowAsync(again), TruckerPayoutFields.OverdueLoggedFor).Should().Be(later);
    }

    // ── A ledger escrow's transport leg (truckerPayoutOverdueLoggedFor = releasedAt) ───────────────────────

    [LocalDynamoDbFact]
    public async Task TheTransportMarker_LandsOncePerRelease_WhileTheLegIsHeld_AndTheRetryReadsItBack()
    {
        var escrowId = NewId("tled");
        var releasedAt = HoursAgo(30);
        await SeedEscrowAsync(escrowId, "released", e =>
        {
            e["releasedAt"] = releasedAt;
            e[LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger;
            e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
            {
                ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
            };
        });

        (await Ledger.TryMarkTransportOverdueLoggedAsync(escrowId, CreatedAt, HoursAgo(31))).Should().BeFalse();
        (await Ledger.TryMarkTransportOverdueLoggedAsync(escrowId, CreatedAt, releasedAt)).Should().BeTrue();
        (await Ledger.TryMarkTransportOverdueLoggedAsync(escrowId, CreatedAt, releasedAt)).Should().BeFalse();

        var escrow = await EscrowAsync(escrowId);
        Str(escrow, TruckerPayoutFields.OverdueLoggedFor).Should().Be(releasedAt);
        AssertUntouched(escrow);

        Dictionary<string, object>? indexed = null;
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (items, next) = await Ledger.QueryOwedTransportLegsAsync("released", start);
            indexed = items.FirstOrDefault(i => Str(i, "escrowId") == escrowId) ?? indexed;
            start = next;
        } while (start != null && indexed == null);

        indexed.Should().NotBeNull();
        Str(indexed!, TruckerPayoutFields.OverdueLoggedFor).Should().Be(releasedAt);
    }

    [LocalDynamoDbFact]
    public async Task TheTransportMarker_IsRefusedOnceTheLegIsDecided_OrOnA1xEscrow()
    {
        var decided = NewId("tdec");
        var releasedAt = HoursAgo(27);
        await SeedEscrowAsync(decided, "released", e =>
        {
            e["releasedAt"] = releasedAt;
            e[LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger;
            e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
            {
                ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
            };
        });
        (await Ledger.TryMarkTransportOverdueLoggedAsync(decided, CreatedAt, releasedAt)).Should().BeFalse();

        var legacy = NewId("t1xled");
        await SeedEscrowAsync(legacy, "released", e => e["releasedAt"] = releasedAt);
        (await Ledger.TryMarkTransportOverdueLoggedAsync(legacy, CreatedAt, releasedAt)).Should().BeFalse();
        Str(await EscrowAsync(legacy), TruckerPayoutFields.OverdueLoggedFor).Should().BeNull();
    }

    // ── A stored release block (settlementBlockOverdueLoggedFor = settlementBlockReasonAt) ──────────────────

    [LocalDynamoDbFact]
    public async Task TheBlockMarker_LandsOncePerStoredBlock_AndAgainForABlockStoredLater()
    {
        const string reason = "release:payout:phone_unverified";
        var escrowId = NewId("blk");
        var reasonAt = HoursAgo(30);
        await SeedEscrowAsync(escrowId, "escrowed", e =>
        {
            e[SettlementBlockFields.Reason] = reason;
            e[SettlementBlockFields.ReasonAt] = reasonAt;
        });

        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, "release:payout:fayda_required", reasonAt))
            .Should().BeFalse(); // another reason
        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, reason, HoursAgo(40))).Should().BeFalse();
        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, reason, reasonAt)).Should().BeTrue();
        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, reason, reasonAt)).Should().BeFalse();

        var escrow = await EscrowAsync(escrowId);
        Str(escrow, SettlementBlockFields.OverdueLoggedFor).Should().Be(reasonAt);
        AssertUntouched(escrow);

        // The gate passed (the reason is cleared): the old block cannot be marked again. Blocked again later, the
        // new block (a new stored time) is marked again.
        (await Escrows.TryClearSettlementBlockReasonAsync(escrowId, CreatedAt, reason)).Should().BeTrue();
        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, reason, reasonAt)).Should().BeFalse();
        Str(await EscrowAsync(escrowId), SettlementBlockFields.OverdueLoggedFor).Should().Be(reasonAt);

        var storedAgain = HoursAgo(25);
        (await Escrows.TrySetSettlementBlockReasonAsync(escrowId, CreatedAt, "escrowed", reason, storedAgain)).Should().BeTrue();
        (await Escrows.TryMarkSettlementBlockOverdueLoggedAsync(escrowId, CreatedAt, reason, storedAgain)).Should().BeTrue();
        Str(await EscrowAsync(escrowId), SettlementBlockFields.OverdueLoggedFor).Should().Be(storedAgain);
    }

    // ── An open ledger row (overdueLoggedFor = createdAt) ───────────────────────────────────────────────────

    private async Task<LedgerRow> PutOpenRowAsync(string escrowId, string reference, string status, string createdAt)
    {
        var row = new LedgerRow
        {
            EscrowId = escrowId,
            Ref = reference,
            Kind = LedgerFields.KindRelease,
            Legs = new[] { LedgerEscrowFields.LegCrop },
            PayeeRole = LedgerFields.PayeeFarmer,
            PayeeId = "farmer-f436",
            Amount = 15_000m,
            Status = status,
            GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
            Reason = "delivery_confirmed",
            Actor = "system",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            DueAt = createdAt,
            Version = 4
        };
        (await Ledger.PutRowAsync(row, new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(createdAt, reference, "ledger_row_pending"),
            "ledger_row_pending", "", status, "system", $"ref={reference}"))).Should().Be(LedgerWrite.Written);
        return row;
    }

    [LocalDynamoDbFact]
    public async Task TheRowMarker_LandsOncePerOpenRow_SurvivesARowWrite_AndTheSweepReadsItBack()
    {
        var escrowId = NewId("row");
        var createdAt = HoursAgo(26);
        const string reference = LedgerRefs.ReleaseFarmer;
        await PutOpenRowAsync(escrowId, reference, LedgerFields.StatusPending, createdAt);

        (await Ledger.TryMarkRowOverdueLoggedAsync(escrowId, reference, LedgerFields.StatusSent, createdAt)).Should().BeFalse();
        (await Ledger.TryMarkRowOverdueLoggedAsync(escrowId, reference, LedgerFields.StatusPending, HoursAgo(30))).Should().BeFalse();
        (await Ledger.TryMarkRowOverdueLoggedAsync(escrowId, reference, LedgerFields.StatusPending, createdAt)).Should().BeTrue();
        (await Ledger.TryMarkRowOverdueLoggedAsync(escrowId, reference, LedgerFields.StatusPending, createdAt)).Should().BeFalse();
        (await Ledger.TryMarkRowOverdueLoggedAsync(escrowId, "release#nothing", LedgerFields.StatusPending, createdAt)).Should().BeFalse();

        var row = (await Ledger.GetRowAsync(escrowId, reference))!;
        row.OverdueLoggedFor.Should().Be(createdAt);
        row.ManualOverdueLoggedFor.Should().BeNull();
        (row.Version, row.UpdatedAt, row.Status).Should().Be((4, createdAt, LedgerFields.StatusPending));

        // A row write (the sweep's call stamp) moves the version and leaves the marker in place.
        var now = DateTime.UtcNow.ToString("o");
        (await Ledger.TryStampCallingAsync(row, now, now)).Should().BeTrue();
        var stamped = (await Ledger.GetRowAsync(escrowId, reference))!;
        (stamped.Version, stamped.OverdueLoggedFor).Should().Be((5, createdAt));

        // The ledger sweep's overdue report reads the open rows through LedgerOpenIndex and sees the marker.
        Dictionary<string, AttributeValue>? startKey = null;
        LedgerRow? indexed = null;
        do
        {
            var (rows, next) = await Ledger.QueryOpenAsync(LedgerFields.StatusPending, DateTime.UtcNow.AddHours(1).ToString("o"), 100, startKey);
            indexed = rows.FirstOrDefault(r => r.EscrowId == escrowId) ?? indexed;
            startKey = next;
        } while (startKey != null && indexed == null);

        indexed.Should().NotBeNull();
        indexed!.OverdueLoggedFor.Should().Be(createdAt);
    }
}
