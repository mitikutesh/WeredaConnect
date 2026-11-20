import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Stacked row showing the server value for one changed field.
///
/// A success-tint tile with a check and the words "current version": the
/// applied value is said twice, never by colour alone.
class ConflictFieldDiff extends StatelessWidget {
  final String fieldName;
  final String serverValue;

  const ConflictFieldDiff({
    required this.fieldName,
    required this.serverValue,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Container(
      decoration: BoxDecoration(
        color: AppTheme.statusSuccessContainer,
        borderRadius: BorderRadius.circular(16),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(
                l10n.conflictCurrentVersion.toUpperCase(),
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.onStatusSuccessContainer,
                  fontWeight: FontWeight.w700,
                  letterSpacing: 1.4,
                ),
              ),
              const SizedBox(width: 4),
              const Icon(
                Icons.check,
                size: 16,
                color: AppTheme.onStatusSuccessContainer,
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            '$fieldName: $serverValue',
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w700,
              color: AppTheme.ink,
            ),
          ),
        ],
      ),
    );
  }
}
