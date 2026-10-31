import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Building blocks every Fieldway board repeats (specs/design-canvas):
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

  const FwPageHeader({
    required this.title,
    this.eyebrow,
    this.eyebrowWidget,
    this.trailing,
    this.padding = const EdgeInsets.fromLTRB(20, 16, 20, 12),
    super.key,
  });

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
                  color: AppTheme.mutedSteel,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ));
    return Padding(
      padding: padding,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
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
            ),
          ),
          if (trailing != null) ...[
            const SizedBox(width: 12),
            trailing!,
          ],
        ],
      ),
    );
  }
}

/// Type the boards repeat.
abstract final class FwText {
  static const TextStyle pageTitle = TextStyle(
    fontSize: 26,
    fontWeight: FontWeight.w800,
    letterSpacing: -0.6,
    height: 1.15,
    color: AppTheme.soilDark,
  );

  static const TextStyle cardTitle = TextStyle(
    fontSize: 16,
    fontWeight: FontWeight.w800,
    letterSpacing: -0.2,
    color: AppTheme.soilDark,
  );

  static const TextStyle body = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w500,
    color: AppTheme.mutedSteel,
  );

  static const TextStyle sectionLabel = TextStyle(
    fontSize: 12,
    fontWeight: FontWeight.w700,
    letterSpacing: 0.7,
    color: AppTheme.mutedSteel,
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

/// The boards' segmented switch: a chalk-grey track holding white raised
/// segments (Market Browse / My Requests, Deliveries Active / Delivered /
/// All, My Crops Active / Reserved / Sold / Drafts).
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
    return Container(
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: AppTheme.segmentTrack,
        borderRadius: BorderRadius.circular(14),
      ),
      child: Row(
        children: [
          for (final segment in segments)
            Expanded(
              child: _FwSegmentButton<T>(
                segment: segment,
                selected: segment.value == selected,
                onTap: () => onChanged(segment.value),
              ),
            ),
        ],
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
    return Semantics(
      button: true,
      selected: selected,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(11),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            constraints: const BoxConstraints(minHeight: 40),
            padding: const EdgeInsets.symmetric(horizontal: 6),
            decoration: BoxDecoration(
              color: selected ? AppTheme.surfaceWhite : Colors.transparent,
              borderRadius: BorderRadius.circular(11),
              boxShadow: selected
                  ? const [
                      BoxShadow(
                        color: Color(0x14000000),
                        blurRadius: 4,
                        offset: Offset(0, 1),
                      ),
                    ]
                  : null,
            ),
            alignment: Alignment.center,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (segment.icon != null) ...[
                  Icon(
                    segment.icon,
                    size: 16,
                    color: selected ? AppTheme.fieldGreen : AppTheme.mutedSteel,
                  ),
                  const SizedBox(width: 6),
                ],
                Flexible(
                  child: Text(
                    label,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                      color:
                          selected ? AppTheme.soilDark : AppTheme.inkSecondary,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The boards' square action button (the bell, the place pin): white,
/// whisper border, 44 by 44, radius 14.
class FwSquareButton extends StatelessWidget {
  final Widget child;
  final VoidCallback? onTap;
  final String? semanticLabel;
  final Color background;

  const FwSquareButton({
    required this.child,
    this.onTap,
    this.semanticLabel,
    this.background = AppTheme.surfaceWhite,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: onTap != null,
      label: semanticLabel,
      child: Material(
        color: background,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: const BorderSide(color: AppTheme.whisperBorder),
        ),
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
    );
  }
}

/// A small rounded status pill ("In Transit", "Release pending").
class FwPill extends StatelessWidget {
  final String label;
  final Color background;
  final Color foreground;
  final IconData? icon;

  const FwPill({
    required this.label,
    this.background = AppTheme.actionCream,
    this.foreground = AppTheme.money,
    this.icon,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(8),
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
                fontWeight: FontWeight.w700,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The boards' white card: radius 18, whisper border, optional tap.
class FwCard extends StatelessWidget {
  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;
  final Color color;
  final Color borderColor;

  const FwCard({
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.onTap,
    this.color = AppTheme.surfaceWhite,
    this.borderColor = AppTheme.whisperBorder,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: color,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(18),
        side: BorderSide(color: borderColor),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(padding: padding, child: child),
      ),
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
      return (background: const Color(0xFFF4EBCF), foreground: AppTheme.money);
    case 'teff' || 'sorghum' || 'coffee':
      return (background: const Color(0xFFEFE6D8), foreground: AppTheme.money);
    case 'chickpea' || 'lentils':
      return (
        background: const Color(0xFFE6EFD9),
        foreground: AppTheme.fieldGreen,
      );
    default:
      return (
        background: AppTheme.fieldGreenTint,
        foreground: AppTheme.fieldGreen,
      );
  }
}
