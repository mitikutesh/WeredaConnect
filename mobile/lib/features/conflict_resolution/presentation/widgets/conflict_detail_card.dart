import 'package:flutter/material.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_field_diff.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Card showing entity header + field diffs + result badge for one conflict.
///
/// Displays the entity type icon, name, timestamp, server field values,
/// and a "Server version applied" badge.
class ConflictDetailCard extends StatelessWidget {
  final ConflictEvent conflict;

  const ConflictDetailCard({required this.conflict, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final fields = _extractDisplayFields();

    return Card(
      elevation: 1,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header row: icon + entity name + timestamp
            Row(
              children: [
                Icon(_entityTypeIcon(), size: 24),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _entityTypeLabel(l10n),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                ),
                Text(
                  _formatTimestamp(conflict.resolvedAt),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),

            // Field diffs
            ...fields.entries.map(
              (entry) => Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: ConflictFieldDiff(
                  fieldName: entry.key,
                  serverValue: entry.value,
                ),
              ),
            ),
            const SizedBox(height: 8),

            // Result badge
            Container(
              width: double.infinity,
              height: 32,
              decoration: BoxDecoration(
                color: Colors.green.shade50,
                border: Border.all(color: Colors.green.shade700),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Icon(
                    Icons.check_circle,
                    size: 16,
                    color: Colors.green.shade700,
                  ),
                  const SizedBox(width: 6),
                  Text(
                    l10n.conflictServerApplied,
                    style: TextStyle(
                      fontSize: 12,
                      color: Colors.green.shade800,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Map<String, String> _extractDisplayFields() {
    final data = conflict.serverData;
    switch (conflict.entityType) {
      case 'crop_listing':
        return {
          'Crop Type': data['cropType']?.toString() ?? '-',
          'Quantity': data['quantity']?.toString() ?? '-',
          'Price/kg': data['pricePerKg']?.toString() ?? '-',
          'Status': data['status']?.toString() ?? '-',
        };
      case 'delivery':
        return {
          'Status': data['status']?.toString() ?? '-',
          'Trip': () {
            final id = data['tripId']?.toString();
            if (id == null) return '-';
            return id.length > 8 ? id.substring(0, 8) : id;
          }(),
        };
      case 'trip':
        return {
          'Route': '${data['fromWoreda'] ?? '?'} → ${data['toWoreda'] ?? '?'}',
          'Status': data['status']?.toString() ?? '-',
          'Capacity': data['capacity']?.toString() ?? '-',
        };
      default:
        return {};
    }
  }

  String _entityTypeLabel(AppLocalizations l10n) {
    switch (conflict.entityType) {
      case 'crop_listing':
        return l10n.conflictEntityCropListing;
      case 'delivery':
        return l10n.conflictEntityDelivery;
      case 'trip':
        return l10n.conflictEntityTrip;
      default:
        return conflict.entityType;
    }
  }

  IconData _entityTypeIcon() {
    switch (conflict.entityType) {
      case 'crop_listing':
        return Icons.grass;
      case 'delivery':
        return Icons.inventory_2;
      case 'trip':
        return Icons.local_shipping;
      default:
        return Icons.sync_problem;
    }
  }

  String _formatTimestamp(DateTime dt) {
    final months = [
      'Jan',
      'Feb',
      'Mar',
      'Apr',
      'May',
      'Jun',
      'Jul',
      'Aug',
      'Sep',
      'Oct',
      'Nov',
      'Dec',
    ];
    return '${months[dt.month - 1]} ${dt.day}, ${dt.year}';
  }
}
