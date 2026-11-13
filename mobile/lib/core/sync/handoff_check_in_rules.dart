/// Wire rules for the arrival check-ins of an accepted order (ESC-34,
/// FUP-285, `sync-protocol.md` 1.55.0 "Arrival check-ins") and the trucker's
/// drop-off check-in (ESC-36, FUP-336, 1.59.0). Pure Dart: the
/// mobile sync queue and executor, the web order repository and the error
/// texts all use them.
library;

/// The create-only sync entity of a check-in.
const String handoffCheckInEntityType = 'handoff_check_in';

/// The kind of the trucker's drop-off check-in (ESC-36, FUP-336).
const String dropoffCheckInKind = 'arrived_dropoff';

/// The entity id of the [kind] check-in of an order: `{orderId}#{kind}`. It
/// names the check-in, not the order, so a check-in waiting on the phone
/// never holds back the order's own operations or its server refresh.
String handoffCheckInEntityId(String orderId, String kind) => '$orderId#$kind';

/// The order id and kind of a [handoffCheckInEntityType] entity id, or null
/// when it has no `#`.
({String orderId, String kind})? parseHandoffCheckInEntityId(
  String entityId,
) {
  final separator = entityId.lastIndexOf('#');
  if (separator <= 0 || separator == entityId.length - 1) return null;
  return (
    orderId: entityId.substring(0, separator),
    kind: entityId.substring(separator + 1),
  );
}

/// The refusal codes of a check-in. Every one is permanent: the client drops
/// the check-in, takes back its mark and says why.
abstract final class CheckInRefusal {
  static const validation = 'CHECKIN_VALIDATION';
  static const notParty = 'CHECKIN_NOT_PARTY';
  static const notOpen = 'CHECKIN_NOT_OPEN';
  static const tooEarly = 'CHECKIN_TOO_EARLY';
  static const tooFar = 'CHECKIN_TOO_FAR';

  /// It reached the server after the deadline: kept on the order as late
  /// evidence, never counted.
  static const tooLate = 'CHECKIN_TOO_LATE';

  /// ESC-36 (FUP-336): a drop-off check-in reached the server without the
  /// trucker's arrival photo attached to the delivery.
  static const photoRequired = 'CHECKIN_PHOTO_REQUIRED';
}

/// The end of a `CHECKIN_TOO_FAR` message when the server kept the drop-off
/// attempt as evidence (`sync-protocol.md` 1.59.0): the first one with its
/// photo, before T8.
const String checkInAttemptKeptSuffix = 'The attempt was kept for review.';

/// Whether a drop-off check-in refused `CHECKIN_TOO_FAR` was kept for review.
bool isCheckInAttemptKeptMessage(String? message) =>
    message != null &&
    message.startsWith(CheckInRefusal.tooFar) &&
    message.trimRight().endsWith(checkInAttemptKeptSuffix);

/// Data key of a queued drop-off check-in naming its delivery (FUP-336). A
/// `_` key: the sync executor never sends it (`wireOperationData`); the
/// phone's preflight holds the check-in back while the arrival photo of that
/// delivery still waits in the queue.
const String handoffCheckInDeliveryIdKey = '_deliveryId';

/// Whether the server refused a check-in for good: any `CHECKIN_*` error.
bool isCheckInRejectionMessage(String? message) =>
    message != null && message.startsWith('CHECKIN_');

/// Whether the server does not take check-ins at all: with
/// `pickupClassification` off the entity is unsupported, as every unknown
/// one (`Unsupported entity type: 'handoff_check_in'.`). Permanent too: the
/// same operation can only get the same answer, so it must not loop.
bool isCheckInUnsupportedMessage(String? message) =>
    message != null &&
    message.startsWith('Unsupported entity type') &&
    message.contains("'$handoffCheckInEntityType'");

/// [isCheckInRejectionMessage] or [isCheckInUnsupportedMessage].
bool isCheckInRefusalMessage(String? message) =>
    isCheckInRejectionMessage(message) || isCheckInUnsupportedMessage(message);
