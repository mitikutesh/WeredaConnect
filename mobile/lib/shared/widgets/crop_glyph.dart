import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The three small line glyphs the boards draw for the crops
/// (CreateCropListingM, CreateIntentM, MarketM): an ear of grain for the
/// cereals, a pod with two seeds for the pulses and sesame, a bean for
/// coffee.
enum CropGlyphShape { ear, pod, bean }

/// The glyph the boards draw for [cropKey] (the stored key, "wheat"), or
/// null for a crop no board covers: that one keeps the sprout.
CropGlyphShape? cropGlyphShape(String? cropKey) {
  switch (cropKey?.trim().toLowerCase()) {
    case 'teff' || 'wheat' || 'barley' || 'maize' || 'sorghum':
      return CropGlyphShape.ear;
    case 'chickpea' || 'lentils' || 'sesame':
      return CropGlyphShape.pod;
    case 'coffee':
      return CropGlyphShape.bean;
    default:
      return null;
  }
}

/// A crop's glyph in a pill or a tile, drawn stroke for stroke from the
/// boards' 24 px drawings. Decoration beside the crop's name: it carries
/// no semantics of its own.
class CropGlyph extends StatelessWidget {
  /// The stored crop key ("wheat"); any case.
  final String? cropKey;
  final double size;

  /// The role's colour when null.
  final Color? color;

  const CropGlyph({
    required this.cropKey,
    this.size = 20,
    this.color,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final shape = cropGlyphShape(cropKey);
    final ink = color ?? context.roleColors.primary;
    if (shape == null) return FwSprout(size: size, color: ink);
    // Centred at its own size inside a tile (a tight box), and only its
    // size anywhere else, as [FwSprout].
    return Center(
      widthFactor: 1,
      heightFactor: 1,
      child: ExcludeSemantics(
        child: CustomPaint(
          size: Size.square(size),
          painter: _CropGlyphPainter(shape, ink),
        ),
      ),
    );
  }
}

/// One stroke of a glyph: a path in the board's own SVG words, a circle,
/// or an ellipse turned about its centre.
class _Stroke {
  final String? path;
  final Offset center;
  final double radiusX;
  final double radiusY;
  final double degrees;

  const _Stroke.path(String this.path)
      : center = Offset.zero,
        radiusX = 0,
        radiusY = 0,
        degrees = 0;

  const _Stroke.circle(this.center, double radius)
      : path = null,
        radiusX = radius,
        radiusY = radius,
        degrees = 0;

  const _Stroke.ellipse(this.center, this.radiusX, this.radiusY, this.degrees)
      : path = null;
}

/// The boards' drawings, each in a 24 by 24 box.
const Map<CropGlyphShape, List<_Stroke>> _glyphs = {
  // An ear of grain: the stalk and two pairs of kernels.
  CropGlyphShape.ear: [
    _Stroke.path('M12 21V3'),
    _Stroke.path('M12 9c-2.6 0-4-1.4-4-4 2.6 0 4 1.4 4 4z'),
    _Stroke.path('M12 9c2.6 0 4-1.4 4-4-2.6 0-4 1.4-4 4z'),
    _Stroke.path('M12 15c-2.6 0-4-1.4-4-4 2.6 0 4 1.4 4 4z'),
    _Stroke.path('M12 15c2.6 0 4-1.4 4-4-2.6 0-4 1.4-4 4z'),
  ],
  // A pod with two seeds.
  CropGlyphShape.pod: [
    _Stroke.path('M4 20C4 11 11 4 20 4c0 9-7 16-16 16z'),
    _Stroke.circle(Offset(9.5, 14.5), 1.4),
    _Stroke.circle(Offset(14.5, 9.5), 1.4),
  ],
  // A coffee bean: the leaning oval and its crease.
  CropGlyphShape.bean: [
    _Stroke.ellipse(Offset(12, 12), 6.5, 9, 30),
    _Stroke.path('M15.8 4.8c-3.4 2.4-1 5.2-3.6 7.2s-0.6 4.8-4 7.2'),
  ],
};

/// The glyphs' line weight in their 24 px box, as on the boards.
const double _strokeWidth = 1.8;

/// The outline of [shape] in its 24 px box, for tests of the drawing.
@visibleForTesting
Rect cropGlyphBounds(CropGlyphShape shape) {
  Rect? bounds;
  for (final stroke in _glyphs[shape]!) {
    final box = _strokePath(stroke).getBounds();
    bounds = bounds == null ? box : bounds.expandToInclude(box);
  }
  return bounds!;
}

Path _strokePath(_Stroke stroke) {
  final d = stroke.path;
  if (d != null) return sketchPath(d);
  final oval = Path()
    ..addOval(
      Rect.fromCenter(
        center: Offset.zero,
        width: stroke.radiusX * 2,
        height: stroke.radiusY * 2,
      ),
    );
  final turn = Matrix4.translationValues(stroke.center.dx, stroke.center.dy, 0)
    ..rotateZ(stroke.degrees * math.pi / 180);
  return oval.transform(turn.storage);
}

class _CropGlyphPainter extends CustomPainter {
  final CropGlyphShape shape;
  final Color color;

  const _CropGlyphPainter(this.shape, this.color);

  @override
  void paint(Canvas canvas, Size size) {
    canvas.scale(size.width / 24, size.height / 24);
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = _strokeWidth
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    for (final stroke in _glyphs[shape]!) {
      canvas.drawPath(_strokePath(stroke), paint);
    }
  }

  @override
  bool shouldRepaint(_CropGlyphPainter old) =>
      old.shape != shape || old.color != color;
}
