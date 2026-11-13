import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_sync_preflight.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';

/// Records uploads and answers with [answer] (a key, or throws).
class _FakeUploader implements DeliveryPhotoUploader {
  _FakeUploader(this.answer);

  Future<String> Function() answer;
  final calls = <({String deliveryId, String type, int size, String type2})>[];

  @override
  Future<String> upload({
    required String deliveryId,
    required String type,
    required Uint8List bytes,
    required String contentType,
  }) {
    calls.add(
      (
        deliveryId: deliveryId,
        type: type,
        size: bytes.length,
        type2: contentType,
      ),
    );
    return answer();
  }
}

/// The first bytes of a WebP file (RIFF....WEBP), enough for the sniff.
final _webp = Uint8List.fromList([
  0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, //
  0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20,
]);

const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _path =
    '/data/user/0/app/files/photos/$_deliveryId/pickup-1700000000000.webp';
const _key = 'uploads/photos/$_deliveryId/pickup-1700000000000.webp';

final _queuedAt = DateTime.utc(2026, 11, 7, 8);

SyncOperation _photoOp({Map<String, dynamic>? data, String type = 'pickup'}) =>
    SyncOperation()
      ..operationId = 'op-photo-1'
      ..idempotencyKey = 'trucker-1:delivery_photo:x:create:1'
      ..entityType = deliveryPhotoEntityType
      ..entityId = deliveryPhotoEntityId(_deliveryId, type)
      ..action = 'create'
      ..data = jsonEncode(data ?? {deliveryPhotoLocalPathKey: _path})
      ..status = SyncStatus.pending
      ..retryCount = 0
      ..createdAt = _queuedAt;

/// FRD-12: the handoff photo goes up before its `delivery_photo` attach is
/// sent, and the attach then carries only the server's key.
void main() {
  late _FakeUploader uploader;
  late Map<String, String> saved;
  late Map<String, Uint8List?> files;
  late bool deliveryOpsOpen;
  late DateTime now;
  late List<Map<String, Object?>> attached;
  late DeliveryPhotoSyncPreflight preflight;

  setUp(() {
    uploader = _FakeUploader(() async => _key);
    saved = {};
    files = {_path: _webp};
    deliveryOpsOpen = false;
    now = _queuedAt.add(const Duration(minutes: 1));
    attached = [];
    preflight = DeliveryPhotoSyncPreflight(
      uploader: uploader,
      readFile: (path) async => files[path],
      saveData: (operationId, data) async => saved[operationId] = data,
      deliveryOpsOpen: (_) async => deliveryOpsOpen,
      now: () => now,
      onAttached: ({
        required deliveryId,
        required type,
        required photoRef,
        required localPath,
      }) async =>
          attached.add({
        'deliveryId': deliveryId,
        'type': type,
        'photoRef': photoRef,
        'localPath': localPath,
      }),
    );
  });

  test('another entity goes as it is', () async {
    final op = _photoOp()..entityType = 'delivery';

    expect(await preflight(op), isTrue);
    expect(uploader.calls, isEmpty);
  });

  test('uploads the photo, then the attach carries only its key', () async {
    final op = _photoOp();

    expect(await preflight(op), isTrue);

    expect(uploader.calls.single.deliveryId, _deliveryId);
    expect(uploader.calls.single.type, 'pickup');
    expect(uploader.calls.single.size, _webp.length);
    expect(uploader.calls.single.type2, 'image/webp');
    // This batch sends the key, and so does any later run (no second upload);
    // the `_` path stays on the phone for the clean-up after the attach.
    final ready = {'photoRef': _key, deliveryPhotoLocalPathKey: _path};
    expect(jsonDecode(op.data), ready);
    expect(jsonDecode(saved['op-photo-1']!), ready);
    expect(wireOperationData(op.data), {'photoRef': _key});
  });

  test('a photo uploaded in an earlier run is not uploaded again', () async {
    final op = _photoOp(data: {'photoRef': _key});

    expect(await preflight(op), isTrue);
    expect(uploader.calls, isEmpty);
    expect(saved, isEmpty);
  });

  test('offline, the attach waits for the next run', () async {
    uploader.answer = () async => throw const NetworkException();
    final op = _photoOp();

    expect(await preflight(op), isFalse);
    expect(jsonDecode(op.data), {deliveryPhotoLocalPathKey: _path});
    expect(saved, isEmpty);
  });

  test('a PUT that did not reach S3 waits, without counting', () async {
    uploader.answer = () async => throw DioException(
          requestOptions: RequestOptions(path: '/put'),
          type: DioExceptionType.connectionTimeout,
        );

    expect(await preflight(_photoOp()), isFalse);
    expect(saved, isEmpty);
  });

  DioException putAnswered(int status) => DioException(
        requestOptions: RequestOptions(path: '/put'),
        type: DioExceptionType.badResponse,
        response: Response<void>(
          requestOptions: RequestOptions(path: '/put'),
          statusCode: status,
        ),
      );

  test('a PUT answered 5xx waits, without counting', () async {
    uploader.answer = () async => throw putAnswered(503);

    expect(await preflight(_photoOp()), isFalse);
    expect(saved, isEmpty);
  });

  test(
      'a PUT refused (4xx) is counted, and the third refusal gives the '
      'photo up: no endless 200 KB uploads', () async {
    uploader.answer = () async => throw putAnswered(403);
    final op = _photoOp();

    expect(await preflight(op), isFalse);
    expect(jsonDecode(op.data)[DeliveryPhotoSyncPreflight.putRefusalsKey], 1);
    expect(await preflight(op), isFalse);
    expect(jsonDecode(op.data)[DeliveryPhotoSyncPreflight.putRefusalsKey], 2);
    // The count never reaches the server.
    expect(wireOperationData(op.data), <String, dynamic>{});

    expect(await preflight(op), isTrue);
    expect(uploader.calls, hasLength(3));
    expect(jsonDecode(op.data), {deliveryPhotoLocalPathKey: _path});
  });

  group('a delivery the server does not know', () {
    setUp(() => uploader.answer = () async => throw const NotFoundException());

    test('waits while its create still waits on the phone', () async {
      deliveryOpsOpen = true;
      now = _queuedAt.add(const Duration(days: 3));

      expect(await preflight(_photoOp()), isFalse);
      expect(saved, isEmpty);
    });

    test('waits a short grace after the photo was queued', () async {
      now = _queuedAt.add(const Duration(minutes: 9));

      expect(await preflight(_photoOp()), isFalse);
    });

    test(
        'is given up once nothing of it waits and the grace is over: the '
        'server settles the photo', () async {
      now = _queuedAt.add(DeliveryPhotoSyncPreflight.notFoundGrace);
      final op = _photoOp();

      expect(await preflight(op), isTrue);
      expect(wireOperationData(op.data), <String, dynamic>{});
      expect(
        jsonDecode(saved['op-photo-1']!),
        {deliveryPhotoLocalPathKey: _path},
      );
    });
  });

  test('an expired session (401) waits for the next run', () async {
    uploader.answer = () async => throw const AuthException();

    expect(await preflight(_photoOp()), isFalse);
  });

  for (final refusal in <ApiException>[
    const AuthException('not your photo', 403, 'DELIVERY_UNAUTHORIZED'),
    const ValidationException('too large', 400, 'DELIVERY_PHOTO_TOO_LARGE'),
    const ConflictException('exists', 409, 'DELIVERY_PHOTO_EXISTS'),
    const ConflictException('cancelled', 409, 'DELIVERY_PHOTO_REFUSED'),
  ]) {
    test(
        'a refusal for good (${refusal.statusCode}) goes without a key, so '
        'the server settles it', () async {
      uploader.answer = () async => throw refusal;
      final op = _photoOp();

      expect(await preflight(op), isTrue);
      expect(wireOperationData(op.data), <String, dynamic>{});
      expect(
        jsonDecode(saved['op-photo-1']!),
        {deliveryPhotoLocalPathKey: _path},
      );
    });
  }

  test('a photo file that is gone goes without a key', () async {
    files.clear();
    final op = _photoOp();

    expect(await preflight(op), isTrue);
    expect(uploader.calls, isEmpty);
    expect(wireOperationData(op.data), <String, dynamic>{});
  });

  test('a file that is not an image goes without a key', () async {
    files[_path] = Uint8List.fromList(utf8.encode('not a photo at all'));

    expect(await preflight(_photoOp()), isTrue);
    expect(uploader.calls, isEmpty);
  });

  test('an attached photo hands its file to the clean-up', () async {
    final op = _photoOp(
      type: 'dropoff',
      data: {'photoRef': _key, deliveryPhotoLocalPathKey: _path},
    );

    await preflight.onSynced(op);

    expect(attached, [
      {
        'deliveryId': _deliveryId,
        'type': 'dropoff',
        'photoRef': _key,
        'localPath': _path,
      },
    ]);
  });

  test('a photo given up has nothing to clean up', () async {
    await preflight
        .onSynced(_photoOp(data: {deliveryPhotoLocalPathKey: _path}));
    await preflight.onSynced(_photoOp()..entityType = 'delivery');

    expect(attached, isEmpty);
  });

  test('a drop-off photo goes up as the drop-off', () async {
    await preflight(_photoOp(type: 'dropoff'));

    expect(uploader.calls.single.type, 'dropoff');
  });

  test("ESC-36: the trucker's arrival photo goes up as the arrival", () async {
    const arrivalKey = 'uploads/photos/$_deliveryId/arrival-1700000000000.webp';
    uploader.answer = () async => arrivalKey;
    final op = _photoOp(type: 'arrival');

    expect(await preflight(op), isTrue);

    expect(uploader.calls.single.type, 'arrival');
    expect(jsonDecode(op.data)[deliveryPhotoRefKey], arrivalKey);
    expect(
      parseDeliveryPhotoEntityId('$_deliveryId#arrival'),
      (deliveryId: _deliveryId, type: 'arrival'),
    );
    expect(parseDeliveryPhotoEntityId('$_deliveryId#other'), isNull);
    await preflight.onSynced(op);
    expect(attached.single['type'], 'arrival');
  });
}
