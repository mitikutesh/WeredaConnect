import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';

/// Pure unit tests for [MatchScorer] — no Isar, no network.
void main() {
  const scorer = MatchScorer();
  final now = DateTime.utc(2026, 1, 1, 12);

  BuyerIntent intent({
    String cropType = 'wheat',
    double quantityNeeded = 10,
    double maxPricePerKg = 100,
    String deliveryWoreda = 'Adama',
  }) {
    return BuyerIntent()
      ..intentId = 'i1'
      ..buyerId = 'b1'
      ..cropType = cropType
      ..quantityNeeded = quantityNeeded
      ..maxPricePerKg = maxPricePerKg
      ..deliveryWoreda = deliveryWoreda
      ..status = BuyerIntentStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;
  }

  CropListing listing({
    String cropType = 'wheat',
    double quantity = 10,
    double pricePerKg = 100,
    String locationName = 'Adama',
    double lat = 0,
    double lon = 0,
    DateTime? createdAt,
  }) {
    return CropListing()
      ..listingId = 'c1'
      ..farmerId = 'f1'
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = (LatLng()
        ..latitude = lat
        ..longitude = lon)
      ..locationName = locationName
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt = createdAt ?? now
      ..updatedAt = now
      ..version = 1;
  }

  group('candidateCropTypes', () {
    test('includes the exact type plus accepted substitutes', () {
      expect(scorer.candidateCropTypes('wheat'), containsAll(['wheat', 'barley']));
    });

    test('teff has no substitutes (returns only itself)', () {
      expect(scorer.candidateCropTypes('teff'), ['teff']);
    });

    test('is case-insensitive on the exact type', () {
      expect(scorer.candidateCropTypes('WHEAT').first, 'wheat');
    });
  });

  group('isCandidate', () {
    test('exact type within budget and quantity is a candidate', () {
      expect(scorer.isCandidate(intent(), listing()), isTrue);
    });

    test('substitute type is a candidate', () {
      expect(scorer.isCandidate(intent(), listing(cropType: 'barley')), isTrue);
    });

    test('unrelated type is rejected', () {
      expect(scorer.isCandidate(intent(), listing(cropType: 'teff')), isFalse);
    });

    test('price within the near-miss band is accepted', () {
      // +15% tolerance: 100 max → up to 115 inclusive.
      expect(scorer.isCandidate(intent(), listing(pricePerKg: 114)), isTrue);
    });

    test('price beyond the near-miss band is rejected', () {
      expect(scorer.isCandidate(intent(), listing(pricePerKg: 120)), isFalse);
    });

    test('quantity below the minimum fraction is rejected', () {
      // Needs 10 quintals; floor is 50% → below 5 rejected.
      expect(scorer.isCandidate(intent(), listing(quantity: 4)), isFalse);
    });

    test('partial fill at or above the floor is accepted', () {
      expect(scorer.isCandidate(intent(), listing(quantity: 6)), isTrue);
    });
  });

  group('component scores', () {
    test('exact type scores higher than a substitute', () {
      final exact = scorer.typeScore(intent(), listing());
      final sub = scorer.typeScore(intent(), listing(cropType: 'barley'));
      expect(exact, 1.0);
      expect(sub, MatchScorer.substituteTypeScore);
      expect(exact, greaterThan(sub));
    });

    test('price within budget scores 1.0 and decays across the band', () {
      expect(scorer.priceScore(intent(), listing(pricePerKg: 90)), 1.0);
      final nearMiss = scorer.priceScore(intent(), listing(pricePerKg: 110));
      expect(nearMiss, greaterThan(0));
      expect(nearMiss, lessThan(1.0));
    });

    test('quantity score is the partial-fill ratio, capped at 1.0', () {
      expect(scorer.quantityScore(intent(), listing(quantity: 5)), 0.5);
      expect(scorer.quantityScore(intent(), listing(quantity: 20)), 1.0);
    });

    test('geo score: same woreda > substring > different', () {
      final same = scorer.geoScore(intent(), listing());
      final near =
          scorer.geoScore(intent(), listing(locationName: 'Adama Zone'));
      final far = scorer.geoScore(intent(), listing(locationName: 'Mekelle'));
      expect(same, 1.0);
      expect(near, greaterThan(far));
      expect(far, MatchScorer.geoNeutral);
    });

    test('freshness decays from full to floor with age', () {
      final fresh = scorer.freshnessScore(
        listing(createdAt: now.subtract(const Duration(days: 1))),
        now,
      );
      final old = scorer.freshnessScore(
        listing(createdAt: now.subtract(const Duration(days: 60))),
        now,
      );
      expect(fresh, 1.0);
      expect(old, MatchScorer.freshFloor);
    });
  });

  group('geoScore haversine', () {
    test('uses true distance when crop has GPS and delivery centroid is known',
        () {
      // Crop sitting on Adama's centroid, delivering to Adama → ~0 km.
      final near = scorer.geoScore(intent(), listing(lat: 8.54, lon: 39.27));
      // Crop on Mekelle's centroid, delivering to Adama → ~550 km.
      final far = scorer.geoScore(intent(), listing(lat: 13.49, lon: 39.47));
      expect(near, greaterThan(0.9));
      expect(far, lessThan(near));
      // Far enough that it scores below the woreda-proxy neutral.
      expect(far, lessThan(MatchScorer.geoNeutral));
    });

    test('falls back to the woreda proxy when the crop has no GPS fix', () {
      // lat/lon default to (0,0) → the "no GPS" sentinel → proxy path.
      final s = scorer.geoScore(intent(), listing());
      expect(s, 1.0); // same woreda name (Adama == Adama)
    });

    test('falls back to the proxy when the delivery woreda is unknown', () {
      final s = scorer.geoScore(
        intent(deliveryWoreda: 'Gimbo'),
        listing(lat: 8.54, lon: 39.27),
      );
      // 'Gimbo' is not in the centroid table; crop name 'Adama' != 'Gimbo'.
      expect(s, MatchScorer.geoNeutral);
    });
  });

  group('scoreListing ranking', () {
    test('a perfect exact match scores near the top of the range', () {
      final score = scorer.scoreListing(intent(), listing(), now);
      expect(score, greaterThan(0.9));
    });

    test('an exact match outranks a substitute when all else is equal', () {
      final exact = scorer.scoreListing(intent(), listing(), now);
      final sub =
          scorer.scoreListing(intent(), listing(cropType: 'barley'), now);
      expect(exact, greaterThan(sub));
    });

    test('a closer, cheaper crop outranks a farther, pricier one', () {
      final good = scorer.scoreListing(
        intent(),
        listing(pricePerKg: 80),
        now,
      );
      final worse = scorer.scoreListing(
        intent(),
        listing(pricePerKg: 110, locationName: 'Mekelle'),
        now,
      );
      expect(good, greaterThan(worse));
    });
  });
}
