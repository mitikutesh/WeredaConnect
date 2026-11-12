/// Wire rules for the arrival check-ins of an accepted order (ESC-34,
/// FUP-285, `sync-protocol.md` 1.55.0 "Arrival check-ins"). Pure Dart: the
/// mobile sync queue and executor, the web order repository and the error
/// texts all use them.
library;

/// The create-only sync entity of a check-in.
const String handoffCheckInEntityType = 'handoff_check_in';

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
}

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
