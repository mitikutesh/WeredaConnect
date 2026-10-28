import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/live_trip/data/prefs_sharing_state_store.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';

/// LT-08: the trucker's sharing answers survive a restart, per account.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => SharedPreferences.setMockInitialValues({}));

  final until = DateTime.utc(2026, 10, 28, 6, 30);

  test('round trips through preferences, one entry per account', () async {
    final store = PrefsSharingStateStore();
    final state = const SharingState()
        .withTrip(
          'trip-1',
          TripSharing(answer: SharingAnswer.shared, pausedUntil: until),
        )
        .withTrip('trip-2', const TripSharing(answer: SharingAnswer.declined))
        .withActive('trip-1');

    await store.save('trucker-1', state);
    final back = await store.load('trucker-1');

    expect(back.activeTripId, 'trip-1');
    expect(back.active?.pausedUntil, until);
    expect(back.trips['trip-2']?.answer, SharingAnswer.declined);
    expect((await store.load('trucker-2')).trips, isEmpty);

    await store.forget('trucker-1');
    expect((await store.load('trucker-1')).trips, isEmpty);
  });

  test('the sharing default round trips; ask is the default', () async {
    final store = PrefsSharingStateStore();
    expect(
      (await store.load('trucker-1')).sharingDefault,
      SharingDefault.ask,
    );

    final state = const SharingState()
        .withDefault(SharingDefault.scansOnly)
        .withTrip('trip-1', const TripSharing(answer: SharingAnswer.shared))
        .withActive('trip-1');
    await store.save('trucker-1', state);
    final back = await store.load('trucker-1');

    expect(back.sharingDefault, SharingDefault.scansOnly);
    expect(back.activeTripId, 'trip-1');
    expect(
      SharingState.fromJson({'default': 'nonsense'}).sharingDefault,
      SharingDefault.ask,
    );
    expect(const SharingState().toJson().containsKey('default'), isFalse);
  });

  test('a broken entry reads as empty', () async {
    SharedPreferences.setMockInitialValues({
      'live_trip_sharing_trucker-1': '{not json',
    });
    expect((await PrefsSharingStateStore().load('trucker-1')).trips, isEmpty);
  });

  test('at most 20 trips are kept, ended ones go first', () {
    var state = const SharingState();
    for (var i = 0; i < 25; i++) {
      state = state.withTrip(
        'trip-$i',
        TripSharing(answer: SharingAnswer.shared, ended: i < 10),
      );
    }
    state = state.withActive('trip-24');

    expect(state.trips, hasLength(SharingState.maxTrips));
    expect(state.trips.containsKey('trip-0'), isFalse);
    expect(state.trips.containsKey('trip-10'), isTrue);
  });
}
