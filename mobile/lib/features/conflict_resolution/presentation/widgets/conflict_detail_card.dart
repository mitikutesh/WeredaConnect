import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_field_diff.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

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
    final fields = _extractDisplayFields(l10n);

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
                color: AppTheme.statusSuccess.withAlpha(25),
                border: Border.all(color: AppTheme.statusSuccess),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  const Icon(
                    Icons.check_circle,
                    size: 16,
                    color: AppTheme.statusSuccess,
                  ),
                  const SizedBox(width: 6),
                  Text(
                    l10n.conflictServerApplied,
                    style: const TextStyle(
                      fontSize: 12,
                      color: AppTheme.statusSuccess,
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

  Map<String, String> _extractDisplayFields(AppLocalizations l10n) {
    final data = conflict.serverData;
    switch (conflict.entityType) {
      case 'crop_listing':
        return {
          l10n.conflictFieldCropType: data['cropType']?.toString() ?? '-',
          l10n.conflictFieldQuantity: data['quantity']?.toString() ?? '-',
          l10n.conflictFieldPricePerKg: data['pricePerKg']?.toString() ?? '-',
          l10n.conflictFieldStatus: data['status']?.toString() ?? '-',
        };
      case 'delivery':
        return {
          l10n.conflictFieldStatus: data['status']?.toString() ?? '-',
          l10n.conflictFieldTrip: () {
            final id = data['tripId']?.toString();
            if (id == null) return '-';
            return id.length > 8 ? id.substring(0, 8) : id;
          }(),
        };
      case 'trip':
        return {
          l10n.conflictFieldRoute:
              '${data['fromWoreda'] ?? '?'} → ${data['toWoreda'] ?? '?'}',
          l10n.conflictFieldStatus: data['status']?.toString() ?? '-',
          l10n.conflictFieldCapacity: data['capacity']?.toString() ?? '-',
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
    return DateFormat.yMMMd().format(dt);
  }
}
