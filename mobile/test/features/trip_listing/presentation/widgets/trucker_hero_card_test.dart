import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripRepository extends Mock implements BaseTripListingRepository {}

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

  testWidgets('posted trip shows route and price without an "Open" pill',
      (tester) async {
    final trip = Trip()
      ..tripId = 'trip-001'
      ..truckerId = 'trucker-001'
      ..fromWoreda = 'Gonder'
      ..toWoreda = 'Addis Ababa'
      ..departureDate = DateTime(2026, 10, 2)
      ..capacity = 100.0
      ..pricePerQuintal = 3455.0
      ..status = TripStatus.open
      ..matchedListings = []
      ..createdAt = DateTime(2026, 9, 28)
      ..updatedAt = DateTime(2026, 9, 28)
      ..version = 1;
    when(() => repository.getMostRelevantTrip('trucker-001'))
        .thenAnswer((_) async => trip);

    await tester.pumpWidget(buildCard());
    await tester.pumpAndSettle();

    expect(find.text('Gonder'), findsOneWidget);
    expect(find.text('Addis Ababa'), findsOneWidget);
    expect(find.textContaining('3455'), findsOneWidget);
    expect(find.text('Open'), findsNothing);
  });
}
