/// Queue rules for a handoff photo (FRD-12, `delivery-verification-api.md`
/// 1.25.0, `sync-protocol.md` 1.39.0). Pure Dart: the mobile sync queue, its
/// preflight and the web repository all use them.
library;

/// The sync entity type that attaches an uploaded photo to its delivery.
const String deliveryPhotoEntityType = 'delivery_photo';

/// Data key of the server's upload key, the only field the server takes.
const String deliveryPhotoRefKey = 'photoRef';

/// Data key of the photo file while it waits on the phone. The preflight
/// replaces the whole payload with [deliveryPhotoRefKey] once the photo is
/// uploaded, so a device path never reaches the server.
const String deliveryPhotoLocalPathKey = 'localPath';

/// The entity id of a delivery's [type] photo (`pickup` or `dropoff`):
/// `{deliveryId}#{type}`. It names the photo, not the delivery, so a photo
/// still waiting to upload never holds back the delivery's own operations
/// or its server refresh.
String deliveryPhotoEntityId(String deliveryId, String type) =>
    '$deliveryId#$type';

/// The delivery id and type of a [deliveryPhotoEntityType] entity id, or
/// null when it has no known type after its last `#`.
({String deliveryId, String type})? parseDeliveryPhotoEntityId(
  String entityId,
) {
  final separator = entityId.lastIndexOf('#');
  if (separator <= 0) return null;
  final type = entityId.substring(separator + 1);
  if (type != 'pickup' && type != 'dropoff') return null;
  return (deliveryId: entityId.substring(0, separator), type: type);
}

/// Whether the server refused a photo attach for good: every
/// `DELIVERY_PHOTO_*` error (a `deferred` answer is not an error).
bool isDeliveryPhotoRejectionMessage(String? message) =>
    message != null && message.startsWith('DELIVERY_PHOTO_');
