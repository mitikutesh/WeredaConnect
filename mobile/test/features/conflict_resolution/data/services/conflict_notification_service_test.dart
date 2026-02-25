import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';

/// Helper to build a test [ConflictEvent].
ConflictEvent _makeConflict({
  String entityId = 'crop-001',
  String entityType = 'crop_listing',
  int serverVersion = 2,
}) {
  final op = SyncOperation()
    ..operationId = 'op-$entityId'
    ..idempotencyKey = '$entityType:$entityId:update:1234567890'
    ..entityType = entityType
    ..entityId = entityId
    ..action = 'update'
    ..data = '{}'
    ..status = SyncStatus.synced
    ..retryCount = 0
    ..createdAt = DateTime.utc(2026, 2, 21);

  return ConflictEvent(
    operation: op,
    serverData: {'cropType': 'Teff', 'quantity': 50.0, 'pricePerKg': 45.0},
    serverVersion: serverVersion,
    entityType: entityType,
    entityId: entityId,
    resolvedAt: DateTime.utc(2026, 2, 21),
  );
}

void main() {
  group('ConflictNotificationService', () {
    late ConflictNotificationService service;

    setUp(() {
      service = ConflictNotificationService();
    });

    tearDown(() {
      service.dispose();
    });

    test('notify() emits event on stream when subscriber is present', () async {
      final conflict = _makeConflict();
      final events = <ConflictEvent>[];

      // Subscribe first
      final subscription = service.onConflict.listen(events.add);

      // Then notify
      service.notify(conflict);

      // Allow microtask to propagate
      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(events.length, 1);
      expect(events.first.entityId, 'crop-001');
      expect(events.first.entityType, 'crop_listing');
      expect(events.first.serverVersion, 2);

      await subscription.cancel();
    });

    test('events fired before subscription are replayed on first subscription',
        () async {
      final conflict1 = _makeConflict();
      final conflict2 = _makeConflict(entityId: 'crop-002');

      // Notify BEFORE subscribing — events go to replay buffer
      service.notify(conflict1);
      service.notify(conflict2);

      final events = <ConflictEvent>[];

      // Now subscribe — buffered events should be replayed
      final subscription = service.onConflict.listen(events.add);

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(events.length, 2);
      expect(events[0].entityId, 'crop-001');
      expect(events[1].entityId, 'crop-002');

      await subscription.cancel();
    });

    test('multiple events are all replayed in order', () async {
      final conflicts = List.generate(
        5,
        (i) => _makeConflict(entityId: 'entity-$i', serverVersion: i + 1),
      );

      // Notify all before subscribing
      for (final c in conflicts) {
        service.notify(c);
      }

      final events = <ConflictEvent>[];
      final subscription = service.onConflict.listen(events.add);

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(events.length, 5);
      for (var i = 0; i < 5; i++) {
        expect(events[i].entityId, 'entity-$i');
        expect(events[i].serverVersion, i + 1);
      }

      await subscription.cancel();
    });

    test('dispose() closes the stream cleanly', () async {
      final events = <ConflictEvent>[];
      bool streamDone = false;

      final subscription = service.onConflict.listen(
        events.add,
        onDone: () => streamDone = true,
      );

      service.dispose();

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(streamDone, isTrue);

      await subscription.cancel();
    });

    test('notify() after dispose() does not throw', () {
      service.dispose();

      // Should not throw — guarded by _controller.isClosed check
      expect(
        () => service.notify(_makeConflict()),
        returnsNormally,
      );
    });

    test('events after subscription go directly to stream, not buffer',
        () async {
      final events = <ConflictEvent>[];

      // Subscribe first
      final subscription = service.onConflict.listen(events.add);

      // Notify after subscribing
      service.notify(_makeConflict(entityId: 'live-001'));
      service.notify(_makeConflict(entityId: 'live-002'));

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(events.length, 2);
      expect(events[0].entityId, 'live-001');
      expect(events[1].entityId, 'live-002');

      await subscription.cancel();
    });
  });
}
