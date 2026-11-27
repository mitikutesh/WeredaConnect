using System.Globalization;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-05 (escrow-protocol.md 2.12.0): an admin's claim on one manual payout leg, so two admins never
/// pay the same leg in the Telebirr merchant portal. A 1.x leg keeps its claim on the escrow row
/// (<c>manual{Leg}ClaimedBy</c>, <c>ClaimedAt</c>, <c>ClaimExpiresAt</c>), a ledger row on itself
/// (<c>claimedBy</c>, <c>claimedAt</c>, <c>claimExpiresAt</c>). A claim is taken and renewed by a
/// conditional write (no live claim of another admin), ends on its own at <see cref="Ttl"/>, is removed by
/// its holder or by the write that records the leg, and is server bookkeeping: no escrow or row version
/// moves with it.
/// </summary>
internal static class ManualPayoutClaimFields
{
    /// <summary>How long a claim lasts from its last claim or renewal; nobody can take a live claim over.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the queue keeps showing a claim that lapsed (not released, not recorded), so other admins see
    /// that someone may have paid the leg and check the portal first. The row keeps the attributes anyway.
    /// </summary>
    public static readonly TimeSpan ShownAfterExpiry = TimeSpan.FromHours(24);

    public const string LedgerClaimedBy = "claimedBy";
    public const string LedgerClaimedAt = "claimedAt";
    public const string LedgerClaimExpiresAt = "claimExpiresAt";

    public static string ClaimedByAttr(string leg) => $"manual{Capitalise(leg)}ClaimedBy";
    public static string ClaimedAtAttr(string leg) => $"manual{Capitalise(leg)}ClaimedAt";
    public static string ClaimExpiresAtAttr(string leg) => $"manual{Capitalise(leg)}ClaimExpiresAt";

    /// <summary>
    /// True when another admin holds a claim that has not expired at <paramref name="nowIso"/>. ISO 8601 UTC
    /// times of the same "o" format compare as strings, as the write conditions compare them.
    /// </summary>
    public static bool IsHeldByOther(string? claimedBy, string? claimExpiresAt, string actor, string nowIso)
        => !string.IsNullOrEmpty(claimedBy)
           && !string.Equals(claimedBy, actor, StringComparison.Ordinal)
           && IsLive(claimExpiresAt, nowIso);

    /// <summary>A claim whose expiry is still ahead of <paramref name="nowIso"/>.</summary>
    public static bool IsLive(string? claimExpiresAt, string nowIso)
        => !string.IsNullOrEmpty(claimExpiresAt) && string.CompareOrdinal(claimExpiresAt, nowIso) > 0;

    private static string Capitalise(string leg)
        => leg.Length == 0 ? leg : char.ToUpperInvariant(leg[0]) + leg[1..];
}

/// <summary>
/// FUP-05: one Telebirr merchant-portal reference is recorded on one payout leg only. Each manual record
/// writes a reference item in escrow-idempotency (key <c>payout-ref#{REFERENCE}</c>, the trimmed reference in
/// upper case) in the same transaction as the leg, conditioned on the key not existing, so two records of
/// one reference cannot both commit. The item names where the reference was used. It expires with the
/// table's other long-lived rows (account-deletion.md: every escrow-idempotency row within 400 days).
/// </summary>
internal static class PayoutReferenceFields
{
    public const string KeyPrefix = "payout-ref#";
    public const string Kind = "payout_ref";
    public const int TtlDays = 400;

    public const string EscrowId = "escrowId";
    public const string Ref = "ref";
    public const string ExternalRef = "externalRef";
    public const string RecordedBy = "recordedBy";
    public const string RecordedAt = "recordedAt";

    /// <summary>
    /// The reference as the uniqueness key compares it: trimmed (as the record call's validation) and upper
    /// case, so the same portal id typed in another case is the same reference.
    /// </summary>
    public static string Normalize(string externalRef) => externalRef.Trim().ToUpperInvariant();

    public static string Key(string externalRef) => KeyPrefix + Normalize(externalRef);

    /// <summary>The reference item's conditional put, one item of the record's transaction.</summary>
    public static TransactWriteItem BuildPut(PayoutReferenceRecord record)
    {
        var recordedAt = DateTime.Parse(record.RecordedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
        var expiresAt = new DateTimeOffset(recordedAt).AddDays(TtlDays).ToUnixTimeSeconds();
        return new TransactWriteItem
        {
            Put = new Put
            {
                TableName = record.Table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["idempotencyKey"] = new() { S = Key(record.ExternalRef) },
                    ["kind"] = new() { S = Kind },
                    [EscrowId] = new() { S = record.EscrowId },
                    [Ref] = new() { S = record.Ref },
                    [ExternalRef] = new() { S = record.ExternalRef },
                    [RecordedBy] = new() { S = record.RecordedBy },
                    [RecordedAt] = new() { S = record.RecordedAt },
                    ["expiresAt"] = new() { N = expiresAt.ToString(CultureInfo.InvariantCulture) }
                },
                ConditionExpression = "attribute_not_exists(#key)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#key"] = "idempotencyKey" }
            }
        };
    }

    /// <summary>
    /// FUP-218: the delete of a reference item, one item of a correction's transaction, only while the item still
    /// names <paramref name="escrowId"/> and <paramref name="reference"/>: an item another leg holds is never removed.
    /// </summary>
    public static TransactWriteItem BuildDelete(string table, string externalRef, string escrowId, string reference) => new()
    {
        Delete = new Delete
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = new() { S = Key(externalRef) } },
            ConditionExpression = "#escrow = :escrow AND #ref = :ref",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#escrow"] = EscrowId, ["#ref"] = Ref },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":escrow"] = new() { S = escrowId },
                [":ref"] = new() { S = reference }
            }
        }
    };
}

/// <summary>FUP-05: the reference item a manual record writes with its leg (see <see cref="PayoutReferenceFields"/>).</summary>
/// <param name="Table">escrow-idempotency.</param>
/// <param name="ExternalRef">The trimmed reference as the admin entered it.</param>
/// <param name="EscrowId">The escrow of the leg.</param>
/// <param name="Ref">The 1.x leg name or the ledger ref.</param>
/// <param name="RecordedBy">The admin id.</param>
/// <param name="RecordedAt">ISO time of the record (the leg's paid time).</param>
public sealed record PayoutReferenceRecord(
    string Table, string ExternalRef, string EscrowId, string Ref, string RecordedBy, string RecordedAt);

/// <summary>FUP-05: where a reference is already recorded.</summary>
public sealed record PayoutReferenceHolder(string EscrowId, string Ref, string? RecordedBy, string? RecordedAt);

/// <summary>
/// FUP-05: a manual record's transaction failed only on its reference item: the reference is recorded on
/// another leg already. Thrown by the 1.x record writes; the caller reads where and refuses.
/// </summary>
public sealed class PayoutReferenceTakenException : Exception
{
    public PayoutReferenceTakenException(string externalRef)
        : base("The payout reference is already recorded on another leg.")
    {
        ExternalRef = externalRef;
    }

    public string ExternalRef { get; }
}

/// <summary>
/// FUP-05: what a ledger row's manual record adds to its transaction: the row must carry no live claim of
/// another admin (the record ends the claim), and the reference item goes with it.
/// </summary>
/// <param name="Actor">The admin recording the leg.</param>
/// <param name="Now">ISO time the claim condition is judged at.</param>
/// <param name="Reference">The reference item, or null where no reference table is wired (tests).</param>
internal sealed record ManualRecordGuard(string Actor, string Now, PayoutReferenceRecord? Reference);
