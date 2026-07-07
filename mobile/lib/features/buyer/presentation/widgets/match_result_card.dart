import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Card showing a matching crop listing with trip info and estimated total.
///
/// Displays: crop type + location, farmer name, quantity + price,
/// why-this-matched chips, trip count badge, and estimated total cost.
///
/// The chips translate the opaque `matchScore` ranking into reasons a buyer
/// can act on ("similar crop", "within budget", "near you", …) — computed
/// from the same [MatchScorer] signals that produced the ranking.
///
/// The card itself opens the listing detail; [onOrder] renders a primary
/// Order button so the funnel's money moment is one tap from the match
/// (previously buyers had to find Place Order two screens deeper).
class MatchResultCard extends StatelessWidget {
  final CropMatchResult result;

  /// The intent this match was scored against — used to derive the
  /// why-matched chips (budget / quantity / proximity).
  final BuyerIntent intent;

  final VoidCallback onTap;
  final VoidCallback? onOrder;

  const MatchResultCard({
    required this.result,
    required this.intent,
    required this.onTap,
    this.onOrder,
    super.key,
  });

  /// Geo score above which a listing is labelled "near you": haversine within
  /// ~33 km of the delivery centroid, or the same/overlapping woreda name.
  static const double _nearYouGeoScore = 0.6;

  List<Widget> _reasonChips(AppLocalizations l10n) {
    const scorer = MatchScorer();
    final listing = result.listing;
    final chips = <Widget>[];

    if (result.isSubstitute) {
      chips.add(
        StatusChip(
          label: l10n.similarCrop,
          kind: StatusKind.info,
          icon: Icons.swap_horiz,
        ),
      );
    }

    // Budget chip only when the buyer actually set a budget (0 = no budget).
    if (intent.maxPricePerKg > 0) {
      if (listing.pricePerKg <= intent.maxPricePerKg) {
        chips.add(
          StatusChip(
            label: l10n.withinBudget,
            kind: StatusKind.success,
            icon: Icons.payments_outlined,
          ),
        );
      } else {
        // Candidates are capped at the +15% near-miss band, so anything over
        // budget here is "slightly" over by construction.
        chips.add(
          StatusChip(
            label: l10n.slightlyOverBudget,
            kind: StatusKind.warning,
            icon: Icons.payments_outlined,
          ),
        );
      }
    }

    if (intent.quantityNeeded > 0) {
      chips.add(
        listing.quantity >= intent.quantityNeeded
            ? StatusChip(
                label: l10n.coversFullQuantity,
                kind: StatusKind.success,
                icon: Icons.inventory_2_outlined,
              )
            : StatusChip(
                label: l10n.partialQuantity,
                kind: StatusKind.neutral,
                icon: Icons.inventory_2_outlined,
              ),
      );
    }

    if (scorer.geoScore(intent, listing) >= _nearYouGeoScore) {
      chips.add(
        StatusChip(
          label: l10n.nearYou,
          kind: StatusKind.info,
          icon: Icons.place_outlined,
        ),
      );
    }

    return chips;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(result.listing.cropType);
    final hasTrips = result.suggestedTrips.isNotEmpty;
    final reasonChips = _reasonChips(l10n);

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
              if (reasonChips.isNotEmpty) ...[
                const SizedBox(height: 8),
                // Why this matched — signals behind the ranking, as chips.
                Wrap(spacing: 6, runSpacing: 6, children: reasonChips),
              ],
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
              if (onOrder != null) ...[
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: onTap,
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size.fromHeight(40),
                        ),
                        child: Text(l10n.viewDetails),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton.icon(
                        onPressed: onOrder,
                        icon: const Icon(Icons.shopping_cart, size: 18),
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(40),
                        ),
                        label: Text(l10n.placeOrder),
                      ),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
