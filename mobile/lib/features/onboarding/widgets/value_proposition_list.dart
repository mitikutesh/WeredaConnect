import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Widget displaying value propositions/benefits on the welcome screen.
///
/// Shows a list of key features with icons:
/// - Works Offline
/// - Voice Support in Your Language
/// - Telebirr Payment
/// - Connect with Local Markets
class ValuePropositionList extends StatelessWidget {
  const ValuePropositionList({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _ValuePropItem(
          icon: Icons.cloud_off,
          text: l10n.offlineReady,
          color: AppTheme.statusSuccess,
        ),
        _ValuePropItem(
          icon: Icons.mic,
          // The value proposition is the benefit ("Voice Support in Your
          // Language"), not the "Record Audio Note" action label.
          text: l10n.voiceSupport,
          color: AppTheme.statusInfo,
        ),
        _ValuePropItem(
          icon: Icons.payment,
          text: l10n.telebirrPayment,
          color: AppTheme.accentGold,
        ),
        _ValuePropItem(
          icon: Icons.storefront,
          text: l10n.connectWithMarkets,
          color: AppTheme.statusTransit,
        ),
      ],
    );
  }
}

/// Individual value proposition item with icon and text.
class _ValuePropItem extends StatelessWidget {
  final IconData icon;
  final String text;
  final Color color;

  const _ValuePropItem({
    required this.icon,
    required this.text,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Icon(icon, color: color, size: 24),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    height: 1.4,
                  ),
            ),
          ),
        ],
      ),
    );
  }
}
