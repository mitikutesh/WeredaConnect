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
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(const LoadCropListings(farmerId: 'farmer-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      expect((states[1] as CropListingsLoaded).listings, isEmpty);

      await subscription.cancel();
      await bloc.close();
    });

    test('emits [Loading, Created] on CreateCropListing', () async {
      final bloc = CropListingBloc(repository: repository);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const CreateCropListing(
          farmerId: 'farmer-001',
          cropType: 'teff',
          quantity: 50.0,
          pricePerKg: 45.0,
          latitude: 9.0192,
          longitude: 38.7525,
          description: 'High quality teff',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingCreated>());
      expect((states[1] as CropListingCreated).listing.cropType, 'teff');

      await subscription.cancel();
      await bloc.close();
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
        description: 'Teff',
      );
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        description: 'Wheat',
      );

      final bloc = CropListingBloc(repository: repository);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const FilterCropListings(
          farmerId: 'farmer-001',
          cropType: 'teff',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      final loaded = states[1] as CropListingsLoaded;
      expect(loaded.activeFilter, 'teff');
      expect(loaded.listings.length, 1);
      expect(loaded.listings.first.cropType, 'teff');

      await subscription.cancel();
      await bloc.close();
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
        description: 'Teff',
      );

      final bloc = CropListingBloc(repository: repository);
      final states = <CropListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        DeleteCropListing(
          listingId: listing.listingId,
          farmerId: 'farmer-001',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<CropListingLoading>());
      expect(states[1], isA<CropListingsLoaded>());
      // Listing should be expired now, but getListingsByFarmer returns all
      final loaded = states[1] as CropListingsLoaded;
      expect(loaded.listings.first.status, ListingStatus.expired);

      await subscription.cancel();
      await bloc.close();
    });
  });
}
