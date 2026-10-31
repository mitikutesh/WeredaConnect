import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// One trip in a list, a sibling of the Trip detail board's header: the
/// route as the title, the departure day and the price per quintal under
/// it, the status pill, and the booked share as the boards' load bar.
///
/// [onCancel] (the trucker's own open or matched trips only) adds a plain
/// Cancel Trip action inside the card.
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

  /// Paid quintals on the trip: the server's count, else the gap between
  /// the capacity and the room left; zero for a trip nobody booked.
  static double bookedQuintals(Trip trip) {
    final booked = trip.bookedQuintals;
    if (booked != null) return booked;
    final remaining = trip.remainingQuintals;
    if (remaining == null) return 0;
    return (trip.capacity - remaining).clamp(0, trip.capacity);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = boardDayInEthiopia(trip.departureDate);
    final price = l10n.tripPricePerQuintalValue(
      formatEtbAmount(trip.pricePerQuintal),
    );
    final booked = bookedQuintals(trip);
    final canCancel = onCancel != null &&
        (trip.status == TripStatus.open || trip.status == TripStatus.matched);

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 5),
      child: FwCard(
        onTap: onTap,
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${trip.fromWoreda} → ${trip.toWoreda}',
                        style: FwText.cardTitle,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                      const SizedBox(height: 3),
                      Text(
                        '$day · $price',
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w500,
                          color: AppTheme.mutedSteel,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 10),
                StatusChip(
                  label: tripStatusLabel(trip.status, l10n),
                  kind: tripStatusKind(trip.status),
                ),
              ],
            ),
            const SizedBox(height: 12),
            FwLoadBar(
              fraction: trip.capacity > 0 ? booked / trip.capacity : 0,
              // Nothing booked: the whole truck is free (ESC-15 wording).
              caption: booked > 0
                  ? l10n.listsTripBooked(
                      formatQuintals(booked),
                      formatQuintals(trip.capacity),
                    )
                  : l10n.tripRemainingCapacity(formatQuintals(trip.capacity)),
            ),
            if (canCancel) ...[
              const SizedBox(height: 12),
              Align(
                alignment: Alignment.centerRight,
                child: OutlinedButton(
                  onPressed: onCancel,
                  style: OutlinedButton.styleFrom(
                    foregroundColor: AppTheme.soilDark,
                    side: const BorderSide(color: Color(0xFFD6D3C8)),
                    minimumSize: const Size(0, 40),
                    padding: const EdgeInsets.symmetric(horizontal: 16),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(12),
                    ),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  child: Text(l10n.cancelTrip),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
