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

  // Foundations board: pale roads on the map ground. The app has no roads
  // file, so the one road drawn is the one the truck uses.
  group('the road under the route', () {
    Future<RenderObject> pumpMap(
      WidgetTester tester,
      DeliveryTrack track, {
      DateTime? now,
    }) async {
      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Center(
            child: SizedBox(
              width: 320,
              height: 200,
              child: TrackMap(
                track: track,
                now: now ?? at(9, 0),
                places: corridorPlaces,
              ),
            ),
          ),
        ),
      );
      return tester.renderObject(
        find.descendant(
          of: find.byType(TrackMap),
          matching: find.byType(CustomPaint),
        ),
      );
    }

    bool roadBand(Symbol method, List<dynamic> args) =>
        method == #drawPath &&
        (args[1] as Paint).color.toARGB32() == AppTheme.mapRoad.toARGB32() &&
        (args[1] as Paint).strokeWidth == 14;
    bool sideRoad(Symbol method, List<dynamic> args) =>
        method == #drawPath &&
        (args[1] as Paint).color.toARGB32() == AppTheme.mapRoad.toARGB32() &&
        (args[1] as Paint).strokeWidth == 7;

    testWidgets('a pale band runs from the pickup to the drop-off',
        (tester) async {
      final map = await pumpMap(tester, corridorTrack());
      expect(map, paints..something(roadBand));
      // The band covers the driven trail and the road ahead: its bounds
      // hold the pickup, every check-in and the drop-off.
      final track = corridorTrack();
      final frame = TrackMapFrame.of(
        track: track,
        now: at(9, 0),
        size: const Size(320, 200),
      )!;
      Rect? bounds;
      expect(
        map,
        paints
          ..something((method, args) {
            if (!roadBand(method, args)) return false;
            bounds = (args[0] as Path).getBounds();
            return true;
          }),
      );
      for (final point in [
        track.pickup!,
        for (final fix in track.trail) fix.point,
        track.dropoff!,
      ]) {
        expect(
          bounds!.inflate(0.5).contains(frame.projection.project(point)),
          isTrue,
        );
      }
    });

    testWidgets('short crossings at the ends, the same pale colour',
        (tester) async {
      final map = await pumpMap(tester, corridorTrack());
      expect(map, paints..something(sideRoad));
    });

    testWidgets('an ended track keeps the road it drove', (tester) async {
      final map = await pumpMap(
        tester,
        corridorTrack(sharing: TrackSharing.ended),
        now: at(12, 0),
      );
      expect(map, paints..something(roadBand));
    });

    testWidgets('one point alone has no road', (tester) async {
      final map = await pumpMap(
        tester,
        DeliveryTrack(
          deliveryId: 'del-101',
          sharing: TrackSharing.live,
          asOf: asOf,
          pickup: const GeoPoint(8.88, 38.79),
          pickedUpAt: at(5, 5),
        ),
      );
      expect(
        map,
        paints..everything((method, args) => !roadBand(method, args)),
      );
      expect(tester.takeException(), isNull);
    });

    test('the road colour is part of the style', () {
      expect(const TrackMapStyle().road, AppTheme.mapRoad);
      expect(
        const TrackMapStyle(road: AppTheme.ink),
        isNot(const TrackMapStyle()),
      );
    });
  });

  // Web deliveries board: zoom in and zoom out on the pane's map.
  group('zoom', () {
    const size = Size(320, 200);

    test('a new zoom repaints', () {
      final track = corridorTrack();
      TrackMapPainter zoomed(double zoom) => TrackMapPainter(
            track: track,
            now: at(9, 0),
            places: corridorPlaces,
            languageCode: 'en',
            zoom: zoom,
          );
      expect(zoomed(1).shouldRepaint(zoomed(1)), isFalse);
      expect(zoomed(1).shouldRepaint(zoomed(1.5)), isTrue);
    });

    test('zooming in spreads the route about the truck, which stays in view',
        () {
      final track = corridorTrack();
      TrackMapFrame frame(double zoom) => TrackMapFrame.of(
            track: track,
            now: at(9, 0),
            size: size,
            zoom: zoom,
          )!;
      final fitted = frame(1);
      final closer = frame(2.25);
      expect(
        closer.projection.pixelsPerKm,
        closeTo(fitted.projection.pixelsPerKm * 2.25, 1e-9),
      );
      expect((Offset.zero & size).contains(closer.truck!), isTrue);
      const middle = Offset(160, 100);
      expect(
        (closer.truck! - middle).distance,
        lessThan((fitted.truck! - middle).distance),
      );
    });

    test('an ended track zooms about the middle of its route', () {
      final frame = TrackMapFrame.of(
        track: corridorTrack(sharing: TrackSharing.ended),
        now: at(12, 0),
        size: size,
        zoom: 2,
      )!;
      expect(frame.truck, isNull);
    });
  });
}
