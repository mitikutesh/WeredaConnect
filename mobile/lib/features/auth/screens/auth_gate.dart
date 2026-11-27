import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/account/presentation/widgets/finish_deletion_listener.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/widgets/app_lock_gate.dart';
import 'package:wereda_connect/features/auth/widgets/security_prompt_listener.dart';
import 'package:wereda_connect/features/live_trip/presentation/share_consent_listener.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_state.dart';
import 'package:wereda_connect/features/onboarding/screens/welcome_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_return_listener.dart';
import 'package:wereda_connect/screens/main_navigation.dart';
import 'package:wereda_connect/screens/splash_screen.dart';

/// Root routing widget that declaratively switches screens based on auth state.
///
/// Uses BlocBuilder to react to AuthBloc state changes:
/// - AuthInitial → splash screen
/// - AuthLoading → ignored (current screen stays, shows its own spinner)
/// - AuthAuthenticated → MainNavigation, behind the security prompts
///   (step-up password, KYC) raised by background sync. The app lock is
///   [AuthAppLock], mounted above the Navigator.
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
          // The app lock sits above the Navigator (AuthAppLock, mounted
          // from MaterialApp.builder) so it also covers pushed routes.
          // A Telebirr return link that no checkout screen takes (the app
          // was killed while the buyer paid) checks the payment (ESC-44).
          // LT-09: a trucker's first pickup scan of a trip asks whether to
          // share where the truck is. FUP-104: a write refused while the
          // account's deletion is unfinished offers "Finish deleting".
          return const SecurityPromptListener(
            child: FinishDeletionListener(
              child: ShareConsentListener(
                child: PaymentReturnListener(child: MainNavigation()),
              ),
            ),
          );
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
/// The brand before anyone is signed in: a white ground, the black brand
/// tile, the app name and tagline, and an orange progress ring.
class _LoadingSplash extends StatelessWidget {
  const _LoadingSplash();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      backgroundColor: AppTheme.ground,
      body: Center(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 24),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Container(
                width: 72,
                height: 72,
                decoration: BoxDecoration(
                  color: AppTheme.ink,
                  borderRadius: BorderRadius.circular(22),
                ),
                child: const Icon(
                  Icons.agriculture,
                  size: 40,
                  color: Colors.white,
                ),
              ),
              const SizedBox(height: 20),
              Text(
                l10n?.appName ?? 'WeredaConnect',
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.ink,
                  letterSpacing: -0.8,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n?.taglineShort ?? 'Connecting Farmers, Truckers & Buyers',
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.slate,
                  fontWeight: FontWeight.w500,
                ),
              ),
              const SizedBox(height: 40),
              const SizedBox(
                width: 28,
                height: 28,
                child: CircularProgressIndicator(
                  strokeWidth: 3,
                  valueColor: AlwaysStoppedAnimation<Color>(AppTheme.orange),
                  backgroundColor: AppTheme.track,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The app lock for the whole app, mounted from `MaterialApp.builder` so it
/// sits above the Navigator and covers every pushed route.
///
/// Active only while the user is signed in ([AuthAuthenticated]); the login
/// and registration screens are never locked. A restored session (auto-login)
/// locks on start. AuthLoading is ignored so an in-flight auth action does
/// not drop the lock.
class AuthAppLock extends StatelessWidget {
  final Widget child;

  const AuthAppLock({required this.child, super.key});

  @override
  Widget build(BuildContext context) {
    return BlocBuilder<AuthBloc, AuthState>(
      buildWhen: (previous, current) => current is! AuthLoading,
      builder: (context, authState) {
        final signedIn = authState is AuthAuthenticated;
        return AppLockGate(
          active: signedIn,
          lockOnStart: signedIn && authState.isRestoredSession,
          child: child,
        );
      },
    );
  }
}
