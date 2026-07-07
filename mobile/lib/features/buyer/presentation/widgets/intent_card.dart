import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/new_matches.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Compact card for displaying a buyer intent on the home screen.
///
/// Shows crop icon, summary (crop, quantity, budget, destination),
/// match count, and a green dot badge for new matches. When [onDelete] /
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

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: AppTheme.borderLight),
          ),
          child: Row(
            children: [
              // Crop icon
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: AppTheme.primaryGreen.withAlpha(20),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(
                  cropType.icon,
                  size: 22,
                  color: AppTheme.primaryGreen,
                ),
              ),
              const SizedBox(width: 12),
              // Intent summary
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      '${cropType.displayName(l10n)} · ${intent.quantityNeeded.toStringAsFixed(0)} ${l10n.quintals}',
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
                      // 0 budget = optional max price left empty.
                      '${intent.maxPricePerKg > 0 ? '≤${intent.maxPricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg}' : l10n.anyPrice} → ${intent.deliveryWoreda}',
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.textMuted,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              // Match count + new-matches badge
              Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  if (intent.matchCount > 0) ...[
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (hasNew)
                          Container(
                            width: 8,
                            height: 8,
                            margin: const EdgeInsets.only(right: 4),
                            decoration: const BoxDecoration(
                              color: AppTheme.statusSuccess,
                              shape: BoxShape.circle,
                            ),
                          ),
                        Text(
                          l10n.matchesFound(intent.matchCount),
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w500,
                            color: hasNew
                                ? AppTheme.statusSuccess
                                : AppTheme.textMuted,
                          ),
                        ),
                      ],
                    ),
                    if (hasNew)
                      Padding(
                        padding: const EdgeInsets.only(top: 2),
                        child: Text(
                          l10n.newMatchesCount(newCount),
                          style: const TextStyle(
                            fontSize: 10,
                            fontWeight: FontWeight.w600,
                            color: AppTheme.statusSuccess,
                          ),
                        ),
                      ),
                  ] else
                    Text(
                      l10n.noMatchesYet,
                      style: const TextStyle(
                        fontSize: 11,
                        color: AppTheme.textMuted,
                      ),
                    ),
                ],
              ),
              if (onDelete != null || onFulfill != null)
                PopupMenuButton<_IntentAction>(
                  icon: const Icon(
                    Icons.more_vert,
                    size: 20,
                    color: AppTheme.textMuted,
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
        ),
      ),
    );
  }
}

enum _IntentAction { fulfill, delete }
