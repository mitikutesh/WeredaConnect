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
          // The track's top and bottom 4 px belong to each segment's 48 tall
          // hit area ([_Segment]).
          padding: const EdgeInsets.symmetric(horizontal: 4),
          decoration: BoxDecoration(
            color: AppTheme.segmentTrack,
            borderRadius: BorderRadius.circular(14),
          ),
          // Equal heights when one name wraps to a second line.
          child: IntrinsicHeight(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                for (final entry in languageProvider.availableLanguages) ...[
                  if (entry.key !=
                      languageProvider.availableLanguages.first.key)
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
      // The track's padding is part of the target: a tap there selects too.
      child: GestureDetector(
        onTap: tap,
        behavior: HitTestBehavior.opaque,
        excludeFromSemantics: true,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Material(
            color: selected ? Colors.white : Colors.transparent,
            borderRadius: BorderRadius.circular(11),
            child: InkWell(
              onTap: tap,
              borderRadius: BorderRadius.circular(11),
              // The board's segment: 40 tall in the track. Large text or a
              // long name wraps to a second line instead of shrinking.
              child: ConstrainedBox(
                constraints: const BoxConstraints(minHeight: 40),
                child: Center(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 4,
                      vertical: 2,
                    ),
                    child: Text(
                      label,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight:
                            selected ? FontWeight.w700 : FontWeight.w600,
                        color: selected
                            ? AppTheme.soilDark
                            : AppTheme.unselectedNav,
                        fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      ),
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
