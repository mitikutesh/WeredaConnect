import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// SIM-50: a buyer's suggested trip shows the day the trucker picked (25 Oct
/// in Addis Ababa, stored as 2026-10-24T21:00:00.000Z), not the UTC day.
void main() {
  testWidgets('shows the departure day in Ethiopia', (tester) async {
    final trip = Trip()
      ..tripId = 'trip-1'
      ..truckerId = 'trucker-1'
      ..truckerName = 'Trucker Tesfaye'
      ..truckerPhone = '+251900100002'
      ..fromWoreda = 'Adama'
      ..toWoreda = 'Bishoftu'
      ..departureDate = DateTime.parse('2026-10-24T21:00:00.000Z')
      ..capacity = 50
      ..pricePerQuintal = 200
      ..status = TripStatus.open
      ..matchedListings = <String>[]
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..updatedAt = DateTime.utc(2026, 10, 20)
      ..version = 1;

    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: SuggestedTripCard(
            trip: trip,
            isSelected: false,
            onSelect: () {},
          ),
        ),
      ),
    );

    expect(find.text('Trucker Tesfaye · Sun 25 Oct'), findsOneWidget);
  });

  // Crop detail board: "Meki → Addis Ababa · 18 quintals room · 4.7".
  testWidgets('a rated trucker ends the route line with the rating',
      (tester) async {
    final trip = Trip()
      ..tripId = 'trip-1'
      ..truckerId = 'trucker-1'
      ..truckerName = 'Trucker Tesfaye'
      ..truckerPhone = '+251900100002'
      ..fromWoreda = 'Adama'
      ..toWoreda = 'Bishoftu'
      ..departureDate = DateTime.parse('2026-10-24T21:00:00.000Z')
      ..capacity = 50
      ..pricePerQuintal = 200
      ..status = TripStatus.open
      ..matchedListings = <String>[]
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..updatedAt = DateTime.utc(2026, 10, 20)
      ..version = 1;

    Future<void> pump({double? rating}) => tester.pumpWidget(
          MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: Scaffold(
              body: SuggestedTripCard(
                key: ValueKey(rating),
                trip: trip,
                isSelected: false,
                onSelect: () {},
                rating: rating,
              ),
            ),
          ),
        );

    await pump(rating: 4.66);
    expect(find.textContaining('Adama → Bishoftu ·'), findsOneWidget);
    // Page M: the room kg first, the quintals after in parentheses.
    expect(find.textContaining('kg room ('), findsOneWidget);
    expect(find.textContaining('quintals)'), findsOneWidget);
    expect(find.textContaining(' · 4.7'), findsOneWidget);

    await pump();
    expect(find.textContaining(' · 4.7'), findsNothing);
  });

  // CropDetailM board: "1,800 kg room (18 quintals)" on the route line and
  // the price per kg over "(150/quintal)".
  testWidgets('the room and the price lead with kilograms', (tester) async {
    final trip = Trip()
      ..tripId = 'trip-1'
      ..truckerId = 'trucker-1'
      ..truckerName = 'Dawit Tesfaye'
      ..fromWoreda = 'Meki'
      ..toWoreda = 'Addis Ababa'
      ..departureDate = DateTime.parse('2026-10-29T21:00:00.000Z')
      ..capacity = 50
      ..remainingQuintals = 18
      ..pricePerQuintal = 150
      ..status = TripStatus.open
      ..matchedListings = <String>[]
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..updatedAt = DateTime.utc(2026, 10, 20)
      ..version = 1;

    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: SuggestedTripCard(
            trip: trip,
            isSelected: true,
            onSelect: () {},
            rating: 4.7,
          ),
        ),
      ),
    );

    expect(
      find.text('Meki → Addis Ababa · 1,800 kg room (18 quintals) · 4.7'),
      findsOneWidget,
    );
    expect(find.text('1.50'), findsOneWidget);
    expect(find.text('ETB/kg'), findsOneWidget);
    expect(find.text('(150/quintal)'), findsOneWidget);
  });

  test('the price per kg is the stored price per quintal over 100', () {
    expect(transportPricePerKgText(150), '1.50');
    expect(transportPricePerKgText(160), '1.60');
    expect(transportPricePerKgText(1234), '12.34');
    expect(transportPricePerKgText(5), '0.05');
  });
}
