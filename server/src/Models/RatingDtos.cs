namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /ratings response: a page of ratings for a ratee plus aggregate figures.
/// </summary>
public sealed record GetRatingsResponse
{
    /// <summary>Ratings received by the queried ratee, newest first.</summary>
    public required List<RatingDto> Ratings { get; init; }

    /// <summary>Aggregate average + count across the ratee's ratings.</summary>
    public required RatingSummaryDto Summary { get; init; }

    /// <summary>
    /// 1.2.0: the ratee's public reputation (reputation-api.md section 3), for the current role
    /// with <c>reputationPublicTags</c> on; saves the detail screens a second call.
    /// </summary>
    public ReputationDto? Reputation { get; init; }

    /// <summary>Pagination token for the next page (null if none).</summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /ratings/{id} response for a single rating.
/// </summary>
public sealed record GetRatingResponse
{
    /// <summary>The rating details.</summary>
    public required RatingDto Rating { get; init; }
}

/// <summary>
/// Aggregate rating figures for a rated user.
/// </summary>
public sealed record RatingSummaryDto
{
    /// <summary>Average star rating (0 when there are no ratings).</summary>
    public required double Average { get; init; }

    /// <summary>Number of ratings.</summary>
    public required int Count { get; init; }

    /// <summary>1.2.0: the role the list and the summary are for; absent when the ratee's row was not read.</summary>
    public string? RateeRole { get; init; }

    /// <summary>
    /// 1.2.0: the average minus the reputation deduction (reputation-api.md 1.4), one decimal.
    /// Absent with no ratings, with <c>reputationPublicTags</c> off, or for a role that is not the
    /// ratee's current one.
    /// </summary>
    public decimal? EffectiveRating { get; init; }
}

/// <summary>
/// Rating data transfer object.
/// A buyer's rating of a counterparty (farmer or trucker) for a delivered order.
/// Immutable — exactly one rating per (order, ratee).
/// </summary>
public sealed record RatingDto
{
    /// <summary>Deterministic id, "{orderId}:{rateeId}".</summary>
    public required string RatingId { get; init; }

    /// <summary>Order this rating is about.</summary>
    public required string OrderId { get; init; }

    /// <summary>User who left the rating (the buyer).</summary>
    public required string RaterId { get; init; }

    /// <summary>User being rated (the order's farmer or trucker).</summary>
    public required string RateeId { get; init; }

    /// <summary>Role of the ratee: "farmer" or "trucker".</summary>
    public required string RateeRole { get; init; }

    /// <summary>Star rating in [1, 5].</summary>
    public required int Stars { get; init; }

    /// <summary>Optional free-text comment.</summary>
    public string? Comment { get; init; }

    /// <summary>ISO 8601 UTC timestamp of creation.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>ISO 8601 UTC timestamp of last update (== CreatedAt; immutable).</summary>
    public required string UpdatedAt { get; init; }

    /// <summary>Version for optimistic concurrency control.</summary>
    public required int Version { get; init; }
}
