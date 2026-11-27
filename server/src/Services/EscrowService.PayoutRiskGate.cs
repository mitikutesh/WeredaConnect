using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-29: the risk half of the shared payout gate (fraud-detection-architecture.md section 17.6). The
/// settlement gate of the sweeps (<see cref="PartySuspensionGate"/>) decides once, before a release. A leg
/// paid later (the trucker retry, the ledger sweep, a transport leg the identity gate held back) was never
/// asked again, so a payee suspended or turned CRITICAL in the meantime was still paid. Every automatic
/// payout now asks here, after the identity rules and before the lock or the call.
/// </summary>
public sealed partial class EscrowService
{
    /// <summary>
    /// Reads the payee's risk tier at payout. Wired in escrow-cron, the only Lambda that pays as the
    /// system. Without it only a suspended payee is blocked, as <see cref="PartySuspensionGate"/> does
    /// without a fraud service.
    /// </summary>
    internal IFraudDetectionService? PayoutRiskService { get; init; }

    /// <summary>The <c>reason</c> of the alarm line when the payee is suspended or CRITICAL.</summary>
    internal const string PayeeFlaggedSkipReason = "payee_flagged";

    /// <summary>
    /// True for a reason this gate gives. Such a block is never stored on the escrow row and never audited
    /// as an identity block: both feed the payee's "payout blocked" notification, which would tell a party
    /// about a fraud flag.
    /// </summary>
    internal static bool IsPayeeRiskBlock(string? reason) => reason is EscrowHoldFields.ReasonFraudConfirmed
        or EscrowHoldFields.ReasonAccountSuspended
        or EscrowHoldFields.ReasonRiskCritical
        or PartySuspensionGate.SkipScoreUnavailable;

    /// <summary>
    /// Why a system payout to <paramref name="payee"/> must not be made now, or null when it may. The
    /// reasons and their order are the settlement gate's: a suspension first (<c>fraud_confirmed</c>, else
    /// <c>account_suspended</c>), read from the row the identity rules already loaded, then the risk tier,
    /// Source before Tier (section 14.4): an unconfigured signals table is LOW, any other result that is
    /// not a computed score fails closed (<c>score_unavailable</c>), CRITICAL (a truncated read included)
    /// is <c>risk_critical</c>. An admin's own settlement is not asked (section 17: the admin decides).
    /// A block logs <c>RISK_SETTLEMENT_SKIPPED</c> (alarmed) and writes nothing: the leg stays owed and the
    /// next run asks again.
    /// </summary>
    private async Task<string?> PayeeRiskBlockAsync(UserDto? payee, string actor, string escrowId, string leg)
    {
        if (actor != SystemActor || payee == null)
        {
            return null;
        }

        string? reason = null;
        if (payee.Suspended)
        {
            reason = payee.SuspendReason == EscrowHoldFields.ReasonFraudConfirmed
                ? EscrowHoldFields.ReasonFraudConfirmed
                : EscrowHoldFields.ReasonAccountSuspended;
        }
        else if (PayoutRiskService != null)
        {
            RiskScoreResult? score;
            try
            {
                score = await PayoutRiskService.GetUserRiskScoreAsync(payee.UserId);
            }
            catch (Exception)
            {
                score = null;
            }

            if (score is not { Source: RiskScoreSource.TableNotConfigured })
            {
                reason = score is not { Source: RiskScoreSource.Computed }
                    ? PartySuspensionGate.SkipScoreUnavailable
                    : score.Tier == RiskTier.Critical ? EscrowHoldFields.ReasonRiskCritical : null;
            }
        }

        if (reason != null)
        {
            var unread = reason == PartySuspensionGate.SkipScoreUnavailable;
            AlarmLog(MoneyAlarmLog.RiskSettlementSkipped(
                escrowId, $"payout-{leg}", unread ? reason : PayeeFlaggedSkipReason, unread ? null : reason));
        }

        return reason;
    }

    /// <summary>The refusal of a payout this gate blocks. Only the system caller ever gets it.</summary>
    private static EscrowResult<string> PayeeRiskRefusal(string legLabel) => EscrowResult<string>.Failure(
        403, "ESCROW_029", $"Payout blocked: the {legLabel}'s account is under review.");
}
