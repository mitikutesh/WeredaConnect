import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockTripListingBloc extends Mock implements TripListingBloc {}

class _FakeAuthEvent extends Fake implements AuthEvent {}

class _FakeAuthState extends Fake implements AuthState {}

class _FakeTripListingEvent extends Fake implements TripListingEvent {}

class _FakeTripListingState extends Fake implements TripListingState {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

const _contactInText = 'Phone numbers are not allowed here. Remove the '
    'number: contact opens after payment is held in escrow.';

/// FUP-49: the trip wizard refuses a phone number in the route and vehicle
/// fields before anything is queued, and explains the server's refusal.
void main() {
  late _MockAuthBloc authBloc;
  late _MockTripListingBloc tripBloc;
  late StreamController<TripListingState> states;

  setUpAll(() {
    registerFallbackValue(_FakeAuthEvent());
    registerFallbackValue(_FakeAuthState());
    registerFallbackValue(_FakeTripListingEvent());
    registerFallbackValue(_FakeTripListingState());
  });

  setUp(() {
    authBloc = _MockAuthBloc();
    tripBloc = _MockTripListingBloc();
    states = StreamController<TripListingState>.broadcast();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'trucker-001',
        tokens: _tokens,
        role: UserRole.trucker,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripBloc.state).thenReturn(const TripListingInitial());
    when(() => tripBloc.stream).thenAnswer((_) => states.stream);
  });

  tearDown(() => states.close());

  Widget app() => MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<TripListingBloc>.value(value: tripBloc),
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

  Future<void> fillRoute(
    WidgetTester tester, {
    String from = 'Bishoftu',
    String to = 'Addis Ababa',
  }) async {
    await tester.enterText(
      find.widgetWithText(TextFormField, 'From (Origin)'),
      from,
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'To (Destination)'),
      to,
    );
    await tester.tap(find.text('Select departure date'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('OK'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();
  }

  testWidgets('a number in From keeps the wizard on the route step',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    await fillRoute(tester, from: 'Bishoftu 0911 234 567');

    expect(find.text(_contactInText), findsOneWidget);
    expect(find.text('Trucker Name'), findsNothing);
  });

  testWidgets('a number in To keeps the wizard on the route step',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    await fillRoute(tester, to: 'Addis Ababa +251 712 345 678');

    expect(find.text(_contactInText), findsOneWidget);
    expect(find.text('Trucker Name'), findsNothing);
  });

  for (final (label, text) in [
    ('Vehicle Description', 'Isuzu FSR, call 0911234567'),
    ('Vehicle Description', 'አይሱዙ ስልክ 0911 23 45 67'),
    ('Vehicle Description', 'Konkolaataa Isuzu, bilbilaa 0712 34 56 78'),
    ('Vehicle Plate', '0911234567'),
  ]) {
    testWidgets('a number in $label keeps the wizard on step 2: $text',
        (tester) async {
      await tester.pumpWidget(app());
      await tester.pumpAndSettle();
      await fillRoute(tester);

      await tester.enterText(find.widgetWithText(TextFormField, label), text);
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      expect(find.text(_contactInText), findsOneWidget);
      expect(find.text('Capacity (quintals)'), findsNothing);
    });
  }

  testWidgets('a plate and a vehicle without a number pass', (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();
    await fillRoute(tester);

    await tester.enterText(
      find.widgetWithText(TextFormField, 'Vehicle Plate'),
      '3-AA-12345',
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Vehicle Description'),
      'Isuzu FSR 2019 model, 70 quintal capacity',
    );
    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();

    expect(find.text(_contactInText), findsNothing);
    expect(find.text('Capacity (quintals)'), findsOneWidget);
  });

  testWidgets('the server refusal is explained', (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    states.add(
      const TripListingError(
        message: 'TRIP_CONTACT_IN_TEXT: Remove the phone number from toWoreda.',
        code: AppErrorCode.contactInText,
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.text(_contactInText), findsOneWidget);
  });
}
