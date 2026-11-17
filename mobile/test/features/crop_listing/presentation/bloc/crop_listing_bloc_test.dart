import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';

import '../../../../helpers/wait_for.dart';
import '../../../../helpers/crop_photo_fixtures.dart';

/// FUP-498: stands in for the farmer's own-listing pull; [_pull] runs it.
class _OwnCropSync extends Fake implements MarketplaceSync {
  _OwnCropSync(this._pull);

  final Future<bool> Function(String farmerId) _pull;
  final List<String> farmers = [];
  bool answered = false;

  @override
  Future<bool> pullOwnCrops({required String farmerId}) async {
    farmers.add(farmerId);
    try {
      return await _pull(farmerId);
    } finally {
      answered = true;
    }
  }
}

void main() {
  group('CropListingBloc', () {
    late Isar isar;
    late Directory tempDir;
    late CropListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('crop_bloc_test_');
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
        name: 'test_crop_bloc_${DateTime.now().millisecondsSinceEpoch}',
      );
      repository = CropListingRepository(
        photoFiles: tempCropPhotoFileStore(),
        isar: isar,
        syncQueue: SyncQueue(isar),
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('initial state is CropListingInitial', () {
      final bloc = CropListingBloc(repository: repository);
      expect(bloc.state, const CropListingInitial());
      bloc.close();
    });

    test('emits [Loading, Loaded] on LoadCropListings', () async {
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);

      bloc.add(const LoadCropListings(farmerId: 'farmer-001'));
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      expect((states[1] as CropListingsLoaded).listings, isEmpty);
    });

    // FUP-498: the farmer's list paints what the phone holds, then pulls the
    // farmer's own list, and paints again only when the pull removed a
    // listing deleted elsewhere.
    group('FUP-498: own listing pull', () {
      /// The farmer's two stored listings' ids, by crop.
      Future<Map<String, String>> seedTwo() async => {
            for (final crop in ['teff', 'wheat'])
              crop: (await repository.createListing(
                photos: testCropPhotos(),
                farmerId: 'farmer-001',
                cropType: crop,
                quantity: 10,
                pricePerKg: 40,
                latitude: 9,
                longitude: 38,
                locationName: 'Adama',
                description: crop,
              ))
                  .listingId,
          };

      /// Removes [listingId] from the phone, as a pull that found it deleted.
      Future<bool> removes(String listingId) async {
        await isar.writeTxn(
          () => isar.cropListings.deleteByListingId(listingId),
        );
        return true;
      }

      test('paints the stored list, then again without the removed listing',
          () async {
        final ids = await seedTwo();
        final sync = _OwnCropSync((_) => removes(ids['wheat']!));
        final bloc =
            CropListingBloc(repository: repository, marketplaceSync: sync);
        addTearDown(bloc.close);
        final states = <CropListingState>[];
        final subscription = bloc.stream.listen(states.add);
        addTearDown(subscription.cancel);

        bloc.add(const LoadCropListings(farmerId: 'farmer-001'));
        await waitForStates(states, 3);

        expect(sync.farmers, ['farmer-001']);
        expect(states[0], isA<CropListingLoading>());
        expect((states[1] as CropListingsLoaded).listings, hasLength(2));
        expect(
          (states[2] as CropListingsLoaded).listings.map((l) => l.listingId),
          [ids['teff']],
        );
      });

      test('a pull that removed nothing or failed leaves the painted list',
          () async {
        await seedTwo();
        for (final pull in <Future<bool> Function(String)>[
          (_) async => false,
          (_) async => throw Exception('offline'),
        ]) {
          final sync = _OwnCropSync(pull);
          final bloc =
              CropListingBloc(repository: repository, marketplaceSync: sync);
          final states = <CropListingState>[];
          final subscription = bloc.stream.listen(states.add);

          bloc.add(const LoadCropListings(farmerId: 'farmer-001'));
          await waitFor(() => sync.answered);
          await pumpEventQueue();

          expect(states, hasLength(2));
          expect((states[1] as CropListingsLoaded).listings, hasLength(2));
          await subscription.cancel();
          await bloc.close();
        }
      });

      test('a filter painted while the pull was on its way stays', () async {
        final ids = await seedTwo();
        final release = Completer<void>();
        final sync = _OwnCropSync((_) async {
          await release.future;
          return removes(ids['wheat']!);
        });
        final bloc =
            CropListingBloc(repository: repository, marketplaceSync: sync);
        addTearDown(bloc.close);
        final states = <CropListingState>[];
        final subscription = bloc.stream.listen(states.add);
        addTearDown(subscription.cancel);

        bloc.add(const LoadCropListings(farmerId: 'farmer-001'));
        await waitForStates(states, 2);
        bloc.add(
          const FilterCropListings(farmerId: 'farmer-001', cropType: 'teff'),
        );
        await waitForStates(states, 4);
        release.complete();
        await waitFor(() => sync.answered);
        await pumpEventQueue();

        expect(states, hasLength(4));
        expect((bloc.state as CropListingsLoaded).activeFilter, 'teff');
      });
    });

    test('emits [Loading, Created] on CreateCropListing', () async {
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);

      bloc.add(
        CreateCropListing(
          photos: testCropPhotos(),
          farmerId: 'farmer-001',
          farmerName: 'Abebe Kebede',
          cropType: 'teff',
          quantity: 50.0,
          pricePerKg: 45.0,
          latitude: 9.0192,
          longitude: 38.7525,
          locationName: 'Ada',
          description: 'High quality teff',
        ),
      );
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingCreated>());
      final created = (states[1] as CropListingCreated).listing;
      expect(created.cropType, 'teff');
      expect(created.farmerName, 'Abebe Kebede');
    });

    test('emits Loaded with filtered list on FilterCropListings', () async {
      // Create some listings first
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat',
      );

      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);

      bloc.add(
        const FilterCropListings(
          farmerId: 'farmer-001',
          cropType: 'teff',
        ),
      );
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      final loaded = states[1] as CropListingsLoaded;
      expect(loaded.activeFilter, 'teff');
      expect(loaded.listings.length, 1);
      expect(loaded.listings.first.cropType, 'teff');
    });

    test('LoadActiveListings forwards searchQuery (SIM-51)', () async {
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        farmerName: 'Abebe Kebede',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Bishoftu',
        description: 'Teff',
      );
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-002',
        farmerName: 'Almaz Tesfaye',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Adama',
        description: 'Wheat',
      );

      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final expectation = expectLater(
        bloc.stream,
        emitsInOrder([
          isA<CropListingLoading>(),
          isA<CropListingsLoaded>()
              .having((s) => s.listings.length, 'listings.length', 1)
              .having(
                (s) => s.listings.single.farmerName,
                'farmerName',
                'Abebe Kebede',
              ),
        ]),
      );

      bloc.add(const LoadActiveListings(searchQuery: 'abebe'));
      await expectation;
    });

    test('emits [Loading, Loaded] on DeleteCropListing', () async {
      // Create a listing first
      final listing = await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );

      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);

      bloc.add(
        DeleteCropListing(
          listingId: listing.listingId,
          farmerId: 'farmer-001',
        ),
      );
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      // Listing should be expired now, but getListingsByFarmer returns all
      final loaded = states[1] as CropListingsLoaded;
      expect(loaded.listings.first.status, ListingStatus.expired);
    });

    /// A listing whose create already reached the server (SIM-56: an edit
    /// waits for an earlier change of the listing to go out).
    Future<CropListing> seed() async {
      final listing = await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );
      final queue = SyncQueue(isar);
      for (final op in await queue.getPendingOperations()) {
        await queue.markAsSynced(op.operationId);
      }
      return listing;
    }

    test('SIM-56: a delete answers its own completer, then the list reloads',
        () async {
      final listing = await seed();
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);
      final done = Completer<void>();

      bloc.add(
        DeleteCropListing(
          listingId: listing.listingId,
          farmerId: 'farmer-001',
          done: done,
        ),
      );
      await done.future;
      await waitForStates(states, 2);

      expect(states[1], isA<CropListingsLoaded>());
    });

    test('SIM-56: emits [Loading, Updated] and completes the edit', () async {
      final listing = await seed();
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);
      final done = Completer<CropListing>();

      bloc.add(
        UpdateCropListing(
          listing: listing,
          farmerId: 'farmer-001',
          pricePerKg: 50,
          description: 'Red teff',
          done: done,
        ),
      );
      final saved = await done.future;
      await waitForStates(states, 2);

      expect(saved.pricePerKg, 50);
      expect(saved.quantity, 50.0);
      expect(states[0], isA<CropListingLoading>());
      final updated = (states[1] as CropListingUpdated).listing;
      expect(updated.description, 'Red teff');
    });

    test('SIM-56: a refused edit fails its completer and emits the error',
        () async {
      final listing = await seed();
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);
      final done = Completer<CropListing>();

      bloc.add(
        UpdateCropListing(
          listing: listing,
          farmerId: 'farmer-002',
          pricePerKg: 50,
          done: done,
        ),
      );

      await expectLater(done.future, throwsStateError);
      await waitForStates(states, 2);
      expect(states[1], isA<CropListingError>());
    });
  });
}
