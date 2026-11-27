import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// Keeps the photos a farmer took for a listing in the app-private documents
/// directory (mobile only), so an offline listing shows them and the sync
/// executor can upload them later. Layout:
/// `<documents>/crop_photos/<listingId>/<photoId>.<ext>`.
///
/// Every id is checked to be a UUID before it becomes part of a path, so a
/// crafted id cannot point outside the photos directory.
class CropPhotoFileStore {
  static final _uuid = RegExp(
    r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$',
  );

  final Future<Directory> Function() _documents;

  CropPhotoFileStore({Future<Directory> Function()? documentsDirectory})
      : _documents = documentsDirectory ?? getApplicationDocumentsDirectory;

  /// Writes [photo] for listing [listingId] and returns the file's path.
  Future<String> save(String listingId, CropPhotoDraft photo) async {
    if (!_uuid.hasMatch(listingId) || !_uuid.hasMatch(photo.photoId)) {
      throw ArgumentError('Listing and photo ids must be UUIDs.');
    }
    final dir = await _listingDir(listingId);
    await dir.create(recursive: true);
    final file = File('${dir.path}/${photo.photoId}.${_extension(photo)}');
    await file.writeAsBytes(photo.bytes, flush: true);
    return file.path;
  }

  /// The bytes at [path], or null when the file is gone or outside the photos
  /// directory.
  Future<Uint8List?> read(String path) async {
    final file = await _contained(path);
    if (file == null || !file.existsSync()) return null;
    try {
      return await file.readAsBytes();
    } on FileSystemException catch (e) {
      debugPrint('[CropPhotoFileStore] read failed: $e');
      return null;
    }
  }

  /// Deletes every photo kept for [listingId].
  Future<void> deleteListing(String listingId) async {
    if (!_uuid.hasMatch(listingId)) return;
    final dir = await _listingDir(listingId);
    if (dir.existsSync()) await dir.delete(recursive: true);
  }

  Future<Directory> _root() async =>
      Directory('${(await _documents()).path}/crop_photos');

  Future<Directory> _listingDir(String listingId) async =>
      Directory('${(await _root()).path}/$listingId');

  /// [path] as a file when it lies inside the photos directory (after
  /// resolving any `..`), else null.
  Future<File?> _contained(String path) async {
    if (path.isEmpty) return null;
    final root = (await _root()).absolute.uri.normalizePath().toFilePath();
    final prefix = root.endsWith(Platform.pathSeparator)
        ? root
        : '$root${Platform.pathSeparator}';
    final canonical = File(path).absolute.uri.normalizePath().toFilePath();
    return canonical.startsWith(prefix) ? File(canonical) : null;
  }

  static String _extension(CropPhotoDraft photo) => switch (photo.contentType) {
        'image/png' => 'png',
        'image/webp' => 'webp',
        _ => 'jpg',
      };
}
