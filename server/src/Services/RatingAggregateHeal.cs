using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-162 (rating-api.md 1.5.0 "Stored aggregate" step 4): stamps the stored rating aggregate of a
/// farmer or trucker whose users row still has no <c>ratingsAggregatedAt</c>, a row the ESC-28
/// backfill did not reach (no <c>woreda</c>, so not on <c>RoleWaredaIndex</c>). The <c>/sync</c>
/// rating create calls it right after its transaction, so the ratee's next readers get the exact
/// stored sum and count.
/// </summary>
public interface IRatingAggregateHeal
{
    /// <summary>
    /// Best effort, never throws: true when this call stamped the row. False for another role, for a
    /// user with more ratings still to add than a request may handle (the order-cron action
    /// <c>reputation_backfill_ratings</c> stamps them), when another writer stamped the row first, and
    /// on any failure (logged).
    /// </summary>
    Task<bool> TryStampAsync(string userId, string role, ILambdaLogger logger);
}

/// <summary>
/// The heal of <see cref="IRatingAggregateHeal"/>: <see cref="RatingAggregateBackfill.StampSmallAsync"/>
/// for one user, at most <see cref="MaxRatings"/> ratings (one transaction). It counts each rating
/// once even against a concurrent rating create, another heal or the backfill: every rating it adds
/// is flagged <c>inAggregate</c> in the same transaction, conditioned on the flag being absent, and
/// the stamp is conditioned on <c>ratingsAggregatedAt</c> being absent; the sum and count are
/// <c>ADD</c>ed, so a create's own <c>ADD</c> is never lost. A lost race cancels the whole
/// transaction and writes nothing.
/// </summary>
public sealed class RatingAggregateHeal : IRatingAggregateHeal
{
    /// <summary>Logged on a failure other than a lost race; the order-cron backfill is the fallback.</summary>
    public const string FailedToken = "RATING_AGGREGATE_HEAL_FAILED";

    /// <summary>The most ratings a heal adds: one transaction with the users row.</summary>
    public const int MaxRatings = RatingAggregateBackfill.RatingsPerChunk;

    private readonly RatingAggregateBackfill _backfill;

    public RatingAggregateHeal(IAmazonDynamoDB dynamoDb, string usersTable, string ratingsTable)
    {
        _backfill = new RatingAggregateBackfill(dynamoDb, usersTable, ratingsTable);
    }

    /// <summary>The stamp's clock; tests set it.</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <inheritdoc />
    public async Task<bool> TryStampAsync(string userId, string role, ILambdaLogger logger)
    {
        if (string.IsNullOrEmpty(userId) || role is not ("farmer" or "trucker"))
        {
            return false;
        }

        try
        {
            if (await _backfill.StampSmallAsync(userId, role, Clock(), MaxRatings))
            {
                logger.LogInformation($"RATING_AGGREGATE_HEALED user={MoneyAlarmLog.LogId(userId)}");
                return true;
            }

            logger.LogInformation(
                $"RATING_AGGREGATE_HEAL_SKIPPED user={MoneyAlarmLog.LogId(userId)}: more than {MaxRatings} ratings to add, left to reputation_backfill_ratings");
            return false;
        }
        catch (TransactionCanceledException)
        {
            // Stamped meanwhile, the role changed, the account is being deleted, or a concurrent
            // transaction on the same rows: nothing was written.
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"{FailedToken} user={MoneyAlarmLog.LogId(userId)} [{ex.GetType().Name}]");
            return false;
        }
    }
}
