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
    /// stored photo key that belongs to this delivery and its type. Unchanged when the bucket is
    /// not configured. Signing is local (no S3 call); the caller catches a failure so a read never
    /// fails on photos.
    /// </summary>
    public static DeliveryDto Sign(DeliveryDto dto, IAmazonS3? s3, string? bucket, DateTime now)
    {
        if (s3 == null || string.IsNullOrEmpty(bucket))
        {
            return dto;
        }

        return dto with
        {
            PickupPhotoUrl = SignOne(s3, bucket, dto.DeliveryId, DeliveryPhotoKeys.Pickup, dto.PickupPhotoRef, now),
            DropoffPhotoUrl = SignOne(s3, bucket, dto.DeliveryId, DeliveryPhotoKeys.Dropoff, dto.DropoffPhotoRef, now),
        };
    }

    private static string? SignOne(IAmazonS3 s3, string bucket, string deliveryId, string type, string? key, DateTime now)
    {
        // A key written before 1.25.0 (any participant could set one through a delivery update)
        // is signed only when it has this delivery's shape for this type.
        if (!DeliveryPhotoKeys.IsKeyOf(key, deliveryId, type))
        {
            return null;
        }

        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = now + DeliveryPhotoKeys.ViewUrlLifetime,
        });
    }
}
