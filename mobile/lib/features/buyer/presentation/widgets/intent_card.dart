import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/buyer/domain/new_matches.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// A buyer's request on Find Crops, My Requests, in the Market board's card
/// language (design canvas page M): raised on paper, the crop tile, "Wheat ·
/// 2,000 kg" with the quintals after it small, the budget and the place,
/// then the match count and the new matches as pills. When [onDelete] /
/// [onFulfill] are provided, a trailing menu exposes those actions so stale
/// or satisfied requests don't keep generating badges and alerts forever.
class IntentCard extends StatelessWidget {
  final BuyerIntent intent;
  final VoidCallback onTap;
  final VoidCallback? onDelete;
  final VoidCallback? onFulfill;

  const IntentCard({
    required this.intent,
    required this.onTap,
    this.onDelete,
    this.onFulfill,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(intent.cropType);
    final newCount = newMatchCount(intent);
    final hasNew = newCount > 0;

    final kg = intent.quantityNeeded * kgPerQuintal;
    final quintals = quintalsInParensText(kg, l10n);

    return FwCard(
      raised: true,
      onTap: onTap,
      padding: const EdgeInsets.fromLTRB(16, 14, 6, 14),
      child: Row(
        children: [
          // The boards' crop tile: the sprout in the crop's colours.
          Container(
            width: 48,
            height: 48,
            decoration: BoxDecoration(
              color: cropTileColors(intent.cropType).background,
              borderRadius: BorderRadius.circular(14),
            ),
            child: FwSprout(
              color: cropTileColors(intent.cropType).foreground,
            ),
          ),
          const SizedBox(width: 12),
          // Intent summary
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // "Wheat · 2,000 kg" with "(20 quintals)" after, small.
                Text.rich(
                  TextSpan(
                    text: '${cropType.displayName(l10n)} · '
                        '${kgQuantityText(kg, l10n)}',
                    children: [
                      if (quintals.isNotEmpty)
                        TextSpan(
                          text: ' ${quintals.replaceAll(' ', '\u00A0')}',
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w500,
                            letterSpacing: 0,
                            color: AppTheme.slate,
                          ),
                        ),
                    ],
                  ),
                  style: FwText.cardTitle,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: 2),
                Text(
                  // 0 budget = optional max price left empty.
                  '${intent.maxPricePerKg > 0 ? '≤${intent.maxPricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg}' : l10n.anyPrice} → ${intent.deliveryWoreda}',
                  style: FwText.body,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: 8),
                // The matches as the Market's pills: how many, and how
                // many are new since the buyer last looked.
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    if (intent.matchCount > 0) ...[
                      FwPill(label: l10n.matchesFound(intent.matchCount)),
                      if (hasNew)
                        FwPill(
                          label: l10n.newMatchesCount(newCount),
                          background: AppTheme.orangeTint,
                          foreground: AppTheme.orangeInk,
                        ),
                    ] else
                      FwPill(
                        label: l10n.noMatchesYet,
                        background: AppTheme.sand,
                        foreground: AppTheme.inkSoft,
                      ),
                  ],
                ),
              ],
            ),
          ),
          if (onDelete != null || onFulfill != null)
            PopupMenuButton<_IntentAction>(
              icon: const Icon(
                Icons.more_vert,
                size: 20,
                color: AppTheme.slate,
              ),
              padding: EdgeInsets.zero,
              onSelected: (action) => switch (action) {
                _IntentAction.fulfill => onFulfill?.call(),
                _IntentAction.delete => onDelete?.call(),
              },
              itemBuilder: (context) => [
                if (onFulfill != null)
                  PopupMenuItem(
                    value: _IntentAction.fulfill,
                    child: Row(
                      children: [
                        const Icon(Icons.check_circle_outline, size: 18),
                        const SizedBox(width: 8),
                        Text(l10n.markFulfilled),
                      ],
                    ),
                  ),
                if (onDelete != null)
                  PopupMenuItem(
                    value: _IntentAction.delete,
                    child: Row(
                      children: [
                        Icon(
                          Icons.delete_outline,
                          size: 18,
                          color: Theme.of(context).colorScheme.error,
                        ),
                        const SizedBox(width: 8),
                        Text(
                          l10n.deleteRequest,
                          style: TextStyle(
                            color: Theme.of(context).colorScheme.error,
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
        ],
      ),
    );
  }
}

enum _IntentAction { fulfill, delete }
