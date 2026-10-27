import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Card displaying a single trip listing.
///
/// Redesigned for rural trucker usability:
/// - Full-width status color bar at top
/// - Visual route line (green dot → dashed → red dot)
/// - Large bold price text
/// - 56dp touch targets on all interactive elements
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
    final dateFormat = DateFormat.yMMMd();

    final statusColor = _statusColor(trip.status);

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Status color bar
            Container(
              height: 4,
              color: statusColor,
            ),
            Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // Route line with dots
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      // Visual route line
                      Padding(
                        padding: const EdgeInsets.only(top: 2),
                        child: Column(
                          children: [
                            Container(
                              width: 10,
                              height: 10,
                              decoration: const BoxDecoration(
                                color: AppTheme.statusSuccess,
                                shape: BoxShape.circle,
                              ),
                            ),
                            Container(
                              width: 2,
                              height: 20,
                              color: AppTheme.borderLight,
                            ),
                            Container(
                              width: 10,
                              height: 10,
                              decoration: const BoxDecoration(
                                color: AppTheme.statusError,
                                shape: BoxShape.circle,
                              ),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(width: 10),
                      // Route text
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              trip.fromWoreda,
                              style: const TextStyle(
                                fontWeight: FontWeight.w600,
                                fontSize: 15,
                                color: AppTheme.textDark,
                              ),
                              overflow: TextOverflow.ellipsis,
                            ),
                            const SizedBox(height: 10),
                            Text(
                              trip.toWoreda,
                              style: const TextStyle(
                                fontWeight: FontWeight.w600,
                                fontSize: 15,
                                color: AppTheme.textDark,
                              ),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ],
                        ),
                      ),
                      // Status badge + cancel
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          _TripStatusChip(status: trip.status),
                          if (onCancel != null &&
                              (trip.status == TripStatus.open ||
                                  trip.status == TripStatus.matched))
                            Padding(
                              padding: const EdgeInsets.only(top: 4),
                              child: IconButton(
                                icon: const Icon(Icons.cancel_outlined),
                                onPressed: onCancel,
                                iconSize: 20,
                                color: AppTheme.statusError,
                                constraints: const BoxConstraints(
                                  minWidth: 56,
                                  minHeight: 56,
                                ),
                              ),
                            ),
                        ],
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  // Price — large and bold
                  Text(
                    '${l10n.etbCurrency(trip.pricePerQuintal.toStringAsFixed(0))}/${l10n.quintals}',
                    style: const TextStyle(
                      fontSize: 20,
                      fontWeight: FontWeight.bold,
                      color: AppTheme.primaryGreen,
                    ),
                  ),
                  const SizedBox(height: 8),
                  // Meta row: date + capacity
                  Row(
                    children: [
                      const Icon(
                        Icons.calendar_today,
                        size: 14,
                        color: AppTheme.textMuted,
                      ),
                      const SizedBox(width: 4),
                      Text(
                        dateFormat.format(dayInEthiopia(trip.departureDate)),
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.textMuted,
                        ),
                      ),
                      const SizedBox(width: 16),
                      const Icon(
                        Icons.local_shipping_outlined,
                        size: 14,
                        color: AppTheme.textMuted,
                      ),
                      const SizedBox(width: 4),
                      Flexible(
                        child: Text(
                          // ESC-15: a booked trip shows the room left.
                          trip.remainingQuintals == null
                              ? '${trip.capacity} ${l10n.quintals}'
                              : l10n.tripRemainingCapacity(
                                  formatQuintals(tripRoomLeft(trip)),
                                ),
                          style: const TextStyle(
                            fontSize: 13,
                            color: AppTheme.textMuted,
                          ),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  static Color _statusColor(TripStatus status) =>
      tripStatusKind(status).foreground;
}

class _TripStatusChip extends StatelessWidget {
  final TripStatus status;

  const _TripStatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return StatusChip(
      label: tripStatusLabel(status, l10n),
      kind: tripStatusKind(status),
    );
  }
}
