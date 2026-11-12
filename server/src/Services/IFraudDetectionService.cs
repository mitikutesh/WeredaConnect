namespace WeredaConnect.Server.Services;

using WeredaConnect.Server.Models;

/// <summary>
/// Fraud detection service for F-14.
/// Checks listing creation, payment initiation, and delivery GPS.
/// AOT-compatible — no reflection.
/// </summary>
public interface IFraudDetectionService
{
    /// <summary>
    /// Run all listing-related fraud checks (velocity, duplicate, price anomaly).
    /// Call BEFORE _cropRepo.CreateAsync().
    /// </summary>
    Task<FraudCheckResult> CheckListingCreationAsync(
        string farmerId, string cropType, decimal pricePerKg);

    /// <summary>
    /// Check payment velocity before escrow initiation.
    /// Call BEFORE _escrowService.InitiateAsync().
    /// </summary>
    Task<FraudCheckResult> CheckPaymentVelocityAsync(string userId);

    /// <summary>
    /// Check GPS consistency during delivery verification.
    /// Pure computation — no DB calls.
    /// </summary>
    FraudCheckResult CheckGpsConsistency(
        double? pickupLat, double? pickupLng,
        double? dropoffLat, double? dropoffLng,
        string fromWoreda, string toWoreda);

    /// <summary>
    /// Persist a fraud signal to the FraudSignalsTable for admin review.
    /// Call when a check rejects (hard) or flags (soft) an operation.
    /// No-op when the signals table is not configured. Callers must treat
    /// this as best-effort: wrap in try/catch so a signal-write failure
    /// never blocks the underlying operation. Writes at most one row per
    /// user, type and UTC hour (FRD-08); a later call in the same hour
    /// writes nothing and returns normally.
    /// </summary>
    Task RecordFraudSignalAsync(
        string userId, string signalType, string? entityId,
        string entityType, string? description);

    /// <summary>
    /// FRD-15: whether the user already holds a signal row of <paramref name="signalType"/> on
    /// <paramref name="entityId"/> within the table's 30 day life, whatever its review status, so a
    /// scan that sees the same entity again in a later hour writes nothing more. False when the
    /// table is not configured. True when the partition has more rows than the read cap (absence
    /// cannot be shown). Throws on a failed read.
    /// </summary>
    Task<bool> HasSignalAsync(string userId, string signalType, string entityId);

    /// <summary>
    /// ESC-33 (fraud-detection-architecture.md section 14.3): write a signal onto
    /// <paramref name="targetUserId"/> that the request of another user, <paramref name="sourceUserId"/>,
    /// caused. A truncated score read is CRITICAL, so such a write is bounded before it is made: at most
    /// one row per target, type and entity, one per source and target, and two per target from all
    /// sources, each over the table's 30 day life (the two caps held by conditional slot writes, so
    /// concurrent requests cannot pass them), on top of the one row per user, type and hour of every
    /// signal. The caller must take both ids from server rows, never from the request. Throws on a failed
    /// read or write: callers treat it as best effort, like <see cref="RecordFraudSignalAsync"/>.
    /// </summary>
    Task<AttributedSignalOutcome> RecordAttributedSignalAsync(
        string targetUserId, string sourceUserId, string signalType, string entityId,
        string entityType, string? description);

    /// <summary>
    /// Record a successful listing creation for velocity tracking.
    /// Call AFTER _cropRepo.CreateAsync() succeeds.
    /// </summary>
    Task RecordListingCreatedAsync(string farmerId);

    /// <summary>
    /// Record a successful payment initiation for velocity tracking.
    /// Call AFTER _escrowService.InitiateAsync() succeeds.
    /// </summary>
    Task RecordPaymentInitiatedAsync(string userId);

    /// <summary>
    /// The user's risk score and tier (fraud-detection-architecture.md sections 14 and 16), from
    /// one Query of the user's fraud-signals partition over the scoring window. Never throws:
    /// an unconfigured table or a failed read gives LOW, with <see cref="RiskScoreResult.Source"/>
    /// saying which, and a read cut at the page cap gives CRITICAL. Check Source before Tier:
    /// an Unavailable result carries Tier Low, and a settlement sweep must skip it. Use a
    /// <see cref="RiskScoreMemo"/> to read each user once per batch or sweep.
    /// </summary>
    Task<RiskScoreResult> GetUserRiskScoreAsync(string userId);
}
