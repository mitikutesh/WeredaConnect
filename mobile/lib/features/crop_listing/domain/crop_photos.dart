import 'dart:typed_data';

import 'package:wereda_connect/core/models/crop_listing.dart';

/// Photo rules of a crop listing (crop-listing-api.md 1.6.0).
///
/// The server's photo processor keeps only two renditions of every upload: a
/// 320 px thumb for the marketplace list and a 1024 px image for the detail
/// (both WebP, metadata stripped). The device only shrinks the camera's photo
/// before the upload so a farmer on 3G does not pay for a 5 MB original.
abstract final class CropPhotoRules {
  /// Fewest photos a listing can be saved with.
  static const int minPhotos = 2;

  /// Most photos a listing can carry.
  static const int maxPhotos = 4;

  /// Longest edge, in pixels, the device shrinks a photo to before upload
  /// (native resize in image_picker). The server keeps at most 1024 px, so
  /// this only saves the farmer's data: a 12 MP camera JPEG of 4 to 6 MB
  /// goes up as roughly 250 to 600 KB.
  static const double uploadMaxEdge = 1600;

  /// JPEG quality of that pre-upload resize.
  static const int uploadJpegQuality = 85;

  /// The server's cap on one upload (10 MiB).
  static const int maxUploadBytes = 10 * 1024 * 1024;

  /// The image types the server accepts.
  static const Set<String> contentTypes = {
    'image/jpeg',
    'image/png',
    'image/webp',
  };

  /// Whether [count] photos are enough to save a listing.
  static bool isValidCount(int count) =>
      count >= minPhotos && count <= maxPhotos;
}

/// A photo the farmer picked for a listing that is not saved yet.
class CropPhotoDraft {
  /// UUID made on the device; becomes the photo's id on the server.
  final String photoId;

  /// The (pre-shrunk) image as it will be uploaded.
  final Uint8List bytes;

  /// MIME type read from [bytes] (see [sniffImageContentType]).
  final String contentType;

  const CropPhotoDraft({
    required this.photoId,
    required this.bytes,
    required this.contentType,
  });

  int get sizeBytes => bytes.length;
}

/// The MIME type of an image from its first bytes: `image/jpeg`, `image/png`
/// or `image/webp`, else null (HEIC, GIF, anything that is not an image).
/// The file name or the picker's type is not trusted: only the bytes go up.
String? sniffImageContentType(Uint8List bytes) {
  if (bytes.length >= 3 &&
      bytes[0] == 0xFF &&
      bytes[1] == 0xD8 &&
      bytes[2] == 0xFF) {
    return 'image/jpeg';
  }
  if (bytes.length >= 8 &&
      bytes[0] == 0x89 &&
      bytes[1] == 0x50 &&
      bytes[2] == 0x4E &&
      bytes[3] == 0x47 &&
      bytes[4] == 0x0D &&
      bytes[5] == 0x0A &&
      bytes[6] == 0x1A &&
      bytes[7] == 0x0A) {
    return 'image/png';
  }
  if (bytes.length >= 12 &&
      bytes[0] == 0x52 && // R
      bytes[1] == 0x49 && // I
      bytes[2] == 0x46 && // F
      bytes[3] == 0x46 && // F
      bytes[8] == 0x57 && // W
      bytes[9] == 0x45 && // E
      bytes[10] == 0x42 && // B
      bytes[11] == 0x50) {
    // P
    return 'image/webp';
  }
  return null;
}

/// The `photos` array of a crop read (`GET /crops` gives `photoId` and
/// `thumbUrl`, `GET /crops/{id}` adds `fullUrl`). Entries without an id are
/// dropped; anything that is not a list reads as no photos.
List<CropPhoto> parseCropPhotos(Object? json) {
  if (json is! List) return [];
  final photos = <CropPhoto>[];
  for (final entry in json) {
    if (entry is! Map) continue;
    final photoId = entry['photoId'];
    if (photoId is! String || photoId.isEmpty) continue;
    final thumbUrl = entry['thumbUrl'];
    final fullUrl = entry['fullUrl'];
    photos.add(
      CropPhoto()
        ..photoId = photoId
        ..thumbUrl = thumbUrl is String && thumbUrl.isNotEmpty ? thumbUrl : null
        ..fullUrl = fullUrl is String && fullUrl.isNotEmpty ? fullUrl : null
        ..uploaded = true,
    );
  }
  return photos;
}

/// [fromServer] with this device's own copies carried over by `photoId`: the
/// photo file it took, its type and size, and its upload state. The server's
/// order wins. With no photos from the server (a listing it has not stored
/// yet, or one read before 1.6.0), [local] stays as it is, so a refresh never
/// drops the photos of the farmer's unsynced listing.
List<CropPhoto> mergeLocalPhotos(
  List<CropPhoto> fromServer,
  List<CropPhoto> local,
) {
  if (fromServer.isEmpty) return local;
  final byId = {for (final photo in local) photo.photoId: photo};
  for (final photo in fromServer) {
    final mine = byId[photo.photoId];
    if (mine == null) continue;
    photo
      ..localPath = mine.localPath
      ..contentType = mine.contentType
      ..sizeBytes = mine.sizeBytes;
  }
  return fromServer;
}

/// The image cache key of a photo rendition. Presigned URLs change on every
/// read, so caching by URL would download the same photo again each time.
String cropPhotoCacheKey(String photoId, {required bool full}) =>
    'crop-photo:$photoId:${full ? 'full' : 'thumb'}';
