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
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
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

/// FUP-08: a delivery operation queued by a build before SIM-60 carries a
/// photo file path in `pickupPhotoRef` or `dropoffPhotoRef`. The server
/// refuses it (`DELIVERY_INVALID_FIELD`, retried without end) and never
/// writes a valid key there since FRD-12, so the executor leaves out any
/// value that is not this delivery's upload key.
void main() {
  const path = '/data/user/0/et.wereda.connect/app_flutter/pickup_1.jpg';
  const key = 'uploads/photos/del-1/pickup-1730000000000.webp';

  group('isDeliveryPhotoKeyOf', () {
    test('accepts only an upload key of this delivery', () {
      expect(isDeliveryPhotoKeyOf(key, 'del-1'), isTrue);
      expect(
        isDeliveryPhotoKeyOf(
          'uploads/photos/del-1/dropoff-1730000000000.jpg',
          'del-1',
        ),
        isTrue,
      );
      for (final ref in <Object?>[
        null,
        '',
        42,
        path,
        'file:///storage/emulated/0/pickup.jpg',
        'uploads/photos/del-2/pickup-1730000000000.webp',
        'photos/del-1/pickup-1730000000000.webp',
        'uploads/photos/del-1/pickup-1730000000000.png',
        'uploads/photos/del-1/cargo-1730000000000.webp',
        '$key/../x',
      ]) {
        expect(isDeliveryPhotoKeyOf(ref, 'del-1'), isFalse, reason: '$ref');
      }
    });
  });

  group('wireOperationData', () {
    test('a delivery op loses a photo path and keeps everything else', () {
      expect(
        wireOperationData(
          '{"status":"pickedUp","version":1,"pickupPhotoRef":"$path",'
          '"dropoffPhotoRef":"uploads/photos/del-2/dropoff-1.webp",'
          '"_localPath":"$path"}',
          entityType: 'delivery',
          entityId: 'del-1',
        ),
        {'status': 'pickedUp', 'version': 1},
      );
    });

    test("a delivery op keeps the delivery's own upload key", () {
      expect(
        wireOperationData(
          '{"status":"pickedUp","version":1,"pickupPhotoRef":"$key"}',
          entityType: 'delivery',
          entityId: 'del-1',
        ),
        {'status': 'pickedUp', 'version': 1, 'pickupPhotoRef': key},
      );
    });

    test('a null or empty ref is left out too', () {
      expect(
        wireOperationData(
          '{"status":"delivered","dropoffPhotoRef":null,"pickupPhotoRef":""}',
          entityType: 'delivery',
          entityId: 'del-1',
        ),
        {'status': 'delivered'},
      );
    });

    test('other entities and callers without the entity are untouched', () {
      const data = '{"pickupPhotoRef":"$path","version":2}';
      expect(
        wireOperationData(data, entityType: 'order', entityId: 'del-1'),
        {'pickupPhotoRef': path, 'version': 2},
      );
      expect(wireOperationData(data), {'pickupPhotoRef': path, 'version': 2});
    });
  });

  group('SyncExecutor', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late _FakeConnectivitySource source;
    late Dio dio;
    late ConnectivityMonitor monitor;
    late SyncExecutor executor;
    late List<Map<String, dynamic>> sentData;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('sync_photo_ref_test_');
      isar = await Isar.open(
        [SyncOperationSchema],
        directory: tempDir.path,
        name: 'sync_photo_ref_${DateTime.now().microsecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      source = _FakeConnectivitySource();
      dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
      sentData = [];
      dio.interceptors.add(
        InterceptorsWrapper(
          onRequest: (options, handler) {
            if (options.path == '/sync') {
              final body = options.data as Map<String, dynamic>;
              for (final op in body['operations'] as List) {
                sentData.add(
                  (op as Map<String, dynamic>)['data'] as Map<String, dynamic>,
                );
              }
            }
            handler.next(options);
          },
        ),
      );
      DioAdapter(dio: dio).onPost(
        '/sync',
        (server) => server.reply(200, {
          'syncBatchId': 'b',
          'results': [
            {'operationId': 'op-1', 'status': 'success', 'serverVersion': 2},
          ],
        }),
        data: Matchers.any,
      );
      monitor = ConnectivityMonitor(source: source, dio: dio);
      executor = SyncExecutor(
        syncQueue: syncQueue,
        connectivityMonitor: monitor,
        dio: dio,
        clientId: 'test-device',
        securityEvents: SecurityEvents.forTesting(),
      );
    });

    tearDown(() async {
      executor.dispose();
      monitor.dispose();
      await source.controller.close();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    test('an old queued pickup goes out without its photo path', () async {
      await syncQueue.enqueue(
        userId: 'trucker-1',
        entityType: 'delivery',
        entityId: 'del-1',
        action: 'update',
        data: '{"status":"pickedUp","version":1,"pickupPhotoRef":"$path"}',
        operationId: 'op-1',
      );

      final result = await executor.executeBatch();

      expect(sentData.single, {'status': 'pickedUp', 'version': 1});
      expect(result.succeeded, 1);
      expect((await syncQueue.getOperation('op-1'))!.status, SyncStatus.synced);
    });
  });
}
