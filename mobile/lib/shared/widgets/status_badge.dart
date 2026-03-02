import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Color-coded status chip for orders, deliveries, listings.
///
/// Renders a small rounded badge with a colored background and text.
/// Use [StatusBadgeType] presets or provide custom [color].
enum StatusBadgeType {
  success,
  warning,
  error,
  info,
  transit,
  pending,
}

class StatusBadge extends StatelessWidget {
  final String label;
  final StatusBadgeType type;

  const StatusBadge({
    required this.label,
    super.key,
    this.type = StatusBadgeType.info,
  });

  @override
  Widget build(BuildContext context) {
    final (bg, fg) = _colors(type);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: fg,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }

  static (Color bg, Color fg) _colors(StatusBadgeType type) {
    return switch (type) {
      StatusBadgeType.success => (
          const Color(0xFFDCFCE7),
          AppTheme.statusSuccess,
        ),
      StatusBadgeType.warning => (
          const Color(0xFFFEF3C7),
          const Color(0xFF92400E),
        ),
      StatusBadgeType.error => (
          const Color(0xFFFEE2E2),
          AppTheme.statusError,
        ),
      StatusBadgeType.info => (
          const Color(0xFFDBEAFE),
          AppTheme.statusInfo,
        ),
      StatusBadgeType.transit => (
          const Color(0xFFEDE9FE),
          AppTheme.statusTransit,
        ),
      StatusBadgeType.pending => (
          const Color(0xFFFEF3C7),
          AppTheme.statusWarning,
        ),
    };
  }
}
