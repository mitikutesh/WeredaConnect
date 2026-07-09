namespace WeredaConnect.Server.Models;

// ──────────────────────────────────────────────
// Dashboard
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/dashboard/stats response — KPI aggregation.
/// </summary>
public sealed record AdminDashboardStatsResponse
{
    public required int TotalUsers { get; init; }
    public required int TotalFarmers { get; init; }
    public required int TotalTruckers { get; init; }
    public required int TotalBuyers { get; init; }
    public required int TotalEscrows { get; init; }
    public required int ActiveEscrows { get; init; }
    public required int DisputedEscrows { get; init; }
    public required int TotalOrders { get; init; }
    public required int ActiveListings { get; init; }
    public required int FraudSignalsCount { get; init; }
    public required decimal TotalEscrowVolume { get; init; }
    public required decimal TotalCommissions { get; init; }
    public required string GeneratedAt { get; init; }
}

// ──────────────────────────────────────────────
// Escrow Management
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/escrows response — paginated escrow list.
/// </summary>
public sealed record AdminEscrowListResponse
{
    public required List<EscrowDto> Escrows { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// GET /admin/escrows/{id} response — single escrow detail.
/// </summary>
public sealed record AdminEscrowDetailResponse
{
    public required EscrowDto Escrow { get; init; }
}

/// <summary>
/// GET /admin/escrows/{id}/audit response — audit trail.
/// </summary>
public sealed record AdminEscrowAuditResponse
{
    public required string EscrowId { get; init; }
    public required List<EscrowAuditEntryDto> AuditEntries { get; init; }
}

// ──────────────────────────────────────────────
// Disputes
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/disputes response — disputed escrows.
/// </summary>
public sealed record AdminDisputeListResponse
{
    public required List<EscrowDto> Disputes { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// POST /admin/disputes/{id}/resolve request body.
/// </summary>
public sealed record ResolveDisputeRequest
{
    public required string Resolution { get; init; }
    public required string Action { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// POST /admin/disputes/{id}/resolve response.
/// </summary>
public sealed record ResolveDisputeResponse
{
    public required string EscrowId { get; init; }
    public required string Status { get; init; }
    public required string Resolution { get; init; }
    public required string ResolvedAt { get; init; }
    public string? Message { get; init; }
}

// ──────────────────────────────────────────────
// Fraud Signals
// ──────────────────────────────────────────────

/// <summary>
/// Fraud signal DTO for admin review.
/// </summary>
public sealed record FraudSignalDto
{
    public required string SignalId { get; init; }
    public required string UserId { get; init; }
    public required string SignalType { get; init; }
    public required string Severity { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public string? EntityId { get; init; }
    public string? EntityType { get; init; }
    public string? ReviewedBy { get; init; }
    public string? ReviewedAt { get; init; }
    public string? ReviewNotes { get; init; }
    public required string CreatedAt { get; init; }
}

/// <summary>
/// GET /admin/fraud-signals response.
/// </summary>
public sealed record AdminFraudSignalListResponse
{
    public required List<FraudSignalDto> Signals { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// POST /admin/fraud-signals/{id}/action request body.
/// </summary>
public sealed record FraudActionRequest
{
    public required string Action { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// POST /admin/fraud-signals/{id}/action response.
/// </summary>
public sealed record FraudActionResponse
{
    public required string SignalId { get; init; }
    public required string Status { get; init; }
    public required string ReviewedAt { get; init; }
    public string? Message { get; init; }
}

// ──────────────────────────────────────────────
// User Management
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/users response — paginated user list.
/// </summary>
public sealed record AdminUserListResponse
{
    public required List<UserDto> Users { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// GET /admin/users/{id} response — single user detail.
/// </summary>
public sealed record AdminUserDetailResponse
{
    public required UserDto User { get; init; }
}

/// <summary>
/// POST /admin/users/{id}/action request body.
/// </summary>
public sealed record UserActionRequest
{
    public required string Action { get; init; }
    public string? Reason { get; init; }
    public string? NewRole { get; init; }
}

/// <summary>
/// POST /admin/users/{id}/action response.
/// </summary>
public sealed record UserActionResponse
{
    public required string UserId { get; init; }
    public required string Action { get; init; }
    public required string Status { get; init; }
    public required string UpdatedAt { get; init; }
    public string? Message { get; init; }
}

// ──────────────────────────────────────────────
// Health & Compliance
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/health/overview response — system health metrics.
/// </summary>
public sealed record AdminHealthOverviewResponse
{
    public required string Status { get; init; }
    public required string Region { get; init; }
    public required int ActiveTables { get; init; }
    public required string Uptime { get; init; }
    public required string CheckedAt { get; init; }
}

/// <summary>
/// GET /admin/compliance/summary response — NBE compliance.
/// </summary>
public sealed record AdminComplianceSummaryResponse
{
    public required int TotalTransactions { get; init; }
    public required int FaydaVerifiedUsers { get; init; }
    public required int UnverifiedUsers { get; init; }
    public required int DisputesOpen { get; init; }
    public required int DisputesResolved { get; init; }
    public required decimal TotalVolume { get; init; }
    public required string Currency { get; init; }
    public required string GeneratedAt { get; init; }
}

// ──────────────────────────────────────────────
// Analytics
// ──────────────────────────────────────────────

/// <summary>
/// GET /admin/analytics/revenue response — commission analytics.
/// </summary>
public sealed record AdminRevenueAnalyticsResponse
{
    public required decimal TotalCommissions { get; init; }
    public required decimal TotalVolume { get; init; }
    public required int TransactionCount { get; init; }
    public required decimal AverageCommission { get; init; }
    public required string Currency { get; init; }
    public required string Period { get; init; }
    public required string GeneratedAt { get; init; }
}

/// <summary>
/// GET /admin/analytics/transactions response — volume metrics.
/// </summary>
public sealed record AdminTransactionAnalyticsResponse
{
    public required int TotalTransactions { get; init; }
    public required int CompletedTransactions { get; init; }
    public required int PendingTransactions { get; init; }
    public required int DisputedTransactions { get; init; }
    public required int RefundedTransactions { get; init; }
    public required decimal TotalVolume { get; init; }
    public required string Currency { get; init; }
    public required string Period { get; init; }
    public required string GeneratedAt { get; init; }
}

// ──────────────────────────────────────────────
// Transaction Monitor (unified order + delivery + escrow view)
// ──────────────────────────────────────────────

/// <summary>
/// One row in the admin transaction monitor list. Anchored on the order (the
/// first record created in a transaction's lifecycle); buyer/farmer/trucker
/// names are carried on the order record itself, so no enrichment is needed.
/// <c>Stage</c> is a coarse lifecycle grouping derived from the order status,
/// used to colour/filter rows at a glance.
/// </summary>
public sealed record AdminTransactionSummary
{
    public required string OrderId { get; init; }
    public required string Status { get; init; }
    public required string Stage { get; init; }
    public required string BuyerId { get; init; }
    public string? BuyerName { get; init; }
    public required string FarmerId { get; init; }
    public string? FarmerName { get; init; }
    public string? TruckerId { get; init; }
    public string? TruckerName { get; init; }
    public required string CropType { get; init; }
    public required double Quantity { get; init; }
    public required double OrderTotal { get; init; }
    public required string Currency { get; init; }
    public string? PaymentMethod { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

/// <summary>
/// GET /admin/transactions response — paginated transaction list.
/// </summary>
public sealed record AdminTransactionListResponse
{
    public required List<AdminTransactionSummary> Transactions { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// One chronological event in a transaction's stitched timeline. Assembled from
/// order timestamps, delivery timestamps, and the (immutable) escrow audit trail —
/// so it answers who/what/when, and where when a GPS fix was captured.
/// </summary>
public sealed record AdminTransactionTimelineEvent
{
    public required string Timestamp { get; init; }

    /// <summary>Which entity the event belongs to: order | delivery | escrow.</summary>
    public required string Entity { get; init; }

    /// <summary>Machine action key (e.g. order_created, picked_up, release).</summary>
    public required string Action { get; init; }

    public string? FromState { get; init; }
    public string? ToState { get; init; }

    /// <summary>Who performed the action (display name, user id, or system/cron/telebirr).</summary>
    public string? Actor { get; init; }

    public string? TelebirrRef { get; init; }

    /// <summary>Source IP the action came from, when captured (the audit "where").</summary>
    public string? IpAddress { get; init; }

    /// <summary>GPS fix captured at the event, when available (pickup/dropoff).</summary>
    public LatLngDto? Location { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// GET /admin/transactions/{orderId} response — the full stitched transaction:
/// the order, its escrow (payment/settlement hub) and delivery when present, and
/// a single merged timeline across all three legs.
/// </summary>
public sealed record AdminTransactionDetailResponse
{
    public required OrderDto Order { get; init; }
    public EscrowDto? Escrow { get; init; }
    public DeliveryDto? Delivery { get; init; }
    public required List<AdminTransactionTimelineEvent> Timeline { get; init; }
}

// ──────────────────────────────────────────────
// Cross-entity audit log (unified transaction-audit trail)
// ──────────────────────────────────────────────

/// <summary>
/// One entry in the unified transaction-audit trail — who/what/when/where for an
/// order, delivery, or admin action.
/// </summary>
public sealed record AdminAuditEntryDto
{
    public required string AuditId { get; init; }
    public required string TransactionId { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Action { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public required string Actor { get; init; }
    public string? ActorRole { get; init; }
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }
    public required string Timestamp { get; init; }
    public string? Metadata { get; init; }
}

/// <summary>
/// GET /admin/audit response — recent cross-entity audit entries (newest first).
/// </summary>
public sealed record AdminAuditListResponse
{
    public required List<AdminAuditEntryDto> Entries { get; init; }
    public required int Total { get; init; }
}
