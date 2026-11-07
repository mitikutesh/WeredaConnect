/// Queue rules for a handoff photo (FRD-12, `delivery-verification-api.md`
/// 1.25.0, `sync-protocol.md` 1.39.0). Pure Dart: the mobile sync queue, its
/// preflight and the web repository all use them.
library;

/// The sync entity type that attaches an uploaded photo to its delivery.
const String deliveryPhotoEntityType = 'delivery_photo';

/// Data key of the server's upload key, the only field the server takes.
const String deliveryPhotoRefKey = 'photoRef';

/// Data key of the photo file on the phone. A `_` key: the sync executor
/// never sends it (`wireOperationData`), so a device path never reaches the
/// server; the preflight keeps it next to [deliveryPhotoRefKey] once the
/// photo is uploaded, to delete the file after the attach.
const String deliveryPhotoLocalPathKey = '_localPath';

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

/// The `delivery` operation fields that held a handoff photo before FRD-12.
/// The server still shape-checks them there but never writes them
/// (`sync-protocol.md` 1.39.0).
const List<String> deliveryOpPhotoRefFields = [
  'pickupPhotoRef',
  'dropoffPhotoRef',
];

final RegExp _photoKeyPattern = RegExp(
  r'^uploads/photos/([a-zA-Z0-9\-]+)/(pickup|dropoff)-\d+\.(webp|jpg)$',
);

/// Whether [ref] is an upload key of a photo of [deliveryId]
/// (`uploads/photos/{deliveryId}/{pickup|dropoff}-{millis}.{webp|jpg}`), the
/// only value the server accepts in a [deliveryOpPhotoRefFields] field. A
/// file path on the phone, another delivery's key or any other value is not.
bool isDeliveryPhotoKeyOf(Object? ref, String deliveryId) {
  if (ref is! String) return false;
  final match = _photoKeyPattern.firstMatch(ref);
  return match != null && match.group(1) == deliveryId;
}

/// The handoff photos that go with a refused `delivery` operation, given its
/// [action] and its payload's `status`: a create was never stored, so both;
/// a refused pickup takes its pickup photo, a refused delivery confirm its
/// drop-off photo; anything else none.
List<String> handoffPhotoTypesOf(String action, Object? status) {
  if (action == 'create') return const ['pickup', 'dropoff'];
  if (status == 'pickedUp') return const ['pickup'];
  if (status == 'delivered') return const ['dropoff'];
  return const [];
}
