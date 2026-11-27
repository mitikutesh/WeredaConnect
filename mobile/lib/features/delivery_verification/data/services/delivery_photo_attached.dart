import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';

/// Deletes the photo files of one handoff on this phone.
typedef HandoffPhotoFilesDeleter = Future<void> Function(
  String deliveryId,
  PhotoType type,
);

/// FRD-12: once the server attached a handoff photo, the phone needs its
/// file no more. The delivery row points at the server's key (the evidence
/// tile then shows the server's photo, cached by its key), and every photo
/// file of that handoff on the phone is deleted, the retakes included.
/// Wired as the `DeliveryPhotoSyncPreflight` attach callback (mobile only).
class DeliveryPhotoAttachedCleanup {
  final Isar _isar;
  final HandoffPhotoFilesDeleter _deleteFiles;

  DeliveryPhotoAttachedCleanup({
    required Isar isar,
    HandoffPhotoFilesDeleter? deleteFiles,
  })  : _isar = isar,
        _deleteFiles = deleteFiles ?? PhotoManager().deleteLocalPhotos;

  Future<void> call({
    required String deliveryId,
    required String type,
    required String photoRef,
    required String? localPath,
  }) async {
    final photoType = switch (type) {
      'pickup' => PhotoType.pickup,
      // ESC-36 (FUP-336): the trucker's arrival photo at the drop-off.
      arrivalPhotoType => PhotoType.arrival,
      _ => PhotoType.dropoff,
    };
    await _isar.writeTxn(() async {
      final delivery = await _isar.deliverys
          .filter()
          .deliveryIdEqualTo(deliveryId)
          .findFirst();
      if (delivery == null) return;
      // Only this phone's file path gives way: a key from the server stays.
      if (photoType == PhotoType.pickup &&
          _isLocalPath(delivery.pickupPhotoRef)) {
        delivery.pickupPhotoRef = photoRef;
      } else if (photoType == PhotoType.dropoff &&
          _isLocalPath(delivery.dropoffPhotoRef)) {
        delivery.dropoffPhotoRef = photoRef;
      } else if (photoType == PhotoType.arrival &&
          _isLocalPath(delivery.arrivalPhotoRef)) {
        delivery.arrivalPhotoRef = photoRef;
      } else {
        return;
      }
      await _isar.deliverys.put(delivery);
    });
    await _deleteFiles(deliveryId, photoType);
  }

  static bool _isLocalPath(String? ref) => ref != null && ref.startsWith('/');
}
