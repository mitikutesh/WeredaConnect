import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// A bottom sheet with contact actions for a counterparty: optionally open an
/// in-app chat, and (when a phone number is available) call or copy it.
///
/// Shared across the crop / trip / order / delivery detail screens and the
/// conversation header so the Call/Copy/Message affordances are consistent.
Future<void> showContactActionsSheet(
  BuildContext context, {
  required String name,
  required String roleLabel,
  String? phone,
  VoidCallback? onMessage,
}) {
  final trimmedPhone = (phone ?? '').trim();
  final hasPhone = trimmedPhone.isNotEmpty;

  return showModalBottomSheet<void>(
    context: context,
    builder: (sheetContext) {
      final l10n = AppLocalizations.of(sheetContext)!;
      final theme = Theme.of(sheetContext);
      return SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 24, 24, 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.person),
                title: Text(name),
                subtitle: Text(roleLabel),
              ),
              if (onMessage != null)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.chat_bubble_outline),
                  title: Text(l10n.sendMessage),
                  onTap: () {
                    Navigator.pop(sheetContext);
                    onMessage();
                  },
                ),
              if (hasPhone)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.phone),
                  title: Text(trimmedPhone),
                  trailing: IconButton(
                    icon: const Icon(Icons.copy),
                    tooltip: l10n.copy,
                    onPressed: () {
                      Clipboard.setData(ClipboardData(text: trimmedPhone));
                      ScaffoldMessenger.of(sheetContext).showSnackBar(
                        SnackBar(content: Text(l10n.copiedToClipboard)),
                      );
                    },
                  ),
                )
              else
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 12),
                  child: Text(
                    l10n.phoneNotAvailable,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ),
              const SizedBox(height: 16),
              Row(
                children: [
                  Expanded(
                    child: TextButton(
                      onPressed: () => Navigator.pop(sheetContext),
                      child: Text(l10n.close),
                    ),
                  ),
                  if (hasPhone) ...[
                    const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton.icon(
                        onPressed: () async {
                          final messenger = ScaffoldMessenger.of(sheetContext);
                          Navigator.pop(sheetContext);
                          final uri = Uri(scheme: 'tel', path: trimmedPhone);
                          final ok = await launchUrl(
                            uri,
                            mode: LaunchMode.externalApplication,
                          );
                          if (!ok) {
                            // No dialer (e.g. tablet/emulator) — copy instead so
                            // the number is never a dead end.
                            await Clipboard.setData(
                              ClipboardData(text: trimmedPhone),
                            );
                            messenger.showSnackBar(
                              SnackBar(content: Text(l10n.copiedToClipboard)),
                            );
                          }
                        },
                        icon: const Icon(Icons.call),
                        label: Text(l10n.call),
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
        ),
      );
    },
  );
}
