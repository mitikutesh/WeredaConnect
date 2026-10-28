import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_labels.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_projection.dart';

import '../../live_trip_fixtures.dart';

/// Every character 8 px wide, every label 12 px high.
Size _measure(String text) => Size(text.length * 8.0, 12);

List<String> _ids(Iterable<Place> places) => [for (final p in places) p.id];

/// LT-10: towns inside the padded bounds ranked by population plus the town
/// nearest to each check-in, greedy collision removal, at most 10 (spec
/// section 8), and the nearest town for the trip log rows.
void main() {
  const corridor = GeoBounds(south: 8.3, west: 38.6, north: 9.1, east: 39.4);
  final fixes = [for (final f in corridorTrail) f.point];

  group('nearestPlace', () {
    test('finds the nearest town and its distance', () {
      final near = nearestPlace(corridorPlaces, const GeoPoint(8.76, 38.99))!;
      expect(near.place.en, 'Bishoftu');
      expect(near.distanceKm, closeTo(1.5, 0.3));
    });

    test('the last corridor check-in is nearest to Mojo', () {
      final near = nearestPlace(corridorPlaces, corridorTrail.last.point)!;
      expect(near.place.en, 'Mojo');
    });

    test('maxKm leaves a far town out', () {
      const desert = GeoPoint(6.5, 44.5);
      expect(nearestPlace(corridorPlaces, desert), isNotNull);
      expect(nearestPlace(corridorPlaces, desert, maxKm: 50), isNull);
    });

    test('places without coordinates are skipped; none gives null', () {
      const noWhere = Place(id: 'gn-0', en: 'Nowhere', population: 99);
      expect(nearestPlace(const [noWhere], const GeoPoint(9, 38)), isNull);
      expect(nearestPlace(const [], const GeoPoint(9, 38)), isNull);
    });
  });

  group('trackLabelCandidates', () {
    test(
        'the truck\'s town first, then towns by population, then the towns '
        'of the earlier check-ins, newest first', () {
      final candidates = trackLabelCandidates(
        places: corridorPlaces,
        bounds: corridor,
        fixes: fixes,
      );
      expect(_ids(candidates), [
        'gn-330491', // Mojo, nearest to the last check-in
        'gn-344979', // Addis Ababa
        'gn-330186', // Adama
        'gn-339666', // Bishoftu (also nearest to the third check-in)
        'gn-338488', // Dukem, nearest to the second check-in
        'gn-10378531', // Akaki, nearest to the pickup scan
      ]);
    });

    test('towns outside the bounds are left out', () {
      final candidates = trackLabelCandidates(
        places: corridorPlaces,
        bounds: const GeoBounds(
          south: 8.5,
          west: 38.9,
          north: 8.9,
          east: 39.3,
        ),
        fixes: fixes,
      );
      expect(candidates.map((p) => p.en), isNot(contains('Addis Ababa')));
      expect(candidates.map((p) => p.en), isNot(contains('Gonder')));
      expect(candidates.first.en, 'Mojo');
    });

    test('a town without population is labelled only near a check-in', () {
      final candidates = trackLabelCandidates(
        places: corridorPlaces,
        bounds: corridor,
        fixes: const [],
      );
      expect(_ids(candidates), [
        'gn-344979',
        'gn-330186',
        'gn-339666',
        'gn-330491',
      ]);
    });

    test('stops at the limit', () {
      final candidates = trackLabelCandidates(
        places: corridorPlaces,
        bounds: corridor,
        fixes: fixes,
        limit: 2,
      );
      expect(_ids(candidates), ['gn-330491', 'gn-344979']);
    });

    test('equal populations rank by id, so the order never changes', () {
      const a = Place(id: 'gn-2', en: 'B', lat: 8.5, lng: 39, population: 5);
      const b = Place(id: 'gn-1', en: 'A', lat: 8.6, lng: 39, population: 5);
      expect(
        _ids(
          trackLabelCandidates(
            places: const [a, b],
            bounds: corridor,
            fixes: const [],
          ),
        ),
        ['gn-1', 'gn-2'],
      );
    });
  });

  group('placeTrackLabels', () {
    const canvas = Size(300, 200);

    TrackLabelCandidate at(String text, double x, double y) =>
        TrackLabelCandidate(text: text, anchor: Offset(x, y));

    test('a label sits right of its dot, vertically centred', () {
      final labels = placeTrackLabels(
        candidates: [at('Mojo', 100, 50)],
        canvas: canvas,
        measure: _measure,
      );
      expect(labels.single.rect, const Rect.fromLTWH(104, 44, 32, 12));
    });

    test('a label that would leave the canvas on the right goes left', () {
      final labels = placeTrackLabels(
        candidates: [at('Adama', 290, 50)],
        canvas: canvas,
        measure: _measure,
      );
      expect(labels.single.rect, const Rect.fromLTWH(246, 44, 40, 12));
    });

    test('greedy: a label touching an earlier one is dropped', () {
      final labels = placeTrackLabels(
        candidates: [
          at('Addis Ababa', 100, 50),
          at('Akaki', 110, 56), // overlaps Addis Ababa on both sides
          at('Dukem', 100, 120),
        ],
        canvas: canvas,
        measure: _measure,
      );
      expect(labels.map((l) => l.text), ['Addis Ababa', 'Dukem']);
    });

    test('a label blocked on the right tries the left', () {
      final labels = placeTrackLabels(
        candidates: [
          at('Bishoftu', 100, 50),
          at('Dukem', 140, 50),
        ],
        canvas: canvas,
        measure: _measure,
      );
      // Bishoftu takes 104 to 168; Dukem's right side would overlap, its
      // left side (96 to 136) overlaps too, so it is dropped.
      expect(labels.map((l) => l.text), ['Bishoftu']);

      final spaced = placeTrackLabels(
        candidates: [
          at('Bishoftu', 100, 50),
          at('Mojo', 230, 50),
        ],
        canvas: canvas,
        measure: _measure,
        reserved: const [Rect.fromLTWH(232, 40, 40, 20)],
      );
      // Mojo's right side is reserved (the truck), so it goes left.
      expect(spaced.last.rect, const Rect.fromLTWH(194, 44, 32, 12));
    });

    test('dots off the canvas and empty names get no label', () {
      final labels = placeTrackLabels(
        candidates: [at('Gondar', -5, 50), at('', 50, 50), at('Mojo', 50, 250)],
        canvas: canvas,
        measure: _measure,
      );
      expect(labels, isEmpty);
    });

    test('reserved areas (pins, the truck) stay free', () {
      final labels = placeTrackLabels(
        candidates: [at('Mojo', 100, 50)],
        canvas: canvas,
        measure: _measure,
        reserved: const [
          Rect.fromLTWH(106, 40, 60, 20),
          Rect.fromLTWH(34, 40, 60, 20),
        ],
      );
      expect(labels, isEmpty);
    });

    test('a dot under a pin gets its label just past the pin', () {
      // Adama sits under the drop-off pin (a 14 px box around 200, 50).
      const pin = Rect.fromLTWH(193, 43, 14, 14);
      final right = placeTrackLabels(
        candidates: [at('Adama', 200, 50)],
        canvas: canvas,
        measure: _measure,
        reserved: const [pin],
      );
      expect(right.single.rect, const Rect.fromLTWH(209, 44, 40, 12));

      final left = placeTrackLabels(
        candidates: [at('Adama', 200, 50)],
        canvas: const Size(240, 200),
        measure: _measure,
        reserved: const [pin],
      );
      expect(left.single.rect, const Rect.fromLTWH(151, 44, 40, 12));
    });

    test('at most 10 labels', () {
      final labels = placeTrackLabels(
        candidates: [
          for (var i = 0; i < 30; i++) at('T$i', 10, 20 + i * 15.0),
        ],
        canvas: const Size(300, 600),
        measure: _measure,
      );
      expect(labels, hasLength(maxTrackMapLabels));
      expect(labels.last.text, 'T9');
    });
  });

  group('selectTrackLabels', () {
    final projection = TrackProjection.fit(
      [...fixes, const GeoPoint(8.55, 39.27)],
      const Size(360, 240),
    )!;

    test('names the towns in the app language', () {
      String names(String code) => selectTrackLabels(
            places: corridorPlaces,
            projection: projection,
            fixes: fixes,
            languageCode: code,
            measure: _measure,
          ).map((l) => l.text).join(', ');

      final en = names('en');
      expect(en, contains('Adama'));
      expect(en, isNot(contains('Nazr')));
      expect(names('am'), contains('ሞጆ'));
      expect(names('om'), contains('Mojoo'));
    });

    test('never more than 10 and never overlapping', () {
      final many = [
        for (var i = 0; i < 40; i++)
          Place(
            id: 'gn-t$i',
            en: 'Town $i',
            lat: 8.5 + (i % 8) * 0.06,
            lng: 38.8 + (i ~/ 8) * 0.1,
            population: 1000 + i,
          ),
      ];
      final labels = selectTrackLabels(
        places: many,
        projection: projection,
        fixes: fixes,
        languageCode: 'en',
        measure: _measure,
      );
      expect(labels.length, lessThanOrEqualTo(maxTrackMapLabels));
      for (var i = 0; i < labels.length; i++) {
        for (var j = i + 1; j < labels.length; j++) {
          expect(labels[i].rect.overlaps(labels[j].rect), isFalse);
        }
      }
    });
  });
}
