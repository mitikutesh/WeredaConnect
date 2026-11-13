using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FRD-12: the <c>delivery_photo</c> entity (delivery-verification-api.md 1.25.0, sync-protocol.md
/// 1.39.0). Create only. After the photo went up through the presigned PUT of
/// <c>POST /deliveries/{id}/photos</c>, its taker attaches the key to the delivery:
///
/// - entity id <c>{deliveryId}#{pickup|dropoff}</c>, data <c>{"photoRef": key}</c>;
/// - the caller must be the photo's taker (the trucker for the pickup, the buyer for the drop-off);
/// - the object must be in the uploads bucket, at most 200 KiB, of its key's type (checked with a
///   HEAD, so a photo that never went up, or went up some other way, is refused);
/// - a handoff keeps its first photo (the same key again is a success, another key is refused);
/// - the write sets only <c>pickupPhotoRef</c> or <c>dropoffPhotoRef</c>, with the S3 version id
///   the HEAD saw (<c>pickupPhotoVersionId</c>, <c>dropoffPhotoVersionId</c>; the reads sign that
///   version, so a second PUT within the presign window cannot swap the evidence): the version and
///   <c>updatedAt</c> do not move, so a handoff queued on another phone never conflicts with it.
/// - FUP-140 (2.14.0): a pickup or drop-off photo waits (<c>deferred</c>) until its handoff is on the row,
///   and before the write the pinned version is copied to its kept key, recorded in the same write
///   (<c>{type}PhotoKeptRef</c>, <c>{type}PhotoKeptVersionId</c>); the reads sign that copy.
///
/// Every refusal starts with <c>DELIVERY_PHOTO_</c> and is final; a storage or table read that
/// fails is <c>deferred</c> (the phone keeps the operation without using a retry).
/// </summary>
public partial class SyncFunction
{
    private IAmazonS3? _deliveryPhotoS3;

    /// <summary>FRD-12: the uploads bucket the photos are in (<c>UPLOADS_BUCKET</c>); null leaves attaches deferred.</summary>
    internal string? UploadsBucket { get; init; }

    /// <summary>
    /// FRD-12: the S3 client of the attach's HEAD, made on first use when the bucket is configured
    /// (a batch without a photo never builds one); tests set it. Null leaves attaches deferred.
    /// </summary>
    internal IAmazonS3? DeliveryPhotoS3
    {
        get => _deliveryPhotoS3 ??= string.IsNullOrEmpty(UploadsBucket) ? null : new AmazonS3Client();
        init => _deliveryPhotoS3 = value;
    }

    private async Task<SyncOperationResult> ProcessDeliveryPhotoAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        if (operation.Action != "create")
        {
            return PhotoResult(operation, "error", "DELIVERY_PHOTO_INVALID: A delivery photo is attached with action create.");
        }

        // ESC-36: the trucker's #arrival only with dropoffClassification on; off, it is refused as before.
        if (!DeliveryPhotoKeys.TryParseEntityId(operation.EntityId, out var deliveryId, out var type, allowArrival: DropoffJudgementOn))
        {
            return PhotoResult(operation, "error",
                "DELIVERY_PHOTO_INVALID: entityId must be {deliveryId}#pickup or {deliveryId}#dropoff.");
        }

        var data = operation.Data ?? new Dictionary<string, object>();
        var unknown = data.Keys.Where(k => k != DeliveryPhotoKeys.PhotoRefField).ToList();
        if (unknown.Count > 0)
        {
            return PhotoResult(operation, "error", $"DELIVERY_PHOTO_INVALID: Unknown fields: {string.Join(", ", unknown)}");
        }

        var photoRef = data.TryGetValue(DeliveryPhotoKeys.PhotoRefField, out var refObj) ? refObj?.ToString() : null;
        if (!DeliveryPhotoKeys.IsKeyOf(photoRef, deliveryId, type))
        {
            return PhotoResult(operation, "error",
                "DELIVERY_PHOTO_INVALID: photoRef must be the key POST /deliveries/{id}/photos issued for this photo.");
        }

        Dictionary<string, AttributeValue>? existing;
        try
        {
            existing = await GetDeliveryItemAsync(deliveryId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"DELIVERY_PHOTO read failed for {MoneyAlarmLog.LogId(deliveryId)}: [{ex.GetType().Name}]");
            return PhotoResult(operation, "deferred", "DELIVERY_PHOTO_PENDING: The delivery could not be read. Will retry automatically.");
        }

        if (existing == null)
        {
            // Not prefixed: the delivery's own create may still be on its way, so the phone retries.
            return PhotoResult(operation, "error", "Delivery not found.");
        }

        var refusal = CheckPhotoParty(existing, type, authenticatedUserId);
        if (refusal != null)
        {
            var forbidden = PhotoResult(operation, "error", refusal);
            return IsDeliveryParty(existing, authenticatedUserId)
                ? forbidden
                : await RefuseWithSignalAsync(forbidden, FraudDetectionService.RoleForbiddenSignal,
                    operation, DeliveryPhotoKeys.EntityType, authenticatedUserId, context);
        }

        var stateAnswer = PhotoStateAnswer(operation, existing, type, photoRef!);
        if (stateAnswer != null)
        {
            return stateAnswer;
        }

        // ESC-36: the arrival photo belongs to the drop-off, so it attaches only while the goods are on the road.
        if (type == DeliveryPhotoKeys.Arrival
            && !DropoffJudgement.OnTheRoadDeliveryStatuses.Contains(AttrString(existing, "status")))
        {
            return PhotoResult(operation, "error", "DELIVERY_PHOTO_REFUSED: The arrival photo is taken while the goods are on the road.");
        }

        if (DeliveryPhotoS3 == null || string.IsNullOrEmpty(UploadsBucket))
        {
            context.Logger.LogError("DELIVERY_PHOTO attach without UPLOADS_BUCKET; deferred.");
            return PhotoResult(operation, "deferred", "DELIVERY_PHOTO_PENDING: Photo storage is not reachable. Will retry automatically.");
        }

        // The object itself: present, within the cap, of its key's type. The PUT signed both, so
        // this only refuses a photo that never went up or reached the bucket some other way.
        long size;
        string? storedType;
        string? versionId;
        try
        {
            var head = await DeliveryPhotoS3.GetObjectMetadataAsync(UploadsBucket, photoRef);
            size = head.ContentLength;
            storedType = head.Headers.ContentType;
            // The uploads bucket is versioned; "null" is S3's id on an unversioned bucket.
            versionId = string.IsNullOrEmpty(head.VersionId) || head.VersionId == "null" ? null : head.VersionId;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return PhotoResult(operation, "error", "DELIVERY_PHOTO_MISSING: The photo was not uploaded.");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"DELIVERY_PHOTO head failed for {MoneyAlarmLog.LogId(deliveryId)}: [{ex.GetType().Name}]");
            return PhotoResult(operation, "deferred", "DELIVERY_PHOTO_PENDING: Photo storage is not reachable. Will retry automatically.");
        }

        if (size < 1 || size > DeliveryPhotoKeys.MaxBytes)
        {
            return PhotoResult(operation, "error",
                $"DELIVERY_PHOTO_TOO_LARGE: A delivery photo is at most {DeliveryPhotoKeys.MaxBytes} bytes.");
        }

        if (!string.Equals(storedType, DeliveryPhotoKeys.ContentTypeOf(photoRef!), StringComparison.Ordinal))
        {
            return PhotoResult(operation, "error", "DELIVERY_PHOTO_INVALID: The photo's type does not match its key.");
        }

        // FUP-140: the evidence outlives its upload. The version the HEAD saw is copied to the kept key before
        // the write, and the write records the copy, so a row never names a photo without its kept copy. A
        // copy whose write then loses the race stays under evidence/photos/ unread.
        DeliveryPhotoEvidence.KeptCopy kept;
        try
        {
            kept = await DeliveryPhotoEvidence.KeepAsync(DeliveryPhotoS3, UploadsBucket, photoRef!, versionId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"DELIVERY_PHOTO keep failed for {MoneyAlarmLog.LogId(deliveryId)}: [{ex.GetType().Name}]");
            return PhotoResult(operation, "deferred", "DELIVERY_PHOTO_PENDING: Photo storage is not reachable. Will retry automatically.");
        }

        var field = DeliveryPhotoKeys.FieldOf(type);
        var names = new Dictionary<string, string>
        {
            { "#photo", field },
            { "#status", "status" },
            { "#kept", DeliveryPhotoKeys.KeptFieldOf(type) },
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":ref", new AttributeValue { S = photoRef } },
            { ":cancelled", new AttributeValue { S = "cancelled" } },
            { ":kept", new AttributeValue { S = kept.Key } },
        };
        var update = "SET #photo = :ref, #kept = :kept";
        if (kept.VersionId != null)
        {
            names["#keptVersion"] = DeliveryPhotoKeys.KeptVersionFieldOf(type);
            values[":keptVersion"] = new AttributeValue { S = kept.VersionId };
            update += ", #keptVersion = :keptVersion";
        }

        if (versionId != null)
        {
            names["#photoVersion"] = DeliveryPhotoKeys.VersionFieldOf(type);
            values[":version"] = new AttributeValue { S = versionId };
            update += ", #photoVersion = :version";
        }

        try
        {
            // The condition keeps the first photo: a retry of the same key may only find it (or
            // nothing) there, so its version is never moved by a later attach of another key.
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _deliveriesTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = deliveryId } },
                    { "tripId", existing["tripId"] },
                },
                UpdateExpression = update,
                ConditionExpression = "attribute_exists(deliveryId) AND attribute_not_exists(#photo) "
                    + "AND #status <> :cancelled AND attribute_not_exists(deletedAt)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // Another photo, a cancel or the row's removal landed in between: judge the row as it is now.
            var fresh = await GetDeliveryItemAsync(deliveryId, consistentRead: true);
            return fresh == null
                ? PhotoResult(operation, "error", "Delivery not found.")
                : PhotoStateAnswer(operation, fresh, type, photoRef!)
                  ?? PhotoResult(operation, "deferred", "DELIVERY_PHOTO_PENDING: The photo could not be attached yet. Will retry automatically.");
        }

        context.Logger.LogInformation(
            $"DELIVERY_PHOTO attached deliveryId={MoneyAlarmLog.LogId(deliveryId)} type={type} bytes={size}");
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = ItemVersion(existing),
        };
    }

    /// <summary>
    /// The refusal for a caller who does not take this photo, or null for its taker: an outsider, the
    /// farmer, the other party, and anyone at all for a self-pickup's pickup (it has no trucker).
    /// </summary>
    private static string? CheckPhotoParty(Dictionary<string, AttributeValue> delivery, string type, string? callerId)
    {
        var authorId = DeliveryPhotoKeys.AuthorId(type, AttrString(delivery, "truckerId"), AttrString(delivery, "buyerId"));
        if (!string.IsNullOrEmpty(callerId) && authorId == callerId)
        {
            return null;
        }

        return type switch
        {
            DeliveryPhotoKeys.Pickup => "DELIVERY_PHOTO_FORBIDDEN: Only the trucker takes the pickup photo.",
            DeliveryPhotoKeys.Arrival => "DELIVERY_PHOTO_FORBIDDEN: Only the trucker takes the arrival photo.",
            _ => "DELIVERY_PHOTO_FORBIDDEN: Only the buyer takes the drop-off photo."
        };
    }

    /// <summary>
    /// The answer the row's state already decides, or null to go on: a cancelled delivery refuses,
    /// the same key again succeeds without a write, another key of this handoff refuses. FUP-140: a
    /// pickup or drop-off photo whose handoff is not on the row yet waits (<c>deferred</c>): the handoff
    /// may still be on its way (deferred itself), and one the server never took (refused, or reverted by a
    /// conflict) must not get a photo; the phone drops that photo when it learns so.
    /// </summary>
    private static SyncOperationResult? PhotoStateAnswer(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string type, string photoRef)
    {
        if (AttrString(delivery, "status") == "cancelled" || !string.IsNullOrEmpty(AttrString(delivery, "deletedAt")))
        {
            return PhotoResult(operation, "error", "DELIVERY_PHOTO_REFUSED: The delivery was cancelled.");
        }

        var stored = AttrString(delivery, DeliveryPhotoKeys.FieldOf(type));
        if (string.IsNullOrEmpty(stored))
        {
            return DeliveryPhotoKeys.HandoffOnRow(type, AttrString(delivery, "status"),
                AttrString(delivery, "pickedUpAt"), AttrString(delivery, "confirmedAt"))
                ? null
                : PhotoResult(operation, "deferred",
                    "DELIVERY_PHOTO_PENDING: The handoff has not reached the server yet. Will retry automatically.");
        }

        return stored == photoRef
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = ItemVersion(delivery),
            }
            : PhotoResult(operation, "error", "DELIVERY_PHOTO_EXISTS: This handoff already has its photo.");
    }

    private static bool IsDeliveryParty(Dictionary<string, AttributeValue> delivery, string? callerId)
        => !string.IsNullOrEmpty(callerId)
           && (callerId == AttrString(delivery, "farmerId")
               || callerId == AttrString(delivery, "truckerId")
               || callerId == AttrString(delivery, "buyerId"));

    private static SyncOperationResult PhotoResult(SyncOperationRequest operation, string status, string message)
        => new() { OperationId = operation.OperationId, Status = status, Message = message };

    /// <summary>
    /// FIX-011 and FRD-12: a delivery create or update may still carry <c>pickupPhotoRef</c> or
    /// <c>dropoffPhotoRef</c>. A value that is not this delivery's photo key fails the operation as
    /// before 1.25.0; a valid key is dropped, never written: since 1.25.0 a photo reaches the delivery
    /// only through <c>delivery_photo</c>, which checks its taker and the object. Returns the refusal
    /// text or null.
    /// </summary>
    private static string? DropDeliveryOpPhotoRefs(SyncOperationRequest operation)
    {
        if (operation.Data == null)
        {
            return null;
        }

        foreach (var photoField in new[] { "pickupPhotoRef", "dropoffPhotoRef" })
        {
            if (!operation.Data.TryGetValue(photoField, out var photoObj))
            {
                continue;
            }

            var photoRef = photoObj?.ToString() ?? "";
            if (!string.IsNullOrEmpty(photoRef) && !DeliveryPhotoKeys.IsKeyOf(photoRef, operation.EntityId))
            {
                return $"DELIVERY_INVALID_FIELD: {photoField} has invalid format.";
            }

            operation.Data.Remove(photoField);
        }

        return null;
    }
}
