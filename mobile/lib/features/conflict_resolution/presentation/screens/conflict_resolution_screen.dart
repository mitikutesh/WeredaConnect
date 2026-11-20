import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart'
    as bloc_event;
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Localized label for a conflict entity type. Falls back to a humanized token
/// (e.g. "buyer_intent" -> "Buyer Intent") for any unmapped type rather than
/// leaking the raw wire token. Shared by the conflict banner and this screen.
String conflictEntityLabel(AppLocalizations l10n, String entityType) {
  switch (entityType) {
    case 'crop_listing':
      return l10n.conflictEntityCropListing;
    case 'delivery':
      return l10n.conflictEntityDelivery;
    case 'trip':
      return l10n.conflictEntityTrip;
    case 'order':
      return l10n.conflictEntityOrder;
    case 'rating':
      return l10n.conflictEntityRating;
    case 'escrow':
    case 'payment':
      return l10n.conflictEntityPayment;
    default:
      return _humanizeToken(entityType);
  }
}

/// Turn a wire token ("picked_up", "in_transit", "buyer_intent") into a readable
/// label ("Picked Up", "In Transit", "Buyer Intent"). Used as a fallback so the
/// conflict UI never shows a raw snake_case/camelCase token.
String _humanizeToken(String token) {
  if (token.isEmpty) return '-';
  final spaced = token
      .replaceAll('_', ' ')
      .replaceAllMapped(RegExp('([a-z])([A-Z])'), (m) => '${m[1]} ${m[2]}');
  return spaced
      .split(' ')
      .where((w) => w.isNotEmpty)
      .map((w) => w[0].toUpperCase() + w.substring(1))
      .join(' ');
}

/// Modal bottom sheet shown when a sync conflict is auto-resolved.
///
/// Informs the user that their local data was updated by the server.
/// The conflict is already resolved: this is purely informational.
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

    // A sheet in the boards' language: the handle, the title, what
    // happened in one line, what it was about as a sand pill and the
    // server's values on a sand tile, then the one orange action.
    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
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
                  color: AppTheme.sketch,
                  borderRadius: BorderRadius.circular(999),
                ),
              ),
            ),
            const SizedBox(height: 16),

            Row(
              children: [
                Container(
                  width: 40,
                  height: 40,
                  decoration: const BoxDecoration(
                    color: AppTheme.orangeTint,
                    shape: BoxShape.circle,
                  ),
                  child: const Icon(
                    Icons.sync_problem,
                    color: AppTheme.orangeInk,
                    size: 20,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Semantics(
                    header: true,
                    child: Text(
                      l10n.conflictTitle,
                      style: FwText.detailTitle.copyWith(fontSize: 26),
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            // Explanation
            Text(
              l10n.conflictExplanation,
              style: const TextStyle(
                fontSize: 14,
                height: 1.45,
                color: AppTheme.inkSoft,
              ),
            ),
            const SizedBox(height: 16),
            // Entity type badge
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: _buildEntityBadge(context, l10n),
            ),
            const SizedBox(height: 12),
            // Field-level summary
            _buildFieldSummary(context, l10n),
            const SizedBox(height: 20),
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
            OutlinedButton(
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
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
    final label = _entityTypeLabel(l10n);
    final shortId = conflict.entityId.length > 8
        ? conflict.entityId.substring(0, 8)
        : conflict.entityId;

    return FwPill(
      label: '$label #$shortId',
      icon: _entityTypeIcon(),
      background: AppTheme.sand,
      foreground: AppTheme.inkSoft,
    );
  }

  Widget _buildFieldSummary(BuildContext context, AppLocalizations l10n) {
    final fields = _extractDisplayFields(l10n);

    if (fields.isEmpty) {
      return Text(
        l10n.conflictNoDetails,
        style: const TextStyle(fontSize: 13, color: AppTheme.slate),
      );
    }

    return FwCard.sand(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.conflictServerValues,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: AppTheme.inkSoft,
            ),
          ),
          const SizedBox(height: 10),
          ...fields.entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 120,
                    child: Text(
                      entry.key,
                      style: const TextStyle(
                        fontSize: 14,
                        color: AppTheme.inkSoft,
                      ),
                    ),
                  ),
                  Expanded(
                    child: Text(
                      entry.value,
                      style: const TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.ink,
                      ),
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
  Map<String, String> _extractDisplayFields(AppLocalizations l10n) {
    final data = conflict.serverData;
    switch (conflict.entityType) {
      case 'crop_listing':
        return {
          l10n.conflictFieldCropType: data['cropType']?.toString() ?? '-',
          l10n.conflictFieldQuantity: data['quantity']?.toString() ?? '-',
          l10n.conflictFieldPricePerKg: data['pricePerKg']?.toString() ?? '-',
          l10n.conflictFieldStatus:
              _humanizeToken(data['status']?.toString() ?? ''),
        };
      case 'delivery':
        return {
          l10n.conflictFieldStatus:
              _humanizeToken(data['status']?.toString() ?? ''),
          // Security fix #2: guard against short tripId
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
          l10n.conflictFieldStatus:
              _humanizeToken(data['status']?.toString() ?? ''),
          l10n.conflictFieldCapacity: data['capacity']?.toString() ?? '-',
        };
      default:
        return {};
    }
  }

  String _entityTypeLabel(AppLocalizations l10n) =>
      conflictEntityLabel(l10n, conflict.entityType);

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
