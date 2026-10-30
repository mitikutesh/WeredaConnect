import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_log_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';

import '../../live_trip_fixtures.dart';
import '../../live_trip_harness.dart';

/// LT-11: the buyer's live trip in every state, and nobody else gets it.
void main() {
  late LiveTripDeps deps;
  late FakeTrackRepository tracks;

  setUpAll(mockScannerChannels);

  setUp(() {
    deps = LiveTripDeps(order: liveOrder());
    tracks = FakeTrackRepository(track: withEta(corridorTrack()));
  });

  Future<void> pumpView(
    WidgetTester tester, {
    Delivery? delivery,
    String userId = buyerId,
    Locale locale = const Locale('en'),
    DateTime Function()? clock,
  }) async {
    // A tall phone, so the whole sheet is built.
    tester.view.physicalSize = const Size(420, 2400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        locale: locale,
        home: Scaffold(
          body: LiveTripView(
            delivery: delivery ?? liveDelivery(),
            currentUserId: userId,
            places: corridorPlaces,
            clock: clock ?? fixedNow,
          ),
        ),
      ),
    );
    await settleReads(tester);
  }

  // Buyer · Live trip board: the driver card names the truck. The buyer may
  // browse every trip anyway; nobody else gets the read.
  group('driver card', () {
    Trip vehicleTrip() => Trip()
      ..tripId = 'trip-001'
      ..truckerId = truckerId
      ..truckerName = 'Dawit Tesfaye'
      ..fromWoreda = 'Meki'
      ..toWoreda = 'Addis Ababa'
      ..departureDate = DateTime.utc(2026, 10, 27)
      ..capacity = 50
      ..pricePerQuintal = 150
      ..vehicleDescription = 'Isuzu FSR 5 t'
      ..vehiclePlate = '3-AA-12345'
      ..status = TripStatus.inTransit
      ..matchedListings = <String>[]
      ..createdAt = DateTime.utc(2026, 10, 25)
      ..updatedAt = DateTime.utc(2026, 10, 27)
      ..version = 1;

    testWidgets('the buyer sees the truck and its plate', (tester) async {
      deps.trip = vehicleTrip();
      await pumpView(tester);

      expect(find.text('Isuzu FSR 5 t · 3-AA-12345'), findsOneWidget);
    });

    testWidgets('without a vehicle the card says Trucker', (tester) async {
      await pumpView(tester);

      expect(find.text('Isuzu FSR 5 t · 3-AA-12345'), findsNothing);
      expect(find.text('Trucker'), findsWidgets);
    });

    testWidgets('a viewer who is not the buyer reads no trip', (tester) async {
      deps.trip = vehicleTrip();
      await pumpView(tester, userId: farmerId);

      verifyNever(() => deps.tripRepository.getTripById(any()));
    });
  });

  group('buyer on the road', () {
    testWidgets('the map, the last check-in, the window and the escrow line',
        (tester) async {
      await pumpView(tester);
      expect(find.byType(TrackMap), findsOneWidget);
      expect(find.text('11:42 · Mojo'), findsOneWidget);
      expect(find.text('28 min ago'), findsOneWidget);
      expect(find.text('13:20-14:00'), findsOneWidget);
      expect(
        find.text('About 47.2 km to go, from the last check-in'),
        findsOneWidget,
      );
      expect(find.text('Wheat · 2,000 kg'), findsOneWidget);
      expect(
        find.text(
          "Your 91,200 ETB stays in escrow until you scan the driver's QR.",
        ),
        findsOneWidget,
      );
      expect(find.text('Dawit Tesfaye'), findsOneWidget);
      expect(find.text('4 check-ins'), findsOneWidget);
      expect(tracks.readers, [buyerId]);
    });

    testWidgets('no arrival window yet says so', (tester) async {
      tracks.track = corridorTrack();
      await pumpView(tester);
      expect(find.text('No arrival estimate yet'), findsOneWidget);
    });

    testWidgets('late: likely no network, check-ins wait on the phone',
        (tester) async {
      await pumpView(tester, clock: () => at(10, 5));
      expect(find.text('No check-in for 1 h 23 min'), findsOneWidget);
      expect(
        find.textContaining("Check-ins saved on Dawit Tesfaye's phone"),
        findsOneWidget,
      );
      expect(
        find.text('Wider window: no check-in for a while'),
        findsOneWidget,
      );
    });

    testWidgets('paused: when sharing turns back on', (tester) async {
      tracks.track = corridorTrack(
        sharing: TrackSharing.paused,
        pausedUntil: at(9, 40),
      );
      await pumpView(tester);
      expect(
        find.text(
          'Dawit Tesfaye paused sharing for a rest stop. It turns back on by '
          'itself at 12:40.',
        ),
        findsOneWidget,
      );
      expect(find.text('Paused · Mojo'), findsOneWidget);
    });

    testWidgets('scans only: the QR scans still record the handoffs',
        (tester) async {
      tracks.track = corridorTrack(sharing: TrackSharing.declined);
      await pumpView(tester);
      expect(
        find.textContaining("Dawit Tesfaye chose not to share the truck's"),
        findsOneWidget,
      );
    });

    testWidgets('offline: the saved map with its time, never a blank',
        (tester) async {
      tracks
        ..offline = true
        ..stored = TrackSnapshot(
          track: withEta(corridorTrack()),
          fetchedAt: at(8, 58),
        );
      await pumpView(tester);
      expect(
        find.text(
          'You are offline. This is the map from 11:58, it updates when you '
          'reconnect.',
        ),
        findsOneWidget,
      );
      expect(find.byType(TrackMap), findsOneWidget);
    });

    testWidgets('offline with nothing saved still shows the trip',
        (tester) async {
      tracks
        ..offline = true
        ..track = null;
      await pumpView(tester);
      expect(
        find.text('You are offline. The map shows when you reconnect.'),
        findsOneWidget,
      );
      expect(find.byType(TrackMap), findsNothing);
      expect(find.text('Dawit Tesfaye'), findsOneWidget);
      expect(find.text('Wheat · 2,000 kg'), findsOneWidget);
    });

    testWidgets('the trip log opens with the same read', (tester) async {
      await pumpView(tester);
      await tester.ensureVisible(find.byKey(const Key('live_trip_log_link')));
      await tester.tap(find.byKey(const Key('live_trip_log_link')));
      await tester.pumpAndSettle();
      expect(find.byType(TripLogScreen), findsOneWidget);
      expect(tracks.fetches, 1);
    });

    testWidgets('am: the window on the Ethiopian clock with the 24-hour time',
        (tester) async {
      await pumpView(tester, locale: const Locale('am'));
      expect(find.text('ከቀኑ 7:20-8:00 (13:20-14:00)'), findsOneWidget);
      expect(find.text('ከጠዋቱ 5:42 · ሞጆ'), findsOneWidget);
    });
  });

  group('before pickup', () {
    testWidgets('the map starts at pickup: no map, no trail', (tester) async {
      tracks.track = DeliveryTrack(
        deliveryId: 'del-101',
        sharing: TrackSharing.notStarted,
        asOf: asOf,
      );
      await pumpView(
        tester,
        delivery: liveDelivery(status: DeliveryStatus.pending),
      );
      expect(
        find.text("The map appears when the driver scans the farmer's QR."),
        findsOneWidget,
      );
      expect(find.byType(TrackMap), findsNothing);
      expect(find.byKey(const Key('live_trip_log_link')), findsNothing);
    });
  });

  // Spec section 4: map hero plus bottom sheet.
  testWidgets('on the road the map runs edge to edge above the sheet',
      (tester) async {
    await pumpView(tester);

    final map = find.byType(LiveTripMap);
    expect(tester.widget<LiveTripMap>(map).borderRadius, 0);
    expect(tester.getSize(map).width, 420);
    expect(
      tester.getTopLeft(find.text('Wheat · 2,000 kg')).dy,
      greaterThan(tester.getBottomLeft(map).dy - 24),
    );
    // A phone has no room for the check-ins beside the details.
    expect(find.byKey(const Key('live_trip_check_ins_card')), findsNothing);
  });

  // Buyer · Live deliveries board (web): the check-ins beside the details.
  testWidgets('wide: the check-ins sit beside the details', (tester) async {
    await pumpView(tester);
    tester.view.physicalSize = const Size(900, 1800);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    final card = find.byKey(const Key('live_trip_check_ins_card'));
    expect(card, findsOneWidget);
    expect(
      find.descendant(
        of: card,
        matching: find.text('Check-ins about every hour'),
      ),
      findsOneWidget,
    );
    final details = find.text('Wheat · 2,000 kg');
    final left = tester.getTopLeft(details).dx;
    expect(tester.getTopLeft(card).dx, greaterThan(left));
    expect(
      tester.getTopLeft(card).dy,
      lessThan(tester.getTopLeft(details).dy + 40),
    );
  });

  group('arriving', () {
    testWidgets('within 5 km: the checklist and Scan driver\'s QR',
        (tester) async {
      tracks.track = arrivingTrack();
      await pumpView(tester);
      expect(find.text('Dawit Tesfaye is about 10 min away'), findsOneWidget);
      expect(find.text('Before you scan'), findsOneWidget);
      expect(find.text('Check the crop is dry and clean'), findsOneWidget);
      expect(find.text('Arriving'), findsOneWidget);

      final scan = find.byKey(const Key('live_trip_scan_qr'));
      await tester.ensureVisible(scan);
      await tester.tap(scan);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      final screen = tester.widget<QrScanScreen>(find.byType(QrScanScreen));
      expect(screen.verificationType, VerificationType.delivery);
      expect(screen.delivery.deliveryId, 'delivery-101');
    });

    testWidgets('the truck_nearby inbox entry also means arriving',
        (tester) async {
      deps.inbox = [truckNearbyEntry('delivery-101')];
      await pumpView(tester);
      expect(find.text("Scan driver's QR"), findsOneWidget);
      expect(
        find.text('Dawit Tesfaye is about 70 min away'),
        findsOneWidget,
      );
    });

    testWidgets('far away there is no scan button yet', (tester) async {
      await pumpView(tester);
      expect(find.text("Scan driver's QR"), findsNothing);
    });
  });

  group('delivered', () {
    testWidgets('the summary, escrow, report a problem and the ratings',
        (tester) async {
      deps.escrow = heldEscrow();
      tracks.track = DeliveryTrack.fromJson(endedTrackJson());
      await pumpView(
        tester,
        delivery: liveDelivery(
          status: DeliveryStatus.delivered,
          confirmedAt: at(11, 2),
        ),
        clock: () => at(12, 0),
      );
      expect(find.text('Sharing ended at delivery'), findsOneWidget);
      expect(find.text('Delivered at 14:02'), findsOneWidget);
      expect(find.text('96.4 km'), findsOneWidget);
      expect(find.text('5 h 35 min'), findsOneWidget);
      expect(find.text('7'), findsOneWidget);
      expect(find.byType(TrackMap), findsNothing);
      // Buyer · Delivered board: one escrow card while the money waits.
      final check = find.byKey(const Key('live_trip_delivered_check'));
      expect(check, findsOneWidget);
      final card = find.byKey(const Key('live_trip_escrow_card'));
      expect(card, findsOneWidget);
      expect(find.byType(EscrowTimeline), findsNothing);
      Finder inCard(String text) =>
          find.descendant(of: card, matching: find.text(text));
      expect(inCard('91,200 ETB in escrow'), findsOneWidget);
      expect(inCard('Release pending'), findsOneWidget);
      expect(inCard('You confirmed delivery'), findsOneWidget);
      expect(inCard('Check window ends'), findsOneWidget);
      expect(
        inCard('Almaz Bekele gets 84,000 ETB · Dawit Tesfaye gets 3,000 ETB'),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: card,
          matching: find.text('Report a problem until Oct 29 14:02'),
        ),
        findsOneWidget,
      );
      expect(find.text('Rate Farmer'), findsOneWidget);
      expect(find.text('Rate Trucker'), findsOneWidget);
    });

    testWidgets('a released escrow keeps the full timeline', (tester) async {
      deps.escrow = heldEscrow()..status = PaymentStatus.released;
      tracks.track = DeliveryTrack.fromJson(endedTrackJson());
      await pumpView(
        tester,
        delivery: liveDelivery(
          status: DeliveryStatus.delivered,
          confirmedAt: at(11, 2),
        ),
        clock: () => DateTime.utc(2026, 10, 30),
      );
      expect(find.byType(EscrowTimeline), findsOneWidget);
      expect(find.byKey(const Key('live_trip_escrow_card')), findsNothing);
    });

    testWidgets('after the 48 h window there is no report button',
        (tester) async {
      tracks.track = DeliveryTrack.fromJson(endedTrackJson());
      await pumpView(
        tester,
        delivery: liveDelivery(
          status: DeliveryStatus.delivered,
          confirmedAt: at(11, 2),
        ),
        clock: () => DateTime.utc(2026, 10, 30),
      );
      expect(find.byKey(const Key('live_trip_report_problem')), findsNothing);
      expect(find.text('Rate Farmer'), findsOneWidget);
    });
  });

  group('roles', () {
    testWidgets('the trucker gets no buyer map and no read', (tester) async {
      await pumpView(tester, userId: truckerId);
      expect(find.byType(TrackMap), findsNothing);
      expect(
        find.text('You are not authorized to view this order.'),
        findsOneWidget,
      );
      expect(tracks.fetches, 0);
    });

    testWidgets('the farmer gets their own view, not this one', (tester) async {
      await pumpView(tester, userId: farmerId);
      expect(find.byType(TrackMap), findsNothing);
      expect(tracks.fetches, 0);
    });
  });

  testWidgets('LiveTripScreen wraps the view with its title', (tester) async {
    await tester.pumpWidget(
      liveTripApp(
        deps: deps,
        tracks: tracks,
        home: LiveTripScreen(
          delivery: liveDelivery(),
          currentUserId: buyerId,
          places: corridorPlaces,
          clock: fixedNow,
        ),
      ),
    );
    await settleReads(tester);
    expect(find.text('Live trip'), findsOneWidget);
    expect(find.byType(TrackMap), findsOneWidget);
  });
}
