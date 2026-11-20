import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/reputation/domain/owner_reputation.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/low_reputation_pill.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// One trip in a list, in the language of the My Crops board (MyCropsM):
/// the truck tile, the route as the title with the departure day under it,
/// the price per 100 kg (one quintal) in the role's colour over the status
/// pill, then the load bar in the role's colour with the booked load under
/// it, kilograms first and the quintals after in parentheses.
///
/// [lead] (the first card of a list) draws the raised card with the black
/// tile, as the board leads with one.
///
/// [onCancel] (the trucker's own open or matched trips only) adds a plain
/// Cancel Trip action inside the card.
///
/// [departureDayPassed] (the trucker's own list, L-7) marks an open or
/// matched trip whose departure day is over: the pill reads "Date passed"
/// instead of Open, as buyers no longer see it.
///
/// [showTrucker] (the buyer's browse of every trucker's trips, SIM-33)
/// names the trucker under the route. The trucker's own list leaves it out,
/// as My Crops leaves out the farmer's own name.
///
/// ESC-31: with [showTrucker], a trip that carries the trucker's public
/// reputation copy also shows their rating beside the name and the "Low
/// reputation" label. Read from the row: no call per card. A trip without
/// the copy, and the trucker's own list, look as before.
class TripCard extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onTap;
  final VoidCallback? onCancel;
  final bool departureDayPassed;
  final bool showTrucker;
  final bool lead;

  const TripCard({
    required this.trip,
    super.key,
    this.onTap,
    this.onCancel,
    this.departureDayPassed = false,
    this.showTrucker = false,
    this.lead = false,
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
    final booked = bookedQuintals(trip);
    final beforePickup =
        trip.status == TripStatus.open || trip.status == TripStatus.matched;
    final canCancel = onCancel != null && beforePickup;
    final datePassed = departureDayPassed && beforePickup;
    final trucker =
        showTrucker ? partyDisplayName(trip.truckerName, l10n).trim() : '';
    final reputation =
        showTrucker ? OwnerReputation.ofTrip(trip) : OwnerReputation.none;
    final rating = reputation.rating;

    final capacityKg = trip.capacity * kgPerQuintal;
    final bookedKg = booked * kgPerQuintal;
    final statusLabel = datePassed
        ? l10n.tripStatusDatePassed
        : tripStatusLabel(trip.status, l10n);

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 6),
      child: FwCard(
        onTap: onTap,
        raised: lead,
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // The My Crops board's tile: black with the orange glyph on
                // the card the list leads with, sand on the rest.
                Container(
                  width: 46,
                  height: 46,
                  decoration: BoxDecoration(
                    color: lead ? AppTheme.ink : AppTheme.sand,
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: Icon(
                    Icons.local_shipping_outlined,
                    size: 20,
                    color: lead ? AppTheme.orange : AppTheme.ink,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${trip.fromWoreda} → ${trip.toWoreda}',
                        style: const TextStyle(
                          fontSize: 19,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.4,
                          height: 1.15,
                          color: AppTheme.ink,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                      if (trucker.isNotEmpty) ...[
                        const SizedBox(height: 3),
                        Row(
                          children: [
                            Flexible(
                              child: Text(
                                '${l10n.trucker}: $trucker',
                                style: const TextStyle(
                                  fontSize: 13,
                                  fontWeight: FontWeight.w700,
                                  color: AppTheme.ink,
                                ),
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                            if (rating != null) ...[
                              const Text(' · ', style: FwText.body),
                              const Icon(
                                Icons.star,
                                size: 13,
                                color: AppTheme.orange,
                              ),
                              const SizedBox(width: 2),
                              Text(
                                '${rating.toStringAsFixed(1)} '
                                '(${reputation.count})',
                                style: FwText.body,
                              ),
                            ],
                          ],
                        ),
                      ],
                      if (reputation.isLow) ...[
                        const SizedBox(height: 6),
                        const LowReputationPill(),
                      ],
                      const SizedBox(height: 3),
                      // Two lines: the Ethiopian date in Amharic and Afaan
                      // Oromoo (FUP-545) must not be cut.
                      Text(
                        day,
                        style: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.slate,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                // The price in the role's colour over the status pill.
                ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 120),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text.rich(
                        TextSpan(
                          text: formatEtbAmount(trip.pricePerQuintal),
                          children: [
                            TextSpan(
                              text: ' ${l10n.etbPer100KgShort}',
                              style: const TextStyle(
                                fontSize: 13,
                                fontWeight: FontWeight.w500,
                                letterSpacing: 0,
                                color: AppTheme.slate,
                              ),
                            ),
                          ],
                        ),
                        textAlign: TextAlign.end,
                        style: TextStyle(
                          fontSize: 17,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.2,
                          color: context.roleColors.primary,
                        ),
                      ),
                      const SizedBox(height: 4),
                      // Open is the plain state: the role's tint pill, as
                      // "Active" on My Crops. Any other state keeps its
                      // own colour and icon.
                      if (trip.status == TripStatus.open && !datePassed)
                        FwPill(label: statusLabel)
                      else
                        StatusChip(
                          label: statusLabel,
                          kind: datePassed
                              ? StatusKind.warning
                              : tripStatusKind(trip.status),
                        ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 14),
            ClipRRect(
              borderRadius: BorderRadius.circular(999),
              child: LinearProgressIndicator(
                value: trip.capacity > 0
                    ? (booked / trip.capacity).clamp(0.0, 1.0)
                    : 0,
                minHeight: 4,
                backgroundColor: AppTheme.track,
                valueColor: AlwaysStoppedAnimation<Color>(
                  context.roleColors.primary,
                ),
              ),
            ),
            const SizedBox(height: 8),
            // Kilograms lead, the quintals follow in parentheses, muted.
            // Nothing booked: the whole truck is free (ESC-15 wording).
            Text.rich(
              TextSpan(
                text: booked > 0
                    ? l10n.tripKgBooked(
                        wholeNumber(bookedKg.round()),
                        wholeNumber(capacityKg.round()),
                      )
                    : l10n.tripKgFree(wholeNumber(capacityKg.round())),
                children: [
                  TextSpan(
                    text: booked > 0
                        ? ' (${l10n.detailBookedOf(
                            formatQuintals(booked),
                            formatQuintals(trip.capacity),
                          )})'
                        : ' ${quintalsInParensText(capacityKg, l10n)}',
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.slate,
                    ),
                  ),
                ],
              ),
              key: const Key('trip_card_load'),
              style: const TextStyle(fontSize: 14, color: AppTheme.ink),
            ),
            if (canCancel) ...[
              const SizedBox(height: 12),
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: OutlinedButton(
                  onPressed: onCancel,
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size(48, 44),
                    padding: const EdgeInsets.symmetric(horizontal: 18),
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
