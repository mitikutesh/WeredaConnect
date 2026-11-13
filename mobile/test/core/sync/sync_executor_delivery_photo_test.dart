import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_sync_preflight.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';

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

class _FakeUploader implements DeliveryPhotoUploader {
  Future<String> Function() answer = () async => _key;
  int calls = 0;

  @override
  Future<String> upload({
    required String deliveryId,
    required String type,
    required Uint8List bytes,
    required String contentType,
  }) {
    calls++;
    return answer();
  }
}

const _userId = 'trucker-1';
const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _path =
    '/data/user/0/app/files/photos/$_deliveryId/pickup-1700000000000.webp';
const _key = 'uploads/photos/$_deliveryId/pickup-1700000000000.webp';

/// The first bytes of a WebP file (RIFF....WEBP).
final _webp = Uint8List.fromList([
  0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, //
  0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20,
]);

/// FRD-12: a handoff photo waits in the sync queue behind its handoff, goes
/// up through the preflight when online, and is attached by a
/// `delivery_photo` operation whose refusals are final and silent.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late _FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<SyncOperation> reverts;
  late _FakeUploader uploader;
  late List<String> cleanedUp;
  late SyncExecutor executor;
  late List<Map<String, dynamic>> requests;
  late Map<String, Map<String, Object>> answers;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_photo_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_photo_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    source = _FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    reverts = [];
    requests = [];
    answers = {};
    uploader = _FakeUploader();
    cleanedUp = [];
    final preflight = DeliveryPhotoSyncPreflight(
      uploader: uploader,
      readFile: (path) async => path == _path ? _webp : null,
      saveData: queue.replaceData,
      deliveryOpsOpen: (deliveryId) =>
          queue.hasOpenOperationsFor('delivery', deliveryId),
      onAttached: ({
        required deliveryId,
        required type,
        required photoRef,
        required localPath,
      }) async =>
          cleanedUp.add('$deliveryId#$type $photoRef $localPath'),
    );
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'mobile-1',
      securityEvents: events,
      onPermanentRejection: (op, _) async => reverts.add(op),
      preflight: preflight.call,
      onOperationSynced: preflight.onSynced,
    );
    adapter.onPost(
      '/sync',
      (server) => server.replyCallback(200, (options) {
        final body = Map<String, dynamic>.from(options.data as Map);
        requests.add(body);
        final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
        return {
          'syncBatchId': body['syncBatchId'],
          'results': [
            for (final op in ops)
              {
                'operationId': op['operationId'],
                ...answers[op['operationId']] ?? {'status': 'success'},
              },
          ],
        };
      }),
      data: Matchers.any,
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

  Future<SyncOperation> enqueuePickupWithPhoto() async {
    await queue.enqueue(
      userId: _userId,
      entityType: 'delivery',
      entityId: _deliveryId,
      action: 'update',
      data: jsonEncode({'status': 'pickedUp', 'version': 1}),
      operationId: 'op-pickup',
    );
    return queue.enqueue(
      userId: _userId,
      entityType: deliveryPhotoEntityType,
      entityId: deliveryPhotoEntityId(_deliveryId, 'pickup'),
      action: 'create',
      data: jsonEncode({deliveryPhotoLocalPathKey: _path}),
      operationId: 'op-photo',
      keyTimestamp: DateTime.utc(2026, 11, 7, 8),
    );
  }

  List<Map<String, dynamic>> sentOps() => [
        for (final request in requests)
          ...(request['operations'] as List).cast<Map<String, dynamic>>(),
      ];

  test('the photo goes up and its attach follows the pickup with the key only',
      () async {
    await enqueuePickupWithPhoto();

    await executor.executeBatch();

    expect(uploader.calls, 1);
    final ops = sentOps();
    expect(ops.map((op) => op['operationId']), ['op-pickup', 'op-photo']);
    expect(ops[1]['entityType'], 'delivery_photo');
    expect(ops[1]['entityId'], '$_deliveryId#pickup');
    expect(ops[1]['data'], {'photoRef': _key});
    expect(await queue.getPendingCount(), 0);
    // Attached: the phone's file goes.
    expect(cleanedUp, ['$_deliveryId#pickup $_key $_path']);
  });

  test('offline for the upload, only the photo waits: the pickup goes',
      () async {
    uploader.answer = () async => throw const NetworkException();
    await enqueuePickupWithPhoto();

    await executor.executeBatch();

    expect(sentOps().map((op) => op['operationId']), ['op-pickup']);
    final photo = await queue.getOperation('op-photo');
    expect(photo!.status, SyncStatus.pending);
    expect(photo.retryCount, 0);
    expect(jsonDecode(photo.data), {deliveryPhotoLocalPathKey: _path});

    // Back online: the photo goes up once and is attached.
    uploader.answer = () async => _key;
    await executor.executeBatch();
    expect(sentOps().last['data'], {'photoRef': _key});
    expect(uploader.calls, 2);
  });

  test('a readied attach that failed to send is sent again without upload',
      () async {
    await enqueuePickupWithPhoto();
    answers['op-photo'] = {
      'status': 'deferred',
      'message': 'DELIVERY_PHOTO_PENDING: Photo storage is not reachable.',
    };

    await executor.executeBatch();
    answers.clear();
    await executor.executeBatch();

    expect(uploader.calls, 1);
    expect(sentOps().last['data'], {'photoRef': _key});
    expect(await queue.getPendingCount(), 0);
  });

  test('a refused delivery create rejects its waiting photos', () async {
    await queue.enqueue(
      userId: _userId,
      entityType: 'delivery',
      entityId: _deliveryId,
      action: 'create',
      data: jsonEncode({'status': 'pending'}),
      operationId: 'op-create',
    );
    await enqueuePickupWithPhoto();
    // The delivery is unknown to the server: its create is in this batch.
    uploader.answer = () async => throw const NotFoundException();
    answers['op-create'] = {
      'status': 'error',
      'message': 'DELIVERY_ORDER_REQUIRED: orderId is required.',
    };

    await executor.executeBatch();

    final photo = await queue.getOperation('op-photo');
    expect(SyncQueue.isRejected(photo!), isTrue);
    expect(await queue.getPendingCount(), 0);
    expect(await queue.getFailedCount(), 1, reason: 'the create only');
    expect(
      sentOps().map((op) => op['operationId']),
      isNot(contains('op-photo')),
    );
  });

  test('a refused pickup rejects its pickup photo, not the drop-off photo',
      () async {
    await enqueuePickupWithPhoto();
    await queue.enqueue(
      userId: _userId,
      entityType: deliveryPhotoEntityType,
      entityId: deliveryPhotoEntityId(_deliveryId, 'dropoff'),
      action: 'create',
      data: jsonEncode({deliveryPhotoLocalPathKey: _path}),
      operationId: 'op-dropoff-photo',
      keyTimestamp: DateTime.utc(2026, 11, 7, 9),
    );
    // Both photos wait for the next run (offline for the uploads).
    uploader.answer = () async => throw const NetworkException();
    answers['op-pickup'] = {
      'status': 'error',
      'message': 'DELIVERY_CODE_MISMATCH: The code does not match.',
    };

    await executor.executeBatch();

    expect(
      SyncQueue.isRejected((await queue.getOperation('op-photo'))!),
      isTrue,
    );
    expect(
      (await queue.getOperation('op-dropoff-photo'))!.status,
      SyncStatus.pending,
    );
  });

  test(
      'a cancelled delivery ends the photo: the presign refusal sends it '
      'without a key and the server settles it', () async {
    await enqueuePickupWithPhoto();
    uploader.answer = () async => throw const ConflictException(
          'The delivery was cancelled.',
          409,
          'DELIVERY_PHOTO_REFUSED',
        );
    answers['op-photo'] = {
      'status': 'error',
      'message': 'DELIVERY_PHOTO_INVALID: photoRef must be the key POST '
          '/deliveries/{id}/photos issued for this photo.',
    };

    await executor.executeBatch();

    expect(sentOps().last['operationId'], 'op-photo');
    expect(sentOps().last['data'], <String, dynamic>{});
    expect(
      SyncQueue.isRejected((await queue.getOperation('op-photo'))!),
      isTrue,
    );
    expect(await queue.getFailedCount(), 0);
    expect(cleanedUp, isEmpty);
  });

  test('a refused attach is final, silent and leaves no failure to retry',
      () async {
    await enqueuePickupWithPhoto();
    answers['op-photo'] = {
      'status': 'error',
      'message': 'DELIVERY_PHOTO_FORBIDDEN: Only the trucker takes the pickup '
          'photo.',
    };

    await executor.executeBatch();

    final photo = await queue.getOperation('op-photo');
    expect(SyncQueue.isRejected(photo!), isTrue);
    expect(emitted.whereType<SyncOperationRejected>(), isEmpty);
    expect(reverts, isEmpty);
    expect(await queue.getFailedCount(), 0);
  });

  test(
      'FUP-373: a refused arrival photo is silent too, but goes to the '
      'rejection handler once, which forgets its kept file', () async {
    await queue.enqueue(
      userId: _userId,
      entityType: deliveryPhotoEntityType,
      entityId: deliveryPhotoEntityId(_deliveryId, arrivalPhotoType),
      action: 'create',
      data: jsonEncode({deliveryPhotoLocalPathKey: _path}),
      operationId: 'op-arrival',
    );
    answers['op-arrival'] = {
      'status': 'error',
      'message': 'DELIVERY_PHOTO_REFUSED: The arrival photo is taken while '
          'the goods are on the road.',
    };

    await executor.executeBatch();

    expect(
      SyncQueue.isRejected((await queue.getOperation('op-arrival'))!),
      isTrue,
    );
    expect(reverts.map((op) => op.operationId), ['op-arrival']);
    expect(emitted.whereType<SyncOperationRejected>(), isEmpty);
    expect(await queue.getFailedCount(), 0);
  });
}
