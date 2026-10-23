using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FRD-04: the caller's risk tier at a user action (fraud-detection-architecture.md sections 17, 17.1
/// and 17.3). CRITICAL refuses new crop listings, trips, orders and payments with
/// <see cref="AccountHeldCode"/> and writes nothing; MEDIUM and HIGH add proofs to the payment step-up
/// (<see cref="StepUpPolicy.CheckPayment"/>). A gate holds nothing: only a settlement sweep writes
/// <c>held</c>. The tier and the score are never returned to clients.
/// </summary>
public static class RiskGatePolicy
{
    /// <summary>The account is under review: <c>403</c> on REST, an operation error on <c>/sync</c>.</summary>
    public const string AccountHeldCode = "FRAUD_ACCOUNT_HELD";

    public const string AccountHeldMessage = "Your account is under review. Some actions are paused. Contact support.";

    /// <summary>The <c>/sync</c> operation message: the code as its prefix, as for every sync rejection.</summary>
    public static string SyncMessage => $"{AccountHeldCode}: {AccountHeldMessage}";

    /// <summary>
    /// The tier a user gate acts on. Source before Tier (section 14.4): anything but a computed score is
    /// LOW here, that is an unconfigured signals table or a failed read (the service logs
    /// <c>RISK_SCORE_UNAVAILABLE</c>), so the gate keeps today's checks and the baseline step-up still
    /// applies. A truncated read is computed and CRITICAL.
    /// </summary>
    public static RiskTier TierAtUserGate(RiskScoreResult score)
        => score is { Source: RiskScoreSource.Computed } ? score.Tier : RiskTier.Low;
}
