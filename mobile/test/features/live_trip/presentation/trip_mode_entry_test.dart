import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_mode_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/sharing_default_tile.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

import 'live_trip_ui_fixtures.dart';

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockOrderBloc extends Mock implements OrderBloc {}

/// LT-09: the ways into trip mode and the sharing default: the trucker
/// home's trip card and Settings > Trip location. A farmer or a buyer has no trucker
/// session on the engine, so neither shows for them.
///
/// Each test builds its engine in its own body: the engine's queue must live
/// in the widget test's fake async zone. It runs on fake timers and is not
/// disposed.
void main() {
  group('trucker home trip card: the way into trip mode', () {
    late MockTripRepository tripRepo;
    late MockTripListingBloc trips;

    setUp(() {
      tripRepo = MockTripRepository();
      trips = MockTripListingBloc();
      when(() => trips.repository).thenReturn(tripRepo);
      when(() => trips.stream).thenAnswer((_) => const Stream.empty());
      when(() => tripRepo.getTripById(tripId)).thenAnswer(
        (_) async => tripFixture()..status = TripStatus.inTransit,
      );
      when(() => tripRepo.getMostRelevantTrip(trucker)).thenAnswer(
        (_) async => tripFixture()..status = TripStatus.inTransit,
      );
      when(() => tripRepo.getRecentRoutes(trucker))
          .thenAnswer((_) async => const []);
    });

    Future<AppLocalizations> pumpCard(
      WidgetTester tester,
      CheckInEngine? engine, {
      Widget Function(Widget app)? above,
    }) async {
      final l10n = await pumpApp(
        tester,
        Scaffold(body: TruckerHeroCard(userId: trucker, engine: engine)),
        above: (app) => BlocProvider<TripListingBloc>.value(
          value: trips,
          child: above == null ? app : above(app),
        ),
      );
      await tester.pumpAndSettle();
      return l10n;
    }

    testWidgets('before the first pickup scan: the trip, no sharing line',
        (tester) async {
      final rig = await EngineRig.signedIn();
      final l10n = await pumpCard(tester, rig.engine);

      expect(find.text('Meki → Addis Ababa'), findsOneWidget);
      expect(find.text(l10n.tripModeSharingLive), findsNothing);
    });

    testWidgets('shows the sharing state and follows it', (tester) async {
      final rig = await EngineRig.signedIn();
      final l10n = await pumpCard(tester, rig.engine);

      await rig.engine.start(tripId);
      await tester.pumpAndSettle();
      expect(find.text(l10n.tripModeSharingLive), findsOneWidget);

      await rig.engine.pause(startTime.add(const Duration(minutes: 30)));
      await tester.pumpAndSettle();
      expect(find.text('${l10n.liveTripPaused} · 09:30'), findsOneWidget);

      await rig.engine.onSharingEnded(tripId);
      await tester.pumpAndSettle();
      expect(find.text(l10n.tripModeSharingLive), findsNothing);
      expect(find.text('Meki → Addis Ababa'), findsOneWidget);
    });

    testWidgets('scans only is shown too', (tester) async {
      final rig = await EngineRig.signedIn();
      await rig.engine.decline(tripId);
      final l10n = await pumpCard(tester, rig.engine);

      expect(find.text(l10n.tripModeScansOnlyTitle), findsOneWidget);
    });

    testWidgets('no engine (a bare harness): the trip, no sharing line',
        (tester) async {
      final l10n = await pumpCard(tester, null);

      expect(find.text('Meki → Addis Ababa'), findsOneWidget);
      expect(find.text(l10n.tripModeSharingLive), findsNothing);
    });

    testWidgets('a tap opens trip mode on the app engine', (tester) async {
      final rig = await EngineRig.signedIn();
      await rig.engine.start(tripId);
      final deliveryRepo = MockDeliveryRepository();
      when(() => deliveryRepo.getDeliveriesByTrip(tripId, userId: trucker))
          .thenAnswer((_) async => const []);
      when(() => deliveryRepo.getDeliveriesByUser(trucker, role: 'trucker'))
          .thenAnswer((_) async => const []);
      final deliveries = MockDeliveryBloc();
      final orders = MockOrderBloc();
      when(() => deliveries.repository).thenReturn(deliveryRepo);
      when(() => orders.repository).thenReturn(MockOrderRepository());
      // BlocProvider listens to the blocs once they are read.
      when(() => deliveries.stream).thenAnswer((_) => const Stream.empty());
      when(() => orders.stream).thenAnswer((_) => const Stream.empty());

      await pumpCard(
        tester,
        null,
        above: (app) => Provider<CheckInEngine?>.value(
          value: rig.engine,
          child: MultiBlocProvider(
            providers: [
              BlocProvider<DeliveryBloc>.value(value: deliveries),
              BlocProvider<OrderBloc>.value(value: orders),
            ],
            child: app,
          ),
        ),
      );
      await tester.tap(find.byKey(const Key('trucker_hero_card')));
      await tester.pumpAndSettle();

      expect(find.byType(TripModeScreen), findsOneWidget);
      expect(find.byKey(const Key('trip_mode_sharing_live')), findsOneWidget);
    });
  });

  group('Settings > Trip location', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));

    Future<void> pumpSettings(
      WidgetTester tester,
      CheckInEngine? engine,
    ) async {
      tester.view.physicalSize = const Size(420, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        MultiProvider(
          providers: [
            ChangeNotifierProvider.value(value: LanguageProvider()),
            ChangeNotifierProvider.value(value: SettingsProvider()),
            Provider<CheckInEngine?>.value(value: engine),
          ],
          child: const MaterialApp(
            locale: Locale('en'),
            localizationsDelegates: [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: SettingsScreen(),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('a trucker picks the default; it applies at once',
        (tester) async {
      final rig = await EngineRig.signedIn();
      await pumpSettings(tester, rig.engine);
      final l10n = lookup('en');

      final tile = find.byKey(const Key('settings_trip_location_tile'));
      expect(tile, findsOneWidget);
      expect(find.text(l10n.sharingDefaultAsk), findsOneWidget);

      await tester.tap(tile);
      await tester.pumpAndSettle();
      expect(find.text(l10n.sharingDefaultHint), findsOneWidget);
      for (final value in SharingDefault.values) {
        expect(
          find.byKey(Key('sharing_default_${value.name}')),
          findsOneWidget,
        );
      }
      await tester.tap(find.byKey(const Key('sharing_default_share')));
      await tester.pumpAndSettle();

      expect(rig.engine.sharingDefault, SharingDefault.share);
      expect(find.byType(SharingDefaultDialog), findsNothing);
      expect(find.text(l10n.sharingDefaultShare), findsOneWidget);
    });

    testWidgets('a farmer or a buyer has no such row', (tester) async {
      final rig = await EngineRig.signedIn(userId: null);
      await pumpSettings(tester, rig.engine);

      expect(
        find.byKey(const Key('settings_trip_location_tile')),
        findsNothing,
      );
      expect(find.byKey(const Key('settings_clock_tile')), findsOneWidget);
    });

    testWidgets('without an engine the screen is as before', (tester) async {
      await pumpSettings(tester, null);

      expect(
        find.byKey(const Key('settings_trip_location_tile')),
        findsNothing,
      );
    });
  });
}
