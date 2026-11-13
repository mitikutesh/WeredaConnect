using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-140 (delivery-verification-api.md 2.14.0): the kept copy of an attached handoff photo. The presigned PUT
/// writes the photo under <c>uploads/photos/</c>, which holds every upload, attached or not, and may expire. The
/// attach (and the one-off backfill of the photos attached before 2.14.0) copies the version it pins to
/// <see cref="DeliveryPhotoKeys.KeptKeyOf"/> under <c>evidence/photos/</c>, which no lifecycle rule touches, and
/// the reads sign that copy.
/// </summary>
public static class DeliveryPhotoEvidence
{
    /// <summary>The kept copy: its key, and its S3 version id (null when the bucket answered none).</summary>
    public sealed record KeptCopy(string Key, string? VersionId);

    /// <summary>
    /// Copies <paramref name="uploadKey"/> at <paramref name="sourceVersionId"/> (the version the attach pinned;
    /// null copies the latest) to its kept key, server side, and returns the copy. A version
    /// never changes, so a retried copy writes the same bytes: at worst it adds another version of them at the kept
    /// key, and the delivery row names the one its own write committed. Throws what S3 throws.
    /// </summary>
    public static async Task<KeptCopy> KeepAsync(
        IAmazonS3 s3, string bucket, string uploadKey, string? sourceVersionId, CancellationToken cancellationToken = default)
    {
        var keptKey = DeliveryPhotoKeys.KeptKeyOf(uploadKey);
        var request = new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = uploadKey,
            DestinationBucket = bucket,
            DestinationKey = keptKey,
        };
        if (IsVersion(sourceVersionId))
        {
            request.SourceVersionId = sourceVersionId;
        }

        var response = await s3.CopyObjectAsync(request, cancellationToken);
        return new KeptCopy(keptKey, IsVersion(response.VersionId) ? response.VersionId : null);
    }

    /// <summary>The source object or version is gone (never uploaded, or deleted).</summary>
    public static bool IsMissing(AmazonS3Exception ex)
        => ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NoSuchVersion";

    /// <summary>A real version id: S3 answers <c>null</c> (or none) on an unversioned bucket.</summary>
    private static bool IsVersion(string? versionId) => !string.IsNullOrEmpty(versionId) && versionId != "null";
}
