import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_projection.dart';

/// LT-10: the equirectangular projection fitted to the trail, the stops and
/// the drop-off, with padding and the cos(mean latitude) correction.
void main() {
  const size = Size(320, 200);
  const addis = GeoPoint(9.025, 38.7469);
  const adama = GeoPoint(8.55, 39.2667);

  test('nothing to show gives no projection', () {
    expect(TrackProjection.fit(const [], size), isNull);
    expect(TrackProjection.fit(const [addis], Size.zero), isNull);
  });

  test('fits the points inside the padding, centred', () {
    final projection = TrackProjection.fit(const [addis, adama], size)!;
    final a = projection.project(addis);
    final b = projection.project(adama);

    for (final p in [a, b]) {
      expect(p.dx, inInclusiveRange(28 - 1e-9, size.width - 28 + 1e-9));
      expect(p.dy, inInclusiveRange(28 - 1e-9, size.height - 28 + 1e-9));
    }
    // North is up, east is right.
    expect(a.dy, lessThan(b.dy));
    expect(a.dx, lessThan(b.dx));
    // The box of the two points is centred on the canvas.
    expect((a.dx + b.dx) / 2, closeTo(size.width / 2, 1e-9));
    expect((a.dy + b.dy) / 2, closeTo(size.height / 2, 1e-9));
    // The trip is taller than the canvas allows, so north-south touches the
    // padding.
    expect(math.min(a.dy, b.dy), closeTo(28, 1e-9));
    expect(math.max(a.dy, b.dy), closeTo(size.height - 28, 1e-9));
  });

  test('a kilometre is as long east-west as north-south', () {
    // 0.1 degree of longitude at 9 degrees north is cos(9) shorter on the
    // ground than 0.1 degree of latitude.
    const origin = GeoPoint(9, 38.5);
    const east = GeoPoint(9, 38.6);
    const north = GeoPoint(9.1, 38.5);
    final projection =
        TrackProjection.fit(const [origin, east, north], size, padding: 10)!;
    final o = projection.project(origin);
    final dx = projection.project(east).dx - o.dx;
    final dy = o.dy - projection.project(north).dy;
    expect(dx / dy, closeTo(math.cos(9.05 * math.pi / 180), 1e-9));
    expect(dy / projection.pixelsPerKm, closeTo(0.1 * kmPerDegree, 1e-9));
  });

  test('a single point gets a view 20 km wide around it', () {
    final projection = TrackProjection.fit(const [addis], size)!;
    expect(projection.project(addis), const Offset(160, 100));
    // The short side (200 px less padding) spans 20 km.
    expect(projection.pixelsPerKm, closeTo((200 - 56) / 20, 1e-9));
  });

  test('unproject inverts project', () {
    final projection = TrackProjection.fit(const [addis, adama], size)!;
    for (final p in const [addis, adama, GeoPoint(8.8, 38.95)]) {
      final back = projection.unproject(projection.project(p));
      expect(back.latitude, closeTo(p.latitude, 1e-9));
      expect(back.longitude, closeTo(p.longitude, 1e-9));
    }
  });

  test('the padded bounds are what the whole canvas shows', () {
    final projection = TrackProjection.fit(const [addis, adama], size)!;
    final bounds = projection.bounds;
    expect(bounds.contains(addis), isTrue);
    expect(bounds.contains(adama), isTrue);
    expect(bounds.north, greaterThan(addis.latitude));
    expect(bounds.south, lessThan(adama.latitude));
    expect(bounds.contains(const GeoPoint(12.6, 37.47)), isFalse);
    final corner = projection.project(GeoPoint(bounds.north, bounds.west));
    expect(corner.dx, closeTo(0, 1e-6));
    expect(corner.dy, closeTo(0, 1e-6));
  });
}
