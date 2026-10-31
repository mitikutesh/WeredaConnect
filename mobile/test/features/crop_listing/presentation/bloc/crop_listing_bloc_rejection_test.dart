import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';

import '../../../../helpers/wait_for.dart';
import '../../../../helpers/crop_photo_fixtures.dart';

class _MockApiClient extends Mock implements ApiClient {}

final _create = CreateCropListing(
  photos: testCropPhotos(),
  farmerId: 'farmer-1',
  cropType: 'teff',
  quantity: 10,
  pricePerKg: 45,
  latitude: 9.6,
  longitude: 39.5,
  locationName: 'Debre Birhan',
  description: '',
);

/// Runs [event] on a fresh bloc and returns the states it emitted (polls,
/// 5s cap, instead of sleeping a fixed time).
Future<List<CropListingState>> _run(
  BaseCropListingRepository repository,
  CropListingEvent event, {
  int count = 2,
}) async {
  final bloc = CropListingBloc(repository: repository);
  final states = <CropListingState>[];
  final sub = bloc.stream.listen(states.add);
  bloc.add(event);
  await waitForStates(states, count);
  await Future<void>.delayed(Duration.zero);
  await sub.cancel();
  await bloc.close();
  return states;
}

/// ESC-12 part B (sync-protocol.md 1.17.0): a refused listing change ends in
/// an explained error on web and in the server's listing on mobile, for
/// every code that can refuse a farmer's listing.
void main() {
  late _MockApiClient api;

  setUp(() {
    api = _MockApiClient();
  });

  group('web: the refusal ends in the error with its own code', () {
    late WebCropListingRepository repository;

    setUp(() {
      repository = WebCropListingRepository(
        api: api,
        photoUploader: FakeCropPhotoUploader(),
      );
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1')).thenAnswer(
        (_) async => {
          'crop': {'cropId': 'crop-1', 'farmerId': 'farmer-1', 'version': 3},
        },
      );
    });

    void refuseWith(String message) {
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'status': 'error', 'message': message},
          ],
        },
      );
    }

    void expectError(List<CropListingState> states, AppErrorCode code) {
      expect(states, hasLength(2));
      expect(states.first, isA<CropListingLoading>());
      expect(states.last, isA<CropListingError>());
      expect((states.last as CropListingError).code, code);
    }

    for (final (message, code) in [
      ('REPUTATION_RESTRICTED: restricted', AppErrorCode.reputationRestricted),
      ('APP_UPDATE_REQUIRED: minimum 2.0.0', AppErrorCode.appUpdateRequired),
    ]) {
      test('create: ${code.name}, never CropListingCreated', () async {
        refuseWith(message);
        expectError(await _run(repository, _create), code);
      });
    }

    for (final (message, code) in [
      ('CROP_HAS_ACTIVE_ORDERS: open orders', AppErrorCode.cropHasActiveOrders),
      ('APP_UPDATE_REQUIRED: minimum 2.0.0', AppErrorCode.appUpdateRequired),
    ]) {
      test('delete: ${code.name}, never the reloaded list', () async {
        refuseWith(message);
        expectError(
          await _run(
            repository,
            const DeleteCropListing(listingId: 'crop-1', farmerId: 'farmer-1'),
          ),
          code,
        );
      });
    }
  });

  group('mobile: after the revert the bloc reads the server listing', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue queue;
    late CropListingRepository repository;
    late IsarMarketplaceSync sync;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('crop_bloc_rejection_');
      isar = await Isar.open(
        [CropListingSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'crop_bloc_rejection_${DateTime.now().microsecondsSinceEpoch}',
      );
      queue = SyncQueue(isar);
      repository = CropListingRepository(
        isar: isar,
        syncQueue: queue,
        photoFiles: tempCropPhotoFileStore(),
      );
      sync = IsarMarketplaceSync(api: api, isar: isar);
    });

    tearDown(() async {
      queue.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    /// The server refuses the farmer's queued operation for good, and the
    /// sync executor hands it to the revert handler.
    Future<void> serverRefuses(String message) async {
      final op = (await isar.syncOperations.where().findAll()).single;
      await queue.markAsRejected(op.operationId);
      await sync.revertRejected(op, message);
    }

    Future<List<CropListing>> reload() async {
      final states = await _run(
        repository,
        const LoadCropListings(farmerId: 'farmer-1'),
      );
      expect(states.last, isA<CropListingsLoaded>());
      return (states.last as CropListingsLoaded).listings;
    }

    test('a refused create (REPUTATION_RESTRICTED) is gone', () async {
      final created = await _run(repository, _create);
      expect(created.last, isA<CropListingCreated>());
      expect(await reload(), hasLength(1));

      await serverRefuses('REPUTATION_RESTRICTED: restricted');

      expect(await reload(), isEmpty);
      verifyZeroInteractions(api);
    });

    // The executor holds it for the updated build; even if the handler got
    // the code, the farmer's listing must stay.
    test('a create answered APP_UPDATE_REQUIRED stays on the list', () async {
      await _run(repository, _create);

      await serverRefuses('APP_UPDATE_REQUIRED: minimum 2.0.0');

      expect((await reload()).single.status, ListingStatus.active);
      verifyZeroInteractions(api);
    });

    for (final message in [
      'CROP_HAS_ACTIVE_ORDERS: open orders',
      'REPUTATION_RESTRICTED: restricted',
    ]) {
      test('a refused delete (${message.split(':').first}) is active again',
          () async {
        final listing = (await _run(repository, _create)).last;
        final listingId = (listing as CropListingCreated).listing.listingId;
        // The create reached the server; only the delete is refused.
        final createOp = (await isar.syncOperations.where().findAll()).single;
        await queue.markAsSynced(createOp.operationId);
        final deleted = await _run(
          repository,
          DeleteCropListing(listingId: listingId, farmerId: 'farmer-1'),
        );
        expect(
          (deleted.last as CropListingsLoaded).listings.single.status,
          ListingStatus.expired,
        );
        when(() => api.get<Map<String, dynamic>>('/crops/$listingId'))
            .thenAnswer(
          (_) async => {
            'crop': {
              'cropId': listingId,
              'farmerId': 'farmer-1',
              'cropType': 'teff',
              'quantityKg': 10,
              'pricePerKg': 45,
              'status': 'available',
              'createdAt': '2026-10-01T08:00:00.000Z',
              'updatedAt': '2026-10-01T08:00:00.000Z',
              'version': 1,
            },
          },
        );

        final op = (await isar.syncOperations
                .filter()
                .actionEqualTo('delete')
                .findAll())
            .single;
        await queue.markAsRejected(op.operationId);
        await sync.revertRejected(op, message);

        final listings = await reload();
        expect(listings.single.status, ListingStatus.active);
        expect(listings.single.version, 1);
      });
    }
  });
}
