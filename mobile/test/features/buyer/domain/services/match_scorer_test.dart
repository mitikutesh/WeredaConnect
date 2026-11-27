import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';

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
    int? ownerReliabilityPct,
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
      ..ownerReliabilityPct = ownerReliabilityPct
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
      // A perfect match from a farmer at 20 against one at 100 differs by
      // wReliability * (1.0 - 0.2) = 0.08.
      final flaky = scorer.scoreListing(
        intent(),
        listing(ownerReliabilityPct: 20),
        now,
      );
      final proven = scorer.scoreListing(
        intent(),
        listing(ownerReliabilityPct: 100),
        now,
      );
      expect(proven - flaky, closeTo(MatchScorer.wReliability * 0.8, 1e-9));
      // A proven farmer's perfect match reaches the top of the range.
      expect(proven, closeTo(1.0, 1e-9));
    });

    test('reliability golden vectors (ESC-30, same table as the C# side)', () {
      // ownerReliabilityPct / 100 is the reliability signal, absent is the
      // neutral 0.7. A perfect match otherwise, so the score is
      // 0.90 + 0.10 * reliability. Rows: pct, signal, score.
      const vectors = <(int?, double, double)>[
        (null, 0.7, 0.97), // absent: flag off, under 3 orders, no copy yet
        (0, 0.0, 0.90),
        (50, 0.5, 0.95),
        (70, 0.7, 0.97), // equal to the neutral value: same score as absent
        (83, 0.83, 0.983),
        (100, 1.0, 1.0),
        (-5, 0.0, 0.90), // out of range values are clamped
        (150, 1.0, 1.0),
      ];
      for (final (pct, signal, score) in vectors) {
        expect(scorer.reliabilityFromPct(pct), closeTo(signal, 1e-12));
        final crop = listing(ownerReliabilityPct: pct);
        expect(scorer.reliabilityScore(crop), closeTo(signal, 1e-12));
        expect(
          scorer.scoreListing(intent(), crop, now),
          closeTo(score, 1e-9),
          reason: 'ownerReliabilityPct $pct',
        );
      }
    });

    test('the old farmerReliability no longer moves the score', () {
      // Rows synced before ESC-30 may still hold it (null or 1.0 in
      // practice). It is ignored: such a row scores as neutral.
      final legacy = scorer.scoreListing(
        intent(),
        listing(farmerReliability: 1.0),
        now,
      );
      expect(legacy, closeTo(0.97, 1e-9));
    });

    test('golden ranking order with reliability is identical across platforms',
        () {
      // Duplicated verbatim on the C# side. Four listings equal in everything
      // but the farmer's reliability, and one pricier listing from a proven
      // farmer: reliability only reorders listings that are otherwise close.
      final i = intent();
      final scores = <String, double>{
        'unknown': scorer.scoreListing(i, listing(), now), // 0.97
        'proven': scorer.scoreListing(
          i,
          listing(ownerReliabilityPct: 100),
          now,
        ), // 1.0
        'average': scorer.scoreListing(
          i,
          listing(ownerReliabilityPct: 50),
          now,
        ), // 0.95
        'flaky': scorer.scoreListing(
          i,
          listing(ownerReliabilityPct: 0),
          now,
        ), // 0.90
        'proven-pricey': scorer.scoreListing(
          i,
          listing(pricePerKg: 110, ownerReliabilityPct: 100),
          now,
        ), // 0.866667
      };
      final ranked = scores.keys.toList()
        ..sort((a, b) => scores[b]!.compareTo(scores[a]!));
      expect(
        ranked,
        ['proven', 'unknown', 'average', 'flaky', 'proven-pricey'],
      );
      expect(scores['proven-pricey'], closeTo(0.866667, 1e-6));
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

  group('rankTripSuggestions (ESC-30)', () {
    Trip trip(String id, double price, [int? reliabilityPct]) => Trip()
      ..tripId = id
      ..truckerId = 't-$id'
      ..truckerName = 'Trucker'
      ..truckerPhone = ''
      ..fromWoreda = 'Adama'
      ..toWoreda = 'Addis Ababa'
      ..departureDate = now
      ..capacity = 100
      ..pricePerQuintal = price
      ..ownerReliabilityPct = reliabilityPct
      ..status = TripStatus.open
      ..matchedListings = <String>[]
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    List<String> ranked(List<Trip> trips) =>
        scorer.rankTripSuggestions(trips).map((t) => t.tripId).toList();

    test('golden order: price first, then the more reliable trucker', () {
      // The same vector runs through MatchingService (Isar),
      // WebMatchingService and MatchScoringTests.SuggestTrips_GoldenOrder_
      // ReliabilityAtEqualPrice (C#).
      expect(
        ranked([
          trip('pricey-proven', 15, 100),
          trip('mid-unknown', 12),
          trip('mid-low', 12, 40),
          trip('mid-proven', 12, 95),
          trip('mid-seventy', 12, 70),
          trip('cheap-flaky', 10, 20),
        ]),
        [
          'cheap-flaky', // the cheapest stays first whatever its reliability
          'mid-proven',
          'mid-seventy', // ties with unknown (both 0.7): the trip id decides
          'mid-unknown', // unknown counts as the neutral 70
          'mid-low',
          'pricey-proven',
        ],
      );
    });

    test('no reliability on any trip: price, then the trip id', () {
      // Flags off, same departure. MATCH-01: the input order no longer
      // decides a tie (the phone's cache and the web's pages differ).
      expect(
        ranked([trip('b', 12), trip('c', 10), trip('a', 12), trip('d', 10)]),
        ['c', 'd', 'a', 'b'],
      );
    });

    test('equal price and reliability: the earlier departure first', () {
      final later = trip('a-later', 12)
        ..departureDate = now.add(const Duration(days: 1));
      expect(ranked([later, trip('z-sooner', 12)]), ['z-sooner', 'a-later']);
    });

    test('extreme and out of range values are clamped', () {
      expect(
        ranked([
          trip('zero', 10, 0),
          trip('negative', 10, -20),
          trip('over', 10, 250),
          trip('hundred', 10, 100),
        ]),
        // 250 and 100 both count as 1.0, 0 and -20 as 0.0: the id decides.
        ['hundred', 'over', 'negative', 'zero'],
      );
    });

    test('route score: at the same price the better route first', () {
      final routes = {'exact': 1.0, 'near': 0.85, 'named': 0.8};
      expect(
        scorer.rankTripSuggestions(
          [
            trip('named', 12, 100),
            trip('near', 12, 100),
            trip('exact', 12, 0),
            trip('cheap-near', 10),
          ],
          routeScore: (t) => routes[t.tripId] ?? 0.85,
        ).map((t) => t.tripId),
        ['cheap-near', 'exact', 'near', 'named'],
      );
    });

    test('an empty list stays empty and the input is not reordered', () {
      expect(scorer.rankTripSuggestions(const <Trip>[]), isEmpty);
      final input = [trip('b', 12), trip('a', 10)];
      scorer.rankTripSuggestions(input);
      expect(input.map((t) => t.tripId), ['b', 'a']);
    });
  });

  // ── MATCH-01 parity guards (MUST match server MatchScoringTests.cs) ───────
  // Trip pairing by centroid distance. The coordinates are literal and the
  // same numbers as the C# vectors (they are the WoredaCentroids entries,
  // pinned below), so the two implementations see identical inputs.
  group('trip pairing (MATCH-01)', () {
    const coords = <String, (double, double)>{
      'Hawassa': (7.05, 38.47),
      'Shashemene': (7.20, 38.60),
      'Addis Ababa': (9.03, 38.74),
      'Addis Abeba': (9.03, 38.74),
      'Bishoftu': (8.75, 38.98),
      'Adama': (8.54, 39.27),
      'Jimma': (7.67, 36.83),
    };

    LatLng? at(String name) {
      final c = coords[name];
      return c == null
          ? null
          : (LatLng()
            ..latitude = c.$1
            ..longitude = c.$2);
    }

    test('the literal coordinates are the shipped centroids', () {
      for (final MapEntry(key: name, value: (lat, lon)) in coords.entries) {
        final point = WoredaCentroids.lookup(name);
        expect(point?.latitude, lat, reason: name);
        expect(point?.longitude, lon, reason: name);
      }
      expect(WoredaCentroids.lookup('Hawassa Zuria'), isNull);
      expect(WoredaCentroids.lookup('Gimbo'), isNull);
    });

    test('end score golden vectors (same table as the C# side)', () {
      // Rows: trip end, its point, wanted place, its point, km (null: a
      // point unknown), score (null: the end does not serve). Duplicated
      // verbatim in MatchScoringTests.TripEndScore_GoldenVectors.
      const vectors = <(
        String,
        double?,
        double?,
        String,
        double?,
        double?,
        double?,
        double?,
      )>[
        // Same place.
        ('Hawassa', 7.05, 38.47, 'Hawassa', 7.05, 38.47, 0.0, 1.0),
        // Neighbouring town, 1 / (1 + 22 / 50).
        ('Shashemene', 7.20, 38.60, 'Hawassa', 7.05, 38.47, 22.0, 0.694444444),
        // Inside 40 km.
        ('Bishoftu', 8.75, 38.98, 'Adama', 8.54, 39.27, 39.5, 0.558659218),
        // Just outside 40 km.
        ('Bishoftu', 8.75, 38.98, 'Addis Ababa', 9.03, 38.74, 40.8, null),
        // Far.
        ('Jimma', 7.67, 36.83, 'Hawassa', 7.05, 38.47, 193.6, null),
        // No centroid: the whole-word name match.
        ('Hawassa Zuria', null, null, 'Hawassa', 7.05, 38.47, null, 0.6),
        // No centroid, another name.
        ('Gimbo', null, null, 'Hawassa', 7.05, 38.47, null, null),
        // Two spellings of one centroid: 0 km.
        ('Addis Abeba', 9.03, 38.74, 'Addis Ababa', 9.03, 38.74, 0.0, 1.0),
        // No centroid: the same name after normalising.
        ('Adama, Oromia', null, null, 'Ādama', null, null, null, 1.0),
        // A blank name never serves, even with a point.
        ('', 7.05, 38.47, 'Hawassa', 7.05, 38.47, 0.0, null),
      ];
      LatLng? point(double? lat, double? lon) => lat == null || lon == null
          ? null
          : (LatLng()
            ..latitude = lat
            ..longitude = lon);
      for (final (tripEnd, tLat, tLon, wanted, wLat, wLon, km, score)
          in vectors) {
        final tripPoint = point(tLat, tLon);
        final wantedPoint = point(wLat, wLon);
        final reason = '$tripEnd -> $wanted';
        final actualKm = scorer.tripEndKm(tripPoint, wantedPoint);
        if (km == null) {
          expect(actualKm, isNull, reason: reason);
        } else {
          expect(actualKm, closeTo(km, 1e-9), reason: reason);
        }
        final actual = scorer.tripEndScore(
          tripEnd,
          wanted,
          tripPoint: tripPoint,
          wantedPoint: wantedPoint,
        );
        if (score == null) {
          expect(actual, isNull, reason: reason);
        } else {
          expect(actual, closeTo(score, 1e-9), reason: reason);
        }
      }
    });

    final goldenNow = DateTime.utc(2026, 1, 1, 12);
    final day2 = DateTime.utc(2026, 1, 2, 21); // Ethiopia midnight, 3 Jan
    final deadline = DateTime.utc(2026, 1, 5, 21);

    Trip pt(
      String id,
      String from,
      String to,
      double price, {
      double? remaining,
      DateTime? departure,
      int? reliabilityPct,
      TripStatus status = TripStatus.open,
    }) =>
        Trip()
          ..tripId = id
          ..truckerId = 't-$id'
          ..truckerName = 'Trucker'
          ..truckerPhone = ''
          ..fromWoreda = from
          ..toWoreda = to
          ..departureDate = departure ?? day2
          ..capacity = 100
          ..remainingQuintals = remaining
          ..pricePerQuintal = price
          ..ownerReliabilityPct = reliabilityPct
          ..status = status
          ..matchedListings = <String>[]
          ..createdAt = goldenNow
          ..updatedAt = goldenNow
          ..version = 1;

    // The golden trip set, in this order on both sides. A crop in Hawassa,
    // delivered to Addis Ababa, 10 quintals, by 2026-01-05T21:00Z.
    List<Trip> goldenTrips() => [
          pt('exact', 'Hawassa', 'Addis Ababa', 12),
          pt('exact-twin', 'Hawassa', 'Addis Ababa', 12),
          pt('exact-proven', 'Hawassa', 'Addis Ababa', 12, reliabilityPct: 95),
          pt(
            'today',
            'Hawassa',
            'Addis Ababa',
            12,
            departure: DateTime.utc(2025, 12, 31, 21),
          ),
          pt('adjacent', 'Shashemene', 'Addis Ababa', 12, reliabilityPct: 100),
          pt('adjacent-cheap', 'Shashemene', 'Addis Ababa', 10),
          pt('fallback', 'Hawassa Zuria', 'Addis Ababa', 12),
          pt('far', 'Jimma', 'Addis Ababa', 9),
          pt('unknown', 'Gimbo', 'Addis Ababa', 9),
          pt('to-bishoftu', 'Hawassa', 'Bishoftu', 9),
          pt('full', 'Hawassa', 'Addis Ababa', 9, remaining: 0),
          pt('short', 'Hawassa', 'Addis Ababa', 9, remaining: 9.98),
          pt('slack', 'Hawassa', 'Addis Ababa', 11, remaining: 9.995),
          pt(
            'late',
            'Hawassa',
            'Addis Ababa',
            9,
            departure: DateTime.utc(2026, 1, 6, 21),
          ),
          pt('deadline-day', 'Hawassa', 'Addis Ababa', 13, departure: deadline),
          pt(
            'left',
            'Hawassa',
            'Addis Ababa',
            9,
            departure: DateTime.utc(2025, 12, 30, 21),
          ),
          pt(
            'matched',
            'Hawassa',
            'Addis Ababa',
            9,
            status: TripStatus.matched,
          ),
        ];

    List<String> suggested({
      int? orderLeadHours,
      double quantityNeeded = 10,
      DateTime? neededByDate,
    }) =>
        scorer
            .suggestTrips(
              goldenTrips(),
              listingWoreda: 'Hawassa',
              deliveryWoreda: 'Addis Ababa',
              quantityNeeded: quantityNeeded,
              now: goldenNow,
              orderLeadHours: orderLeadHours,
              neededByDate: neededByDate ?? deadline,
              locate: at,
            )
            .map((t) => t.tripId)
            .toList();

    test('golden order (same as the C# SuggestTrips vector)', () {
      expect(suggested(), [
        'adjacent-cheap', // price stays the first key
        'slack', // 9.995 room for 10: inside the 0.01 tolerance
        'exact-proven', // exact route, reliability 95
        'today', // exact, neutral: the earlier departure first
        'exact',
        'exact-twin', // same as exact in all but the id
        'adjacent', // 22 km away ranks below exact at the same price
        'fallback', // no centroid: the whole-word name match, 0.8
        'deadline-day', // departs on the deadline
      ]);
      // Dropped: far (193.6 km), unknown (no centroid, other name),
      // to-bishoftu (40.8 km from Addis Ababa), full, short, late, left,
      // matched.
    });

    test('route score golden vectors', () {
      final byId = {for (final t in goldenTrips()) t.tripId: t};
      double? route(String id) => scorer.tripRouteScore(
            byId[id]!,
            listingWoreda: 'Hawassa',
            deliveryWoreda: 'Addis Ababa',
            locate: at,
          );
      expect(route('exact'), 1.0);
      expect(route('adjacent'), closeTo(0.847222222, 1e-9));
      expect(route('fallback'), closeTo(0.8, 1e-9));
      expect(route('far'), isNull);
      expect(route('unknown'), isNull);
      expect(route('to-bishoftu'), isNull);
      // A trip ending 39.5 km from the delivery woreda serves it.
      expect(
        scorer.tripRouteScore(
          pt('to-near', 'Hawassa', 'Bishoftu', 12),
          listingWoreda: 'Hawassa',
          deliveryWoreda: 'Adama',
          locate: at,
        ),
        closeTo(0.779329609, 1e-9),
      );
    });

    test('reverse golden vectors (same table as the C# side)', () {
      // A trip running the other way fits each end within 40 km when the
      // crop and the delivery woredas are close; it is dropped when the
      // swapped route scores higher. Equal scores stay. Rows: crop woreda,
      // delivery woreda, trip from, trip to, route (null: not suggested).
      // Duplicated verbatim in MatchScoringTests.TripRouteScore_Reverse.
      const vectors = <(String, String, String, String, double?)>[
        ('Adama', 'Bishoftu', 'Adama', 'Bishoftu', 1.0), // forward
        ('Adama', 'Bishoftu', 'Bishoftu', 'Adama', null), // reverse
        ('Adama', 'Bishoftu', 'Adama', 'Adama', 0.779329609), // tie stays
        ('Hawassa', 'Shashemene', 'Shashemene', 'Hawassa', null), // reverse
        ('Hawassa', 'Shashemene', 'Hawassa', 'Shashemene', 1.0), // forward
        ('Hawassa', 'Hawassa', 'Hawassa', 'Hawassa', 1.0), // one woreda
        ('Hawassa', 'Hawassa', 'Shashemene', 'Hawassa', 0.847222222), // tie
      ];
      for (final (listing, delivery, from, to, route) in vectors) {
        final actual = scorer.tripRouteScore(
          pt('r', from, to, 12),
          listingWoreda: listing,
          deliveryWoreda: delivery,
          locate: at,
        );
        final reason = '$from -> $to for $listing -> $delivery';
        if (route == null) {
          expect(actual, isNull, reason: reason);
        } else {
          expect(actual, closeTo(route, 1e-9), reason: reason);
        }
      }
    });

    test('reverse golden order: the cheapest trip runs the wrong way', () {
      // Duplicated verbatim in MatchScoringTests.SuggestTrips_Reverse.
      final ids = scorer
          .suggestTrips(
            [
              pt('forward', 'Adama', 'Bishoftu', 12),
              pt('reverse', 'Bishoftu', 'Adama', 9),
              pt('local', 'Adama', 'Adama', 11),
            ],
            listingWoreda: 'Adama',
            deliveryWoreda: 'Bishoftu',
            quantityNeeded: 10,
            now: goldenNow,
            neededByDate: deadline,
            locate: at,
          )
          .map((t) => t.tripId)
          .toList();
      expect(ids, ['local', 'forward']);
    });

    test('with the 24 hour order lead the trip leaving today is dropped', () {
      expect(suggested(orderLeadHours: 24), [
        'adjacent-cheap',
        'slack',
        'exact-proven',
        'exact',
        'exact-twin',
        'adjacent',
        'fallback',
        'deadline-day',
      ]);
    });

    test('no positive need: any trip with a quintal of room', () {
      // The full trip is still dropped; the short one (9.98) now fits.
      final ids = suggested(quantityNeeded: 0);
      expect(ids, isNot(contains('full')));
      expect(ids, contains('short'));
    });

    test('the input order does not change the result', () {
      final forward = scorer.suggestTrips(
        goldenTrips(),
        listingWoreda: 'Hawassa',
        deliveryWoreda: 'Addis Ababa',
        quantityNeeded: 10,
        now: goldenNow,
        neededByDate: deadline,
        locate: at,
      );
      final backward = scorer.suggestTrips(
        goldenTrips().reversed,
        listingWoreda: 'Hawassa',
        deliveryWoreda: 'Addis Ababa',
        quantityNeeded: 10,
        now: goldenNow,
        neededByDate: deadline,
        locate: at,
      );
      expect(
        backward.map((t) => t.tripId),
        forward.map((t) => t.tripId),
      );
    });

    test('by default the centroids come from WoredaCentroids', () {
      final ids = scorer
          .suggestTrips(
            goldenTrips(),
            listingWoreda: 'Hawassa',
            deliveryWoreda: 'Addis Ababa',
            quantityNeeded: 10,
            now: goldenNow,
            neededByDate: deadline,
          )
          .map((t) => t.tripId)
          .toList();
      expect(ids, suggested());
    });
  });
}
