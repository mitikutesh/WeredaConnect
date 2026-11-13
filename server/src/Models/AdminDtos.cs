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

    /// <summary>
    /// Signals waiting for review (<c>status: pending</c>). FRD-07: a count on the sparse status index; before
    /// it, a Scan counted every signal row, reviewed ones included.
    /// </summary>
    public required int FraudSignalsCount { get; init; }

    /// <summary>FRD-07: escrows frozen for fraud review (<c>held</c>), the length of the held queue.</summary>
    public int HeldEscrows { get; init; }
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
    /// <c>wait_for_delivery</c> for <c>farmer_wins</c>. FUP-64: <c>settle_as_is</c> for either single verdict (never a
    /// split) when none of those fits. Needs <see cref="Reason"/>.
    /// </summary>
    public string? Disposition { get; init; }

    /// <summary>FUP-16: why the admin chose <see cref="Disposition"/>, 10 to 500 characters; stored in the audit trail.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// ESC-20 (escrow-protocol.md 2.2.0, P.8): the split of a <c>partial_refund</c> verdict (<c>action: split</c>),
    /// in ETB with at most 2 decimals: what the farmer is paid out of the crop leg held.
    /// </summary>
    public decimal? FarmerAmount { get; init; }

    /// <summary>ESC-20: what the trucker is paid out of the transport leg held.</summary>
    public decimal? TruckerAmount { get; init; }

    /// <summary>ESC-20: what goes back to the buyer.</summary>
    public decimal? BuyerRefund { get; init; }
}

/// <summary>ESC-20 (P.8): the amounts of a split verdict as recorded on the escrow.</summary>
public sealed record VerdictAmountsDto
{
    public required decimal FarmerAmount { get; init; }
    public required decimal TruckerAmount { get; init; }
    public required decimal BuyerRefund { get; init; }
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

    /// <summary>ESC-20 (P.8): the split, on a <c>partial_refund</c> verdict.</summary>
    public VerdictAmountsDto? VerdictAmounts { get; init; }
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

    /// <summary>
    /// When the first refusal of the row's hour happened. Since FRD-08 one row stands for every refusal of its
    /// type in one UTC hour (fraud-detection-architecture.md section 14.3), so this is the first, not the only one.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// FRD-07: the order of the transaction timeline the signal belongs to, when its entity names one: an
    /// <c>order</c> signal, or an <c>escrow</c> signal on an order escrow (<c>escrow-order-{orderId}</c>).
    /// </summary>
    public string? OrderId { get; init; }

    /// <summary>
    /// FRD-19: on an <c>AI_ADVISORY</c> row, the AI reviewer's concern (<c>none</c>, <c>low</c>, <c>medium</c> or
    /// <c>high</c>). Its explanation is the <see cref="Description"/>. Advice only (fraud-detection-architecture.md
    /// section 21): its weight is capped and never counts toward CRITICAL.
    /// </summary>
    public string? AiConcern { get; init; }

    /// <summary>FRD-19: the Bedrock model that wrote the advice, on an <c>AI_ADVISORY</c> row.</summary>
    public string? AiModel { get; init; }

    /// <summary>
    /// FUP-145: on an <c>AI_ADVISORY</c> row, what each label the explanation may cite stands for (<c>order-1</c>).
    /// Null on a row written before the map was kept, or when the timeline named no entity. Admin only.
    /// </summary>
    public List<FraudAdvisoryLabelDto>? AiLabels { get; init; }
}

/// <summary>
/// FUP-145: one label of an AI advisory's timeline and the record it stands for. The model saw only the label; the
/// real id was kept on the row by the server and is returned to admins only.
/// </summary>
public sealed record FraudAdvisoryLabelDto
{
    /// <summary>The label as the explanation writes it: <c>order-1</c>, <c>delivery-2</c>, <c>escrow-1</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The kind in the label: <c>order</c>, <c>delivery</c>, <c>escrow</c>, <c>crop</c>, <c>trip</c>, <c>user</c> or <c>entity</c>.</summary>
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    /// <summary>The order of the transaction timeline, when the entity names one (an order, or an order escrow).</summary>
    public string? OrderId { get; init; }
}

/// <summary>
/// GET /admin/fraud-signals response. FRD-07: one page of the status index, newest first, and the risk score of
/// each user on the page, so Fraud Review groups the signals by user.
/// </summary>
public sealed record AdminFraudSignalListResponse
{
    public required List<FraudSignalDto> Signals { get; init; }
    public string? NextToken { get; init; }
    public required int Total { get; init; }

    /// <summary>FRD-07: one entry per distinct user of <see cref="Signals"/>, in their first appearance order.</summary>
    public List<FraudSignalUserDto> Users { get; init; } = [];
}

/// <summary>
/// FRD-07: a user's risk score as Fraud Review shows it (fraud-detection-architecture.md sections 14 and 16). The
/// score is admin-only: parties never read it (section 17).
/// </summary>
public sealed record FraudSignalUserDto
{
    public required string UserId { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>The full score, AI advisory weight included (section 14.2).</summary>
    public required int RiskScore { get; init; }

    /// <summary>The score without the advisory weight, which decides CRITICAL.</summary>
    public required int EnforcementScore { get; init; }

    /// <summary><c>low</c>, <c>medium</c>, <c>high</c> or <c>critical</c>.</summary>
    public required string RiskTier { get; init; }

    /// <summary>Rows were left unread after the page cap, so the tier is CRITICAL (section 14.3).</summary>
    public bool Truncated { get; init; }

    /// <summary>False when the score could not be read; the score and tier then say nothing.</summary>
    public required bool ScoreAvailable { get; init; }
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

    /// <summary>
    /// FRD-07: why the account is suspended, as the admin typed it, or <c>fraud_confirmed</c> after a Fraud Review
    /// reject (section 18.1). Admin-only: <see cref="UserDto.SuspendReason"/> is never serialized to a party.
    /// </summary>
    public string? SuspendReason { get; init; }

    /// <summary>
    /// FUP-317 (user-entity.md 2.3.0 section 14): the user's accepted profile edits, newest first, at most 50; empty
    /// when there are none. Absent when the transaction-audit table is not configured or could not be read, and for a
    /// deleting or deleted account (<see cref="ProfileHistoryHidden"/>).
    /// </summary>
    public List<AdminProfileEditDto>? ProfileHistory { get; init; }

    /// <summary>FUP-317: true when the user has older edits than the ones listed; absent with the history.</summary>
    public bool? ProfileHistoryMore { get; init; }

    /// <summary>
    /// FUP-317: true when the account has <c>deletionState</c> and the history is withheld (its old names are not
    /// shown, ACC-05); absent otherwise. Tells the admin app "deleted account" apart from "could not be read".
    /// </summary>
    public bool? ProfileHistoryHidden { get; init; }
}

/// <summary>FUP-317: one accepted profile edit on the admin user detail, from its transaction-audit row.</summary>
public sealed record AdminProfileEditDto
{
    /// <summary>When the edit was saved (the row's <c>timestamp</c>, ISO 8601 UTC).</summary>
    public required string EditedAt { get; init; }

    /// <summary>Who saved it: the user's own id (only the user edits their profile).</summary>
    public required string Actor { get; init; }

    public string? ActorRole { get; init; }

    /// <summary>The fields that changed, in the order displayName, woreda, companyName; empty when unreadable.</summary>
    public required List<ProfileFieldChange> Changes { get; init; }
}

/// <summary>
/// FUP-317: one changed profile field with its value before and after the edit. <c>From</c> is absent when the row had
/// no value. Also the shape of the audit row's <c>metadata</c> (a JSON array of these).
/// </summary>
public sealed record ProfileFieldChange
{
    public required string Field { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }
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

    /// <summary>FUP-287: the pickup judgement with its check-ins as evidence; absent for an order never stamped judgeable.</summary>
    public AdminPickupEvidenceDto? PickupEvidence { get; init; }
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
/// scan, and this delivery's own) and from every planned stop of the trip (each order's listing
/// location, the trip's destination point): the admin board's "stops off the stop list".
/// </summary>
public sealed record AdminTrailAwayStopDto
{
    public required string From { get; init; }
    public required string Until { get; init; }

    /// <summary>Where the truck stood: the last precise fix at or before the pause, 4 decimals.</summary>
    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    /// <summary>The nearest QR scan, 1 decimal; null when the trip has no scan with a place.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? NearestScanKm { get; init; }

    /// <summary>The nearest planned stop, 1 decimal; null when the stop list could not be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? NearestPlannedStopKm { get; init; }
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

// ──────────────────────────────────────────────
// Dispute evidence (FRD-14)
// ──────────────────────────────────────────────

/// <summary>
/// FRD-14: GET /admin/disputes/{escrowId}/evidence response (escrow-protocol.md 1.40.0), the evidence
/// of one dispute on one screen: the escrow with its dispute, the order, the delivery with its handoff
/// photos (FRD-12), the stitched timeline of the transaction monitor, the handoffs (QR scan or typed
/// code, who, GPS and the distance to the planned point) and, only when asked for, the order's and
/// the delivery's message threads (every such read is audited). Admin group only.
/// </summary>
public sealed record AdminDisputeEvidenceResponse
{
    public required EscrowDto Escrow { get; init; }

    /// <summary>The escrow's order; null for an escrow without one, or when the order is gone.</summary>
    public OrderDto? Order { get; init; }

    /// <summary>The delivery, without the phones and the QR token; null when none is linked yet.</summary>
    public DeliveryDto? Delivery { get; init; }

    /// <summary>The order, delivery and escrow legs in time order, as on the transaction view.</summary>
    public required List<AdminTransactionTimelineEvent> Timeline { get; init; }

    /// <summary>The handoffs that happened, in time order: the pickup, then the drop-off (or the self-pickup).</summary>
    public required List<AdminHandoffEvidenceDto> Handoffs { get; init; }

    /// <summary>
    /// The messages of the order's and the delivery's threads, oldest first; only with
    /// <c>include=messages</c>, otherwise left out.
    /// </summary>
    public List<MessageDto>? Messages { get; init; }

    /// <summary>True when a thread held more messages than one read returns (the newest are kept).</summary>
    public bool? MessagesTruncated { get; init; }
}

/// <summary>
/// FRD-14: one handoff of a disputed delivery. <c>pickup</c> is the trucker's scan of the farmer's QR,
/// <c>dropoff</c> the buyer's scan at delivery, <c>self_pickup</c> the buyer's scan at the farm.
/// </summary>
public sealed record AdminHandoffEvidenceDto
{
    /// <summary><c>pickup</c>, <c>dropoff</c> or <c>self_pickup</c>.</summary>
    public required string Leg { get; init; }

    /// <summary>The server's time of the handoff (<c>pickedUpAt</c> or <c>confirmedAt</c>).</summary>
    public string? At { get; init; }

    /// <summary>The phone's time of the pickup scan (<c>pickupCapturedAt</c>), when recorded.</summary>
    public string? CapturedAt { get; init; }

    /// <summary>Who made the handoff, from its audit row; null for a delivery older than that trail.</summary>
    public string? ActorId { get; init; }

    /// <summary><c>buyer</c>, <c>farmer</c> or <c>trucker</c> when <see cref="ActorId"/> is one of the parties.</summary>
    public string? ActorRole { get; init; }

    /// <summary><c>qr</c> (the QR scanned) or <c>code</c> (the typed code), from the audit row; null when not recorded.</summary>
    public string? Method { get; init; }

    /// <summary>The phone's position at the handoff, when it had a fix.</summary>
    public LatLngDto? Gps { get; init; }

    /// <summary>The planned point: the listing's location for a pickup, the trip's destination for a drop-off.</summary>
    public LatLngDto? Planned { get; init; }

    /// <summary>Great-circle distance from <see cref="Gps"/> to <see cref="Planned"/> in km, 2 decimals.</summary>
    public double? DistanceKm { get; init; }
}
