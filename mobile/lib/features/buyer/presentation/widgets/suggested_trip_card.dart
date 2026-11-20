import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/low_reputation_pill.dart';

/// A trip's transport price per kilogram for display ("1.50"), from the
/// stored price per quintal: page M leads with the price per kg and puts
/// the per-quintal price after it in parentheses.
String transportPricePerKgText(double pricePerQuintal) =>
    (pricePerQuintal / kgPerQuintal).toStringAsFixed(2);

/// A suggested trip on the crop detail screen, as the CropDetailM board
/// draws it: a radio, the trucker and the day, the route with the room left
/// kg first and the trucker's rating when they have one, and on the right
/// the price per kg in the role colour over "ETB/kg" and "(150/quintal)".
/// Tapping anywhere on the card picks (or unpicks) it; the chosen one is
/// the raised paper card, the others are outlined.
class SuggestedTripCard extends StatelessWidget {
  final Trip trip;
  final bool isSelected;
  final VoidCallback onSelect;

  /// The trucker's average rating, ending the route line as " · 4.7"; null
  /// (nothing shown, no placeholder) when they have none or it is unknown.
  final double? rating;

  /// ESC-31: shows the "Low reputation" label under the route line (the
  /// trucker's public tier is low). Off by default: no label, as before.
  final bool lowReputation;

  const SuggestedTripCard({
    required this.trip,
    required this.isSelected,
    required this.onSelect,
    this.rating,
    this.lowReputation = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = boardDayInEthiopia(trip.departureDate);
    final roomKg = tripRoomLeft(trip) * kgPerQuintal;
    final room = '${l10n.marketKgRoom(kgQuantityText(roomKg, l10n))} '
        '${quintalsInParensText(roomKg, l10n)}';
    final rated = rating == null ? '' : ' · ${rating!.toStringAsFixed(1)}';

    final radius = BorderRadius.circular(24);
    final card = Material(
      color: isSelected ? AppTheme.paper : Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: radius,
        side: BorderSide(
          color: isSelected ? AppTheme.line : AppTheme.fieldBorder,
        ),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onSelect,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 15),
          child: Row(
            children: [
              _Radio(selected: isSelected),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Two lines, so the day with its Ethiopian date
                    // (FUP-545) is not cut.
                    Text(
                      '${trip.truckerName} · $day',
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.15,
                        color: AppTheme.ink,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 3),
                    Text(
                      '${trip.fromWoreda} → ${trip.toWoreda} · $room$rated',
                      style: const TextStyle(
                        fontSize: 14,
                        color: AppTheme.slate,
                      ),
                      maxLines: 3,
                      overflow: TextOverflow.ellipsis,
                    ),
                    if (lowReputation) ...[
                      const SizedBox(height: 6),
                      const LowReputationPill(),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 12),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    transportPricePerKgText(trip.pricePerQuintal),
                    style: TextStyle(
                      fontSize: 17,
                      fontWeight: FontWeight.w800,
                      letterSpacing: -0.17,
                      color: context.roleColors.primary,
                    ),
                  ),
                  Text(l10n.etbPerKg, style: _unitStyle),
                  Text(
                    l10n.perQuintalInParens(
                      formatEtbAmount(trip.pricePerQuintal),
                    ),
                    style: _unitStyle,
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );

    return Semantics(
      button: true,
      selected: isSelected,
      child: isSelected
          ? DecoratedBox(
              decoration: BoxDecoration(
                borderRadius: radius,
                // The raised card's soft shadow ([FwCard.raised]).
                boxShadow: [
                  BoxShadow(
                    color: Theme.of(context).colorScheme.shadow,
                    blurRadius: 30,
                    offset: const Offset(0, 14),
                  ),
                ],
              ),
              child: card,
            )
          : card,
    );
  }

  static const TextStyle _unitStyle = TextStyle(
    fontSize: 13,
    color: AppTheme.slate,
  );
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
          color: selected ? AppTheme.ink : AppTheme.slate,
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
                  color: AppTheme.ink,
                ),
              ),
            )
          : null,
    );
  }
}
