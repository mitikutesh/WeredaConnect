import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripRepository extends Mock implements BaseTripListingRepository {}

/// The Trucker · Home board's trip card, one dark card for every trip state.
/// The live sharing states are covered with the engine in
/// test/features/live_trip/presentation/trip_mode_entry_test.dart.
void main() {
  late MockTripListingBloc bloc;
  late MockTripRepository repository;

  setUp(() {
    bloc = MockTripListingBloc();
    repository = MockTripRepository();
    when(() => bloc.repository).thenReturn(repository);
    when(() => bloc.state).thenReturn(const TripListingInitial());
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => repository.getRecentRoutes(any())).thenAnswer((_) async => []);
  });

  Trip trip({TripStatus status = TripStatus.open, double? remaining}) => Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Dawit Tesfaye'
    ..truckerPhone = '+251900100002'
    ..fromWoreda = 'Meki'
    ..toWoreda = 'Addis Ababa'
    // A day that is never "today" for the test run (Ethiopia noon).
    ..departureDate = DateTime.utc(2027, 3, 5, 9)
    ..capacity = 50.0
    ..remainingQuintals = remaining
    ..pricePerQuintal = 150.0
    ..status = status
    ..matchedListings = []
    ..createdAt = DateTime(2026, 10, 28)
    ..updatedAt = DateTime(2026, 10, 28)
    ..version = 1;

  Widget buildCard() {
    return BlocProvider<TripListingBloc>.value(
      value: bloc,
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(body: TruckerHeroCard(userId: 'trucker-001')),
      ),
    );
  }

  testWidgets('an open trip shows the route, its day and its price',
      (tester) async {
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => trip());

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('Meki → Addis Ababa'), findsOneWidget);
    expect(find.text('Mar 5'), findsOneWidget);
    expect(find.text('150 ETB per quintal'), findsOneWidget);
    // The whole card opens the trip; no "Open" pill reads as a button.
    expect(find.text('Open'), findsNothing);
  });

  // Trucker · Home board: "Space on today's trip", booked out of capacity.
  testWidgets('a trip card shows how full the truck is', (tester) async {
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => trip(remaining: 20));

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('Space on this trip'), findsOneWidget);
    expect(find.text('30 of 50 quintals loaded'), findsOneWidget);
    final bar = tester.widget<LinearProgressIndicator>(
      find.byType(LinearProgressIndicator),
    );
    expect(bar.value, closeTo(0.6, 1e-9));
  });

  testWidgets('a booked trip says what to do on the trip day', (tester) async {
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => trip(status: TripStatus.matched));

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('Matched'), findsOneWidget);
    expect(find.text("Scan each farmer's QR at pickup"), findsOneWidget);
  });

  testWidgets('a trip on the road without the engine shows its state',
      (tester) async {
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => trip(status: TripStatus.inTransit));

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('In Transit'), findsOneWidget);
    // No deliveries to read here: the price stands in for the next stop.
    expect(find.text('150 ETB per quintal'), findsOneWidget);
  });

  testWidgets('no trip: a quiet line and the recent routes as chips',
      (tester) async {
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => null);
    when(() => repository.getRecentRoutes('trucker-001'))
        .thenAnswer((_) async => [(from: 'Meki', to: 'Adama')]);

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('No active trip'), findsOneWidget);
    expect(
      find.text('Post a trip and buyers can book space on it.'),
      findsOneWidget,
    );
    expect(find.text('Meki → Adama'), findsOneWidget);
  });
}
