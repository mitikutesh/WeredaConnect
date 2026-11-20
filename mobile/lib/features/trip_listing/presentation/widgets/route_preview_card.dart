import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

// Trip screens must not import the live trip feature (live-trip-api.md
// section 5, invariant 1), so this file keeps its own small projection.

/// Kilometres per degree of latitude (and of longitude at the equator).
const double _kmPerDegree = 111.32;

/// Straight-line distance between two places in whole kilometres, or null
/// when either has no coordinates.
int? routeDistanceKm(Place? from, Place? to) {
  if (from?.lat == null || from?.lng == null) return null;
  if (to?.lat == null || to?.lng == null) return null;
  final meters = GpsDistanceService.haversineDistance(
    from!.lat!,
    from.lng!,
    to!.lat!,
    to.lng!,
  );
  return (meters / 1000).round();
}

/// Up to [limit] towns (places with a known population) from [places] near
/// the straight line from [from] to
/// [to] (within [corridorKm] of it, between the two ends), largest first,
/// returned in order along the route. Roads bend, so the corridor is wide
/// enough for towns like Dukem, about 15 km off the Meki to Addis line.
List<Place> townsOnTheWay(
  Place from,
  Place to,
  Iterable<Place> places, {
  double corridorKm = 20,
  int limit = 4,
}) {
  final cos = math.cos((from.lat! + to.lat!) / 2 * math.pi / 180);
  // Kilometre plane around the route: x east, y north.
  double x(Place p) => p.lng! * cos * _kmPerDegree;
  double y(Place p) => p.lat! * _kmPerDegree;
  final dx = x(to) - x(from);
  final dy = y(to) - y(from);
  final lengthSquared = dx * dx + dy * dy;
  if (lengthSquared == 0) return const [];

  final candidates = <({Place place, double t})>[];
  for (final place in places) {
    if (place.lat == null || place.lng == null) continue;
    if (place.id == from.id || place.id == to.id) continue;
    // Only a town with a known population is named on the way. The list
    // also holds small localities without one, some named like a well-known
    // town far away: the "Gimbi" near Debre Birhan was read as Gimbi in
    // West Wellega (BUG-28).
    if (place.population <= 0) continue;
    final t =
        ((x(place) - x(from)) * dx + (y(place) - y(from)) * dy) / lengthSquared;
    if (t <= 0.08 || t >= 0.92) continue;
    final px = x(from) + t * dx - x(place);
    final py = y(from) + t * dy - y(place);
    if (px * px + py * py > corridorKm * corridorKm) continue;
    candidates.add((place: place, t: t));
  }
  candidates.sort((l, r) => r.place.population.compareTo(l.place.population));
  final picked = candidates.take(limit).toList()
    ..sort((l, r) => l.t.compareTo(r.t));
  return [for (final c in picked) c.place];
}

/// The Create trip board's route preview: the two ends, the towns on the way
/// and the distance, drawn on the phone from the bundled place list (no map
/// tiles, no download).
class RoutePreviewCard extends StatelessWidget {
  const RoutePreviewCard({
    required this.from,
    required this.to,
    required this.places,
    this.fromLabel,
    this.toLabel,
    this.footer,
    this.height = 120,
    super.key,
  });

  final Place from;
  final Place to;

  /// The place list to pick towns on the way from.
  final List<Place> places;

  /// Labels drawn at the two ends instead of the place names (the Crop
  /// detail board's "Meki · crop here" and "Merkato · you").
  final String? fromLabel;
  final String? toLabel;

  /// The line under the drawing; the distance with the towns note when null.
  final String? footer;

  final double height;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageCode = Localizations.localeOf(context).languageCode;
    final km = routeDistanceKm(from, to);
    if (km == null) return const SizedBox.shrink();
    final towns = townsOnTheWay(from, to, places);
    final fromName = from.displayName(languageCode);
    final toName = to.displayName(languageCode);

    return Container(
      clipBehavior: Clip.antiAlias,
      decoration: _previewCardDecoration,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            image: true,
            label: l10n.createTripRoutePreviewLabel(fromName, toName),
            child: ExcludeSemantics(
              child: SizedBox(
                height: height,
                child: CustomPaint(
                  painter: _RoutePreviewPainter(
                    from: from,
                    to: to,
                    towns: towns,
                    languageCode: languageCode,
                    textDirection: Directionality.of(context),
                    fromLabel: fromLabel ?? fromName,
                    toLabel: toLabel ?? toName,
                    accent: context.roleColors.primary,
                  ),
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 10, 16, 12),
            child: Text(
              footer ?? l10n.createTripRouteDistance(km),
              style: const TextStyle(fontSize: 13, color: AppTheme.slate),
            ),
          ),
        ],
      ),
    );
  }
}

class _RoutePreviewPainter extends CustomPainter {
  _RoutePreviewPainter({
    required this.from,
    required this.to,
    required this.towns,
    required this.languageCode,
    required this.textDirection,
    required this.fromLabel,
    required this.toLabel,
    required this.accent,
  });

  final Place from;
  final Place to;
  final List<Place> towns;
  final String languageCode;
  final TextDirection textDirection;
  final String fromLabel;
  final String toLabel;

  /// The role's colour: the start of the person's own route.
  final Color accent;

  /// The destination: the black dot, as in the To field.
  static const _destination = AppTheme.ink;

  /// An equirectangular projection fitting [from] and [to] into [size]
  /// with [padding] pixels free; at least 20 km shows in each direction.
  Offset Function(Place) _fit(Size size, {double padding = 30}) {
    final centerLat = (from.lat! + to.lat!) / 2;
    final centerLng = (from.lng! + to.lng!) / 2;
    final cos = math.cos(centerLat * math.pi / 180);
    const minSpan = 20 / _kmPerDegree;
    final spanX = math.max((from.lng! - to.lng!).abs() * cos, minSpan);
    final spanY = math.max((from.lat! - to.lat!).abs(), minSpan);
    final scale = math.min(
      math.max(size.width - 2 * padding, 1) / spanX,
      math.max(size.height - 2 * padding, 1) / spanY,
    );
    return (p) => Offset(
          size.width / 2 + (p.lng! - centerLng) * cos * scale,
          size.height / 2 - (p.lat! - centerLat) * scale,
        );
  }

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = _ground);
    final project = _fit(size);
    final a = project(from);
    final b = project(to);

    // Dashed straight line: the road is unknown, the direction is not.
    final line = Paint()
      ..color = AppTheme.ink.withValues(alpha: 0.55)
      ..strokeWidth = 2.5;
    final total = (b - a).distance;
    if (total > 0) {
      final step = (b - a) / total;
      for (var d = 0.0; d < total; d += 12) {
        canvas.drawLine(a + step * d, a + step * math.min(d + 6, total), line);
      }
    }

    for (final town in towns) {
      _paintTownDot(canvas, project(town));
    }

    canvas
      ..drawCircle(a, 8, Paint()..color = Colors.white)
      ..drawCircle(
        a,
        8,
        Paint()
          ..color = accent
          ..style = PaintingStyle.stroke
          ..strokeWidth = 3.5,
      )
      ..drawCircle(b, 8, Paint()..color = _destination);
    // The ends' labels first; a town's name is skipped when it would sit
    // on a label already drawn (towns bunch up near a city).
    final placed = <Rect>[];
    final fromRect = _paintLabel(
      canvas,
      size,
      fromLabel,
      a + const Offset(12, 4),
      textDirection: textDirection,
    );
    if (fromRect != null) placed.add(fromRect);
    final toRect = _paintLabel(
      canvas,
      size,
      toLabel,
      b + const Offset(-12, -22),
      textDirection: textDirection,
      alignEnd: true,
    );
    if (toRect != null) placed.add(toRect);
    for (final town in towns) {
      final at = project(town) + const Offset(6, 2);
      final rect = _paintLabel(
        canvas,
        size,
        town.displayName(languageCode),
        at,
        textDirection: textDirection,
        avoid: placed,
      );
      if (rect != null) placed.add(rect);
    }
  }

  @override
  bool shouldRepaint(_RoutePreviewPainter old) =>
      old.from.id != from.id ||
      old.to.id != to.id ||
      old.accent != accent ||
      old.languageCode != languageCode ||
      old.towns.length != towns.length ||
      old.fromLabel != fromLabel ||
      old.toLabel != toLabel;
}

/// The drawn map's ground and the pale roads over it, from the Create trip
/// board (CreateTripM: the route preview). Page M has no token for either:
/// needs a token.
const Color _ground = Color(0xFFDCD5C9);
const Color _road = Color(0xFFEFEBE4);

/// Both preview cards: the raised paper card of page M (radius 28, the soft
/// shadow, the quiet edge).
final BoxDecoration _previewCardDecoration = BoxDecoration(
  color: AppTheme.paper,
  borderRadius: BorderRadius.circular(28),
  border: Border.all(color: AppTheme.line),
  boxShadow: [
    BoxShadow(
      color: AppTheme.ink.withValues(alpha: 0.14),
      blurRadius: 30,
      offset: const Offset(0, 14),
    ),
  ],
);

/// A town on the way: a white dot with a slate edge (the board).
void _paintTownDot(Canvas canvas, Offset at) {
  canvas
    ..drawCircle(at, 3.5, Paint()..color = Colors.white)
    ..drawCircle(
      at,
      3.5,
      Paint()
        ..color = AppTheme.slate
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2,
    );
}

/// A small haloed label on the drawing, kept on the canvas when its place
/// sits near an edge. Returns where it was drawn, or null when it would
/// overlap one of [avoid] and was left out.
Rect? _paintLabel(
  Canvas canvas,
  Size size,
  String text,
  Offset at, {
  required TextDirection textDirection,
  bool alignEnd = false,
  Color color = AppTheme.ink,
  List<Rect> avoid = const [],
}) {
  TextPainter build(Paint? outline) => TextPainter(
        text: TextSpan(
          text: text,
          style: TextStyle(
            fontFamily: AppTheme.fontFamily,
            fontSize: 12,
            fontWeight: FontWeight.w700,
            color: outline == null ? color : null,
            foreground: outline,
            fontFamilyFallback: AppTheme.ethiopicFontFallback,
          ),
        ),
        textDirection: textDirection,
        maxLines: 1,
      )..layout();
  final halo = build(
    Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 3
      ..color = _ground,
  );
  final fill = build(null);
  final wanted = alignEnd ? at - Offset(fill.width, 0) : at;
  final origin = Offset(
    wanted.dx.clamp(4, math.max(4, size.width - fill.width - 4)),
    wanted.dy.clamp(2, math.max(2, size.height - fill.height - 2)),
  );
  final rect = origin & Size(fill.width, fill.height);
  if (avoid.any((r) => r.inflate(2).overlaps(rect))) return null;
  halo.paint(canvas, origin);
  fill.paint(canvas, origin);
  return rect;
}

/// Up to [limit] places within [radiusKm] of a point, largest first, the
/// point's own place left out.
List<Place> townsAround(
  double lat,
  double lng,
  Iterable<Place> places, {
  double radiusKm = 30,
  int limit = 4,
  String? excludeId,
}) {
  final cos = math.cos(lat * math.pi / 180);
  final found = <({Place place, double km})>[];
  for (final place in places) {
    if (place.lat == null || place.lng == null) continue;
    if (place.id == excludeId) continue;
    final dx = (place.lng! - lng) * cos * _kmPerDegree;
    final dy = (place.lat! - lat) * _kmPerDegree;
    final km = math.sqrt(dx * dx + dy * dy);
    if (km < 0.5 || km > radiusKm) continue;
    found.add((place: place, km: km));
  }
  found.sort((l, r) => r.place.population.compareTo(l.place.population));
  return [for (final f in found.take(limit)) f.place];
}

/// The Create listing board's location card: the farm (the GPS fix, else
/// the picked place's centre) with the towns around it, drawn on the phone
/// from the bundled place list; [footer] holds the GPS chip.
class PlacePreviewCard extends StatelessWidget {
  const PlacePreviewCard({
    required this.lat,
    required this.lng,
    required this.label,
    required this.places,
    this.excludeId,
    this.footer,
    this.height = 128,
    super.key,
  });

  final double lat;
  final double lng;

  /// The label beside the point ("Your farm").
  final String label;
  final List<Place> places;

  /// The picked place's id, not drawn again as a town around the point.
  final String? excludeId;
  final Widget? footer;
  final double height;

  @override
  Widget build(BuildContext context) {
    final languageCode = Localizations.localeOf(context).languageCode;
    final towns = townsAround(lat, lng, places, excludeId: excludeId);
    return Container(
      clipBehavior: Clip.antiAlias,
      decoration: _previewCardDecoration,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            image: true,
            label: label,
            child: ExcludeSemantics(
              child: SizedBox(
                height: height,
                child: CustomPaint(
                  painter: _PlacePreviewPainter(
                    lat: lat,
                    lng: lng,
                    label: label,
                    towns: towns,
                    languageCode: languageCode,
                    textDirection: Directionality.of(context),
                    accent: context.roleColors.primary,
                  ),
                ),
              ),
            ),
          ),
          if (footer != null)
            Padding(
              padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
              child: footer,
            ),
        ],
      ),
    );
  }
}

class _PlacePreviewPainter extends CustomPainter {
  _PlacePreviewPainter({
    required this.lat,
    required this.lng,
    required this.label,
    required this.towns,
    required this.languageCode,
    required this.textDirection,
    required this.accent,
  });

  final double lat;
  final double lng;
  final String label;
  final List<Place> towns;
  final String languageCode;
  final TextDirection textDirection;
  final Color accent;

  /// Kilometres shown across the card's width.
  static const double _spanKm = 60;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = _ground);
    final cos = math.cos(lat * math.pi / 180);
    final scale = size.width / _spanKm;
    Offset project(double pLat, double pLng) => Offset(
          size.width / 2 + (pLng - lng) * cos * _kmPerDegree * scale,
          size.height / 2 - (pLat - lat) * _kmPerDegree * scale,
        );

    // Two pale shapes, as the roads on the board: a hint of land, no map.
    canvas
      ..drawOval(
        Rect.fromCenter(
          center: Offset(size.width * 0.26, size.height * 0.76),
          width: size.width * 0.46,
          height: size.height * 0.54,
        ),
        Paint()..color = _road,
      )
      ..drawOval(
        Rect.fromCenter(
          center: Offset(size.width * 0.72, size.height * 0.3),
          width: size.width * 0.24,
          height: size.height * 0.26,
        ),
        Paint()..color = _road,
      );

    for (final town in towns) {
      final p = project(town.lat!, town.lng!);
      _paintTownDot(canvas, p);
      _paintLabel(
        canvas,
        size,
        town.displayName(languageCode),
        p + const Offset(8, -6),
        textDirection: textDirection,
      );
    }

    final here = project(lat, lng);
    canvas
      ..drawCircle(
        here,
        18,
        Paint()..color = accent.withValues(alpha: 0.14),
      )
      ..drawCircle(here, 8, Paint()..color = Colors.white)
      ..drawCircle(
        here,
        8,
        Paint()
          ..color = accent
          ..style = PaintingStyle.stroke
          ..strokeWidth = 4,
      );
    _paintLabel(
      canvas,
      size,
      label,
      here + const Offset(16, -10),
      textDirection: textDirection,
    );
  }

  @override
  bool shouldRepaint(_PlacePreviewPainter old) =>
      old.lat != lat ||
      old.accent != accent ||
      old.lng != lng ||
      old.label != label ||
      old.languageCode != languageCode ||
      old.towns.length != towns.length;
}
