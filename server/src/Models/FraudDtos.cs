namespace WeredaConnect.Server.Models;

/// <summary>
/// Result of a fraud detection check.
/// Fraud checks can either allow, flag (soft), or reject (hard).
/// </summary>
public sealed record FraudCheckResult
{
    /// <summary>
    /// Whether the operation is allowed to proceed.
    /// False = hard reject (return error to client).
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Whether the operation is flagged for review (soft check).
    /// The operation still proceeds, but metadata is added.
    /// </summary>
    public bool IsFlagged { get; init; }

    /// <summary>
    /// Error code for rejected operations (e.g., FRAUD_DUPLICATE_LISTING).
    /// Null when IsAllowed is true.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Human-readable message for the fraud check result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Flag reason code for flagged operations (e.g., "PRICE_ANOMALY", "GPS_ANOMALY").
    /// Null when IsFlagged is false.
    /// </summary>
    public string? FlagReason { get; init; }

    public static FraudCheckResult Allowed() =>
        new() { IsAllowed = true };

    public static FraudCheckResult Flagged(string flagReason, string message) =>
        new() { IsAllowed = true, IsFlagged = true, FlagReason = flagReason, Message = message };

    public static FraudCheckResult Rejected(string errorCode, string message) =>
        new() { IsAllowed = false, ErrorCode = errorCode, Message = message };
}
