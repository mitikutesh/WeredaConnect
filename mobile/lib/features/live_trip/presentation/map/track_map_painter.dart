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

/// Colours and sizes of the track map (the Foundations board of the design).
@immutable
class TrackMapStyle {
  const TrackMapStyle({
    this.background = const Color(0xFFEEF2EF),
    this.town = const Color(0xFF8A97A6),
    this.labelText = AppTheme.textMuted,
    this.labelOutline = AppTheme.surfaceWhite,
    this.trail = AppTheme.primary,
    this.ended = AppTheme.textMuted,
    this.late = AppTheme.statusWarning,
    this.muted = AppTheme.textMuted,
    this.pin = AppTheme.primaryDeep,
    this.otherStop = const Color(0xFF94A3B8),
    this.labelStyle = const TextStyle(
      fontFamily: AppTheme.fontFamily,
      fontFamilyFallback: AppTheme.ethiopicFontFallback,
      fontSize: 11,
      fontWeight: FontWeight.w600,
    ),
  });

  final Color background;

  /// Town dots.
  final Color town;
  final Color labelText;

  /// The outline that keeps a label readable over the lines.
  final Color labelOutline;

  /// The trail, the road ahead and the truck while the position is current.
  final Color trail;

  /// The trail after the delivery ended.
  final Color ended;

  /// The truck and its halo when the position is late.
  final Color late;

  /// The truck and its halo while paused or with scans only.
  final Color muted;

  /// The pickup ring and the drop-off dot.
  final Color pin;

  /// Other stops on the truck: grey rings, never labelled.
  final Color otherStop;

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
      other.late == late &&
      other.muted == muted &&
      other.pin == pin &&
      other.otherStop == otherStop &&
      other.labelStyle == labelStyle;

  @override
  int get hashCode => Object.hash(
        background,
        town,
        labelText,
        labelOutline,
        trail,
        ended,
        late,
        muted,
        pin,
        otherStop,
        labelStyle,
      );
}

/// Draws a delivery's track from the bundled place list (no tiles, no map
/// package; spec section 8): the trail through the check-ins with a dot at
/// each, a dotted segment where two check-ins are more than 90 minutes
/// apart, the road ahead dashed from the last check-in to the drop-off (an
/// estimate), the pickup ring, the drop-off dot, other stops as grey rings,
/// the truck at its last check-in inside a halo that grows with the
/// check-in's age (none while paused), and up to 10 town labels.
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
    final projection = TrackProjection.fit(
      [
        for (final fix in fixes) fix.point,
        if (pickup != null) pickup,
        if (dropoff != null) dropoff,
        for (final stop in track.otherStops) stop.point,
      ],
      size,
    );
    if (projection == null) {
      _labels = const [];
      return;
    }

    final band = trackFreshness(track, now) ?? TrackFreshness.normal;
    final ended = band == TrackFreshness.ended;
    final last = fixes.isEmpty ? null : fixes.last;
    final truck = last == null || ended ? null : projection.project(last.point);
    final accent = _accentOf(band);

    // The halo first, so everything else reads over it.
    if (truck != null) {
      final radiusKm =
          trackHaloRadiusKm(band, trackFixAge(track, now) ?? Duration.zero);
      if (radiusKm != null) {
        final radius = radiusKm * projection.pixelsPerKm;
        canvas.drawCircle(
          truck,
          radius,
          Paint()..color = accent.withValues(alpha: 0.14),
        );
        canvas.drawCircle(
          truck,
          radius,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 1
            ..color = accent.withValues(alpha: 0.45),
        );
      }
    }

    // Pins, check-ins, stops and the truck keep labels off them.
    final reserved = <Rect>[
      if (truck != null) Rect.fromCircle(center: truck, radius: 9),
      if (pickup != null)
        Rect.fromCircle(center: projection.project(pickup), radius: 7),
      if (dropoff != null)
        Rect.fromCircle(center: projection.project(dropoff), radius: 7),
      for (final fix in fixes)
        Rect.fromCircle(center: projection.project(fix.point), radius: 4),
      for (final stop in track.otherStops)
        Rect.fromCircle(center: projection.project(stop.point), radius: 5.5),
    ];
    // Where the labels go depends on the track, the places, the language,
    // the size and the text settings, never on [now]: a repaint for the age
    // ticking on screen reuses them instead of searching the place list.
    final labelKey = _TrackLabelKey(
      track: track,
      places: places,
      languageCode: languageCode,
      size: size,
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
          ..color = accent
          ..strokeWidth = 2
          ..strokeCap = StrokeCap.round,
      );
    }

    _drawTrail(canvas, projection, fixes, ended ? style.ended : style.trail);

    for (final stop in track.otherStops) {
      _ring(
        canvas,
        projection.project(stop.point),
        radius: 4.5,
        width: 1.5,
        color: stop.done
            ? style.otherStop.withValues(alpha: 0.5)
            : style.otherStop,
      );
    }
    if (pickup != null) {
      _ring(
        canvas,
        projection.project(pickup),
        radius: 6,
        width: 2.5,
        color: style.pin,
      );
    }
    if (dropoff != null) {
      final at = projection.project(dropoff);
      canvas.drawCircle(at, 7, Paint()..color = style.labelOutline);
      canvas.drawCircle(at, 5.5, Paint()..color = style.pin);
    }

    if (truck != null) _drawTruck(canvas, truck, accent, band);

    for (final label in _labels) {
      _drawLabel(canvas, label);
    }
    if (noteLabel != null) _drawLabel(canvas, noteLabel, bold: true);
  }

  Color _accentOf(TrackFreshness band) => switch (band) {
        TrackFreshness.fresh || TrackFreshness.normal => style.trail,
        TrackFreshness.late => style.late,
        TrackFreshness.paused || TrackFreshness.scansOnly => style.muted,
        TrackFreshness.ended => style.ended,
      };

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
      ..strokeWidth = 3
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

    final outline = Paint()..color = style.labelOutline;
    for (final point in points) {
      canvas.drawCircle(point, 3.5, outline);
      canvas.drawCircle(point, 2.5, dots);
    }
  }

  void _drawTruck(
    Canvas canvas,
    Offset at,
    Color color,
    TrackFreshness band,
  ) {
    canvas.drawCircle(at, 9, Paint()..color = style.labelOutline);
    canvas.drawCircle(at, 7, Paint()..color = color);
    if (band == TrackFreshness.paused) {
      final bar = Paint()..color = style.labelOutline;
      canvas.drawRect(Rect.fromLTWH(at.dx - 3, at.dy - 3.5, 2, 7), bar);
      canvas.drawRect(Rect.fromLTWH(at.dx + 1, at.dy - 3.5, 2, 7), bar);
    } else {
      canvas.drawCircle(at, 2.5, Paint()..color = style.labelOutline);
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
      color: outline ? null : (bold ? style.pin : style.labelText),
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

  static void _ring(
    Canvas canvas,
    Offset at, {
    required double radius,
    required double width,
    required Color color,
  }) {
    canvas.drawCircle(at, radius, Paint()..color = const Color(0xFFFFFFFF));
    canvas.drawCircle(
      at,
      radius,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = width
        ..color = color,
    );
  }

  static void _dashed(Canvas canvas, Offset from, Offset to, Paint paint) {
    const dash = 6.0;
    const space = 4.0;
    final length = (to - from).distance;
    if (length == 0) return;
    final step = (to - from) / length;
    for (var at = 0.0; at < length; at += dash + space) {
      final end = math.min(at + dash, length);
      canvas.drawLine(from + step * at, from + step * end, paint);
    }
  }

  static void _dotted(Canvas canvas, Offset from, Offset to, Paint paint) {
    const space = 6.0;
    final length = (to - from).distance;
    if (length == 0) return;
    final step = (to - from) / length;
    for (var at = space; at < length - space / 2; at += space) {
      canvas.drawCircle(from + step * at, 1.3, paint);
    }
  }

  @override
  bool shouldRepaint(TrackMapPainter oldDelegate) =>
      !identical(oldDelegate.track, track) ||
      oldDelegate.now != now ||
      !identical(oldDelegate.places, places) ||
      oldDelegate.languageCode != languageCode ||
      oldDelegate.style != style ||
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
    required this.labelStyle,
    required this.textDirection,
    required this.textScaler,
  });

  final DeliveryTrack track;
  final List<Place> places;
  final String languageCode;
  final Size size;
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
      other.labelStyle == labelStyle &&
      other.textDirection == textDirection &&
      other.textScaler == textScaler;

  @override
  int get hashCode => Object.hash(
        identityHashCode(track),
        identityHashCode(places),
        languageCode,
        size,
        labelStyle,
        textDirection,
        textScaler,
      );
}
