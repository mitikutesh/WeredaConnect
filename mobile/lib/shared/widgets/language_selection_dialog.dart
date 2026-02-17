// ignore_for_file: deprecated_member_use
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

/// Dialog for selecting the app language.
///
/// Displays all supported languages (English, Amharic, Afan Oromo)
/// with radio buttons showing the current selection.
///
/// When a language is selected, updates the app-wide locale immediately
/// and persists the choice to shared preferences.
class LanguageSelectionDialog extends StatelessWidget {
  const LanguageSelectionDialog({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageProvider = Provider.of<LanguageProvider>(context);
    final currentLocale = languageProvider.locale;

    return AlertDialog(
      title: Text(l10n.language),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: languageProvider.availableLanguages.map((entry) {
          final locale = entry.key;
          final name = entry.value;
          final isSelected = locale == currentLocale;

          return RadioListTile<Locale>(
            value: locale,
            groupValue: currentLocale,
            onChanged: (Locale? newLocale) async {
              if (newLocale != null) {
                await languageProvider.setLocale(newLocale);
                if (context.mounted) {
                  Navigator.of(context).pop();

                  // Show confirmation in the new language
                  final newL10n = AppLocalizations.of(context)!;
                  ScaffoldMessenger.of(context).showSnackBar(
                    SnackBar(
                      content: Text(
                        '${newL10n.language}: ${languageProvider.getLanguageName(newLocale)}',
                      ),
                      duration: const Duration(seconds: 2),
                    ),
                  );
                }
              }
            },
            title: Text(
              name,
              style: TextStyle(
                fontWeight: isSelected ? FontWeight.bold : FontWeight.normal,
              ),
            ),
            secondary: isSelected
                ? const Icon(Icons.check, color: Colors.green)
                : null,
          );
        }).toList(),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.cancel),
        ),
      ],
    );
  }
}
