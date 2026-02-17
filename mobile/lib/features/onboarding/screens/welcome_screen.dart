import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/features/onboarding/widgets/language_selector_card.dart';
import 'package:wereda_connect/features/onboarding/widgets/value_proposition_list.dart';
import 'package:wereda_connect/features/onboarding/widgets/welcome_actions.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Welcome/Landing screen shown on first launch or when unauthenticated.
///
/// Features:
/// - Hero illustration (agriculture icon for now, SVG later)
/// - Language selector (prominent at top)
/// - Value proposition list (4 key benefits)
/// - Three CTAs: Login, Register, Guest (coming soon)
/// - "How it works" chip (shows info for now)
///
/// Ethiopian context:
/// - Language selection BEFORE authentication (respectful of linguistic diversity)
/// - Visual-first design for low-literacy users
/// - Large touch targets (48dp minimum)
/// - Offline-first messaging prominent
class WelcomeScreen extends StatelessWidget {
  const WelcomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Scaffold(
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24.0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const SizedBox(height: 32),

              // Hero illustration
              Icon(
                Icons.agriculture,
                size: 120,
                color: theme.primaryColor,
              ),
              const SizedBox(height: 16),

              // App title
              Text(
                'WeredaConnect',
                style: theme.textTheme.headlineMedium?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 24),

              // Language selector card
              const LanguageSelectorCard(),
              const SizedBox(height: 24),

              // Tagline
              Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 16,
                  vertical: 12,
                ),
                decoration: BoxDecoration(
                  color: Colors.grey[50],
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  l10n.aboutTagline,
                  style: const TextStyle(
                    fontSize: 16,
                    height: 1.4,
                  ),
                  textAlign: TextAlign.center,
                ),
              ),
              const SizedBox(height: 32),

              // Value propositions
              const ValuePropositionList(),
              const SizedBox(height: 32),

              // Action buttons
              WelcomeActions(
                onWelcomeComplete: () {
                  // Mark welcome as seen when user proceeds to login/register
                  context.read<OnboardingBloc>().add(const MarkWelcomeSeen());
                },
              ),
              const SizedBox(height: 16),

              // Info chips at bottom
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  _InfoChip(
                    icon: Icons.help_outline,
                    label: l10n.howItWorks,
                    onTap: () => _showHowItWorksInfo(context, l10n),
                  ),
                  const SizedBox(width: 12),
                  _InfoChip(
                    icon: Icons.cloud_off,
                    label: l10n.offlineMode,
                    onTap: () => _showOfflineModeInfo(context, l10n),
                  ),
                ],
              ),
              const SizedBox(height: 24),
            ],
          ),
        ),
      ),
    );
  }

  void _showHowItWorksInfo(BuildContext context, AppLocalizations l10n) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          'Tutorial carousel coming soon. You can explore the app by creating an account!',
        ),
        duration: const Duration(seconds: 3),
      ),
    );
  }

  void _showOfflineModeInfo(BuildContext context, AppLocalizations l10n) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(l10n.dataWillSyncWhenOnline),
        duration: const Duration(seconds: 3),
      ),
    );
  }
}

/// Info chip widget for bottom row.
class _InfoChip extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback onTap;

  const _InfoChip({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Chip(
        avatar: Icon(icon, size: 16),
        label: Text(
          label,
          style: const TextStyle(fontSize: 14),
        ),
        padding: const EdgeInsets.symmetric(horizontal: 8),
      ),
    );
  }
}
