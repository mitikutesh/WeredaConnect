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
    /// </summary>
    Task PutNonceAsync(string deliveryId, string nonce);

    /// <summary>Read the secret record for a delivery, or null if the farmer hasn't synced it yet.</summary>
    Task<DeliveryQrSecret?> GetAsync(string deliveryId);

    /// <summary>
    /// Record single-use consumption of the nonce for a specific target status
    /// (e.g. "pickedUp" or "delivered") by a specific operation. Called AFTER the
    /// version CAS succeeds so a failed transition never burns the nonce.
    /// </summary>
    Task MarkConsumedAsync(string deliveryId, string targetStatus, string operationId);
}

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
}
