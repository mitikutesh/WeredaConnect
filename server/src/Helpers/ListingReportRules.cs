using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>A parsed <c>listing_report</c> (listing-report-api.md section 1).</summary>
public sealed record ListingReportInput(string ListingType, string ListingId, string Reason);

/// <summary>
/// FUP-67: rules of the <c>listing_report</c> sync entity (listing-report-api.md 1.0.0, sync-protocol.md
/// 1.44.0). A buyer reports a crop listing or a trip to the admins with a reason code; there is no free
/// text, so a report holds no personal data beyond pseudonymous ids. Every refusal starts with
/// <c>SYNC_VALIDATION</c>, which every client already treats as permanent.
/// </summary>
public static class ListingReportRules
{
    public const string EntityType = "listing_report";

    public const string ListingCrop = "crop";
    public const string ListingTrip = "trip";

    /// <summary>Reports a buyer may send per UTC day; more are refused (the duplicate of one already sent is not counted).</summary>
    public const int DailyLimit = 10;

    /// <summary>Days a report row is kept (TTL on <c>expiresAt</c>), reviewed or not.</summary>
    public const int TtlDays = 180;

    public const int MaxListingIdLength = 128;

    public const string StatusOpen = "open";
    public const string StatusDismissed = "dismissed";
    public const string StatusActioned = "actioned";

    /// <summary>Longest admin review note, in characters.</summary>
    public const int MaxReviewNoteLength = 500;

    /// <summary>
    /// Reason codes. <c>contact_info</c> covers a phone number or other contact in the listing, its photos
    /// or its audio note, which the server cannot screen (crop-listing-api.md, FUP-49).
    /// </summary>
    public static readonly IReadOnlyList<string> Reasons = new[] { "contact_info", "misleading", "scam", "offensive", "other" };

    /// <summary>What an admin may decide for the open reports of a listing.</summary>
    public static readonly IReadOnlyList<string> Decisions = new[] { StatusDismissed, StatusActioned };

    /// <summary>The statuses an admin may list.</summary>
    public static readonly IReadOnlyList<string> Statuses = new[] { StatusOpen, StatusDismissed, StatusActioned };

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "listingType", "listingId", "reason", "version"
    };

    /// <summary>The entity id of a report: one per listing for each reporter.</summary>
    public static string EntityIdFor(string listingType, string listingId) => $"{listingType}#{listingId}";

    /// <summary>The rate-limits key counting a reporter's reports on one UTC day.</summary>
    public static string RateLimitKey(string reporterId, DateTime nowUtc)
        => $"LISTING_REPORT#{reporterId}#{nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>TTL of the day counter: past the end of its day, so the window never slides.</summary>
    public const int RateLimitTtlSeconds = 2 * 24 * 60 * 60;

    /// <summary>The <c>expiresAt</c> of a report written at <paramref name="nowUtc"/> (epoch seconds).</summary>
    public static long ExpiresAt(DateTime nowUtc)
        => new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).AddDays(TtlDays).ToUnixTimeSeconds();

    /// <summary>
    /// Reads and checks the payload. Returns the problems found (empty when valid); <paramref name="input"/>
    /// is set only when there are none.
    /// </summary>
    public static List<string> Parse(string entityId, Dictionary<string, object> data, out ListingReportInput? input)
    {
        input = null;
        var errors = new List<string>();

        var unknown = data.Keys.Where(k => !AllowedFields.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            errors.Add($"Unknown fields: {string.Join(", ", unknown)}.");
        }

        var listingType = ReadString(data, "listingType");
        if (listingType is not (ListingCrop or ListingTrip))
        {
            errors.Add("listingType must be crop or trip.");
            listingType = null;
        }

        var listingId = ReadString(data, "listingId");
        if (string.IsNullOrWhiteSpace(listingId))
        {
            errors.Add("listingId is required.");
            listingId = null;
        }
        else if (listingId.Length > MaxListingIdLength || listingId.Contains('#'))
        {
            errors.Add($"listingId must be at most {MaxListingIdLength} characters and contain no '#'.");
            listingId = null;
        }

        var reason = ReadString(data, "reason");
        if (reason == null || !Reasons.Contains(reason))
        {
            errors.Add($"reason must be one of: {string.Join(", ", Reasons)}.");
            reason = null;
        }

        if (listingType != null && listingId != null
            && !string.Equals(entityId, EntityIdFor(listingType, listingId), StringComparison.Ordinal))
        {
            errors.Add("entityId must be '{listingType}#{listingId}'.");
        }

        if (errors.Count == 0)
        {
            input = new ListingReportInput(listingType!, listingId!, reason!);
        }

        return errors;
    }

    private static string? ReadString(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };
    }
}
