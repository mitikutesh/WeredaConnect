using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-28 (rating-api.md 1.2.0 "Stored aggregate" step 3): after a rating create commits, the
/// ratee's reputation aggregate is recomputed with the new rating sum and count, best effort. A
/// failure is logged (<c>REPUTATION_COPY_FAILED</c>) and never fails the rating: the transaction
/// left a <c>HEAL#</c> marker, so the daily job does it.
/// </summary>
public partial class SyncFunction
{
    /// <summary>The log token the monitoring stack counts (rate alarm).</summary>
    internal const string ReputationCopyFailedToken = "REPUTATION_COPY_FAILED";

    /// <summary>
    /// The service that refreshes a ratee's aggregate; null while <c>reputationPenalties</c> is off
    /// or the reputation-events table is not configured (the rating sum and count are still kept).
    /// </summary>
    internal ReputationService? Reputation { get; init; }

    /// <summary>The refresh service of the Lambda: built only with the flag on, so no policy read happens while it is off.</summary>
    private static ReputationService? BuildReputationService(
        IAmazonDynamoDB dynamoDb, string? reputationEventsTable, string usersTable, IFraudDetectionService? fraud)
    {
        if (!ReputationFlags.PenaltiesEnabled || string.IsNullOrEmpty(reputationEventsTable))
        {
            return null;
        }

        var settings = ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line));
        return new ReputationService(
            new ReputationEventRepository(dynamoDb, reputationEventsTable, usersTable), settings, fraud,
            copies: ReputationCopyRepository.FromEnvironment(dynamoDb));
    }

    /// <summary>
    /// ESC-29: the <c>reputationPenalties</c> flag for the gates of reputation-api.md 8.1 and 8.2
    /// and the owner stamps of section 7. Off (every environment today): no gate, no stamp.
    /// </summary>
    internal bool ReputationGatesEnabled { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary>ESC-29, <c>reputationPublicTags</c>: a crop or trip row in <c>serverData</c> carries the owner fields. Off by default.</summary>
    internal bool ReputationPublicTags { get; init; } = ReputationFlags.PublicTagsEnabled;

    /// <summary>
    /// The caller's users row, read strongly consistent once per batch for the ACC-02 guard; the
    /// suspension refusal, the own gate and the owner stamps reuse it. Null without a users table.
    /// </summary>
    private UserDto? _batchCaller;

    private ReputationPolicySettings ReputationSettings => Reputation?.Settings ?? ReputationPolicySettings.Defaults;

    /// <summary>
    /// 8.1: the <c>REPUTATION_RESTRICTED</c> refusal of this operation when the caller is restricted
    /// now, else null. Nothing is written.
    /// </summary>
    private SyncOperationResult? RefuseIfRestricted(SyncOperationRequest operation)
    {
        if (!ReputationGatesEnabled || ReputationGate.CheckOwn(_batchCaller, DateTime.UtcNow) is not { } message)
        {
            return null;
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = ReputationGate.ForClient(message, _batchAppBuild, operation.EntityType)
        };
    }

    /// <summary>
    /// 8.2: the refusal of an order create whose listing farmer or trip trucker is restricted or
    /// suspended (one strongly consistent GetItem per owner; the copied <c>ownerTier</c> in the
    /// buyer's cache may be stale). A failed read refuses with a retryable text and writes nothing.
    /// An order that already exists is not refused here: its create is a replay and gets the
    /// conflict answer, so the client never drops an order the server holds.
    /// </summary>
    private async Task<SyncOperationResult?> RefuseRestrictedCounterpartyAsync(
        SyncOperationRequest operation, ILambdaContext context, params string?[] ownerIds)
    {
        if (!ReputationGatesEnabled || _userRepo == null)
        {
            return null;
        }

        string? message = null;
        try
        {
            foreach (var ownerId in ownerIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
            {
                var owner = await _userRepo.GetUserByIdAsync(ownerId!, consistentRead: true);
                message = ReputationGate.CheckCounterparty(owner, DateTime.UtcNow);
                if (message != null)
                {
                    break;
                }
            }

            if (message == null || await _orderRepo.GetByIdAsync(operation.EntityId) != null)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Order create {MoneyAlarmLog.LogId(operation.EntityId)}: the owner's account could not be checked: [{ex.GetType().Name}]");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = ReputationGate.CounterpartyUnreadableMessage
            };
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = ReputationGate.ForClient(message, _batchAppBuild, operation.EntityType)
        };
    }

    /// <summary>
    /// Section 7: stamp the owner's reputation fields onto a row being created by the caller, from
    /// the row read for the batch. Only while <c>reputationPenalties</c> is on: the copies are kept
    /// current by the reputation writes, which run only with that flag.
    /// </summary>
    private void StampOwnerReputation(Dictionary<string, object> data)
    {
        if (!ReputationGatesEnabled || _batchCaller == null)
        {
            return;
        }

        var fields = ReputationGate.OwnerFields(_batchCaller, DateTime.UtcNow, ReputationSettings);
        data[OwnerReputationFields.TierField] = fields.Tier;
        data[OwnerReputationFields.RatingCountField] = fields.RatingCount;
        data[OwnerReputationFields.AtField] = ReputationPolicy.Iso(fields.At);
        if (fields.EffectiveRating is { } rating)
        {
            data[OwnerReputationFields.EffectiveRatingField] = rating;
        }

        if (fields.ReliabilityPct is { } reliability)
        {
            data[OwnerReputationFields.ReliabilityPctField] = reliability;
        }
    }

    /// <summary>Recompute the ratee's aggregate after their rating was stored; never throws.</summary>
    private async Task RefreshRateeReputationAsync(string rateeId, ILambdaContext context)
    {
        if (Reputation == null)
        {
            return;
        }

        try
        {
            await Reputation.RefreshAsync(rateeId, context.Logger);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{ReputationCopyFailedToken} step=rating_refresh user={MoneyAlarmLog.LogId(rateeId)} [{ex.GetType().Name}]");
        }
    }
}
