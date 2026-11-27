import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_file_store.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_uploader.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// Uploads a listing's photos before its create goes to `POST /sync`
/// (crop-listing-api.md 1.6.0, rule 9): the server refuses a create without
/// `photoIds`, and a buyer should never see a listing whose photos have not
/// left the farmer's phone. Plugged into the mobile `SyncExecutor` as its
/// preflight.
///
/// Answers true when the operation can go: it is not a crop create, or every
/// photo this device still has is uploaded. False keeps it queued (offline,
/// a server error, a failed PUT) for the next sync run, without using up a
/// retry. A refusal that can never change (`CROP_VALIDATION`,
/// `CROP_FORBIDDEN`) and photo files that are gone also answer true: the
/// create then gets the server's own answer instead of waiting forever.
class CropPhotoSyncPreflight {
  final Isar _isar;
  final CropPhotoUploader _uploader;
  final CropPhotoFileStore _files;

  CropPhotoSyncPreflight({
    required Isar isar,
    CropPhotoUploader? uploader,
    CropPhotoFileStore? files,
  })  : _isar = isar,
        _uploader = uploader ?? CropPhotoUploader(),
        _files = files ?? CropPhotoFileStore();

  Future<bool> call(SyncOperation operation) async {
    if (operation.entityType != 'crop_listing' ||
        operation.action != 'create') {
      return true;
    }
    final listing = await _isar.cropListings
        .filter()
        .listingIdEqualTo(operation.entityId)
        .findFirst();
    if (listing == null) return true;

    final waiting = listing.photos
        .where((photo) => !photo.uploaded && photo.localPath != null)
        .toList();
    if (waiting.isEmpty) return true;

    final drafts = <CropPhotoDraft>[];
    for (final photo in waiting) {
      final bytes = await _files.read(photo.localPath!);
      if (bytes == null) {
        debugPrint('[CropPhotoSyncPreflight] a photo file is gone; sending '
            'the listing without it');
        continue;
      }
      drafts.add(
        CropPhotoDraft(
          photoId: photo.photoId,
          bytes: bytes,
          contentType: photo.contentType,
        ),
      );
    }
    if (drafts.isEmpty) return true;

    final Set<String> uploaded;
    try {
      uploaded = await _uploader.upload(
        listingId: listing.listingId,
        photos: drafts,
      );
    } on ApiException catch (e) {
      if (e is ValidationException || e.code == 'CROP_FORBIDDEN') {
        debugPrint('[CropPhotoSyncPreflight] upload refused for good: $e');
        return true;
      }
      return false;
    } on Object catch (e) {
      debugPrint('[CropPhotoSyncPreflight] upload failed, will retry: $e');
      return false;
    }

    if (uploaded.isNotEmpty) {
      await _isar.writeTxn(() async {
        // Re-read inside the write: the listing may have changed meanwhile.
        final fresh = await _isar.cropListings
            .filter()
            .listingIdEqualTo(listing.listingId)
            .findFirst();
        if (fresh == null) return;
        fresh.photos = [
          for (final photo in fresh.photos)
            if (uploaded.contains(photo.photoId))
              (photo..uploaded = true)
            else
              photo,
        ];
        await _isar.cropListings.put(fresh);
      });
    }
    return drafts.every((draft) => uploaded.contains(draft.photoId));
  }
}
