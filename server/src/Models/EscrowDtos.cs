namespace WeredaConnect.Server.Models;

// ──────────────────────────────────────────────
// Request / Response DTOs (Endpoint layer)
// ──────────────────────────────────────────────

/// <summary>
/// POST /escrow/initiate request body.
/// Buyer initiates escrow payment for a delivery.
/// </summary>
public sealed record InitiateEscrowRequest
{
    /// <summary>
    /// The delivery this payment covers (UUID).
    /// </summary>
    public required string DeliveryId { get; init; }

    /// <summary>
    /// Buyer initiating payment (must match JWT sub claim).
    /// </summary>
    public required string BuyerId { get; init; }

    /// <summary>
    /// Farmer receiving payment (must be a verified user).
    /// </summary>
    public required string FarmerId { get; init; }

    /// <summary>
    /// Total payment amount in ETB (100.00 – 500,000.00).
    /// </summary>
    public required decimal Amount { get; init; }

    /// <summary>
    /// Currency code. Must be "ETB".
    /// </summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Idempotency key: {buyerId}:{deliveryId}:initiate:{timestamp}.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// POST /escrow/initiate success response (201 Created or 200 OK for duplicates).
/// </summary>
public sealed record InitiateEscrowResponse
{
    public required string EscrowId { get; init; }
    public required string DeliveryId { get; init; }
    public required string BuyerId { get; init; }
    public required string FarmerId { get; init; }
    public required decimal Amount { get; init; }
    public required decimal Commission { get; init; }
    public required decimal FarmerPayout { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public string? TelebirrRef { get; init; }
    public required string CreatedAt { get; init; }
    public required string ExpiresAt { get; init; }
    public string? Message { get; init; }
    public bool IsDuplicate { get; init; }
}

/// <summary>
/// POST /escrow/release request body.
/// Triggers release of escrowed funds to the farmer.
/// </summary>
public sealed record ReleaseEscrowRequest
{
    public required string EscrowId { get; init; }

    /// <summary>
    /// Enum: delivery_confirmed, dispute_resolved, admin_override.
    /// </summary>
    public required string Trigger { get; init; }

    public required string DeliveryId { get; init; }
    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// POST /escrow/release success response (200 OK).
/// </summary>
public sealed record ReleaseEscrowResponse
{
    public required string EscrowId { get; init; }
    public required string Status { get; init; }
    public required decimal FarmerPayout { get; init; }
    public required decimal Commission { get; init; }
    public string? TelebirrRef { get; init; }
    public required string ReleasedAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// POST /escrow/dispute request body.
/// Buyer or farmer raises a dispute within 48 hours of delivery confirmation.
/// </summary>
public sealed record DisputeEscrowRequest
{
    public required string EscrowId { get; init; }

    /// <summary>
    /// Must be buyer or farmer on this escrow (UUID).
    /// </summary>
    public required string DisputedBy { get; init; }

    /// <summary>
    /// Enum: damaged_goods, wrong_quantity, wrong_crop, not_delivered, quality_mismatch, other.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Free-text explanation (0-1000 chars, supports Amharic/Oromo/English).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// S3 references to photos/documents (max 5).
    /// </summary>
    public List<string>? EvidenceRefs { get; init; }

    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// POST /escrow/dispute success response (200 OK).
/// </summary>
public sealed record DisputeEscrowResponse
{
    public required string EscrowId { get; init; }
    public required string Status { get; init; }
    public required string DisputeId { get; init; }
    public required string DisputedBy { get; init; }
    public required string Reason { get; init; }
    public required string DisputeWindowEndsAt { get; init; }
    public required string CreatedAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// POST /escrow/refund request body.
/// Refunds escrowed funds to the buyer.
/// </summary>
public sealed record RefundEscrowRequest
{
    public required string EscrowId { get; init; }

    /// <summary>
    /// Enum: dispute_resolved, delivery_timeout, admin_override.
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary>
    /// Explanation for audit trail (0-500 chars).
    /// </summary>
    public string? Reason { get; init; }

    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// POST /escrow/refund success response (200 OK).
/// </summary>
public sealed record RefundEscrowResponse
{
    public required string EscrowId { get; init; }
    public required string Status { get; init; }
    public required decimal RefundAmount { get; init; }
    public string? TelebirrRef { get; init; }
    public required string RefundedAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// GET /escrow/history success response (200 OK).
/// </summary>
public sealed record GetEscrowHistoryResponse
{
    public required List<EscrowSummaryDto> Escrows { get; init; }
    public string? NextToken { get; init; }
    public bool HasMore { get; init; }
    public int Total { get; init; }
}

// ──────────────────────────────────────────────
// Domain DTOs
// ──────────────────────────────────────────────

/// <summary>
/// Full escrow data transfer object (GET /escrow/{id} response).
/// </summary>
public sealed record EscrowDto
{
    public required string EscrowId { get; init; }
    public required string DeliveryId { get; init; }
    public required string BuyerId { get; init; }
    public required string FarmerId { get; init; }
    public required decimal Amount { get; init; }
    public required decimal Commission { get; init; }
    public required decimal CommissionRate { get; init; }
    public required decimal FarmerPayout { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public string? TelebirrRef { get; init; }
    public string? TelebirrHoldRef { get; init; }
    public string? TelebirrReleaseRef { get; init; }
    public string? TelebirrRefundRef { get; init; }
    public DisputeDto? Dispute { get; init; }
    public required bool BuyerFaydaVerified { get; init; }
    public required bool FarmerFaydaVerified { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public required string ExpiresAt { get; init; }
    public string? DeliveryConfirmedAt { get; init; }
    public string? DisputeWindowEndsAt { get; init; }
    public string? ReleasedAt { get; init; }
    public string? RefundedAt { get; init; }
    public string? CompletedAt { get; init; }
    public required int Version { get; init; }
}

/// <summary>
/// Embedded dispute details within an EscrowDto.
/// </summary>
public sealed record DisputeDto
{
    public required string DisputeId { get; init; }
    public required string EscrowId { get; init; }
    public required string DisputedBy { get; init; }
    public required string Reason { get; init; }
    public string? Description { get; init; }
    public List<string>? EvidenceRefs { get; init; }
    public string? Resolution { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ResolvedAt { get; init; }
    public required string CreatedAt { get; init; }
}

/// <summary>
/// Summary DTO for payment history listings.
/// </summary>
public sealed record EscrowSummaryDto
{
    public required string EscrowId { get; init; }
    public required string DeliveryId { get; init; }
    public required string CounterpartyName { get; init; }
    public required decimal Amount { get; init; }
    public required decimal Commission { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public string? CompletedAt { get; init; }
}

/// <summary>
/// Immutable audit record for every escrow state change.
/// </summary>
public sealed record EscrowAuditEntryDto
{
    public required string AuditId { get; init; }
    public required string EscrowId { get; init; }
    public required string Action { get; init; }
    public required string FromState { get; init; }
    public required string ToState { get; init; }
    public required string Actor { get; init; }
    public string? TelebirrRef { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public required string Timestamp { get; init; }
    public string? IpAddress { get; init; }
}

// ──────────────────────────────────────────────
// Telebirr Gateway DTOs
// ──────────────────────────────────────────────

/// <summary>
/// Request to hold funds in the buyer's Telebirr account.
/// </summary>
public sealed record HoldFundsRequest
{
    public required string TransactionRef { get; init; }
    public required string BuyerPhone { get; init; }
    public required decimal Amount { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Telebirr HoldFunds response.
/// </summary>
public sealed record HoldFundsResponse
{
    public required bool Success { get; init; }
    public string? TelebirrRef { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to release held funds to the farmer's Telebirr account.
/// </summary>
public sealed record ReleaseFundsRequest
{
    public required string TransactionRef { get; init; }
    public required string HoldRef { get; init; }
    public required string FarmerPhone { get; init; }
    public required decimal Amount { get; init; }
    public required decimal PlatformCommission { get; init; }
}

/// <summary>
/// Telebirr ReleaseFunds response.
/// </summary>
public sealed record ReleaseFundsResponse
{
    public required bool Success { get; init; }
    public string? TelebirrRef { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to refund held funds to the buyer's Telebirr account.
/// </summary>
public sealed record RefundFundsRequest
{
    public required string TransactionRef { get; init; }
    public required string HoldRef { get; init; }
    public required string BuyerPhone { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary>
/// Telebirr RefundFunds response.
/// </summary>
public sealed record RefundFundsResponse
{
    public required bool Success { get; init; }
    public string? TelebirrRef { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Telebirr transaction status response (polling / reconciliation).
/// </summary>
public sealed record TransactionStatusResponse
{
    public required string TransactionRef { get; init; }
    public required string Status { get; init; }
    public string? TelebirrRef { get; init; }
    public required decimal Amount { get; init; }
    public required string UpdatedAt { get; init; }
}

// ──────────────────────────────────────────────
// Error DTOs
// ──────────────────────────────────────────────

/// <summary>
/// Escrow error response body per escrow-protocol.md contract.
/// </summary>
public sealed record EscrowErrorResponse
{
    public required EscrowError Error { get; init; }
}

/// <summary>
/// Escrow error detail with ESCROW_* code, message, and optional field reference.
/// </summary>
public sealed record EscrowError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Field { get; init; }
}
