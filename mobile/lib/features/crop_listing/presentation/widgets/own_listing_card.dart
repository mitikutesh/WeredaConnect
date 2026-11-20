import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/kg_entry.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// A farmer's own listing on My Crops (the MyCrops board, design canvas
/// page M): a raised card with the crop tile, the crop over "Meki · listed
/// Mon", the price in the farmer's colour over the status pill, then the
/// thin bar of what is ordered and the quantity in kilograms with the
/// quintals after it, smaller and muted.
///
/// The listing is stored in quintals; the card converts for display only.
class OwnListingCard extends StatelessWidget {
  final String crop;
  final String subtitle;

  /// "42", with [priceUnit] ("ETB/kg") small beside it.
  final String price;
  final String priceUnit;

  /// "Active", or "Not sent yet" with [statusPending] (BUG-36: the listing
  /// has not reached the server, an upload mark on the orange tint).
  final String status;
  final bool statusPending;

  /// The first card of the list: its tile is black with the orange sprout.
  final bool lead;

  /// Replaces the crop tile (the listing's cover photo).
  final Widget? leading;

  /// The free stock and what paid orders took, in quintals as stored.
  final double freeQuintals;
  final double orderedQuintals;

  /// The farmer's own rating; nothing when null.
  final double? rating;
  final int? ratingCount;
  final VoidCallback? onTap;

  const OwnListingCard({
    required this.crop,
    required this.subtitle,
    required this.price,
    required this.priceUnit,
    required this.status,
    required this.freeQuintals,
    this.orderedQuintals = 0,
    this.statusPending = false,
    this.lead = false,
    this.leading,
    this.rating,
    this.ratingCount,
    this.onTap,
    super.key,
  });

  static const TextStyle _line = TextStyle(fontSize: 14, color: AppTheme.ink);
  static const TextStyle _quintals = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w500,
    color: AppTheme.slate,
  );

  /// "3,200 of 6,000 kg ordered (32 of 60 quintals)", or "800 kg (8
  /// quintals) · no orders yet": kilograms lead, quintals follow.
  InlineSpan _quantitySpan(AppLocalizations l10n) {
    final total = freeQuintals + orderedQuintals;
    if (orderedQuintals <= 0) {
      return TextSpan(
        children: [
          TextSpan(
            text: l10n.quantityKg(kgNumberText(freeQuintals * kgPerQuintal)),
          ),
          TextSpan(
            text: ' ${l10n.quintalsInParens(formatQuintals(freeQuintals))}',
            style: _quintals,
          ),
          TextSpan(text: ' · ${l10n.myCropsNoOrdersShort}'),
        ],
      );
    }
    return TextSpan(
      children: [
        TextSpan(
          text: l10n.myCropsOrderedOfKg(
            kgNumberText(orderedQuintals * kgPerQuintal),
            kgNumberText(total * kgPerQuintal),
          ),
        ),
        TextSpan(
          text: ' ${l10n.quintalsOfInParens(
            formatQuintals(orderedQuintals),
            formatQuintals(total),
          )}',
          style: _quintals,
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final role = context.roleColors;
    final total = freeQuintals + orderedQuintals;
    final share = total <= 0 ? 0.0 : (orderedQuintals / total).clamp(0.0, 1.0);

    return FwCard(
      raised: true,
      onTap: onTap,
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              leading ??
                  Container(
                    width: 46,
                    height: 46,
                    decoration: BoxDecoration(
                      color: lead ? AppTheme.ink : AppTheme.sand,
                      borderRadius: BorderRadius.circular(14),
                    ),
                    child: FwSprout(
                      color: lead ? AppTheme.orange : AppTheme.ink,
                      size: 20,
                    ),
                  ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      crop,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: 19,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.4,
                        height: 1.2,
                        color: AppTheme.ink,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text.rich(
                      TextSpan(
                        children: [
                          TextSpan(text: subtitle),
                          if (rating != null) ...[
                            const TextSpan(text: ' · '),
                            const WidgetSpan(
                              alignment: PlaceholderAlignment.middle,
                              child: Icon(
                                Icons.star,
                                size: 14,
                                color: AppTheme.orange,
                              ),
                            ),
                            TextSpan(
                              text: ratingCount == null
                                  ? ' ${rating!.toStringAsFixed(1)}'
                                  : ' ${rating!.toStringAsFixed(1)} '
                                      '($ratingCount)',
                            ),
                          ],
                        ],
                      ),
                      style: const TextStyle(
                        fontSize: 14,
                        color: AppTheme.slate,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text.rich(
                    TextSpan(
                      text: price,
                      children: [
                        TextSpan(
                          text: ' $priceUnit',
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w500,
                            letterSpacing: 0,
                            color: AppTheme.slate,
                          ),
                        ),
                      ],
                    ),
                    style: TextStyle(
                      fontSize: 17,
                      fontWeight: FontWeight.w800,
                      letterSpacing: -0.17,
                      color: role.primary,
                    ),
                  ),
                  const SizedBox(height: 4),
                  ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 130),
                    child: statusPending
                        ? Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              // Kept as its own keyed mark: the listing is
                              // on this phone only (BUG-36).
                              const Icon(
                                Icons.cloud_upload_outlined,
                                key: Key('listing_card_pending_icon'),
                                size: 14,
                                color: AppTheme.orangeInk,
                              ),
                              const SizedBox(width: 4),
                              Flexible(
                                child: FwPill(
                                  label: status,
                                  background: AppTheme.orangeTint,
                                  foreground: AppTheme.orangeInk,
                                ),
                              ),
                            ],
                          )
                        : FwPill(label: status),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 14),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: share,
              minHeight: 4,
              backgroundColor: AppTheme.track,
              valueColor: AlwaysStoppedAnimation<Color>(role.primary),
            ),
          ),
          const SizedBox(height: 8),
          Text.rich(_quantitySpan(l10n), style: _line),
        ],
      ),
    );
  }
}

/// My Crops with no listing at all (the FarmerEmptyCrops board): the large
/// farmer sketch, a title, a line of explanation, the orange action that
/// opens Create listing and, where there is a Home to go back to, the
/// outlined way back. The header draws no sketch then (one per screen).
class OwnCropsEmptyState extends StatelessWidget {
  final VoidCallback onListCrop;

  /// Null where the screen has no way back to Home of its own (the phone's
  /// tab, where Home is one tap away in the bar).
  final VoidCallback? onBackToHome;

  const OwnCropsEmptyState({
    required this.onListCrop,
    this.onBackToHome,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final onBackToHome = this.onBackToHome;
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(28, 40, 28, 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const Align(
            alignment: AlignmentDirectional.centerStart,
            child: RoleSketch.emptyState(role: UserRole.farmer),
          ),
          const SizedBox(height: 28),
          Semantics(
            header: true,
            child: Text(
              l10n.myCropsEmptyTitle,
              style: const TextStyle(
                fontSize: 24,
                fontWeight: FontWeight.w800,
                letterSpacing: -0.5,
                height: 1.15,
                color: AppTheme.ink,
              ),
            ),
          ),
          const SizedBox(height: 10),
          Text(
            l10n.myCropsEmptyBody,
            style: const TextStyle(
              fontSize: 15,
              height: 1.45,
              color: AppTheme.slate,
            ),
          ),
          const SizedBox(height: 28),
          FilledButton.icon(
            key: const Key('my_crops_empty_list_crop'),
            onPressed: onListCrop,
            icon: const Icon(Icons.add, size: 18),
            label: Text(l10n.myCropsEmptyAction),
          ),
          if (onBackToHome != null) ...[
            const SizedBox(height: 12),
            OutlinedButton(
              key: const Key('my_crops_empty_back_home'),
              onPressed: onBackToHome,
              child: Text(l10n.backToHome),
            ),
          ],
        ],
      ),
    );
  }
}
