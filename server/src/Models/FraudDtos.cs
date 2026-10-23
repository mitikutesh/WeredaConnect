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

/// <summary>
/// Risk tier of a user (fraud-detection-architecture.md section 16). Unrelated to the
/// reputation tiers of the three-party escrow design.
/// </summary>
public enum RiskTier
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>Where a <see cref="RiskScoreResult"/> came from (section 14.4).</summary>
public enum RiskScoreSource
{
    /// <summary>Read and scored.</summary>
    Computed,

    /// <summary><c>FRAUD_SIGNALS_TABLE</c> is empty: LOW, no read.</summary>
    TableNotConfigured,

    /// <summary>
    /// The read failed: LOW at a user-facing gate, but a settlement sweep must skip the
    /// escrow for this run instead of treating the user as LOW.
    /// </summary>
    Unavailable
}

/// <summary>
/// A user's risk score (section 14.2). Server-internal: the tier and the score are never
/// returned to clients (section 17), so this type is not serialized.
/// </summary>
public sealed record RiskScoreResult
{
    /// <summary>Full score, AI advisory weight included. Decides MEDIUM and HIGH.</summary>
    public required int Score { get; init; }

    /// <summary>Score without the advisory weight. Decides CRITICAL.</summary>
    public required int EnforcementScore { get; init; }

    public required RiskTier Tier { get; init; }

    /// <summary>Rows were left unread after the page cap; the tier is then CRITICAL.</summary>
    public bool Truncated { get; init; }

    public RiskScoreSource Source { get; init; } = RiskScoreSource.Computed;

    /// <summary>LOW with score 0, for a read that did not happen or failed.</summary>
    public static RiskScoreResult Unscored(RiskScoreSource source) =>
        new() { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low, Source = source };
}
