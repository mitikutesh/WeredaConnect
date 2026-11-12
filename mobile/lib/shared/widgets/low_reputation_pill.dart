import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// ESC-31: the "Low reputation" label a buyer sees on a farmer's or
/// trucker's card (reputation-api.md 1.2: the public tier `low`). Red with
/// an icon, so it does not rely on colour alone. The only reputation label
/// shown to buyers: internal tier names never reach a card.
class LowReputationPill extends StatelessWidget {
  const LowReputationPill({super.key});

  @override
  Widget build(BuildContext context) {
    return FwPill(
      label: AppLocalizations.of(context)!.reputationLowTag,
      icon: Icons.error_outline,
      background: AppTheme.statusErrorContainer,
      foreground: AppTheme.statusError,
    );
  }
}
