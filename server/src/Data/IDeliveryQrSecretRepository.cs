namespace WeredaConnect.Server.Data;

/// <summary>
/// The per-delivery QR handoff secret (the pickup nonce), kept in a dedicated table
/// SEPARATE from the delivery record so it can never leak through any delivery
/// sync-down / GSI / DTO / conflict-echo path (non-leakage true by construction).
///
/// The farmer's device generates the nonce offline and persists it here (write-only
/// from the client's perspective — it is never returned to any client). The server
/// compares a scanner's echoed nonce against this record to authorize a
/// pickedUp/delivered transition, enforces a freshness window via <c>IssuedAt</c>,
/// and records single-use consumption per target status.
///
/// Table (DeliveryQrSecrets): PK deliveryId (STRING), TTL expiresAt (NUMBER, cleanup only).
/// </summary>
public interface IDeliveryQrSecretRepository
{
    /// <summary>
    /// Store (or refresh) the pickup nonce for a delivery. Idempotent: re-persisting the
    /// same stable nonce is a no-op-ish upsert that preserves any consumption markers.
    /// A different nonce (the farmer's Regenerate) also clears the typed-code misses of
    /// both legs: the old code is useless once the nonce changed.
    /// </summary>
    Task PutNonceAsync(string deliveryId, string nonce);

    /// <summary>
    /// ESC-35: store the trucker's drop-off nonce next to the farmer's pickup nonce; the buyer's
    /// <c>delivered</c> is then checked against it. Only a row that holds the farmer's nonce takes
    /// it (every delivery picked up since FRD-11 has one). A registration (<paramref name="renew"/>
    /// false: the code made at pickup, or sent again when the drop-off QR opens) is stored only
    /// while no drop-off code is; a different stored code stays. Only the trucker's renewal (New
    /// drop-off code) replaces it, restarting its freshness and clearing the drop-off leg's
    /// typed-code misses. The same nonce again keeps both either way.
    /// </summary>
    Task<DropoffNonceOutcome> PutDropoffNonceAsync(string deliveryId, string nonce, bool renew);

    /// <summary>
    /// Take one typed-code attempt of <paramref name="targetStatus"/> ("pickedUp" or
    /// "delivered") BEFORE the code is compared (T-1). <paramref name="attemptId"/> names the
    /// operation and the code (<c>HandoffCode.AttemptId</c>). One conditional write, so parallel
    /// guesses cannot pass the cap: taken while the leg holds fewer than
    /// <paramref name="maxAttempts"/> attempts, or when this very attempt is already held (a retry
    /// of the same operation with the same code), and only while the stored nonce is still
    /// <paramref name="nonce"/> (the trucker's drop-off nonce when <paramref name="dropoffNonce"/>,
    /// ESC-35). A taken attempt that turns out wrong stays as a miss; a right one is given back
    /// with <see cref="ReturnCodeAttemptAsync"/>.
    /// </summary>
    Task<CodeAttempt> TryTakeCodeAttemptAsync(
        string deliveryId, string targetStatus, string attemptId, string nonce, int maxAttempts,
        bool dropoffNonce = false);

    /// <summary>The typed code was right: give its attempt back, so it never counts as a miss.</summary>
    Task ReturnCodeAttemptAsync(string deliveryId, string targetStatus, string attemptId);

    /// <summary>Read the secret record for a delivery, or null if the farmer hasn't synced it yet.</summary>
    Task<DeliveryQrSecret?> GetAsync(string deliveryId);

    /// <summary>
    /// Record single-use consumption of the nonce for a specific target status
    /// (e.g. "pickedUp" or "delivered") by a specific operation. Called AFTER the
    /// version CAS succeeds so a failed transition never burns the nonce.
    /// </summary>
    Task MarkConsumedAsync(string deliveryId, string targetStatus, string operationId);
}

/// <summary>What became of a trucker's drop-off nonce (<see cref="IDeliveryQrSecretRepository.PutDropoffNonceAsync"/>).</summary>
public enum DropoffNonceOutcome
{
    /// <summary>Stored: the first drop-off code, or a renewal.</summary>
    Stored,

    /// <summary>This very code was stored already; only the row's TTL moved.</summary>
    AlreadyStored,

    /// <summary>Not stored: another drop-off code is stored and this was no renewal.</summary>
    OtherCodeStored,

    /// <summary>Not stored: the row holds no farmer's nonce (no pickup this store proved).</summary>
    NoPickupNonce
}

/// <summary>How a typed-code attempt went (<see cref="IDeliveryQrSecretRepository.TryTakeCodeAttemptAsync"/>).</summary>
public enum CodeAttemptOutcome
{
    /// <summary>The attempt was taken; <see cref="CodeAttempt.Count"/> holds the leg's attempts with it.</summary>
    Taken,

    /// <summary>The leg's attempts are used up.</summary>
    Locked,

    /// <summary>The stored nonce is no longer the one the code was checked against (regenerated meanwhile).</summary>
    NonceChanged
}

/// <summary>The outcome of a typed-code attempt and the leg's attempt count after it.</summary>
public readonly record struct CodeAttempt(CodeAttemptOutcome Outcome, int Count);

/// <summary>Immutable view of a delivery's QR secret record.</summary>
public sealed record DeliveryQrSecret
{
    public required string DeliveryId { get; init; }
    public required string Nonce { get; init; }

    /// <summary>When the farmer generated/persisted the nonce (ISO 8601), for freshness.</summary>
    public string? IssuedAt { get; init; }

    /// <summary>ESC-35: the trucker's drop-off nonce, or null while none is stored.</summary>
    public string? DropoffNonce { get; init; }

    /// <summary>When the current drop-off nonce was stored (ISO 8601), for its freshness.</summary>
    public string? DropoffIssuedAt { get; init; }

    /// <summary>OperationId that consumed the nonce for the "pickedUp" transition, if any.</summary>
    public string? ConsumedPickedUpOp { get; init; }

    /// <summary>OperationId that consumed the nonce for the "delivered" transition, if any.</summary>
    public string? ConsumedDeliveredOp { get; init; }

    /// <summary>The operation that already consumed the nonce for <paramref name="targetStatus"/>, or null.</summary>
    public string? ConsumedOpFor(string targetStatus) => targetStatus switch
    {
        "pickedUp" => ConsumedPickedUpOp,
        "delivered" => ConsumedDeliveredOp,
        _ => null
    };

    /// <summary>Wrong typed codes counted for the "pickedUp" leg since the nonce was stored.</summary>
    public int CodeMissesPickedUp { get; init; }

    /// <summary>Wrong typed codes counted for the "delivered" leg since the nonce was stored.</summary>
    public int CodeMissesDelivered { get; init; }

    /// <summary>The wrong typed codes counted for <paramref name="targetStatus"/>.</summary>
    public int CodeMissesFor(string targetStatus) => targetStatus switch
    {
        "pickedUp" => CodeMissesPickedUp,
        "delivered" => CodeMissesDelivered,
        _ => 0
    };
}
