import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// ESC-15: who carries the order and when: the trucker, the route, the
/// departure day (Ethiopia) and the room left. Shown where a buyer picks or
/// confirms a truck, so the trucker the order will ask is never a surprise.
///
/// Styled like the boards' suggested transport rows: a truck tile on the
/// left, the trucker as the title, route and day under it, price and room
/// as the third line.
class TripSummaryTile extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onTap;

  const TripSummaryTile({required this.trip, super.key, this.onTap});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = boardDayInEthiopia(trip.departureDate);
    final price = l10n.tripPricePerQuintalValue(
      formatEtbAmount(trip.pricePerQuintal),
    );
    final room = l10n.tripRemainingCapacity(
      formatQuintals(tripRoomLeft(trip)),
    );
    return FwCard(
      onTap: onTap,
      padding: const EdgeInsets.all(14),
      child: Row(
        children: [
          Container(
            width: 46,
            height: 46,
            decoration: BoxDecoration(
              color: AppTheme.actionCream,
              borderRadius: BorderRadius.circular(13),
            ),
            child: const Icon(
              Icons.local_shipping_outlined,
              size: 22,
              color: AppTheme.money,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${l10n.trucker}: ${partyDisplayName(trip.truckerName, l10n)}',
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: 3),
                Text(
                  '${trip.fromWoreda} → ${trip.toWoreda} · $day',
                  style: FwText.body,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: 2),
                Text(
                  '$price · $room',
                  style: FwText.body,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          if (onTap != null) ...[
            const SizedBox(width: 8),
            const Icon(Icons.chevron_right, color: AppTheme.mutedSteel),
          ],
        ],
      ),
    );
  }
}

/// Lets the buyer pick one of [trips] for an order. Returns the chosen trip,
/// or null when the sheet was dismissed.
Future<Trip?> showChooseTripSheet(BuildContext context, List<Trip> trips) {
  final l10n = AppLocalizations.of(context)!;
  return showModalBottomSheet<Trip>(
    context: context,
    isScrollControlled: true,
    backgroundColor: AppTheme.chalkBackground,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
    ),
    builder: (sheetContext) => SafeArea(
      child: ConstrainedBox(
        constraints: BoxConstraints(
          maxHeight: MediaQuery.of(sheetContext).size.height * 0.7,
        ),
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.fromLTRB(20, 20, 20, 16),
          children: [
            Text(l10n.chooseTrip, style: FwText.cardTitle),
            const SizedBox(height: 12),
            for (final trip in trips) ...[
              TripSummaryTile(
                trip: trip,
                onTap: () => Navigator.of(sheetContext).pop(trip),
              ),
              const SizedBox(height: 10),
            ],
          ],
        ),
      ),
    ),
  );
}
