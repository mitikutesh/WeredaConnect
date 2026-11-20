import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// Branded splash/landing screen shown on first launch.
///
/// The brand before sign-in (design canvas page M): a white ground, the
/// black brand tile, the headline in black and the subtitle in slate, the
/// language pills, and the orange "Get started" pill at the foot.
class SplashScreen extends StatelessWidget {
  const SplashScreen({required this.onGetStarted, super.key});

  /// Called when the user taps "Get started".
  final VoidCallback onGetStarted;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    // BUG-40: the language is chosen here, before anything else is read.
    // Null where no provider sits above (a bare test).
    final languages = context.watch<LanguageProvider?>();

    return Scaffold(
      backgroundColor: AppTheme.ground,
      body: SafeArea(
        child: FwContentWidth(
          maxWidth: 480,
          child: JoinPage(
            padding: const EdgeInsets.fromLTRB(20, 24, 20, 28),
            body: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // ── Header: logo + app name ────────────────────────────
                Row(
                  children: [
                    Container(
                      width: 40,
                      height: 40,
                      decoration: BoxDecoration(
                        color: AppTheme.ink,
                        borderRadius: BorderRadius.circular(14),
                      ),
                      child: const Icon(
                        Icons.agriculture,
                        color: Colors.white,
                        size: 25,
                      ),
                    ),
                    const SizedBox(width: 10),
                    Text(
                      l10n?.appName ?? 'WeredaConnect',
                      style: const TextStyle(
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.3,
                        color: AppTheme.ink,
                      ),
                    ),
                  ],
                ),

                const SizedBox(height: 48),

                // ── Headline ───────────────────────────────────────────
                Text(
                  l10n?.splashHeadline ?? 'Grow it. Move it.\nBuy it. Done.',
                  style: const TextStyle(
                    fontSize: 42,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.ink,
                    height: 1.1,
                    letterSpacing: -1.4,
                  ),
                ),

                const SizedBox(height: 16),

                // ── Subtitle ───────────────────────────────────────────
                Text(
                  l10n?.splashSubtitle ??
                      'One platform for farmers, truckers, and buyers, with secure escrow.',
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.slate,
                    height: 1.5,
                  ),
                ),

                if (languages != null) ...[
                  const SizedBox(height: 28),
                  _LanguageChoice(languages: languages),
                ],
              ],
            ),

            // ── Bottom CTA ─────────────────────────────────────────────
            footer: SizedBox(
              // The orange pill, 54 tall (the theme's button).
              child: FilledButton.icon(
                onPressed: onGetStarted,
                iconAlignment: IconAlignment.end,
                icon: const Icon(Icons.arrow_forward, size: 20),
                label: Text(l10n?.getStarted ?? 'Get started'),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The first screen's language choice (BUG-40): one pill per language,
/// each named in its own script so it reads without knowing another. The
/// pick applies at once and is kept as this device's language.
class _LanguageChoice extends StatelessWidget {
  final LanguageProvider languages;

  const _LanguageChoice({required this.languages});

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Padding(
          padding: EdgeInsets.only(top: 13),
          child: Icon(Icons.language, size: 22, color: AppTheme.slate),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Wrap(
            spacing: 8,
            children: [
              for (final entry in languages.availableLanguages)
                _LanguageButton(
                  key: Key('splash_language_${entry.key.languageCode}'),
                  name: entry.value,
                  selected: entry.key == languages.locale,
                  onPressed: () => languages.setLocale(entry.key),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

class _LanguageButton extends StatelessWidget {
  final String name;
  final bool selected;
  final VoidCallback onPressed;

  const _LanguageButton({
    required this.name,
    required this.selected,
    required this.onPressed,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      selected: selected,
      child: OutlinedButton(
        onPressed: onPressed,
        style: OutlinedButton.styleFrom(
          // A 44 tall pill in a 48 tall hit area, the picked one black.
          minimumSize: const Size(48, 44),
          tapTargetSize: MaterialTapTargetSize.padded,
          padding: const EdgeInsets.symmetric(horizontal: 16),
          backgroundColor: selected ? AppTheme.ink : Colors.transparent,
          foregroundColor: selected ? Colors.white : AppTheme.inkSoft,
          side: BorderSide(
            color: selected ? AppTheme.ink : AppTheme.fieldBorder,
          ),
          shape: const StadiumBorder(),
          textStyle: TextStyle(
            fontFamily: AppTheme.fontFamily,
            fontFamilyFallback: AppTheme.ethiopicFontFallback,
            fontSize: 14,
            fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
          ),
        ),
        child: Text(name),
      ),
    );
  }
}
