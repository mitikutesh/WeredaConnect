import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_labels.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_projection.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Two check-ins further apart than this are joined by a dotted line: the
/// road the truck took in between is unknown.
const Duration trackGapOver = Duration(minutes: 90);

/// The map's ground (the BuyerLiveTrip, BuyerDeliveries and FarmerJourney
/// boards of design canvas page M). Needs a token: no theme colour is this
/// warm grey.
const Color _mapGround = Color(0xFFDCD5C9);

/// The dashed edge of the halo around the last check-in (the Foundations
/// board, page M). Needs a token: a lighter brown than `orangeInk`.
const Color _haloEdge = Color(0xFFA85518);

/// What the map frames.
enum TrackMapFocus {
  /// Everything: the pickup, every check-in, the stops and the drop-off.
  wholeRoute,

  /// The truck and what is still ahead of it (the stops to come and the
  /// drop-off). The same as [wholeRoute] when there is no truck or no
  /// drop-off to frame.
  roadAhead,
}

/// Colours and sizes of the track map (the Foundations board, design canvas
/// page M: the map language).
@immutable
class TrackMapStyle {
  const TrackMapStyle({
    this.background = _mapGround,
    this.town = AppTheme.slate,
    this.labelText = AppTheme.inkSoft,
    this.labelOutline = _mapGround,
    this.trail = AppTheme.ink,
    this.ended = AppTheme.slate,
    this.ahead = AppTheme.ink,
    this.halo = AppTheme.orange,
    this.haloEdge = _haloEdge,
    this.truck = AppTheme.ink,
    this.truckGlyph = AppTheme.orange,
    this.truckMuted = AppTheme.inkMuted,
    this.pin = AppTheme.ink,
    this.dropoff = AppTheme.slate,
    this.otherStop = AppTheme.fieldBorder,
    this.markerFill = AppTheme.surfaceWhite,
    this.truckSize = 30,
    this.labelStyle = const TextStyle(
      fontFamily: AppTheme.fontFamily,
      fontFamilyFallback: AppTheme.ethiopicFontFallback,
      fontSize: 12,
      fontWeight: FontWeight.w700,
    ),
  });

  /// The map in the signed-in role's colours: the person's own trail and
  /// the pickup pin (the Foundations board: "Farm in the trail colour,
  /// buyer in slate"). The buyer's trail is green, the farmer's plum.
  factory TrackMapStyle.forRole(RoleColors colors) =>
      TrackMapStyle(trail: colors.primary, pin: colors.primary);

  final Color background;

  /// Town dots.
  final Color town;
  final Color labelText;

  /// The outline that keeps a label readable over the lines: the ground.
  final Color labelOutline;

  /// The trail and its check-in rings while the trip runs.
  final Color trail;

  /// The trail after the delivery ended.
  final Color ended;

  /// The dashed road ahead, drawn at about half strength.
  final Color ahead;

  /// The halo around the last check-in: a faint fill inside [haloEdge].
  final Color halo;
  final Color haloEdge;

  /// The truck's rounded square and the truck drawn on it; [truckMuted]
  /// is the drawing while paused or with scans only.
  final Color truck;
  final Color truckGlyph;
  final Color truckMuted;

  /// The pickup ring (the farm).
  final Color pin;

  /// The drop-off disc (the buyer).
  final Color dropoff;

  /// Other stops on the truck: grey rings, never labelled.
  final Color otherStop;

  /// The inside of the rings and of the drop-off disc.
  final Color markerFill;

  /// The side of the truck's square.
  final double truckSize;

  /// Town labels; the colour comes from [labelText].
  final TextStyle labelStyle;

  @override
  bool operator ==(Object other) =>
      other is TrackMapStyle &&
      other.background == background &&
      other.town == town &&
      other.labelText == labelText &&
      other.labelOutline == labelOutline &&
      other.trail == trail &&
      other.ended == ended &&
      other.ahead == ahead &&
      other.halo == halo &&
      other.haloEdge == haloEdge &&
      other.truck == truck &&
      other.truckGlyph == truckGlyph &&
      other.truckMuted == truckMuted &&
      other.pin == pin &&
      other.dropoff == dropoff &&
      other.otherStop == otherStop &&
      other.markerFill == markerFill &&
      other.truckSize == truckSize &&
      other.labelStyle == labelStyle;

  @override
  int get hashCode => Object.hash(
        background,
        town,
        labelText,
        labelOutline,
        trail,
        ended,
        ahead,
        halo,
        haloEdge,
        truck,
        truckGlyph,
        truckMuted,
        pin,
        dropoff,
        otherStop,
        markerFill,
        truckSize,
        labelStyle,
      );
}

/// Draws a delivery's track from the bundled place list (no tiles, no map
/// package; spec section 8): the trail through the check-ins with a ring at
/// each, a dotted segment where two check-ins are more than 90 minutes
/// apart, the road ahead dashed from the last check-in to the drop-off (an
/// estimate), the pickup ring in the trail colour, the drop-off disc in
/// slate, other stops as grey rings, the truck (a black rounded square with
/// an orange truck) at its last check-in inside a dashed orange halo that
/// grows with the check-in's age (none while paused), and up to 10 town
/// labels.
///
/// The look follows the freshness band at [now] ([trackFreshness]). An
/// `ended` track carries no coordinates, so only the background is drawn
/// unless the caller passes a track with points (for example the last live
/// snapshot), which is then drawn muted, without the truck or the road
/// ahead.
class TrackMapPainter extends CustomPainter {
  TrackMapPainter({
    required this.track,
    required this.now,
    required this.places,
    required this.languageCode,
    this.style = const TrackMapStyle(),
    this.focus = TrackMapFocus.wholeRoute,
    this.textDirection = TextDirection.ltr,
    this.textScaler = TextScaler.noScaling,
    this.labelCache,
  });

  final DeliveryTrack track;
  final DateTime now;

  /// The towns to label (the bundled place list); empty draws no labels.
  final List<Place> places;

  /// `en`, `am` or `om`: the language of the town labels.
  final String languageCode;

  final TrackMapStyle style;

  /// What the map frames; the whole route by default.
  final TrackMapFocus focus;
  final TextDirection textDirection;
  final TextScaler textScaler;

  /// Keeps the labels between paints (see [TrackLabelCache]); null chooses
  /// them again on every paint.
  final TrackLabelCache? labelCache;

  /// The town labels of the last paint, for tests and semantics.
  List<TrackLabel> get labels => _labels;
  List<TrackLabel> _labels = const [];

  /// The fixes in capture order, the last check-in included.
  List<TrackFix> get _fixes {
    final fixes = [...track.trail];
    final last = track.lastFix;
    if (last != null &&
        (fixes.isEmpty || last.capturedAt.isAfter(fixes.last.capturedAt))) {
      fixes.add(last);
    }
    return fixes;
  }

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = style.background);

    final fixes = _fixes;
    final pickup = track.pickup;
    final dropoff = track.dropoff;
    final band = trackFreshness(track, now) ?? TrackFreshness.normal;
    final ended = band == TrackFreshness.ended;
    final last = fixes.isEmpty ? null : fixes.last;
    // The road ahead needs a truck and somewhere to go; else the whole
    // route is framed.
    final aheadOnly = focus == TrackMapFocus.roadAhead &&
        !ended &&
        last != null &&
        dropoff != null;
    final projection = TrackProjection.fit(
      aheadOnly
          ? [
              last.point,
              dropoff,
              for (final stop in track.otherStops)
                if (!stop.done) stop.point,
            ]
          : [
              for (final fix in fixes) fix.point,
              if (pickup != null) pickup,
              if (dropoff != null) dropoff,
              for (final stop in track.otherStops) stop.point,
            ],
      size,
      // The road ahead leaves the truck and the drop-off clear of the
      // controls floating over the map's edges.
      padding: aheadOnly ? 48 : 28,
    );
    if (projection == null) {
      _labels = const [];
      return;
    }
    final truck = last == null || ended ? null : projection.project(last.point);
    final half = style.truckSize / 2;

    // The halo first, so everything else reads over it.
    if (truck != null) {
      final radiusKm =
          trackHaloRadiusKm(band, trackFixAge(track, now) ?? Duration.zero);
      if (radiusKm != null) {
        // Never smaller than the truck's square, or it would hide it.
        final radius = math.max(radiusKm * projection.pixelsPerKm, half + 8);
        canvas.drawCircle(
          truck,
          radius,
          Paint()..color = style.halo.withValues(alpha: 0.16),
        );
        _dashedCircle(
          canvas,
          truck,
          radius,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 2
            ..color = style.haloEdge,
        );
      }
    }

    // Pins, check-ins, stops and the truck keep labels off them.
    final reserved = <Rect>[
      if (truck != null) Rect.fromCircle(center: truck, radius: half),
      if (pickup != null)
        Rect.fromCircle(center: projection.project(pickup), radius: 10),
      if (dropoff != null)
        Rect.fromCircle(center: projection.project(dropoff), radius: 10),
      for (final fix in fixes)
        Rect.fromCircle(center: projection.project(fix.point), radius: 5),
      for (final stop in track.otherStops)
        Rect.fromCircle(center: projection.project(stop.point), radius: 6),
    ];
    // Where the labels go depends on the track, the places, the language,
    // the size and the text settings, never on [now]: a repaint for the age
    // ticking on screen reuses them instead of searching the place list.
    final labelKey = _TrackLabelKey(
      track: track,
      places: places,
      languageCode: languageCode,
      size: size,
      focus: focus,
      labelStyle: style.labelStyle,
      textDirection: textDirection,
      textScaler: textScaler,
    );
    final cached = labelCache?._lookup(labelKey);
    final TrackLabel? noteLabel;
    if (cached != null) {
      _labels = cached.towns;
      noteLabel = cached.note;
    } else {
      final note = track.dropoffNote?.trim() ?? '';
      noteLabel = dropoff == null || note.isEmpty
          ? null
          : placeTrackLabels(
              candidates: [
                TrackLabelCandidate(
                  text: note,
                  anchor: projection.project(dropoff),
                ),
              ],
              canvas: size,
              measure: (text) => _measure(text, bold: true),
              reserved: reserved,
            ).firstOrNull;
      _labels = selectTrackLabels(
        places: places,
        projection: projection,
        fixes: [for (final fix in fixes) fix.point],
        languageCode: languageCode,
        measure: _measure,
        reserved: [...reserved, if (noteLabel != null) noteLabel.rect],
      );
      labelCache?._store(labelKey, _labels, noteLabel);
    }

    final townPaint = Paint()..color = style.town;
    for (final label in _labels) {
      canvas.drawCircle(label.anchor, 2, townPaint);
    }

    if (truck != null && dropoff != null) {
      _dashed(
        canvas,
        truck,
        projection.project(dropoff),
        Paint()
          ..color = style.ahead.withValues(alpha: 0.5)
          ..strokeWidth = 2.5,
      );
    }

    _drawTrail(canvas, projection, fixes, ended ? style.ended : style.trail);

    for (final stop in track.otherStops) {
      _ring(
        canvas,
        projection.project(stop.point),
        radius: 5,
        width: 3,
        color: stop.done
            ? style.otherStop.withValues(alpha: 0.55)
            : style.otherStop,
      );
    }
    if (pickup != null) {
      _ring(
        canvas,
        projection.project(pickup),
        radius: 8,
        width: 4,
        color: ended ? style.ended : style.pin,
      );
    }
    if (dropoff != null) {
      final at = projection.project(dropoff);
      canvas.drawCircle(at, 10, Paint()..color = style.dropoff);
      canvas.drawCircle(at, 3.5, Paint()..color = style.markerFill);
    }

    if (truck != null) _drawTruck(canvas, truck, band);

    for (final label in _labels) {
      _drawLabel(canvas, label);
    }
    if (noteLabel != null) _drawLabel(canvas, noteLabel, bold: true);
  }

  void _drawTrail(
    Canvas canvas,
    TrackProjection projection,
    List<TrackFix> fixes,
    Color color,
  ) {
    if (fixes.isEmpty) return;
    final line = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 4
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    final dots = Paint()..color = color;
    final points = [for (final fix in fixes) projection.project(fix.point)];

    var path = Path()..moveTo(points.first.dx, points.first.dy);
    var open = false;
    for (var i = 1; i < points.length; i++) {
      final gap = fixes[i].capturedAt.difference(fixes[i - 1].capturedAt);
      if (gap > trackGapOver) {
        if (open) canvas.drawPath(path, line);
        _dotted(canvas, points[i - 1], points[i], dots);
        path = Path()..moveTo(points[i].dx, points[i].dy);
        open = false;
      } else {
        path.lineTo(points[i].dx, points[i].dy);
        open = true;
      }
    }
    if (open) canvas.drawPath(path, line);

    // A white ring in the trail colour at every check-in (the board).
    for (final point in points) {
      _ring(canvas, point, radius: 4.5, width: 3, color: color);
    }
  }

  /// The truck at its last check-in: a black rounded square with the truck
  /// drawn on it in orange (grey while paused or with scans only, with the
  /// pause bars instead of the truck while paused).
  void _drawTruck(Canvas canvas, Offset at, TrackFreshness band) {
    final half = style.truckSize / 2;
    // The square's soft contact shadow. Also the one circle of this radius
    // on the map, which is how the paint tests find the truck.
    canvas.drawCircle(
      at.translate(0, 3),
      9,
      Paint()..color = style.truck.withValues(alpha: 0.18),
    );
    canvas.drawRRect(
      RRect.fromRectAndRadius(
        Rect.fromCircle(center: at, radius: half),
        Radius.circular(style.truckSize * 0.3),
      ),
      Paint()..color = style.truck,
    );
    final muted =
        band == TrackFreshness.paused || band == TrackFreshness.scansOnly;
    final glyph = muted ? style.truckMuted : style.truckGlyph;
    if (band == TrackFreshness.paused) {
      final bar = Paint()..color = glyph;
      final h = style.truckSize * 0.36;
      final w = style.truckSize * 0.1;
      for (final dx in [-w * 1.5, w * 0.5]) {
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(at.dx + dx, at.dy - h / 2, w, h),
            const Radius.circular(1),
          ),
          bar,
        );
      }
      return;
    }
    // The boards' truck icon on a 24 px grid, centred on the square.
    final scale = style.truckSize * 0.6 / 24;
    final stroke = Paint()
      ..color = glyph
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2.2 * scale
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    Offset p(double x, double y) =>
        at + Offset((x - 12) * scale, (y - 12.5) * scale);
    canvas.drawPath(
      Path()
        ..moveTo(p(3, 7).dx, p(3, 7).dy)
        ..lineTo(p(14, 7).dx, p(14, 7).dy)
        ..lineTo(p(14, 16).dx, p(14, 16).dy)
        ..lineTo(p(3, 16).dx, p(3, 16).dy)
        ..close()
        ..moveTo(p(14, 10).dx, p(14, 10).dy)
        ..lineTo(p(18, 10).dx, p(18, 10).dy)
        ..lineTo(p(21, 13).dx, p(21, 13).dy)
        ..lineTo(p(21, 16).dx, p(21, 16).dy)
        ..lineTo(p(14, 16).dx, p(14, 16).dy),
      stroke,
    );
    final wheel = Paint()..color = style.truck;
    for (final x in [7.0, 17.0]) {
      canvas.drawCircle(p(x, 17.5), 1.8 * scale, wheel);
      canvas.drawCircle(p(x, 17.5), 1.8 * scale, stroke);
    }
  }

  void _drawLabel(Canvas canvas, TrackLabel label, {bool bold = false}) {
    for (final outline in [true, false]) {
      final painter = _layout(label.text, bold: bold, outline: outline);
      painter.paint(canvas, label.rect.topLeft);
      painter.dispose();
    }
  }

  Size _measure(String text, {bool bold = false}) {
    final painter = _layout(text, bold: bold);
    final size = painter.size;
    painter.dispose();
    return size;
  }

  /// A label's text: the fill, or with [outline] the light edge drawn under
  /// it. The colour is set here, so a colour in [TrackMapStyle.labelStyle]
  /// is ignored.
  TextPainter _layout(
    String text, {
    bool bold = false,
    bool outline = false,
  }) {
    final base = style.labelStyle;
    final textStyle = TextStyle(
      fontFamily: base.fontFamily,
      fontFamilyFallback: base.fontFamilyFallback,
      fontSize: base.fontSize,
      fontWeight: bold ? FontWeight.w700 : base.fontWeight,
      letterSpacing: base.letterSpacing,
      color: outline ? null : (bold ? AppTheme.ink : style.labelText),
      foreground: outline
          ? (Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 3
            ..strokeJoin = StrokeJoin.round
            ..color = style.labelOutline)
          : null,
    );
    return TextPainter(
      text: TextSpan(text: text, style: textStyle),
      textDirection: textDirection,
      textScaler: textScaler,
      maxLines: 1,
    )..layout();
  }

  void _ring(
    Canvas canvas,
    Offset at, {
    required double radius,
    required double width,
    required Color color,
  }) {
    canvas.drawCircle(at, radius, Paint()..color = style.markerFill);
    canvas.drawCircle(
      at,
      radius,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = width
        ..color = color,
    );
  }

  /// The halo's edge: short dashes round the circle (the board's "3 4").
  static void _dashedCircle(
    Canvas canvas,
    Offset center,
    double radius,
    Paint paint,
  ) {
    const dash = 3.0;
    const space = 4.0;
    final count = math.max((2 * math.pi * radius / (dash + space)).floor(), 8);
    final sweep = 2 * math.pi / count;
    final rect = Rect.fromCircle(center: center, radius: radius);
    for (var i = 0; i < count; i++) {
      canvas.drawArc(
        rect,
        i * sweep,
        sweep * dash / (dash + space),
        false,
        paint,
      );
    }
  }

  static void _dashed(Canvas canvas, Offset from, Offset to, Paint paint) {
    const dash = 6.0;
    const space = 6.0;
    final length = (to - from).distance;
    if (length == 0) return;
    final step = (to - from) / length;
    for (var at = 0.0; at < length; at += dash + space) {
      final end = math.min(at + dash, length);
      canvas.drawLine(from + step * at, from + step * end, paint);
    }
  }

  static void _dotted(Canvas canvas, Offset from, Offset to, Paint paint) {
    const space = 9.0;
    final length = (to - from).distance;
    if (length == 0) return;
    final step = (to - from) / length;
    for (var at = space; at < length - space / 2; at += space) {
      canvas.drawCircle(from + step * at, 2, paint);
    }
  }

  @override
  bool shouldRepaint(TrackMapPainter oldDelegate) =>
      !identical(oldDelegate.track, track) ||
      oldDelegate.now != now ||
      !identical(oldDelegate.places, places) ||
      oldDelegate.languageCode != languageCode ||
      oldDelegate.style != style ||
      oldDelegate.focus != focus ||
      oldDelegate.textDirection != textDirection ||
      oldDelegate.textScaler != textScaler;
}

/// The town labels of a map's last paint, kept between paints (LT-11). A
/// track screen repaints every minute so the age of the check-in stays
/// current; choosing the labels again would search the whole place list
/// (about 3,860 towns) each time. The labels are chosen again only when the
/// track, the places, the language, the canvas size or the text settings
/// change. One cache per map: [TrackMap] keeps one for as long as it is
/// shown.
class TrackLabelCache {
  _TrackLabelKey? _key;
  List<TrackLabel> _towns = const [];
  TrackLabel? _note;

  /// How many times the labels were chosen, for tests.
  @visibleForTesting
  int get selections => _selections;
  int _selections = 0;

  ({List<TrackLabel> towns, TrackLabel? note})? _lookup(_TrackLabelKey key) =>
      key == _key ? (towns: _towns, note: _note) : null;

  void _store(_TrackLabelKey key, List<TrackLabel> towns, TrackLabel? note) {
    _key = key;
    _towns = towns;
    _note = note;
    _selections++;
  }
}

/// What the labels of a paint depend on. The track and the place list are
/// compared by identity: a new answer from the server is a new track.
@immutable
class _TrackLabelKey {
  const _TrackLabelKey({
    required this.track,
    required this.places,
    required this.languageCode,
    required this.size,
    required this.focus,
    required this.labelStyle,
    required this.textDirection,
    required this.textScaler,
  });

  final DeliveryTrack track;
  final List<Place> places;
  final String languageCode;
  final Size size;
  final TrackMapFocus focus;
  final TextStyle labelStyle;
  final TextDirection textDirection;
  final TextScaler textScaler;

  @override
  bool operator ==(Object other) =>
      other is _TrackLabelKey &&
      identical(other.track, track) &&
      identical(other.places, places) &&
      other.languageCode == languageCode &&
      other.size == size &&
      other.focus == focus &&
      other.labelStyle == labelStyle &&
      other.textDirection == textDirection &&
      other.textScaler == textScaler;

  @override
  int get hashCode => Object.hash(
        identityHashCode(track),
        identityHashCode(places),
        languageCode,
        size,
        focus,
        labelStyle,
        textDirection,
        textScaler,
      );
}
