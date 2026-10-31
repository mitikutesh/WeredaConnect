namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Crop listing photos (crop-listing-api.md 1.6.0): the id rules and the S3 keys shared by
/// POST /crops/{id}/photos, the sync rule 9 check, the read-side URL signing and the photo processor.
///
/// Keys:
/// - incoming original: <c>{farmerId}/{cropId}/{photoId}</c>
/// - renditions: <c>{farmerId}/{cropId}/{photoId}/thumb.webp</c> and <c>/full.webp</c>
///
/// The farmer segment is always the uploader's own id (upload) or the listing's stored farmerId
/// (read), never a value the caller chooses, so a listing can only show its own farmer's photos.
/// </summary>
public static class CropPhotoKeys
{
    /// <summary>Fewest photos a listing carries (rule 9).</summary>
    public const int MinPerListing = 2;

    /// <summary>Most photos a listing carries (rule 9), and most upload URLs in one request.</summary>
    public const int MaxPerListing = 4;

    /// <summary>Largest original the device may upload and the processor will decode: 10 MiB.</summary>
    public const long MaxOriginalBytes = 10L * 1024 * 1024;

    /// <summary>The rule 9 refusal text, inside the <c>CROP_VALIDATION: </c> message.</summary>
    public const string PhotoIdsRule = "photoIds must list 2 to 4 distinct photo ids.";

    /// <summary>Content types the device may upload.</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "image/jpeg", "image/png", "image/webp" };

    private const string ThumbName = "thumb.webp";
    private const string FullName = "full.webp";

    /// <summary>True for a UUID in the canonical 8-4-4-4-12 form (either case).</summary>
    public static bool IsUuid(string? value)
        => !string.IsNullOrEmpty(value) && Guid.TryParseExact(value, "D", out _);

    /// <summary>The incoming bucket key of one original.</summary>
    public static string IncomingKey(string farmerId, string cropId, string photoId)
        => $"{farmerId}/{cropId}/{photoId}";

    /// <summary>The photos bucket key of the 320 px thumb.</summary>
    public static string ThumbKey(string farmerId, string cropId, string photoId)
        => $"{farmerId}/{cropId}/{photoId}/{ThumbName}";

    /// <summary>The photos bucket key of the 1024 px full rendition.</summary>
    public static string FullKey(string farmerId, string cropId, string photoId)
        => $"{farmerId}/{cropId}/{photoId}/{FullName}";

    /// <summary>
    /// Split an incoming key into its three segments. False unless there are exactly three and
    /// each is a canonical UUID (the processor deletes anything else without decoding it).
    /// </summary>
    public static bool TryParseIncomingKey(string? key, out string farmerId, out string cropId, out string photoId)
    {
        farmerId = cropId = photoId = string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var parts = key.Split('/');
        if (parts.Length != 3 || !IsUuid(parts[0]) || !IsUuid(parts[1]) || !IsUuid(parts[2]))
        {
            return false;
        }

        farmerId = parts[0];
        cropId = parts[1];
        photoId = parts[2];
        return true;
    }

    /// <summary>
    /// Rule 9: read a sync payload's <c>photoIds</c> as 2 to 4 distinct UUID strings, lower-cased,
    /// in the order sent. The value is a <see cref="System.Text.Json.JsonElement"/> array on the
    /// wire; a string sequence is accepted too (already-parsed data). False for anything else:
    /// not an array, a non-string or non-UUID entry, a repeat, fewer than 2 or more than 4.
    /// </summary>
    public static bool TryNormalizePhotoIds(object? value, out List<string> photoIds)
    {
        photoIds = new List<string>();
        var raw = new List<string>();

        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    raw.Add(entry.GetString() ?? string.Empty);
                }

                break;
            case IEnumerable<string> strings:
                raw.AddRange(strings);
                break;
            case IEnumerable<object> objects:
                foreach (var entry in objects)
                {
                    if (entry is not string s)
                    {
                        return false;
                    }

                    raw.Add(s);
                }

                break;
            default:
                return false;
        }

        if (raw.Count < MinPerListing || raw.Count > MaxPerListing)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in raw)
        {
            if (!IsUuid(id))
            {
                return false;
            }

            var lower = id.ToLowerInvariant();
            if (!seen.Add(lower))
            {
                return false;
            }

            photoIds.Add(lower);
        }

        return true;
    }

    /// <summary>
    /// The photo ids stored on a listing item (a DynamoDB L of S, read back as a list), in display
    /// order. Entries that are not UUIDs are skipped so no odd key is ever signed. Empty for
    /// listings made before 1.6.0.
    /// </summary>
    public static List<string> StoredPhotoIds(Dictionary<string, object> item)
    {
        var ids = new List<string>();
        if (!item.TryGetValue("photoIds", out var value) || value is null or string)
        {
            return ids;
        }

        if (value is IEnumerable<object> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is string s && IsUuid(s))
                {
                    ids.Add(s);
                }
            }
        }

        return ids;
    }
}
