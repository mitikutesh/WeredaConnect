import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_file_store.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_picker.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_uploader.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// A valid 1x1 PNG, small enough for any test and decodable by Image.memory.
final Uint8List onePixelPng = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
);

/// [count] distinct photo drafts (2 by default: the fewest a listing needs,
/// crop-listing-api.md 1.6.0).
List<CropPhotoDraft> testCropPhotos([int count = 2]) => [
      for (var i = 0; i < count; i++)
        CropPhotoDraft(
          photoId: const Uuid().v4(),
          bytes: onePixelPng,
          contentType: 'image/png',
        ),
    ];

/// A photo file store under a fresh temp directory (no path_provider in
/// unit tests).
CropPhotoFileStore tempCropPhotoFileStore() {
  final dir = Directory.systemTemp.createTempSync('crop_photos_test');
  return CropPhotoFileStore(documentsDirectory: () async => dir);
}

/// An uploader that "uploads" every photo except [failIds] and records the
/// calls. Throws [error] instead when set.
class FakeCropPhotoUploader implements CropPhotoUploader {
  final Set<String> failIds;
  final Object? error;
  final calls = <({String listingId, List<CropPhotoDraft> photos})>[];

  FakeCropPhotoUploader({this.failIds = const {}, this.error});

  @override
  Future<Set<String>> upload({
    required String listingId,
    required List<CropPhotoDraft> photos,
  }) async {
    calls.add((listingId: listingId, photos: photos));
    if (error != null) throw error!;
    return {
      for (final photo in photos)
        if (!failIds.contains(photo.photoId)) photo.photoId,
    };
  }
}

/// Camera and gallery without a device: each take gives one new photo, a
/// gallery pick fills [galleryCount] (capped at the limit), [issue] rides on
/// every result.
class FakeCropPhotoPicker implements CropPhotoPicker {
  final int galleryCount;
  final CropPhotoPickIssue? issue;
  int takes = 0;
  final galleryLimits = <int>[];

  FakeCropPhotoPicker({this.galleryCount = 2, this.issue});

  @override
  Future<CropPhotoPickResult> takePhoto() async {
    takes++;
    return CropPhotoPickResult(testCropPhotos(1), issue);
  }

  @override
  Future<CropPhotoPickResult> chooseFromGallery({required int limit}) async {
    galleryLimits.add(limit);
    return CropPhotoPickResult(
      testCropPhotos(galleryCount < limit ? galleryCount : limit),
      issue,
    );
  }
}

/// Adds [count] photos on the create screen through its camera button.
Future<void> addTestPhotos(WidgetTester tester, [int count = 2]) async {
  final button = find.byKey(const Key('create_listing_photo_camera'));
  for (var i = 0; i < count; i++) {
    await tester.ensureVisible(button);
    await tester.pumpAndSettle();
    await tester.tap(button);
    await tester.pumpAndSettle();
  }
}
