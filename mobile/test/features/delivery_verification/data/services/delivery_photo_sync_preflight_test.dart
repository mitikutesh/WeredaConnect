import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
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
      ..createdAt = DateTime.utc(2026, 11, 7);

/// FRD-12: the handoff photo goes up before its `delivery_photo` attach is
/// sent, and the attach then carries only the server's key.
void main() {
  late _FakeUploader uploader;
  late Map<String, String> saved;
  late Map<String, Uint8List?> files;
  late DeliveryPhotoSyncPreflight preflight;

  setUp(() {
    uploader = _FakeUploader(() async => _key);
    saved = {};
    files = {_path: _webp};
    preflight = DeliveryPhotoSyncPreflight(
      uploader: uploader,
      readFile: (path) async => files[path],
      saveData: (operationId, data) async => saved[operationId] = data,
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
    // This batch sends the key, and so does any later run (no second upload).
    expect(jsonDecode(op.data), {'photoRef': _key});
    expect(jsonDecode(saved['op-photo-1']!), {'photoRef': _key});
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

  test('a failed PUT to S3 waits for the next run', () async {
    uploader.answer = () async => throw DioException(
          requestOptions: RequestOptions(path: '/put'),
          type: DioExceptionType.connectionTimeout,
        );

    expect(await preflight(_photoOp()), isFalse);
  });

  test(
      'a delivery not found yet (its create is in the same batch) waits '
      'for the next run', () async {
    uploader.answer = () async => throw const NotFoundException();

    expect(await preflight(_photoOp()), isFalse);
  });

  test('an expired session (401) waits for the next run', () async {
    uploader.answer = () async => throw const AuthException();

    expect(await preflight(_photoOp()), isFalse);
  });

  for (final refusal in <ApiException>[
    const AuthException('not your photo', 403, 'DELIVERY_UNAUTHORIZED'),
    const ValidationException('too large', 400, 'DELIVERY_PHOTO_TOO_LARGE'),
    const ConflictException('exists', 409, 'DELIVERY_PHOTO_EXISTS'),
  ]) {
    test(
        'a refusal for good (${refusal.statusCode}) goes without a key, so '
        'the server settles it', () async {
      uploader.answer = () async => throw refusal;
      final op = _photoOp();

      expect(await preflight(op), isTrue);
      expect(jsonDecode(op.data), <String, dynamic>{});
      expect(saved['op-photo-1'], '{}');
    });
  }

  test('a photo file that is gone goes without a key', () async {
    files.clear();
    final op = _photoOp();

    expect(await preflight(op), isTrue);
    expect(uploader.calls, isEmpty);
    expect(jsonDecode(op.data), <String, dynamic>{});
  });

  test('a file that is not an image goes without a key', () async {
    files[_path] = Uint8List.fromList(utf8.encode('not a photo at all'));

    expect(await preflight(_photoOp()), isTrue);
    expect(uploader.calls, isEmpty);
  });

  test('a drop-off photo goes up as the drop-off', () async {
    await preflight(_photoOp(type: 'dropoff'));

    expect(uploader.calls.single.type, 'dropoff');
  });
}
