import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/crop_glyph.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/low_reputation_pill.dart';

/// A crop listing card as the boards draw it (design canvas page M:
/// MarketM.dc.html and MyCropsM.dc.html): raised on paper, a crop tile, the
/// title with the quintals after it in small muted text, one muted line
/// with the seller and the rating, the price in the role colour, and either
/// the transport pill (Find Crops) or the ordered share bar (My Crops).
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

  /// Number of ratings behind [rating]; rendered as "4.6 (12 ratings)"
  /// when set.
  final int? ratingCount;

  /// After [title], smaller and muted: the quintals in parentheses
  /// ("(28 quintals)", page M's kg-first rule).
  final String? titleSuffix;

  /// Puts [priceUnit] under the price (Find Crops) instead of beside it
  /// (My Crops).
  final bool priceUnitBelow;

  /// ESC-31: shows the "Low reputation" label under the seller line (the
  /// seller's public tier is low). Off by default: no label, as before.
  final bool lowReputation;

  /// The crop's stored key ("wheat"), for the tile's colours
  /// ([cropTileColors]). Falls back to the title when null.
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

  /// BUG-36: [status] says the listing has not reached the server yet ("Not
  /// sent yet"): it shows in amber with an upload icon instead of green.
  final bool statusPending;

  /// Replaces the "seller, location" line (My Crops: "Meki · listed Mon",
  /// the seller being the farmer looking).
  final String? subtitle;

  /// Under the card's rows (Match results: why it matched, the estimate and
  /// the order actions).
  final Widget? footer;

  /// Replaces the crop tile at the start (the listing's cover photo,
  /// crop-listing-api.md 1.6.0). The tile stays when null.
  final Widget? leading;

  const ListingCard({
    required this.title,
    required this.location,
    required this.sellerName,
    required this.price,
    super.key,
    this.hasEscrow = false,
    this.rating,
    this.ratingCount,
    this.titleSuffix,
    this.priceUnitBelow = false,
    this.lowReputation = false,
    this.cropKey,
    this.onTap,
    this.detail,
    this.subtitle,
    this.detailAsPill = false,
    this.detailHighlighted = false,
    this.progress,
    this.priceUnit,
    this.status,
    this.statusPending = false,
    this.footer,
    this.leading,
  });

  @override
  Widget build(BuildContext context) {
    final isWheat = cropKey == null &&
        (title.toLowerCase().contains('wheat') ||
            title.toLowerCase().contains('ስንዴ'));
    final tile = cropTileColors(isWheat ? 'wheat' : cropKey);
    final iconBg = tile.background;
    final iconFg = tile.foreground;
    // "Seller, place", leaving out a part that is empty.
    final line = subtitle ??
        [sellerName, location].where((part) => part.isNotEmpty).join(', ');

    // Read only where a string is needed: a card without a rating count or
    // an escrow pill builds without the app's localizations.
    AppLocalizations l10n() => AppLocalizations.of(context)!;
    final titleSuffix = this.titleSuffix;
    final priceText = Text(
      price,
      style: TextStyle(
        fontSize: 19,
        fontWeight: FontWeight.w800,
        letterSpacing: -0.2,
        color: context.roleColors.primary,
      ),
    );
    final unitText = priceUnit == null
        ? null
        : Text(
            priceUnit!,
            style: const TextStyle(fontSize: 13, color: AppTheme.slate),
          );

    return FwCard(
      raised: true,
      onTap: onTap,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              leading ??
                  Container(
                    width: 48,
                    height: 48,
                    decoration: BoxDecoration(
                      color: iconBg,
                      borderRadius: BorderRadius.circular(14),
                    ),
                    child: CropGlyph(
                      cropKey: isWheat ? 'wheat' : cropKey,
                      color: iconFg,
                      size: 24,
                    ),
                  ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Three lines at most: a four-digit price can push
                    // "Teff · 10,000 kg left (100 quintals)" over on a phone.
                    Text.rich(
                      TextSpan(
                        text: title,
                        children: [
                          if (titleSuffix != null && titleSuffix.isNotEmpty)
                            TextSpan(
                              // No-break spaces: the parentheses wrap whole.
                              text: ' ${titleSuffix.replaceAll(' ', '\u00A0')}',
                              style: const TextStyle(
                                fontSize: 13,
                                fontWeight: FontWeight.w500,
                                letterSpacing: 0,
                                color: AppTheme.slate,
                              ),
                            ),
                        ],
                      ),
                      maxLines: 3,
                      overflow: TextOverflow.ellipsis,
                      style: FwText.cardTitle,
                    ),
                    const SizedBox(height: 3),
                    // "Almaz Bekele, Meki · 4.8 (12 ratings)": one line when
                    // it fits, two when the names are long.
                    Text.rich(
                      TextSpan(
                        text: line,
                        children: [
                          if (rating != null) ...[
                            const TextSpan(text: ' · '),
                            const WidgetSpan(
                              alignment: PlaceholderAlignment.middle,
                              child: Padding(
                                padding: EdgeInsetsDirectional.only(end: 2),
                                child: Icon(
                                  Icons.star,
                                  size: 13,
                                  color: AppTheme.orange,
                                ),
                              ),
                            ),
                            TextSpan(
                              // No-break spaces: the rating wraps whole.
                              text: (ratingCount == null
                                      ? rating!.toStringAsFixed(1)
                                      : '${rating!.toStringAsFixed(1)} '
                                          '(${l10n().ratingsCount(ratingCount!)})')
                                  .replaceAll(' ', '\u00A0'),
                            ),
                          ],
                        ],
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: FwText.body.copyWith(
                        fontSize: 14,
                        fontWeight: FontWeight.w400,
                      ),
                    ),
                    if (lowReputation) ...[
                      const SizedBox(height: 6),
                      const Align(
                        alignment: Alignment.centerLeft,
                        child: LowReputationPill(),
                      ),
                    ],
                    if (hasEscrow) ...[
                      const SizedBox(height: 6),
                      Align(
                        alignment: Alignment.centerLeft,
                        child: FwPill(
                          label: l10n().escrow,
                          icon: Icons.verified_user_outlined,
                          background: context.roleColors.tint,
                          foreground: context.roleColors.primary,
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
                              ? AppTheme.orangeTint
                              : AppTheme.sand,
                          foreground: detailHighlighted
                              ? AppTheme.orangeInk
                              : AppTheme.inkSoft,
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
                          color: AppTheme.inkSoft,
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
                  if (priceUnitBelow) ...[
                    priceText,
                    if (unitText != null) unitText,
                  ] else
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        priceText,
                        if (unitText != null) ...[
                          const SizedBox(width: 3),
                          unitText,
                        ],
                      ],
                    ),
                  if (status != null) ...[
                    const SizedBox(height: 4),
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (statusPending) ...[
                          const Icon(
                            Icons.cloud_upload_outlined,
                            key: Key('listing_card_pending_icon'),
                            size: 14,
                            color: AppTheme.statusWarning,
                          ),
                          const SizedBox(width: 4),
                        ],
                        Text(
                          status!,
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w700,
                            color: statusPending
                                ? AppTheme.statusWarning
                                : context.roleColors.primary,
                          ),
                        ),
                      ],
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
                      minHeight: 4,
                      backgroundColor: AppTheme.track,
                      valueColor: AlwaysStoppedAnimation<Color>(
                        context.roleColors.primary,
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
          if (footer != null) ...[
            const SizedBox(height: 12),
            footer!,
          ],
        ],
      ),
    );
  }
}
