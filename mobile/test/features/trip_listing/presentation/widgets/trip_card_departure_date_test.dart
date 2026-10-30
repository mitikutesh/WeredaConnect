import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// SIM-50: 25 Oct picked in Addis Ababa is stored as 2026-10-24T21:00:00.000Z.
/// Web keeps it in UTC and showed 24 Oct; Isar gives mobile the local form.
/// Both must show 25 Oct, whatever the viewer's timezone.
void main() {
  final stored = DateTime.parse('2026-10-24T21:00:00.000Z');

  Trip trip(DateTime departure) => Trip()
    ..tripId = 'trip-1'
    ..truckerId = 'trucker-1'
    ..truckerName = 'Trucker'
    ..truckerPhone = '+251900100002'
    ..fromWoreda = 'Adama'
    ..toWoreda = 'Bishoftu'
    ..departureDate = departure
    ..capacity = 50
    ..pricePerQuintal = 200
    ..status = TripStatus.open
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

  testWidgets('web: the UTC value from the server shows the picked day',
      (tester) async {
    expect(stored.isUtc, isTrue);
    await tester.pumpWidget(wrap(TripCard(trip: trip(stored))));

    expect(find.textContaining('Oct 25'), findsOneWidget);
    expect(find.textContaining('Oct 24'), findsNothing);
  });

  testWidgets('mobile: the local value Isar returns shows the same day',
      (tester) async {
    await tester.pumpWidget(wrap(TripCard(trip: trip(stored.toLocal()))));

    expect(find.textContaining('Oct 25'), findsOneWidget);
  });
}
