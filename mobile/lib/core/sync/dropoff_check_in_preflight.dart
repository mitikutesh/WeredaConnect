import 'dart:convert';

import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';

/// Whether an operation of the entity still waits to reach the server:
/// pending, on its way, or failed and to be retried
/// (`SyncQueue.hasWaitingOperationsFor`).
typedef WaitingOperationsCheck = Future<bool> Function(
  String entityType,
  String entityId,
);

/// ESC-36 (FUP-336, `sync-protocol.md` 1.59.0): the server refuses a drop-off
/// check-in for good (`CHECKIN_PHOTO_REQUIRED`) unless the trucker's arrival
/// photo is attached to the delivery first. On the phone both are queued, the
/// photo first; this preflight holds the check-in back while the photo
/// (`delivery_photo` `{deliveryId}#arrival`) still waits in the queue, so a
/// photo still uploading on a slow line never costs the check-in. Once the
/// photo was attached, or refused for good, the check-in goes and the server
/// decides. Every other operation goes at once.
///
/// Plugged into the mobile `SyncExecutor` as part of its preflight; the
/// executor asks a held check-in again in the next batch of the same run.
class DropoffCheckInSyncPreflight {
  final WaitingOperationsCheck _waiting;

  DropoffCheckInSyncPreflight({required WaitingOperationsCheck waiting})
      : _waiting = waiting;

  Future<bool> call(SyncOperation operation) async {
    if (operation.entityType != handoffCheckInEntityType) return true;
    if (parseHandoffCheckInEntityId(operation.entityId)?.kind !=
        dropoffCheckInKind) {
      return true;
    }
    final deliveryId = dropoffCheckInDeliveryIdOf(operation.data);
    if (deliveryId == null) return true;
    return !await _waiting(
      deliveryPhotoEntityType,
      deliveryPhotoEntityId(deliveryId, arrivalPhotoType),
    );
  }
}

/// The delivery a queued drop-off check-in names
/// ([handoffCheckInDeliveryIdKey]), or null.
String? dropoffCheckInDeliveryIdOf(String data) {
  try {
    final decoded = jsonDecode(data);
    final value = decoded is Map ? decoded[handoffCheckInDeliveryIdKey] : null;
    return value is String && value.isNotEmpty ? value : null;
  } on FormatException {
    return null;
  }
}
