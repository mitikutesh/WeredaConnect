import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The single status vocabulary for WeredaConnect.
///
/// Every status enum (orders, trips, escrow, deliveries) maps onto these
/// six kinds so users can transfer color-meaning between features:
/// "in transit" is the same violet on a trip, an order, and a delivery.
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
      padding: large
          ? const EdgeInsets.symmetric(horizontal: 16, vertical: 8)
          : const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: fg.withValues(alpha: 0.25)),
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
                fontWeight: large ? FontWeight.w700 : FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
