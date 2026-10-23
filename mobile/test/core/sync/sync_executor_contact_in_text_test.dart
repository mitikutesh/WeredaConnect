import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _FakeConnectivitySource implements ConnectivitySource {
  // Closed in tearDown.
  // ignore: close_sinks
  final controller = StreamController<List<ConnectivityResult>>.broadcast();

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async =>
      [ConnectivityResult.wifi];
}

/// Polls [condition] for up to 5 seconds instead of sleeping a fixed time.
Future<void> _waitFor(bool Function() condition) async {
  final deadline = DateTime.now().add(const Duration(seconds: 5));
  while (!condition() && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// FUP-49: a crop listing or trip refused because its text holds a phone
/// number is deterministic, so the queue never resends it; it is announced
/// (the snackbar explains it) and, until a crop/trip revert exists, not
/// undone.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;
  late _FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<(SyncOperation, String)> reverts;
  late SyncExecutor executor;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_contact_text_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_contact_text_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
    source = _FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    reverts = [];
    executor = SyncExecutor(
      syncQueue: syncQueue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'test-device',
      securityEvents: events,
      onPermanentRejection: (op, message) async => reverts.add((op, message)),
    );
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> enqueue({
    required String entityType,
    required String entityId,
    required String action,
    required String data,
  }) {
    return syncQueue.enqueue(
      userId: 'farmer-1',
      entityType: entityType,
      entityId: entityId,
      action: action,
      data: data,
      operationId: 'op-1',
    );
  }

  void replyWith(String message) {
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, {
        'syncBatchId': 'b',
        'results': [
          {'operationId': 'op-1', 'status': 'error', 'message': message},
        ],
      }),
      data: Matchers.any,
    );
  }

  List<SyncOperationRejected> rejections() =>
      emitted.whereType<SyncOperationRejected>().toList();

  for (final (entityType, entityId, code, field) in [
    ('crop_listing', 'crop-1', 'CROP_CONTACT_IN_TEXT', 'description'),
    ('trip', 'trip-1', 'TRIP_CONTACT_IN_TEXT', 'vehicleDescription'),
  ]) {
    test('$code is permanent, announced and not undone', () async {
      final message = '$code: Remove the phone number from $field. '
          'Contact opens after payment is held in escrow.';
      await enqueue(
        entityType: entityType,
        entityId: entityId,
        action: 'create',
        data: '{"$field":"Call 0911234567"}',
      );
      replyWith(message);

      final result = await executor.executeBatch();
      await _waitFor(() => rejections().isNotEmpty);

      expect(result.failed, 1);
      expect(result.operationErrors['op-1'], message);
      final op = await syncQueue.getOperation('op-1');
      expect(SyncQueue.isRejected(op!), isTrue);
      // A reconnect never resends it.
      expect(await syncQueue.retryFailed(), 0);
      expect(reverts, isEmpty);
      final event = rejections().single;
      expect(event.entityType, entityType);
      expect(event.entityId, entityId);
      expect(event.message, message);
    });
  }

  test('CROP_VALIDATION stays retryable', () async {
    await enqueue(
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'create',
      data: '{"description":"White teff"}',
    );
    replyWith('CROP_VALIDATION: The farmer profile could not be read.');

    await executor.executeBatch();

    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.pending);
    expect(op.retryCount, 1);
    expect(reverts, isEmpty);
  });
}
