import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Branded splash/landing screen shown on first launch.
///
/// Warm dirty-white background with teal accent colors matching the
/// WeredaConnect brand. Shows the tractor logo, headline, subtitle,
/// and a "Get started" CTA button.
class SplashScreen extends StatelessWidget {
  const SplashScreen({required this.onGetStarted, super.key});

  /// Called when the user taps "Get started".
  final VoidCallback onGetStarted;

  // ── Design tokens ─────────────────────────────────────────────────────
  static const _bgColor = AppTheme.backgroundCream;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final mediaQuery = MediaQuery.of(context);

    return Scaffold(
      backgroundColor: _bgColor,
      body: SafeArea(
        bottom: false,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // ── Header: logo + app name ──────────────────────────────
            const Padding(
              padding: EdgeInsets.fromLTRB(24, 24, 24, 0),
              child: Row(
                children: [
                  Icon(
                    Icons.agriculture,
                    size: 44,
                    color: AppTheme.primaryGreen,
                  ),
                  SizedBox(width: 12),
                  Text(
                    'WeredaConnect',
                    style: TextStyle(
                      fontSize: 18,
                      fontWeight: FontWeight.w600,
                      color: AppTheme.primaryGreenDark,
                      letterSpacing: 0.3,
                    ),
                  ),
                ],
              ),
            ),

            const SizedBox(height: 40),

            // ── Headline ─────────────────────────────────────────────
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 24),
              child: Text(
                l10n?.splashHeadline ?? 'Grow it. Move it.\nBuy it. Done.',
                style: const TextStyle(
                  fontSize: 36,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.primaryGreenDark,
                  height: 1.2,
                  letterSpacing: -0.5,
                ),
              ),
            ),

            const SizedBox(height: 16),

            // ── Subtitle ─────────────────────────────────────────────
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 24),
              child: Text(
                l10n?.splashSubtitle ??
                    'One platform for farmers, truckers, and buyers — with secure escrow.',
                style: const TextStyle(
                  fontSize: 16,
                  fontWeight: FontWeight.w400,
                  color: AppTheme.textMuted,
                  height: 1.5,
                ),
              ),
            ),

            const Spacer(),

            // ── Bottom CTA bar ───────────────────────────────────────
            Padding(
              padding: EdgeInsets.fromLTRB(
                24,
                0,
                24,
                mediaQuery.padding.bottom + 24,
              ),
              child: Material(
                borderRadius: BorderRadius.circular(28),
                clipBehavior: Clip.antiAlias,
                child: Ink(
                  decoration: const BoxDecoration(
                    gradient: LinearGradient(
                      colors: [
                        AppTheme.primaryGreenLight,
                        AppTheme.primaryGreen,
                      ],
                    ),
                  ),
                  child: InkWell(
                    onTap: onGetStarted,
                    child: SizedBox(
                      height: 56,
                      child: Row(
                        mainAxisAlignment: MainAxisAlignment.end,
                        children: [
                          Text(
                            l10n?.getStarted ?? 'Get started',
                            style: const TextStyle(
                              fontSize: 16,
                              fontWeight: FontWeight.w600,
                              color: Colors.white,
                              letterSpacing: 0.3,
                            ),
                          ),
                          const SizedBox(width: 8),
                          const Icon(
                            Icons.arrow_forward,
                            size: 20,
                            color: Colors.white,
                          ),
                          const SizedBox(width: 24),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
