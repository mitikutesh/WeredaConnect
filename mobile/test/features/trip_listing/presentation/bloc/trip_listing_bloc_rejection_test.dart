import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';

class _MockApiClient extends Mock implements ApiClient {}

CreateTrip _create() => CreateTrip(
      truckerId: 'trucker-1',
      truckerName: 'Trucker',
      truckerPhone: '+251900100002',
      fromWoreda: 'Adama',
      toWoreda: 'Bishoftu',
      departureDate: DateTime.now().toUtc().add(const Duration(days: 3)),
      capacity: 50,
      pricePerQuintal: 200,
    );

/// Runs [event] on a fresh bloc and returns the states it emitted (polls,
/// 5s cap, instead of sleeping a fixed time).
Future<List<TripListingState>> _run(
  BaseTripListingRepository repository,
  TripListingEvent event,
) async {
  final bloc = TripListingBloc(repository: repository);
  final states = <TripListingState>[];
  final sub = bloc.stream.listen(states.add);
  bloc.add(event);
  final deadline = DateTime.now().add(const Duration(seconds: 5));
  while (states.length < 2 && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
  await Future<void>.delayed(Duration.zero);
  await sub.cancel();
  await bloc.close();
  return states;
}

/// ESC-12 part B (sync-protocol.md 1.17.0): a refused trip change ends in an
/// explained error on web and in the server's trip on mobile, for every code
/// that can refuse a trucker's trip.
void main() {
  late _MockApiClient api;

  setUp(() {
    api = _MockApiClient();
  });

  group('web: the refusal ends in the error with its own code', () {
    late WebTripListingRepository repository;

    setUp(() {
      repository = WebTripListingRepository(api: api);
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async => {
          'trip': {
            'tripId': 'trip-1',
            'truckerId': 'trucker-1',
            'fromWoreda': 'Adama',
            'toWoreda': 'Bishoftu',
            'departureDate': '2026-10-30T00:00:00.000Z',
            'capacity': 50,
            'pricePerQuintal': 200,
            'status': 'open',
            'createdAt': '2026-10-01T00:00:00.000Z',
            'updatedAt': '2026-10-01T00:00:00.000Z',
            'version': 2,
          },
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

    void expectError(List<TripListingState> states, AppErrorCode code) {
      expect(states, hasLength(2));
      expect(states.first, isA<TripListingLoading>());
      expect(states.last, isA<TripListingError>());
      expect((states.last as TripListingError).code, code);
    }

    for (final (message, code) in [
      ('REPUTATION_RESTRICTED: restricted', AppErrorCode.reputationRestricted),
      ('APP_UPDATE_REQUIRED: minimum 2.0.0', AppErrorCode.appUpdateRequired),
    ]) {
      test('create: ${code.name}, never TripCreated', () async {
        refuseWith(message);
        expectError(await _run(repository, _create()), code);
      });
    }

    for (final (message, code) in [
      ('TRIP_HAS_BOOKINGS: booked', AppErrorCode.tripHasBookings),
      ('APP_UPDATE_REQUIRED: minimum 2.0.0', AppErrorCode.appUpdateRequired),
    ]) {
      for (final (name, event) in <(String, TripListingEvent)>[
        (
          'cancel',
          const CancelTrip(tripId: 'trip-1', truckerId: 'trucker-1'),
        ),
        ('start', const StartTrip(tripId: 'trip-1', truckerId: 'trucker-1')),
      ]) {
        test('$name: ${code.name}, never success', () async {
          refuseWith(message);
          expectError(await _run(repository, event), code);
        });
      }
    }
  });

  group('mobile: after the revert the bloc reads the server trip', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue queue;
    late TripListingRepository repository;
    late IsarMarketplaceSync sync;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('trip_bloc_rejection_');
      isar = await Isar.open(
        [TripSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'trip_bloc_rejection_${DateTime.now().microsecondsSinceEpoch}',
      );
      queue = SyncQueue(isar);
      repository = TripListingRepository(isar: isar, syncQueue: queue);
      sync = IsarMarketplaceSync(api: api, isar: isar);
    });

    tearDown(() async {
      queue.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    Future<List<Trip>> reload() async {
      final states =
          await _run(repository, const LoadTrips(truckerId: 'trucker-1'));
      expect(states.last, isA<TripsLoaded>());
      return (states.last as TripsLoaded).trips;
    }

    /// The server refuses the trucker's queued [action] for good, and the
    /// sync executor hands it to the revert handler.
    Future<void> serverRefuses(String action, String message) async {
      final op =
          (await isar.syncOperations.filter().actionEqualTo(action).findAll())
              .single;
      await queue.markAsRejected(op.operationId);
      await sync.revertRejected(op, message);
    }

    for (final message in [
      'REPUTATION_RESTRICTED: restricted',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('a refused create (${message.split(':').first}) is gone', () async {
        expect((await _run(repository, _create())).last, isA<TripCreated>());
        expect(await reload(), hasLength(1));

        await serverRefuses('create', message);

        expect(await reload(), isEmpty);
        verifyZeroInteractions(api);
      });
    }

    for (final message in [
      'TRIP_HAS_BOOKINGS: booked',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('a refused cancel (${message.split(':').first}) is open again',
          () async {
        final created = (await _run(repository, _create())).last as TripCreated;
        final tripId = created.trip.tripId;
        // The create reached the server; only the cancel is refused.
        final createOp = (await isar.syncOperations.where().findAll()).single;
        await queue.markAsSynced(createOp.operationId);
        await _run(
          repository,
          CancelTrip(tripId: tripId, truckerId: 'trucker-1'),
        );
        expect((await reload()).single.status, TripStatus.cancelled);
        when(() => api.get<Map<String, dynamic>>('/trips/$tripId')).thenAnswer(
          (_) async => {
            'trip': {
              'tripId': tripId,
              'truckerId': 'trucker-1',
              'truckerName': 'Trucker',
              'fromWoreda': 'Adama',
              'toWoreda': 'Bishoftu',
              'departureDate': '2026-10-30T00:00:00.000Z',
              'capacity': 50,
              'pricePerQuintal': 200,
              'status': 'open',
              'createdAt': '2026-10-01T00:00:00.000Z',
              'updatedAt': '2026-10-01T00:00:00.000Z',
              'version': 1,
            },
          },
        );

        await serverRefuses('delete', message);

        final trips = await reload();
        expect(trips.single.status, TripStatus.open);
        expect(trips.single.version, 1);
      });
    }
  });
}
