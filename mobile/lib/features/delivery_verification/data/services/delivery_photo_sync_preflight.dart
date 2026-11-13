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

/// Whether an operation of the delivery itself (its create, a handoff) still
/// waits in the queue (`SyncQueue.hasOpenOperationsFor`).
typedef DeliveryOpsOpen = Future<bool> Function(String deliveryId);

/// Called once the server attached the photo [photoRef] (the photo's file
/// [localPath] is no longer needed on the phone).
typedef DeliveryPhotoAttached = Future<void> Function({
  required String deliveryId,
  required String type,
  required String photoRef,
  required String? localPath,
});

/// Uploads a handoff photo before its `delivery_photo` operation goes to
/// `POST /sync` (FRD-12, delivery-verification-api.md 1.25.0). Plugged into
/// the mobile `SyncExecutor` as part of its preflight.
///
/// The operation is queued with the photo file's path (a `_` key, never sent).
/// Once the photo is in the uploads bucket, its payload becomes
/// `{"photoRef": key}`, in memory for this batch and in the queue for any
/// later run, so a photo goes up once.
///
/// Answers true when the operation can go: it is not a photo, it is readied
/// already, or the upload just worked. False keeps it queued (offline, a
/// server error, a failed PUT, a delivery whose own create is still on its
/// way) for the next sync run, without using a retry. Every wait is bounded:
/// a refusal that can never change (not this user's photo, over the cap, the
/// handoff has its photo, the delivery was cancelled), a file that is gone, a
/// delivery the server does not know once none of its own operations waits
/// on the phone ([notFoundGrace] after the photo was queued), and the
/// [maxPutRefusals]th refused PUT (an upload S3 timed out is not a refusal,
/// FUP-140) all answer true without a key: the server
/// then refuses the operation for good (`DELIVERY_PHOTO_INVALID`) and the
/// queue settles it, instead of the photo waiting forever.
class DeliveryPhotoSyncPreflight {
  /// How long after it was queued a photo still waits for a delivery the
  /// server does not know, once no operation of that delivery waits on the
  /// phone (a slow index read just after the delivery's create).
  static const Duration notFoundGrace = Duration(minutes: 10);

  /// PUTs that S3 refused (a 4xx) before the photo is given up: a fresh
  /// presigned URL is refused only for a reason the next one shares, so a
  /// 200 KB upload is not repeated on every sync.
  static const int maxPutRefusals = 3;

  /// Payload key counting the refused PUTs (a `_` key, never sent).
  static const String putRefusalsKey = '_putRefusals';

  final DeliveryPhotoUploader _uploader;
  final PhotoFileReader _readFile;
  final OperationDataWriter _saveData;
  final DeliveryOpsOpen _deliveryOpsOpen;
  final DeliveryPhotoAttached? _onAttached;
  final DateTime Function() _now;

  DeliveryPhotoSyncPreflight({
    required OperationDataWriter saveData,
    required DeliveryOpsOpen deliveryOpsOpen,
    DeliveryPhotoAttached? onAttached,
    DeliveryPhotoUploader? uploader,
    PhotoFileReader? readFile,
    DateTime Function()? now,
  })  : _saveData = saveData,
        _deliveryOpsOpen = deliveryOpsOpen,
        _onAttached = onAttached,
        _uploader = uploader ?? DeliveryPhotoUploader(),
        _readFile = readFile ?? readKeptPhoto,
        _now = now ?? DateTime.now;

  Future<bool> call(SyncOperation operation) async {
    if (operation.entityType != deliveryPhotoEntityType) return true;

    final data = _decode(operation.data);
    if (data == null || data[deliveryPhotoRefKey] is String) return true;

    final photo = parseDeliveryPhotoEntityId(operation.entityId);
    final path = data[deliveryPhotoLocalPathKey];
    if (photo == null || path is! String) return true;

    final bytes = await _readFile(path);
    // The phone compresses to WebP; the bytes decide, not the file name.
    final contentType = bytes == null ? null : sniffImageContentType(bytes);
    if (bytes == null ||
        (contentType != 'image/webp' && contentType != 'image/jpeg')) {
      debugPrint('[DeliveryPhotoSyncPreflight] no photo file to send');
      await _giveUp(operation, path);
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
    } on NotFoundException {
      return _waitForDelivery(operation, photo.deliveryId, path);
    } on ApiException catch (e) {
      if (_refusedForGood(e)) {
        debugPrint('[DeliveryPhotoSyncPreflight] upload refused for good: $e');
        await _giveUp(operation, path);
        return true;
      }
      return false;
    } on DioException catch (e) {
      return _putFailed(operation, data, path, e);
    } on Object catch (e) {
      debugPrint('[DeliveryPhotoSyncPreflight] upload failed, will retry: $e');
      return false;
    }

    await _save(operation, {
      deliveryPhotoRefKey: photoRef,
      deliveryPhotoLocalPathKey: path,
    });
    return true;
  }

  /// After the server attached a photo (a `success` or `duplicate` answer):
  /// the phone's file is no longer needed.
  Future<void> onSynced(SyncOperation operation) async {
    if (operation.entityType != deliveryPhotoEntityType) return;
    final data = _decode(operation.data);
    final photoRef = data?[deliveryPhotoRefKey];
    final photo = parseDeliveryPhotoEntityId(operation.entityId);
    final onAttached = _onAttached;
    if (photoRef is! String || photo == null || onAttached == null) return;
    final path = data?[deliveryPhotoLocalPathKey];
    try {
      await onAttached(
        deliveryId: photo.deliveryId,
        type: photo.type,
        photoRef: photoRef,
        localPath: path is String ? path : null,
      );
    } on Object catch (e) {
      debugPrint('[DeliveryPhotoSyncPreflight] attach clean-up failed: $e');
    }
  }

  /// The server does not know the delivery. Wait while one of its own
  /// operations (its create) still waits on the phone, or for
  /// [notFoundGrace] after the photo was queued; then give the photo up.
  Future<bool> _waitForDelivery(
    SyncOperation operation,
    String deliveryId,
    String path,
  ) async {
    if (await _deliveryOpsOpen(deliveryId)) return false;
    if (_now().difference(operation.createdAt) < notFoundGrace) return false;
    debugPrint('[DeliveryPhotoSyncPreflight] the delivery is unknown: '
        'the photo is given up');
    await _giveUp(operation, path);
    return true;
  }

  /// A PUT that did not reach S3, or got a 5xx, waits for the next run. A
  /// refusal (4xx) is counted, and the [maxPutRefusals]th gives the photo up.
  /// FUP-140: S3's `RequestTimeout` on a stalled upload is a 400 but no
  /// refusal ([DeliveryPhotoUploader.isStalledUpload]): it waits, uncounted.
  Future<bool> _putFailed(
    SyncOperation operation,
    Map<String, dynamic> data,
    String path,
    DioException e,
  ) async {
    final status = e.response?.statusCode;
    if (status == null ||
        status < 400 ||
        status >= 500 ||
        DeliveryPhotoUploader.isStalledUpload(e)) {
      debugPrint('[DeliveryPhotoSyncPreflight] photo PUT failed, will retry: '
          '${e.type}');
      return false;
    }
    final refusals = ((data[putRefusalsKey] as num?)?.toInt() ?? 0) + 1;
    if (refusals >= maxPutRefusals) {
      debugPrint('[DeliveryPhotoSyncPreflight] photo PUT refused $refusals '
          'times ($status): the photo is given up');
      await _giveUp(operation, path);
      return true;
    }
    await _save(operation, {...data, putRefusalsKey: refusals});
    return false;
  }

  /// Sends the operation without a key, so the server refuses it for good.
  Future<void> _giveUp(SyncOperation operation, String path) =>
      _save(operation, {deliveryPhotoLocalPathKey: path});

  /// Gives [operation] a new payload, for this batch and later runs.
  Future<void> _save(SyncOperation operation, Map<String, Object?> data) async {
    final encoded = jsonEncode(data);
    operation.data = encoded;
    await _saveData(operation.operationId, encoded);
  }

  static Map<String, dynamic>? _decode(String data) {
    try {
      final decoded = jsonDecode(data);
      return decoded is Map<String, dynamic> ? decoded : null;
    } on FormatException {
      return null;
    }
  }

  /// A presign refusal that the same photo can never pass: a 400 (over the
  /// cap), a 409 (the handoff has its photo, the delivery was cancelled) or
  /// a 403 naming this photo's rules. A 401 (an expired session) and a 5xx
  /// are worth another try; a 404 is decided by [_waitForDelivery].
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
