import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_uploader.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// Stands in for S3: records each PUT and answers 200, or fails the photo
/// whose URL is in [failUrls] with a receive timeout.
class _FakeStorage implements HttpClientAdapter {
  final List<RequestOptions> puts = [];
  final Set<String> failUrls;

  _FakeStorage({this.failUrls = const {}});

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    puts.add(options);
    await requestStream?.drain<void>();
    if (failUrls.contains(options.uri.toString())) {
      throw DioException.receiveTimeout(
        timeout: options.receiveTimeout!,
        requestOptions: options,
      );
    }
    return ResponseBody.fromString('', 200);
  }

  @override
  void close({bool force = false}) {}
}

CropPhotoDraft _photo(String id, int sizeBytes) => CropPhotoDraft(
      photoId: id,
      bytes: Uint8List(sizeBytes),
      contentType: 'image/jpeg',
    );

void main() {
  group('CropPhotoUploader.uploadTimeoutFor (BUG-33)', () {
    test('grows with the photo: 30 s plus its size at 2 KB per second', () {
      expect(
        CropPhotoUploader.uploadTimeoutFor(0),
        const Duration(seconds: 30),
      );
      // The two photos of the finding: both timed out at a fixed 30 s.
      expect(
        CropPhotoUploader.uploadTimeoutFor(44000),
        const Duration(seconds: 52),
      );
      expect(
        CropPhotoUploader.uploadTimeoutFor(95000),
        const Duration(seconds: 78),
      );
    });

    test('is capped, so a dead link cannot hold the sync for long', () {
      expect(
        CropPhotoUploader.uploadTimeoutFor(600 * 1024),
        const Duration(minutes: 3),
      );
      expect(
        CropPhotoUploader.uploadTimeoutFor(CropPhotoRules.maxUploadBytes),
        CropPhotoUploader.maxUploadTimeout,
      );
    });
  });

  group('CropPhotoUploader.upload', () {
    late _MockApiClient api;

    setUp(() {
      api = _MockApiClient();
      when(
        () => api.post<Map<String, dynamic>>(
          '/crops/listing-1/photos',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'uploads': [
            {'photoId': 'small', 'uploadUrl': 'https://s3.test/small'},
            {'photoId': 'large', 'uploadUrl': 'https://s3.test/large'},
          ],
        },
      );
    });

    test(
        'BUG-33: each PUT waits for its own photo, on the receive side too '
        '(a small body is sent at once and then waits for the response)',
        () async {
      final storage = _FakeStorage();
      final uploader = CropPhotoUploader(
        api: api,
        storage: Dio()..httpClientAdapter = storage,
      );

      final done = await uploader.upload(
        listingId: 'listing-1',
        photos: [_photo('small', 44000), _photo('large', 95000)],
      );

      expect(done, {'small', 'large'});
      expect(storage.puts.map((p) => p.method), ['PUT', 'PUT']);
      expect(storage.puts[0].receiveTimeout, const Duration(seconds: 52));
      expect(storage.puts[0].sendTimeout, const Duration(seconds: 52));
      expect(storage.puts[1].receiveTimeout, const Duration(seconds: 78));
      expect(storage.puts[1].sendTimeout, const Duration(seconds: 78));
    });

    test(
        'a photo that runs out of time is left out and the next one still '
        'goes up, so only that photo is sent again', () async {
      final storage = _FakeStorage(failUrls: {'https://s3.test/small'});
      final uploader = CropPhotoUploader(
        api: api,
        storage: Dio()..httpClientAdapter = storage,
      );

      final done = await uploader.upload(
        listingId: 'listing-1',
        photos: [_photo('small', 44000), _photo('large', 95000)],
      );

      expect(done, {'large'});
      expect(storage.puts, hasLength(2));
    });
  });
}
