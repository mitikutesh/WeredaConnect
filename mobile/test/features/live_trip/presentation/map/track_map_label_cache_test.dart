import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';

import '../../live_trip_fixtures.dart';

/// LT-11: a repaint for a new `now` (the age ticking on a track screen)
/// reuses the town labels; a new track, a new size or a new language chooses
/// them again.
void main() {
  const size = Size(320, 200);

  void paint(TrackMapPainter painter, {Size at = size}) {
    final recorder = PictureRecorder();
    painter.paint(Canvas(recorder), at);
    recorder.endRecording().dispose();
  }

  TrackMapPainter painter(
    TrackLabelCache cache, {
    required DateTime now,
    DeliveryTrack? track,
    String lang = 'en',
  }) =>
      TrackMapPainter(
        track: track ?? corridor,
        now: now,
        places: corridorPlaces,
        languageCode: lang,
        labelCache: cache,
      );

  test('a new now reuses the labels', () {
    final cache = TrackLabelCache();
    final first = painter(cache, now: at(9, 0));
    paint(first);
    final second = painter(cache, now: at(9, 1));
    paint(second);
    expect(cache.selections, 1);
    expect(identical(first.labels, second.labels), isTrue);
    expect(second.labels, isNotEmpty);
  });

  test('a new track, size or language chooses them again', () {
    final cache = TrackLabelCache();
    paint(painter(cache, now: at(9, 0)));
    paint(painter(cache, now: at(9, 0), track: corridorTrack()));
    expect(cache.selections, 2);
    paint(
      painter(cache, now: at(9, 0), track: corridor),
      at: const Size(300, 300),
    );
    expect(cache.selections, 3);
    paint(painter(cache, now: at(9, 0), track: corridor, lang: 'am'));
    expect(cache.selections, 4);
  });

  testWidgets('TrackMap keeps one cache while the clock ticks', (tester) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    Future<TrackMapPainter> pump(DateTime now) async {
      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: TrackMap(track: corridor, now: now, places: corridorPlaces),
        ),
      );
      return tester
          .widget<CustomPaint>(
            find.descendant(
              of: find.byType(TrackMap),
              matching: find.byType(CustomPaint),
            ),
          )
          .painter! as TrackMapPainter;
    }

    final first = await pump(at(9, 0));
    final second = await pump(at(9, 5));
    expect(identical(first.labelCache, second.labelCache), isTrue);
    expect(second.labelCache!.selections, 1);
    expect(identical(first.labels, second.labels), isTrue);
  });
}

final corridor = corridorTrack();
