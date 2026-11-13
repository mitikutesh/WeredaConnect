using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-06 against real DynamoDB (LocalStack in CI): the markers behind <c>MANUAL_PAYOUT_NEWLY_OVERDUE</c>, with the
/// conditions as DynamoDB evaluates them. The 1.x marker on the escrow row lands once per queue entry (keyed on its
/// queue time), never on a closed entry, and again for an entry queued later; the ledger marker lands once per stay
/// of a row in <c>manual</c> (keyed on its <c>dueAt</c>). Neither moves a version or <c>updatedAt</c>, and both are
/// read back through the index the reports read, so the next run skips them without a write.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ManualPayoutNewlyOverdueDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string UpdatedAt = "2026-11-02T09:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;

    public ManualPayoutNewlyOverdueDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private static string NewId(string label) => $"escrow-order-f06{label}-{Guid.NewGuid():N}"[..48];

    private static string HoursAgo(double hours) => DateTime.UtcNow.AddHours(-hours).ToString("o");

    /// <summary>A 1.x refund waiting in the manual queue since <paramref name="queuedAt"/>: one buyer leg, pending.</summary>
    private async Task<string> SeedQueuedRefundAsync(string label, string queuedAt)
    {
        var escrowId = NewId(label);
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = UpdatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f06",
            ["farmerId"] = "farmer-f06",
            ["amount"] = 2_000m,
            ["farmerPayout"] = 1_950m,
            ["commission"] = 50m,
            ["currency"] = "ETB",
            ["version"] = 3,
            ["settlementLock"] = "refund",
            [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
            [ManualPayoutFields.QueuedAt] = queuedAt,
            [ManualPayoutFields.Op] = ManualPayoutFields.OpRefund,
            [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported,
            [ManualPayoutFields.FromStatus] = "escrowed",
            [ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending
        });
        return escrowId;
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string escrowId)
        => (await Escrows.GetByIdAsync(escrowId, consistentRead: true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>The escrow as <c>ManualPayoutIndex</c> returns it to the hourly report (every page, the tables are shared).</summary>
    private async Task<Dictionary<string, object>?> FromIndexAsync(string escrowId)
    {
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var (items, next) = await Escrows.QueryOpenManualPayoutsAsync(50, startKey);
            var found = items.FirstOrDefault(i => Str(i, "escrowId") == escrowId);
            if (found != null)
            {
                return found;
            }

            startKey = next;
        } while (startKey != null);

        return null;
    }

    [LocalDynamoDbFact]
    public async Task TheEscrowMarker_LandsOncePerQueueEntry_WithoutMovingTheVersion()
    {
        var queuedAt = HoursAgo(30);
        var escrowId = await SeedQueuedRefundAsync("once", queuedAt);

        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(escrowId, CreatedAt, queuedAt)).Should().BeTrue();
        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(escrowId, CreatedAt, queuedAt)).Should().BeFalse();

        var escrow = await EscrowAsync(escrowId);
        Str(escrow, ManualPayoutFields.OverdueLoggedFor).Should().Be(queuedAt);
        Convert.ToInt32(escrow["version"]).Should().Be(3);
        Str(escrow, "updatedAt").Should().Be(UpdatedAt);
        Str(escrow, ManualPayoutFields.Marker).Should().Be(ManualPayoutFields.MarkerOpen);

        // The report reads the marker through the index, so the next hourly run skips the entry without a write.
        var indexed = await FromIndexAsync(escrowId);
        indexed.Should().NotBeNull();
        Str(indexed!, ManualPayoutFields.OverdueLoggedFor).Should().Be(queuedAt);
    }

    [LocalDynamoDbFact]
    public async Task TheEscrowMarker_NeedsTheEntryStillOpen_WithTheQueueTimeRead()
    {
        var queuedAt = HoursAgo(26);
        var closed = await SeedQueuedRefundAsync("closed", queuedAt);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = new() { S = closed }, ["createdAt"] = new() { S = CreatedAt } },
            UpdateExpression = "REMOVE #marker",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#marker"] = ManualPayoutFields.Marker }
        });

        // The last leg was recorded between the report's read and its write: nothing is marked or announced.
        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(closed, CreatedAt, queuedAt)).Should().BeFalse();
        Str(await EscrowAsync(closed), ManualPayoutFields.OverdueLoggedFor).Should().BeNull();

        // A queue time the row no longer has (the report read an older entry) is refused too.
        var open = await SeedQueuedRefundAsync("stale", queuedAt);
        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(open, CreatedAt, HoursAgo(40))).Should().BeFalse();
        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(NewId("missing"), CreatedAt, queuedAt)).Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task AnEntryQueuedAgainLater_IsMarkedAgain()
    {
        var firstQueuedAt = HoursAgo(80);
        var escrowId = await SeedQueuedRefundAsync("again", firstQueuedAt);
        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(escrowId, CreatedAt, firstQueuedAt)).Should().BeTrue();

        // The entry closed and a later one opened on the same row (the marker of the first one stays).
        var secondQueuedAt = HoursAgo(25);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = new() { S = escrowId }, ["createdAt"] = new() { S = CreatedAt } },
            UpdateExpression = "SET #queuedAt = :queuedAt",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#queuedAt"] = ManualPayoutFields.QueuedAt },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":queuedAt"] = new() { S = secondQueuedAt } }
        });

        (await Escrows.TryMarkManualPayoutOverdueLoggedAsync(escrowId, CreatedAt, secondQueuedAt)).Should().BeTrue();
        Str(await EscrowAsync(escrowId), ManualPayoutFields.OverdueLoggedFor).Should().Be(secondQueuedAt);
    }

    [LocalDynamoDbFact]
    public async Task TheLedgerMarker_LandsOncePerStayInManual_WithoutMovingTheRowVersion()
    {
        var escrowId = NewId("ledger");
        var dueAt = HoursAgo(25);
        const string reference = LedgerRefs.ReleaseFarmer;
        var written = await Ledger.PutRowAsync(
            new LedgerRow
            {
                EscrowId = escrowId,
                Ref = reference,
                Kind = LedgerFields.KindRelease,
                Legs = new[] { LedgerEscrowFields.LegCrop },
                PayeeRole = LedgerFields.PayeeFarmer,
                PayeeId = "farmer-f06",
                Amount = 1_950m,
                Status = LedgerFields.StatusManual,
                GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
                ManualReason = LedgerFields.ManualPayoutNotSupported,
                Reason = "delivery_confirmed",
                Actor = "system",
                CreatedAt = CreatedAt,
                UpdatedAt = dueAt,
                DueAt = dueAt,
                Version = 4
            },
            new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(dueAt, reference, "ledger_row_manual"),
                "ledger_row_manual", LedgerFields.StatusPending, LedgerFields.StatusManual, "system", $"ref={reference}"));
        written.Should().Be(LedgerWrite.Written);

        (await Ledger.TryMarkManualOverdueLoggedAsync(escrowId, reference, HoursAgo(30))).Should().BeFalse(); // another stay
        (await Ledger.TryMarkManualOverdueLoggedAsync(escrowId, reference, dueAt)).Should().BeTrue();
        (await Ledger.TryMarkManualOverdueLoggedAsync(escrowId, reference, dueAt)).Should().BeFalse();
        (await Ledger.TryMarkManualOverdueLoggedAsync(escrowId, "release#nothing", dueAt)).Should().BeFalse();

        var row = (await Ledger.GetRowAsync(escrowId, reference))!;
        row.ManualOverdueLoggedFor.Should().Be(dueAt);
        row.Version.Should().Be(4);
        row.UpdatedAt.Should().Be(dueAt);
        row.Status.Should().Be(LedgerFields.StatusManual);

        // The ledger sweep's overdue report reads the manual rows through LedgerOpenIndex and sees the marker.
        Dictionary<string, AttributeValue>? startKey = null;
        LedgerRow? indexed = null;
        do
        {
            var (rows, next) = await Ledger.QueryOpenAsync(LedgerFields.StatusManual, HoursAgo(24), 100, startKey);
            indexed = rows.FirstOrDefault(r => r.EscrowId == escrowId) ?? indexed;
            startKey = next;
        } while (startKey != null && indexed == null);

        indexed.Should().NotBeNull();
        indexed!.ManualOverdueLoggedFor.Should().Be(dueAt);
    }

    [LocalDynamoDbFact]
    public async Task TheLedgerMarker_IsRefusedOnARowThatLeftManual()
    {
        var escrowId = NewId("done");
        var dueAt = HoursAgo(26);
        const string reference = LedgerRefs.RefundAll;
        var written = await Ledger.PutRowAsync(
            new LedgerRow
            {
                EscrowId = escrowId,
                Ref = reference,
                Kind = LedgerFields.KindRefund,
                PayeeRole = LedgerFields.PayeeBuyer,
                PayeeId = "buyer-f06",
                Amount = 2_000m,
                Status = LedgerFields.StatusPending,
                GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
                Reason = "cancelled",
                Actor = "system",
                CreatedAt = CreatedAt,
                UpdatedAt = dueAt,
                DueAt = dueAt
            },
            new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(dueAt, reference, "ledger_row_pending"),
                "ledger_row_pending", "", LedgerFields.StatusPending, "system", $"ref={reference}"));
        written.Should().Be(LedgerWrite.Written);

        (await Ledger.TryMarkManualOverdueLoggedAsync(escrowId, reference, dueAt)).Should().BeFalse();
        (await Ledger.GetRowAsync(escrowId, reference))!.ManualOverdueLoggedFor.Should().BeNull();
    }
}
