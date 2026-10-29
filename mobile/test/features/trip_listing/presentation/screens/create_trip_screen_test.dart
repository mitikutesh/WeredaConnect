import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripListingRepository extends Mock
    implements BaseTripListingRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeTripListingEvent extends Fake implements TripListingEvent {}

class FakeTripListingState extends Fake implements TripListingState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

void main() {
  late MockAuthBloc authBloc;
  late MockTripListingBloc tripListingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeTripListingEvent());
    registerFallbackValue(FakeTripListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    tripListingBloc = MockTripListingBloc();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'trucker-001',
        tokens: _testTokens,
        role: UserRole.trucker,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<TripListingBloc>.value(value: tripListingBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: CreateTripScreen(
          truckerName: 'Test Trucker',
          truckerPhone: '+251911000000',
        ),
      ),
    );
  }

  void useTallPhone(WidgetTester tester) {
    tester.view.physicalSize = const Size(500, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  group('CreateTripScreen (single page, Trucker · Create trip board)', () {
    testWidgets('shows every trip field on one page with a pinned Save',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Create Trip'), findsOneWidget);
      expect(find.text('From (Origin)'), findsOneWidget);
      expect(find.text('To (Destination)'), findsOneWidget);
      expect(find.text('Departure Date'), findsOneWidget);
      expect(find.text('Capacity (quintals)'), findsOneWidget);
      expect(find.text('Price per Quintal (ETB)'), findsOneWidget);
      expect(find.text('Vehicle Plate'), findsOneWidget);
      expect(find.text('Vehicle Description'), findsOneWidget);
      expect(find.text('Save'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      expect(
        find.text('Saved on your phone first, sent when you have network.'),
        findsOneWidget,
      );
    });

    testWidgets('a profile-filled driver gets no name or phone fields',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Trucker Name'), findsNothing);
      expect(find.text('Test Trucker'), findsNothing);
    });

    testWidgets('a driver the profile could not fill gets the fields',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(
        MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<TripListingBloc>.value(value: tripListingBloc),
          ],
          child: const MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: CreateTripScreen(truckerName: 'Test Trucker'),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Trucker Name'), findsOneWidget);
      expect(find.text('Test Trucker'), findsOneWidget);
    });

    testWidgets('validates the route on save', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      expect(find.text('Origin woreda is required'), findsOneWidget);
      verifyNever(() => tripListingBloc.add(any(that: isA<CreateTrip>())));
    });

    testWidgets('shows same origin/destination error', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.enterText(
        find.widgetWithText(TextFormField, 'From (Origin)'),
        'Bishoftu',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'To (Destination)'),
        'Bishoftu',
      );

      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      expect(
        find.text('Origin and destination cannot be the same'),
        findsOneWidget,
      );
    });

    testWidgets('the swap button swaps From and To', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      final from = find.widgetWithText(TextFormField, 'From (Origin)');
      final to = find.widgetWithText(TextFormField, 'To (Destination)');
      await tester.enterText(from, 'Meki');
      await tester.enterText(to, 'Addis Ababa');
      await tester.tap(find.byKey(const Key('create_trip_swap')));
      await tester.pumpAndSettle();

      expect(
        tester.widget<TextFormField>(from).controller!.text,
        'Addis Ababa',
      );
      expect(tester.widget<TextFormField>(to).controller!.text, 'Meki');
      expect(find.byTooltip('Swap origin and destination'), findsOneWidget);
    });

    testWidgets('Repeat Last Trip fills the last route', (tester) async {
      useTallPhone(tester);
      final repo = MockTripListingRepository();
      when(() => tripListingBloc.repository).thenReturn(repo);
      when(() => repo.getRecentRoutes(any())).thenAnswer(
        (_) async => [(from: 'Meki', to: 'Addis Ababa')],
      );
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Repeat Last Trip · Meki → Addis Ababa'));
      await tester.pumpAndSettle();

      final from = find.widgetWithText(TextFormField, 'From (Origin)');
      final to = find.widgetWithText(TextFormField, 'To (Destination)');
      expect(tester.widget<TextFormField>(from).controller!.text, 'Meki');
      expect(tester.widget<TextFormField>(to).controller!.text, 'Addis Ababa');
    });

    testWidgets('validates capacity range on save', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Capacity (quintals)'),
        '9999',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Price per Quintal (ETB)'),
        '120',
      );

      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      expect(
        find.text('Capacity must be between 0.1 and 1000 quintals'),
        findsOneWidget,
      );
    });
  });

  group('route preview helpers', () {
    Place place(String id, double lat, double lng, {int population = 0}) =>
        Place(id: id, en: id, lat: lat, lng: lng, population: population);

    test('distance is the straight line in whole kilometres', () {
      // Meki to Addis Ababa: about 98 km in a straight line.
      final km = routeDistanceKm(
        place('meki', 8.15, 38.8167),
        place('addis', 9.025, 38.7469),
      );
      expect(km, inInclusiveRange(95, 100));
    });

    test('no distance without coordinates', () {
      expect(
        routeDistanceKm(const Place(id: 'x', en: 'x'), place('a', 9, 38)),
        isNull,
      );
    });

    test('towns on the way: near the line, between the ends, in order', () {
      final meki = place('meki', 8.15, 38.8167);
      final addis = place('addis', 9.025, 38.7469);
      final towns = townsOnTheWay(meki, addis, [
        place('dukem', 8.80, 38.90, population: 5000),
        place('mojo', 8.59, 39.12, population: 9000), // about 33 km off
        place('koka', 8.42, 38.78, population: 3000),
        place('assosa', 10.07, 34.53, population: 99999),
        meki,
        addis,
      ]);
      expect(towns.map((p) => p.id), ['koka', 'dukem']);
    });
  });
}
