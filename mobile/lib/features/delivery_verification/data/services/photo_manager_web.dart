import 'package:flutter/foundation.dart';
import 'package:image_picker/image_picker.dart';

import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_types.dart';

/// Handoff photos in the browser (FRD-12). The web app keeps no files and
/// is online whenever it is used, so the photo goes up as soon as it is
/// taken: the browser shrinks it to 800 px at JPEG quality 70 on a canvas,
/// then [DeliveryPhotoUploader] sends it to its presigned PUT. The result
/// carries the server's key for the handoff confirm and the bytes for the
/// preview.
class PhotoManager {
  /// JPEG quality of the browser's re-encode: a camera photo lands well
  /// under the 200 KB cap at 800 px.
  static const int webJpegQuality = 70;

  final ImagePicker _picker;
  final DeliveryPhotoUploader _uploader;

  PhotoManager({ImagePicker? picker, DeliveryPhotoUploader? uploader})
      : _picker = picker ?? ImagePicker(),
        _uploader = uploader ?? DeliveryPhotoUploader();

  /// Take a photo, shrink it and upload it as the [type] photo of
  /// [deliveryId].
  Future<PhotoResult> captureAndCompress({
    required String deliveryId,
    required PhotoType type,
  }) async {
    if (!DeliveryConstants.uuidRegex.hasMatch(deliveryId)) {
      return const PhotoResult.error('Invalid delivery ID format');
    }

    final Uint8List bytes;
    try {
      final picked = await _picker.pickImage(
        source: ImageSource.camera,
        maxWidth: DeliveryConstants.maxPhotoWidth.toDouble(),
        maxHeight: DeliveryConstants.maxPhotoWidth.toDouble(),
        imageQuality: webJpegQuality,
        requestFullMetadata: false,
      );
      if (picked == null) return const PhotoResult.cancelled();
      bytes = await picked.readAsBytes();
    } on Object catch (e) {
      debugPrint('[PhotoManager] camera failed: $e');
      return const PhotoResult.error('Photo capture failed');
    }

    // Only the bytes are trusted: the browser re-encodes JPEG and WebP; a
    // PNG would stay lossless and far over the cap.
    final contentType = sniffImageContentType(bytes);
    if (contentType != 'image/jpeg' && contentType != 'image/webp') {
      return const PhotoResult.error('Unsupported photo type');
    }
    if (bytes.length > DeliveryConstants.maxPhotoSizeBytes) {
      return const PhotoResult.error('Photo too large');
    }

    try {
      final photoRef = await _uploader.upload(
        deliveryId: deliveryId,
        type: type.name,
        bytes: bytes,
        contentType: contentType!,
      );
      return PhotoResult.success(
        localPath: null,
        fileSizeBytes: bytes.length,
        serverPhotoRef: photoRef,
        previewBytes: bytes,
      );
    } on Object catch (e) {
      debugPrint('[PhotoManager] photo upload failed: $e');
      return const PhotoResult.error('Photo upload failed');
    }
  }

  /// The browser keeps no photo files.
  Future<String?> getLocalPhotoPath(String deliveryId, PhotoType type) async =>
      null;

  /// The browser keeps no photo files.
  Future<void> deleteLocalPhoto(String path) async {}
}
