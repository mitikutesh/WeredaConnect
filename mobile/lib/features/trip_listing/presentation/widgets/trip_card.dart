import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Card displaying a single trip listing.
///
/// Shows route (fromWoreda → toWoreda), departure date, capacity, price,
/// and status badge. Designed for ListView.builder to be memory-efficient
/// on 2GB devices.
class TripCard extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onTap;
  final VoidCallback? onCancel;

  const TripCard({
    required this.trip,
    super.key,
    this.onTap,
    this.onCancel,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final dateFormat = DateFormat.yMMMd();

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              // Truck icon
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: theme.colorScheme.primaryContainer,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Icon(
                  Icons.local_shipping,
                  color: theme.colorScheme.onPrimaryContainer,
                  size: 24,
                ),
              ),
              const SizedBox(width: 16),
              // Details
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      '${trip.fromWoreda} → ${trip.toWoreda}',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.bold,
                      ),
                    ),
                    const SizedBox(height: 4),
                    if (trip.vehiclePlate != null &&
                        trip.vehiclePlate!.isNotEmpty)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 2),
                        child: Text(
                          [
                            trip.vehiclePlate,
                            if (trip.vehicleDescription != null &&
                                trip.vehicleDescription!.isNotEmpty)
                              trip.vehicleDescription,
                          ].join(' · '),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ),
                    Text(
                      '${dateFormat.format(trip.departureDate)} · ${trip.capacity} ${l10n.quintals}',
                      style: theme.textTheme.bodyMedium,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '${trip.pricePerQuintal.toStringAsFixed(2)} ${l10n.etbCurrency}',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: theme.colorScheme.primary,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
                ),
              ),
              // Status badge
              _TripStatusChip(status: trip.status),
              if (onCancel != null && trip.status == TripStatus.open) ...[
                const SizedBox(width: 8),
                IconButton(
                  icon: const Icon(Icons.cancel_outlined),
                  onPressed: onCancel,
                  iconSize: 20,
                  color: AppTheme.statusError,
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _TripStatusChip extends StatelessWidget {
  final TripStatus status;

  const _TripStatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (color, label) = switch (status) {
      TripStatus.open => (AppTheme.statusSuccess, l10n.tripStatusOpen),
      TripStatus.matched => (AppTheme.statusInfo, l10n.tripStatusMatched),
      TripStatus.inTransit => (AppTheme.statusWarning, l10n.tripStatusInTransit),
      TripStatus.delivered => (AppTheme.statusSuccess, l10n.tripStatusDelivered),
      TripStatus.cancelled => (AppTheme.statusError, l10n.tripStatusCancelled),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: color,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}
