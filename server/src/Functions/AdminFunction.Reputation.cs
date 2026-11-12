using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-29 (reputation-api.md 8.4): a suspended owner counts as restricted for buyers, so a suspend
/// or an unsuspend must reach the owner fields copied onto their crop and trip rows. The admin
/// Lambda leaves the day's <c>HEAL#</c> marker on the user's reputation events; the daily job
/// (<c>reputation_daily</c>) recomputes the aggregate, which reads <c>suspended</c>, and rewrites
/// the copies. Until it runs, an order to the suspended owner is still refused by the live check
/// of 8.2. The admin reputation actions themselves are ESC-32.
/// </summary>
public partial class AdminFunction
{
    /// <summary><c>reputationPenalties</c>: off (every environment today) writes no marker.</summary>
    internal bool ReputationPenaltiesEnabled { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary>The reputation-events table; null or empty writes no marker.</summary>
    internal string? ReputationEventsTable { get; init; } = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE");

    /// <summary>Best effort: a failure is logged with the copy token and never fails the admin action.</summary>
    private async Task MarkReputationCopyDueAsync(string userId, string action, ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled || string.IsNullOrEmpty(ReputationEventsTable)
            || action is not ("suspend" or "unsuspend"))
        {
            return;
        }

        try
        {
            var heal = ReputationEventRepository.HealUpsert(ReputationEventsTable, userId, DateTime.UtcNow);
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = heal.TableName,
                Key = heal.Key,
                UpdateExpression = heal.UpdateExpression,
                ExpressionAttributeNames = heal.ExpressionAttributeNames,
                ExpressionAttributeValues = heal.ExpressionAttributeValues
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{Services.ReputationService.CopyFailedToken} step=admin_{action} user={MoneyAlarmLog.LogId(userId)} [{ex.GetType().Name}]");
        }
    }
}
