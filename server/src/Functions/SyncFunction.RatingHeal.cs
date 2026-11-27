using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-162 (rating-api.md 1.5.0 "Stored aggregate" step 4): a rating for a farmer or trucker whose
/// users row has no <c>ratingsAggregatedAt</c> yet stamps their full aggregate right after the
/// create, before the reputation refresh reads it. Best effort: the rating is stored either way,
/// and the order-cron action <c>reputation_backfill_ratings</c> reaches whoever is left.
/// </summary>
public partial class SyncFunction
{
    /// <summary>The heal; null without the ratings table (tests set it in the initializer).</summary>
    internal IRatingAggregateHeal? RatingHeal { get; init; }

    /// <summary>
    /// Heals the ratee read before the create (the ACC-02 check), only when that row is unstamped
    /// and still in the rated role. A stale read costs one Query: the heal's conditions refuse a row
    /// stamped meanwhile.
    /// </summary>
    private async Task HealRateeAggregateAsync(string rateeId, UserDto? ratee, string rateeRole, ILambdaContext context)
    {
        if (RatingHeal == null || ratee == null || !string.IsNullOrEmpty(ratee.RatingsAggregatedAt)
            || !string.Equals(ratee.Role, rateeRole, StringComparison.Ordinal))
        {
            return;
        }

        await RatingHeal.TryStampAsync(rateeId, rateeRole, context.Logger);
    }
}
