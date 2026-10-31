import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
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

    return Scaffold(
      backgroundColor: AppTheme.chalkBackground,
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
                        color: AppTheme.fieldGreen,
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
                        color: AppTheme.soilDark,
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
                    color: AppTheme.soilDark,
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
                    color: AppTheme.mutedSteel,
                    height: 1.5,
                  ),
                ),
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
