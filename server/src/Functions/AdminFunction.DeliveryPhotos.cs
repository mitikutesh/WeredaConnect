using Amazon.S3;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FRD-12: the delivery's handoff photos on the admin transaction view
/// (<c>GET /admin/transactions/{orderId}</c>, delivery-verification-api.md 1.25.0), as presigned GETs
/// of 15 minutes. Dispute evidence; FRD-14 builds the full evidence package on top.
/// </summary>
public partial class AdminFunction
{
    private IAmazonS3? _deliveryPhotoS3;

    /// <summary>The uploads bucket the photos are in (<c>UPLOADS_BUCKET</c>); null or empty signs nothing.</summary>
    internal string? UploadsBucket { get; init; } = Environment.GetEnvironmentVariable("UPLOADS_BUCKET");

    /// <summary>The signing client, made on first use when the bucket is configured; tests set it.</summary>
    internal IAmazonS3? DeliveryPhotoS3
    {
        get => _deliveryPhotoS3 ??= string.IsNullOrEmpty(UploadsBucket) ? null : new AmazonS3Client();
        init => _deliveryPhotoS3 = value;
    }

    /// <summary>
    /// <paramref name="delivery"/> with its photo URLs. A signing failure leaves them off: the
    /// transaction is answered without them.
    /// </summary>
    private DeliveryDto WithDeliveryPhotoUrls(
        DeliveryDto delivery, IReadOnlyDictionary<string, object> row, ILambdaContext context)
    {
        try
        {
            return DeliveryPhotoUrls.Sign(delivery, row, DeliveryPhotoS3, UploadsBucket, Clock());
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Delivery photo URLs not signed for {delivery.DeliveryId}: [{ex.GetType().Name}]");
            return delivery;
        }
    }
}
