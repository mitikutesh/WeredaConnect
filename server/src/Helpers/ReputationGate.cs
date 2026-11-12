using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-29 (reputation-api.md section 8, reputation-architecture.md section 10): the pure checks
/// behind <c>REPUTATION_RESTRICTED</c> and the owner fields stamped onto a new crop or trip row.
/// Everything is decided from the users row as read (the <c>KycPolicy</c> pattern): no I/O here.
/// </summary>
public static class ReputationGate
{
    public const string Code = "REPUTATION_RESTRICTED";

    /// <summary>The counterparty refusal of 8.2: the owner's dates are never shown to the buyer.</summary>
    public const string CounterpartyMessage =
        Code + ": counterparty: This farmer or trucker can't take new orders right now.";

    /// <summary>8.2: the owner's row could not be read. No prefix, so every client retries.</summary>
    public const string CounterpartyUnreadableMessage =
        "The seller's account could not be checked. Please try again later.";

    /// <summary>
    /// 8.1: the refusal for a restricted caller's new listing, trip or accept, or null when the
    /// caller may go on. A suspended caller never reaches this (the whole batch is refused, 8.4).
    /// </summary>
    public static string? CheckOwn(UserDto? caller, DateTime now)
    {
        var snapshot = caller?.Reputation;
        if (snapshot == null || !snapshot.IsRestrictedAt(now))
        {
            return null;
        }

        return snapshot.RestrictionUntil is { } until
            ? $"{Code}: until={ReputationPolicy.Iso(until)}: Your account can't take new orders until "
              + $"{until.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}. Your current orders continue."
            : $"{Code}: until=review: Your account can't take new orders while it is under review. Your current orders continue.";
    }

    /// <summary>
    /// 8.2: the refusal for an order (or a transport choice) whose listing farmer or trip trucker
    /// is restricted or suspended, or null. A missing row is not a restriction: the listing and trip
    /// rules answer that themselves.
    /// </summary>
    public static string? CheckCounterparty(UserDto? owner, DateTime now)
        => owner != null && (owner.Suspended || owner.Reputation?.IsRestrictedAt(now) == true)
            ? CounterpartyMessage
            : null;

    /// <summary>
    /// The text for a client that sends no readable <c>X-App-Build</c> (build 1 or older): it may
    /// predate the client that knows <c>REPUTATION_RESTRICTED</c> and would retry an unknown code
    /// (TASK FUP-75), so the same sentence goes out under a code every client treats as permanent:
    /// <c>ORDER_VALIDATION</c> on an order, <c>SYNC_VALIDATION</c> elsewhere.
    /// </summary>
    public static string ForClient(string message, int? clientBuild, string entityType)
    {
        if (clientBuild != null || !message.StartsWith(Code + ":", StringComparison.Ordinal))
        {
            return message;
        }

        var text = message[(message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
        return (entityType == "order" ? "ORDER_VALIDATION: " : "SYNC_VALIDATION: ") + text;
    }

    /// <summary>
    /// Section 7: the owner fields of a row created at <paramref name="now"/> by this owner, from
    /// the stored aggregate (an absent map reads as <c>good</c>, no reliability). The rating part
    /// uses the stored sum and count only once they are exact (<c>ratingsAggregatedAt</c>).
    /// </summary>
    public static OwnerReputationFields OwnerFields(UserDto owner, DateTime now, ReputationPolicySettings settings)
    {
        var aggregate = ReputationPolicy.FromSnapshot(
            owner.Reputation, StoredRatings(owner.RatingSum, owner.RatingCount, owner.RatingsAggregatedAt), owner.Suspended, now, settings);
        return OwnerFields(aggregate, StoredRatings(owner.RatingSum, owner.RatingCount, owner.RatingsAggregatedAt).Count, now);
    }

    /// <summary>The owner fields of an aggregate computed at <paramref name="now"/>.</summary>
    public static OwnerReputationFields OwnerFields(ReputationAggregate aggregate, long ratingCount, DateTime now) => new()
    {
        Tier = aggregate.Tier,
        EffectiveRating = aggregate.EffectiveRating,
        RatingCount = ratingCount,
        ReliabilityPct = aggregate.ReliabilityPct,
        At = now
    };

    /// <summary>
    /// The stored rating sum and count, or none while the row has no <c>ratingsAggregatedAt</c>: an
    /// unstamped row (a farmer or trucker the backfill skipped) holds only the ratings created since
    /// the deploy, which would understate the average (rating-api.md "Stored aggregate" step 4).
    /// </summary>
    public static (long Sum, long Count) StoredRatings(long ratingSum, long ratingCount, string? ratingsAggregatedAt)
        => string.IsNullOrEmpty(ratingsAggregatedAt) ? (0, 0) : (ratingSum, ratingCount);
}
