import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Inline language switch: English, Amharic and Afaan Oromoo as three
/// pills, each in its own script, the picked one black (the WelcomeM
/// board).
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
        Row(
          children: [
            const ExcludeSemantics(
              child: Icon(Icons.language, size: 16, color: AppTheme.slate),
            ),
            const SizedBox(width: 8),
            Expanded(child: FwSectionLabel(l10n.language)),
          ],
        ),
        const SizedBox(height: 6),
        // The pills hug their names; a narrow screen or large text wraps
        // them to a second row.
        Wrap(
          spacing: 8,
          children: [
            for (final entry in languageProvider.availableLanguages)
              _Segment(
                key: ValueKey('language-${entry.key.languageCode}'),
                label: entry.value,
                selected: entry.key == current,
                onTap: () => languageProvider.setLocale(entry.key),
              ),
          ],
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
    final tap = selected ? null : onTap;
    return Semantics(
      button: true,
      selected: selected,
      inMutuallyExclusiveGroup: true,
      excludeSemantics: true,
      label: label,
      // excludeSemantics drops the InkWell's tap action: give it back.
      onTap: tap,
      // A 44 tall pill in a 48 tall hit area.
      child: FwTapTarget(
        onTap: tap,
        child: Material(
          color: selected ? AppTheme.ink : Colors.transparent,
          shape: StadiumBorder(
            side: BorderSide(
              color: selected ? AppTheme.ink : AppTheme.fieldBorder,
            ),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: tap,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Center(
                  widthFactor: 1,
                  heightFactor: 1,
                  child: Text(
                    label,
                    textAlign: TextAlign.center,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                      color: selected ? Colors.white : AppTheme.inkSoft,
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
