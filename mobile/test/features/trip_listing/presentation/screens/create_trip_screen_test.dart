import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
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
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

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
      expect(
        find.text('Buyers can book until 24 h before you leave.'),
        findsOneWidget,
      );
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

    // The route marker sits in a fixed slot: unsized, it stretched over the
    // whole empty field and hid the input and its hint.
    testWidgets('empty From and To keep their input and hint beside the marker',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      for (final key in const ['create_trip_from', 'create_trip_to']) {
        final field = find.descendant(
          of: find.byKey(Key(key)),
          matching: find.byType(EditableText),
        );
        expect(tester.getSize(field).width, greaterThan(150));
      }
      expect(find.text('Search woreda or town'), findsNWidgets(2));
    });

    // The board's grid: two fields per row, labels above. A screen reader
    // hears each label as its field's, and a label that wraps (Afaan Oromoo)
    // keeps the pair lined up.
    testWidgets('capacity, price, plate and vehicle sit two per row',
        (tester) async {
      tester.view.physicalSize = const Size(390, 1800);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      Rect at(String key) => tester.getRect(find.byKey(Key(key)));
      expect(at('create_trip_capacity').top, at('create_trip_price').top);
      expect(
        at('create_trip_capacity').right,
        lessThan(at('create_trip_price').left),
      );
      expect(at('create_trip_plate').top, at('create_trip_vehicle').top);
      expect(
        at('create_trip_price').bottom,
        lessThan(at('create_trip_plate').top),
      );

      final handle = tester.ensureSemantics();
      expect(
        tester.getSemantics(find.byKey(const Key('create_trip_capacity'))),
        isA<SemanticsNode>().having(
          (n) => n.getSemanticsData().label,
          'label',
          contains('Capacity (quintals)'),
        ),
      );
      handle.dispose();
    });

    testWidgets('a long Afaan Oromoo label wraps and the pair lines up',
        (tester) async {
      tester.view.physicalSize = const Size(390, 1800);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<TripListingBloc>.value(value: tripListingBloc),
          ],
          child: const MaterialApp(
            locale: Locale('om'),
            // As in main.dart: Afaan Oromoo has no Material localizations of
            // its own.
            localizationsDelegates: [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: CreateTripScreen(
              truckerName: 'Test Trucker',
              truckerPhone: '+251911000000',
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final om = await AppLocalizations.delegate.load(const Locale('om'));
      final label = tester.renderObject<RenderParagraph>(
        find.text(om.vehiclePlate),
      );
      expect(label.didExceedMaxLines, isFalse);
      Rect at(String key) => tester.getRect(find.byKey(Key(key)));
      expect(at('create_trip_plate').top, at('create_trip_vehicle').top);
      expect(tester.takeException(), isNull);
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

    testWidgets('never saves without a driver name', (tester) async {
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
            home: CreateTripScreen(truckerPhone: '+251911000000'),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('create_trip_from')),
          matching: find.byType(TextFormField),
        ),
        'Meki',
      );
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('create_trip_to')),
          matching: find.byType(TextFormField),
        ),
        'Addis Ababa',
      );
      await tester.tap(find.text('Select departure date'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('create_trip_capacity')),
        '50',
      );
      await tester.enterText(
        find.byKey(const Key('create_trip_price')),
        '150',
      );
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      expect(find.text('Trucker Name'), findsOneWidget);
      verifyNever(() => tripListingBloc.add(any(that: isA<CreateTrip>())));
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
        find.descendant(
          of: find.byKey(const Key('create_trip_from')),
          matching: find.byType(TextFormField),
        ),
        'Bishoftu',
      );
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('create_trip_to')),
          matching: find.byType(TextFormField),
        ),
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

      final from = find.descendant(
        of: find.byKey(const Key('create_trip_from')),
        matching: find.byType(TextFormField),
      );
      final to = find.descendant(
        of: find.byKey(const Key('create_trip_to')),
        matching: find.byType(TextFormField),
      );
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

      final from = find.descendant(
        of: find.byKey(const Key('create_trip_from')),
        matching: find.byType(TextFormField),
      );
      final to = find.descendant(
        of: find.byKey(const Key('create_trip_to')),
        matching: find.byType(TextFormField),
      );
      expect(tester.widget<TextFormField>(from).controller!.text, 'Meki');
      expect(tester.widget<TextFormField>(to).controller!.text, 'Addis Ababa');
    });

    testWidgets('validates capacity range on save', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.enterText(
        find.byKey(const Key('create_trip_capacity')),
        '9999',
      );
      await tester.enterText(
        find.byKey(const Key('create_trip_price')),
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
