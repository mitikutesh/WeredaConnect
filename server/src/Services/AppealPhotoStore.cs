using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Functions;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-29 (reputation-architecture.md 16, account-deletion.md): a deleted account's appeal photos
/// leave the private uploads bucket with its reputation events. The bucket is versioned, so every
/// version and delete marker under <c>uploads/appeals/{userId}/</c> is removed, not only the
/// current objects.
/// </summary>
public sealed class AppealPhotoStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public AppealPhotoStore(IAmazonS3 s3, string bucket)
    {
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
    }

    /// <summary>Delete everything under the user's appeal prefix; returns how many versions went. Safe to run again.</summary>
    public async Task<int> DeleteAllAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId) || userId.Contains('/'))
        {
            return 0;
        }

        var prefix = $"{ReputationFunction.AppealPhotoPrefix}{userId}/";
        var deleted = 0;
        string? keyMarker = null;
        string? versionMarker = null;
        while (true)
        {
            var page = await _s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = prefix,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker
            });

            var versions = page.Versions ?? new List<S3ObjectVersion>();
            if (versions.Count > 0)
            {
                var response = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _bucket,
                    Quiet = true,
                    Objects = versions.Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId }).ToList()
                });
                if (response.DeleteErrors is { Count: > 0 } errors)
                {
                    throw new InvalidOperationException($"{errors.Count} appeal photo version(s) could not be deleted.");
                }

                deleted += versions.Count;
            }

            if (page.IsTruncated != true)
            {
                return deleted;
            }

            keyMarker = page.NextKeyMarker;
            versionMarker = page.NextVersionIdMarker;
        }
    }
}
