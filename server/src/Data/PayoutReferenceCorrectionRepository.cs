using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-218 (escrow-protocol.md 2.18.0): the one transaction that replaces the portal reference of a manual payout
/// leg an admin recorded by mistake. It writes the leg's reference (the 1.x <c>manual{Leg}Ref</c>, or the ledger
/// row's <c>telebirrRef</c>) and the escrow's settlement reference field when that field holds the old reference,
/// removes the old <c>payout-ref#</c> item when this leg holds it, puts the new one only if no leg holds it, and
/// writes the escrow-audit row, all or nothing. Every item is conditioned on what the caller read, so a write that
/// lands in between makes the whole transaction fail and the caller reads again. No amount or status is written.
/// </summary>
internal interface IPayoutReferenceCorrectionRepository
{
    Task<PayoutReferenceCorrectionWrite> CorrectAsync(PayoutReferenceCorrection correction);
}

/// <summary>The outcome of a correction's transaction.</summary>
internal enum PayoutReferenceCorrectionWrite
{
    Written,

    /// <summary>The leg, the escrow or the old reference item changed since it was read, or the items were busy: nothing was written.</summary>
    Changed,

    /// <summary>Only the new reference item failed: another leg holds the new reference. Nothing was written.</summary>
    ReferenceTaken
}

/// <summary>A planned correction: what the caller read and what the transaction writes.</summary>
internal sealed record PayoutReferenceCorrection
{
    public required string EscrowId { get; init; }

    /// <summary>The 1.x leg name or the ledger ref.</summary>
    public required string Ref { get; init; }

    /// <summary>The reference the leg carries, as read (the condition of the leg's write).</summary>
    public required string OldRef { get; init; }

    /// <summary>The trimmed reference that replaces it.</summary>
    public required string NewRef { get; init; }

    /// <summary>The admin who recorded the leg, as read; the leg's write requires it unchanged.</summary>
    public required string PaidBy { get; init; }

    /// <summary>ISO time of the correction (the escrow's <c>updatedAt</c>).</summary>
    public required string Now { get; init; }

    /// <summary>A 1.x leg: the escrow row as read. Null for a ledger row.</summary>
    public LegacyLegRead? Legacy { get; init; }

    /// <summary>A ledger row: its version as read, and the escrow when its settlement reference is written. Null for a 1.x leg.</summary>
    public LedgerRowRead? Ledger { get; init; }

    /// <summary>
    /// The escrow's settlement reference field that holds the old reference (<c>telebirrReleaseRef</c>,
    /// <c>truckerTelebirrRef</c> or <c>telebirrRefundRef</c>), written with the leg; null when it holds anything
    /// else (the queue still open, or another row's reference).
    /// </summary>
    public string? SettlementRefField { get; init; }

    /// <summary>The old reference item names this leg and goes; otherwise it is left alone.</summary>
    public required bool DeleteOldItem { get; init; }

    public required PayoutReferenceRecord NewItem { get; init; }

    public required LedgerAudit Audit { get; init; }
}

/// <param name="CreatedAt">The escrow's sort key.</param>
/// <param name="Status">The escrow status read; the write requires it unchanged.</param>
/// <param name="Version">The escrow version read; null on a row written before versions (1.38.0).</param>
internal sealed record LegacyLegRead(string CreatedAt, string Status, int? Version);

/// <param name="RowVersion">The ledger row's version read.</param>
/// <param name="EscrowCreatedAt">The escrow's sort key, when its settlement reference field is written.</param>
/// <param name="EscrowVersion">The escrow version read, when its settlement reference field is written.</param>
internal sealed record LedgerRowRead(int RowVersion, string? EscrowCreatedAt = null, int? EscrowVersion = null);

internal sealed class PayoutReferenceCorrectionRepository : IPayoutReferenceCorrectionRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _escrowsTable;
    private readonly string? _ledgerTable;
    private readonly string _referenceTable;
    private readonly string _auditTable;

    public PayoutReferenceCorrectionRepository(
        IAmazonDynamoDB dynamoDb, string escrowsTable, string? ledgerTable, string referenceTable, string auditTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _escrowsTable = escrowsTable;
        _ledgerTable = string.IsNullOrEmpty(ledgerTable) ? null : ledgerTable;
        _referenceTable = referenceTable;
        _auditTable = auditTable;
    }

    /// <inheritdoc />
    public async Task<PayoutReferenceCorrectionWrite> CorrectAsync(PayoutReferenceCorrection correction)
    {
        var items = new List<TransactWriteItem>();
        if (correction.Legacy is { } legacy)
        {
            items.Add(new TransactWriteItem { Update = LegacyLegUpdate(correction, legacy) });
        }
        else if (correction.Ledger is { } ledger)
        {
            items.Add(new TransactWriteItem { Update = LedgerRowUpdate(correction, ledger) });
            if (correction.SettlementRefField != null && ledger.EscrowCreatedAt != null && ledger.EscrowVersion != null)
            {
                items.Add(new TransactWriteItem { Update = LedgerEscrowUpdate(correction, ledger) });
            }
        }
        else
        {
            throw new ArgumentException("A correction names a 1.x leg or a ledger row.", nameof(correction));
        }

        if (correction.DeleteOldItem)
        {
            items.Add(PayoutReferenceFields.BuildDelete(_referenceTable, correction.OldRef, correction.EscrowId, correction.Ref));
        }

        var putIndex = items.Count;
        items.Add(PayoutReferenceFields.BuildPut(correction.NewItem));
        items.Add(EscrowLedgerRepository.AuditPut(_auditTable, correction.Audit));

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
            return PayoutReferenceCorrectionWrite.Written;
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            var failed = Enumerable.Range(0, reasons.Count)
                .Where(i => reasons[i]?.Code == "ConditionalCheckFailed")
                .ToList();
            // Everything else held and only the new item exists: the new reference is recorded on another leg.
            return failed.Count == 1 && failed[0] == putIndex
                ? PayoutReferenceCorrectionWrite.ReferenceTaken
                : PayoutReferenceCorrectionWrite.Changed;
        }
    }

    /// <summary>
    /// The 1.x leg on the escrow row: the leg still paid with the old reference by the admin read, the escrow in the
    /// status and at the version read (no amount or status moved since), and the settlement field still the old
    /// reference when it is written. The version goes up by one (1.38.0): an escrow read returns the references.
    /// </summary>
    private Update LegacyLegUpdate(PayoutReferenceCorrection c, LegacyLegRead legacy)
    {
        var leg = c.Ref;
        var names = new Dictionary<string, string>
        {
            ["#legStatus"] = ManualPayoutFields.StatusAttr(leg),
            ["#legRef"] = ManualPayoutFields.RefAttr(leg),
            ["#legPaidBy"] = ManualPayoutFields.PaidByAttr(leg),
            ["#status"] = "status",
            ["#updated"] = "updatedAt",
            ["#version"] = "version"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":paid"] = S(ManualPayoutFields.LegPaid),
            [":old"] = S(c.OldRef),
            [":new"] = S(c.NewRef),
            [":paidBy"] = S(c.PaidBy),
            [":status"] = S(legacy.Status),
            [":now"] = S(c.Now),
            [":one"] = N(1)
        };
        var sets = "#legRef = :new, #updated = :now";
        var condition = "#legStatus = :paid AND #legRef = :old AND #legPaidBy = :paidBy AND #status = :status";
        if (legacy.Version is { } version)
        {
            values[":v"] = N(version);
            condition += " AND #version = :v";
        }
        else
        {
            condition += " AND attribute_not_exists(#version)";
        }

        if (c.SettlementRefField != null)
        {
            names["#settlementRef"] = c.SettlementRefField;
            sets += ", #settlementRef = :new";
            condition += " AND #settlementRef = :old";
        }

        return new Update
        {
            TableName = _escrowsTable,
            Key = EscrowKey(c.EscrowId, legacy.CreatedAt),
            UpdateExpression = "SET " + sets + " ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    /// <summary>
    /// The ledger row: still <c>done</c> with the old reference by the admin read, at the version read (P.3: every
    /// row write is conditioned on its version and adds one). <c>updatedAt</c> stays: it stands in for
    /// <c>doneAt</c> on rows without one (the backfill's record time, the queue's paid time).
    /// </summary>
    private Update LedgerRowUpdate(PayoutReferenceCorrection c, LedgerRowRead ledger) => new()
    {
        TableName = _ledgerTable ?? throw new InvalidOperationException("The escrow ledger table is not configured."),
        Key = new Dictionary<string, AttributeValue>
        {
            [LedgerFields.EscrowId] = S(c.EscrowId),
            [LedgerFields.Ref] = S(c.Ref)
        },
        UpdateExpression = "SET #tref = :new ADD #version :one",
        ConditionExpression = "#version = :v AND #status = :done AND #tref = :old AND #paidBy = :paidBy",
        ExpressionAttributeNames = new Dictionary<string, string>
        {
            ["#tref"] = LedgerFields.TelebirrRef,
            ["#version"] = LedgerFields.Version,
            ["#status"] = LedgerFields.Status,
            ["#paidBy"] = LedgerFields.PaidBy
        },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":new"] = S(c.NewRef),
            [":old"] = S(c.OldRef),
            [":v"] = N(ledger.RowVersion),
            [":done"] = S(LedgerFields.StatusDone),
            [":paidBy"] = S(c.PaidBy),
            [":one"] = N(1)
        }
    };

    /// <summary>
    /// A ledger escrow's settlement reference field, when it holds the row's old reference: conditioned on the escrow
    /// version read, as every escrow write of the ledger (2.2.0), so a row that becomes done at the same time
    /// serializes with the correction.
    /// </summary>
    private Update LedgerEscrowUpdate(PayoutReferenceCorrection c, LedgerRowRead ledger) => new()
    {
        TableName = _escrowsTable,
        Key = EscrowKey(c.EscrowId, ledger.EscrowCreatedAt!),
        UpdateExpression = "SET #settlementRef = :new, #updated = :now ADD #version :one",
        ConditionExpression = "#version = :v AND #settlementRef = :old",
        ExpressionAttributeNames = new Dictionary<string, string>
        {
            ["#settlementRef"] = c.SettlementRefField!,
            ["#updated"] = "updatedAt",
            ["#version"] = "version"
        },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":new"] = S(c.NewRef),
            [":old"] = S(c.OldRef),
            [":now"] = S(c.Now),
            [":v"] = N(ledger.EscrowVersion!.Value),
            [":one"] = N(1)
        }
    };

    private static Dictionary<string, AttributeValue> EscrowKey(string escrowId, string createdAt) => new()
    {
        ["escrowId"] = S(escrowId),
        ["createdAt"] = S(createdAt)
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}
