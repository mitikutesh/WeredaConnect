import 'package:flutter/material.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';

/// Icon-based status indicator for deliveries.
///
/// Displays a 48x48 (default) container with a status-appropriate icon
/// and color for low-literacy users. Larger icons are easier to
/// identify on low-end Android devices.
class DeliveryStatusIndicator extends StatelessWidget {
  final DeliveryStatus status;
  final double size;

  const DeliveryStatusIndicator({
    required this.status,
    this.size = 48,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final (icon, color) = switch (status) {
      DeliveryStatus.pending => (Icons.inventory_2, Colors.orange),
      DeliveryStatus.pickedUp => (Icons.local_shipping, Colors.blue),
      DeliveryStatus.inTransit => (Icons.route, Colors.indigo),
      DeliveryStatus.delivered => (Icons.check_circle, Colors.green),
      DeliveryStatus.disputed => (Icons.warning, Colors.red),
      DeliveryStatus.cancelled => (Icons.cancel, Colors.grey),
    };

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Center(
        child: Icon(
          icon,
          color: color,
          size: size / 2,
        ),
      ),
    );
  }
}
