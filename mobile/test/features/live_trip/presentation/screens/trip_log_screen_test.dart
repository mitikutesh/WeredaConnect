import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_log_screen.dart';

import '../../live_trip_fixtures.dart';
import '../../live_trip_harness.dart';

/// LT-11: the buyer's trip log: times on the viewer's clock, the town of
/// each check-in, sent late tags, other stops never named, pickup proof,
/// price breakdown, help and privacy.
void main() {
  late LiveTripDeps deps;

  setUp(() {
    deps = LiveTripDeps(order: liveOrder());
  });

  /// Picked up near Akaki, another order's scan at 05:40, hourly check-ins,
  /// a late one (received 40 minutes after capture), a pause, two other
  /// stops (one done).
  DeliveryTrack logTrack() {
    final trail = [
      fix(8.88, 38.79, at(5, 5), source: 'scan'),
      fix(8.83, 38.86, at(5, 40), source: 'scan'),
      fix(8.752, 38.978, at(7, 5)),
      fix(8.64, 39.06, at(8, 42), receivedAt: at(9, 22)),
    ];
    return DeliveryTrack(
      deliveryId: 'del-101',
      sharing: TrackSharing.live,
      asOf: asOf,
      lastFix: trail.last,
      trail: trail,
      pauses: [TrackPause(from: at(7, 30), until: at(8, 0))],
      pickup: const GeoPoint(8.88, 38.79),
      pickedUpAt: at(5, 5),
      dropoff: const GeoPoint(8.55, 39.27),
      otherStops: const [
        TrackStop(kind: 'pickup', point: GeoPoint(8.83, 38.86), done: true),
        TrackStop(kind: 'dropoff', point: GeoPoint(8.6, 39.18), done: false),
      ],
      eta: TrackEta(from: at(10, 20), to: at(11, 0)),
      remainingKm: 47.2,
    );
  }

  Future<void> pumpLog(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
  }) async {
    tester.view.physicalSize = const Size(420, 3200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: FakeTrackRepository(),
        locale: locale,
        home: TripLogScreen(
          delivery: liveDelivery(),
          currentUserId: buyerId,
          track: logTrack(),
          order: liveOrder(),
          places: corridorPlaces,
          now: asOf,
        ),
      ),
    );
    await settleReads(tester);
  }

  // Web board: beside the details the check-ins are one line each, newest
  // first, the late one tagged with the short pill and a pause's range
  // kept whole.
  testWidgets('the compact list: short sent-late pill, unwrapped range',
      (tester) async {
    tester.view.physicalSize = const Size(330, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: FakeTrackRepository(),
        home: Scaffold(
          body: TripCheckInList(
            track: logTrack(),
            places: corridorPlaces,
            compact: true,
          ),
        ),
      ),
    );
    await settleReads(tester);

    expect(find.text('Sent late, no network'), findsOneWidget);
    expect(find.textContaining('no network until'), findsNothing);
    final range = find.textContaining('-');
    expect(range, findsWidgets);
    expect(tester.takeException(), isNull);
  });

  testWidgets('check-ins with times, towns and tags', (tester) async {
    await pumpLog(tester);
    expect(find.text('08:05'), findsOneWidget);
    expect(find.text('Akaki'), findsWidgets);
    expect(
      find.text("Picked up, the farmer's QR was scanned"),
      findsOneWidget,
    );
    expect(find.text('Hourly check-in'), findsNWidgets(2));
    expect(find.text('11:42'), findsOneWidget);
    // The latest check-in carries its tag in the title (the board).
    expect(find.text('Mojo · latest'), findsOneWidget);
    // The late check-in says until when the phone had no network.
    expect(find.text('Sent late, no network until 12:22'), findsOneWidget);
    expect(find.text('10:30-11:00'), findsOneWidget);
    expect(find.text('Sharing paused'), findsOneWidget);
  });

  testWidgets('other stops are listed but never named', (tester) async {
    await pumpLog(tester);
    // The other order's scan, the done stop, the stop still ahead.
    expect(find.text('Another stop on this truck'), findsNWidgets(3));
    expect(find.text('passed'), findsOneWidget);
    // Only the buyer's own parties are named on the screen.
    expect(find.textContaining('farmer-002'), findsNothing);
  });

  // Buyer · Trip log board: the stop ahead says roughly when, then the
  // buyer's drop-off; still without a name or a town.
  testWidgets('the stop ahead says roughly when, then on to you',
      (tester) async {
    await pumpLog(tester);
    expect(find.text('About 35 min, then on to you'), findsOneWidget);
  });

  testWidgets('ahead, proof, prices, help and privacy', (tester) async {
    await pumpLog(tester);
    expect(find.textContaining('13:20-14:00'), findsOneWidget);
    expect(
      find.textContaining("Your drop-off: you scan the driver's QR here"),
      findsOneWidget,
    );
    expect(find.text('QR scanned 08:05 · GPS recorded'), findsOneWidget);
    expect(find.text('84,000 ETB'), findsOneWidget);
    expect(find.text('3,000 ETB'), findsOneWidget);
    expect(find.text('91,200 ETB'), findsOneWidget);
    expect(
      find.text(
        'Almaz Bekele is paid 84,000 ETB and Dawit Tesfaye 3,000 ETB, 48 h '
        'after you confirm delivery.',
      ),
      findsOneWidget,
    );
    expect(find.text('Who has agreed'), findsOneWidget);
    expect(
      find.textContaining('A picked-up order can no longer be cancelled.'),
      findsOneWidget,
    );
    // The two buttons sit side by side, each with the first name (the
    // board).
    expect(find.text('Message Dawit'), findsOneWidget);
    expect(find.text('Message Almaz'), findsOneWidget);
    expect(
      find.textContaining('Only you and Almaz Bekele see where this truck is'),
      findsOneWidget,
    );
  });

  testWidgets('om: the times on the Ethiopian clock', (tester) async {
    await pumpLog(tester, locale: const Locale('om'));
    expect(find.text('2:05 ganama'), findsOneWidget);
    expect(find.text('5:42 ganama'), findsOneWidget);
    expect(find.textContaining('7:20-8:00 waaree booda'), findsOneWidget);
  });
}
