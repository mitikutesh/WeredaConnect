import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intl/intl.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/create_delivery_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Full detail view for a single crop listing.
///
/// Displays crop info (type, quantity, price, location, description, date)
/// and role-based action buttons (trucker → Create Delivery, buyer → Contact
/// Farmer). Follows the same SafeArea + SingleChildScrollView layout used by
/// [DeliveryDetailScreen] and [CreateCropListingScreen].
class CropListingDetailScreen extends StatelessWidget {
  final CropListing listing;
  final String currentUserId;
  final UserRole currentUserRole;

  const CropListingDetailScreen({
    required this.listing,
    required this.currentUserId,
    required this.currentUserRole,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final cropType = CropType.fromKey(listing.cropType);
    final dateFormat = DateFormat.yMMMd().add_jm();

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.cropListings),
        elevation: 0,
      ),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Header section: crop icon + name + status chip
              _HeaderSection(
                cropType: cropType,
                status: listing.status,
              ),
              const SizedBox(height: 24),

              // Details card
              _DetailsCard(
                listing: listing,
                cropType: cropType,
                dateFormat: dateFormat,
              ),
              const SizedBox(height: 32),

              // Role-based action buttons
              ..._buildActionButtons(context, theme),
            ],
          ),
        ),
      ),
    );
  }

  List<Widget> _buildActionButtons(BuildContext context, ThemeData theme) {
    final widgets = <Widget>[];

    switch (currentUserRole) {
      case UserRole.farmer:
        // Owner sees no actions for now (editing is future work)
        break;

      case UserRole.trucker:
        final l10n = AppLocalizations.of(context)!;
        widgets.add(
          SizedBox(
            width: double.infinity,
            child: ElevatedButton.icon(
              onPressed: () {
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => CreateDeliveryScreen(listing: listing),
                  ),
                );
              },
              icon: const Icon(Icons.local_shipping),
              label: Text(l10n.createDelivery),
              style: ElevatedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
          ),
        );

      case UserRole.buyer:
        final l10n = AppLocalizations.of(context)!;
        // Primary: Place Order
        widgets.add(
          SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              onPressed: () async {
                final farmerName = await _lookupFarmerName(listing.farmerId);
                if (!context.mounted) return;
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => PlaceOrderScreen(
                      listing: listing,
                      farmerName: farmerName,
                    ),
                  ),
                );
              },
              icon: const Icon(Icons.shopping_cart),
              label: Text(l10n.placeOrder),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
          ),
        );
        widgets.add(const SizedBox(height: 12));
        // Secondary: Contact Farmer
        widgets.add(
          SizedBox(
            width: double.infinity,
            child: OutlinedButton.icon(
              onPressed: () async {
                final farmerName = await _lookupFarmerName(listing.farmerId);
                if (!context.mounted) return;
                showModalBottomSheet<void>(
                  context: context,
                  builder: (context) {
                    final l10n = AppLocalizations.of(context)!;
                    return Padding(
                      padding: const EdgeInsets.all(24),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            l10n.farmerContactInfo,
                            style: Theme.of(context).textTheme.titleLarge,
                          ),
                          const SizedBox(height: 16),
                          ListTile(
                            leading: const Icon(Icons.person),
                            title: Text(farmerName),
                            trailing: IconButton(
                              icon: const Icon(Icons.copy),
                              onPressed: () {
                                Clipboard.setData(
                                  ClipboardData(text: farmerName),
                                );
                                Navigator.pop(context);
                                ScaffoldMessenger.of(context).showSnackBar(
                                  SnackBar(
                                    content: Text(l10n.copiedToClipboard),
                                  ),
                                );
                              },
                            ),
                          ),
                        ],
                      ),
                    );
                  },
                );
              },
              icon: const Icon(Icons.chat_outlined),
              label: Text(l10n.contactFarmer),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
          ),
        );
    }

    return widgets;
  }

  Future<String> _lookupFarmerName(String farmerId) async {
    final isar = IsarService.instance.db;
    final farmer =
        await isar.users.filter().userIdEqualTo(farmerId).findFirst();
    return farmer?.name ?? farmerId;
  }
}

/// Header section with large crop icon, crop type name, and status chip.
class _HeaderSection extends StatelessWidget {
  final CropType cropType;
  final ListingStatus status;

  const _HeaderSection({required this.cropType, required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Center(
      child: Column(
        children: [
          Container(
            width: 80,
            height: 80,
            decoration: BoxDecoration(
              color: theme.colorScheme.primaryContainer,
              borderRadius: BorderRadius.circular(20),
            ),
            child: Icon(
              cropType.icon,
              color: theme.colorScheme.onPrimaryContainer,
              size: 40,
            ),
          ),
          const SizedBox(height: 16),
          Text(
            cropType.displayName(l10n),
            style: theme.textTheme.headlineSmall?.copyWith(
              fontWeight: FontWeight.bold,
            ),
          ),
          const SizedBox(height: 8),
          _StatusChip(status: status),
        ],
      ),
    );
  }
}

/// Status chip matching the pattern from [CropListingCard].
class _StatusChip extends StatelessWidget {
  final ListingStatus status;

  const _StatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (color, label) = switch (status) {
      ListingStatus.active => (Colors.green, l10n.listingStatusActive),
      ListingStatus.reserved => (Colors.orange, l10n.listingStatusReserved),
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

/// Card containing listing detail rows.
class _DetailsCard extends StatelessWidget {
  final CropListing listing;
  final CropType cropType;
  final DateFormat dateFormat;

  const _DetailsCard({
    required this.listing,
    required this.cropType,
    required this.dateFormat,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          children: [
            _DetailRow(
              icon: Icons.scale,
              label: l10n.detailQuantity,
              value: '${listing.quantity} ${l10n.quintals}',
            ),
            const Divider(height: 24),
            _DetailRow(
              icon: Icons.payments,
              label: l10n.detailPrice,
              value:
                  '${listing.pricePerKg.toStringAsFixed(2)} ${l10n.etbPerKg}',
              valueStyle: theme.textTheme.bodyLarge?.copyWith(
                color: theme.colorScheme.primary,
                fontWeight: FontWeight.w600,
              ),
            ),
            const Divider(height: 24),
            _DetailRow(
              icon: Icons.location_on,
              label: l10n.location,
              value: listing.locationName,
            ),
            if (listing.description.isNotEmpty) ...[
              const Divider(height: 24),
              _DetailRow(
                icon: Icons.description,
                label: l10n.description,
                value: listing.description,
              ),
            ],
            const Divider(height: 24),
            _DetailRow(
              icon: Icons.access_time,
              label: l10n.detailCreated,
              value: dateFormat.format(listing.createdAt),
            ),
          ],
        ),
      ),
    );
  }
}

/// A single detail row with icon, label, and value.
class _DetailRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final TextStyle? valueStyle;

  const _DetailRow({
    required this.icon,
    required this.label,
    required this.value,
    this.valueStyle,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(
          icon,
          size: 20,
          color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
              const SizedBox(height: 2),
              Text(
                value,
                style: valueStyle ??
                    theme.textTheme.bodyLarge?.copyWith(
                      fontWeight: FontWeight.w500,
                    ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
