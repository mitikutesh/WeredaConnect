import 'package:flutter/foundation.dart';
import 'package:image_picker/image_picker.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// Why a pick produced fewer photos than the farmer chose.
enum CropPhotoPickIssue {
  /// A file was not a JPEG, PNG or WebP image.
  unsupported,

  /// A file was over the server's 10 MiB cap even after the resize.
  tooLarge,

  /// The camera or gallery failed (no permission, no camera, I/O).
  failed,
}

/// What a pick produced: the photos to add, and the first problem when some
/// files could not be used. A cancelled pick is no photos and no issue.
class CropPhotoPickResult {
  final List<CropPhotoDraft> photos;
  final CropPhotoPickIssue? issue;

  const CropPhotoPickResult(this.photos, [this.issue]);
}

/// Takes or chooses crop photos for a listing (crop-listing-api.md 1.6.0).
///
/// image_picker shrinks the image natively to
/// [CropPhotoRules.uploadMaxEdge] at JPEG quality
/// [CropPhotoRules.uploadJpegQuality] (Android re-encodes on the device; the
/// browser draws it on a canvas), so the upload stays small on 3G and a 2 GB
/// phone never decodes a full 12 MP photo in Dart. The server makes the
/// thumb and the full image it keeps.
class CropPhotoPicker {
  final ImagePicker _picker;
  final String Function() _newId;

  CropPhotoPicker({ImagePicker? picker, String Function()? newId})
      : _picker = picker ?? ImagePicker(),
        _newId = newId ?? (() => const Uuid().v4());

  /// One photo from the camera.
  Future<CropPhotoPickResult> takePhoto() async {
    try {
      final file = await _picker.pickImage(
        source: ImageSource.camera,
        maxWidth: CropPhotoRules.uploadMaxEdge,
        maxHeight: CropPhotoRules.uploadMaxEdge,
        imageQuality: CropPhotoRules.uploadJpegQuality,
        requestFullMetadata: false,
      );
      return _toResult(file == null ? const [] : [file]);
    } on Object catch (e) {
      debugPrint('[CropPhotoPicker] camera failed: $e');
      return const CropPhotoPickResult([], CropPhotoPickIssue.failed);
    }
  }

  /// Up to [limit] photos from the gallery (at least 1).
  Future<CropPhotoPickResult> chooseFromGallery({required int limit}) async {
    if (limit < 1) return const CropPhotoPickResult([]);
    try {
      // pickMultiImage refuses a limit under 2, so one free slot is a single
      // pick.
      final files = limit == 1
          ? [
              await _picker.pickImage(
                source: ImageSource.gallery,
                maxWidth: CropPhotoRules.uploadMaxEdge,
                maxHeight: CropPhotoRules.uploadMaxEdge,
                imageQuality: CropPhotoRules.uploadJpegQuality,
                requestFullMetadata: false,
              ),
            ].whereType<XFile>().toList()
          : await _picker.pickMultiImage(
              maxWidth: CropPhotoRules.uploadMaxEdge,
              maxHeight: CropPhotoRules.uploadMaxEdge,
              imageQuality: CropPhotoRules.uploadJpegQuality,
              limit: limit,
              requestFullMetadata: false,
            );
      // Some pickers ignore the limit: keep the first ones.
      return _toResult(files.take(limit).toList());
    } on Object catch (e) {
      debugPrint('[CropPhotoPicker] gallery failed: $e');
      return const CropPhotoPickResult([], CropPhotoPickIssue.failed);
    }
  }

  Future<CropPhotoPickResult> _toResult(List<XFile> files) async {
    final photos = <CropPhotoDraft>[];
    CropPhotoPickIssue? issue;
    for (final file in files) {
      final Uint8List bytes;
      try {
        bytes = await file.readAsBytes();
      } on Object catch (e) {
        debugPrint('[CropPhotoPicker] could not read a photo: $e');
        issue ??= CropPhotoPickIssue.failed;
        continue;
      }
      final contentType = sniffImageContentType(bytes);
      if (contentType == null) {
        issue ??= CropPhotoPickIssue.unsupported;
        continue;
      }
      if (bytes.length > CropPhotoRules.maxUploadBytes) {
        issue ??= CropPhotoPickIssue.tooLarge;
        continue;
      }
      photos.add(
        CropPhotoDraft(
          photoId: _newId(),
          bytes: bytes,
          contentType: contentType,
        ),
      );
    }
    return CropPhotoPickResult(photos, issue);
  }
}
