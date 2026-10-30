import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Card showing a matching crop listing with trip info and estimated total.
///
/// The Market board's listing card (crop tile, "Teff · 50 quintals left",
/// farmer and place, the price large, the transport pill), then the
/// why-this-matched chips, the estimated total and the order actions.
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
    final listing = result.listing;
    final cropType = CropType.fromKey(listing.cropType);
    final hasTrips = result.suggestedTrips.isNotEmpty;
    final reasonChips = _reasonChips(l10n);

    // The Market listing card ("Teff · 50 quintals left", the farmer and the
    // place, the price large, the transport pill), with why it matched, the
    // estimate and the order actions under it.
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: ListingCard(
        title: '${cropType.displayName(l10n)} · '
            '${l10n.marketQuintalsLeft(formatQuintals(listing.quantity))}',
        location: listing.locationName,
        sellerName: listing.farmerName,
        price: wholeNumber(listing.pricePerKg.round()),
        priceUnit: l10n.etbPerKg,
        cropKey: listing.cropType,
        icon: cropType.icon,
        onTap: onTap,
        detail: hasTrips
            ? l10n.tripsAvailable(result.suggestedTrips.length)
            : l10n.noTripsYet,
        detailAsPill: true,
        detailHighlighted: hasTrips,
        footer: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (reasonChips.isNotEmpty) ...[
              // Why this matched: the signals behind the ranking.
              Wrap(spacing: 6, runSpacing: 6, children: reasonChips),
              const SizedBox(height: 10),
            ],
            FwMoneyRow(
              label: l10n.estimatedTotal,
              amount: l10n.etbCurrency(
                wholeNumber(result.estimatedTotalMin.round()),
              ),
            ),
            if (onOrder != null) ...[
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      onPressed: onTap,
                      style: OutlinedButton.styleFrom(
                        minimumSize: const Size.fromHeight(44),
                      ),
                      child: Text(
                        l10n.viewDetails,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: FilledButton(
                      onPressed: onOrder,
                      style: FilledButton.styleFrom(
                        minimumSize: const Size.fromHeight(44),
                      ),
                      child: Text(
                        l10n.placeOrder,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}
