import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// ESC-15: who carries the order and when: the trucker, the route, the
/// departure day (Ethiopia) and the room left. Shown where a buyer picks or
/// confirms a truck, so the trucker the order will ask is never a surprise.
class TripSummaryTile extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onTap;

  const TripSummaryTile({required this.trip, super.key, this.onTap});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final day = DateFormat.yMMMd().format(dayInEthiopia(trip.departureDate));
    return ListTile(
      onTap: onTap,
      contentPadding: EdgeInsets.zero,
      leading: const Icon(Icons.local_shipping, color: AppTheme.primaryGreen),
      title: Text(
        '${l10n.trucker}: ${partyDisplayName(trip.truckerName, l10n)}',
        style:
            theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600),
      ),
      subtitle: Text(
        '${trip.fromWoreda} → ${trip.toWoreda} · $day\n'
        '${l10n.etbCurrency(trip.pricePerQuintal.toStringAsFixed(0))}/'
        '${l10n.quintals} · '
        '${l10n.tripRemainingCapacity(formatQuintals(tripRoomLeft(trip)))}',
      ),
      isThreeLine: true,
      trailing: onTap == null ? null : const Icon(Icons.chevron_right),
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
          padding: const EdgeInsets.fromLTRB(24, 20, 24, 16),
          children: [
            Text(
              l10n.chooseTrip,
              style: Theme.of(sheetContext)
                  .textTheme
                  .titleMedium
                  ?.copyWith(fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 8),
            for (final trip in trips) ...[
              TripSummaryTile(
                trip: trip,
                onTap: () => Navigator.of(sheetContext).pop(trip),
              ),
              const Divider(height: 1),
            ],
          ],
        ),
      ),
    ),
  );
}
