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

    /// <summary>
    /// FUP-16: what happens when the order or delivery cannot follow the verdict as it is:
    /// <c>return_to_farmer</c> or <c>delivered_anyway</c> for <c>buyer_wins</c>, <c>settle_now</c> or
    /// <c>wait_for_delivery</c> for <c>farmer_wins</c>. Needs <see cref="Reason"/>.
    /// </summary>
    public string? Disposition { get; init; }

    /// <summary>FUP-16: why the admin chose <see cref="Disposition"/>, 10 to 500 characters; stored in the audit trail.</summary>
    public string? Reason { get; init; }
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

    /// <summary>FUP-16: the disposition recorded on the verdict, if any.</summary>
    public string? Disposition { get; init; }
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

    /// <summary>FRD-05: the admin's decision on a reviewed signal: <c>approve</c>, <c>reject</c> or <c>whitelist</c>.</summary>
    public string? Resolution { get; init; }
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
/// POST /admin/fraud-signals/{id}/action request body. <see cref="Action"/> is <c>approve</c>,
/// <c>reject</c> or <c>whitelist</c>, exactly (FRD-05).
/// </summary>
public sealed record FraudActionRequest
{
    public required string Action { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// POST /admin/fraud-signals/{id}/action response. FRD-05 (fraud-detection-architecture.md section 19)
/// adds what the action changed: the suspension and the open escrows for <c>reject</c>, the expiry for
/// <c>whitelist</c>.
/// </summary>
public sealed record FraudActionResponse
{
    public required string SignalId { get; init; }
    public required string Status { get; init; }
    public required string ReviewedAt { get; init; }
    public string? Message { get; init; }

    /// <summary>The action applied: <c>approve</c>, <c>reject</c> or <c>whitelist</c>.</summary>
    public string? Resolution { get; init; }

    /// <summary>The user the signal belongs to.</summary>
    public string? UserId { get; init; }

    /// <summary>reject: the account is suspended (<see cref="SuspendReason"/> <c>fraud_confirmed</c>).</summary>
    public bool? Suspended { get; init; }

    public string? SuspendReason { get; init; }

    /// <summary>reject: false when the sign-in service could not be updated, so sessions may outlive the suspension.</summary>
    public bool? SessionsEnded { get; init; }

    /// <summary>
    /// reject: the user's escrows that are still open (<c>escrowed</c>, <c>disputed</c>,
    /// <c>dispute_resolved</c>, <c>held</c>), read-only. Nothing is held by the action; the next settlement
    /// sweep holds each one that comes due.
    /// </summary>
    public List<FraudOpenEscrowDto>? OpenEscrows { get; init; }

    /// <summary>reject: false when the read stopped at its budget or failed, so the list may be incomplete.</summary>
    public bool? OpenEscrowsComplete { get; init; }

    /// <summary>whitelist: until when the user's soft-class signals do not count (ISO 8601).</summary>
    public string? WhitelistUntil { get; init; }
}

/// <summary>FRD-05: one open escrow of a rejected user, as the reject response lists it.</summary>
public sealed record FraudOpenEscrowDto
{
    public required string EscrowId { get; init; }
    public required string Status { get; init; }

    /// <summary>The user's side of the escrow: <c>buyer</c>, <c>farmer</c> or <c>trucker</c>.</summary>
    public required string Party { get; init; }
    public required decimal Amount { get; init; }
    public string? OrderId { get; init; }
    public string? CreatedAt { get; init; }
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

// ──────────────────────────────────────────────
// Delivery trail (LT-13, dispute evidence)
// ──────────────────────────────────────────────

/// <summary>
/// LT-13: GET /admin/deliveries/{deliveryId}/trail response (live-trip-api.md section 3). Every
/// check-in and sharing event of the delivery's trip from its pickup to its delivery (or now while
/// it is on the road), at 4 decimals, with the legs between precise check-ins, the pauses, the gaps
/// and what they add up to. Admin group only; read only. Nullable fields are written as <c>null</c>.
/// </summary>
public sealed record AdminDeliveryTrailResponse
{
    public required string DeliveryId { get; init; }

    /// <summary>The trip of the check-ins; null for a self-pickup (no truck, no trail).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? TripId { get; init; }

    public required string Status { get; init; }

    /// <summary>True while the delivery is <c>pickedUp</c> or <c>inTransit</c>: the window runs to now.</summary>
    public required bool Open { get; init; }

    /// <summary>The window's start, the delivery's <c>pickedUpAt</c>; null before pickup.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? From { get; init; }

    /// <summary>The window's end, when the delivery left the road; null while open or before pickup.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Until { get; init; }

    /// <summary>The server time of this answer.</summary>
    public required string AsOf { get; init; }

    /// <summary>The pickup QR scan: <c>gpsPickup</c> and <c>pickedUpAt</c>.</summary>
    public required AdminTrailStopDto Pickup { get; init; }

    /// <summary>The drop-off QR scan: <c>gpsDropoff</c> and <c>confirmedAt</c>.</summary>
    public required AdminTrailStopDto Dropoff { get; init; }

    /// <summary>Every fix and event of the window, in capture order.</summary>
    public required List<AdminTrailPointDto> Points { get; init; }

    /// <summary>One leg per pair of consecutive precise fixes (a coarse fix is no end of a leg).</summary>
    public required List<AdminTrailLegDto> Legs { get; init; }

    public required List<TrackPauseDto> Pauses { get; init; }

    /// <summary>Stretches of the window without a check-in for longer than the cadence allows, pauses left out.</summary>
    public required List<AdminTrailGapDto> Gaps { get; init; }

    /// <summary>Pauses at a place far from every QR scan of the trip (see <see cref="AdminTrailAwayStopDto"/>).</summary>
    public required List<AdminTrailAwayStopDto> AwayStops { get; init; }

    public required AdminTrailSummaryDto Summary { get; init; }
}

/// <summary>
/// A pause of the driver more than 5 km from every QR scan of the trip (any order's pickup or drop-off
/// scan, and this delivery's own): a stop no handoff on the truck explains. This is the admin board's
/// "stops off the stop list", judged from the scans the trail already holds rather than each order's
/// planned points.
/// </summary>
public sealed record AdminTrailAwayStopDto
{
    public required string From { get; init; }
    public required string Until { get; init; }

    /// <summary>Where the truck stood: the last precise fix at or before the pause, 4 decimals.</summary>
    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    /// <summary>The nearest QR scan, 1 decimal.</summary>
    public required double NearestScanKm { get; init; }
}

/// <summary>A QR handoff point of the delivery at 4 decimals, with its time.</summary>
public sealed record AdminTrailStopDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Latitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Longitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? At { get; init; }
}

/// <summary>One stored check-in (<c>fix</c>) or sharing event (<c>paused</c>, <c>resumed</c>, <c>declined</c>).</summary>
public sealed record AdminTrailPointDto
{
    public required string CapturedAt { get; init; }
    public required string ReceivedAt { get; init; }

    /// <summary><c>fix</c>, <c>paused</c>, <c>resumed</c> or <c>declined</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A fix's source (<c>hourly</c>, <c>scan</c>, ...); null on an event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Latitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Longitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? AccuracyM { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Mocked { get; init; }

    /// <summary>The end the driver chose, on a <c>paused</c> event only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PausedUntil { get; init; }

    /// <summary>A fix less precise than 1,000 m: drawn, but no end of a leg (live-trip-api.md section 5, invariant 6).</summary>
    public required bool Coarse { get; init; }

    /// <summary>Received more than 15 minutes after it was captured (it waited offline).</summary>
    public required bool Late { get; init; }
}

/// <summary>The leg between two consecutive precise fixes: great circle distance, time and speed.</summary>
public sealed record AdminTrailLegDto
{
    public required string FromCapturedAt { get; init; }
    public required string ToCapturedAt { get; init; }

    /// <summary>Great circle distance, 2 decimals.</summary>
    public required double DistanceKm { get; init; }

    /// <summary>1 decimal.</summary>
    public required double Minutes { get; init; }

    /// <summary>1 decimal; null when both fixes carry the same time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Kmh { get; init; }

    /// <summary>The <c>GPS_JUMP</c> rule: more than 1 km apart at more than 130 km/h.</summary>
    public required bool Jump { get; init; }
}

/// <summary>A stretch without a check-in: from a check-in (or the pickup) to the next (or the end).</summary>
public sealed record AdminTrailGapDto
{
    public required string From { get; init; }
    public required string Until { get; init; }

    /// <summary>The minutes without a check-in, the paused time left out, whole.</summary>
    public required int Minutes { get; init; }
}

/// <summary>What the trail says, for the "what the trail says" panel.</summary>
public sealed record AdminTrailSummaryDto
{
    /// <summary>At least one fix, no gap and the driver never declined in the window.</summary>
    public required bool SharedWholeWay { get; init; }

    /// <summary>Sharing was declined at some time of the window (also by a decline before pickup).</summary>
    public required bool Declined { get; init; }

    public required int Fixes { get; init; }
    public required int CoarseFixes { get; init; }
    public required int MockedFixes { get; init; }
    public required int LateFixes { get; init; }
    public required int Jumps { get; init; }
    public required int Gaps { get; init; }

    /// <summary>The count of <see cref="AdminDeliveryTrailResponse.AwayStops"/>.</summary>
    public required int AwayStops { get; init; }

    /// <summary>The sum of the legs, 1 decimal.</summary>
    public required double DistanceKm { get; init; }

    /// <summary>The window in whole minutes (to now while open).</summary>
    public required int DurationMin { get; init; }

    /// <summary>The fastest leg with a speed, 1 decimal; null without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? MaxKmh { get; init; }
}
