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
    double? farmerReliability,
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
      ..farmerReliability = farmerReliability
      ..status = ListingStatus.active
      ..createdAt = createdAt ?? now
      ..updatedAt = now
      ..version = 1;
  }

  group('candidateCropTypes', () {
    test('includes the exact type plus accepted substitutes', () {
      expect(
        scorer.candidateCropTypes('wheat'),
        containsAll(['wheat', 'barley']),
      );
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

    test('no budget (0) leaves price unconstrained', () {
      expect(
        scorer.isCandidate(
          intent(maxPricePerKg: 0),
          listing(pricePerKg: 500),
        ),
        isTrue,
      );
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

    test('no budget (0) scores price 1.0 — parity with the server scorer', () {
      expect(
        scorer.priceScore(intent(maxPricePerKg: 0), listing(pricePerKg: 500)),
        1.0,
      );
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

    test('geo proxy normalises spelling (SIM-52, server parity)', () {
      // Picker spelling "Ādama" vs typed "Adama, Oromia" is the same place.
      // Mirrors MatchScoringTests.GeoScore_WoredaProxy_NormalisesSpelling.
      // 'Ādama' is not in the centroid table, so this exercises the proxy.
      expect(
        scorer.geoScore(
          intent(deliveryWoreda: 'Ādama Zone, Oromia'),
          listing(locationName: 'adama zone'),
        ),
        1.0,
      );
      expect(
        scorer.geoScore(
          intent(deliveryWoreda: 'Debre Mark’os'),
          listing(locationName: 'Debre Markos, Amhara'),
        ),
        1.0,
      );
      // Whole-word overlap keeps the partial score (listing defaults to
      // 'Adama').
      expect(
        scorer.geoScore(intent(deliveryWoreda: 'Adama Zone'), listing()),
        MatchScorer.geoPartial,
      );
      // Raw-substring false positive is gone: "Ambo" is not "Gambo".
      expect(
        scorer.geoScore(
          intent(deliveryWoreda: 'Ambo'),
          listing(locationName: 'Gambo'),
        ),
        MatchScorer.geoNeutral,
      );
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

    test('delivery woreda with a region suffix or diacritics still resolves',
        () {
      // Centroid lookup goes through the shared woreda normaliser, so the
      // haversine path (not the string proxy) is used for these spellings:
      // a crop at the Adama centroid scores 1.0 even though the raw strings
      // would never be equal.
      final crop = listing(lat: 8.54, lon: 39.27, locationName: 'Elsewhere');
      expect(
        scorer.geoScore(intent(deliveryWoreda: 'Adama, Oromia'), crop),
        1.0,
      );
      expect(scorer.geoScore(intent(deliveryWoreda: 'Ādama'), crop), 1.0);
      expect(
        scorer.geoScore(intent(deliveryWoreda: 'Dirē Dawa'), crop),
        lessThan(0.2),
      );
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

  // ── Parity guards (MUST match server MatchScoringTests.cs) ─────────────────
  // The server scorer (MatchScoring.cs) is an independent implementation whose
  // agreement with this one is the contract's hard invariant. These golden
  // vectors and the weight-sum check are duplicated verbatim on the C# side; if
  // a weight/band changes on one platform, the identical assertion fails here.
  group('parity guards', () {
    test('weights sum to 1.0', () {
      expect(
        MatchScorer.wType +
            MatchScorer.wPrice +
            MatchScorer.wGeo +
            MatchScorer.wQuantity +
            MatchScorer.wFreshness +
            MatchScorer.wReliability,
        closeTo(1.0, 1e-10),
      );
    });

    test('golden vector: substitute, partial, disjoint woreda → 0.664', () {
      // type=substitute(0.6), price=within(1.0), qty=5/10(0.5),
      // geo=disjoint-proxy(0.3), freshness=fresh(1.0), reliability=neutral(0.7).
      // 0.30*0.6 + 0.20*1.0 + 0.18*0.3 + 0.12*0.5 + 0.10*1.0 + 0.10*0.7 = 0.664
      final score = scorer.scoreListing(
        intent(),
        listing(cropType: 'barley', quantity: 5, locationName: 'Mekelle'),
        now,
      );
      expect(score, closeTo(0.664, 1e-6));
    });

    test('golden vector: price +10% near-miss band → 0.836667', () {
      // type=exact(1.0), price=1-(0.10/0.15)=0.333333, qty=1.0,
      // geo=same-woreda(1.0), freshness=fresh(1.0), reliability=neutral(0.7).
      // 0.30 + 0.20*0.333333 + 0.18 + 0.12 + 0.10 + 0.10*0.7 = 0.836667
      final score =
          scorer.scoreListing(intent(), listing(pricePerKg: 110), now);
      expect(score, closeTo(0.836667, 1e-6));
    });

    test('golden vector: reliability moves the score by its full weight', () {
      // A perfect match with a flaky farmer (reliability 0.2) vs a proven one
      // (1.0) must differ by exactly wReliability * (1.0 - 0.2) = 0.08.
      final flaky = scorer.scoreListing(
        intent(),
        listing(farmerReliability: 0.2),
        now,
      );
      final proven = scorer.scoreListing(
        intent(),
        listing(farmerReliability: 1.0),
        now,
      );
      expect(proven - flaky, closeTo(MatchScorer.wReliability * 0.8, 1e-9));
      // A proven farmer's perfect match reaches the top of the range.
      expect(proven, closeTo(1.0, 1e-9));
    });

    test('haversine Adama→Mekelle pins radius and units (~0.083)', () {
      // Crop with a real GPS fix on Mekelle, delivering to the Adama centroid
      // (~550 km) → 1/(1+km/50) ≈ 0.083. Catches a radius/units regression
      // against the server haversine.
      final score = scorer.geoScore(intent(), listing(lat: 13.49, lon: 39.47));
      expect(score, closeTo(0.083, 0.04));
    });

    test('full substitute map parity (exact lists, both directions)', () {
      // The COMPLETE substitute map as exact lists — a one-sided edit to any
      // pair (not just the golden-vector pairs) fails here and on the C# side.
      expect(scorer.candidateCropTypes('wheat'), ['wheat', 'barley']);
      expect(scorer.candidateCropTypes('barley'), ['barley', 'wheat']);
      expect(scorer.candidateCropTypes('maize'), ['maize', 'sorghum']);
      expect(scorer.candidateCropTypes('sorghum'), ['sorghum', 'maize']);
      expect(scorer.candidateCropTypes('chickpea'), ['chickpea', 'lentils']);
      expect(scorer.candidateCropTypes('lentils'), ['lentils', 'chickpea']);
    });

    test('golden vector: maize→sorghum mirrors wheat→barley (0.664)', () {
      // The maize↔sorghum pair must score exactly like wheat↔barley — pins the
      // one substitute pair the original golden vectors didn't exercise.
      final viaGrain = scorer.scoreListing(
        intent(),
        listing(cropType: 'barley', quantity: 5, locationName: 'Mekelle'),
        now,
      );
      final viaMaize = scorer.scoreListing(
        intent(cropType: 'maize'),
        listing(cropType: 'sorghum', quantity: 5, locationName: 'Mekelle'),
        now,
      );
      expect(viaMaize, closeTo(0.664, 1e-6));
      expect(viaMaize, closeTo(viaGrain, 1e-9));
    });

    test('golden ranking order is identical across platforms', () {
      // A fixed candidate set must produce ONE exact ranked order, duplicated
      // verbatim on the C# side — catches formula drift that the per-signal
      // golden vectors can miss. All crops carry neutral reliability.
      final i = intent();
      final exactSame = scorer.scoreListing(i, listing(), now); // 0.97
      final nearMissPrice =
          scorer.scoreListing(i, listing(pricePerKg: 110), now); // 0.836667
      final exactFar = scorer.scoreListing(
        i,
        listing(locationName: 'Mekelle'),
        now,
      ); // 0.844
      final substitutePartial =
          scorer.scoreListing(i, listing(cropType: 'barley', quantity: 5), now);
      final scores = <String, double>{
        'exact-same': exactSame,
        'near-miss-price': nearMissPrice,
        'exact-far': exactFar,
        'substitute-partial': substitutePartial, // 0.79
      };
      final ranked = scores.keys.toList()
        ..sort((a, b) => scores[b]!.compareTo(scores[a]!));
      expect(ranked, [
        'exact-same',
        'exact-far',
        'near-miss-price',
        'substitute-partial',
      ]);
    });

    test('legume substitutes are reachable (chickpea↔lentils)', () {
      // Regression: the substitute map previously used non-existent crop types
      // ("lentil"/"fababean") so legume substitution never matched real data.
      expect(
        scorer.candidateCropTypes('chickpea'),
        containsAll(['chickpea', 'lentils']),
      );
      expect(
        scorer.candidateCropTypes('lentils'),
        containsAll(['lentils', 'chickpea']),
      );
      expect(
        scorer.isSubstitute(
          intent(cropType: 'chickpea'),
          listing(cropType: 'lentils'),
        ),
        isTrue,
      );
      expect(
        scorer.isCandidate(
          intent(cropType: 'chickpea'),
          listing(cropType: 'lentils'),
        ),
        isTrue,
      );
    });
  });
}
