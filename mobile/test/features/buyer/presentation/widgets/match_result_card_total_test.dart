import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/match_result_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The estimated total groups thousands the way the boards write money.
void main() {
  testWidgets('estimated total groups thousands', (tester) async {
    final listing = CropListing()
      ..listingId = 'l1'
      ..farmerId = 'f1'
      ..cropType = 'teff'
      ..quantity = 50
      ..pricePerKg = 45
      ..location = (LatLng()
        ..latitude = 0
        ..longitude = 0)
      ..locationName = 'Adama'
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..updatedAt = DateTime.utc(2026, 10, 20)
      ..version = 1;
    final intent = BuyerIntent()
      ..intentId = 'intent-1'
      ..buyerId = 'buyer-1'
      ..cropType = 'teff'
      ..quantityNeeded = 50
      ..maxPricePerKg = 0
      ..deliveryWoreda = 'Adama'
      ..status = BuyerIntentStatus.active
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..updatedAt = DateTime.utc(2026, 10, 20)
      ..version = 1;

    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: MatchResultCard(
            result: CropMatchResult(
              listing: listing,
              suggestedTrips: const [],
              estimatedTotalMin: 241000.4,
            ),
            intent: intent,
            onTap: () {},
          ),
        ),
      ),
    );

    expect(find.textContaining('241,000 ETB'), findsOneWidget);
    expect(find.textContaining('241000'), findsNothing);
  });
}
