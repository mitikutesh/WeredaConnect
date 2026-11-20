import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// ESC-48: the short note shown where Call, Copy and Message are hidden
/// because contact has not opened yet (crop and trip detail, an order before
/// the farmer accepted it, a closed chat thread). Drawn as on the buyer's
/// order board: a lock in a sand circle, the reason beside it.
class ContactLockedNote extends StatelessWidget {
  final String text;

  const ContactLockedNote({required this.text, super.key});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Container(
          width: 38,
          height: 38,
          decoration: const BoxDecoration(
            color: AppTheme.sand,
            shape: BoxShape.circle,
          ),
          child: const Icon(
            Icons.lock_outline,
            size: 16,
            color: AppTheme.inkSoft,
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            text,
            style: const TextStyle(
              fontSize: 14,
              height: 1.35,
              color: AppTheme.slate,
            ),
          ),
        ),
      ],
    );
  }
}
