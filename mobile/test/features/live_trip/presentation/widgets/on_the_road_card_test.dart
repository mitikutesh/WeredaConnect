import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/farmer_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';

import '../../live_trip_harness.dart';

/// LT-11: the farmer's home card shows only their own deliveries on the
/// road; the cards themselves show nothing to the trucker.
void main() {
  late LiveTripDeps deps;
  late FakeTrackRepository tracks;

  setUp(() {
    deps = LiveTripDeps(order: liveOrder());
    tracks = FakeTrackRepository(track: recentTrack());
  });

  Future<void> pumpHome(
    WidgetTester tester,
    List<Delivery> deliveries, {
    String userId = farmerId,
  }) async {
    when(
      () => deps.deliveryRepository.getDeliveriesByUser(
        any(),
        role: any(named: 'role'),
        statusFilter: any(named: 'statusFilter'),
      ),
    ).thenAnswer((_) async => deliveries);
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        home: Scaffold(
          body: SingleChildScrollView(
            child: FarmerOnTheRoadSection(userId: userId),
          ),
        ),
      ),
    );
    await settleReads(tester);
  }

  testWidgets('the farmer\'s own order on the road, with its window',
      (tester) async {
    await pumpHome(tester, [
      liveDelivery(),
      liveDelivery(id: 'delivery-102', status: DeliveryStatus.pending),
      liveDelivery(id: 'delivery-103', status: DeliveryStatus.delivered),
    ]);
    expect(find.byType(OnTheRoadCard), findsOneWidget);
    expect(find.text('Your Wheat is on the road'), findsOneWidget);
    expect(find.text('Selam Traders'), findsOneWidget);
    expect(find.textContaining('Checked in '), findsOneWidget);
    expect(find.textContaining('Arrives '), findsOneWidget);
    verify(
      () => deps.deliveryRepository.getDeliveriesByUser(
        farmerId,
        role: 'farmer',
      ),
    ).called(1);

    await tester.tap(find.byType(OnTheRoadCard));
    await tester.pumpAndSettle();
    expect(find.byType(FarmerTripScreen), findsOneWidget);
  });

  testWidgets('another farmer\'s delivery never shows', (tester) async {
    await pumpHome(tester, [liveDelivery(farmer: 'farmer-002')]);
    expect(find.byType(OnTheRoadCard), findsNothing);
    expect(tracks.fetches, 0);
  });

  testWidgets('nothing on the road, nothing shown', (tester) async {
    await pumpHome(tester, [
      liveDelivery(status: DeliveryStatus.pending),
    ]);
    expect(find.byType(OnTheRoadCard), findsNothing);
  });

  testWidgets('a card given to the trucker draws nothing and reads nothing',
      (tester) async {
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        home: Scaffold(
          body: OnTheRoadCard(
            delivery: liveDelivery(),
            currentUserId: truckerId,
          ),
        ),
      ),
    );
    await settleReads(tester);
    expect(find.text('On the road'), findsNothing);
    expect(tracks.fetches, 0);
  });
}
