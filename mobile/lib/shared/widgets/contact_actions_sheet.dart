import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

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
    backgroundColor: AppTheme.ground,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
    ),
    builder: (sheetContext) {
      final l10n = AppLocalizations.of(sheetContext)!;
      Future<void> call() async {
        final messenger = ScaffoldMessenger.of(sheetContext);
        Navigator.pop(sheetContext);
        final uri = Uri(scheme: 'tel', path: trimmedPhone);
        final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
        if (!ok) {
          // No dialer (a tablet, an emulator): copy instead so the number
          // is never a dead end.
          await Clipboard.setData(ClipboardData(text: trimmedPhone));
          messenger.showSnackBar(
            SnackBar(content: Text(l10n.copiedToClipboard)),
          );
        }
      }

      return SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Center(
                child: Container(
                  width: 40,
                  height: 4,
                  decoration: BoxDecoration(
                    color: AppTheme.sketch,
                    borderRadius: BorderRadius.circular(999),
                  ),
                ),
              ),
              const SizedBox(height: 18),
              // Who this is, as the Contact card of the order boards: the
              // initials in a circle, the name, the role under it.
              Row(
                children: [
                  ExcludeSemantics(
                    child: Container(
                      width: 48,
                      height: 48,
                      alignment: Alignment.center,
                      decoration: const BoxDecoration(
                        color: AppTheme.sand,
                        shape: BoxShape.circle,
                      ),
                      child: Text(
                        _initials(name),
                        style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w800,
                          color: AppTheme.inkSoft,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          name,
                          style: const TextStyle(
                            fontSize: 19,
                            fontWeight: FontWeight.w800,
                            letterSpacing: -0.4,
                            color: AppTheme.ink,
                          ),
                        ),
                        Text(
                          roleLabel,
                          style: const TextStyle(
                            fontSize: 14,
                            color: AppTheme.slate,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 16),
              if (hasPhone)
                // The number on a sand tile, Copy beside it.
                Container(
                  padding: const EdgeInsetsDirectional.fromSTEB(16, 4, 4, 4),
                  decoration: BoxDecoration(
                    color: AppTheme.sand,
                    borderRadius: BorderRadius.circular(24),
                  ),
                  child: Row(
                    children: [
                      const Icon(
                        Icons.phone_outlined,
                        size: 18,
                        color: AppTheme.inkSoft,
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          trimmedPhone,
                          style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.w800,
                            color: AppTheme.ink,
                          ),
                        ),
                      ),
                      IconButton(
                        icon: const Icon(Icons.copy, size: 20),
                        color: AppTheme.ink,
                        tooltip: l10n.copy,
                        onPressed: () {
                          Clipboard.setData(ClipboardData(text: trimmedPhone));
                          ScaffoldMessenger.of(sheetContext).showSnackBar(
                            SnackBar(content: Text(l10n.copiedToClipboard)),
                          );
                        },
                      ),
                    ],
                  ),
                )
              else
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 16,
                    vertical: 14,
                  ),
                  decoration: BoxDecoration(
                    color: AppTheme.sand,
                    borderRadius: BorderRadius.circular(24),
                  ),
                  child: Text(
                    l10n.phoneNotAvailable,
                    style: const TextStyle(
                      fontSize: 14,
                      color: AppTheme.inkSoft,
                    ),
                  ),
                ),
              const SizedBox(height: 16),
              // Call is the black button, Message the outlined one, as on
              // the boards' contact rows.
              if (hasPhone) ...[
                FilledButton.icon(
                  onPressed: call,
                  style: FilledButton.styleFrom(
                    backgroundColor: AppTheme.ink,
                    foregroundColor: Colors.white,
                  ),
                  icon: const Icon(Icons.call, size: 18),
                  label: Text(l10n.call),
                ),
                const SizedBox(height: 8),
              ],
              if (onMessage != null) ...[
                OutlinedButton.icon(
                  onPressed: () {
                    Navigator.pop(sheetContext);
                    onMessage();
                  },
                  icon: const Icon(Icons.chat_bubble_outline, size: 18),
                  label: Text(l10n.sendMessage),
                ),
                const SizedBox(height: 4),
              ],
              TextButton(
                onPressed: () => Navigator.pop(sheetContext),
                style: TextButton.styleFrom(
                  foregroundColor: AppTheme.ink,
                  minimumSize: const Size.fromHeight(48),
                ),
                child: Text(l10n.close),
              ),
            ],
          ),
        ),
      );
    },
  );
}

/// The first letters of the first two words of [name]; "?" when empty.
String _initials(String name) {
  final words = name.trim().split(RegExp(r'\s+')).where((w) => w.isNotEmpty);
  if (words.isEmpty) return '?';
  return words.take(2).map((w) => w.characters.first.toUpperCase()).join();
}
