import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

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
        home: Scaffold(body: BrowseTripsScreen()),
      ),
    );
  }

  group('BrowseTripsScreen', () {
    testWidgets('shows empty state when no trips', (tester) async {
      when(() => tripListingBloc.state).thenReturn(
        const TripsLoaded(trips: []),
      );
      when(() => tripListingBloc.stream).thenAnswer(
        (_) => Stream.value(const TripsLoaded(trips: [])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('No trips found'), findsOneWidget);
    });

    testWidgets('shows loading indicator', (tester) async {
      when(() => tripListingBloc.state).thenReturn(const TripListingLoading());
      when(() => tripListingBloc.stream).thenAnswer(
        (_) => Stream.value(const TripListingLoading()),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows trip card when trips loaded', (tester) async {
      final trip = Trip()
        ..tripId = 'trip-001'
        ..truckerId = 'trucker-001'
        ..fromWoreda = 'Bishoftu'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime(2026, 2, 18)
        ..capacity = 150.0
        ..pricePerQuintal = 120.0
        ..status = TripStatus.open
        ..matchedListings = []
        ..createdAt = DateTime(2026, 2, 16)
        ..updatedAt = DateTime(2026, 2, 16)
        ..version = 1;

      when(() => tripListingBloc.state).thenReturn(
        TripsLoaded(trips: [trip]),
      );
      when(() => tripListingBloc.stream).thenAnswer(
        (_) => Stream.value(TripsLoaded(trips: [trip])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Route should be displayed
      expect(find.textContaining('Bishoftu'), findsOneWidget);
      expect(find.textContaining('Addis Ababa'), findsOneWidget);
      // Status badge
      expect(find.text('Open'), findsOneWidget);

      // LT-01: the list ends clear of the floating create button.
      final list = tester.widget<ListView>(
        find.descendant(
          of: find.byType(RefreshIndicator),
          matching: find.byType(ListView),
        ),
      );
      expect(
        list.padding!.resolve(TextDirection.ltr).bottom,
        Breakpoints.fabClearance,
      );
    });

    testWidgets('shows filter fields', (tester) async {
      when(() => tripListingBloc.state).thenReturn(
        const TripsLoaded(trips: []),
      );
      when(() => tripListingBloc.stream).thenAnswer(
        (_) => Stream.value(const TripsLoaded(trips: [])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // From/To filters are place-autocomplete pickers (with a search apply
      // button beside them).
      expect(find.byType(PlaceSelector), findsNWidgets(2));
      expect(find.widgetWithIcon(IconButton, Icons.search), findsOneWidget);
    });
  });
}
