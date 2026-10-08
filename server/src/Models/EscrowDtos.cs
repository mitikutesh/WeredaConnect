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
    /// Optional: the order this escrow is linked to (UUID).
    /// When present, amounts are derived from the order record (3-way split).
    /// </summary>
    public string? OrderId { get; init; }

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
    public string? OrderId { get; init; }
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

    /// <summary>
    /// Transient H5 checkout URL the buyer must open to complete payment (async
    /// C2B). Present only when the real gateway is used and status is 'initiated';
    /// null for the synchronous mock. NEVER persisted / never in GET /escrow/{id}.
    /// </summary>
    public string? ToPayUrl { get; init; }
}

/// <summary>
/// ESC-01: POST /escrow/order-checkout request body. The server derives everything else
/// (amount, buyer, merch_order_id) from its own order record.
/// </summary>
public sealed record OrderCheckoutRequest
{
    public required string OrderId { get; init; }
}

/// <summary>
/// ESC-01: POST /escrow/order-checkout response. <see cref="ToPayUrl"/> is transient: it is
/// never persisted and never appears in any other response.
/// </summary>
public sealed record OrderCheckoutResponse
{
    public required string OrderId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }

    /// <summary>The Telebirr checkout page the buyer opens to pay.</summary>
    public required string ToPayUrl { get; init; }

    /// <summary>When the checkout stops being payable (ISO 8601, UTC).</summary>
    public required string OpenUntil { get; init; }

    /// <summary>True when this is the same open checkout returned again (not a new payment).</summary>
    public bool IsExisting { get; init; }
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
    public decimal? TruckerPayout { get; init; }
    public required decimal Commission { get; init; }
    public string? TelebirrRef { get; init; }
    public string? TruckerTelebirrRef { get; init; }
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
// Manual payout queue (ESC-02, admin)
// ──────────────────────────────────────────────

/// <summary>
/// One payee leg of a queued manual payout: who gets paid, how much, and whether an
/// admin (or the gateway) already paid it.
/// </summary>
public sealed record ManualPayoutLegDto
{
    /// <summary>Leg name used in the completion URL: farmer, trucker or buyer.</summary>
    public required string Ref { get; init; }

    public required string PayeeRole { get; init; }
    public required string PayeeId { get; init; }
    public string? PayeeName { get; init; }

    /// <summary>Registered phone the admin pays in the merchant portal; null when the profile is missing.</summary>
    public string? PayeePhone { get; init; }

    /// <summary>False when the payee fails the same identity gate the gateway release applies.</summary>
    public required bool PayeeVerified { get; init; }
    public string? BlockReason { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>pending or paid.</summary>
    public required string Status { get; init; }
    public string? ExternalRef { get; init; }
    public string? PaidBy { get; init; }
    public string? PaidAt { get; init; }
}

/// <summary>
/// An escrow waiting in the manual payout queue (GET /admin/payouts/manual).
/// </summary>
public sealed record ManualPayoutDto
{
    public required string EscrowId { get; init; }
    public string? OrderId { get; init; }
    public string? DeliveryId { get; init; }

    /// <summary>release or refund.</summary>
    public required string Op { get; init; }

    /// <summary>PAYOUT_NOT_SUPPORTED or GATEWAY_REF_MISSING.</summary>
    public required string Reason { get; init; }
    public required string EscrowStatus { get; init; }

    /// <summary>Escrow status when it was queued; differs from <see cref="EscrowStatus"/> after a later dispute.</summary>
    public required string FromStatus { get; init; }
    public required string QueuedAt { get; init; }
    public required string Currency { get; init; }
    public required List<ManualPayoutLegDto> Legs { get; init; }

    /// <summary>Sum of the legs still pending.</summary>
    public required decimal PendingAmount { get; init; }
}

public sealed record ManualPayoutListResponse
{
    public required List<ManualPayoutDto> Payouts { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>POST /admin/payouts/{escrowId}/{ref}/complete body.</summary>
public sealed record CompleteManualPayoutRequest
{
    /// <summary>The Telebirr merchant-portal transaction id of the payment the admin made.</summary>
    public string? ExternalRef { get; init; }
}

public sealed record CompleteManualPayoutResponse
{
    public required string EscrowId { get; init; }
    public string? OrderId { get; init; }
    public required string Ref { get; init; }
    public required string LegStatus { get; init; }
    public required string EscrowStatus { get; init; }
    public required string ExternalRef { get; init; }
    public required string PaidAt { get; init; }
    public required List<string> RemainingLegs { get; init; }
    public string? Message { get; init; }
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
    public string? OrderId { get; init; }
    public required string BuyerId { get; init; }
    public string? BuyerName { get; set; }
    public required string FarmerId { get; init; }
    public string? FarmerName { get; set; }
    public required decimal Amount { get; init; }
    public required decimal Commission { get; init; }
    public required decimal CommissionRate { get; init; }
    public required decimal FarmerPayout { get; init; }

    /// <summary>Transport leg of the held amount; null when the order carried no trucker split.</summary>
    public decimal? TruckerPayout { get; init; }

    /// <summary>Trucker paid out of this escrow (the truck the order was bound to), when any.</summary>
    public string? TruckerId { get; init; }
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

    /// <summary>See <see cref="EscrowSummaryDto.RefundPending"/>. SIM-20.</summary>
    public bool RefundPending { get; init; }
    public string? RefundReason { get; init; }
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

    /// <summary>Empty string until an order-funded escrow is linked to a delivery.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>Set on order-funded escrows and order-linked initiations.</summary>
    public string? OrderId { get; init; }

    /// <summary>
    /// Party ids let offline clients cache history rows they did not initiate
    /// (device local reads filter by own-party id).
    /// </summary>
    public required string BuyerId { get; init; }

    public required string FarmerId { get; init; }
    public required string CounterpartyName { get; init; }
    public required decimal Amount { get; init; }
    public required decimal Commission { get; init; }

    /// <summary>Crop leg of the held amount: what the farmer receives on release.</summary>
    public decimal FarmerPayout { get; init; }

    /// <summary>Transport leg of the held amount; null when the order carried no trucker split.</summary>
    public decimal? TruckerPayout { get; init; }

    /// <summary>Trucker paid out of this escrow (the truck the order was bound to), when any.</summary>
    public string? TruckerId { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public string? CompletedAt { get; init; }

    /// <summary>Auto-refund deadline; set to "now" when a cancelled order's refund is scheduled.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>
    /// True while the money is still held but on its way back to the buyer: the
    /// order was cancelled or rejected (refund scheduled), or the hold expired
    /// and the refund sweep has not run yet. Clients label such rows
    /// "Refund pending", never "Funds held safely". SIM-20.
    /// </summary>
    public bool RefundPending { get; init; }

    /// <summary>Why the refund was scheduled, when the server recorded one.</summary>
    public string? RefundReason { get; init; }
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

    /// <summary>
    /// Transient H5 checkout URL for the buyer to complete payment (async C2B).
    /// Set only by the real gateway. When present, escrow stays 'initiated' and
    /// the webhook later transitions it to 'escrowed'. NEVER persisted (SEC-05).
    /// Null for the synchronous mock gateway (behavior unchanged).
    /// </summary>
    public string? ToPayUrl { get; init; }

    /// <summary>
    /// ESC-01: Telebirr's prepay_id behind <see cref="ToPayUrl"/>. The order checkout keeps it
    /// server-side so a retry can get a freshly signed URL for the same payment. Never
    /// returned to clients. Null for the synchronous mock gateway.
    /// </summary>
    public string? PrepayId { get; init; }
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
