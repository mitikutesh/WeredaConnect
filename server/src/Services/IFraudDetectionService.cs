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
    /// Record a successful listing creation for velocity tracking.
    /// Call AFTER _cropRepo.CreateAsync() succeeds.
    /// </summary>
    Task RecordListingCreatedAsync(string farmerId);

    /// <summary>
    /// Record a successful payment initiation for velocity tracking.
    /// Call AFTER _escrowService.InitiateAsync() succeeds.
    /// </summary>
    Task RecordPaymentInitiatedAsync(string userId);
}
