import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// A crop listing card as the Fieldway boards draw it (Market.dc.html and
/// MyCrops.dc.html): a crop tile, the title, one muted line with the seller
/// and the rating, the price large with its unit beside it, and either the
/// transport pill (Find Crops) or the ordered share bar (My Crops).
///
/// [rating] is the seller's real aggregate (see `GET /ratings?rateeId=`);
/// pass null to show nothing, never a placeholder.
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

  /// The crop's stored key ("wheat"): wheat gets the cream tile, every other
  /// crop the green one. Falls back to the title when null.
  final String? cropKey;
  final VoidCallback? onTap;

  /// An extra muted line (My Crops: how much is ordered). Beside the bar
  /// when [progress] is set.
  final String? detail;

  /// Shows [detail] as a small pill (Find Crops: the transport line), amber
  /// when [detailHighlighted], else grey.
  final bool detailAsPill;
  final bool detailHighlighted;

  /// 0 to 1: the share already ordered (My Crops), as a bar under the card.
  final double? progress;

  /// Beside [price], small ("ETB/kg").
  final String? priceUnit;

  /// Under the price, in green (My Crops: "Active").
  final String? status;

  /// Replaces the "seller, location" line (My Crops: "Meki · listed Mon",
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
    this.cropKey,
    this.onTap,
    this.detail,
    this.subtitle,
    this.detailAsPill = false,
    this.detailHighlighted = false,
    this.progress,
    this.priceUnit,
    this.status,
  });

  @override
  Widget build(BuildContext context) {
    final isWheat = cropKey == null
        ? title.toLowerCase().contains('wheat') ||
            title.toLowerCase().contains('ስንዴ')
        : cropKey == 'wheat';
    final iconBg = isWheat ? const Color(0xFFF4EBCF) : AppTheme.fieldGreenTint;
    final iconFg = isWheat ? AppTheme.money : AppTheme.fieldGreen;
    final line = subtitle ??
        (location.isNotEmpty ? '$sellerName, $location' : sellerName);

    return FwCard(
      onTap: onTap,
      padding: const EdgeInsets.all(14),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: iconBg,
                  borderRadius: BorderRadius.circular(14),
                ),
                child:
                    Icon(icon ?? Icons.eco_outlined, color: iconFg, size: 24),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Two lines at most: a four-digit price can push "Teff ·
                    // 100 quintals left" over on a phone.
                    Text(
                      title,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: FwText.cardTitle,
                    ),
                    const SizedBox(height: 3),
                    // "Almaz Bekele, Meki · 4.8 (12)" on one line.
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            line,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: FwText.body,
                          ),
                        ),
                        if (rating != null) ...[
                          const Text(' · ', style: FwText.body),
                          const Icon(
                            Icons.star,
                            size: 13,
                            color: AppTheme.accentSoft,
                          ),
                          const SizedBox(width: 2),
                          Text(
                            ratingCount == null
                                ? rating!.toStringAsFixed(1)
                                : '${rating!.toStringAsFixed(1)} ($ratingCount)',
                            style: FwText.body,
                          ),
                        ],
                      ],
                    ),
                    if (hasEscrow) ...[
                      const SizedBox(height: 6),
                      Align(
                        alignment: Alignment.centerLeft,
                        child: FwPill(
                          label: AppLocalizations.of(context)!.escrow,
                          icon: Icons.verified_user_outlined,
                          background: AppTheme.fieldGreenTint,
                          foreground: AppTheme.fieldGreen,
                        ),
                      ),
                    ],
                    if (detail != null && detailAsPill) ...[
                      const SizedBox(height: 8),
                      Align(
                        alignment: Alignment.centerLeft,
                        child: FwPill(
                          label: detail!,
                          background: detailHighlighted
                              ? AppTheme.actionCream
                              : AppTheme.segmentTrack,
                          foreground: detailHighlighted
                              ? AppTheme.money
                              : AppTheme.unselectedNav,
                        ),
                      ),
                    ],
                    if (detail != null &&
                        !detailAsPill &&
                        progress == null) ...[
                      const SizedBox(height: 4),
                      Text(
                        detail!,
                        style: FwText.body.copyWith(
                          fontWeight: FontWeight.w600,
                          color: AppTheme.unselectedNav,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 10),
              // The price large, its unit beside it, the status under it.
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.baseline,
                    textBaseline: TextBaseline.alphabetic,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        price,
                        style: const TextStyle(
                          fontSize: 20,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.3,
                          color: AppTheme.soilDark,
                        ),
                      ),
                      if (priceUnit != null) ...[
                        const SizedBox(width: 3),
                        Text(
                          priceUnit!,
                          style: const TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w500,
                            color: AppTheme.mutedSteel,
                          ),
                        ),
                      ],
                    ],
                  ),
                  if (status != null) ...[
                    const SizedBox(height: 4),
                    Text(
                      status!,
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.fieldGreen,
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
          if (progress != null) ...[
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(999),
                    child: LinearProgressIndicator(
                      value: progress!.clamp(0, 1).toDouble(),
                      minHeight: 6,
                      backgroundColor: AppTheme.whisperBorder,
                      valueColor: const AlwaysStoppedAnimation<Color>(
                        AppTheme.fieldGreen,
                      ),
                    ),
                  ),
                ),
                if (detail != null) ...[
                  const SizedBox(width: 10),
                  Text(
                    detail!,
                    style: FwText.body.copyWith(fontSize: 12),
                  ),
                ],
              ],
            ),
          ],
        ],
      ),
    );
  }
}
