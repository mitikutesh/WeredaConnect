using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>A parsed <c>location_ping</c> (live-trip-api.md section 1). Times are UTC.</summary>
public sealed record LocationPing(
    string TripId,
    string Kind,
    DateTime CapturedAtUtc,
    string? Source,
    double? Latitude,
    double? Longitude,
    double? AccuracyM,
    bool? Mocked,
    DateTime? PausedUntilUtc)
{
    /// <summary>The trip-locations sort key: <c>{capturedAt}#{kind}</c>.</summary>
    public string SortKey => $"{LocationPingRules.FormatUtc(CapturedAtUtc)}#{Kind}";
}

/// <summary>Whether a trip's deliveries let a check-in captured at a given time in.</summary>
public enum LocationCoverage
{
    /// <summary>A delivery is on the road, or was on the road after the capture time.</summary>
    Active,

    /// <summary>No delivery has been picked up yet, or one may still be: the client re-queues the ping.</summary>
    NotActive,

    /// <summary>Every delivery that was picked up left the road before the capture time, and none is pending.</summary>
    Ended
}

/// <summary>
/// LT-05: the payload rules, the time window and the active-trip rule of a <c>location_ping</c>
/// (live-trip-api.md 1.0.0 section 1, live-trip.md section 6.1). Pure: the caller passes the
/// server clock and the trip's deliveries.
/// </summary>
public static class LocationPingRules
{
    public const string EntityType = "location_ping";

    public const string ValidationCode = "LOCATION_VALIDATION";
    public const string NotTruckerCode = "LOCATION_NOT_TRUCKER";
    public const string NotActiveCode = "LOCATION_NOT_ACTIVE";
    public const string SharingEndedCode = "LOCATION_SHARING_ENDED";
    public const string RateLimitedCode = "LOCATION_RATE_LIMITED";
    public const string StaleCode = "LOCATION_STALE";

    public const string KindFix = "fix";
    public const string KindPaused = "paused";
    public const string KindResumed = "resumed";
    public const string KindDeclined = "declined";

    /// <summary>At most this many <c>fix</c> pings per trip per clock hour (UTC) of <c>capturedAt</c>.</summary>
    public const int MaxFixesPerHour = 20;

    public const double MinLatitude = 3;
    public const double MaxLatitude = 15;
    public const double MinLongitude = 32.5;
    public const double MaxLongitude = 48.5;
    public const double MaxAccuracyM = 5000;

    /// <summary>Longest trip id accepted (trip ids are UUIDs); bounds the table keys.</summary>
    public const int MaxTripIdLength = 128;

    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(18);

    /// <summary>Rows expire this long after <c>capturedAt</c> (the table's TTL on <c>expiresAt</c>).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        KindFix, KindPaused, KindResumed, KindDeclined
    };

    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "hourly", "scan", "near_stop", "manual", "arrived", "start"
    };

    // "version" is accepted and ignored: the sync clients echo it on every operation.
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "tripId", "kind", "source", "capturedAt", "latitude", "longitude", "accuracyM", "mocked",
        "pausedUntil", "version"
    };

    /// <summary>
    /// Parse and validate the payload. Returns the problems (empty when valid); <paramref name="ping"/>
    /// is set only when there are none. Fields that belong to another kind (a <c>source</c> on a
    /// <c>paused</c> event) are ignored and not stored, so an event is never lost for an extra field.
    /// </summary>
    public static List<string> Parse(string entityId, Dictionary<string, object> data, out LocationPing? ping)
    {
        ping = null;
        var errors = new List<string>();

        var unknown = data.Keys.Where(k => !AllowedFields.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            errors.Add($"Unknown fields: {string.Join(", ", unknown)}.");
        }

        var tripId = ReadString(data, "tripId", out var tripIdWrongType);
        if (tripIdWrongType)
        {
            errors.Add("tripId must be a string.");
            tripId = null;
        }
        else if (string.IsNullOrWhiteSpace(tripId))
        {
            errors.Add("tripId is required.");
            tripId = null;
        }
        else if (tripId.Length > MaxTripIdLength)
        {
            errors.Add($"tripId must be at most {MaxTripIdLength} characters.");
            tripId = null;
        }

        var kind = ReadString(data, "kind", out _);
        if (kind == null || !Kinds.Contains(kind))
        {
            errors.Add("kind must be one of: fix, paused, resumed, declined.");
            kind = null;
        }

        var capturedAt = ReadTime(data, "capturedAt", errors);

        if (tripId != null && capturedAt is { } captured)
        {
            var expected = $"{tripId}#{ToEpochMs(captured).ToString(CultureInfo.InvariantCulture)}";
            if (!string.Equals(entityId, expected, StringComparison.Ordinal))
            {
                errors.Add("entityId must be '{tripId}#{capturedAt in epoch ms}'.");
            }
        }

        string? source = null;
        double? latitude = null, longitude = null, accuracyM = null;
        bool? mocked = null;
        DateTime? pausedUntil = null;

        if (kind == KindFix)
        {
            source = ReadString(data, "source", out _);
            if (source == null || !Sources.Contains(source))
            {
                errors.Add("source must be one of: hourly, scan, near_stop, manual, arrived, start.");
                source = null;
            }

            latitude = ReadBoundedNumber(data, "latitude", MinLatitude, MaxLatitude, errors);
            longitude = ReadBoundedNumber(data, "longitude", MinLongitude, MaxLongitude, errors);
            accuracyM = ReadBoundedNumber(data, "accuracyM", 0, MaxAccuracyM, errors);

            if (data.TryGetValue("mocked", out var mockedRaw) && !IsNullValue(mockedRaw))
            {
                if (TryReadBool(mockedRaw, out var m))
                {
                    mocked = m;
                }
                else
                {
                    errors.Add("mocked must be a boolean.");
                }
            }
        }
        else if (kind == KindPaused
                 && ReadTime(data, "pausedUntil", errors) is { } until
                 && capturedAt is { } from)
        {
            if (until <= from || until - from > MaxPause)
            {
                errors.Add("pausedUntil must be after capturedAt and at most 18 hours later.");
            }
            else
            {
                pausedUntil = until;
            }
        }

        if (errors.Count == 0)
        {
            ping = new LocationPing(tripId!, kind!, capturedAt!.Value, source, latitude, longitude, accuracyM, mocked, pausedUntil);
        }

        return errors;
    }

    /// <summary>More than 2 minutes in the future or more than 7 days old by the server clock.</summary>
    public static bool IsStale(DateTime capturedAtUtc, DateTime nowUtc)
        => capturedAtUtc > nowUtc + MaxFutureSkew || capturedAtUtc < nowUtc - MaxAge;

    /// <summary>
    /// The active-trip rule over every delivery on the trip. A delivery that is <c>pickedUp</c> or
    /// <c>inTransit</c> lets the ping in, and so does one that left those statuses after
    /// <paramref name="capturedAtUtc"/> (a late offline ping is kept): <c>delivered</c> and
    /// <c>disputed</c> left at <c>confirmedAt</c>, <c>cancelled</c> (an admin verdict after pickup) at
    /// <c>cancelledAt</c>. A delivery cancelled before pickup was never on the road and counts for
    /// nothing. With none of those, a <c>pending</c> delivery may still be picked up, so the answer is
    /// <see cref="LocationCoverage.NotActive"/>; otherwise every picked-up delivery left before the
    /// capture (<see cref="LocationCoverage.Ended"/>), or none was ever picked up (NotActive).
    /// </summary>
    public static LocationCoverage Coverage(IEnumerable<Dictionary<string, object>> deliveries, DateTime capturedAtUtc)
    {
        var anyPending = false;
        var anyEnded = false;

        foreach (var delivery in deliveries)
        {
            var status = Text(delivery, "status");
            switch (status)
            {
                case "pickedUp":
                case "inTransit":
                    return LocationCoverage.Active;

                case "pending":
                    anyPending = true;
                    break;

                case "delivered":
                case "disputed":
                case "cancelled":
                    if (status != "delivered" && string.IsNullOrEmpty(Text(delivery, "pickedUpAt")))
                    {
                        break;
                    }

                    // A row without a readable time is given the benefit of the doubt: storing a ping
                    // costs little, and the track and trail reads only show their own window.
                    var left = LeftRoadAt(delivery, status);
                    if (left == null || left > capturedAtUtc)
                    {
                        return LocationCoverage.Active;
                    }

                    anyEnded = true;
                    break;
            }
        }

        return anyEnded && !anyPending ? LocationCoverage.Ended : LocationCoverage.NotActive;
    }

    /// <summary>ISO 8601 UTC with milliseconds, the format of every time in the trip-locations table.</summary>
    public static string FormatUtc(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The sort key prefix shared by every ping of the clock hour (UTC) of <paramref name="capturedAtUtc"/>.</summary>
    public static string HourPrefix(DateTime capturedAtUtc)
        => capturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:", CultureInfo.InvariantCulture);

    /// <summary>Whether a sort key is a <c>fix</c>.</summary>
    public static bool IsFixSortKey(string sortKey) => sortKey.EndsWith("#" + KindFix, StringComparison.Ordinal);

    /// <summary>Coordinates are stored at 4 decimals (about 11 m).</summary>
    public static double RoundCoordinate(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>The row's TTL: <c>capturedAt</c> + 60 days, in epoch seconds.</summary>
    public static long ExpiresAt(DateTime capturedAtUtc)
        => new DateTimeOffset(DateTime.SpecifyKind(capturedAtUtc, DateTimeKind.Utc) + Retention).ToUnixTimeSeconds();

    private static long ToEpochMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime? LeftRoadAt(Dictionary<string, object> delivery, string status)
    {
        var keys = status == "cancelled"
            ? new[] { "cancelledAt", "disputeResolvedAt", "updatedAt" }
            : new[] { "confirmedAt", "disputeResolvedAt", "updatedAt" };
        foreach (var key in keys)
        {
            if (TryParseUtc(Text(delivery, key), out var at))
            {
                return at;
            }
        }

        return null;
    }

    private static string? Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool TryParseUtc(string? raw, out DateTime utc)
    {
        utc = default;
        return !string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    private static bool IsNullValue(object? value)
        => value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    /// <summary>A string field; null when absent, null or not a string (<paramref name="wrongType"/> for the last).</summary>
    private static string? ReadString(Dictionary<string, object> data, string key, out bool wrongType)
    {
        wrongType = false;
        if (!data.TryGetValue(key, out var value) || IsNullValue(value))
        {
            return null;
        }

        switch (value)
        {
            case string s:
                return s;
            case JsonElement { ValueKind: JsonValueKind.String } je:
                return je.GetString();
            default:
                wrongType = true;
                return null;
        }
    }

    /// <summary>A required ISO 8601 time, as UTC (a time without an offset is read as UTC), cut to milliseconds.</summary>
    private static DateTime? ReadTime(Dictionary<string, object> data, string key, List<string> errors)
    {
        var raw = ReadString(data, key, out var wrongType);
        if (raw == null && !wrongType)
        {
            errors.Add($"{key} is required.");
            return null;
        }

        if (raw == null || !DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            errors.Add($"{key} must be an ISO 8601 date and time.");
            return null;
        }

        // The entity id and the sort key carry milliseconds, so finer digits are dropped.
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private static double? ReadBoundedNumber(
        Dictionary<string, object> data, string key, double min, double max, List<string> errors)
    {
        if (!data.TryGetValue(key, out var value) || IsNullValue(value))
        {
            errors.Add($"{key} is required for fix.");
            return null;
        }

        if (!TryReadNumber(value, out var number))
        {
            errors.Add($"{key} must be a number.");
            return null;
        }

        if (number < min || number > max)
        {
            errors.Add($"{key} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
            return null;
        }

        return number;
    }

    private static bool TryReadNumber(object value, out double number)
    {
        number = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            _ => double.NaN
        };
        return !double.IsNaN(number) && !double.IsInfinity(number);
    }

    private static bool TryReadBool(object value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                result = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
