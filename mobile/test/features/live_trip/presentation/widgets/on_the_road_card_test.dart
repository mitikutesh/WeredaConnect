import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/farmer_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';

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
    expect(find.textContaining('Selam Traders'), findsOneWidget);
    // Farmer home board: a sand card with the status pill, the ribbon,
    // the buyer with the window and the age of the last check-in.
    expect(find.text('In Transit'), findsOneWidget);
    expect(find.byType(RouteRibbon), findsOneWidget);
    expect(find.textContaining('min ago'), findsOneWidget);
    expect(
      find.textContaining('Arrives ', findRichText: true),
      findsOneWidget,
    );
    // The farmer's card carries no map: that is the buyer's.
    expect(find.byType(TrackMap), findsNothing);
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

  Future<void> pumpBuyerCard(WidgetTester tester, Delivery delivery) async {
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        home: Scaffold(
          body: OnTheRoadCard(delivery: delivery, currentUserId: buyerId),
        ),
      ),
    );
    await settleReads(tester);
  }

  testWidgets(
      'buyer: the board\'s card, the map with Open map, crop and kg from '
      'the row, the farmer with the pickup place, no status pill',
      (tester) async {
    await pumpBuyerCard(
      tester,
      liveDelivery()
        ..cropType = 'teff'
        ..quantity = 1500
        ..pickupPlace = 'Meki',
    );

    // The row wins over the order read (the order says 2,000 kg of wheat).
    expect(find.text('Teff · 1,500 kg'), findsOneWidget);
    expect(
      find.text('Almaz Bekele, Meki · Dawit Tesfaye'),
      findsOneWidget,
    );
    // Buyer deliveries board (page M): the card's top is the map, with
    // how current the check-in is and "Open map" over it.
    expect(find.byType(TrackMap), findsOneWidget);
    expect(find.text('Open map'), findsOneWidget);
    expect(find.textContaining('Checked in '), findsOneWidget);
    expect(find.text('Arriving'), findsOneWidget);
    expect(find.byType(RouteRibbon), findsNothing);
    expect(find.text('On the road'), findsNothing);
  });

  testWidgets('buyer: an older row without cargo reads it from the order',
      (tester) async {
    await pumpBuyerCard(tester, liveDelivery());

    expect(find.text('Wheat · 2,000 kg'), findsOneWidget);
    expect(find.text('Almaz Bekele · Dawit Tesfaye'), findsOneWidget);
  });
}
