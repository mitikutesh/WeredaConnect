import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mocktail/mocktail.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';

// --- Mocks ---

class MockImagePicker extends Mock implements ImagePicker {}

class MockXFile extends Mock implements XFile {}

// Fake PathProviderPlatform to control getApplicationDocumentsDirectory().
class FakePathProvider extends Fake
    with MockPlatformInterfaceMixin
    implements PathProviderPlatform {
  final String basePath;

  FakePathProvider(this.basePath);

  @override
  Future<String?> getApplicationDocumentsPath() async => basePath;

  @override
  Future<String?> getTemporaryPath() async => '$basePath/tmp';

  @override
  Future<String?> getApplicationSupportPath() async => '$basePath/support';

  @override
  Future<String?> getLibraryPath() async => '$basePath/library';

  @override
  Future<String?> getApplicationCachePath() async => '$basePath/cache';

  @override
  Future<String?> getExternalStoragePath() async => null;

  @override
  Future<List<String>?> getExternalCachePaths() async => null;

  @override
  Future<List<String>?> getExternalStoragePaths({
    StorageDirectory? type,
  }) async =>
      null;

  @override
  Future<String?> getDownloadsPath() async => '$basePath/downloads';
}

// Valid UUID for tests.
const _validUuid = '550e8400-e29b-41d4-a716-446655440000';

void main() {
  late MockImagePicker mockPicker;
  late PhotoManager photoManager;
  late Directory tempDir;
  late String tempPath;

  setUpAll(() {
    registerFallbackValue(ImageSource.camera);
  });

  setUp(() async {
    mockPicker = MockImagePicker();
    photoManager = PhotoManager(picker: mockPicker);

    // Create a real temp directory for path_provider to return.
    tempDir = await Directory.systemTemp.createTemp('photo_manager_test_');
    tempPath = tempDir.path;

    PathProviderPlatform.instance = FakePathProvider(tempPath);
  });

  tearDown(() async {
    if (tempDir.existsSync()) {
      await tempDir.delete(recursive: true);
    }
  });

  group('PhotoManager', () {
    group('UUID validation', () {
      test('rejects delivery ID containing path traversal (..)', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: '../../../etc/passwd',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('rejects delivery ID with slash characters', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: 'abc/def',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('rejects empty delivery ID', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: '',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('rejects uppercase UUID', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: '550E8400-E29B-41D4-A716-446655440000',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('rejects non-UUID string', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: 'not-a-uuid-at-all',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('rejects UUID with extra characters', () async {
        final result = await photoManager.captureAndCompress(
          deliveryId: '${_validUuid}extra',
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Invalid delivery ID format');
      });

      test('accepts valid lowercase UUID v4', () async {
        // This will proceed past validation and hit the picker.
        when(
          () => mockPicker.pickImage(
            source: any(named: 'source'),
            maxWidth: any(named: 'maxWidth'),
          ),
        ).thenAnswer((_) async => null);

        final result = await photoManager.captureAndCompress(
          deliveryId: _validUuid,
          type: PhotoType.pickup,
        );

        // Should not fail with "Invalid delivery ID format" -
        // it gets past validation and returns cancelled because
        // picker returned null.
        expect(result.errorMessage, isNot('Invalid delivery ID format'));
      });
    });

    group('captureAndCompress', () {
      test('returns error when picker returns null (user cancelled)', () async {
        when(
          () => mockPicker.pickImage(
            source: any(named: 'source'),
            maxWidth: any(named: 'maxWidth'),
          ),
        ).thenAnswer((_) async => null);

        final result = await photoManager.captureAndCompress(
          deliveryId: _validUuid,
          type: PhotoType.pickup,
        );

        expect(result.success, isFalse);
        expect(result.errorMessage, 'Photo capture cancelled');
      });

      test('passes ImageSource.camera and maxWidth to picker', () async {
        when(
          () => mockPicker.pickImage(
            source: any(named: 'source'),
            maxWidth: any(named: 'maxWidth'),
          ),
        ).thenAnswer((_) async => null);

        await photoManager.captureAndCompress(
          deliveryId: _validUuid,
          type: PhotoType.dropoff,
        );

        verify(
          () => mockPicker.pickImage(
            source: ImageSource.camera,
            maxWidth: DeliveryConstants.maxPhotoWidth.toDouble(),
          ),
        ).called(1);
      });
    });

    group('getLocalPhotoPath', () {
      test('returns null for invalid delivery ID', () async {
        final path = await photoManager.getLocalPhotoPath(
          '../traversal',
          PhotoType.pickup,
        );

        expect(path, isNull);
      });

      test('returns null for non-existent delivery directory', () async {
        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        expect(path, isNull);
      });

      test('returns null when directory exists but has no matching files',
          () async {
        // Create the directory but leave it empty.
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        expect(path, isNull);
      });

      test('returns matching file path for pickup type', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        // Create a matching file.
        final file = File('${photoDir.path}/pickup-1000.webp');
        await file.writeAsBytes([0x00]);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        expect(path, isNotNull);
        expect(path, file.path);
      });

      test('returns matching file path for dropoff type', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        final file = File('${photoDir.path}/dropoff-2000.webp');
        await file.writeAsBytes([0x00]);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.dropoff,
        );

        expect(path, isNotNull);
        expect(path, file.path);
      });

      test('returns most recent file when multiple exist', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        // Create older and newer files (sorted by path descending).
        final older = File('${photoDir.path}/pickup-1000.webp');
        await older.writeAsBytes([0x00]);

        final newer = File('${photoDir.path}/pickup-2000.webp');
        await newer.writeAsBytes([0x00]);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        // Should return the one with the higher timestamp (sorted desc).
        expect(path, newer.path);
      });

      test('does not return files of wrong type', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        // Only a dropoff file exists.
        final file = File('${photoDir.path}/dropoff-1000.webp');
        await file.writeAsBytes([0x00]);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        expect(path, isNull);
      });

      test('does not return non-webp files', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        // File with right prefix but wrong extension.
        final file = File('${photoDir.path}/pickup-1000.jpg');
        await file.writeAsBytes([0x00]);

        final path = await photoManager.getLocalPhotoPath(
          _validUuid,
          PhotoType.pickup,
        );

        expect(path, isNull);
      });
    });

    group('deleteLocalPhoto', () {
      test('deletes file within the photos directory', () async {
        final photoDir = Directory('$tempPath/photos/$_validUuid');
        await photoDir.create(recursive: true);

        final file = File('${photoDir.path}/pickup-1000.webp');
        await file.writeAsBytes([0x00]);
        expect(file.existsSync(), isTrue);

        await photoManager.deleteLocalPhoto(file.path);

        expect(file.existsSync(), isFalse);
      });

      test('rejects path outside photos directory (path traversal)', () async {
        // Create a file outside the photos directory.
        final outsideFile = File('$tempPath/sensitive.txt');
        await outsideFile.writeAsString('secret');
        expect(outsideFile.existsSync(), isTrue);

        await photoManager.deleteLocalPhoto(outsideFile.path);

        // File should NOT have been deleted.
        expect(outsideFile.existsSync(), isTrue);
      });

      test('rejects path with traversal attempt to escape photos dir',
          () async {
        // Attempt to traverse out of photos directory using ../
        final traversalPath = '$tempPath/photos/../sensitive.txt';

        // Create the target file.
        final targetFile = File('$tempPath/sensitive.txt');
        await targetFile.writeAsString('secret');

        await photoManager.deleteLocalPhoto(traversalPath);

        // Path canonicalization resolves ../ before the prefix check,
        // so the file must NOT be deleted.
        expect(targetFile.existsSync(), isTrue);
      });

      test('handles already-deleted file gracefully', () async {
        final deletedPath = '$tempPath/photos/pickup-1000.webp';

        // Should not throw even though file doesn't exist.
        await expectLater(
          photoManager.deleteLocalPhoto(deletedPath),
          completes,
        );
      });

      test('rejects absolute path to system directory', () async {
        await photoManager.deleteLocalPhoto('/etc/passwd');

        // /etc/passwd should still exist (and we didn't try to delete it).
        // The path doesn't start with the photos prefix so it's rejected.
      });

      test('rejects empty path', () async {
        await expectLater(
          photoManager.deleteLocalPhoto(''),
          completes,
        );
      });
    });

    group('DeliveryConstants.uuidRegex', () {
      test('matches valid UUID v4', () {
        expect(
          DeliveryConstants.uuidRegex.hasMatch(_validUuid),
          isTrue,
        );
      });

      test('matches UUID with all hex digits', () {
        expect(
          DeliveryConstants.uuidRegex
              .hasMatch('abcdef01-2345-6789-abcd-ef0123456789'),
          isTrue,
        );
      });

      test('rejects uppercase hex', () {
        expect(
          DeliveryConstants.uuidRegex
              .hasMatch('550E8400-E29B-41D4-A716-446655440000'),
          isFalse,
        );
      });

      test('rejects missing hyphens', () {
        expect(
          DeliveryConstants.uuidRegex
              .hasMatch('550e8400e29b41d4a716446655440000'),
          isFalse,
        );
      });

      test('rejects too-short UUID', () {
        expect(
          DeliveryConstants.uuidRegex.hasMatch('550e8400-e29b-41d4-a716'),
          isFalse,
        );
      });

      test('rejects UUID with trailing newline', () {
        expect(
          DeliveryConstants.uuidRegex
              .hasMatch('550e8400-e29b-41d4-a716-446655440000\n'),
          isFalse,
        );
      });

      test('rejects path traversal string', () {
        expect(
          DeliveryConstants.uuidRegex.hasMatch('../../../etc/passwd'),
          isFalse,
        );
      });

      test('rejects empty string', () {
        expect(
          DeliveryConstants.uuidRegex.hasMatch(''),
          isFalse,
        );
      });
    });
  });
}
