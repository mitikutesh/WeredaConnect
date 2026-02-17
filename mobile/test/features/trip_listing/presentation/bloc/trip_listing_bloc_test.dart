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
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';

void main() {
  group('TripListingBloc', () {
    late Isar isar;
    late Directory tempDir;
    late TripListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('trip_bloc_test_');
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
        name: 'test_trip_bloc_${DateTime.now().millisecondsSinceEpoch}',
      );
      repository = TripListingRepository(
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

    test('initial state is TripListingInitial', () {
      final bloc = TripListingBloc(repository: repository);
      expect(bloc.state, const TripListingInitial());
      bloc.close();
    });

    test('emits [Loading, Loaded] on LoadTrips', () async {
      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(const LoadTrips(truckerId: 'trucker-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripsLoaded>());
      expect((states[1] as TripsLoaded).trips, isEmpty);

      await subscription.cancel();
      await bloc.close();
    });

    test('emits [Loading, Created] on CreateTrip', () async {
      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        CreateTrip(
          truckerId: 'trucker-001',
          truckerName: 'Test Trucker',
          truckerPhone: '+251911000000',
          fromWoreda: 'Bishoftu',
          toWoreda: 'Addis Ababa',
          departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
          capacity: 150.0,
          pricePerQuintal: 120.0,
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripCreated>());
      expect((states[1] as TripCreated).trip.fromWoreda, 'Bishoftu');

      await subscription.cancel();
      await bloc.close();
    });

    test('emits [Loading, Loaded] on CancelTrip', () async {
      // Create a trip first
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        CancelTrip(
          tripId: trip.tripId,
          truckerId: 'trucker-001',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripsLoaded>());
      // Trip should be cancelled now
      final loaded = states[1] as TripsLoaded;
      expect(loaded.trips.first.status, TripStatus.cancelled);

      await subscription.cancel();
      await bloc.close();
    });

    test('emits Loaded with filtered list on FilterTrips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      // Create some trips
      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const FilterTrips(
          truckerId: 'trucker-001',
          fromWoreda: 'Bishoftu',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripsLoaded>());
      final loaded = states[1] as TripsLoaded;
      expect(loaded.fromWoredaFilter, 'Bishoftu');
      expect(loaded.trips.length, 1);
      expect(loaded.trips.first.fromWoreda, 'Bishoftu');

      await subscription.cancel();
      await bloc.close();
    });

    test('FilterTrips with no filters loads trucker trips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const FilterTrips(truckerId: 'trucker-001'),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[1], isA<TripsLoaded>());
      final loaded = states[1] as TripsLoaded;
      expect(loaded.trips.length, 1);

      await subscription.cancel();
      await bloc.close();
    });
  });
}
