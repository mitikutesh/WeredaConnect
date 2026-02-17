import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Card displaying a single crop listing.
///
/// Shows crop icon, name, quantity, price, and date.
/// Designed for ListView.builder to be memory-efficient on 2GB devices.
class CropListingCard extends StatelessWidget {
  final CropListing listing;
  final VoidCallback? onTap;
  final VoidCallback? onDelete;

  const CropListingCard({
    required this.listing,
    super.key,
    this.onTap,
    this.onDelete,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final cropType = CropType.fromKey(listing.cropType);
    final dateFormat = DateFormat.yMMMd();

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              // Crop icon
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: theme.colorScheme.primaryContainer,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Icon(
                  cropType.icon,
                  color: theme.colorScheme.onPrimaryContainer,
                  size: 24,
                ),
              ),
              const SizedBox(width: 16),
              // Details
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      '${cropType.displayName(l10n)} - ${listing.quantity} ${l10n.quintals}',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.bold,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '${listing.pricePerKg.toStringAsFixed(2)} ${l10n.etbPerKg}',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: theme.colorScheme.primary,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      dateFormat.format(listing.createdAt),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
              ),
              // Status chip
              _StatusChip(status: listing.status),
              if (onDelete != null) ...[
                const SizedBox(width: 8),
                IconButton(
                  icon: const Icon(Icons.delete_outline),
                  onPressed: onDelete,
                  iconSize: 20,
                  color: Colors.red.shade400,
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  final ListingStatus status;

  const _StatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (color, label) = switch (status) {
      ListingStatus.active => (Colors.green, l10n.listingStatusActive),
      ListingStatus.draft => (Colors.grey, l10n.listingStatusDraft),
      ListingStatus.sold => (Colors.blue, l10n.listingStatusSold),
      ListingStatus.expired => (Colors.red, l10n.listingStatusExpired),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: color,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}
