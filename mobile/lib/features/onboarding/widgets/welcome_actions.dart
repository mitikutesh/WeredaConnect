import 'package:flutter/material.dart';

import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Action buttons for the welcome screen.
///
/// Provides three call-to-action buttons:
/// 1. Login (primary elevated button)
/// 2. Create Account (secondary outlined button)
/// 3. Explore as Guest (text link - coming soon)
class WelcomeActions extends StatelessWidget {
  final VoidCallback? onWelcomeComplete;

  const WelcomeActions({
    super.key,
    this.onWelcomeComplete,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Primary: Login button
        ElevatedButton(
          onPressed: () {
            // Don't mark welcome as seen yet - only mark when user completes auth
            Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const LoginScreen(),
              ),
            );
          },
          style: ElevatedButton.styleFrom(
            backgroundColor: const Color(0xFF2E7D32), // Deep green
            foregroundColor: Colors.white,
            minimumSize: const Size.fromHeight(48),
            padding: const EdgeInsets.symmetric(vertical: 16),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(8),
            ),
            elevation: 2,
          ),
          child: Text(
            l10n.login,
            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
          ),
        ),
        const SizedBox(height: 12),

        // Secondary: Create Account button
        OutlinedButton(
          onPressed: () {
            // Don't mark welcome as seen yet - only mark when user completes auth
            Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const RegisterScreen(),
              ),
            );
          },
          style: OutlinedButton.styleFrom(
            foregroundColor: const Color(0xFF2E7D32),
            minimumSize: const Size.fromHeight(48),
            padding: const EdgeInsets.symmetric(vertical: 16),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(8),
            ),
            side: const BorderSide(color: Color(0xFF2E7D32), width: 2),
          ),
          child: Text(
            l10n.signUp,
            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
          ),
        ),
        const SizedBox(height: 24),

        // Tertiary: Explore as Guest (text link)
        Center(
          child: TextButton(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
            child: const Text(
              'Explore as Guest', // TODO: Add to l10n
              style: TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w500,
                decoration: TextDecoration.underline,
              ),
            ),
          ),
        ),
      ],
    );
  }
}
