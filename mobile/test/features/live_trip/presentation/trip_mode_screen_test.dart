import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_mode_screen.dart';

import 'live_trip_ui_fixtures.dart';

/// LT-09: trip mode, the trucker's own screen for the trip on the road, in
/// each sharing state.
void main() {
  late EngineRig rig;

  // Each test builds its engine in its own body: the engine's queue must
  // live in the widget test's fake async zone (one built in setUp never
  // runs there). It runs on fake timers and is not disposed.

  // 05:00 UTC, 08:00 in Ethiopia.
  final pickedUpAt = startTime.subtract(const Duration(hours: 1));

  List<Delivery> oneOnBoard() => [
        deliveryFixture(
          'd-1',
          status: DeliveryStatus.pickedUp,
          farmer: 'Almaz Bekele',
          buyer: 'Hawi Flour Mill',
          pickedUpAt: pickedUpAt,
        ),
        deliveryFixture(
          'd-2',
          status: DeliveryStatus.pending,
          farmer: 'Tolosa Gemechu',
          buyer: 'Selam Traders',
        ),
      ];

  List<Order> orders() => [
        orderFixture('d-1', crop: 'wheat', kg: 2000, transportCost: 3000),
        orderFixture('d-2', crop: 'maize', kg: 1000, transportCost: 1500),
      ];

  Future<FakeConnectivity> pump(
    WidgetTester tester, {
    List<Delivery>? deliveries,
    bool online = true,
    bool failLoad = false,
    String locale = 'en',
    TripStopAction? onScanPickup,
    TripStopAction? onOpenDelivery,
  }) async {
    final net = FakeConnectivity(online: online);
    await pumpApp(
      tester,
      TripModeScreen(
        engine: rig.engine,
        loader: loaderFor(
          deliveries: deliveries ?? oneOnBoard(),
          orders: orders(),
          failDeliveries: failLoad,
        ),
        connectivity: net,
        now: () => rig.clock.now,
        onScanPickup: onScanPickup ?? (_, __) async {},
        onOpenDelivery: onOpenDelivery ?? (_, __) async {},
      ),
      locale: locale,
    );
    await tester.pumpAndSettle();
    return net;
  }

  group('sharing', () {
    testWidgets('shows the trip, the last and next check-in, stops and pay',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester);
      final l10n = lookup('en');

      expect(
        find.byKey(const Key('trip_mode_sharing_live')),
        findsOneWidget,
      );
      expect(find.text('Meki → Addis Ababa'), findsOneWidget);
      expect(find.text(l10n.tripModeSharingLive), findsOneWidget);
      expect(
        find.text('${l10n.tripModeLastCheckIn('09:00')} · '
            '${l10n.tripModeNextBy('10:00')}'),
        findsOneWidget,
      );
      expect(find.text(l10n.tripModePrivacy), findsOneWidget);

      // The next stop is the pickup still waiting.
      // The next stop label is set in capitals (the board).
      expect(
        find.text(l10n.tripModeNextPickup.toUpperCase()),
        findsOneWidget,
      );
      expect(find.byKey(const Key('trip_mode_scan')), findsOneWidget);
      expect(find.byKey(const Key('trip_mode_arrived')), findsOneWidget);

      // The stop list names every party; the scanned pickup has its time.
      expect(
        find.text(l10n.tripModeStopPickup('Almaz Bekele')),
        findsOneWidget,
      );
      expect(
        find.text('${l10n.cropTypeWheat} · ${l10n.quantityKg('2,000')}'
            ' · ${l10n.tripModeScannedAt('08:00')}'),
        findsOneWidget,
      );
      expect(
        find.text(l10n.tripModeStopDropoff('Hawi Flour Mill, Addis Ababa')),
        findsOneWidget,
      );
      expect(
        find.text(l10n.tripModeStopDropoff('Selam Traders, Addis Ababa')),
        findsOneWidget,
      );
      expect(find.text(l10n.tripModeNextLabel), findsOneWidget);

      expect(find.text(l10n.etbCurrency('4,500')), findsOneWidget);
      expect(find.byKey(const Key('trip_mode_offline')), findsNothing);
    });

    testWidgets('Check in now records a manual fix', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester);
      final l10n = lookup('en');

      await tester.tap(find.byKey(const Key('trip_mode_check_in')));
      await tester.pumpAndSettle();

      expect(rig.pings.fixes.last.source, CheckInSource.manual);
      expect(find.text(l10n.tripModeCheckInSaved), findsOneWidget);
    });

    testWidgets('no fix in time says so', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      rig.location.answers.add(null);
      await pump(tester);
      final l10n = lookup('en');

      await tester.tap(find.byKey(const Key('trip_mode_check_in')));
      await tester.pumpAndSettle();

      expect(rig.pings.fixes, hasLength(1));
      expect(find.text(l10n.tripModeNoFix), findsOneWidget);
    });

    testWidgets("I've arrived records an arrived fix", (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester);

      await tester.tap(find.byKey(const Key('trip_mode_arrived')));
      await tester.pumpAndSettle();

      expect(rig.pings.fixes.last.source, CheckInSource.arrived);
    });

    testWidgets('Pause opens the sheet; the pause shows its end; Resume',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester);
      final l10n = lookup('en');

      await tester.tap(find.byKey(const Key('trip_mode_pause')));
      await tester.pumpAndSettle();
      expect(find.text(l10n.pauseSheetTitle), findsOneWidget);
      await tester.tap(find.byKey(const Key('pause_confirm')));
      await tester.pumpAndSettle();

      expect(rig.engine.status.mode, SharingMode.paused);
      expect(
        rig.engine.status.pausedUntil,
        startTime.add(const Duration(minutes: 30)),
      );
      expect(find.byKey(const Key('trip_mode_sharing_paused')), findsOneWidget);
      expect(find.text(l10n.tripModePausedUntil('09:30')), findsOneWidget);
      // Check in now still works while paused.
      expect(find.byKey(const Key('trip_mode_check_in')), findsOneWidget);

      await tester.tap(find.byKey(const Key('trip_mode_resume')));
      await tester.pumpAndSettle();

      expect(rig.engine.status.mode, SharingMode.live);
      expect(rig.pings.events.last.kind, PingKind.resumed);
    });

    testWidgets('Keep sharing leaves sharing on', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester);

      await tester.tap(find.byKey(const Key('trip_mode_pause')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('pause_keep')));
      await tester.pumpAndSettle();

      expect(rig.engine.status.mode, SharingMode.live);
      expect(rig.pings.events, isEmpty);
    });

    testWidgets('offline: how many check-ins wait on the phone',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await rig.engine.checkInNow();
      final net = await pump(tester, online: false);
      final l10n = lookup('en');

      expect(find.byKey(const Key('trip_mode_offline')), findsOneWidget);
      expect(find.text(l10n.tripModeOfflineTitle), findsOneWidget);
      expect(find.text(l10n.tripModeOfflineWaiting(2)), findsOneWidget);

      net.set(online: true);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('trip_mode_offline')), findsNothing);
    });

    testWidgets('a pickup opens the QR scan; a drop-off opens the delivery',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      final scanned = <String>[];
      await pump(
        tester,
        onScanPickup: (_, d) async => scanned.add(d.deliveryId),
      );
      await tester.tap(find.byKey(const Key('trip_mode_scan')));
      await tester.pumpAndSettle();
      expect(scanned, ['d-2']);
    });

    testWidgets('with every load on the truck the next stop is a drop-off',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      final opened = <String>[];
      final deliveries = oneOnBoard();
      deliveries[1]
        ..status = DeliveryStatus.inTransit
        ..pickedUpAt = pickedUpAt.add(const Duration(minutes: 40));
      await pump(
        tester,
        deliveries: deliveries,
        onOpenDelivery: (_, d) async => opened.add(d.deliveryId),
      );
      final l10n = lookup('en');

      expect(
        find.text(l10n.tripModeNextDropoff.toUpperCase()),
        findsOneWidget,
      );
      expect(find.text('Hawi Flour Mill, Addis Ababa'), findsOneWidget);
      expect(find.byKey(const Key('trip_mode_arrived')), findsOneWidget);

      await tester.tap(find.byKey(const Key('trip_mode_open')));
      await tester.pumpAndSettle();
      expect(opened, ['d-1']);
    });

    testWidgets('Amharic: times on the Ethiopian clock', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester, locale: 'am');
      final l10n = lookup('am');

      expect(
        find.text('${l10n.tripModeLastCheckIn('ከጠዋቱ 3:00')} · '
            '${l10n.tripModeNextBy('ከጠዋቱ 4:00')}'),
        findsOneWidget,
      );
      expect(find.text(l10n.tripModeCheckInNow), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('Afaan Oromo: times on the Ethiopian clock', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester, locale: 'om');
      final l10n = lookup('om');

      expect(
        find.text('${l10n.tripModeLastCheckIn('3:00 ganama')} · '
            '${l10n.tripModeNextBy('4:00 ganama')}'),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('the stops that cannot be read offer a retry', (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      await pump(tester, failLoad: true);
      final l10n = lookup('en');

      expect(find.text(l10n.tripModeLoadFailed), findsOneWidget);
      expect(find.text(l10n.retry), findsOneWidget);
      // The sharing card still works without the stops.
      expect(find.byKey(const Key('trip_mode_check_in')), findsOneWidget);
    });
  });

  testWidgets('scans only: no check-ins; Share trip location starts sharing',
      (tester) async {
    rig = await EngineRig.signedIn();
    await rig.engine.decline(tripId);
    await pump(tester);
    final l10n = lookup('en');

    expect(find.byKey(const Key('trip_mode_sharing_declined')), findsOneWidget);
    expect(find.text(l10n.tripModeScansOnlyText), findsOneWidget);
    expect(find.byKey(const Key('trip_mode_check_in')), findsNothing);
    expect(find.byKey(const Key('trip_mode_arrived')), findsNothing);
    // The next pickup's scan still works.
    expect(find.byKey(const Key('trip_mode_scan')), findsOneWidget);

    await tester.tap(find.byKey(const Key('trip_mode_share')));
    await tester.pumpAndSettle();

    expect(rig.engine.sharingFor(tripId)?.answer, SharingAnswer.shared);
    expect(find.byKey(const Key('trip_mode_sharing_live')), findsOneWidget);
    expect(find.text(l10n.liveTripSharingStarted), findsOneWidget);
  });

  testWidgets('ended: sharing stopped, no actions', (tester) async {
    rig = await EngineRig.signedIn();
    await rig.engine.start(tripId);
    await rig.engine.onSharingEnded(tripId);
    await pump(tester);
    final l10n = lookup('en');

    expect(find.byKey(const Key('trip_mode_sharing_ended')), findsOneWidget);
    expect(find.text(l10n.tripModeEndedText), findsOneWidget);
    expect(find.byKey(const Key('trip_mode_check_in')), findsNothing);
    expect(find.byKey(const Key('trip_mode_pause')), findsNothing);
  });

  testWidgets('no trip on the road: says when trip mode starts',
      (tester) async {
    rig = await EngineRig.signedIn();
    await pump(tester);
    final l10n = lookup('en');

    expect(find.byKey(const Key('trip_mode_sharing_off')), findsOneWidget);
    expect(find.text(l10n.tripModeNoTrip), findsOneWidget);
    expect(find.text(l10n.tripModeStops), findsNothing);
  });
}
