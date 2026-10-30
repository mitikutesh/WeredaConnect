import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

/// The trip card as a sibling of the Trip detail board's header: route
/// title, day and price line, the booked share as a load bar, the status
/// pill, and Cancel Trip as a plain action for the trucker's open trips.
void main() {
  Trip trip({
    TripStatus status = TripStatus.open,
    double capacity = 50,
    double? booked,
    double? remaining,
  }) =>
      Trip()
        ..tripId = 'trip-1'
        ..truckerId = 'trucker-1'
        ..truckerName = 'Dawit'
        ..truckerPhone = '+251900100002'
        ..fromWoreda = 'Meki'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime.utc(2026, 10, 29, 21)
        ..capacity = capacity
        ..pricePerQuintal = 150
        ..bookedQuintals = booked
        ..remainingQuintals = remaining
        ..status = status
        ..matchedListings = <String>[]
        ..createdAt = DateTime.utc(2026, 10, 20)
        ..updatedAt = DateTime.utc(2026, 10, 20)
        ..version = 1;

  Widget wrap(Widget child) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(body: child),
      );

  testWidgets('route as the title, day and price under it, status pill',
      (tester) async {
    await tester.pumpWidget(wrap(TripCard(trip: trip())));

    expect(find.text('Meki → Addis Ababa'), findsOneWidget);
    expect(find.text('Fri, Oct 30 · 150 ETB per quintal'), findsOneWidget);
    expect(find.text('Open'), findsOneWidget);
  });

  testWidgets('a trip nobody booked shows its capacity and an empty bar',
      (tester) async {
    await tester.pumpWidget(wrap(TripCard(trip: trip())));

    expect(find.text('50 quintals free'), findsOneWidget);
    expect(find.byType(FwLoadBar), findsOneWidget);
    expect(find.byType(FractionallySizedBox), findsNothing);
  });

  testWidgets(
      'the booked share comes from the room left when the server '
      'sends only that', (tester) async {
    await tester.pumpWidget(wrap(TripCard(trip: trip(remaining: 20))));

    expect(find.text('30 of 50 quintals booked'), findsOneWidget);
    final fill = tester.widget<FractionallySizedBox>(
      find.byType(FractionallySizedBox),
    );
    expect(fill.widthFactor, closeTo(0.6, 0.001));
  });

  testWidgets('the server booked count wins over the room left',
      (tester) async {
    await tester.pumpWidget(
      wrap(TripCard(trip: trip(booked: 47, remaining: 3))),
    );

    expect(find.text('47 of 50 quintals booked'), findsOneWidget);
  });

  group('Cancel Trip', () {
    testWidgets('is a plain action in the card for an open trip',
        (tester) async {
      var cancelled = 0;
      await tester.pumpWidget(
        wrap(TripCard(trip: trip(), onCancel: () => cancelled++)),
      );

      await tester.tap(find.widgetWithText(OutlinedButton, 'Cancel Trip'));
      expect(cancelled, 1);
      expect(find.byIcon(Icons.cancel_outlined), findsNothing);
    });

    testWidgets('is absent without a handler (the buyer sees all trips)',
        (tester) async {
      await tester.pumpWidget(wrap(TripCard(trip: trip())));

      expect(find.text('Cancel Trip'), findsNothing);
    });

    for (final status in [
      TripStatus.inTransit,
      TripStatus.delivered,
      TripStatus.cancelled,
    ]) {
      testWidgets('is absent once the trip is ${status.name}', (tester) async {
        await tester.pumpWidget(
          wrap(TripCard(trip: trip(status: status), onCancel: () {})),
        );

        expect(find.text('Cancel Trip'), findsNothing);
      });
    }
  });
}
