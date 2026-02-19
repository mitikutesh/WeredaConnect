import 'dart:io';

import 'package:flutter_image_compress/flutter_image_compress.dart';
import 'package:image_picker/image_picker.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';

/// Photo capture, compression, and local storage for delivery evidence.
///
/// Pipeline: ImagePicker (maxWidth:800) -> FlutterImageCompress (WebP, 75%)
/// -> size check (<200KB) -> fallback quality 60% -> local storage.
///
/// Security:
/// - F12-SEC-09: deliveryId validated as UUID before path construction
/// - F12-SEC-12: EXIF stripping verified by flutter_image_compress WebP transcode
/// - Photos stored in app-private directory (not accessible by other apps)
class PhotoManager {
  final ImagePicker _picker;

  PhotoManager({ImagePicker? picker}) : _picker = picker ?? ImagePicker();

  /// Capture photo from camera, compress to WebP, save locally.
  ///
  /// Returns [PhotoResult.success] with local file path and size,
  /// or [PhotoResult.error] with reason.
  Future<PhotoResult> captureAndCompress({
    required String deliveryId,
    required PhotoType type,
  }) async {
    // F12-SEC-09: Validate deliveryId format to prevent path traversal
    if (!DeliveryConstants.uuidRegex.hasMatch(deliveryId)) {
      return const PhotoResult.error('Invalid delivery ID format');
    }

    try {
      final picked = await _picker.pickImage(
        source: ImageSource.camera,
        maxWidth: DeliveryConstants.maxPhotoWidth.toDouble(),
      );

      if (picked == null) {
        return const PhotoResult.error('Photo capture cancelled');
      }

      final photoDir = await _getPhotoDir(deliveryId);
      final timestamp = DateTime.now().toUtc().millisecondsSinceEpoch;
      final outputPath = '${photoDir.path}/${type.name}-$timestamp.webp';

      // Compress to WebP at primary quality
      var compressed = await FlutterImageCompress.compressAndGetFile(
        picked.path,
        outputPath,
        format: CompressFormat.webp,
        quality: DeliveryConstants.photoQuality,
        minWidth: DeliveryConstants.maxPhotoWidth,
        minHeight: DeliveryConstants.minPhotoHeight,
      );

      if (compressed == null) {
        return const PhotoResult.error('Photo compression failed');
      }

      // Check size limit; retry at fallback quality if needed
      var fileSize = await compressed.length();
      if (fileSize > DeliveryConstants.maxPhotoSizeBytes) {
        final fallbackPath = '${photoDir.path}/${type.name}-$timestamp-fb.webp';
        compressed = await FlutterImageCompress.compressAndGetFile(
          picked.path,
          fallbackPath,
          format: CompressFormat.webp,
          quality: DeliveryConstants.photoFallbackQuality,
          minWidth: DeliveryConstants.maxPhotoWidth,
          minHeight: DeliveryConstants.minPhotoHeight,
        );

        if (compressed == null) {
          return const PhotoResult.error('Photo compression failed');
        }

        // Delete the oversized first attempt
        final firstFile = File(outputPath);
        if (firstFile.existsSync()) {
          await firstFile.delete();
        }

        fileSize = await compressed.length();
      }

      return PhotoResult.success(
        localPath: compressed.path,
        fileSizeBytes: fileSize,
      );
    } on Exception catch (e) {
      return PhotoResult.error('Photo capture failed: $e');
    }
  }

  /// Get local photo file path for a delivery and type.
  ///
  /// Returns the path of the most recent matching photo, or null if none.
  Future<String?> getLocalPhotoPath(
    String deliveryId,
    PhotoType type,
  ) async {
    if (!DeliveryConstants.uuidRegex.hasMatch(deliveryId)) return null;

    final photoDir = await _getPhotoDir(deliveryId);
    if (!photoDir.existsSync()) return null;

    final prefix = type.name;
    final files = photoDir
        .listSync()
        .whereType<File>()
        .where((f) => f.path.contains('/$prefix-') && f.path.endsWith('.webp'))
        .toList()
      ..sort((a, b) => b.path.compareTo(a.path));

    return files.isEmpty ? null : files.first.path;
  }

  /// Delete a local photo file.
  ///
  /// F12-CR-02: Validates that the path is within the app's photos directory
  /// before deletion to prevent arbitrary file deletion.
  Future<void> deleteLocalPhoto(String path) async {
    final appDir = await getApplicationDocumentsDirectory();
    final photosPrefix = '${appDir.path}/photos/';
    if (!path.startsWith(photosPrefix)) {
      return;
    }
    final file = File(path);
    if (file.existsSync()) {
      await file.delete();
    }
  }

  /// Get the photo storage directory for a delivery, creating it if needed.
  Future<Directory> _getPhotoDir(String deliveryId) async {
    final appDir = await getApplicationDocumentsDirectory();
    final photoDir = Directory('${appDir.path}/photos/$deliveryId');
    if (!photoDir.existsSync()) {
      await photoDir.create(recursive: true);
    }
    return photoDir;
  }
}

/// Type of photo evidence for delivery verification.
enum PhotoType {
  /// Photo taken at pickup (farmer's location).
  pickup,

  /// Photo taken at dropoff (buyer's location).
  dropoff,
}

/// Result of photo capture and compression.
class PhotoResult {
  final bool success;
  final String? localPath;
  final int? fileSizeBytes;
  final String? errorMessage;

  const PhotoResult.success({
    required this.localPath,
    required this.fileSizeBytes,
  })  : success = true,
        errorMessage = null;

  const PhotoResult.error(this.errorMessage)
      : success = false,
        localPath = null,
        fileSizeBytes = null;
}

/// Result of photo upload to server.
class PhotoUploadResult {
  final bool success;

  /// S3 key returned by server.
  final String? serverPhotoRef;
  final String? errorMessage;

  const PhotoUploadResult.success(this.serverPhotoRef)
      : success = true,
        errorMessage = null;

  const PhotoUploadResult.error(this.errorMessage)
      : success = false,
        serverPhotoRef = null;
}
