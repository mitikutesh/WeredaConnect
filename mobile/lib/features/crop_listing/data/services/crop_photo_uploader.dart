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
                connectTimeout: const Duration(seconds: 30),
                // Each PUT sets its own send and receive timeout
                // ([uploadTimeoutFor]).
                sendTimeout: maxUploadTimeout,
                receiveTimeout: maxUploadTimeout,
              ),
            );

  /// The shortest time a photo gets to go up, whatever its size: the
  /// request headers and S3's answer.
  static const Duration minUploadTimeout = Duration(seconds: 30);

  /// The longest one photo may hold the sync, so a dead link cannot keep the
  /// other waiting changes back for long.
  static const Duration maxUploadTimeout = Duration(minutes: 3);

  /// The slowest uplink a photo is still given time for: 2 KB per second
  /// (16 kbit/s, about a 2G GSM data link).
  static const int slowestUplinkBytesPerSecond = 2000;

  /// BUG-33: how long one photo of [sizeBytes] may take to go up, used for
  /// both the send and the receive timeout of its PUT: [minUploadTimeout]
  /// plus the time [sizeBytes] need at [slowestUplinkBytesPerSecond], at
  /// most [maxUploadTimeout]. A 95 KB photo gets 78 seconds, a 44 KB one 52
  /// seconds.
  ///
  /// Why the receive timeout has to cover the upload: Dio's send timeout
  /// only runs while the body is handed to the socket, and a photo this
  /// small fits in the phone's socket buffer, so that hand-over is over at
  /// once. The bytes then leave at the speed of the uplink while Dio already
  /// waits for the response, which the receive timeout bounds. With the old
  /// fixed 30 seconds a photo had to go up at 25 kbit/s or faster, and on a
  /// 2G link it never did; the 120 second send timeout never applied. A
  /// larger photo that does not fit the buffer is bounded by the send
  /// timeout instead, so both get the same budget.
  ///
  /// A presigned S3 PUT cannot resume, so a photo that runs out of time is
  /// sent again whole on the next sync. A photo that went up is not sent
  /// again (the sync preflight keeps its state).
  static Duration uploadTimeoutFor(int sizeBytes) {
    final budget = minUploadTimeout +
        Duration(seconds: (sizeBytes / slowestUplinkBytesPerSecond).ceil());
    return budget > maxUploadTimeout ? maxUploadTimeout : budget;
  }

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
      final timeout = uploadTimeoutFor(photo.sizeBytes);
      try {
        await _storage.put<void>(
          url,
          data: Stream<List<int>>.value(photo.bytes),
          options: Options(
            sendTimeout: timeout,
            receiveTimeout: timeout,
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
