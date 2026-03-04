import 'package:flutter/material.dart';

import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Card showing a matching crop listing with trip info and estimated total.
///
/// Displays: crop type + location, farmer name, quantity + price,
/// trip count badge, and estimated total cost.
class MatchResultCard extends StatelessWidget {
  final CropMatchResult result;
  final VoidCallback onTap;

  const MatchResultCard({
    required this.result,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(result.listing.cropType);
    final hasTrips = result.suggestedTrips.isNotEmpty;

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Top row: crop info + location
              Row(
                children: [
                  Icon(cropType.icon, size: 24, color: AppTheme.primaryGreen),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      '${cropType.displayName(l10n)} · ${result.listing.locationName}',
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),

              // Quantity + price row
              Text(
                '${result.listing.quantity.toStringAsFixed(0)}q · ${result.listing.pricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg}',
                style: const TextStyle(
                  fontSize: 13,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 8),

              // Trip badge + estimated total row
              Row(
                children: [
                  // Trip count badge
                  Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                    decoration: BoxDecoration(
                      color: hasTrips
                          ? AppTheme.statusSuccess.withAlpha(20)
                          : AppTheme.textMuted.withAlpha(20),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          Icons.local_shipping,
                          size: 14,
                          color: hasTrips
                              ? AppTheme.statusSuccess
                              : AppTheme.textMuted,
                        ),
                        const SizedBox(width: 4),
                        Text(
                          hasTrips
                              ? l10n.tripsAvailable(
                                  result.suggestedTrips.length,
                                )
                              : l10n.noTripsYet,
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w500,
                            color: hasTrips
                                ? AppTheme.statusSuccess
                                : AppTheme.textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const Spacer(),
                  // Estimated total
                  Text(
                    '${l10n.estimatedTotal}: ${l10n.etbCurrency(result.estimatedTotalMin.toStringAsFixed(0))}',
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: AppTheme.primaryGreen,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
