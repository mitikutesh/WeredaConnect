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

/// Up to [limit] towns from [places] near the straight line from [from] to
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
    super.key,
  });

  final Place from;
  final Place to;

  /// The place list to pick towns on the way from.
  final List<Place> places;

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
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: AppTheme.whisperBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            image: true,
            label: l10n.createTripRoutePreviewLabel(fromName, toName),
            child: ExcludeSemantics(
              child: SizedBox(
                height: 120,
                child: CustomPaint(
                  painter: _RoutePreviewPainter(
                    from: from,
                    to: to,
                    towns: towns,
                    languageCode: languageCode,
                    textDirection: Directionality.of(context),
                  ),
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            child: Text(
              l10n.createTripRouteDistance(km),
              style: const TextStyle(fontSize: 12, color: AppTheme.mutedSteel),
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
  });

  final Place from;
  final Place to;
  final List<Place> towns;
  final String languageCode;
  final TextDirection textDirection;

  static const _ground = Color(0xFFE9E7DE);
  static const _townDot = Color(0xFFA7ADA9);
  static const _destination = Color(0xFF2E4C7D);

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
      ..color = AppTheme.soilDark.withValues(alpha: 0.45)
      ..strokeWidth = 2.5;
    final total = (b - a).distance;
    if (total > 0) {
      final step = (b - a) / total;
      for (var d = 0.0; d < total; d += 12) {
        canvas.drawLine(a + step * d, a + step * math.min(d + 6, total), line);
      }
    }

    for (final town in towns) {
      final p = project(town);
      canvas.drawCircle(p, 3.5, Paint()..color = _townDot);
      _label(
        canvas,
        size,
        town.displayName(languageCode),
        p + const Offset(6, 2),
      );
    }

    canvas
      ..drawCircle(a, 8, Paint()..color = Colors.white)
      ..drawCircle(
        a,
        8,
        Paint()
          ..color = AppTheme.fieldGreen
          ..style = PaintingStyle.stroke
          ..strokeWidth = 3.5,
      )
      ..drawCircle(b, 8, Paint()..color = _destination);
    _label(
      canvas,
      size,
      from.displayName(languageCode),
      a + const Offset(12, 4),
    );
    _label(
      canvas,
      size,
      to.displayName(languageCode),
      b + const Offset(-12, -22),
      alignEnd: true,
    );
  }

  void _label(
    Canvas canvas,
    Size size,
    String text,
    Offset at, {
    bool alignEnd = false,
  }) {
    TextPainter build(Paint? outline) => TextPainter(
          text: TextSpan(
            text: text,
            style: TextStyle(
              fontSize: 11,
              fontWeight: FontWeight.w600,
              color: outline == null ? AppTheme.unselectedNav : null,
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
    // Keep the label on the canvas when its place sits near an edge.
    final origin = Offset(
      wanted.dx.clamp(4, math.max(4, size.width - fill.width - 4)),
      wanted.dy.clamp(2, math.max(2, size.height - fill.height - 2)),
    );
    halo.paint(canvas, origin);
    fill.paint(canvas, origin);
  }

  @override
  bool shouldRepaint(_RoutePreviewPainter old) =>
      old.from.id != from.id ||
      old.to.id != to.id ||
      old.languageCode != languageCode ||
      old.towns.length != towns.length;
}
