import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Inline language switch: English, Amharic and Afaan Oromoo side by side,
/// each in its own script (the Join · Welcome and login board).
///
/// One tap changes the app language through [LanguageProvider], which
/// persists it, so a user can switch before they log in, offline too.
class LanguageSegmentedPicker extends StatelessWidget {
  const LanguageSegmentedPicker({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageProvider = context.watch<LanguageProvider>();
    final current = languageProvider.locale;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          l10n.language,
          style: const TextStyle(
            fontSize: 13,
            fontWeight: FontWeight.w700,
            color: AppTheme.unselectedNav,
          ),
        ),
        const SizedBox(height: 8),
        Container(
          padding: const EdgeInsets.all(4),
          decoration: BoxDecoration(
            color: const Color(0xFFECEAE2),
            borderRadius: BorderRadius.circular(14),
          ),
          child: Row(
            children: [
              for (final entry in languageProvider.availableLanguages) ...[
                if (entry.key != languageProvider.availableLanguages.first.key)
                  const SizedBox(width: 6),
                Expanded(
                  child: _Segment(
                    key: ValueKey('language-${entry.key.languageCode}'),
                    label: entry.value,
                    selected: entry.key == current,
                    onTap: () => languageProvider.setLocale(entry.key),
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _Segment extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _Segment({
    required this.label,
    required this.selected,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      selected: selected,
      inMutuallyExclusiveGroup: true,
      excludeSemantics: true,
      label: label,
      child: Material(
        color: selected ? Colors.white : Colors.transparent,
        borderRadius: BorderRadius.circular(11),
        child: InkWell(
          onTap: selected ? null : onTap,
          borderRadius: BorderRadius.circular(11),
          child: SizedBox(
            height: 48,
            child: Center(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 4),
                child: FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(
                    label,
                    maxLines: 1,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                      color:
                          selected ? AppTheme.soilDark : AppTheme.unselectedNav,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
