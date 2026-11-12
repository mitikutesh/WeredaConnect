using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-33 (reputation-architecture.md section 13, fraud-detection-architecture.md section 15.3): the fraud
/// signals a recorded reputation event leads to. They run in <see cref="ReputationService.RecordAsync"/>
/// right after the event's transaction committed, so:
/// - only while <c>reputationPenalties</c> is on (nothing records events otherwise);
/// - once per event: a replayed record meets the existing slot and never comes here, and the signal
///   writer keeps one row per user, type and hour on top;
/// - never against the event: every hook is best effort, a failure is logged and the event stands.
///
/// The hooks:
/// - <c>NO_SHOW</c> and <c>DELIVERY_OVERDUE</c> with points: the same-named signal on the penalised
///   party, the order as the entity. Their producers are ESC-34 and ESC-36; when those start writing
///   the order fields the derivation reads, the signals follow with no further change;
/// - a decline or an expiry (<c>DECLINED_REQUEST</c>, <c>RESPONSE_EXPIRED</c>, with or without points):
///   the buyer decline pattern on the order's buyer, which feeds the FRD-09 pattern set.
/// </summary>
internal static class ReputationFraudHooks
{
    /// <summary>Log line of a hook that failed (the exception type only; the event is not affected).</summary>
    public const string FailedToken = "REPUTATION_FRAUD_HOOK_FAILED";

    public static async Task AfterRecordedAsync(
        IFraudDetectionService? fraud, TransactionPatternService? buyerPatterns, ReputationPolicySettings settings,
        ReputationEvent recorded, DateTime nowUtc, ILambdaLogger logger)
    {
        if (fraud == null || string.IsNullOrEmpty(recorded.OrderId))
        {
            return;
        }

        if (ReliabilitySignalOf(recorded) is { } signalType)
        {
            try
            {
                await fraud.RecordFraudSignalAsync(
                    recorded.UserId, signalType, recorded.OrderId, "order", ReliabilityDescription(recorded));
            }
            catch (Exception ex)
            {
                logger.LogWarning($"{FailedToken} hook=reliability code={recorded.Code} [{ex.GetType().Name}]");
            }
        }

        if (buyerPatterns != null && ReputationCodes.Requests.Contains(recorded.Code) && !string.IsNullOrEmpty(recorded.BuyerId))
        {
            try
            {
                var hits = await buyerPatterns.CheckBuyerDeclinePatternAsync(
                    recorded.BuyerId, recorded.OrderId, nowUtc, settings.BuyerDeclineSignalCount, settings.BuyerDeclineSignalDays);
                foreach (var hit in hits)
                {
                    await fraud.RecordFraudSignalAsync(recorded.BuyerId, hit.SignalType, recorded.OrderId, "order", hit.Description);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"{FailedToken} hook=buyer_decline code={recorded.Code} [{ex.GetType().Name}]");
            }
        }
    }

    /// <summary>The signal of a no-show or an overdue delivery that cost the party points; null for anything else.</summary>
    internal static string? ReliabilitySignalOf(ReputationEvent recorded)
    {
        if (recorded.Points <= 0)
        {
            return null; // not penalised (a policy of 0 points): reputation recorded it, the risk score ignores it
        }

        return recorded.Code switch
        {
            ReputationCodes.NoShow => FraudDetectionService.NoShowSignal,
            ReputationCodes.DeliveryOverdue => FraudDetectionService.DeliveryOverdueSignal,
            _ => null
        };
    }

    /// <summary>What Fraud Review shows: the role and the points, no person and no phone.</summary>
    private static string ReliabilityDescription(ReputationEvent recorded)
    {
        var party = string.IsNullOrEmpty(recorded.Party) ? "party" : recorded.Party;
        return recorded.Code == ReputationCodes.NoShow
            ? $"The {party} was judged a no-show at the pickup of this order ({recorded.Points} reputation points)."
            : $"The {party} did not deliver this order by its deadline ({recorded.Points} reputation points).";
    }
}
