import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
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

  group('CreateTripScreen', () {
    testWidgets('renders form fields', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Verify trucker info fields (pre-filled)
      expect(find.text('Trucker Name'), findsOneWidget);
      expect(find.text('Trucker Phone'), findsOneWidget);

      // Verify form fields
      expect(find.text('From (Origin)'), findsOneWidget);
      expect(find.text('To (Destination)'), findsOneWidget);
      expect(find.text('Departure Date'), findsOneWidget);

      // Scroll to find remaining fields
      await tester.scrollUntilVisible(
        find.text('Save'),
        100,
        scrollable: find.byType(Scrollable).last,
      );
      expect(find.text('Save'), findsOneWidget);
    });

    testWidgets('shows validation errors on empty submit', (tester) async {
      // Use a large enough surface so everything is visible
      tester.view.physicalSize = const Size(800, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Clear trucker fields to trigger validation
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Trucker Name'),
        '',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Trucker Phone'),
        '',
      );

      // Tap save
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // Should show validation errors for required fields
      expect(find.text('Origin woreda is required'), findsOneWidget);
    });

    testWidgets('shows capacity validation error for out-of-range',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Fill in woreda fields
      await tester.enterText(
        find.widgetWithText(TextFormField, 'From (Origin)'),
        'Bishoftu',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'To (Destination)'),
        'Addis Ababa',
      );

      // Scroll to capacity field and enter invalid capacity (too high)
      await tester.scrollUntilVisible(
        find.widgetWithText(TextFormField, 'Capacity (quintals)'),
        100,
        scrollable: find.byType(Scrollable).last,
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Capacity (quintals)'),
        '9999',
      );

      // Enter valid price
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Price per Quintal (ETB)'),
        '120',
      );

      // Scroll to Save button and tap
      await tester.scrollUntilVisible(
        find.text('Save'),
        100,
        scrollable: find.byType(Scrollable).last,
      );
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // Scroll to see the capacity validation error
      await tester.scrollUntilVisible(
        find.text('Capacity must be between 0.1 and 1000 quintals'),
        -100,
        scrollable: find.byType(Scrollable).last,
      );
      expect(
        find.text('Capacity must be between 0.1 and 1000 quintals'),
        findsOneWidget,
      );
    });

    testWidgets('shows same origin/destination error', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Fill in same woreda for both fields
      await tester.enterText(
        find.widgetWithText(TextFormField, 'From (Origin)'),
        'Bishoftu',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'To (Destination)'),
        'Bishoftu',
      );

      // Scroll to Save button and tap
      await tester.scrollUntilVisible(
        find.text('Save'),
        100,
        scrollable: find.byType(Scrollable).last,
      );
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // Scroll to see the validation error
      await tester.scrollUntilVisible(
        find.text('Origin and destination cannot be the same'),
        -100,
        scrollable: find.byType(Scrollable).last,
      );
      expect(
        find.text('Origin and destination cannot be the same'),
        findsOneWidget,
      );
    });
  });
}
