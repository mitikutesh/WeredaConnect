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
    /// Explanation for audit trail (0-500 chars; 10-500 with <see cref="OverrideOrderMismatch"/>).
    /// </summary>
    public string? Reason { get; init; }

    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// ESC-03, admin group only: refund a <c>buyer_wins</c> verdict although its order cannot
    /// follow it (the goods are already moving). Needs a <see cref="Reason"/>; audited with the
    /// admin and the reason, and the order is flagged. The sweep never sets it. Deprecated for
    /// verdicts since escrow-protocol 1.21.0 (FUP-16): a disposition on the verdict moves the order
    /// and delivery instead. Still the tool for an ESC-09 Part B refund of a plain escrowed escrow.
    /// </summary>
    public bool OverrideOrderMismatch { get; init; }
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

    /// <summary>
    /// ESC-05: the reference the server sends Telebirr for this leg (<c>release:{escrowId}</c>,
    /// <c>release:{escrowId}:trucker</c> or <c>refund:{escrowId}</c>). The admin searches the
    /// portal for it before paying a leg whose outcome is unknown.
    /// </summary>
    public string? GatewayRef { get; init; }
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

    /// <summary>PAYOUT_NOT_SUPPORTED, GATEWAY_REF_MISSING or (ESC-05) GATEWAY_OUTCOME_UNKNOWN.</summary>
    public required string Reason { get; init; }
    public required string EscrowStatus { get; init; }

    /// <summary>Escrow status when it was queued; differs from <see cref="EscrowStatus"/> after a later dispute.</summary>
    public required string FromStatus { get; init; }

    /// <summary>
    /// When the delivery was confirmed, if it was. On a queued refund this means the goods
    /// arrived after the refund was queued: check before refunding.
    /// </summary>
    public string? DeliveryConfirmedAt { get; init; }

    /// <summary>
    /// ESC-05: when the settlement lock was taken, which is when the gateway may have been
    /// called. Null for a lock taken before ESC-05.
    /// </summary>
    public string? SettlementLockedAt { get; init; }

    /// <summary>
    /// ESC-05: the dispute verdict on the escrow, if any. A lock taken before ESC-03 can contradict
    /// it (a <c>release</c> queued on a <c>buyer_wins</c> verdict); the admin page warns then.
    /// </summary>
    public string? DisputeResolution { get; init; }
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

    /// <summary>True when this leg was already recorded with the same reference (a replay); nothing new was paid or settled.</summary>
    public bool AlreadyRecorded { get; init; }
}

// ──────────────────────────────────────────────
// Manual refund of an unapplied payment (FUP-02, admin)
// ──────────────────────────────────────────────

/// <summary>
/// A captured Telebirr payment the webhook did not apply, still waiting for its manual refund
/// (GET /admin/payments/unapplied).
/// </summary>
public sealed record UnappliedPaymentDto
{
    public required string OrderId { get; init; }

    /// <summary>The escrow-audit key the rows sit under: <c>escrow-order-{orderId}</c>.</summary>
    public required string EscrowId { get; init; }

    /// <summary>Telebirr payment_order_id; null when the webhook never learned it (search the portal by <see cref="MerchOrderId"/>).</summary>
    public string? PaymentOrderId { get; init; }
    public string? MerchOrderId { get; init; }

    /// <summary><c>order_payment_unmatched</c>, <c>order_payment_duplicate</c> and/or <c>order_payment_unprocessed</c>.</summary>
    public required List<string> Actions { get; init; }

    /// <summary>The webhook's reasons (<c>order_missing</c>, <c>status_cancelled</c>, <c>amount</c>, ...).</summary>
    public required List<string> Reasons { get; init; }
    public required string FirstSeenAt { get; init; }
    public required string LastSeenAt { get; init; }

    /// <summary>What Telebirr captured, when the rows tell; null when only the portal knows.</summary>
    public decimal? CapturedAmount { get; init; }
    public required string Currency { get; init; }

    /// <summary>The order's status now; null when the order does not exist.</summary>
    public string? OrderStatus { get; init; }
    public string? BuyerId { get; init; }

    /// <summary>
    /// Only <c>unprocessed</c> rows and the order still waits for a payment: a redelivery may
    /// still apply it. Do not refund yet (the record call answers <c>409 ESCROW_044</c>).
    /// </summary>
    public required bool MayStillApply { get; init; }
}

public sealed record UnappliedPaymentListResponse
{
    public required List<UnappliedPaymentDto> Payments { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>POST /admin/payments/{orderId}/refund-manual body.</summary>
public sealed record RecordManualRefundRequest
{
    /// <summary>Telebirr payment_order_id of the captured payment that was refunded.</summary>
    public string? PaymentOrderId { get; init; }

    /// <summary>Merchant-portal transaction id of the refund.</summary>
    public string? TelebirrRef { get; init; }

    /// <summary>Amount refunded, ETB.</summary>
    public decimal? Amount { get; init; }

    /// <summary>Why, for the audit trail: 10 to 500 characters.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Only for a payment the webhook could not name (no payment_order_id on its rows): its
    /// checkout's merch_order_id, as listed.
    /// </summary>
    public string? MerchOrderId { get; init; }
}

public sealed record RecordManualRefundResponse
{
    public required string OrderId { get; init; }
    public required string EscrowId { get; init; }
    public required string PaymentOrderId { get; init; }
    public string? MerchOrderId { get; init; }
    public required string TelebirrRef { get; init; }
    public decimal? Amount { get; init; }

    /// <summary>False when the rows did not record what was captured, so the amount could not be checked.</summary>
    public required bool AmountChecked { get; init; }
    public required string RecordedAt { get; init; }
    public required string RecordedBy { get; init; }
    public string? Message { get; init; }

    /// <summary>True on a replay with the same reference: no second row was written.</summary>
    public bool AlreadyRecorded { get; init; }
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

    /// <summary>FUP-16: the admin's disposition recorded on the verdict. Admin routes only; null for the parties.</summary>
    public string? Disposition { get; init; }

    /// <summary>FUP-16: the admin's reason for <see cref="Disposition"/>. Admin routes only.</summary>
    public string? DispositionReason { get; init; }

    /// <summary>FUP-16: the admin who recorded <see cref="Disposition"/>. Admin routes only.</summary>
    public string? DispositionBy { get; init; }

    /// <summary>FUP-16: when <see cref="Disposition"/> was recorded. Admin routes only.</summary>
    public string? DispositionAt { get; init; }
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
