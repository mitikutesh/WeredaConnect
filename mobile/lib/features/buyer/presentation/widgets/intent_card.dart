import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Compact card for displaying a buyer intent on the home screen.
///
/// Shows crop icon, summary (crop, quantity, budget, destination),
/// match count, and a green dot badge for new matches.
class IntentCard extends StatelessWidget {
  final BuyerIntent intent;
  final VoidCallback onTap;

  const IntentCard({
    required this.intent,
    required this.onTap,
    super.key,
  });

  bool get _hasNewMatches {
    if (intent.matchCount == 0) return false;
    if (intent.lastViewedAt == null) return intent.matchCount > 0;
    if (intent.lastMatchedAt == null) return false;
    return intent.lastMatchedAt!.isAfter(intent.lastViewedAt!);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(intent.cropType);

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
                      '≤${intent.maxPricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg} → ${intent.deliveryWoreda}',
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
              // Match count + badge
              Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (intent.matchCount > 0)
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (_hasNewMatches)
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
                            color: _hasNewMatches
                                ? AppTheme.statusSuccess
                                : AppTheme.textMuted,
                          ),
                        ),
                      ],
                    )
                  else
                    Text(
                      l10n.noMatchesYet,
                      style: const TextStyle(
                        fontSize: 11,
                        color: AppTheme.textMuted,
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
