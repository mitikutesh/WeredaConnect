import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// A photo upload failed. The listing must not be created without it: the
/// web form shows this, mobile keeps the create queued (see
/// `CropPhotoSyncPreflight`).
class CropPhotoUploadException implements Exception {
  /// Marker the create screen looks for in the error message.
  static const marker = 'CROP_PHOTO_UPLOAD_FAILED';

  final String detail;

  const CropPhotoUploadException(this.detail);

  @override
  String toString() => '$marker: $detail';
}

/// Sends a listing's photos to the private incoming bucket
/// (crop-listing-api.md 1.6.0, `POST /crops/{id}/photos`).
///
/// One API call asks for a presigned PUT URL per photo, then each photo goes
/// straight to S3, so the photo bytes never pass through a Lambda. The S3
/// requests use their own plain [Dio]: the API client's auth header would
/// clash with the presigned signature, and its certificate pins are for the
/// API host only.
class CropPhotoUploader {
  final ApiClient _api;
  final Dio _storage;

  CropPhotoUploader({ApiClient? api, Dio? storage})
      : _api = api ?? ApiClient(),
        _storage = storage ??
            Dio(
              BaseOptions(
                // A 600 KB photo on a slow 3G uplink.
                connectTimeout: const Duration(seconds: 30),
                sendTimeout: const Duration(seconds: 120),
                receiveTimeout: const Duration(seconds: 30),
              ),
            );

  /// Uploads [photos] for listing [listingId] and returns the ids that went
  /// up. Throws when the URLs cannot be had (offline, server refusal); a
  /// single failed PUT only leaves its id out, so the caller retries just
  /// that photo.
  Future<Set<String>> upload({
    required String listingId,
    required List<CropPhotoDraft> photos,
  }) async {
    if (photos.isEmpty) return {};
    final response = await _api.post<Map<String, dynamic>>(
      '/crops/$listingId/photos',
      data: {
        'photos': [
          for (final photo in photos)
            {
              'photoId': photo.photoId,
              'contentType': photo.contentType,
              'sizeBytes': photo.sizeBytes,
            },
        ],
      },
    );

    final urls = <String, String>{};
    final uploads = response['uploads'];
    if (uploads is List) {
      for (final entry in uploads) {
        if (entry is! Map) continue;
        final photoId = entry['photoId'];
        final url = entry['uploadUrl'];
        if (photoId is String && url is String && url.isNotEmpty) {
          urls[photoId] = url;
        }
      }
    }

    final done = <String>{};
    for (final photo in photos) {
      final url = urls[photo.photoId];
      if (url == null) continue;
      try {
        await _storage.put<void>(
          url,
          data: Stream<List<int>>.value(photo.bytes),
          options: Options(
            // Both headers are signed into the URL: anything else is a 403.
            // A browser sets Content-Length itself and refuses a script
            // that tries to.
            headers: {
              Headers.contentTypeHeader: photo.contentType,
              if (!kIsWeb) Headers.contentLengthHeader: photo.sizeBytes,
            },
            contentType: photo.contentType,
          ),
        );
        done.add(photo.photoId);
      } on Object catch (e) {
        debugPrint('[CropPhotoUploader] photo upload failed: $e');
      }
    }
    return done;
  }
}
