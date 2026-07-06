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
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

  /// Completes step 1 (route + departure date) and moves to step 2.
  Future<void> completeRouteStep(WidgetTester tester) async {
    await tester.enterText(
      find.widgetWithText(TextFormField, 'From (Origin)'),
      'Bishoftu',
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'To (Destination)'),
      'Addis Ababa',
    );
    // Pick a departure date via the date picker dialog.
    await tester.tap(find.text('Select departure date'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('OK'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();
  }

  group('CreateTripScreen wizard', () {
    testWidgets('step 1 shows route fields only, with pinned Continue',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Route step fields
      expect(find.text('From (Origin)'), findsOneWidget);
      expect(find.text('To (Destination)'), findsOneWidget);
      expect(find.text('Departure Date'), findsOneWidget);

      // Vehicle and price fields belong to later steps
      expect(find.text('Trucker Name'), findsNothing);
      expect(find.text('Capacity (quintals)'), findsNothing);

      // Pinned action bar shows Continue (not Save yet)
      expect(find.text('Continue'), findsOneWidget);
      expect(find.text('Save'), findsNothing);
    });

    testWidgets('validates route step on continue', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      expect(find.text('Origin woreda is required'), findsOneWidget);
    });

    testWidgets('shows same origin/destination error', (tester) async {
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

      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      expect(
        find.text('Origin and destination cannot be the same'),
        findsOneWidget,
      );
    });

    testWidgets('step 2 shows prefilled trucker info', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await completeRouteStep(tester);

      expect(find.text('Trucker Name'), findsOneWidget);
      expect(find.text('Test Trucker'), findsOneWidget);
      expect(find.text('+251911000000'), findsOneWidget);
      // Back appears once past step 1
      expect(find.text('Back'), findsOneWidget);
    });

    testWidgets('step 3 validates capacity range on save', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await completeRouteStep(tester);

      // Step 2 is prefilled — continue straight through.
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      // Step 3: capacity + price with the final Save action.
      expect(find.text('Capacity (quintals)'), findsOneWidget);
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
}
