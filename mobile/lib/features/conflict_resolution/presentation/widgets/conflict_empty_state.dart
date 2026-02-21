import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Empty state widget shown when there are no pending conflicts.
///
/// Displays a large green checkmark with reassuring text for
/// low-literacy users — "Everything is up to date".
class ConflictEmptyState extends StatelessWidget {
  const ConflictEmptyState({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.check_circle,
            size: 80,
            color: Colors.green.shade400,
          ),
          const SizedBox(height: 24),
          Text(
            l10n.conflictEmptyTitle,
            style: theme.textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          Text(
            l10n.conflictEmptySubtitle,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
        ],
      ),
    );
  }
}
