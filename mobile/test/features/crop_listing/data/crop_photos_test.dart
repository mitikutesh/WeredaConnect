import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_file_store.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_sync_preflight.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_uploader.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

import '../../../helpers/crop_photo_fixtures.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// Answers every request 200 except [failUrls] (403), and records what was
/// sent.
class _RecordingAdapter implements HttpClientAdapter {
  final Set<String> failUrls;
  final requests = <({RequestOptions options, List<int> body})>[];

  _RecordingAdapter({this.failUrls = const {}});

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final body = <int>[];
    if (requestStream != null) {
      await for (final chunk in requestStream) {
        body.addAll(chunk);
      }
    }
    requests.add((options: options, body: body));
    return ResponseBody.fromString(
      '',
      failUrls.contains(options.uri.toString()) ? 403 : 200,
    );
  }

  @override
  void close({bool force = false}) {}
}

const _listingId = '0b8e5b9c-1d2e-4f3a-8b4c-5d6e7f8a9b0c';

void main() {
  setUpAll(() => registerFallbackValue(<String, dynamic>{}));

  group('crop photo helpers', () {
    test('sniffs JPEG, PNG and WebP from the bytes, nothing else', () {
      expect(
        sniffImageContentType(Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE0])),
        'image/jpeg',
      );
      expect(sniffImageContentType(onePixelPng), 'image/png');
      expect(
        sniffImageContentType(
          Uint8List.fromList(
            [...ascii.encode('RIFF'), 0, 0, 0, 0, ...ascii.encode('WEBP')],
          ),
        ),
        'image/webp',
      );
      expect(
        sniffImageContentType(Uint8List.fromList(ascii.encode('GIF89a'))),
        isNull,
      );
      expect(
        sniffImageContentType(
          Uint8List.fromList([0, 0, 0, 0x18, ...ascii.encode('ftypheic')]),
        ),
        isNull,
      );
      expect(sniffImageContentType(Uint8List(0)), isNull);
    });

    test('parses read photos and drops entries without an id', () {
      final photos = parseCropPhotos([
        {'photoId': 'a', 'thumbUrl': 'https://t/a', 'fullUrl': 'https://f/a'},
        {'photoId': 'b', 'thumbUrl': 'https://t/b'},
        {'thumbUrl': 'https://t/none'},
        'junk',
      ]);
      expect(photos.map((p) => p.photoId), ['a', 'b']);
      expect(photos[0].fullUrl, 'https://f/a');
      expect(photos[1].fullUrl, isNull);
      expect(photos.every((p) => p.uploaded), isTrue);
      expect(parseCropPhotos(null), isEmpty);
      expect(parseCropPhotos('nope'), isEmpty);
    });

    test('merge keeps the server order and this device\'s files', () {
      final local = [
        CropPhoto()
          ..photoId = 'a'
          ..localPath = '/files/a.jpg'
          ..sizeBytes = 10,
        CropPhoto()
          ..photoId = 'b'
          ..localPath = '/files/b.jpg',
      ];
      final merged = mergeLocalPhotos(
        parseCropPhotos([
          {'photoId': 'b', 'thumbUrl': 'https://t/b'},
          {'photoId': 'a', 'thumbUrl': 'https://t/a'},
        ]),
        local,
      );
      expect(merged.map((p) => p.photoId), ['b', 'a']);
      expect(merged.map((p) => p.localPath), ['/files/b.jpg', '/files/a.jpg']);
      expect(merged[1].thumbUrl, 'https://t/a');
      // Nothing from the server (not stored yet): the local photos stay.
      expect(mergeLocalPhotos([], local), same(local));
    });

    test('caches by photo id and rendition', () {
      expect(cropPhotoCacheKey('a', full: false), 'crop-photo:a:thumb');
      expect(cropPhotoCacheKey('a', full: true), 'crop-photo:a:full');
    });

    test('a listing needs 2 to 4 photos', () {
      expect(
        [0, 1, 2, 3, 4, 5].map(CropPhotoRules.isValidCount),
        [false, false, true, true, true, false],
      );
    });
  });

  group('CropPhotoFileStore', () {
    late Directory dir;
    late CropPhotoFileStore store;

    setUp(() {
      dir = Directory.systemTemp.createTempSync('crop_photo_store_');
      store = CropPhotoFileStore(documentsDirectory: () async => dir);
    });

    tearDown(() => dir.deleteSync(recursive: true));

    test('saves under the listing and reads it back', () async {
      final photo = testCropPhotos(1).single;
      final path = await store.save(_listingId, photo);

      expect(path, contains('/crop_photos/$_listingId/${photo.photoId}.png'));
      expect(await store.read(path), photo.bytes);
    });

    test('refuses ids that are not UUIDs', () async {
      final photo = CropPhotoDraft(
        photoId: '../escape',
        bytes: onePixelPng,
        contentType: 'image/png',
      );
      expect(() => store.save(_listingId, photo), throwsArgumentError);
    });

    test('never reads outside the photos directory', () async {
      final outside = File('${dir.path}/secret.txt')..writeAsStringSync('x');
      expect(await store.read(outside.path), isNull);
      expect(
        await store.read('${dir.path}/crop_photos/../secret.txt'),
        isNull,
      );
      expect(
        await store.read('${dir.path}/crop_photos/$_listingId/gone.jpg'),
        isNull,
      );
    });

    test('deletes a listing\'s photos', () async {
      final path = await store.save(_listingId, testCropPhotos(1).single);
      await store.deleteListing(_listingId);
      expect(File(path).existsSync(), isFalse);
    });
  });

  group('CropPhotoUploader', () {
    late _MockApiClient api;

    setUp(() => api = _MockApiClient());

    void answerUrls(List<CropPhotoDraft> photos) {
      when(
        () => api.post<Map<String, dynamic>>(
          '/crops/$_listingId/photos',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'uploads': [
            for (final p in photos)
              {
                'photoId': p.photoId,
                'uploadUrl': 'https://s3.test/${p.photoId}',
              },
          ],
          'expiresInSeconds': 900,
        },
      );
    }

    test('asks once, then PUTs each photo with its signed headers', () async {
      final photos = testCropPhotos();
      answerUrls(photos);
      final adapter = _RecordingAdapter();
      final uploader = CropPhotoUploader(
        api: api,
        storage: Dio()..httpClientAdapter = adapter,
      );

      final done = await uploader.upload(listingId: _listingId, photos: photos);

      expect(done, photos.map((p) => p.photoId).toSet());
      final body = verify(
        () => api.post<Map<String, dynamic>>(
          '/crops/$_listingId/photos',
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured.single as Map;
      expect((body['photos'] as List).first, {
        'photoId': photos.first.photoId,
        'contentType': 'image/png',
        'sizeBytes': onePixelPng.length,
      });
      expect(adapter.requests, hasLength(2));
      for (final request in adapter.requests) {
        expect(request.options.method, 'PUT');
        expect(request.options.headers['content-type'], 'image/png');
        expect(request.options.headers['content-length'], onePixelPng.length);
        expect(request.options.headers.containsKey('Authorization'), isFalse);
        expect(request.body, onePixelPng);
      }
    });

    test('a failed PUT leaves only that photo out', () async {
      final photos = testCropPhotos();
      answerUrls(photos);
      final uploader = CropPhotoUploader(
        api: api,
        storage: Dio()
          ..httpClientAdapter = _RecordingAdapter(
            failUrls: {'https://s3.test/${photos.last.photoId}'},
          ),
      );

      final done = await uploader.upload(listingId: _listingId, photos: photos);

      expect(done, {photos.first.photoId});
    });

    test('no upload URLs is an error for the caller', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          any(),
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(const NetworkException());
      final uploader = CropPhotoUploader(api: api, storage: Dio());

      expect(
        () => uploader.upload(listingId: _listingId, photos: testCropPhotos()),
        throwsA(isA<NetworkException>()),
      );
    });
  });

  group('Isar: repository and preflight', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue queue;
    late CropPhotoFileStore files;
    late CropListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('crop_photos_isar_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'crop_photos_${DateTime.now().microsecondsSinceEpoch}',
      );
      queue = SyncQueue(isar);
      files = CropPhotoFileStore(documentsDirectory: () async => tempDir);
      repository = CropListingRepository(
        isar: isar,
        syncQueue: queue,
        photoFiles: files,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    Future<CropListing> create(List<CropPhotoDraft> photos) =>
        repository.createListing(
          listingId: _listingId,
          farmerId: 'farmer-1',
          cropType: 'teff',
          quantity: 10,
          pricePerKg: 45,
          latitude: 9.0,
          longitude: 38.7,
          locationName: 'Ada',
          description: '',
          photos: photos,
        );

    SyncOperation createOp() => SyncOperation()
      ..entityType = 'crop_listing'
      ..entityId = _listingId
      ..action = 'create';

    test('create keeps the photos on the phone and sends their ids', () async {
      final photos = testCropPhotos(3);
      final listing = await create(photos);

      expect(listing.listingId, _listingId);
      expect(
        listing.photos.map((p) => p.photoId),
        photos.map((p) => p.photoId),
      );
      expect(listing.photos.every((p) => !p.uploaded), isTrue);
      for (final photo in listing.photos) {
        expect(File(photo.localPath!).existsSync(), isTrue);
      }
      final op = (await queue.getPendingOperations()).single;
      final data = jsonDecode(op.data) as Map<String, dynamic>;
      expect(data['photoIds'], photos.map((p) => p.photoId).toList());
    });

    test('create refuses fewer than 2 or more than 4 photos, writing nothing',
        () async {
      for (final count in [0, 1, 5]) {
        await expectLater(create(testCropPhotos(count)), throwsArgumentError);
      }
      expect(await isar.cropListings.count(), 0);
      expect(await queue.getPendingCount(), 0);
    });

    test('preflight ignores everything but a crop create', () async {
      final uploader = FakeCropPhotoUploader();
      final preflight = CropPhotoSyncPreflight(
        isar: isar,
        uploader: uploader,
        files: files,
      );

      expect(await preflight(createOp()..action = 'update'), isTrue);
      expect(await preflight(createOp()..entityType = 'trip'), isTrue);
      expect(uploader.calls, isEmpty);
    });

    test('preflight uploads and marks the photos, then lets the create go',
        () async {
      await create(testCropPhotos());
      final uploader = FakeCropPhotoUploader();
      final preflight = CropPhotoSyncPreflight(
        isar: isar,
        uploader: uploader,
        files: files,
      );

      expect(await preflight(createOp()), isTrue);
      expect(uploader.calls.single.photos, hasLength(2));
      final stored = await repository.getListingById(_listingId);
      expect(stored!.photos.every((p) => p.uploaded), isTrue);

      // Nothing left to send: no second upload.
      expect(await preflight(createOp()), isTrue);
      expect(uploader.calls, hasLength(1));
    });

    test('preflight holds the create while a photo is missing on the server',
        () async {
      final photos = testCropPhotos();
      await create(photos);
      final preflight = CropPhotoSyncPreflight(
        isar: isar,
        uploader: FakeCropPhotoUploader(failIds: {photos.last.photoId}),
        files: files,
      );

      expect(await preflight(createOp()), isFalse);
      final stored = await repository.getListingById(_listingId);
      expect(stored!.photos.map((p) => p.uploaded), [true, false]);

      // The next run sends only the photo that is still missing.
      final retry = FakeCropPhotoUploader();
      expect(
        await CropPhotoSyncPreflight(isar: isar, uploader: retry, files: files)(
          createOp(),
        ),
        isTrue,
      );
      expect(
        retry.calls.single.photos.map((p) => p.photoId),
        [photos.last.photoId],
      );
    });

    test('preflight waits when offline, goes on a refusal that cannot change',
        () async {
      await create(testCropPhotos());

      Future<bool> withError(Object error) => CropPhotoSyncPreflight(
            isar: isar,
            uploader: FakeCropPhotoUploader(error: error),
            files: files,
          )(createOp());

      expect(await withError(const NetworkException()), isFalse);
      expect(await withError(const ServerException('boom', 500)), isFalse);
      expect(await withError(const ValidationException('bad size')), isTrue);
    });

    test('preflight sends the create when the photo files are gone', () async {
      final listing = await create(testCropPhotos());
      for (final photo in listing.photos) {
        File(photo.localPath!).deleteSync();
      }
      final uploader = FakeCropPhotoUploader();

      expect(
        await CropPhotoSyncPreflight(
          isar: isar,
          uploader: uploader,
          files: files,
        )(createOp()),
        isTrue,
      );
      expect(uploader.calls, isEmpty);
    });
  });

  group('WebCropListingRepository photos', () {
    late _MockApiClient api;

    setUp(() {
      api = _MockApiClient();
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'status': 'success'},
          ],
        },
      );
    });

    Future<void> create(
      WebCropListingRepository repository,
      List<CropPhotoDraft> photos,
    ) =>
        repository.createListing(
          farmerId: 'farmer-1',
          cropType: 'teff',
          quantity: 10,
          pricePerKg: 45,
          latitude: 9.0,
          longitude: 38.7,
          locationName: 'Ada',
          description: '',
          photos: photos,
        );

    test('uploads first, then creates with the photo ids', () async {
      final uploader = FakeCropPhotoUploader();
      final photos = testCropPhotos();

      await create(
        WebCropListingRepository(api: api, photoUploader: uploader),
        photos,
      );

      final listingId = uploader.calls.single.listingId;
      final body = verify(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured.single as Map;
      final op = (body['operations'] as List).single as Map;
      expect(op['entityId'], listingId);
      expect((op['data'] as Map)['photoIds'], photos.map((p) => p.photoId));
    });

    test('a photo that does not upload fails the save before any create',
        () async {
      final photos = testCropPhotos();

      await expectLater(
        create(
          WebCropListingRepository(
            api: api,
            photoUploader:
                FakeCropPhotoUploader(failIds: {photos.first.photoId}),
          ),
          photos,
        ),
        throwsA(isA<CropPhotoUploadException>()),
      );
      verifyNever(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      );
    });

    test('reads photos from GET /crops', () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/crops',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'crops': [
            {
              'cropId': _listingId,
              'status': 'available',
              'photos': [
                {'photoId': 'p1', 'thumbUrl': 'https://t/p1'},
              ],
            },
          ],
        },
      );

      final listings = await WebCropListingRepository(api: api)
          .getListingsByFarmer('farmer-1');

      expect(listings.single.photos.single.thumbUrl, 'https://t/p1');
    });
  });
}
