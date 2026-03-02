import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_state.dart';
import 'package:wereda_connect/features/onboarding/screens/welcome_screen.dart';
import 'package:wereda_connect/screens/main_navigation.dart';
import 'package:wereda_connect/screens/splash_screen.dart';

/// Root routing widget that declaratively switches screens based on auth state.
///
/// Uses BlocBuilder to react to AuthBloc state changes:
/// - AuthInitial → splash screen
/// - AuthLoading → ignored (current screen stays, shows its own spinner)
/// - AuthAuthenticated → MainNavigation
/// - AuthUnauthenticated / AuthError → Check onboarding state
///   - FirstLaunch OR hasn't seen welcome → WelcomeScreen
///   - ReturningUser → LoginScreen
class AuthGate extends StatefulWidget {
  const AuthGate({super.key});

  @override
  State<AuthGate> createState() => _AuthGateState();
}

class _AuthGateState extends State<AuthGate> {
  @override
  void initState() {
    super.initState();
    // Check first launch status on init
    context.read<OnboardingBloc>().add(const CheckFirstLaunch());
  }

  @override
  Widget build(BuildContext context) {
    return BlocBuilder<AuthBloc, AuthState>(
      // Don't rebuild on AuthLoading — let the current screen handle it
      // with its own inline loading indicator. Without this, emitting
      // AuthLoading during OTP verification would swap the OTP screen for
      // WelcomeScreen/LoginScreen, then flash back on AuthAuthenticated.
      buildWhen: (previous, current) => current is! AuthLoading,
      builder: (context, authState) {
        // If authenticated, mark welcome as seen and go to main app
        if (authState is AuthAuthenticated) {
          // Mark welcome as seen when user successfully authenticates
          context.read<OnboardingBloc>().add(const MarkWelcomeSeen());
          return const MainNavigation();
        }

        // If initial auth check still in progress, show loading splash
        if (authState is AuthInitial) {
          return const _LoadingSplash();
        }

        // AuthUnauthenticated or AuthError — check onboarding state
        return BlocBuilder<OnboardingBloc, OnboardingState>(
          builder: (context, onboardingState) {
            // First launch: show branded splash landing, then welcome screen
            if (onboardingState is FirstLaunch) {
              if (!onboardingState.splashDismissed) {
                return SplashScreen(
                  onGetStarted: () =>
                      context.read<OnboardingBloc>().add(const DismissSplash()),
                );
              }
              return const WelcomeScreen();
            }

            // If onboarding state still initial, show loading splash
            if (onboardingState is OnboardingInitial) {
              return const _LoadingSplash();
            }

            // Returning user - show login directly
            return const LoginScreen();
          },
        );
      },
    );
  }
}

/// Loading splash shown during initial auth check.
///
/// Full-screen dark green background with centered logo,
/// app name, tagline, and gold progress indicator.
class _LoadingSplash extends StatelessWidget {
  const _LoadingSplash();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      backgroundColor: AppTheme.primaryGreen,
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            // Agriculture icon
            const Icon(
              Icons.agriculture,
              size: 80,
              color: Colors.white,
            ),
            const SizedBox(height: 24),
            // App name
            Text(
              l10n?.appName ?? 'WeredaConnect',
              style: const TextStyle(
                fontSize: 28,
                fontWeight: FontWeight.w700,
                color: Colors.white,
                letterSpacing: 0.5,
              ),
            ),
            const SizedBox(height: 8),
            // Tagline
            Text(
              l10n?.taglineShort ?? 'Connecting Farmers, Truckers & Buyers',
              style: TextStyle(
                fontSize: 14,
                color: Colors.white.withAlpha(200),
                fontWeight: FontWeight.w400,
              ),
            ),
            const SizedBox(height: 48),
            // Gold progress indicator
            const SizedBox(
              width: 28,
              height: 28,
              child: CircularProgressIndicator(
                strokeWidth: 3,
                valueColor: AlwaysStoppedAnimation<Color>(AppTheme.accentGold),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
