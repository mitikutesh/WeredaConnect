import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The single status vocabulary for WeredaConnect.
///
/// Every status enum (orders, trips, escrow, deliveries) maps onto these
/// six kinds so users can transfer color-meaning between features:
/// "in transit" is the same harvest brown on a trip, an order, and a delivery.
/// Color is never the only signal — every chip carries an icon + label,
/// and all pairs meet WCAG AA (≥6.3:1, computed in the design brief).
enum StatusKind { neutral, success, warning, error, info, transit }

extension StatusKindVisual on StatusKind {
  Color get background => switch (this) {
        StatusKind.neutral => AppTheme.statusNeutralContainer,
        StatusKind.success => AppTheme.statusSuccessContainer,
        StatusKind.warning => AppTheme.statusWarningContainer,
        StatusKind.error => AppTheme.statusErrorContainer,
        StatusKind.info => AppTheme.statusInfoContainer,
        StatusKind.transit => AppTheme.statusTransitContainer,
      };

  Color get foreground => switch (this) {
        StatusKind.neutral => AppTheme.onStatusNeutralContainer,
        StatusKind.success => AppTheme.onStatusSuccessContainer,
        StatusKind.warning => AppTheme.onStatusWarningContainer,
        StatusKind.error => AppTheme.onStatusErrorContainer,
        StatusKind.info => AppTheme.onStatusInfoContainer,
        StatusKind.transit => AppTheme.onStatusTransitContainer,
      };

  IconData get defaultIcon => switch (this) {
        StatusKind.neutral => Icons.radio_button_unchecked,
        StatusKind.success => Icons.check_circle,
        StatusKind.warning => Icons.error_outline,
        StatusKind.error => Icons.cancel,
        StatusKind.info => Icons.info_outline,
        StatusKind.transit => Icons.local_shipping_outlined,
      };
}

/// Compact icon + label chip; the only way statuses are rendered.
///
/// [large] renders the detail-screen variant (bigger type and padding).
/// Pass [icon] to override the kind's default glyph when a status has a
/// more specific metaphor (e.g. a shield for "escrowed").
class StatusChip extends StatelessWidget {
  final String label;
  final StatusKind kind;
  final IconData? icon;
  final bool large;

  const StatusChip({
    required this.label,
    required this.kind,
    this.icon,
    this.large = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final fg = kind.foreground;
    final bg = kind.background;

    return Container(
      // The boards' status pill: tinted ground, no outline.
      padding: large
          ? const EdgeInsets.symmetric(horizontal: 14, vertical: 7)
          : const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(large ? 10 : 8),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon ?? kind.defaultIcon, size: large ? 20 : 14, color: fg),
          SizedBox(width: large ? 8 : 4),
          Flexible(
            child: Text(
              label,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                color: fg,
                fontSize: large ? 16 : 12,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// A read that failed, said as such (E-6): an error pill with [message] and
/// a [retryLabel] button, never an empty list or a zero that looks like
/// "none".
class LoadErrorChip extends StatelessWidget {
  final String message;
  final String retryLabel;
  final VoidCallback onRetry;

  const LoadErrorChip({
    required this.message,
    required this.retryLabel,
    required this.onRetry,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    const kind = StatusKind.error;
    return Container(
      padding: const EdgeInsetsDirectional.fromSTEB(12, 4, 4, 4),
      decoration: BoxDecoration(
        color: kind.background,
        borderRadius: BorderRadius.circular(12),
      ),
      // The retry button takes half the row at most: with large text a long
      // label ("Irra Deebi'ii Yaali" at 200%) wraps and leaves the message
      // room, instead of pushing the row off the screen.
      child: LayoutBuilder(
        builder: (context, constraints) => Row(
          children: [
            Icon(Icons.error_outline, size: 16, color: kind.foreground),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                message,
                style: TextStyle(
                  color: kind.foreground,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
            ConstrainedBox(
              constraints: BoxConstraints(maxWidth: constraints.maxWidth / 2),
              child: TextButton(
                onPressed: onRetry,
                style: TextButton.styleFrom(
                  foregroundColor: kind.foreground,
                  minimumSize: const Size(48, 40),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 13,
                    fontWeight: FontWeight.w800,
                  ),
                ),
                child: Text(retryLabel, textAlign: TextAlign.center),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
