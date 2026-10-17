import 'package:flutter/material.dart';

/// ESC-48: the short note shown where Call, Copy and Message are hidden
/// because contact has not opened yet (crop and trip detail, an order before
/// the farmer accepted it, a closed chat thread).
class ContactLockedNote extends StatelessWidget {
  final String text;

  const ContactLockedNote({required this.text, super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = theme.colorScheme.onSurface.withValues(alpha: 0.7);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(Icons.lock_outline, size: 18, color: color),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            text,
            style: theme.textTheme.bodyMedium?.copyWith(color: color),
          ),
        ),
      ],
    );
  }
}
