using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
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
        return new ReputationService(new ReputationEventRepository(dynamoDb, reputationEventsTable, usersTable), settings, fraud);
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
