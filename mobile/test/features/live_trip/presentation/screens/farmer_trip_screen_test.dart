import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/farmer_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';

import '../../live_trip_fixtures.dart';
import '../../live_trip_harness.dart';

/// LT-11: the farmer's "Your wheat is on the road": only their own order's
/// delivery, other stops as unnamed grey rings, the payment steps.
void main() {
  late LiveTripDeps deps;
  late FakeTrackRepository tracks;

  setUp(() {
    deps = LiveTripDeps(order: liveOrder());
    tracks = FakeTrackRepository(track: withEta(corridorTrack()));
  });

  Future<void> pumpFarmer(
    WidgetTester tester, {
    Delivery? delivery,
    String userId = farmerId,
    Locale locale = const Locale('en'),
  }) async {
    tester.view.physicalSize = const Size(420, 2400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        locale: locale,
        home: FarmerTripScreen(
          delivery: delivery ?? liveDelivery(),
          currentUserId: userId,
          places: corridorPlaces,
          clock: fixedNow,
        ),
      ),
    );
    await settleReads(tester);
  }

  testWidgets('the farmer follows their own order from their pickup',
      (tester) async {
    await pumpFarmer(tester);
    expect(find.text('Your Wheat is on the road'), findsOneWidget);
    expect(find.text('2,000 kg to Selam Traders'), findsOneWidget);
    expect(find.byType(TrackMap), findsOneWidget);
    // The board's chip under the map: time, town and age on one line.
    expect(find.text('11:42 · Mojo · 28 min ago'), findsOneWidget);
    expect(find.text('Arrives 13:20-14:00'), findsOneWidget);
    expect(
      find.text(
        'Grey rings are other stops on this truck. They are never named.',
      ),
      findsOneWidget,
    );
    expect(find.text('Your payment'), findsOneWidget);
    expect(find.text('84,000 ETB'), findsOneWidget);
    expect(find.text('Buyer paid, held in escrow'), findsOneWidget);
    expect(find.text('You handed over, QR scanned'), findsOneWidget);
    expect(find.text('Buyer confirms delivery'), findsOneWidget);
    expect(find.text('Selam Traders'), findsOneWidget);
    expect(find.text('Dawit Tesfaye'), findsOneWidget);
    expect(
      find.byKey(const Key('farmer_trip_order_link')),
      findsOneWidget,
    );
    expect(tracks.readers, [farmerId]);
  });

  testWidgets('another farmer, the buyer and the trucker get nothing here',
      (tester) async {
    for (final (delivery, userId) in [
      (liveDelivery(farmer: 'farmer-002'), farmerId),
      (liveDelivery(), buyerId),
      (liveDelivery(), truckerId),
    ]) {
      await pumpFarmer(tester, delivery: delivery, userId: userId);
      expect(find.byType(TrackMap), findsNothing, reason: userId);
      expect(
        find.text('You are not authorized to view this order.'),
        findsOneWidget,
        reason: userId,
      );
    }
    expect(tracks.fetches, 0);
  });

  testWidgets('after delivery: sharing ended, no map', (tester) async {
    tracks.track = DeliveryTrack.fromJson(endedTrackJson());
    await pumpFarmer(
      tester,
      delivery: liveDelivery(
        status: DeliveryStatus.delivered,
        confirmedAt: at(11, 2),
      ),
    );
    expect(find.text('Delivered at 14:02'), findsOneWidget);
    expect(find.text('Sharing ended at delivery'), findsOneWidget);
    expect(find.byType(TrackMap), findsNothing);
  });

  testWidgets('am: the arrival window on the Ethiopian clock', (tester) async {
    await pumpFarmer(tester, locale: const Locale('am'));
    expect(
      find.text('የሚደርስበት ከቀኑ 7:20-8:00 (13:20-14:00)'),
      findsOneWidget,
    );
  });
}
