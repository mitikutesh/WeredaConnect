import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';

/// Reads a photo file the phone kept, or null when it is gone or lies
/// outside the photos directory.
typedef PhotoFileReader = Future<Uint8List?> Function(String path);

/// Stores a readied operation's new payload (`SyncQueue.replaceData`).
typedef OperationDataWriter = Future<void> Function(
  String operationId,
  String data,
);

/// Uploads a handoff photo before its `delivery_photo` operation goes to
/// `POST /sync` (FRD-12, delivery-verification-api.md 1.25.0). Plugged into
/// the mobile `SyncExecutor` as part of its preflight.
///
/// The operation is queued with the photo file's path. Once the photo is in
/// the uploads bucket, its payload becomes `{"photoRef": key}`, in memory for
/// this batch and in the queue for any later run, so a photo goes up once.
///
/// Answers true when the operation can go: it is not a photo, it is readied
/// already, or the upload just worked. False keeps it queued (offline, a
/// server error, a failed PUT, a delivery whose own create is still on its
/// way) for the next sync run, without using a retry. A refusal that can
/// never change (not this user's photo, over the cap, the handoff has its
/// photo) and a file that is gone also answer true without a key: the
/// server then refuses the operation for good (`DELIVERY_PHOTO_INVALID`)
/// and the queue settles it, instead of the photo waiting forever.
class DeliveryPhotoSyncPreflight {
  final DeliveryPhotoUploader _uploader;
  final PhotoFileReader _readFile;
  final OperationDataWriter _saveData;

  DeliveryPhotoSyncPreflight({
    required OperationDataWriter saveData,
    DeliveryPhotoUploader? uploader,
    PhotoFileReader? readFile,
  })  : _saveData = saveData,
        _uploader = uploader ?? DeliveryPhotoUploader(),
        _readFile = readFile ?? readKeptPhoto;

  Future<bool> call(SyncOperation operation) async {
    if (operation.entityType != deliveryPhotoEntityType) return true;

    final Map<String, dynamic> data;
    try {
      final decoded = jsonDecode(operation.data);
      if (decoded is! Map<String, dynamic>) return true;
      data = decoded;
    } on FormatException {
      return true;
    }
    if (data[deliveryPhotoRefKey] is String) return true;

    final photo = parseDeliveryPhotoEntityId(operation.entityId);
    final path = data[deliveryPhotoLocalPathKey];
    if (photo == null || path is! String) return true;

    final bytes = await _readFile(path);
    // The phone compresses to WebP; the bytes decide, not the file name.
    final contentType = bytes == null ? null : sniffImageContentType(bytes);
    if (bytes == null ||
        (contentType != 'image/webp' && contentType != 'image/jpeg')) {
      debugPrint('[DeliveryPhotoSyncPreflight] no photo file to send');
      await _ready(operation, const {});
      return true;
    }

    final String photoRef;
    try {
      photoRef = await _uploader.upload(
        deliveryId: photo.deliveryId,
        type: photo.type,
        bytes: bytes,
        contentType: contentType!,
      );
    } on ApiException catch (e) {
      if (_refusedForGood(e)) {
        debugPrint('[DeliveryPhotoSyncPreflight] upload refused for good: $e');
        await _ready(operation, const {});
        return true;
      }
      return false;
    } on DioException catch (e) {
      debugPrint('[DeliveryPhotoSyncPreflight] photo PUT failed, will retry: '
          '${e.type}');
      return false;
    } on Object catch (e) {
      debugPrint('[DeliveryPhotoSyncPreflight] upload failed, will retry: $e');
      return false;
    }

    await _ready(operation, {deliveryPhotoRefKey: photoRef});
    return true;
  }

  /// Gives [operation] its final payload, for this batch and later runs.
  Future<void> _ready(SyncOperation operation, Map<String, String> data) async {
    final encoded = jsonEncode(data);
    operation.data = encoded;
    await _saveData(operation.operationId, encoded);
  }

  /// A presign refusal that the same photo can never pass: a 400 (over the
  /// cap), a 409 (the handoff has its photo) or a 403 naming this photo's
  /// rules. A 401 (an expired session), a 404 (the delivery's create may
  /// still be on its way) and a 5xx are worth another try.
  static bool _refusedForGood(ApiException e) =>
      e is ValidationException ||
      e is ConflictException ||
      (e is AuthException && e.statusCode == 403);
}

/// The bytes of a photo this phone kept under `<documents>/photos/`, or null
/// when the file is gone or [path] points anywhere else (after resolving any
/// `..`).
Future<Uint8List?> readKeptPhoto(String path) async {
  if (path.isEmpty) return null;
  try {
    final documents = await getApplicationDocumentsDirectory();
    final root = Directory('${documents.path}/photos')
        .absolute
        .uri
        .normalizePath()
        .toFilePath();
    final prefix = root.endsWith(Platform.pathSeparator)
        ? root
        : '$root${Platform.pathSeparator}';
    final canonical = File(path).absolute.uri.normalizePath().toFilePath();
    if (!canonical.startsWith(prefix)) return null;
    final file = File(canonical);
    if (!file.existsSync()) return null;
    return await file.readAsBytes();
  } on FileSystemException catch (e) {
    debugPrint('[DeliveryPhotoSyncPreflight] read failed: $e');
    return null;
  }
}
