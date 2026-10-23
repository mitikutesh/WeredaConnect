import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Card showing a suggested trip for transport on the crop detail screen.
///
/// Displays trucker name, departure date, capacity, price per quintal,
/// and a select button.
class SuggestedTripCard extends StatelessWidget {
  final Trip trip;
  final bool isSelected;
  final VoidCallback onSelect;

  const SuggestedTripCard({
    required this.trip,
    required this.isSelected,
    required this.onSelect,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.MMMd();

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: isSelected
            ? const BorderSide(color: AppTheme.primaryGreen, width: 2)
            : BorderSide.none,
      ),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Row(
          children: [
            // Truck icon
            Container(
              width: 36,
              height: 36,
              decoration: BoxDecoration(
                color: AppTheme.statusTransit.withAlpha(20),
                borderRadius: BorderRadius.circular(8),
              ),
              child: const Icon(
                Icons.local_shipping,
                size: 20,
                color: AppTheme.statusTransit,
              ),
            ),
            const SizedBox(width: 12),
            // Trip details
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    '${trip.truckerName} · ${dateFormat.format(dayInEthiopia(trip.departureDate))}',
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w600,
                      color: AppTheme.textDark,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  const SizedBox(height: 2),
                  Text(
                    '${trip.capacity.toStringAsFixed(0)}q ${l10n.capacity} · ${trip.pricePerQuintal.toStringAsFixed(0)} ETB/q',
                    style: const TextStyle(
                      fontSize: 12,
                      color: AppTheme.textMuted,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 8),
            // Select button
            TextButton(
              onPressed: onSelect,
              style: TextButton.styleFrom(
                backgroundColor: isSelected
                    ? AppTheme.primaryGreen
                    : AppTheme.primaryGreen.withAlpha(15),
                foregroundColor:
                    isSelected ? Colors.white : AppTheme.primaryGreen,
                padding:
                    const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                minimumSize: Size.zero,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(8),
                ),
              ),
              child: Text(
                isSelected ? '✓' : l10n.selectThisTrip,
                style: const TextStyle(fontSize: 12),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
