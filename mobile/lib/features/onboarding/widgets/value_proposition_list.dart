import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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
          color: Colors.green,
        ),
        _ValuePropItem(
          icon: Icons.mic,
          text: l10n.recordAudioNote,
          color: Colors.blue,
        ),
        const _ValuePropItem(
          icon: Icons.payment,
          text: 'Telebirr Payment', // TODO: Add to l10n
          color: Colors.orange,
        ),
        const _ValuePropItem(
          icon: Icons.storefront,
          text: 'Connect with Local Markets', // TODO: Add to l10n
          color: Colors.purple,
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
              style: const TextStyle(fontSize: 14, height: 1.4),
            ),
          ),
        ],
      ),
    );
  }
}
