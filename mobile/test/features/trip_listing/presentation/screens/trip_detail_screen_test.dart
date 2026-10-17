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
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
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

Trip _trip({String truckerPhone = '+251911000003'}) => Trip()
  ..tripId = 'trip-001'
  ..truckerId = 'trucker-001'
  ..truckerName = 'Trucker Tesfaye'
  ..truckerPhone = truckerPhone
  ..fromWoreda = 'Adama'
  ..toWoreda = 'Addis Ababa'
  ..departureDate = DateTime(2026, 10, 20)
  ..capacity = 50
  ..pricePerQuintal = 100
  ..status = TripStatus.open
  ..matchedListings = <String>[]
  ..createdAt = DateTime(2026, 10)
  ..updatedAt = DateTime(2026, 10)
  ..version = 1;

/// ESC-48: a trip shows its trucker's number only to that trucker; a buyer
/// reaches the trucker from the order once the farmer accepted it.
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
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  void signIn(String userId, UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _testTokens, role: role),
    );
  }

  Widget buildScreen(Trip trip) => MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<TripListingBloc>.value(value: tripListingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: TripDetailScreen(trip: trip),
        ),
      );

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  testWidgets('a buyer sees no number and no message button, only the note',
      (tester) async {
    tallView(tester);
    signIn('buyer-001', UserRole.buyer);

    await tester.pumpWidget(buildScreen(_trip()));
    await tester.pumpAndSettle();

    expect(find.text('Trucker Tesfaye'), findsOneWidget);
    expect(find.textContaining('911000003'), findsNothing);
    expect(find.text('Message Trucker'), findsNothing);
    expect(
      find.text('Contact opens after payment is held in escrow.'),
      findsOneWidget,
    );
  });

  testWidgets('the trip owner sees their own number', (tester) async {
    tallView(tester);
    signIn('trucker-001', UserRole.trucker);

    await tester.pumpWidget(buildScreen(_trip()));
    await tester.pumpAndSettle();

    expect(find.text('+251911000003'), findsOneWidget);
    expect(
      find.text('Contact opens after payment is held in escrow.'),
      findsNothing,
    );
  });
}
