using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FRD-12: presigned GETs of a delivery's handoff photos (delivery-verification-api.md 1.25.0).
/// The caller decides who may see them first: <c>GET /deliveries/{id}</c> after its participant
/// check, the admin transaction view behind the admin group. Lists never carry them.
/// </summary>
public static class DeliveryPhotoUrls
{
    /// <summary>
    /// <paramref name="dto"/> with <c>pickupPhotoUrl</c> and <c>dropoffPhotoUrl</c> set for each
    /// stored photo key that belongs to this delivery and its type, pinned to the S3 version the
    /// attach recorded (read from the raw <paramref name="item"/>; never on the DTO), of its kept copy
    /// when the row records one (FUP-140, delivery-verification-api.md 2.14.0). Unchanged
    /// when the bucket is not configured. Signing is local (no S3 call); the caller catches a
    /// failure so a read never fails on photos.
    /// </summary>
    public static DeliveryDto Sign(
        DeliveryDto dto, IReadOnlyDictionary<string, object> item, IAmazonS3? s3, string? bucket, DateTime now)
    {
        if (s3 == null || string.IsNullOrEmpty(bucket))
        {
            return dto;
        }

        return dto with
        {
            PickupPhotoUrl = SignOne(s3, bucket, dto.DeliveryId, DeliveryPhotoKeys.Pickup, dto.PickupPhotoRef, item, now),
            DropoffPhotoUrl = SignOne(s3, bucket, dto.DeliveryId, DeliveryPhotoKeys.Dropoff, dto.DropoffPhotoRef, item, now),
            // ESC-36: the trucker's arrival photo at the drop-off, signed like the others when attached.
            ArrivalPhotoUrl = SignOne(s3, bucket, dto.DeliveryId, DeliveryPhotoKeys.Arrival, dto.ArrivalPhotoRef, item, now),
        };
    }

    private static string? SignOne(
        IAmazonS3 s3, string bucket, string deliveryId, string type, string? key,
        IReadOnlyDictionary<string, object> item, DateTime now)
    {
        // A key written before 1.25.0 (any participant could set one through a delivery update)
        // is signed only when it has this delivery's shape for this type.
        if (!DeliveryPhotoKeys.IsKeyOf(key, deliveryId, type))
        {
            return null;
        }

        // FUP-140: the kept copy the attach (or the backfill) made, when the row records it: the upload key
        // under uploads/photos/ may have expired. A row from before 2.14.0 that the backfill has not reached
        // still signs its upload key.
        var keptKey = DeliveryPhotoKeys.KeptKeyOf(key!);
        var kept = Text(item, DeliveryPhotoKeys.KeptFieldOf(type)) == keptKey;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = kept ? keptKey : key,
            Verb = HttpVerb.GET,
            Expires = now + DeliveryPhotoKeys.ViewUrlLifetime,
        };
        // The attached version, so a later PUT to the key cannot swap the evidence. A row without
        // one (the bucket answered no version) signs the key's latest version.
        var version = Text(item, kept ? DeliveryPhotoKeys.KeptVersionFieldOf(type) : DeliveryPhotoKeys.VersionFieldOf(type));
        if (version is { Length: > 0 } && version != "null")
        {
            request.VersionId = version;
        }

        return s3.GetPreSignedURL(request);
    }

    private static string? Text(IReadOnlyDictionary<string, object> item, string field)
        => item.TryGetValue(field, out var value) ? value?.ToString() : null;
}
