import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/screens/main_navigation.dart';

/// Root routing widget that declaratively switches screens based on auth state.
///
/// Uses BlocBuilder to react to AuthBloc state changes:
/// - AuthInitial / AuthLoading → splash screen
/// - AuthAuthenticated → MainNavigation
/// - AuthOtpVerificationRequired → OtpVerificationScreen
/// - AuthUnauthenticated / AuthError → LoginScreen
class AuthGate extends StatelessWidget {
  const AuthGate({super.key});

  @override
  Widget build(BuildContext context) {
    return BlocBuilder<AuthBloc, AuthState>(
      builder: (context, state) {
        if (state is AuthAuthenticated) {
          return const MainNavigation();
        }

        if (state is AuthOtpVerificationRequired) {
          return OtpVerificationScreen(phoneNumber: state.phoneNumber);
        }

        if (state is AuthInitial || state is AuthLoading) {
          return const _SplashScreen();
        }

        // AuthUnauthenticated or AuthError
        return const LoginScreen();
      },
    );
  }
}

/// Splash screen shown during initial auth check.
class _SplashScreen extends StatelessWidget {
  const _SplashScreen();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.agriculture,
              size: 96,
              color: theme.primaryColor,
            ),
            const SizedBox(height: 24),
            Text(
              'WeredaConnect',
              style: theme.textTheme.headlineMedium?.copyWith(
                fontWeight: FontWeight.bold,
                color: theme.primaryColor,
              ),
            ),
            const SizedBox(height: 32),
            const CircularProgressIndicator(),
          ],
        ),
      ),
    );
  }
}
