import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';

import '../../../../helpers/wait_for.dart';

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

    test('emits [Loading, Created] on CreateCropListing', () async {
      final bloc = CropListingBloc(repository: repository);
      addTearDown(bloc.close);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);
      addTearDown(subscription.cancel);

      bloc.add(
        const CreateCropListing(
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
  });
}
