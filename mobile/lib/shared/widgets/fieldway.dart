import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// Building blocks every board repeats (design canvas page M,
/// specs/design-canvas; the Fw prefix is from the earlier Fieldway boards):
/// the page header, the uppercase section label, the segmented switch, the
/// square icon button, the status pill and the phone-width content column.

/// The boards' page header: an optional context line ("Selam Traders ·
/// Merkato"), the page title, and a trailing action (usually the bell).
class FwPageHeader extends StatelessWidget {
  final String? eyebrow;
  final Widget? eyebrowWidget;
  final String title;
  final Widget? trailing;
  final EdgeInsetsGeometry padding;

  /// Draws this role's sketch at the header's end, beside the title, when
  /// the title leaves it room (the canvas's sketch rules, [RoleSketch]).
  final UserRole? sketchRole;

  const FwPageHeader({
    required this.title,
    this.eyebrow,
    this.eyebrowWidget,
    this.trailing,
    this.sketchRole,
    this.padding = const EdgeInsets.fromLTRB(20, 16, 20, 12),
    super.key,
  });

  /// The header sketch's widest and narrowest size, and the gap it keeps
  /// from the title.
  static const double _sketchWidth = 84;
  static const double _sketchMinWidth = 56;
  static const double _sketchGap = 16;

  /// How wide the sketch may be beside [title] in a header [width] wide,
  /// or null when the title leaves it no room: the sketch is never under
  /// the title, and never squeezed below [_sketchMinWidth].
  static double? _sketchWidthFor(
    BuildContext context,
    String title,
    double width,
  ) {
    if (!RoleSketch.sketchesShown(context)) return null;
    final painter = TextPainter(
      text: TextSpan(
        text: title,
        style: DefaultTextStyle.of(context).style.merge(FwText.pageTitle),
      ),
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
      maxLines: 1,
    )..layout();
    final room = width - painter.width - _sketchGap;
    painter.dispose();
    if (room < _sketchMinWidth) return null;
    return room < _sketchWidth ? room : _sketchWidth;
  }

  @override
  Widget build(BuildContext context) {
    final eyebrowWidget = this.eyebrowWidget ??
        (eyebrow == null || eyebrow!.isEmpty
            ? null
            : Text(
                eyebrow!,
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.slate,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ));
    final text = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (eyebrowWidget != null) ...[
          eyebrowWidget,
          const SizedBox(height: 2),
        ],
        Semantics(
          header: true,
          child: Text(
            title,
            style: FwText.pageTitle,
          ),
        ),
      ],
    );
    final sketchRole = this.sketchRole;
    // The sketch sits at the end of the title's own column, on the
    // title's line: before the trailing buttons, never under them.
    final heading = sketchRole == null
        ? text
        : LayoutBuilder(
            builder: (context, constraints) {
              final sketchWidth =
                  _sketchWidthFor(context, title, constraints.maxWidth);
              if (sketchWidth == null) return text;
              return Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Expanded(child: text),
                  const SizedBox(width: _sketchGap),
                  RoleSketch(role: sketchRole, width: sketchWidth),
                ],
              );
            },
          );
    final trailing = this.trailing;
    return Padding(
      padding: padding,
      child: trailing == null
          ? Row(children: [Expanded(child: heading)])
          : FwHeaderRow(
              heading: heading,
              trailing: trailing,
              headingFontSize: FwText.pageTitle.fontSize!,
              // A 42 px title: seven letters of room (294 px) keep the
              // bell beside it on every phone, as on the boards.
              minWidthInLetters: 7,
            ),
    );
  }
}

/// A header's title beside its trailing buttons, or under them when the
/// row is too narrow for both: at 200% zoom or with large text, a title
/// squeezed beside the bell and account buttons broke one syllable per
/// line. The buttons then sit on their own line at the end, above the
/// title, which gets the full width.
class FwHeaderRow extends StatelessWidget {
  final Widget heading;
  final Widget trailing;

  /// The heading's font size before text scaling: the room it needs is
  /// counted in letters of this size.
  final double headingFontSize;

  /// How the heading and the buttons line up side by side.
  final CrossAxisAlignment crossAxisAlignment;

  const FwHeaderRow({
    required this.heading,
    required this.trailing,
    this.headingFontSize = 26,
    this.minWidthInLetters = 9,
    this.crossAxisAlignment = CrossAxisAlignment.start,
    super.key,
  });

  /// Below this many heading-sized letters of width the buttons move up.
  final double minWidthInLetters;

  @override
  Widget build(BuildContext context) {
    final letter = MediaQuery.textScalerOf(context).scale(headingFontSize);
    // A screen reader reads the title first, then the buttons, in both
    // layouts: stacked, the buttons are drawn above the title.
    final heading = Semantics(
      container: true,
      explicitChildNodes: true,
      sortKey: const OrdinalSortKey(0),
      child: this.heading,
    );
    final trailing = Semantics(
      container: true,
      explicitChildNodes: true,
      sortKey: const OrdinalSortKey(1),
      child: this.trailing,
    );
    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth < letter * minWidthInLetters) {
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Align(alignment: AlignmentDirectional.centerEnd, child: trailing),
              const SizedBox(height: 8),
              heading,
            ],
          );
        }
        return Row(
          crossAxisAlignment: crossAxisAlignment,
          children: [
            Expanded(child: heading),
            const SizedBox(width: 12),
            trailing,
          ],
        );
      },
    );
  }
}

/// Type the boards repeat.
abstract final class FwText {
  static const TextStyle pageTitle = TextStyle(
    fontSize: 42,
    fontWeight: FontWeight.w800,
    letterSpacing: -1.4,
    height: 1.1,
    color: AppTheme.ink,
  );

  /// The title of a page under a tab (a detail page, a form, a join step).
  static const TextStyle detailTitle = TextStyle(
    fontSize: 32,
    fontWeight: FontWeight.w800,
    letterSpacing: -1.1,
    height: 1.1,
    color: AppTheme.ink,
  );

  static const TextStyle cardTitle = TextStyle(
    fontSize: 17,
    fontWeight: FontWeight.w800,
    letterSpacing: -0.3,
    color: AppTheme.ink,
  );

  static const TextStyle body = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w500,
    color: AppTheme.slate,
  );

  static const TextStyle sectionLabel = TextStyle(
    fontSize: 12,
    fontWeight: FontWeight.w700,
    letterSpacing: 1.4,
    color: AppTheme.slate,
  );
}

/// The uppercase section label ("AT A GLANCE", "ON THE ROAD NOW").
class FwSectionLabel extends StatelessWidget {
  final String text;
  final EdgeInsetsGeometry padding;

  const FwSectionLabel(
    this.text, {
    this.padding = EdgeInsets.zero,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: padding,
      child: Semantics(
        header: true,
        child: Text(
          text.toUpperCase(),
          style: FwText.sectionLabel,
        ),
      ),
    );
  }
}

/// A short label (a tab, a segment, a language) that wraps at spaces and
/// never inside a word: when its longest word is wider than the room, the
/// label is laid out at that word's width and scaled down to fit, the only
/// case where it shrinks. Large text and long Afaan Oromoo words
/// ("Geejjibaalee") used to break mid-word ("Geejjibaale|e").
///
/// Works under [IntrinsicHeight], which a [LayoutBuilder] would not.
class FwWordFitText extends StatelessWidget {
  final String text;
  final TextStyle style;
  final int maxLines;
  final TextAlign textAlign;

  const FwWordFitText(
    this.text, {
    required this.style,
    this.maxLines = 2,
    this.textAlign = TextAlign.center,
    super.key,
  });

  /// The width of [text]'s widest word in [context]'s text style and scale.
  static double longestWordWidth(
    BuildContext context,
    String text,
    TextStyle style,
  ) {
    var effective = DefaultTextStyle.of(context).style.merge(style);
    if (MediaQuery.boldTextOf(context)) {
      effective = effective.merge(const TextStyle(fontWeight: FontWeight.bold));
    }
    final painter = TextPainter(
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
      maxLines: 1,
    );
    var widest = 0.0;
    for (final word in text.split(RegExp(r'\s+'))) {
      if (word.isEmpty) continue;
      painter
        ..text = TextSpan(text: word, style: effective)
        ..layout();
      if (painter.width > widest) widest = painter.width;
    }
    painter.dispose();
    return widest.ceilToDouble();
  }

  @override
  Widget build(BuildContext context) {
    return _WordFit(
      minChildWidth: longestWordWidth(context, text, style),
      child: Text(
        text,
        style: style,
        maxLines: maxLines,
        overflow: TextOverflow.ellipsis,
        textAlign: textAlign,
      ),
    );
  }
}

class _WordFit extends SingleChildRenderObjectWidget {
  final double minChildWidth;

  const _WordFit({required this.minChildWidth, required super.child});

  @override
  RenderObject createRenderObject(BuildContext context) =>
      _RenderWordFit(minChildWidth);

  @override
  void updateRenderObject(BuildContext context, _RenderWordFit renderObject) {
    renderObject.minChildWidth = minChildWidth;
  }
}

/// Lays its child out at least [minChildWidth] wide and scales it down
/// when that is wider than the room.
class _RenderWordFit extends RenderProxyBox {
  _RenderWordFit(this._minChildWidth);

  double _minChildWidth;
  double _scale = 1;

  set minChildWidth(double value) {
    if (value == _minChildWidth) return;
    _minChildWidth = value;
    markNeedsLayout();
  }

  bool _shrinks(double maxWidth) =>
      maxWidth.isFinite && maxWidth > 0 && _minChildWidth > maxWidth;

  BoxConstraints _childConstraints(BoxConstraints constraints) =>
      BoxConstraints(maxWidth: _minChildWidth);

  @override
  double computeMinIntrinsicHeight(double width) => _scaledHeight(
        width,
        (w) => child?.getMinIntrinsicHeight(w) ?? 0,
      );

  @override
  double computeMaxIntrinsicHeight(double width) => _scaledHeight(
        width,
        (w) => child?.getMaxIntrinsicHeight(w) ?? 0,
      );

  double _scaledHeight(double width, double Function(double) height) {
    if (!_shrinks(width)) return height(width);
    return height(_minChildWidth) * width / _minChildWidth;
  }

  @override
  Size computeDryLayout(BoxConstraints constraints) {
    final child = this.child;
    if (child == null) return constraints.smallest;
    if (!_shrinks(constraints.maxWidth)) return child.getDryLayout(constraints);
    final natural = child.getDryLayout(_childConstraints(constraints));
    final scale = (constraints.maxWidth / natural.width).clamp(0.0, 1.0);
    return constraints.constrain(natural * scale);
  }

  @override
  void performLayout() {
    final child = this.child;
    if (child == null) {
      size = constraints.smallest;
      return;
    }
    if (!_shrinks(constraints.maxWidth)) {
      _scale = 1;
      child.layout(constraints, parentUsesSize: true);
      size = constraints.constrain(child.size);
      return;
    }
    child.layout(_childConstraints(constraints), parentUsesSize: true);
    _scale = (constraints.maxWidth / child.size.width).clamp(0.0, 1.0);
    size = constraints.constrain(child.size * _scale);
  }

  Matrix4 get _transform => Matrix4.diagonal3Values(_scale, _scale, 1);

  @override
  void paint(PaintingContext context, Offset offset) {
    final child = this.child;
    if (child == null) return;
    if (_scale == 1) {
      context.paintChild(child, offset);
      return;
    }
    layer = context.pushTransform(
      needsCompositing,
      offset,
      _transform,
      (context, offset) => context.paintChild(child, offset),
      oldLayer: layer as TransformLayer?,
    );
  }

  @override
  bool hitTestChildren(BoxHitTestResult result, {required Offset position}) {
    final child = this.child;
    if (child == null) return false;
    return result.addWithPaintTransform(
      transform: _transform,
      position: position,
      hitTest: (result, position) => child.hitTest(result, position: position),
    );
  }

  @override
  void applyPaintTransform(RenderBox child, Matrix4 transform) {
    transform.multiply(_transform);
  }
}

/// One option of a [FwSegmented] switch.
class FwSegment<T> {
  final T value;
  final String label;

  /// Shown after the label as " · 2" when not null.
  final int? count;
  final IconData? icon;

  const FwSegment({
    required this.value,
    required this.label,
    this.count,
    this.icon,
  });
}

/// The boards' switch between a page's views (Market Browse / My
/// Requests, Deliveries Active / Delivered / All, My Crops Active /
/// Reserved / Sold / Drafts): a row of pills, the selected one black with
/// white ink, the others outlined. The row scrolls sideways when the
/// pills do not fit (long labels, large text).
class FwSegmented<T> extends StatelessWidget {
  final List<FwSegment<T>> segments;
  final T selected;
  final ValueChanged<T> onChanged;

  const FwSegmented({
    required this.segments,
    required this.selected,
    required this.onChanged,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: AlignmentDirectional.centerStart,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
          children: [
            for (var i = 0; i < segments.length; i++) ...[
              if (i > 0) const SizedBox(width: 8),
              _FwSegmentButton<T>(
                segment: segments[i],
                selected: segments[i].value == selected,
                onTap: () => onChanged(segments[i].value),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _FwSegmentButton<T> extends StatelessWidget {
  final FwSegment<T> segment;
  final bool selected;
  final VoidCallback onTap;

  const _FwSegmentButton({
    required this.segment,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final count = segment.count;
    final label = count == null ? segment.label : '${segment.label} · $count';
    final foreground = selected ? Colors.white : AppTheme.inkSoft;
    return Semantics(
      button: true,
      selected: selected,
      // A 44 tall pill in a 48 tall hit area.
      child: FwTapTarget(
        onTap: onTap,
        child: Material(
          color: selected ? AppTheme.ink : Colors.transparent,
          shape: StadiumBorder(
            side: BorderSide(
              color: selected ? AppTheme.ink : AppTheme.fieldBorder,
            ),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (segment.icon != null) ...[
                      Icon(segment.icon, size: 16, color: foreground),
                      const SizedBox(width: 6),
                    ],
                    Text(
                      label,
                      maxLines: 1,
                      softWrap: false,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight:
                            selected ? FontWeight.w700 : FontWeight.w500,
                        color: foreground,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Grows a control's hit area to Material's 48 by 48 minimum without
/// changing how it looks: [child] sits centred in the larger box and a tap
/// on the margin reaches [onTap], as a tap on the child does. Put the
/// control's [Semantics] around it, so screen readers get the larger box.
class FwTapTarget extends StatelessWidget {
  final Widget child;
  final VoidCallback? onTap;
  final Size minSize;

  const FwTapTarget({
    required this.child,
    required this.onTap,
    this.minSize = const Size.square(kMinInteractiveDimension),
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      behavior: HitTestBehavior.opaque,
      // The child's own semantics (its tap action) speak for it.
      excludeFromSemantics: true,
      child: ConstrainedBox(
        constraints: BoxConstraints(
          minWidth: minSize.width,
          minHeight: minSize.height,
        ),
        child: Center(widthFactor: 1, heightFactor: 1, child: child),
      ),
    );
  }
}

/// The boards' round action button (the bell, back, the place pin): a 44
/// by 44 circle on the paper grey, no edge, in a 48 by 48 hit area. (The
/// name is from the earlier boards, where it was a square.)
class FwSquareButton extends StatelessWidget {
  final Widget child;
  final VoidCallback? onTap;
  final String? semanticLabel;
  final Color background;

  const FwSquareButton({
    required this.child,
    this.onTap,
    this.semanticLabel,
    this.background = AppTheme.paper,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: onTap != null,
      label: semanticLabel,
      child: FwTapTarget(
        onTap: onTap,
        child: Material(
          color: background,
          shape: const CircleBorder(),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: SizedBox(
              width: 44,
              height: 44,
              child: Center(child: child),
            ),
          ),
        ),
      ),
    );
  }
}

/// A small rounded status pill ("In Transit", "Release pending").
class FwPill extends StatelessWidget {
  final String label;
  final Color? background;
  final Color? foreground;
  final IconData? icon;

  const FwPill({
    required this.label,
    this.background,
    this.foreground,
    this.icon,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    // The role's tint pill unless the caller names a pair.
    final background = this.background ?? context.roleColors.tint;
    final foreground = this.foreground ?? context.roleColors.primary;
    return Container(
      constraints: const BoxConstraints(minHeight: 26),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: ShapeDecoration(
        color: background,
        shape: const StadiumBorder(),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 13, color: foreground),
            const SizedBox(width: 4),
          ],
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w800,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The boards' card. Three grounds:
/// - the default: paper grey with a quiet edge, radius 24
/// - [raised]: the card a screen leads with (the action card, a listing):
///   the same paper, radius 28, on a soft shadow
/// - [FwCard.sand]: a flat tile or note on the sand ground, no edge
class FwCard extends StatelessWidget {
  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;
  final Color color;
  final Color? borderColor;
  final bool raised;

  const FwCard({
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.onTap,
    this.color = AppTheme.paper,
    this.borderColor = AppTheme.line,
    this.raised = false,
    super.key,
  });

  const FwCard.sand({
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.onTap,
    super.key,
  })  : color = AppTheme.sand,
        borderColor = null,
        raised = false;

  @override
  Widget build(BuildContext context) {
    final radius = BorderRadius.circular(raised ? 28 : 24);
    final borderColor = this.borderColor;
    final card = Material(
      color: color,
      shape: RoundedRectangleBorder(
        borderRadius: radius,
        side: borderColor == null
            ? BorderSide.none
            : BorderSide(color: borderColor),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(padding: padding, child: child),
      ),
    );
    if (!raised) return card;
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: radius,
        boxShadow: const [
          BoxShadow(
            color: AppTheme.shadowSoft,
            blurRadius: 30,
            offset: Offset(0, 14),
          ),
        ],
      ),
      child: card,
    );
  }
}

/// Keeps a phone board's column readable on a wide window: full width
/// below [Breakpoints.mobile], else centred at [maxWidth]. Every tab body
/// keeps its board layout at every width instead of a separate desktop
/// layout. It is never taller than its child, so it also fits a pinned
/// bottom bar or a header in a Column.
class FwContentWidth extends StatelessWidget {
  final Widget child;
  final double maxWidth;

  const FwContentWidth({
    required this.child,
    this.maxWidth = FwContentWidth.defaultMaxWidth,
    super.key,
  });

  static const double defaultMaxWidth = 720;

  @override
  Widget build(BuildContext context) {
    if (Breakpoints.isMobile(context)) return child;
    return Align(
      alignment: Alignment.topCenter,
      heightFactor: 1,
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: maxWidth),
        child: child,
      ),
    );
  }
}

/// A crop tile's colours as the boards paint them (Market, My Crops,
/// Deliveries): wheat, barley and sesame on cream, teff, sorghum and coffee
/// on the warmer sand, the pulses on the olive tint, maize and anything
/// else on field green tint. [cropKey] is the stored key ("wheat").
({Color background, Color foreground}) cropTileColors(String? cropKey) {
  switch (cropKey?.toLowerCase()) {
    case 'wheat' || 'barley' || 'sesame':
      return (background: AppTheme.orangeTint, foreground: AppTheme.orangeInk);
    case 'teff' || 'sorghum' || 'coffee':
      return (background: AppTheme.sand, foreground: AppTheme.inkSoft);
    case 'chickpea' || 'lentils':
      return (background: AppTheme.segmentTrack, foreground: AppTheme.inkSoft);
    default:
      return (
        background: AppTheme.statusSuccessContainer,
        foreground: AppTheme.statusSuccess,
      );
  }
}

/// The boards' crop glyph: a stem and two leaves, the same for every crop
/// (the tile's colour tells the crops apart, [cropTileColors]). Drawn from
/// the canvas's 24 px path, stroked at 2 px with round ends.
class FwSprout extends StatelessWidget {
  final double size;

  /// The role's colour when null.
  final Color? color;

  const FwSprout({this.size = 22, this.color, super.key});

  @override
  Widget build(BuildContext context) {
    final color = this.color ?? context.roleColors.primary;
    // Centred at its own size inside a tile (a tight box), and only its
    // size anywhere else.
    return Center(
      widthFactor: 1,
      heightFactor: 1,
      child: CustomPaint(
        size: Size.square(size),
        painter: _SproutPainter(color),
      ),
    );
  }
}

class _SproutPainter extends CustomPainter {
  final Color color;

  const _SproutPainter(this.color);

  @override
  void paint(Canvas canvas, Size size) {
    canvas.scale(size.width / 24, size.height / 24);
    final stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    // M12 20v-8
    canvas.drawLine(const Offset(12, 20), const Offset(12, 12), stroke);
    // M12 12c0-4 3-7 8-7 0 5-3 8-8 7z
    canvas.drawPath(
      Path()
        ..moveTo(12, 12)
        ..cubicTo(12, 8, 15, 5, 20, 5)
        ..cubicTo(20, 10, 17, 13, 12, 12)
        ..close(),
      stroke,
    );
    // M12 14c0-3-2.5-5.5-7-5.5 0 4 2.5 6 7 5.5z
    canvas.drawPath(
      Path()
        ..moveTo(12, 14)
        ..cubicTo(12, 11, 9.5, 8.5, 5, 8.5)
        ..cubicTo(5, 12.5, 7.5, 14.5, 12, 14)
        ..close(),
      stroke,
    );
  }

  @override
  bool shouldRepaint(_SproutPainter oldDelegate) => oldDelegate.color != color;
}
