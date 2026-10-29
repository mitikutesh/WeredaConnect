import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Reusable marketplace listing card matching Fieldway canvas design (Market.dc.html).
///
/// Used by both crop listing and trip listing screens.
/// Shows crop name, location, seller, price, an optional escrow badge and the
/// seller's rating. [rating] is the seller's real aggregate (see
/// `GET /ratings?rateeId=`); pass null to show nothing, never a placeholder.
class ListingCard extends StatelessWidget {
  final String title;
  final String location;
  final String sellerName;
  final String price;
  final bool hasEscrow;
  final double? rating;

  /// Number of ratings behind [rating]; rendered as "4.6 (12)" when set.
  final int? ratingCount;
  final IconData? icon;
  final VoidCallback? onTap;

  /// An extra muted line under the seller (My Crops: how much is ordered).
  final String? detail;

  /// Replaces the "seller, location" line (My Crops: "Meki · listed 28 Oct",
  /// the seller being the farmer looking).
  final String? subtitle;

  const ListingCard({
    required this.title,
    required this.location,
    required this.sellerName,
    required this.price,
    super.key,
    this.hasEscrow = false,
    this.rating,
    this.ratingCount,
    this.icon,
    this.onTap,
    this.detail,
    this.subtitle,
  });

  @override
  Widget build(BuildContext context) {
    final isWheat = title.toLowerCase().contains('wheat') ||
        title.toLowerCase().contains('ስንዴ');
    final iconBg = isWheat ? const Color(0xFFF4EBCF) : const Color(0xFFE3EEE6);
    final iconFg = isWheat ? const Color(0xFF8A5410) : AppTheme.fieldGreen;

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(18),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(18),
            border: Border.all(color: AppTheme.whisperBorder),
          ),
          child: Row(
            children: [
              // Crop icon badge
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: iconBg,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Icon(
                  icon ?? Icons.eco,
                  color: iconFg,
                  size: 24,
                ),
              ),
              const SizedBox(width: 12),
              // Details
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      subtitle ??
                          (location.isNotEmpty
                              ? '$sellerName, $location'
                              : sellerName),
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.mutedSteel,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    if (detail != null) ...[
                      const SizedBox(height: 2),
                      Text(
                        detail!,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.unselectedNav,
                        ),
                      ),
                    ],
                    const SizedBox(height: 4),
                    Row(
                      children: [
                        if (hasEscrow) ...[
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 6,
                              vertical: 2,
                            ),
                            decoration: BoxDecoration(
                              color: const Color(0xFFE3EEE6),
                              borderRadius: BorderRadius.circular(6),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                const Icon(
                                  Icons.verified_user_outlined,
                                  size: 11,
                                  color: AppTheme.fieldGreen,
                                ),
                                const SizedBox(width: 3),
                                Text(
                                  AppLocalizations.of(context)!.escrow,
                                  style: const TextStyle(
                                    fontSize: 11,
                                    fontWeight: FontWeight.w700,
                                    color: AppTheme.fieldGreen,
                                  ),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(width: 8),
                        ],
                        if (rating != null) ...[
                          const Icon(
                            Icons.star,
                            size: 13,
                            color: Color(0xFFD9922B),
                          ),
                          const SizedBox(width: 3),
                          Text(
                            ratingCount == null
                                ? rating!.toStringAsFixed(1)
                                : '${rating!.toStringAsFixed(1)} ($ratingCount)',
                            style: const TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w600,
                              color: AppTheme.mutedSteel,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              // Price
              Text(
                price,
                style: const TextStyle(
                  fontSize: 16,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
