import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/match_result_card.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/features/reputation/domain/owner_reputation.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';
import 'package:wereda_connect/shared/widgets/low_reputation_pill.dart';

/// ESC-31: the owner's public reputation on the cards. Shown only when the
/// row carries the server's copy; without it every card is as before.
void main() {
  CropListing listing({
    String? tier,
    double? rating,
    int? count,
    int? reliability,
    DateTime? at,
  }) =>
      CropListing()
        ..listingId = 'l1'
        ..farmerId = 'f1'
        ..farmerName = 'Almaz Bekele'
        ..cropType = 'teff'
        ..quantity = 50
        ..pricePerKg = 45
        ..location = (LatLng()
          ..latitude = 0
          ..longitude = 0)
        ..locationName = 'Adama'
        ..description = ''
        ..ownerTier = tier
        ..ownerEffectiveRating = rating
        ..ownerRatingCount = count
        ..ownerReliabilityPct = reliability
        ..ownerReputationAt = at
        ..status = ListingStatus.active
        ..createdAt = DateTime.utc(2026, 10, 20)
        ..updatedAt = DateTime.utc(2026, 10, 20)
        ..version = 1;

  Trip trip({String? tier, double? rating, int? count, DateTime? at}) => Trip()
    ..tripId = 'trip-1'
    ..truckerId = 'trucker-1'
    ..truckerName = 'Dawit'
    ..truckerPhone = ''
    ..fromWoreda = 'Meki'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = DateTime.utc(2026, 10, 29, 21)
    ..capacity = 50
    ..pricePerQuintal = 150
    ..ownerTier = tier
    ..ownerEffectiveRating = rating
    ..ownerRatingCount = count
    ..ownerReputationAt = at
    ..status = TripStatus.open
    ..matchedListings = <String>[]
    ..createdAt = DateTime.utc(2026, 10, 20)
    ..updatedAt = DateTime.utc(2026, 10, 20)
    ..version = 1;

  final stamp = DateTime.utc(2026, 11, 7, 9);

  Widget wrap(Widget child) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(body: SingleChildScrollView(child: child)),
      );

  group('OwnerReputation', () {
    test('a row without the copy is not present and never low', () {
      final reputation = OwnerReputation.ofListing(listing());
      expect(reputation.isPresent, isFalse);
      expect(reputation.isLow, isFalse);
      expect(reputation.rating, isNull);
      expect(reputation.count, isNull);
    });

    test('the reliability alone (ESC-30) is not a copy', () {
      expect(
        OwnerReputation.ofListing(listing(reliability: 83)).isPresent,
        isFalse,
      );
    });

    test('low shows the label, good and unknown tiers do not', () {
      expect(OwnerReputation.ofTrip(trip(tier: 'low')).isLow, isTrue);
      expect(OwnerReputation.ofTrip(trip(tier: 'good')).isLow, isFalse);
      // The public tier is good or low; an internal name is never shown as
      // its own label.
      expect(OwnerReputation.ofTrip(trip(tier: 'warning')).isLow, isFalse);
      expect(OwnerReputation.ofTrip(trip(tier: 'restricted')).isLow, isTrue);
      expect(OwnerReputation.ofTrip(trip(tier: 'gold')).isLow, isFalse);
    });

    test('the rating needs ratings behind it', () {
      final rated = OwnerReputation.ofListing(
        listing(tier: 'good', rating: 4.4, count: 12, at: stamp),
      );
      expect(rated.isPresent, isTrue);
      expect(rated.rating, 4.4);
      expect(rated.count, 12);

      final unrated = OwnerReputation.ofListing(
        listing(tier: 'good', count: 0, at: stamp),
      );
      expect(unrated.isPresent, isTrue);
      expect(unrated.rating, isNull);
      expect(unrated.count, isNull);
    });
  });

  group('ListingCard', () {
    ListingCard card({bool low = false}) => ListingCard(
          title: 'Teff',
          location: 'Adama',
          sellerName: 'Almaz',
          price: '45',
          lowReputation: low,
        );

    testWidgets('no label by default', (tester) async {
      await tester.pumpWidget(wrap(card()));
      expect(find.byType(LowReputationPill), findsNothing);
      expect(find.text('Low reputation'), findsNothing);
    });

    testWidgets('the Low reputation label with an icon', (tester) async {
      await tester.pumpWidget(wrap(card(low: true)));
      expect(find.text('Low reputation'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });
  });

  group('TripCard', () {
    testWidgets('a trip without the copy looks as before', (tester) async {
      await tester.pumpWidget(wrap(TripCard(trip: trip(), showTrucker: true)));
      expect(find.text('Trucker: Dawit'), findsOneWidget);
      expect(find.byIcon(Icons.star), findsNothing);
      expect(find.byType(LowReputationPill), findsNothing);
    });

    testWidgets('a buyer sees the rating with its count', (tester) async {
      await tester.pumpWidget(
        wrap(
          TripCard(
            trip: trip(tier: 'good', rating: 4.4, count: 12, at: stamp),
            showTrucker: true,
          ),
        ),
      );
      expect(find.text('Trucker: Dawit'), findsOneWidget);
      expect(find.text('4.4 (12)'), findsOneWidget);
      expect(find.byType(LowReputationPill), findsNothing);
    });

    testWidgets('a buyer sees the Low reputation label', (tester) async {
      await tester.pumpWidget(
        wrap(
          TripCard(
            trip: trip(tier: 'low', rating: 3.9, count: 8, at: stamp),
            showTrucker: true,
          ),
        ),
      );
      expect(find.text('3.9 (8)'), findsOneWidget);
      expect(find.text('Low reputation'), findsOneWidget);
    });

    testWidgets('a low trucker without ratings shows the label alone',
        (tester) async {
      await tester.pumpWidget(
        wrap(
          TripCard(
            trip: trip(tier: 'low', count: 0, at: stamp),
            showTrucker: true,
          ),
        ),
      );
      expect(find.byIcon(Icons.star), findsNothing);
      expect(find.text('Low reputation'), findsOneWidget);
    });

    testWidgets("the trucker's own list shows neither", (tester) async {
      await tester.pumpWidget(
        wrap(
          TripCard(trip: trip(tier: 'low', rating: 3.9, count: 8, at: stamp)),
        ),
      );
      expect(find.byIcon(Icons.star), findsNothing);
      expect(find.byType(LowReputationPill), findsNothing);
    });
  });

  group('SuggestedTripCard', () {
    testWidgets('no label by default', (tester) async {
      await tester.pumpWidget(
        wrap(
          SuggestedTripCard(trip: trip(), isSelected: false, onSelect: () {}),
        ),
      );
      expect(find.byType(LowReputationPill), findsNothing);
      expect(find.textContaining(' · 4.'), findsNothing);
    });

    testWidgets('the rating ends the route line, the label under it',
        (tester) async {
      await tester.pumpWidget(
        wrap(
          SuggestedTripCard(
            trip: trip(),
            isSelected: false,
            onSelect: () {},
            rating: 4.1,
            lowReputation: true,
          ),
        ),
      );
      expect(find.textContaining(' · 4.1'), findsOneWidget);
      expect(find.text('Low reputation'), findsOneWidget);
    });
  });

  group('MatchResultCard', () {
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

    Widget card(CropListing listing) => wrap(
          MatchResultCard(
            result: CropMatchResult(
              listing: listing,
              suggestedTrips: const [],
              estimatedTotalMin: 1000,
            ),
            intent: intent,
            onTap: () {},
            onOrder: () {},
          ),
        );

    testWidgets('a listing without the copy shows no rating and no label',
        (tester) async {
      await tester.pumpWidget(card(listing()));
      expect(find.byIcon(Icons.star), findsNothing);
      expect(find.byType(LowReputationPill), findsNothing);
    });

    testWidgets('the effective rating with its count', (tester) async {
      await tester.pumpWidget(
        card(listing(tier: 'good', rating: 4.4, count: 12, at: stamp)),
      );
      expect(find.text('4.4 (12)'), findsOneWidget);
      expect(find.byType(LowReputationPill), findsNothing);
    });

    testWidgets('the Low reputation label', (tester) async {
      await tester.pumpWidget(
        card(listing(tier: 'low', rating: 3.8, count: 5, at: stamp)),
      );
      expect(find.text('3.8 (5)'), findsOneWidget);
      expect(find.text('Low reputation'), findsOneWidget);
    });
  });
}
