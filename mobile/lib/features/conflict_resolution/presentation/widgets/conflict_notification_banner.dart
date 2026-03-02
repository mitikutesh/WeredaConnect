import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// MaterialBanner shown after sync resolves conflicts.
///
/// Non-intrusive notification that slides in at the top of the screen.
/// Tappable "View Changes" action leads to the conflict resolution screen.
class ConflictNotificationBanner extends StatelessWidget {
  final String entityType;
  final int conflictCount;
  final VoidCallback onViewChanges;
  final VoidCallback onDismiss;

  const ConflictNotificationBanner({
    required this.entityType,
    required this.conflictCount,
    required this.onViewChanges,
    required this.onDismiss,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final message = conflictCount > 1
        ? l10n.conflictBannerMultiple(conflictCount)
        : l10n.conflictBannerSingle(entityType);

    return MaterialBanner(
      backgroundColor: AppTheme.statusWarning.withAlpha(25),
      leading: const Icon(Icons.sync, size: 24, color: AppTheme.statusWarning),
      content: Text(
        message,
        style: Theme.of(context).textTheme.bodyLarge,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
      ),
      actions: [
        TextButton(
          onPressed: onViewChanges,
          child: Text(l10n.conflictBannerAction),
        ),
      ],
      onVisible: () {
        // Auto-dismiss after 8 seconds
        Future.delayed(const Duration(seconds: 8), () {
          onDismiss();
        });
      },
    );
  }
}
