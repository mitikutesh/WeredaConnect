using System.Text.RegularExpressions;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Delivery handoff photos (delivery-verification-api.md 1.25.0, FRD-12): the rules shared by
/// <c>POST /deliveries/{id}/photos</c> (the presigned PUT), the <c>delivery_photo</c> sync operation
/// (the attach) and the reads that sign a GET (the delivery detail and the admin transaction view).
///
/// Key: <c>uploads/photos/{deliveryId}/{pickup|dropoff|arrival}-{ms}.{webp|jpg}</c> in the uploads bucket.
/// The delivery segment is always the delivery the server checked, never a value the caller picks.
///
/// Who takes which photo: the party who confirms that handoff. The pickup photo is the trucker's
/// (a self-pickup has no trucker, so it has none); the drop-off photo is the buyer's (the buyer's
/// delivery confirm, or the self-pickup confirm at the farm). The farmer takes none. Every party
/// of the delivery, and an admin, may see both. ESC-36: the arrival photo is the trucker's, taken at the
/// drop-off for the drop-off check-in, taken only with <c>dropoffClassification</c> on (callers pass
/// <c>allowArrival</c>; with the flag off the type does not exist).
///
/// FUP-140 (delivery-verification-api.md 2.14.0): the attach copies the photo's pinned version to its kept
/// key, <c>evidence/photos/{deliveryId}/{type}-{ms}.{webp|jpg}</c>, before it writes the photo, and the reads
/// sign that copy. <c>uploads/photos/</c> holds only uploads and may expire; <c>evidence/photos/</c> never does.
/// </summary>
public static partial class DeliveryPhotoKeys
{
    /// <summary>Where the presigned PUT writes a photo (the upload key).</summary>
    public const string UploadPrefix = "uploads/photos/";

    /// <summary>FUP-140: where the attach keeps the photo's copy, outside every expiring prefix.</summary>
    public const string KeptPrefix = "evidence/photos/";

    /// <summary>The photo taken when the goods leave the farm.</summary>
    public const string Pickup = "pickup";

    /// <summary>The photo taken when the goods reach the buyer.</summary>
    public const string Dropoff = "dropoff";

    /// <summary>ESC-36: the trucker's photo at the drop-off, the evidence of the drop-off check-in.</summary>
    public const string Arrival = "arrival";

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

    [GeneratedRegex(@"^uploads/photos/([a-zA-Z0-9\-]+)/(pickup|dropoff|arrival)-\d+\.(webp|jpg)$")]
    private static partial Regex KeyRegex();

    /// <summary>The delivery attribute that holds the key of a photo type.</summary>
    public static string FieldOf(string type) => type switch
    {
        Pickup => "pickupPhotoRef",
        Arrival => "arrivalPhotoRef",
        _ => "dropoffPhotoRef"
    };

    /// <summary>
    /// The server-only delivery attribute that holds the S3 version id of the attached photo. The
    /// uploads bucket is versioned, so the reads sign that version: a second PUT to the same key
    /// within its presign window adds a version and never replaces the attached evidence.
    /// </summary>
    public static string VersionFieldOf(string type) => type switch
    {
        Pickup => "pickupPhotoVersionId",
        Arrival => "arrivalPhotoVersionId",
        _ => "dropoffPhotoVersionId"
    };

    /// <summary>FUP-140: the server-only delivery attribute that holds the kept copy's key of a photo type.</summary>
    public static string KeptFieldOf(string type) => type switch
    {
        Pickup => "pickupPhotoKeptRef",
        Arrival => "arrivalPhotoKeptRef",
        _ => "dropoffPhotoKeptRef"
    };

    /// <summary>FUP-140: the server-only delivery attribute that holds the kept copy's S3 version id.</summary>
    public static string KeptVersionFieldOf(string type) => type switch
    {
        Pickup => "pickupPhotoKeptVersionId",
        Arrival => "arrivalPhotoKeptVersionId",
        _ => "dropoffPhotoKeptVersionId"
    };

    /// <summary>Every server-only photo attribute of a delivery row (the S3 version ids and the kept copies).</summary>
    public static readonly IReadOnlyList<string> ServerOnlyFields = new[]
    {
        "pickupPhotoVersionId", "dropoffPhotoVersionId", "arrivalPhotoVersionId",
        "pickupPhotoKeptRef", "dropoffPhotoKeptRef", "arrivalPhotoKeptRef",
        "pickupPhotoKeptVersionId", "dropoffPhotoKeptVersionId", "arrivalPhotoKeptVersionId",
    };

    /// <summary>
    /// FUP-140: the kept key of an upload key, <c>evidence/photos/...</c> in place of <c>uploads/photos/...</c>.
    /// Only for a key that <see cref="IsKeyOf"/> accepted.
    /// </summary>
    public static string KeptKeyOf(string uploadKey) => KeptPrefix + uploadKey[UploadPrefix.Length..];

    /// <summary>
    /// FUP-140: whether the row shows the handoff a <paramref name="type"/> photo belongs to, so the photo may
    /// be attached. The pickup: <c>pickedUpAt</c>, or a status at or past the pickup. The drop-off:
    /// <c>confirmedAt</c>, or <c>delivered</c>. The arrival has its own rule (on the road), so it is always true
    /// here. A phone uploads before its handoff syncs; its attach waits (deferred) until the handoff is on the
    /// row, so a handoff the server never took (refused, or reverted by a conflict) never gets a photo.
    /// </summary>
    public static bool HandoffOnRow(string type, string? status, string? pickedUpAt, string? confirmedAt) => type switch
    {
        Pickup => !string.IsNullOrEmpty(pickedUpAt) || status is "pickedUp" or "inTransit" or "delivered",
        Dropoff => !string.IsNullOrEmpty(confirmedAt) || status == "delivered",
        _ => true
    };

    /// <summary>True for <c>pickup</c> or <c>dropoff</c>; ESC-36: also <c>arrival</c> with <paramref name="allowArrival"/>.</summary>
    public static bool IsType(string? type, bool allowArrival = false)
        => type is Pickup or Dropoff || (allowArrival && type == Arrival);

    /// <summary>The key of a new photo of <paramref name="deliveryId"/>.</summary>
    public static string Key(string deliveryId, string type, long epochMillis, string contentType)
        => $"uploads/photos/{deliveryId}/{type}-{epochMillis}.{ExtensionByContentType[contentType]}";

    /// <summary>
    /// True when <paramref name="key"/> is a photo key of <paramref name="deliveryId"/> (and of
    /// <paramref name="type"/> when one is given). A local file path, another delivery's key, a
    /// key of the other type or a pre-1.7.0 <c>photos/...</c> shape is false (FIX-011). ESC-36: an
    /// <c>arrival</c> key matches only when that type is asked for, so the type-less check of a delivery
    /// operation's <c>pickupPhotoRef</c> / <c>dropoffPhotoRef</c> answers as before.
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
            && (type == null
                ? match.Groups[2].Value != Arrival
                : string.Equals(match.Groups[2].Value, type, StringComparison.Ordinal));
    }

    /// <summary>
    /// Split a <see cref="EntityType"/> entity id, <c>{deliveryId}#{pickup|dropoff}</c>. The id names
    /// the photo rather than the delivery, so a photo waiting in a phone's queue never holds back the
    /// delivery's own operations.
    /// </summary>
    public static bool TryParseEntityId(string? entityId, out string deliveryId, out string type, bool allowArrival = false)
    {
        deliveryId = type = string.Empty;
        var separator = entityId?.LastIndexOf('#') ?? -1;
        if (separator <= 0)
        {
            return false;
        }

        var parsedType = entityId![(separator + 1)..];
        if (!IsType(parsedType, allowArrival))
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
    /// for the pickup and (ESC-36) the arrival, the buyer for the drop-off. Null when the delivery has
    /// no such party (a self-pickup's pickup or arrival).
    /// </summary>
    public static string? AuthorId(string type, string? truckerId, string? buyerId)
    {
        var author = type is Pickup or Arrival ? truckerId : buyerId;
        return string.IsNullOrEmpty(author) ? null : author;
    }
}
