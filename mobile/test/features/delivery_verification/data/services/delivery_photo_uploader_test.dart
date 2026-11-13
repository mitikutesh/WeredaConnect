import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_uploader.dart';

class _MockApiClient extends Mock implements ApiClient {}

const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _key = 'uploads/photos/$_deliveryId/pickup-1700000000000.webp';
const _uploadUrl = 'https://uploads.s3.eu-north-1.amazonaws.com/put';

/// FRD-12: one handoff photo goes up through `POST /deliveries/{id}/photos`
/// and its presigned PUT; the 200 KB cap is checked before anything is sent.
void main() {
  late _MockApiClient api;
  late Dio storage;
  late DioAdapter s3;
  late DeliveryPhotoUploader uploader;
  late List<RequestOptions> puts;

  setUp(() {
    api = _MockApiClient();
    storage = Dio();
    s3 = DioAdapter(dio: storage);
    puts = [];
    s3.onPut(
      _uploadUrl,
      (server) => server.replyCallback(200, (options) {
        puts.add(options);
        return '';
      }),
      data: Matchers.any,
    );
    uploader = DeliveryPhotoUploader(api: api, storage: storage);
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer(
      (_) async => {'uploadUrl': _uploadUrl, 'photoRef': _key},
    );
  });

  test('asks for a URL with the exact size and type, then PUTs the photo',
      () async {
    final bytes = Uint8List(150 * 1024);

    final key = await uploader.upload(
      deliveryId: _deliveryId,
      type: 'pickup',
      bytes: bytes,
      contentType: 'image/webp',
    );

    expect(key, _key);
    final body = verify(
      () => api.post<Map<String, dynamic>>(
        '/deliveries/$_deliveryId/photos',
        data: captureAny<dynamic>(named: 'data'),
      ),
    ).captured.single as Map<String, dynamic>;
    expect(body, {
      'type': 'pickup',
      'sizeBytes': 150 * 1024,
      'contentType': 'image/webp',
    });
    expect(puts, hasLength(1));
    expect(puts.single.headers[Headers.contentTypeHeader], 'image/webp');
  });

  test('a photo of exactly 200 KB goes up', () async {
    await uploader.upload(
      deliveryId: _deliveryId,
      type: 'pickup',
      bytes: Uint8List(200 * 1024),
      contentType: 'image/webp',
    );

    expect(puts, hasLength(1));
  });

  test('a photo over the 200 KB cap is refused before any request', () async {
    await expectLater(
      uploader.upload(
        deliveryId: _deliveryId,
        type: 'pickup',
        bytes: Uint8List(200 * 1024 + 1),
        contentType: 'image/webp',
      ),
      throwsA(
        isA<ValidationException>()
            .having((e) => e.code, 'code', 'DELIVERY_PHOTO_TOO_LARGE'),
      ),
    );
    verifyNever(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    );
    expect(puts, isEmpty);
  });

  test('an answer without a key of this delivery is refused', () async {
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer(
      (_) async => {
        'uploadUrl': _uploadUrl,
        'photoRef': 'uploads/photos/other-delivery/pickup-1.webp',
      },
    );

    await expectLater(
      uploader.upload(
        deliveryId: _deliveryId,
        type: 'pickup',
        bytes: Uint8List(10),
        contentType: 'image/webp',
      ),
      throwsA(isA<ValidationException>()),
    );
    expect(puts, isEmpty);
  });

  test("the server's refusal comes through", () async {
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(
      const AuthException(
        'Only the trucker takes the pickup photo.',
        403,
        'DELIVERY_UNAUTHORIZED',
      ),
    );

    await expectLater(
      uploader.upload(
        deliveryId: _deliveryId,
        type: 'pickup',
        bytes: Uint8List(10),
        contentType: 'image/webp',
      ),
      throwsA(isA<AuthException>()),
    );
    expect(puts, isEmpty);
  });

  group('FUP-140: a stalled upload is no refusal', () {
    const timeoutBody = '<?xml version="1.0" encoding="UTF-8"?>'
        '<Error><Code>RequestTimeout</Code><Message>Your socket connection '
        'to the server was not read from or written to within the timeout '
        'period. Idle connections will be closed.</Message></Error>';

    Future<DioException> putAnswered(int status, String body) async {
      s3.onPut(
        _uploadUrl,
        (server) => server.reply(
          status,
          body,
          headers: {
            Headers.contentTypeHeader: ['application/xml'],
          },
        ),
        data: Matchers.any,
      );
      try {
        await uploader.upload(
          deliveryId: _deliveryId,
          type: 'pickup',
          bytes: Uint8List(10),
          contentType: 'image/webp',
        );
      } on DioException catch (e) {
        return e;
      }
      fail('the PUT should have failed');
    }

    test("S3's RequestTimeout (a 400) is told apart from a refusal", () async {
      final e = await putAnswered(400, timeoutBody);

      expect(e.response?.statusCode, 400);
      expect(DeliveryPhotoUploader.isStalledUpload(e), isTrue);
    });

    test('a refusal of the photo stays a refusal', () async {
      const mismatch = '<Error><Code>SignatureDoesNotMatch</Code></Error>';
      expect(
        DeliveryPhotoUploader.isStalledUpload(await putAnswered(403, mismatch)),
        isFalse,
      );
      const badDigest = '<Error><Code>BadDigest</Code></Error>';
      expect(
        DeliveryPhotoUploader.isStalledUpload(
          await putAnswered(400, badDigest),
        ),
        isFalse,
      );
    });

    DioException answered(int? status, Object? data) => DioException(
          requestOptions: RequestOptions(path: '/put'),
          type: status == null
              ? DioExceptionType.connectionTimeout
              : DioExceptionType.badResponse,
          response: status == null
              ? null
              : Response<Object?>(
                  requestOptions: RequestOptions(path: '/put'),
                  statusCode: status,
                  data: data,
                ),
        );

    test('a 408, or the timeout read from raw bytes, is a stalled upload', () {
      expect(
        DeliveryPhotoUploader.isStalledUpload(answered(408, null)),
        isTrue,
      );
      expect(
        DeliveryPhotoUploader.isStalledUpload(
          answered(400, Uint8List.fromList(timeoutBody.codeUnits)),
        ),
        isTrue,
      );
    });

    test('no answer at all, or a 400 without a body, is not', () {
      expect(
        DeliveryPhotoUploader.isStalledUpload(answered(null, null)),
        isFalse,
      );
      expect(
        DeliveryPhotoUploader.isStalledUpload(answered(400, null)),
        isFalse,
      );
    });
  });
}
