import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_state.dart';
import 'package:wereda_connect/features/onboarding/screens/welcome_screen.dart';
import 'package:wereda_connect/screens/main_navigation.dart';

/// Root routing widget that declaratively switches screens based on auth state.
///
/// Uses BlocBuilder to react to AuthBloc state changes:
/// - AuthInitial → splash screen
/// - AuthLoading → ignored (current screen stays, shows its own spinner)
/// - AuthAuthenticated → MainNavigation
/// - AuthOtpVerificationRequired → OtpVerificationScreen
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

        // If OTP verification required, show OTP screen
        if (authState is AuthOtpVerificationRequired) {
          return OtpVerificationScreen(phoneNumber: authState.phoneNumber);
        }

        // If initial auth check still in progress, show splash
        if (authState is AuthInitial) {
          return const _SplashScreen();
        }

        // AuthUnauthenticated or AuthError — check onboarding state
        return BlocBuilder<OnboardingBloc, OnboardingState>(
          builder: (context, onboardingState) {
            // If first launch, show welcome screen
            if (onboardingState is FirstLaunch) {
              return const WelcomeScreen();
            }

            // If onboarding state still initial, show splash while checking
            if (onboardingState is OnboardingInitial) {
              return const _SplashScreen();
            }

            // Returning user - show login directly
            return const LoginScreen();
          },
        );
      },
    );
  }
}

/// Branded splash screen shown during initial auth check.
///
/// Full-screen dark green background with centered logo,
/// app name, tagline, and gold progress indicator.
class _SplashScreen extends StatelessWidget {
  const _SplashScreen();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      backgroundColor: AppTheme.primaryGreen,
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            // Logo icon
            Container(
              width: 100,
              height: 100,
              decoration: BoxDecoration(
                color: Colors.white.withAlpha(30),
                shape: BoxShape.circle,
              ),
              child: const Icon(
                Icons.agriculture,
                size: 56,
                color: Colors.white,
              ),
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
