import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

import '../../../../helpers/tolerant_golden_comparator.dart';
import '../../live_trip_fixtures.dart';

const _size = Size(320, 200);
const _boundary = Key('track_map_boundary');

/// LT-10: the map painter in its six states (fresh, normal, late, paused,
/// scans only, ended). The text is drawn in the test font (boxes), so the
/// goldens are the same on every machine; the comparator absorbs the
/// anti-aliasing noise between macOS and the Linux CI runner.
///
/// Regenerate after a deliberate change with
/// `flutter test --update-goldens test/features/live_trip/presentation/map/track_map_golden_test.dart`.
void main() {
  setUpAll(() {
    TolerantGoldenComparator.install(
      'track_map_golden_test.dart',
      tolerance: 0.05,
    );
  });

  Future<TrackMapPainter> pumpMap(
    WidgetTester tester,
    DeliveryTrack track,
    DateTime now,
  ) async {
    tester.view.physicalSize = _size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: RepaintBoundary(
          key: _boundary,
          child: SizedBox.fromSize(
            size: _size,
            child: TrackMap(
              track: track,
              now: now,
              places: corridorPlaces,
              semanticLabel: 'Truck near Mojo',
            ),
          ),
        ),
      ),
    );
    final paint = tester.widget<CustomPaint>(
      find.descendant(
        of: find.byType(TrackMap),
        matching: find.byType(CustomPaint),
      ),
    );
    return paint.painter! as TrackMapPainter;
  }

  // Every state, the band it must show, and its golden.
  final states = <String, (DeliveryTrack, DateTime, TrackFreshness)>{
    'fresh': (
      corridorTrack(dropoffNote: 'Bus station'),
      at(8, 52),
      TrackFreshness.fresh,
    ),
    'normal': (corridorTrack(), at(9, 30), TrackFreshness.normal),
    'late': (corridorTrack(), at(10, 5), TrackFreshness.late),
    'paused': (
      corridorTrack(sharing: TrackSharing.paused, pausedUntil: at(9, 40)),
      at(9, 10),
      TrackFreshness.paused,
    ),
    'scans_only': (
      corridorTrack(
        sharing: TrackSharing.declined,
        trail: [
          fix(8.88, 38.79, at(5, 5), source: 'scan'),
          fix(8.83, 38.86, at(5, 40), source: 'scan'),
        ],
      ),
      at(6, 10),
      TrackFreshness.scansOnly,
    ),
    // The server's ended answer has no coordinates; this is the last live
    // snapshot drawn in the ended style (no truck, no halo, no road ahead).
    'ended': (
      corridorTrack(sharing: TrackSharing.ended),
      at(12, 0),
      TrackFreshness.ended,
    ),
  };

  for (final MapEntry(key: name, value: (track, now, band)) in states.entries) {
    testWidgets('$name state', (tester) async {
      expect(trackFreshness(track, now), band);
      final painter = await pumpMap(tester, track, now);

      expect(painter.labels.length, lessThanOrEqualTo(10));
      expect(painter.labels, isNotEmpty);
      await expectLater(
        find.byKey(_boundary),
        matchesGoldenFile('goldens/track_map_$name.png'),
      );
    });
  }

  testWidgets('the truck\'s town is labelled first', (tester) async {
    final painter = await pumpMap(tester, corridorTrack(), at(9, 0));
    expect(painter.labels.first.place!.en, 'Mojo');
  });

  // The goldens accept 1% of the pixels changed, more than the truck marker
  // (about 0.4% of the map) or an ended track drawn with its truck and road
  // ahead (about 0.7%). So both are checked on the paint calls as well: the
  // truck's 9 px outline is the only circle of that size, the road ahead the
  // only straight line.
  testWidgets('the truck and the road ahead show while live, never after',
      (tester) async {
    final map = find.descendant(
      of: find.byType(TrackMap),
      matching: find.byType(CustomPaint),
    );
    bool truck(Symbol method, List<dynamic> args) =>
        method == #drawCircle && args[1] == 9.0;

    await pumpMap(tester, corridorTrack(), at(9, 0));
    expect(tester.renderObject(map), paints..something(truck));
    expect(tester.renderObject(map), paints..line());

    await pumpMap(
      tester,
      corridorTrack(sharing: TrackSharing.ended),
      at(12, 0),
    );
    expect(
      tester.renderObject(map),
      paints..everything((method, args) => !truck(method, args)),
    );
    expect(tester.renderObject(map), isNot(paints..line()));
  });

  testWidgets('an ended answer without coordinates draws only the background',
      (tester) async {
    final ended = DeliveryTrack.fromJson(endedTrackJson());
    final painter = await pumpMap(tester, ended, at(12, 0));
    expect(painter.labels, isEmpty);
    expect(tester.takeException(), isNull);
  });

  testWidgets('the map carries its screen reader label', (tester) async {
    final handle = tester.ensureSemantics();
    await pumpMap(tester, corridorTrack(), at(9, 0));
    expect(find.bySemanticsLabel('Truck near Mojo'), findsOneWidget);
    handle.dispose();
  });

  testWidgets('without places the map labels from the bundled list',
      (tester) async {
    PlaceRepository.instance.loadFromStringForTest(
      '[{"id":"gn-330491","en":"Mojo","lat":8.5868,"lng":39.1211,'
      '"population":61300}]',
    );
    tester.view.physicalSize = _size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: TrackMap(track: corridorTrack(), now: at(9, 0)),
      ),
    );
    await tester.pump();
    final painter = tester
        .widget<CustomPaint>(
          find.descendant(
            of: find.byType(TrackMap),
            matching: find.byType(CustomPaint),
          ),
        )
        .painter! as TrackMapPainter;
    expect(painter.places.map((p) => p.en), ['Mojo']);
  });

  test('repaints only when what it draws changes', () {
    final track = corridorTrack();
    TrackMapPainter painter({
      DeliveryTrack? t,
      DateTime? now,
      String lang = 'en',
    }) =>
        TrackMapPainter(
          track: t ?? track,
          now: now ?? at(9, 0),
          places: corridorPlaces,
          languageCode: lang,
        );

    expect(painter().shouldRepaint(painter()), isFalse);
    expect(painter().shouldRepaint(painter(now: at(9, 1))), isTrue);
    expect(painter().shouldRepaint(painter(t: corridorTrack())), isTrue);
    expect(painter().shouldRepaint(painter(lang: 'am')), isTrue);
  });
}
