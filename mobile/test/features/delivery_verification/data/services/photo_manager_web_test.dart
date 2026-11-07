import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager_web.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_types.dart';

class _MockImagePicker extends Mock implements ImagePicker {}

class _FakeUploader implements DeliveryPhotoUploader {
  Future<String> Function() answer = () async => _key;
  final sent = <({String type, int size, String contentType})>[];

  @override
  Future<String> upload({
    required String deliveryId,
    required String type,
    required Uint8List bytes,
    required String contentType,
  }) {
    sent.add((type: type, size: bytes.length, contentType: contentType));
    return answer();
  }
}

const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _key = 'uploads/photos/$_deliveryId/dropoff-1700000000000.jpg';

/// A JPEG's first bytes (FF D8 FF) padded to [size].
Uint8List _jpeg(int size) =>
    Uint8List(size)..setAll(0, const [0xFF, 0xD8, 0xFF, 0xE0]);

/// FRD-12: in the browser the photo goes up as soon as it is taken (the web
/// app keeps no files and is online when used), as the browser's JPEG under
/// the 200 KB cap.
void main() {
  late _MockImagePicker picker;
  late _FakeUploader uploader;
  late PhotoManager manager;

  setUpAll(() => registerFallbackValue(ImageSource.camera));

  setUp(() {
    picker = _MockImagePicker();
    uploader = _FakeUploader();
    manager = PhotoManager(picker: picker, uploader: uploader);
  });

  void pick(Uint8List? bytes) {
    when(
      () => picker.pickImage(
        source: any(named: 'source'),
        maxWidth: any(named: 'maxWidth'),
        maxHeight: any(named: 'maxHeight'),
        imageQuality: any(named: 'imageQuality'),
        requestFullMetadata: any(named: 'requestFullMetadata'),
      ),
    ).thenAnswer(
      (_) async => bytes == null ? null : XFile.fromData(bytes),
    );
  }

  Future<PhotoResult> take() => manager.captureAndCompress(
        deliveryId: _deliveryId,
        type: PhotoType.dropoff,
      );

  test('the photo goes up when taken and comes back as its key', () async {
    final bytes = _jpeg(90 * 1024);
    pick(bytes);

    final result = await take();

    expect(result.success, isTrue);
    expect(result.photoRef, _key);
    expect(result.localPath, isNull);
    expect(result.previewBytes, bytes);
    expect(
      uploader.sent.single,
      (type: 'dropoff', size: 90 * 1024, contentType: 'image/jpeg'),
    );
    // The browser shrinks it to 800 px at JPEG quality 70.
    verify(
      () => picker.pickImage(
        source: ImageSource.camera,
        maxWidth: 800,
        maxHeight: 800,
        imageQuality: PhotoManager.webJpegQuality,
        requestFullMetadata: false,
      ),
    ).called(1);
  });

  test('a closed camera is a cancel, not an error', () async {
    pick(null);

    final result = await take();

    expect(result.cancelled, isTrue);
    expect(uploader.sent, isEmpty);
  });

  test('a photo over the 200 KB cap is not sent', () async {
    pick(_jpeg(200 * 1024 + 1));

    final result = await take();

    expect(result.success, isFalse);
    expect(result.cancelled, isFalse);
    expect(uploader.sent, isEmpty);
  });

  test('a PNG is not sent (lossless, far over the cap)', () async {
    pick(
      Uint8List(1024)
        ..setAll(0, const [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
    );

    final result = await take();

    expect(result.success, isFalse);
    expect(uploader.sent, isEmpty);
  });

  test('a failed upload is an error the screen shows', () async {
    pick(_jpeg(1024));
    uploader.answer = () async => throw const NetworkException();

    final result = await take();

    expect(result.success, isFalse);
    expect(result.cancelled, isFalse);
  });
}
