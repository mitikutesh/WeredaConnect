import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Card widget for selecting language on the welcome screen.
///
/// Displays current language with a dropdown indicator.
/// Taps open a bottom sheet with language options.
class LanguageSelectorCard extends StatelessWidget {
  const LanguageSelectorCard({super.key});

  @override
  Widget build(BuildContext context) {
    final languageProvider = Provider.of<LanguageProvider>(context);
    final currentLocale = languageProvider.locale;
    final selectedLanguageName =
        languageProvider.getLanguageName(currentLocale);

    return Card(
      elevation: 2,
      child: InkWell(
        onTap: () => _showLanguageBottomSheet(context),
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
          child: Row(
            children: [
              const Icon(Icons.language, size: 24),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Language / ቋንቋ / Afaan',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      selectedLanguageName,
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ],
                ),
              ),
              const Icon(Icons.arrow_drop_down),
            ],
          ),
        ),
      ),
    );
  }

  void _showLanguageBottomSheet(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (context) => const LanguageSelectionDialog(),
    );
  }
}
