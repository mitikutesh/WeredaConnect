using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-20: the escrow ledger table and the writes that move a ledger escrow (escrow-protocol.md 2.2.0,
/// P.3 and P.5). Every write is conditional: a row on its own <c>version</c>, the escrow on its
/// <c>version</c>. Reads are a Query on the escrow's partition or on <c>LedgerOpenIndex</c>, never a Scan.
/// Every row status write puts its escrow-audit row in the same transaction (P.7), so no movement goes
/// unaudited.
/// </summary>
internal interface IEscrowLedgerRepository
{
    /// <summary>Every row of one escrow, strongly consistent.</summary>
    Task<List<LedgerRow>> GetRowsAsync(string escrowId);

    /// <summary>
    /// ESC-24: one page of an escrow's rows in ref order (a Query on the escrow's partition, strongly
    /// consistent), at most <paramref name="limit"/> (1 to 100). Read only, for the admin ledger view.
    /// </summary>
    Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> GetRowsPageAsync(
        string escrowId, int limit, Dictionary<string, AttributeValue>? startKey);

    /// <summary>One row, strongly consistent; null when it does not exist.</summary>
    Task<LedgerRow?> GetRowAsync(string escrowId, string reference);

    /// <summary>
    /// P.5 step 1: put every row of <paramref name="decision"/> <c>pending</c> (each only if its ref is new)
    /// and update the escrow (legs, amounts, totals, version + 1) conditioned on the version read, the
    /// status the caller validated and the moving legs still being <c>held</c>, in one transaction.
    /// </summary>
    Task<LedgerWrite> DecideAsync(LedgerDecision decision);

    /// <summary>
    /// P.9: put one row <c>pending</c> without touching the escrow (a duplicate payment is outside
    /// <c>amount</c>), only if its ref is new.
    /// </summary>
    Task<LedgerWrite> PutRowAsync(LedgerRow row, LedgerAudit audit);

    /// <summary>
    /// P.5 step 2: stamp <c>callingSince</c> just before a gateway call, only while the row is still
    /// <c>pending</c> or <c>failed</c>, has no stamp and has the version read. The stamp also moves
    /// <c>dueAt</c> so the sweep looks at the row again after <see cref="LedgerFields.RecheckAfter"/>.
    /// </summary>
    Task<bool> TryStampCallingAsync(LedgerRow row, string now, string dueAt);

    /// <summary>
    /// A row's status write after a call or a status check (<c>sent</c>, <c>failed</c>, <c>manual</c>,
    /// <c>done</c>, or a deferral), conditioned on the row's version, with its audit rows and, for a row of
    /// <c>amount</c> that becomes <c>done</c>, the escrow's status write conditioned on the escrow version.
    /// </summary>
    Task<LedgerWrite> TransitionAsync(LedgerTransition transition);

    /// <summary>
    /// One page of <c>LedgerOpenIndex</c>: rows in <paramref name="openStatus"/> whose <c>dueAt</c> is at
    /// or before <paramref name="dueBefore"/>, oldest first.
    /// </summary>
    Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryOpenAsync(
        string openStatus, string dueBefore, int limit, Dictionary<string, AttributeValue>? startKey);

    /// <summary>
    /// F2: one page of ledger escrows in <paramref name="status"/> (<c>StatusExpiresIndex</c>, oldest first) whose
    /// crop leg is decided and whose transport leg is still held: the trucker failed the identity gate at the
    /// release. The hourly retry pays them once the gate passes.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? Next)> QueryOwedTransportLegsAsync(
        string status, Dictionary<string, AttributeValue>? startKey);
}

/// <summary>The outcome of a ledger write.</summary>
internal enum LedgerWrite
{
    Written,

    /// <summary>A row with this ref already exists: the movement was decided before.</summary>
    RowExists,

    /// <summary>The row moved on since it was read (its version or status differs).</summary>
    RowConflict,

    /// <summary>The escrow moved on since it was read (its version, status or a leg differs).</summary>
    EscrowConflict,

    /// <summary>FUP-05: only the reference item of a manual record failed: the reference is recorded on another leg.</summary>
    ReferenceTaken
}

/// <summary>An escrow-audit row written in the same transaction as the ledger write it records (P.7).</summary>
internal sealed record LedgerAudit(
    string EscrowId, string Timestamp, string Action, string FromState, string ToState, string Actor, string Metadata,
    string? TelebirrRef = null);

/// <summary>P.5 step 1: the rows of one decision and the escrow update that goes with them.</summary>
internal sealed record LedgerDecision(
    LedgerEscrow Before,
    LedgerEscrow After,
    IReadOnlyList<LedgerRow> Rows,
    IReadOnlyList<LedgerAudit> Audits,
    IReadOnlyDictionary<string, object>? ExtraEscrowFields = null);

/// <summary>
/// A row status write. <see cref="EscrowStatus"/> is set when the escrow moves with this row (the write
/// that makes the deciding row <c>done</c>); <see cref="EscrowVersion"/> is the escrow version read, the
/// condition of every escrow write here (null leaves the escrow alone).
/// </summary>
internal sealed record LedgerTransition
{
    public required LedgerRow Before { get; init; }
    public required LedgerRow After { get; init; }
    public required IReadOnlyList<LedgerAudit> Audits { get; init; }

    /// <summary>The escrow's key and the version read; null when the escrow is not written.</summary>
    public (string EscrowId, string CreatedAt, int Version)? Escrow { get; init; }
    public string? EscrowStatus { get; init; }
    public IReadOnlyDictionary<string, object>? EscrowFields { get; init; }

    /// <summary>
    /// FUP-05: an admin's manual record. The row update also requires that no other admin holds a live claim
    /// on the row and removes the claim, and the reference item is written in the same transaction.
    /// </summary>
    public ManualRecordGuard? ManualRecord { get; init; }
}
