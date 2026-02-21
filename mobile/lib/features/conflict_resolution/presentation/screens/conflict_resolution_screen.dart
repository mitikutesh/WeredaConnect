import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart'
    as bloc_event;
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Modal bottom sheet shown when a sync conflict is auto-resolved.
///
/// Informs the user that their local data was updated by the server.
/// The conflict is already resolved — this is purely informational.
///
/// Shows:
/// - Entity type (crop listing, delivery, trip)
/// - Key fields from server data
/// - "OK, Got It" button to dismiss
class ConflictResolutionScreen extends StatelessWidget {
  final ConflictEvent conflict;

  const ConflictResolutionScreen({required this.conflict, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Drag handle
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),

            // Header with warning icon
            Row(
              children: [
                Icon(
                  Icons.sync_problem,
                  color: theme.colorScheme.tertiary,
                  size: 28,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    l10n.conflictTitle,
                    style: theme.textTheme.titleLarge,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),

            // Explanation
            Text(
              l10n.conflictExplanation,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
              ),
            ),
            const SizedBox(height: 16),

            // Entity type badge
            _buildEntityBadge(context, l10n),
            const SizedBox(height: 16),

            // Field-level summary
            _buildFieldSummary(context, l10n),
            const SizedBox(height: 24),

            // Primary action
            FilledButton(
              onPressed: () {
                context.read<ConflictBloc>().add(
                      const bloc_event.AcceptServerVersion(),
                    );
                Navigator.of(context).pop();
              },
              child: Text(l10n.conflictAccept),
            ),
            const SizedBox(height: 8),

            // Secondary action
            TextButton(
              onPressed: () {
                context.read<ConflictBloc>().add(
                      const bloc_event.DismissConflict(),
                    );
                Navigator.of(context).pop();
              },
              child: Text(l10n.conflictDismiss),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildEntityBadge(BuildContext context, AppLocalizations l10n) {
    final theme = Theme.of(context);
    final label = _entityTypeLabel(l10n);
    final shortId = conflict.entityId.length > 8
        ? conflict.entityId.substring(0, 8)
        : conflict.entityId;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: theme.colorScheme.secondaryContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Row(
        children: [
          Icon(_entityTypeIcon(),
              size: 20, color: theme.colorScheme.onSecondaryContainer),
          const SizedBox(width: 8),
          Text(
            '$label #$shortId',
            style: theme.textTheme.labelLarge?.copyWith(
              color: theme.colorScheme.onSecondaryContainer,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFieldSummary(BuildContext context, AppLocalizations l10n) {
    final theme = Theme.of(context);
    final fields = _extractDisplayFields();

    if (fields.isEmpty) {
      return Text(
        l10n.conflictNoDetails,
        style: theme.textTheme.bodySmall,
      );
    }

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.conflictServerValues,
            style: theme.textTheme.labelMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
          const SizedBox(height: 8),
          ...fields.entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 120,
                    child: Text(
                      entry.key,
                      style: theme.textTheme.bodySmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  Expanded(
                    child: Text(
                      entry.value,
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  /// Extract human-readable field names and values from serverData
  /// based on entity type.
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
          // Security fix #2: guard against short tripId
          'Trip': () {
            final id = data['tripId']?.toString();
            if (id == null) return '-';
            return id.length > 8 ? id.substring(0, 8) : id;
          }(),
        };
      case 'trip':
        return {
          'Route':
              '${data['fromWoreda'] ?? '?'} → ${data['toWoreda'] ?? '?'}',
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
        return Icons.list_alt;
      case 'delivery':
        return Icons.inventory_2;
      case 'trip':
        return Icons.local_shipping;
      default:
        return Icons.sync_problem;
    }
  }
}
