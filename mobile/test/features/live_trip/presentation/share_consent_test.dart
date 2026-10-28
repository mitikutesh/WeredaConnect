import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/share_consent_listener.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/share_consent_sheet.dart';

import '../check_in_fakes.dart';
import 'live_trip_ui_fixtures.dart';

/// LT-09: "Share where the truck is?" after a trip's first pickup scan, once
/// per trip; "Not now, scans only" records `declined`.
void main() {
  group('consent sheet', () {
    Future<void> open(
      WidgetTester tester, {
      String locale = 'en',
      bool web = false,
    }) async {
      await pumpApp(
        tester,
        Builder(
          builder: (context) => Center(
            child: TextButton(
              onPressed: () => showModalBottomSheet<ShareConsentChoice>(
                context: context,
                isDismissible: false,
                isScrollControlled: true,
                builder: (_) => ShareConsentSheet(web: web),
              ),
              child: const Text('open'),
            ),
          ),
        ),
        locale: locale,
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
    }

    testWidgets('says who sees it, how often, when it stops and the data cost',
        (tester) async {
      await open(tester);
      final l10n = lookup('en');

      expect(find.text(l10n.liveTripConsentTitle), findsOneWidget);
      expect(find.text(l10n.liveTripConsentWhoText), findsOneWidget);
      expect(find.text(l10n.liveTripConsentOftenText), findsOneWidget);
      expect(find.text(l10n.liveTripConsentStopsText), findsOneWidget);
      expect(find.textContaining('170 KB'), findsOneWidget);
      expect(find.text(l10n.liveTripConsentAndroidNote), findsOneWidget);
      expect(find.text(l10n.liveTripConsentShare), findsOneWidget);
      expect(find.text(l10n.liveTripConsentDecline), findsOneWidget);
    });

    testWidgets('web says check-ins go only while the page is open',
        (tester) async {
      await open(tester, web: true);
      final l10n = lookup('en');

      expect(find.text(l10n.liveTripConsentWebNote), findsOneWidget);
      expect(find.text(l10n.liveTripConsentAndroidNote), findsNothing);
    });

    testWidgets('cannot be dismissed: back does not close it', (tester) async {
      await open(tester);

      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();

      expect(find.byType(ShareConsentSheet), findsOneWidget);
    });

    for (final code in ['am', 'om']) {
      testWidgets('$code: the sheet is translated', (tester) async {
        await open(tester, locale: code);
        final l10n = lookup(code);

        expect(find.text(l10n.liveTripConsentTitle), findsOneWidget);
        expect(find.text(l10n.liveTripConsentShare), findsOneWidget);
        expect(find.text(l10n.liveTripConsentDecline), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  });

  group('consent listener', () {
    late EngineRig rig;

    // Each test builds its engine in its own body: the engine's queue must
    // live in the widget test's fake async zone (one built in setUp never
    // runs there). It runs on fake timers and is not disposed.

    Future<void> pumpListener(WidgetTester tester) => pumpApp(
          tester,
          ShareConsentListener(
            engine: rig.engine,
            settle: Duration.zero,
            child: const Scaffold(body: Text('home')),
          ),
        );

    Future<void> scan(WidgetTester tester) async {
      await rig.engine.onScan(tripId);
      await tester.pumpAndSettle();
    }

    testWidgets('the first pickup scan asks; Share starts sharing',
        (tester) async {
      rig = await EngineRig.signedIn();
      await pumpListener(tester);
      final l10n = lookup('en');

      await scan(tester);
      expect(find.byType(ShareConsentSheet), findsOneWidget);

      await tester.tap(find.byKey(const Key('consent_share')));
      await tester.pumpAndSettle();

      expect(find.byType(ShareConsentSheet), findsNothing);
      expect(rig.engine.sharingFor(tripId)?.answer, SharingAnswer.shared);
      expect(rig.pings.fixes.single.source, CheckInSource.start);
      expect(rig.engine.status.mode, SharingMode.live);
      expect(find.text(l10n.liveTripSharingStarted), findsOneWidget);
      expect(find.text(l10n.tripModeOpen), findsOneWidget);
    });

    testWidgets('Not now, scans only records declined and is not asked again',
        (tester) async {
      rig = await EngineRig.signedIn();
      await pumpListener(tester);
      final l10n = lookup('en');

      await scan(tester);
      await tester.tap(find.byKey(const Key('consent_scans_only')));
      await tester.pumpAndSettle();

      expect(rig.engine.sharingFor(tripId)?.answer, SharingAnswer.declined);
      expect(rig.pings.events.single.kind, PingKind.declined);
      expect(rig.location.running, isFalse);
      expect(find.text(l10n.liveTripScansOnlyChosen), findsOneWidget);

      // The trip's next pickup scan records its place without asking.
      await scan(tester);
      expect(find.byType(ShareConsentSheet), findsNothing);
      expect(rig.pings.fixes.single.source, CheckInSource.scan);
    });

    testWidgets('"Always share" in Settings shares without the sheet',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.setSharingDefault(SharingDefault.share);
      await pumpListener(tester);

      await scan(tester);

      expect(find.byType(ShareConsentSheet), findsNothing);
      expect(rig.engine.sharingFor(tripId)?.answer, SharingAnswer.shared);
    });

    testWidgets('a farmer or a buyer is never asked (no trucker session)',
        (tester) async {
      rig = await EngineRig.signedIn();
      await rig.engine.signOut();
      await pumpListener(tester);

      await scan(tester);

      expect(find.byType(ShareConsentSheet), findsNothing);
      expect(rig.pings.rows, isEmpty);
    });

    testWidgets('a second trip asks again', (tester) async {
      rig = await EngineRig.signedIn();
      rig.trips.set('trip-2', [leg('d-9', DeliveryStatus.pickedUp)]);
      await pumpListener(tester);

      await scan(tester);
      await tester.tap(find.byKey(const Key('consent_scans_only')));
      await tester.pumpAndSettle();
      await rig.engine.onScan('trip-2');
      await tester.pumpAndSettle();

      expect(find.byType(ShareConsentSheet), findsOneWidget);
    });
  });
}
