import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart'
    as bloc_event;
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';

/// Helper to build a test [ConflictEvent].
ConflictEvent _makeConflict({
  String entityId = 'crop-001',
  String entityType = 'crop_listing',
  int serverVersion = 2,
  Map<String, dynamic>? serverData,
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
    serverData: serverData ??
        {'cropType': 'Teff', 'quantity': 50.0, 'pricePerKg': 45.0},
    serverVersion: serverVersion,
    entityType: entityType,
    entityId: entityId,
    resolvedAt: DateTime.utc(2026, 2, 21),
  );
}

void main() {
  group('ConflictBloc', () {
    late ConflictNotificationService notificationService;

    setUp(() {
      notificationService = ConflictNotificationService();
    });

    tearDown(() {
      notificationService.dispose();
    });

    test('initial state is ConflictInitial', () {
      final bloc = ConflictBloc(notificationService: notificationService);
      expect(bloc.state, isA<ConflictInitial>());
      bloc.close();
    });

    test('ConflictDetected event emits ConflictPending with correct conflict',
        () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      final conflict = _makeConflict();
      bloc.add(bloc_event.ConflictDetected(conflict));

      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 1);
      expect(states[0], isA<ConflictPending>());
      final pending = states[0] as ConflictPending;
      expect(pending.conflict.entityId, 'crop-001');
      expect(pending.conflict.entityType, 'crop_listing');
      expect(pending.conflict.serverVersion, 2);
      expect(pending.conflict.serverData['cropType'], 'Teff');

      await subscription.cancel();
      await bloc.close();
    });

    test(
        'AcceptServerVersion from ConflictPending emits ConflictResolved then ConflictInitial',
        () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      // First trigger a conflict to get to ConflictPending
      bloc.add(bloc_event.ConflictDetected(_makeConflict()));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // Now accept the server version
      bloc.add(const bloc_event.AcceptServerVersion());
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // States: ConflictPending, ConflictResolved, ConflictInitial
      expect(states.length, 3);
      expect(states[0], isA<ConflictPending>());
      expect(states[1], isA<ConflictResolved>());
      expect(states[2], isA<ConflictInitial>());

      await subscription.cancel();
      await bloc.close();
    });

    test('DismissConflict emits ConflictResolved then ConflictInitial',
        () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      // First trigger a conflict
      bloc.add(bloc_event.ConflictDetected(_makeConflict()));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // Dismiss instead of accept
      bloc.add(const bloc_event.DismissConflict());
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // States: ConflictPending, ConflictResolved, ConflictInitial
      expect(states.length, 3);
      expect(states[0], isA<ConflictPending>());
      expect(states[1], isA<ConflictResolved>());
      expect(states[2], isA<ConflictInitial>());

      await subscription.cancel();
      await bloc.close();
    });

    test('second conflict replaces first in ConflictPending state', () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      final conflict1 = _makeConflict();
      final conflict2 = _makeConflict(entityId: 'crop-002', serverVersion: 3);

      bloc.add(bloc_event.ConflictDetected(conflict1));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      bloc.add(bloc_event.ConflictDetected(conflict2));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // States: ConflictPending(crop-001), ConflictPending(crop-002)
      expect(states.length, 2);
      expect(states[0], isA<ConflictPending>());
      expect((states[0] as ConflictPending).conflict.entityId, 'crop-001');
      expect(states[1], isA<ConflictPending>());
      expect((states[1] as ConflictPending).conflict.entityId, 'crop-002');

      await subscription.cancel();
      await bloc.close();
    });

    test('notification service stream triggers ConflictDetected automatically',
        () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      // Notify via the service (not by adding bloc event directly)
      notificationService.notify(_makeConflict(entityId: 'stream-001'));

      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 1);
      expect(states[0], isA<ConflictPending>());
      expect((states[0] as ConflictPending).conflict.entityId, 'stream-001');

      await subscription.cancel();
      await bloc.close();
    });

    test('close() cancels notification service subscription', () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      await bloc.close();

      // Notify after close — should not reach the bloc
      notificationService.notify(_makeConflict(entityId: 'late-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // No states emitted after close
      expect(states, isEmpty);

      await subscription.cancel();
    });

    test('handles delivery entity type conflict', () async {
      final bloc = ConflictBloc(notificationService: notificationService);
      final states = <ConflictState>[];
      final subscription = bloc.stream.listen(states.add);

      final deliveryConflict = _makeConflict(
        entityId: 'del-001',
        entityType: 'delivery',
        serverData: {'status': 'delivered', 'tripId': 'trip-abc12345'},
      );
      bloc.add(bloc_event.ConflictDetected(deliveryConflict));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 1);
      final pending = states[0] as ConflictPending;
      expect(pending.conflict.entityType, 'delivery');
      expect(pending.conflict.serverData['status'], 'delivered');

      await subscription.cancel();
      await bloc.close();
    });
  });
}
