import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/live_trip/data/repositories/api_track_repository.dart';
import 'package:wereda_connect/features/live_trip/data/repositories/track_repository.dart';
import 'package:wereda_connect/features/live_trip/data/repositories/web_track_repository.dart';
import 'package:wereda_connect/features/live_trip/data/track_snapshot_store.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';

import '../live_trip_fixtures.dart';

class _MockApiClient extends Mock implements ApiClient {}

class _MockConnectivity extends Mock implements ConnectivityMonitor {}

const _path = '/deliveries/del-101/track';
const _buyer = 'buyer-1';

/// LT-10: the track read on both platforms, its offline snapshot and its
/// errors (live-trip-api.md section 2: 404 DELIVERY_NOT_FOUND, 500
/// TRACK_UNAVAILABLE, the client keeps its last snapshot).
void main() {
  late _MockApiClient api;
  late _MockConnectivity connectivity;
  late TrackSnapshotStore store;
  late DateTime now;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    api = _MockApiClient();
    connectivity = _MockConnectivity();
    when(() => connectivity.isConnected()).thenAnswer((_) async => true);
    store = TrackSnapshotStore();
    now = DateTime.utc(2026, 10, 27, 9, 10, 30);
  });

  void answer(Map<String, dynamic> body) =>
      when(() => api.get<Map<String, dynamic>>(_path))
          .thenAnswer((_) async => body);

  void fail(ApiException error) =>
      when(() => api.get<Map<String, dynamic>>(_path)).thenThrow(error);

  final platforms = <String, ApiTrackRepository Function()>{
    'mobile': () => TrackRepository(
          api: api,
          connectivity: connectivity,
          store: store,
          clock: () => now,
        ),
    'web': () => WebTrackRepository(api: api, store: store, clock: () => now),
  };

  for (final MapEntry(key: platform, value: build) in platforms.entries) {
    group(platform, () {
      test('a good answer is returned and stored with its fetch time',
          () async {
        answer(contractTrackJson());
        final repository = build();

        final result = await repository.fetchTrack('del-101', userId: _buyer);

        expect(result.failure, isNull);
        expect(result.fromNetwork, isTrue);
        expect(result.track!.sharing, TrackSharing.live);
        expect(result.snapshot!.fetchedAt, now);
        verify(() => api.get<Map<String, dynamic>>(_path)).called(1);

        final stored = await repository.lastSnapshot('del-101', userId: _buyer);
        expect(stored!.fetchedAt, now);
        expect(stored.track.lastFix!.point, const GeoPoint(8.752, 38.979));
        expect(stored.track.dropoffNote, 'Merkato, near Anwar Mosque');
      });

      test('a network error answers the snapshot with its age', () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);

        now = now.add(const Duration(minutes: 40));
        fail(const NetworkException());
        final result = await repository.fetchTrack('del-101', userId: _buyer);

        expect(result.failure, TrackFailure.offline);
        expect(result.fromNetwork, isFalse);
        expect(result.track!.deliveryId, 'del-101');
        expect(
          result.snapshot!.ageAt(now),
          const Duration(minutes: 40),
        );
      });

      test('a timeout is offline too', () async {
        fail(const TimeoutException('slow'));
        final result = await build().fetchTrack('del-101', userId: _buyer);
        expect(result.failure, TrackFailure.offline);
        expect(result.snapshot, isNull);
      });

      test('404 reports not found and clears nothing', () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);

        fail(
          const NotFoundException(
            'Delivery not found.',
            404,
            'DELIVERY_NOT_FOUND',
          ),
        );
        final result = await repository.fetchTrack('del-101', userId: _buyer);

        expect(result.failure, TrackFailure.notFound);
        expect(result.snapshot, isNotNull);
        expect(
          await repository.lastSnapshot('del-101', userId: _buyer),
          isNotNull,
        );
      });

      test('500 TRACK_UNAVAILABLE keeps the snapshot', () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);
        final firstFetch = now;

        now = now.add(const Duration(minutes: 5));
        fail(
          const ServerException(
            'The live trip is unavailable right now.',
            500,
            'TRACK_UNAVAILABLE',
          ),
        );
        final result = await repository.fetchTrack('del-101', userId: _buyer);

        expect(result.failure, TrackFailure.unavailable);
        expect(result.snapshot!.fetchedAt, firstFetch);
        expect(
          (await repository.lastSnapshot('del-101', userId: _buyer))!.fetchedAt,
          firstFetch,
        );
      });

      test('a refused session or an odd answer fails and keeps the snapshot',
          () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);

        fail(const AuthException());
        expect(
          (await repository.fetchTrack('del-101', userId: _buyer)).failure,
          TrackFailure.failed,
        );

        answer({'unexpected': true});
        final odd = await repository.fetchTrack('del-101', userId: _buyer);
        expect(odd.failure, TrackFailure.failed);
        expect(odd.snapshot, isNotNull);
      });

      test('429 is unavailable', () async {
        fail(const RateLimitException('slow down'));
        expect(
          (await build().fetchTrack('del-101', userId: _buyer)).failure,
          TrackFailure.unavailable,
        );
      });

      test('an ended answer replaces the live snapshot', () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);

        answer(endedTrackJson());
        final ended = await repository.fetchTrack('del-101', userId: _buyer);
        expect(ended.track!.sharing, TrackSharing.ended);

        final stored = await repository.lastSnapshot('del-101', userId: _buyer);
        expect(stored!.track.sharing, TrackSharing.ended);
        expect(stored.track.summary!.fixes, 7);
      });

      test('another user on the same device never reads the snapshot',
          () async {
        answer(contractTrackJson());
        final repository = build();
        await repository.fetchTrack('del-101', userId: _buyer);

        fail(const NetworkException());
        final other =
            await repository.fetchTrack('del-101', userId: 'farmer-9');
        expect(other.failure, TrackFailure.offline);
        expect(other.snapshot, isNull);
      });

      test('a failed snapshot write still returns the fresh answer', () async {
        answer(contractTrackJson());
        final broken = TrackSnapshotStore(
          preferences: () async => throw Exception('disk full'),
        );
        final repository = platform == 'mobile'
            ? TrackRepository(
                api: api,
                connectivity: connectivity,
                store: broken,
                clock: () => now,
              )
            : WebTrackRepository(api: api, store: broken, clock: () => now);

        final result = await repository.fetchTrack('del-101', userId: _buyer);
        expect(result.fromNetwork, isTrue);
        expect(await repository.lastSnapshot('del-101', userId: _buyer), null);
      });
    });
  }

  group('mobile only', () {
    test('offline answers the snapshot without calling the server', () async {
      answer(contractTrackJson());
      final repository = TrackRepository(
        api: api,
        connectivity: connectivity,
        store: store,
        clock: () => now,
      );
      await repository.fetchTrack('del-101', userId: _buyer);
      clearInteractions(api);

      when(() => connectivity.isConnected()).thenAnswer((_) async => false);
      final result = await repository.fetchTrack('del-101', userId: _buyer);

      expect(result.failure, TrackFailure.offline);
      expect(result.snapshot, isNotNull);
      verifyNever(() => api.get<Map<String, dynamic>>(any()));
    });

    test('a failing connectivity check still tries the server', () async {
      answer(contractTrackJson());
      when(() => connectivity.isConnected()).thenThrow(Exception('plugin'));
      final repository = TrackRepository(
        api: api,
        connectivity: connectivity,
        store: store,
        clock: () => now,
      );
      final result = await repository.fetchTrack('del-101', userId: _buyer);
      expect(result.fromNetwork, isTrue);
    });
  });

  test('the delivery id is escaped in the route', () {
    expect(
      ApiTrackRepository.trackPath('del 1/2'),
      '/deliveries/del%201%2F2/track',
    );
  });
}
