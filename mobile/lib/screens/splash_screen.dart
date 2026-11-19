import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// Branded splash/landing screen shown on first launch.
///
/// Fieldway: the chalk ground, the green wordmark tile from the Welcome
/// board, the headline and subtitle in soil dark and muted steel, and a
/// "Get started" button at the foot (52 tall, radius 16).
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
                        color: context.roleColors.primary,
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: const Icon(
                        Icons.route,
                        color: Colors.white,
                        size: 22,
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
                    fontSize: 36,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.ink,
                    height: 1.1,
                    letterSpacing: -1,
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
              height: 52,
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

/// The first screen's language choice (BUG-40): one button per language,
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
          padding: EdgeInsets.only(top: 12),
          child: Icon(Icons.language, size: 22, color: AppTheme.slate),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Wrap(
            spacing: 8,
            runSpacing: 8,
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
          minimumSize: const Size(48, 48),
          backgroundColor:
              selected ? context.roleColors.tint : AppTheme.surfaceWhite,
          foregroundColor: AppTheme.ink,
          side: BorderSide(
            color: selected ? context.roleColors.primary : AppTheme.line,
            width: selected ? 2 : 1,
          ),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(14),
          ),
          textStyle: const TextStyle(
            fontFamily: AppTheme.fontFamily,
            fontFamilyFallback: AppTheme.ethiopicFontFallback,
            fontSize: 15,
            fontWeight: FontWeight.w700,
          ),
        ),
        child: Text(name),
      ),
    );
  }
}
