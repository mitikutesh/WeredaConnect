import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Empty state widget shown when there are no pending conflicts.
///
/// Displays a check mark with reassuring text for low-literacy users:
/// "Everything is up to date".
class ConflictEmptyState extends StatelessWidget {
  const ConflictEmptyState({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(
            Icons.check_circle,
            size: 56,
            color: AppTheme.statusSuccess,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.conflictEmptyTitle,
            textAlign: TextAlign.center,
            style: FwText.detailTitle.copyWith(fontSize: 26),
          ),
          const SizedBox(height: 8),
          Text(
            l10n.conflictEmptySubtitle,
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 15, color: AppTheme.slate),
          ),
        ],
      ),
    );
  }
}
