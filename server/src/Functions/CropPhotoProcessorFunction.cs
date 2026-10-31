using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>What the processor did with one incoming object.</summary>
public enum CropPhotoOutcome
{
    /// <summary>Both renditions written, original deleted.</summary>
    Processed,

    /// <summary>Bad key, too large or not a usable image: original deleted, nothing written.</summary>
    Refused,

    /// <summary>The object was already gone (a retry after it was processed): nothing to do.</summary>
    AlreadyGone,

    /// <summary>The event named a bucket other than the incoming one: left alone.</summary>
    Ignored,
}

/// <summary>
/// S3 ObjectCreated consumer for the crop photo incoming bucket (crop-listing-api 1.6.0, Photo
/// Renditions). For each original at <c>{farmerId}/{cropId}/{photoId}</c> it writes the thumb and
/// the full WebP to the photos bucket and deletes the original.
///
/// Failure policy: a bad key, an object over 10 MiB or an image that is refused or fails to decode
/// is deleted with a warning and nothing is written; the handler does not throw, so Lambda does not
/// retry it. A failed read or put throws so Lambda retries the event (the work is idempotent: puts
/// overwrite, and a retry that finds the original gone stops there).
///
/// AOT note: the handler takes the raw event <see cref="Stream"/> and parses it with
/// <see cref="JsonDocument"/>, like the DynamoDB stream functions, so no event package is needed.
/// </summary>
public class CropPhotoProcessorFunction
{
    internal const string RenditionContentType = "image/webp";
    internal const string RenditionCacheControl = "private, max-age=31536000, immutable";

    private readonly IAmazonS3 _s3;
    private readonly string? _incomingBucket;
    private readonly string _photosBucket;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public CropPhotoProcessorFunction()
    {
        _photosBucket = Environment.GetEnvironmentVariable("CROP_PHOTOS_BUCKET")
                        ?? throw new InvalidOperationException("CROP_PHOTOS_BUCKET is not set.");
        _incomingBucket = Environment.GetEnvironmentVariable("CROP_PHOTO_INCOMING_BUCKET");
        _s3 = new AmazonS3Client();
    }

    /// <summary>Constructor for testing with a mocked S3 client.</summary>
    public CropPhotoProcessorFunction(IAmazonS3 s3, string? incomingBucket, string photosBucket)
    {
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _incomingBucket = incomingBucket;
        _photosBucket = string.IsNullOrEmpty(photosBucket)
            ? throw new ArgumentException("The photos bucket is required.", nameof(photosBucket))
            : photosBucket;
    }

    /// <summary>
    /// Lambda entry point: every record of the S3 event in turn. A record whose put fails throws
    /// out of here, so the whole event is retried; records already done are then found gone.
    /// </summary>
    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        using var doc = await JsonDocument.ParseAsync(input);
        if (!doc.RootElement.TryGetProperty("Records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            // s3:TestEvent (sent when the notification is configured) and anything else.
            return;
        }

        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("s3", out var s3)
                || !s3.TryGetProperty("bucket", out var bucket)
                || !s3.TryGetProperty("object", out var obj))
            {
                continue;
            }

            var bucketName = bucket.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "";
            // Event keys are URL-encoded, with '+' for a space.
            var key = obj.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? WebUtility.UrlDecode(k.GetString() ?? "")
                : "";
            long? size = obj.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                         && s.TryGetInt64(out var sizeValue)
                ? sizeValue
                : null;

            await ProcessObjectAsync(bucketName, key, size, context);
        }
    }

    /// <summary>Process one incoming object (exposed for unit tests).</summary>
    public async Task<CropPhotoOutcome> ProcessObjectAsync(string bucket, string key, long? size, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(bucket)
            || (!string.IsNullOrEmpty(_incomingBucket) && !string.Equals(bucket, _incomingBucket, StringComparison.Ordinal)))
        {
            context.Logger.LogWarning("Crop photo event for a bucket other than the incoming one ignored.");
            return CropPhotoOutcome.Ignored;
        }

        if (!CropPhotoKeys.TryParseIncomingKey(key, out var farmerId, out var cropId, out var photoId))
        {
            return await RefuseAsync(bucket, key, "unknown", "key is not three UUID segments", context);
        }

        if (size > CropPhotoKeys.MaxOriginalBytes)
        {
            return await RefuseAsync(bucket, key, photoId, $"{size} bytes is over 10 MiB", context);
        }

        byte[]? original;
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
            original = response.ContentLength > CropPhotoKeys.MaxOriginalBytes
                ? null
                : await ReadCappedAsync(response.ResponseStream, CropPhotoKeys.MaxOriginalBytes);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            context.Logger.LogInformation($"Crop photo {photoId} already processed (original gone).");
            return CropPhotoOutcome.AlreadyGone;
        }

        if (original == null)
        {
            return await RefuseAsync(bucket, key, photoId, "object is over 10 MiB", context);
        }

        var rendered = CropPhotoRenditions.Render(original);
        if (rendered.IsRefused)
        {
            return await RefuseAsync(bucket, key, photoId, rendered.Refusal!, context);
        }

        // A failed put throws: Lambda retries the event and the work is redone from the original.
        await PutRenditionAsync(CropPhotoKeys.ThumbKey(farmerId, cropId, photoId), rendered.Thumb!);
        await PutRenditionAsync(CropPhotoKeys.FullKey(farmerId, cropId, photoId), rendered.Full!);

        await DeleteOriginalAsync(bucket, key, photoId, context);
        context.Logger.LogInformation(
            $"Crop photo {photoId} processed: thumb {rendered.Thumb!.Length} B, full {rendered.Full!.Length} B.");
        return CropPhotoOutcome.Processed;
    }

    private async Task<CropPhotoOutcome> RefuseAsync(
        string bucket, string key, string photoId, string reason, ILambdaContext context)
    {
        context.Logger.LogWarning($"Crop photo {photoId} refused and deleted: {reason}.");
        await DeleteOriginalAsync(bucket, key, photoId, context);
        return CropPhotoOutcome.Refused;
    }

    /// <summary>
    /// Best effort: a failed delete is logged, not thrown. The incoming bucket expires anything
    /// left after a day, and a retry here would only redo finished work.
    /// </summary>
    private async Task DeleteOriginalAsync(string bucket, string key, string photoId, ILambdaContext context)
    {
        try
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Crop photo {photoId} original not deleted: [{ex.GetType().Name}].");
        }
    }

    private async Task PutRenditionAsync(string key, byte[] bytes)
    {
        var request = new PutObjectRequest
        {
            BucketName = _photosBucket,
            Key = key,
            InputStream = new MemoryStream(bytes, writable: false),
            ContentType = RenditionContentType,
            AutoCloseStream = true,
        };
        request.Headers.CacheControl = RenditionCacheControl;
        await _s3.PutObjectAsync(request);
    }

    /// <summary>The whole stream, or null when it holds more than <paramref name="cap"/> bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, long cap)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
