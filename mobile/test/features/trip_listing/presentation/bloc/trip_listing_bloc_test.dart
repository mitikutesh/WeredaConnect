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

/// Wait (bounded) until [states] holds at least [count] emissions. A fixed
/// 100ms sleep is flaky on slow CI runners — CreateTrip was observed emitting
/// only Loading within the window. Falls through on timeout so the following
/// expect reports the actual emissions.
Future<void> waitForStates(List<TripListingState> states, int count) async {
  final deadline = DateTime.now().add(const Duration(seconds: 10));
  while (states.length < count && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}

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
      await waitForStates(states, 2);

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
      await waitForStates(states, 2);

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

      // Load trips first so the BLoC has them in state for optimistic update
      bloc.add(const LoadTrips(truckerId: 'trucker-001'));
      await waitForStates(states, 2);

      states.clear();

      bloc.add(
        CancelTrip(
          tripId: trip.tripId,
          truckerId: 'trucker-001',
        ),
      );
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripsLoaded>());
      // Trip should be cancelled now via optimistic update
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
      await waitForStates(states, 2);

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
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[1], isA<TripsLoaded>());
      final loaded = states[1] as TripsLoaded;
      expect(loaded.trips.length, 1);

      await subscription.cancel();
      await bloc.close();
    });

    // --- Edge Case: Load Empty Trucker ---

    test('LoadTrips for nonexistent trucker emits empty list', () async {
      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(const LoadTrips(truckerId: 'nonexistent-trucker'));
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[0], isA<TripListingLoading>());
      expect(states[1], isA<TripsLoaded>());
      expect((states[1] as TripsLoaded).trips, isEmpty);

      await subscription.cancel();
      await bloc.close();
    });

    // --- Edge Case: Sequential Events ---

    test('Create then Load shows the created trip', () async {
      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      // Create a trip
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
      await waitForStates(states, 2);

      // Now load trips
      bloc.add(const LoadTrips(truckerId: 'trucker-001'));
      await waitForStates(states, 4);

      // Should have: Loading, Created, Loading, Loaded(1 trip)
      expect(states.length, 4);
      expect(states[1], isA<TripCreated>());
      expect(states[3], isA<TripsLoaded>());
      expect((states[3] as TripsLoaded).trips.length, 1);
      expect(
        (states[3] as TripsLoaded).trips.first.fromWoreda,
        'Bishoftu',
      );

      await subscription.cancel();
      await bloc.close();
    });

    // --- Edge Case: FilterTrips with toWoreda Only ---

    test('FilterTrips with toWoreda only filters correctly', () async {
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

      // NOTE: trucker-002's trip here goes to Hawassa, so the destination
      // filter excludes it either way — this case alone does NOT prove
      // trucker-scoping. The "scopes to the requesting trucker" tests below
      // (where trucker-002 shares the destination / there is no route filter)
      // are the discriminating proof for the Trucker visibility rule.
      bloc.add(
        const FilterTrips(
          truckerId: 'trucker-001',
          toWoreda: 'Addis Ababa',
        ),
      );
      await waitForStates(states, 2);

      expect(states.length, 2);
      expect(states[1], isA<TripsLoaded>());
      final loaded = states[1] as TripsLoaded;
      expect(loaded.toWoredaFilter, 'Addis Ababa');
      expect(loaded.trips.length, 1);
      expect(loaded.trips.first.toWoreda, 'Addis Ababa');

      await subscription.cancel();
      await bloc.close();
    });

    // --- Role visibility: Trucker MUST NOT see other truckers' trips ---

    test(
      'FilterTrips scopes to the requesting trucker even when another trucker '
      'shares the destination',
      () async {
        final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

        // Both truckers have a trip to the SAME destination trucker-001 filters
        // on. The old unscoped getOpenTrips would return BOTH; the scoped
        // getTripsByTrucker must return only trucker-001's. This test FAILS
        // under the pre-fix code, which is what makes it a real proof.
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
          toWoreda: 'Addis Ababa',
          departureDate: departure,
          capacity: 100.0,
          pricePerQuintal: 80.0,
        );

        final bloc = TripListingBloc(repository: repository);
        final states = <TripListingState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const FilterTrips(truckerId: 'trucker-001', toWoreda: 'Addis Ababa'),
        );
        await waitForStates(states, 2);

        expect(states.last, isA<TripsLoaded>());
        final loaded = states.last as TripsLoaded;
        expect(loaded.trips, isNotEmpty);
        expect(
          loaded.trips.every((t) => t.truckerId == 'trucker-001'),
          isTrue,
          reason: "trucker-002's trip to the same destination must not leak",
        );
        expect(loaded.trips.any((t) => t.truckerId == 'trucker-002'), isFalse);

        await subscription.cancel();
        await bloc.close();
      },
    );

    test(
      'FilterTrips with no route filter returns only the requesting '
      "trucker's trips",
      () async {
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

        bloc.add(const FilterTrips(truckerId: 'trucker-001'));
        await waitForStates(states, 2);

        expect(states.last, isA<TripsLoaded>());
        final loaded = states.last as TripsLoaded;
        expect(loaded.trips.length, 1);
        expect(loaded.trips.every((t) => t.truckerId == 'trucker-001'), isTrue);
        expect(loaded.trips.any((t) => t.truckerId == 'trucker-002'), isFalse);

        await subscription.cancel();
        await bloc.close();
      },
    );

    // --- Edge Case: CancelTrip then LoadTrips Shows Cancelled ---

    test('CancelTrip then LoadTrips includes cancelled trip', () async {
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

      // Load trips first so the BLoC has them in state for optimistic update
      bloc.add(const LoadTrips(truckerId: 'trucker-001'));
      await waitForStates(states, 2);

      states.clear();

      bloc.add(
        CancelTrip(
          tripId: trip.tripId,
          truckerId: 'trucker-001',
        ),
      );
      await waitForStates(states, 2);

      // CancelTrip uses optimistic update from loaded state
      expect(states.length, 2);
      expect(states[1], isA<TripsLoaded>());
      final loaded = states[1] as TripsLoaded;
      expect(loaded.trips.length, 1);
      expect(loaded.trips.first.status, TripStatus.cancelled);

      await subscription.cancel();
      await bloc.close();
    });

    // --- Edge Case: Multilingual Trip ---

    test('CreateTrip with Amharic names preserves characters', () async {
      final bloc = TripListingBloc(repository: repository);
      final states = <TripListingState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        CreateTrip(
          truckerId: 'trucker-001',
          truckerName: 'ታደሰ ወርቁ',
          truckerPhone: '+251911000000',
          fromWoreda: 'ቢሾፍቱ',
          toWoreda: 'አዲስ አበባ',
          departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
          capacity: 150.0,
          pricePerQuintal: 120.0,
        ),
      );
      await waitForStates(states, 2);

      expect(states[1], isA<TripCreated>());
      final created = states[1] as TripCreated;
      expect(created.trip.fromWoreda, 'ቢሾፍቱ');
      expect(created.trip.toWoreda, 'አዲስ አበባ');

      await subscription.cancel();
      await bloc.close();
    });
  });
}
