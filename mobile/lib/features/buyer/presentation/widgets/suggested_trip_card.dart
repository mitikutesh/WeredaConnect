import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// A suggested trip on the crop detail screen, as the Buyer · Crop detail
/// board draws it: a radio, the trucker and the day, the route with the
/// room left and the trucker's rating when they have one, and the price per
/// quintal on the right. Tapping anywhere on the card picks (or unpicks) it;
/// the chosen one gets the dark outline.
class SuggestedTripCard extends StatelessWidget {
  final Trip trip;
  final bool isSelected;
  final VoidCallback onSelect;

  /// The trucker's average rating, ending the route line as " · 4.7"; null
  /// (nothing shown, no placeholder) when they have none or it is unknown.
  final double? rating;

  const SuggestedTripCard({
    required this.trip,
    required this.isSelected,
    required this.onSelect,
    this.rating,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = boardDayInEthiopia(trip.departureDate);
    final room = l10n.tripRemainingCapacity(formatQuintals(tripRoomLeft(trip)));
    final rated = rating == null ? '' : ' · ${rating!.toStringAsFixed(1)}';

    return Semantics(
      button: true,
      selected: isSelected,
      child: Material(
        color: AppTheme.surfaceWhite,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: isSelected
              ? const BorderSide(color: AppTheme.soilDark, width: 2)
              : const BorderSide(color: AppTheme.whisperBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onSelect,
          child: Padding(
            padding: EdgeInsets.all(isSelected ? 14 : 15),
            child: Row(
              children: [
                _Radio(selected: isSelected),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${trip.truckerName} · $day',
                        style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.soilDark,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                      const SizedBox(height: 2),
                      Text(
                        '${trip.fromWoreda} → ${trip.toWoreda} · $room$rated',
                        style: const TextStyle(
                          fontSize: 12,
                          color: AppTheme.mutedSteel,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Text(
                      trip.pricePerQuintal.toStringAsFixed(0),
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    Text(
                      l10n.detailEtbPerQuintalShort,
                      style: const TextStyle(
                        fontSize: 11,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The board's radio: a 20 px ring, filled dark when picked.
class _Radio extends StatelessWidget {
  final bool selected;

  const _Radio({required this.selected});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 20,
      height: 20,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        border: Border.all(
          color: selected ? AppTheme.soilDark : AppTheme.mutedSteel,
          width: 2,
        ),
      ),
      child: selected
          ? Center(
              child: Container(
                width: 10,
                height: 10,
                decoration: const BoxDecoration(
                  shape: BoxShape.circle,
                  color: AppTheme.soilDark,
                ),
              ),
            )
          : null,
    );
  }
}
