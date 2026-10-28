import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/farmer_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_log_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';

import '../../live_trip_fixtures.dart';
import '../../live_trip_harness.dart';

/// LT-11: every live trip screen lays out on a 360 dp phone in all three
/// languages (Amharic and Afaan Oromo run longer than English), with no
/// overflow.
void main() {
  setUpAll(mockScannerChannels);

  final screens = <String, (Widget Function(), DeliveryTrack)>{
    'live': (
      () => LiveTripView(
            delivery: liveDelivery(),
            currentUserId: buyerId,
            places: corridorPlaces,
            clock: fixedNow,
          ),
      withEta(corridorTrack()),
    ),
    'late': (
      () => LiveTripView(
            delivery: liveDelivery(),
            currentUserId: buyerId,
            places: corridorPlaces,
            clock: () => at(10, 5),
          ),
      withEta(corridorTrack()),
    ),
    'arriving': (
      () => LiveTripView(
            delivery: liveDelivery(),
            currentUserId: buyerId,
            places: corridorPlaces,
            clock: fixedNow,
          ),
      arrivingTrack(),
    ),
    'delivered': (
      () => LiveTripView(
            delivery: liveDelivery(
              status: DeliveryStatus.delivered,
              confirmedAt: at(11, 2),
            ),
            currentUserId: buyerId,
            places: corridorPlaces,
            clock: () => at(12, 0),
          ),
      DeliveryTrack.fromJson(endedTrackJson()),
    ),
    'farmer': (
      () => FarmerTripView(
            delivery: liveDelivery(),
            currentUserId: farmerId,
            places: corridorPlaces,
            clock: fixedNow,
          ),
      withEta(corridorTrack()),
    ),
    'trip log': (
      () => TripLogScreen(
            delivery: liveDelivery(),
            currentUserId: buyerId,
            track: withEta(corridorTrack()),
            order: liveOrder(),
            places: corridorPlaces,
            now: asOf,
          ),
      withEta(corridorTrack()),
    ),
    'card': (
      () => ListView(
            children: [
              OnTheRoadCard(
                delivery: liveDelivery(),
                currentUserId: buyerId,
                places: corridorPlaces,
                clock: fixedNow,
              ),
              OnTheRoadCard(
                delivery: liveDelivery(),
                currentUserId: farmerId,
                places: corridorPlaces,
                clock: fixedNow,
              ),
            ],
          ),
      withEta(corridorTrack()),
    ),
  };

  for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
    for (final MapEntry(key: name, value: (build, track)) in screens.entries) {
      testWidgets('$name fits a small phone in ${locale.languageCode}',
          (tester) async {
        tester.view.physicalSize = const Size(360, 2600);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final deps = LiveTripDeps(order: liveOrder(), escrow: heldEscrow());
        await tester.pumpWidget(
          liveTripApp(
            deps: deps,
            tracks: FakeTrackRepository(track: track),
            locale: locale,
            home: Scaffold(body: build()),
          ),
        );
        await settleReads(tester);
        expect(tester.takeException(), isNull);
      });
    }
  }
}
