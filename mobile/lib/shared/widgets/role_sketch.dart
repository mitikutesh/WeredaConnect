import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The line drawing that tells the three apps apart (design canvas page M):
/// a coffee branch for the farmer, a loaded truck for the trucker, a
/// balance scale for the buyer.
///
/// The sketch rules of the canvas (specs/design-canvas/canvas.json):
/// 1. Decoration only. It never carries information and is hidden from
///    screen readers.
/// 2. One sketch per screen: the header sketch, kept in the right 35% of
///    the header. Hide it when the title is long ([FwPageHeader] does).
/// 3. The larger drawing ([RoleSketch.emptyState]) is for empty states
///    only. Never behind cards or near the bottom bar.
/// 4. No text on top of a sketch.
/// 5. Hidden in large-text and reduced-effects modes ([sketchesShown]).
class RoleSketch extends StatelessWidget {
  final UserRole role;

  /// The drawn width; the height follows the drawing's proportions.
  final double width;
  final Color color;

  /// The main line's thickness on screen, whatever the size.
  final double strokeWidth;

  const RoleSketch({
    required this.role,
    this.width = 84,
    this.color = AppTheme.sketch,
    this.strokeWidth = 1.55,
    super.key,
  });

  /// The larger drawing above an empty state's title ("No crops yet").
  const RoleSketch.emptyState({
    required this.role,
    this.width = 200,
    this.color = AppTheme.sketch,
    this.strokeWidth = 2.1,
    super.key,
  });

  /// Rule 5: no sketch with large text (it would crowd the title) or when
  /// the person asked for fewer effects (reduced motion, high contrast,
  /// bold text).
  static bool sketchesShown(BuildContext context) {
    final media = MediaQuery.of(context);
    if (media.textScaler.scale(10) > 13) return false;
    return !(media.disableAnimations || media.highContrast || media.boldText);
  }

  /// The drawing's width divided by its height.
  static double aspectRatio(UserRole role) {
    final box = _drawings[role]!.viewBox;
    return box.width / box.height;
  }

  @override
  Widget build(BuildContext context) {
    if (!sketchesShown(context)) return const SizedBox.shrink();
    final drawing = _drawings[role]!;
    final size =
        Size(width, width * drawing.viewBox.height / drawing.viewBox.width);
    // Rule 1: decoration, nothing for a screen reader or a tap.
    return ExcludeSemantics(
      child: IgnorePointer(
        child: CustomPaint(
          size: size,
          painter: _SketchPainter(drawing, color, strokeWidth),
        ),
      ),
    );
  }
}

/// One stroke of a drawing: a path in the canvas's own SVG path words
/// (absolute M, L, H, V, C and Z only) or a circle, with its line weight
/// relative to the drawing's main line.
class _Stroke {
  final String? path;
  final Offset? center;
  final double radius;
  final double weight;

  const _Stroke.path(this.path, [this.weight = 1])
      : center = null,
        radius = 0;

  const _Stroke.circle(this.center, this.radius, [this.weight = 1])
      : path = null;
}

class _Drawing {
  final Rect viewBox;
  final List<_Stroke> strokes;

  const _Drawing(this.viewBox, this.strokes);
}

/// The three drawings, stroke for stroke as on the boards (FarmerHomeM,
/// TruckerHomeM, MarketM).
const Map<UserRole, _Drawing> _drawings = {
  // A coffee branch: the stem, two leaves with their midribs, two cherries.
  UserRole.farmer: _Drawing(Rect.fromLTWH(0, 0, 140, 104), [
    _Stroke.path('M140 4 C 112 14, 78 34, 48 72'),
    _Stroke.path('M98 26 C 86 10, 66 6, 50 12 C 56 28, 78 36, 98 26 Z', 0.9),
    _Stroke.path('M98 26 C 82 20, 66 15, 53 13', 0.73),
    _Stroke.path('M76 44 C 92 46, 108 60, 110 78 C 92 78, 78 64, 76 44 Z', 0.9),
    _Stroke.path('M76 44 C 88 54, 100 66, 108 76', 0.73),
    _Stroke.path('M48 72 L 44 80', 0.76),
    _Stroke.path('M48 72 L 56 82', 0.76),
    _Stroke.circle(Offset(42, 87), 6.5, 0.9),
    _Stroke.circle(Offset(58, 89), 6.5, 0.9),
  ]),
  // A loaded truck on the road: the road, the bed with three sacks, the
  // cab and its window, two wheels.
  UserRole.trucker: _Drawing(Rect.fromLTWH(0, 0, 140, 68), [
    _Stroke.path('M4 62 C 46 58, 96 60, 138 56', 0.9),
    _Stroke.path('M58 12 H 120 V 46 H 58 Z'),
    _Stroke.path('M66 12 C 68 5, 78 5, 80 12', 0.86),
    _Stroke.path('M82 12 C 84 4, 96 4, 98 12', 0.86),
    _Stroke.path('M100 12 C 102 6, 112 6, 114 12', 0.86),
    _Stroke.path(
      'M58 22 L 40 22 C 36 22, 33 25, 31 30 L 27 40 L 27 46 L 120 46',
    ),
    _Stroke.path('M39 26 L 52 26 L 52 35 L 35 35 Z', 0.86),
    _Stroke.circle(Offset(44, 50), 7),
    _Stroke.circle(Offset(104, 50), 7),
    _Stroke.circle(Offset(44, 50), 2, 0.81),
    _Stroke.circle(Offset(104, 50), 2, 0.81),
  ]),
  // A balance scale: the foot and post, the beam, two pans on their
  // cords, grain in one pan and two weights in the other.
  UserRole.buyer: _Drawing(Rect.fromLTWH(20, 20, 112, 76), [
    _Stroke.path('M58 92 L 96 92'),
    _Stroke.path('M77 92 L 77 30'),
    _Stroke.circle(Offset(77, 26), 3.5),
    _Stroke.path('M40 38 L 114 34'),
    _Stroke.path('M40 38 L 30 62', 0.83),
    _Stroke.path('M40 38 L 50 62', 0.83),
    _Stroke.path('M26 62 L 54 62 C 52 73, 28 73, 26 62 Z'),
    _Stroke.path('M114 34 L 104 56', 0.83),
    _Stroke.path('M114 34 L 124 56', 0.83),
    _Stroke.path('M100 56 L 128 56 C 126 67, 102 67, 100 56 Z'),
    _Stroke.path('M31 62 C 35 53, 45 53, 49 62', 0.83),
    _Stroke.circle(Offset(110, 51), 4, 0.83),
    _Stroke.circle(Offset(119, 51), 4, 0.83),
  ]),
};

/// Reads the few SVG path words the drawings use: absolute M, L, H, V, C
/// and Z, numbers split by spaces or commas.
@visibleForTesting
Path sketchPath(String d) {
  final path = Path();
  final tokens = RegExp(r'[MLHVCZ]|-?\d+(?:\.\d+)?').allMatches(d);
  var command = '';
  final numbers = <double>[];
  var current = Offset.zero;

  void flush() {
    switch (command) {
      case 'M':
        current = Offset(numbers[0], numbers[1]);
        path.moveTo(current.dx, current.dy);
      case 'L':
        current = Offset(numbers[0], numbers[1]);
        path.lineTo(current.dx, current.dy);
      case 'H':
        current = Offset(numbers[0], current.dy);
        path.lineTo(current.dx, current.dy);
      case 'V':
        current = Offset(current.dx, numbers[0]);
        path.lineTo(current.dx, current.dy);
      case 'C':
        current = Offset(numbers[4], numbers[5]);
        path.cubicTo(
          numbers[0],
          numbers[1],
          numbers[2],
          numbers[3],
          numbers[4],
          numbers[5],
        );
    }
    numbers.clear();
  }

  const arity = {'M': 2, 'L': 2, 'H': 1, 'V': 1, 'C': 6};
  for (final token in tokens) {
    final word = token.group(0)!;
    if (word == 'Z') {
      path.close();
      command = '';
    } else if (arity.containsKey(word)) {
      command = word;
    } else {
      numbers.add(double.parse(word));
      if (numbers.length == arity[command]) flush();
    }
  }
  return path;
}

class _SketchPainter extends CustomPainter {
  final _Drawing drawing;
  final Color color;
  final double strokeWidth;

  const _SketchPainter(this.drawing, this.color, this.strokeWidth);

  @override
  void paint(Canvas canvas, Size size) {
    final box = drawing.viewBox;
    final scale = size.width / box.width;
    canvas
      ..scale(scale)
      ..translate(-box.left, -box.top);
    for (final stroke in drawing.strokes) {
      final paint = Paint()
        ..color = color
        ..style = PaintingStyle.stroke
        // The same thickness on screen at every size.
        ..strokeWidth = strokeWidth * stroke.weight / scale
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round;
      final d = stroke.path;
      if (d != null) {
        canvas.drawPath(sketchPath(d), paint);
      } else {
        canvas.drawCircle(stroke.center!, stroke.radius, paint);
      }
    }
  }

  @override
  bool shouldRepaint(_SketchPainter old) =>
      old.drawing != drawing ||
      old.color != color ||
      old.strokeWidth != strokeWidth;
}
