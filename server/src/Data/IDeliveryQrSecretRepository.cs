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
    /// Take one typed-code attempt of <paramref name="targetStatus"/> ("pickedUp" or
    /// "delivered") for <paramref name="operationId"/> BEFORE the code is compared (T-1). One
    /// conditional write, so parallel guesses cannot pass the cap: taken while the leg holds
    /// fewer than <paramref name="maxAttempts"/> operation ids, or when this operation already
    /// holds one (its retry), and only while the stored nonce is still <paramref name="nonce"/>.
    /// A taken attempt that turns out wrong stays as a miss; a right one is given back with
    /// <see cref="ReturnCodeAttemptAsync"/>.
    /// </summary>
    Task<CodeAttempt> TryTakeCodeAttemptAsync(
        string deliveryId, string targetStatus, string operationId, string nonce, int maxAttempts);

    /// <summary>The typed code was right: give its attempt back, so it never counts as a miss.</summary>
    Task ReturnCodeAttemptAsync(string deliveryId, string targetStatus, string operationId);

    /// <summary>Read the secret record for a delivery, or null if the farmer hasn't synced it yet.</summary>
    Task<DeliveryQrSecret?> GetAsync(string deliveryId);

    /// <summary>
    /// Record single-use consumption of the nonce for a specific target status
    /// (e.g. "pickedUp" or "delivered") by a specific operation. Called AFTER the
    /// version CAS succeeds so a failed transition never burns the nonce.
    /// </summary>
    Task MarkConsumedAsync(string deliveryId, string targetStatus, string operationId);
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
