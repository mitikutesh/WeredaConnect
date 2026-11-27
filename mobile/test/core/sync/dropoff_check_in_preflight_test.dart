import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/dropoff_check_in_preflight.dart';

SyncOperation _op(String entityType, String entityId, Object data) =>
    SyncOperation()
      ..operationId = 'op-$entityId'
      ..entityType = entityType
      ..entityId = entityId
      ..action = 'create'
      ..data = data is String ? data : jsonEncode(data)
      ..idempotencyKey = 'trucker-1:x'
      ..status = SyncStatus.pending
      ..createdAt = DateTime.utc(2026, 11, 22);

/// ESC-36 (FUP-336): a drop-off check-in waits on the phone while its
/// delivery's arrival photo still waits in the queue; nothing else does.
void main() {
  late Set<String> waiting;
  late List<(String, String)> asked;
  late DropoffCheckInSyncPreflight preflight;

  setUp(() {
    waiting = {};
    asked = [];
    preflight = DropoffCheckInSyncPreflight(
      waiting: (entityType, entityId) async {
        asked.add((entityType, entityId));
        return waiting.contains('$entityType:$entityId');
      },
    );
  });

  final checkIn = _op(
    'handoff_check_in',
    'ord-1#arrived_dropoff',
    {'orderId': 'ord-1', 'kind': 'arrived_dropoff', '_deliveryId': 'd-1'},
  );

  test('holds the check-in while the arrival photo waits', () async {
    waiting.add('delivery_photo:d-1#arrival');

    expect(await preflight(checkIn), isFalse);
    expect(asked, [('delivery_photo', 'd-1#arrival')]);
  });

  test('lets it go once the photo was attached or refused', () async {
    expect(await preflight(checkIn), isTrue);
  });

  test('every other operation goes without a look', () async {
    waiting.add('delivery_photo:d-1#arrival');
    for (final op in [
      _op('handoff_check_in', 'ord-1#arrived_pickup', {'_deliveryId': 'd-1'}),
      _op('delivery_photo', 'd-1#arrival', {'_localPath': '/x'}),
      _op('order', 'ord-1', {'status': 'accepted'}),
      // An older queued check-in without its delivery: the server decides.
      _op('handoff_check_in', 'ord-1#arrived_dropoff', {'orderId': 'ord-1'}),
      _op('handoff_check_in', 'ord-1#arrived_dropoff', 'not json'),
    ]) {
      expect(await preflight(op), isTrue, reason: op.entityId);
    }
    expect(asked, isEmpty);
  });

  test('reads the delivery a queued check-in names', () {
    expect(dropoffCheckInDeliveryIdOf(checkIn.data), 'd-1');
    expect(dropoffCheckInDeliveryIdOf('{"_deliveryId": ""}'), isNull);
    expect(dropoffCheckInDeliveryIdOf('[]'), isNull);
  });
}
