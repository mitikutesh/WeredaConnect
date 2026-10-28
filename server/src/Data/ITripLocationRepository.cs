namespace WeredaConnect.Server.Data;

/// <summary>
/// LT-05: one check-in or sharing event of a trip (live-trip-api.md 1.0.0 section 1, Storage). Table
/// <c>{prefix}-trip-locations</c>: PK <c>tripId</c>, SK <c>sortKey</c> = <c>{capturedAt}#{kind}</c>
/// (ISO 8601 UTC with milliseconds), so a trip's rows read in capture order. Coordinates at 4
/// decimals; <c>Latitude</c>, <c>Longitude</c>, <c>AccuracyM</c>, <c>Source</c> and <c>Mocked</c> only
/// on a <c>fix</c>, <c>PausedUntil</c> only on <c>paused</c>. TTL on <c>expiresAt</c> (epoch seconds,
/// <c>capturedAt</c> + 60 days). Location never goes on trip rows (live-trip.md D6).
/// </summary>
public sealed record TripLocationRow(
    string TripId,
    string SortKey,
    string TruckerId,
    double? Latitude,
    double? Longitude,
    double? AccuracyM,
    string? Source,
    bool? Mocked,
    string? PausedUntil,
    string ReceivedAt,
    long ExpiresAt);

public interface ITripLocationRepository
{
    /// <summary>Puts the row unless one with the same key exists. True when it was new.</summary>
    Task<bool> TryPutAsync(TripLocationRow row);

    /// <summary>
    /// Every sort key of the trip that starts with <paramref name="sortKeyPrefix"/> (all pages, read
    /// strongly consistent): with a clock-hour prefix, the hour's pings for the fix rate limit and
    /// the repeat check.
    /// </summary>
    Task<List<string>> ListSortKeysAsync(string tripId, string sortKeyPrefix);
}
