import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';

/// Sends one handoff photo to the uploads bucket (FRD-12,
/// delivery-verification-api.md section 4): `POST /deliveries/{id}/photos`
/// returns a presigned PUT with the size and type signed into it, then the
/// photo goes straight to S3, so its bytes never pass through a Lambda. The
/// key it returns is attached afterwards by a `delivery_photo` sync
/// operation.
///
/// The S3 request uses its own plain [Dio]: the API client's auth header
/// would clash with the presigned signature, and its certificate pins are
/// for the API host only.
class DeliveryPhotoUploader {
  final ApiClient _api;
  final Dio _storage;

  DeliveryPhotoUploader({ApiClient? api, Dio? storage})
      : _api = api ?? ApiClient(),
        _storage = storage ??
            Dio(
              BaseOptions(
                // A 200 KB photo on a slow 3G uplink.
                connectTimeout: const Duration(seconds: 30),
                sendTimeout: const Duration(seconds: 90),
                receiveTimeout: const Duration(seconds: 30),
              ),
            );

  /// Uploads [bytes] as the [type] (`pickup` or `dropoff`) photo of
  /// [deliveryId] and returns its upload key. [contentType] is `image/webp`
  /// (the phone) or `image/jpeg` (the browser).
  ///
  /// Throws a [ValidationException] for a photo over the 200 KB cap (the
  /// server would refuse it too) or an answer without a key of this
  /// delivery; the server's refusals and network failures come through as
  /// the [ApiException] or [DioException] they are.
  Future<String> upload({
    required String deliveryId,
    required String type,
    required Uint8List bytes,
    required String contentType,
  }) async {
    if (bytes.isEmpty || bytes.length > DeliveryConstants.maxPhotoSizeBytes) {
      throw const ValidationException(
        'A delivery photo is at most 200 KB.',
        400,
        'DELIVERY_PHOTO_TOO_LARGE',
      );
    }

    final response = await _api.post<Map<String, dynamic>>(
      '/deliveries/$deliveryId/photos',
      data: {
        'type': type,
        'sizeBytes': bytes.length,
        'contentType': contentType,
      },
    );
    final uploadUrl = response['uploadUrl'];
    final photoRef = response['photoRef'];
    if (uploadUrl is! String ||
        uploadUrl.isEmpty ||
        photoRef is! String ||
        !DeliveryConstants.isServerPhotoRef(photoRef, deliveryId)) {
      throw const ValidationException(
        'The server did not return an upload URL for this photo.',
      );
    }

    await _storage.put<void>(
      uploadUrl,
      data: Stream<List<int>>.value(bytes),
      options: Options(
        // Both headers are signed into the URL: anything else is a 403. A
        // browser sets Content-Length itself and refuses a script that
        // tries to.
        headers: {
          Headers.contentTypeHeader: contentType,
          if (!kIsWeb) Headers.contentLengthHeader: bytes.length,
        },
        contentType: contentType,
      ),
    );
    return photoRef;
  }
}
