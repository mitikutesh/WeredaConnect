import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

import '../../live_trip_fixtures.dart';

/// Design canvas page M: the map frames the road ahead or the whole route
/// ("Whole route" on the buyer's live trip), in the role's colours.
void main() {
  TrackMapPainter painter(TrackMapFocus focus, {DeliveryTrack? track}) =>
      TrackMapPainter(
        track: track ?? corridorTrack(),
        now: at(9, 0),
        places: corridorPlaces,
        languageCode: 'en',
        focus: focus,
      );

  test('a new focus repaints', () {
    final track = corridorTrack();
    expect(
      painter(TrackMapFocus.wholeRoute, track: track)
          .shouldRepaint(painter(TrackMapFocus.roadAhead, track: track)),
      isTrue,
    );
  });

  testWidgets('the road ahead zooms in on what is left', (tester) async {
    Future<int> labelled(TrackMapFocus focus) async {
      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Center(
            child: SizedBox(
              width: 320,
              height: 200,
              child: TrackMap(
                key: ValueKey(focus),
                track: corridorTrack(),
                now: at(9, 0),
                places: corridorPlaces,
                focus: focus,
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
      return (paint.painter! as TrackMapPainter).labels.length;
    }

    // The whole route shows more towns than the stretch still ahead.
    final whole = await labelled(TrackMapFocus.wholeRoute);
    final ahead = await labelled(TrackMapFocus.roadAhead);
    expect(tester.takeException(), isNull);
    expect(ahead, lessThanOrEqualTo(whole));
    expect(whole, greaterThan(0));
  });

  testWidgets('an ended track has no road ahead to frame', (tester) async {
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: TrackMap(
          track: corridorTrack(sharing: TrackSharing.ended),
          now: at(12, 0),
          places: corridorPlaces,
          focus: TrackMapFocus.roadAhead,
        ),
      ),
    );
    expect(tester.takeException(), isNull);
  });

  test('the person\'s own trail and pickup pin take the role colour', () {
    for (final colors in [RoleColors.buyer, RoleColors.farmer]) {
      final style = TrackMapStyle.forRole(colors);
      expect(style.trail, colors.primary);
      expect(style.pin, colors.primary);
      // The buyer's drop-off is slate, other stops unnamed grey rings.
      expect(style.dropoff, AppTheme.slate);
      expect(style.otherStop, AppTheme.fieldBorder);
      expect(style.truck, AppTheme.ink);
      expect(style.truckGlyph, AppTheme.orange);
    }
  });
}
