using System.Text.RegularExpressions;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Delivery handoff photos (delivery-verification-api.md 1.25.0, FRD-12): the rules shared by
/// <c>POST /deliveries/{id}/photos</c> (the presigned PUT), the <c>delivery_photo</c> sync operation
/// (the attach) and the reads that sign a GET (the delivery detail and the admin transaction view).
///
/// Key: <c>uploads/photos/{deliveryId}/{pickup|dropoff}-{ms}.{webp|jpg}</c> in the uploads bucket.
/// The delivery segment is always the delivery the server checked, never a value the caller picks.
///
/// Who takes which photo: the party who confirms that handoff. The pickup photo is the trucker's
/// (a self-pickup has no trucker, so it has none); the drop-off photo is the buyer's (the buyer's
/// delivery confirm, or the self-pickup confirm at the farm). The farmer takes none. Every party
/// of the delivery, and an admin, may see both.
/// </summary>
public static partial class DeliveryPhotoKeys
{
    /// <summary>The photo taken when the goods leave the farm.</summary>
    public const string Pickup = "pickup";

    /// <summary>The photo taken when the goods reach the buyer.</summary>
    public const string Dropoff = "dropoff";

    /// <summary>
    /// Largest photo the device may upload: 200 KiB, a WebP or JPEG of at most 800 px compressed
    /// for 3G. Signed into the PUT as its Content-Length and checked again at the attach.
    /// </summary>
    public const long MaxBytes = 200L * 1024;

    /// <summary>Lifetime of a presigned GET of a photo (the detail screen and the admin view).</summary>
    public static readonly TimeSpan ViewUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Content types the device may upload, with the key's file extension.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtensionByContentType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "image/webp", "webp" },
            { "image/jpeg", "jpg" },
        };

    /// <summary>The sync entity that attaches an uploaded photo to its delivery.</summary>
    public const string EntityType = "delivery_photo";

    /// <summary>The one data key of a <see cref="EntityType"/> operation: the uploaded key.</summary>
    public const string PhotoRefField = "photoRef";

    [GeneratedRegex(@"^uploads/photos/([a-zA-Z0-9\-]+)/(pickup|dropoff)-\d+\.(webp|jpg)$")]
    private static partial Regex KeyRegex();

    /// <summary>The delivery attribute that holds the key of a photo type.</summary>
    public static string FieldOf(string type) => type == Pickup ? "pickupPhotoRef" : "dropoffPhotoRef";

    /// <summary>True for <c>pickup</c> or <c>dropoff</c>.</summary>
    public static bool IsType(string? type) => type is Pickup or Dropoff;

    /// <summary>The key of a new photo of <paramref name="deliveryId"/>.</summary>
    public static string Key(string deliveryId, string type, long epochMillis, string contentType)
        => $"uploads/photos/{deliveryId}/{type}-{epochMillis}.{ExtensionByContentType[contentType]}";

    /// <summary>
    /// True when <paramref name="key"/> is a photo key of <paramref name="deliveryId"/> (and of
    /// <paramref name="type"/> when one is given). A local file path, another delivery's key, a
    /// key of the other type or a pre-1.7.0 <c>photos/...</c> shape is false (FIX-011).
    /// </summary>
    public static bool IsKeyOf(string? key, string deliveryId, string? type = null)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var match = KeyRegex().Match(key);
        return match.Success
            && string.Equals(match.Groups[1].Value, deliveryId, StringComparison.Ordinal)
            && (type == null || string.Equals(match.Groups[2].Value, type, StringComparison.Ordinal));
    }

    /// <summary>
    /// Split a <see cref="EntityType"/> entity id, <c>{deliveryId}#{pickup|dropoff}</c>. The id names
    /// the photo rather than the delivery, so a photo waiting in a phone's queue never holds back the
    /// delivery's own operations.
    /// </summary>
    public static bool TryParseEntityId(string? entityId, out string deliveryId, out string type)
    {
        deliveryId = type = string.Empty;
        var separator = entityId?.LastIndexOf('#') ?? -1;
        if (separator <= 0)
        {
            return false;
        }

        var parsedType = entityId![(separator + 1)..];
        if (!IsType(parsedType))
        {
            return false;
        }

        deliveryId = entityId[..separator];
        type = parsedType;
        return true;
    }

    /// <summary>The content type a photo key was issued for (its extension).</summary>
    public static string ContentTypeOf(string key)
        => key.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "image/webp";

    /// <summary>
    /// The user id of the delivery party who takes a photo of <paramref name="type"/>: the trucker
    /// for the pickup, the buyer for the drop-off. Null when the delivery has no such party (a
    /// self-pickup's pickup).
    /// </summary>
    public static string? AuthorId(string type, string? truckerId, string? buyerId)
    {
        var author = type == Pickup ? truckerId : buyerId;
        return string.IsNullOrEmpty(author) ? null : author;
    }
}
