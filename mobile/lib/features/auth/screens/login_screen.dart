import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Login screen with Ethiopian phone input and offline mode support.
///
/// Features:
/// - Ethiopian phone number input (+251 prefix auto-filled)
/// - Password input with visibility toggle
/// - Offline mode indicator
/// - Large touch targets (48dp minimum per Material Design)
/// - Localized error messages (Amharic/Oromo/English)
///
/// Ethiopian context:
/// - Phone number format: +251XXXXXXXXX (9 digits after +251)
/// - Simple error messages for low-literacy users
/// - Visual feedback (colors, icons) more than text
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _phoneController = TextEditingController(text: '+251');
  final _passwordController = TextEditingController();

  bool _obscurePassword = true;
  String? _errorMessage;

  @override
  void dispose() {
    _phoneController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  String _formatDuration(int seconds) {
    final l10n = AppLocalizations.of(context)!;
    if (seconds >= 3600) {
      return l10n.durationHours((seconds / 3600).ceil());
    } else if (seconds >= 60) {
      return l10n.durationMinutes((seconds / 60).ceil());
    } else {
      return l10n.durationSeconds(seconds);
    }
  }

  String _localizeAuthError(
    String errorCode,
    String fallback, {
    int? retryAfterSeconds,
  }) {
    final l10n = AppLocalizations.of(context)!;
    if (errorCode == 'AUTH_015' && retryAfterSeconds != null) {
      return l10n.authAccountLocked(_formatDuration(retryAfterSeconds));
    }
    return switch (errorCode) {
      AuthErrorCode.invalidPhoneFormat => l10n.invalidPhoneFormat,
      AuthErrorCode.invalidPassword => l10n.passwordRequirements,
      AuthErrorCode.invalidCredentials => l10n.invalidCredentials,
      AuthErrorCode.userNotConfirmed => l10n.authErrorUserNotConfirmed,
      AuthErrorCode.userNotFound => l10n.invalidCredentials,
      AuthErrorCode.accountLocked => l10n.authAccountDisabled,
      AuthErrorCode.rateLimitExceeded => l10n.authRateLimitExceeded,
      AuthErrorCode.internalServerError => l10n.authServerError,
      AuthErrorCode.cognitoUnavailable => l10n.authServiceUnavailable,
      AuthErrorCode.networkError => l10n.authNetworkError,
      AuthErrorCode.apiNotConfigured => l10n.authApiNotConfigured,
      AuthErrorCode.offlineNotAvailable => l10n.authOfflineNotAvailable,
      AuthErrorCode.refreshTokenExpired => l10n.authErrorTokenExpired,
      AuthErrorCode.refreshTokenRevoked => l10n.authErrorTokenExpired,
      AuthErrorCode.invalidToken => l10n.authErrorTokenExpired,
      AuthErrorCode.secureStorageError => l10n.authErrorSecureStorage,
      AuthErrorCode.databaseError => l10n.authErrorDatabase,
      _ => l10n.genericError,
    };
  }

  void _handleLogin() {
    // Clear previous errors
    setState(() {
      _errorMessage = null;
    });

    // Validate form
    if (!_formKey.currentState!.validate()) {
      return;
    }

    // Dispatch login event via BLoC
    context.read<AuthBloc>().add(
          AuthLoginRequested(
            phoneNumber: _phoneController.text.trim(),
            password: _passwordController.text,
          ),
        );
  }

  void _showLanguageSelectionDialog(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (context) => const LanguageSelectionDialog(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthAuthenticated) {
          // Pop self if pushed on top of AuthGate (e.g. from WelcomeScreen).
          // AuthGate will then show MainNavigation.
          if (Navigator.of(context).canPop()) {
            Navigator.of(context).pop();
          }
        } else if (state is AuthError) {
          setState(() {
            _errorMessage = _localizeAuthError(
              state.errorCode,
              state.errorMessage,
              retryAfterSeconds: state.retryAfterSeconds,
            );
          });
        }
      },
      child: Scaffold(
        appBar: AppBar(
          leading: IconButton(
            icon: const Icon(Icons.arrow_back),
            onPressed: () {
              // Cancel any in-flight login before navigating away
              if (context.read<AuthBloc>().state is AuthLoading) {
                context.read<AuthBloc>().add(const AuthCancelRequested());
              }
              if (Navigator.of(context).canPop()) {
                // Pushed as a route (e.g. from WelcomeScreen) — pop back
                Navigator.of(context).pop();
              } else {
                // Rendered inline by AuthGate — tell AuthGate to show WelcomeScreen
                context.read<OnboardingBloc>().add(const ShowWelcomeScreen());
              }
            },
            tooltip: l10n.backToHome,
          ),
          title: Text(l10n.login),
          elevation: 0,
          actions: [
            IconButton(
              icon: const Icon(Icons.language),
              onPressed: () => _showLanguageSelectionDialog(context),
              tooltip: l10n.language,
            ),
          ],
        ),
        body: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24.0),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 480),
                child: Form(
                  key: _formKey,
                  child: Builder(
                    builder: (context) {
                      final isLoading =
                          context.watch<AuthBloc>().state is AuthLoading;

                      return Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          // App logo/icon
                          const SizedBox(height: 32),
                          Icon(
                            Icons.agriculture,
                            size: 80,
                            color: theme.colorScheme.primary,
                          ),
                          const SizedBox(height: 16),

                          // Welcome message
                          Text(
                            l10n.welcomeMessage,
                            style: theme.textTheme.headlineSmall?.copyWith(
                              fontWeight: FontWeight.bold,
                            ),
                            textAlign: TextAlign.center,
                          ),
                          const SizedBox(height: 48),

                          // Phone number input
                          TextFormField(
                            controller: _phoneController,
                            decoration: InputDecoration(
                              labelText: l10n.phoneNumber,
                              prefixIcon: const Icon(Icons.phone),
                              hintText: l10n.ethiopianPhoneExample,
                              border: const OutlineInputBorder(),
                              // Large touch target
                              contentPadding: const EdgeInsets.symmetric(
                                horizontal: 16,
                                vertical: 20,
                              ),
                            ),
                            keyboardType: TextInputType.phone,
                            inputFormatters: [
                              // Ensure +251 prefix is always present
                              FilteringTextInputFormatter.allow(
                                  RegExp(r'[+\d]')),
                            ],
                            validator: (value) {
                              if (value == null || value.isEmpty) {
                                return l10n.phoneNumber;
                              }
                              // Ethiopian phone format: +251XXXXXXXXX
                              if (!RegExp(r'^\+251\d{9}$').hasMatch(value)) {
                                return l10n.invalidPhoneFormat;
                              }
                              return null;
                            },
                          ),
                          const SizedBox(height: 16),

                          // Password input
                          TextFormField(
                            controller: _passwordController,
                            decoration: InputDecoration(
                              labelText: l10n.password,
                              prefixIcon: const Icon(Icons.lock),
                              suffixIcon: IconButton(
                                icon: Icon(
                                  _obscurePassword
                                      ? Icons.visibility_off
                                      : Icons.visibility,
                                ),
                                onPressed: () {
                                  setState(() {
                                    _obscurePassword = !_obscurePassword;
                                  });
                                },
                                // Large touch target
                                iconSize: 24,
                                padding: const EdgeInsets.all(12),
                              ),
                              border: const OutlineInputBorder(),
                              contentPadding: const EdgeInsets.symmetric(
                                horizontal: 16,
                                vertical: 20,
                              ),
                            ),
                            obscureText: _obscurePassword,
                            maxLength: 128,
                            validator: (value) {
                              if (value == null || value.isEmpty) {
                                return l10n.password;
                              }
                              if (value.length < 8) {
                                return l10n.passwordRequirements;
                              }
                              return null;
                            },
                          ),
                          const SizedBox(height: 8),

                          // Forgot password link
                          Align(
                            alignment: Alignment.centerRight,
                            child: TextButton(
                              onPressed: () {
                                showDialog<void>(
                                  context: context,
                                  builder: (context) {
                                    final l10n = AppLocalizations.of(context)!;
                                    return AlertDialog(
                                      title: Text(l10n.forgotPassword),
                                      content: Text(
                                        l10n.forgotPasswordContactSupport,
                                      ),
                                      actions: [
                                        TextButton(
                                          onPressed: () =>
                                              Navigator.pop(context),
                                          child: Text(l10n.ok),
                                        ),
                                      ],
                                    );
                                  },
                                );
                              },
                              child: Text(l10n.forgotPassword),
                            ),
                          ),
                          const SizedBox(height: 24),

                          // Error message
                          if (_errorMessage != null)
                            Container(
                              padding: const EdgeInsets.all(12),
                              margin: const EdgeInsets.only(bottom: 16),
                              decoration: BoxDecoration(
                                color: theme.colorScheme.errorContainer,
                                border: Border.all(
                                  color: AppTheme.statusError.withAlpha(80),
                                ),
                                borderRadius: BorderRadius.circular(12),
                              ),
                              child: Row(
                                children: [
                                  Icon(
                                    Icons.error_outline,
                                    color: theme.colorScheme.error,
                                  ),
                                  const SizedBox(width: 12),
                                  Expanded(
                                    child: Text(
                                      _errorMessage!,
                                      style: TextStyle(
                                        color: theme.colorScheme.error,
                                      ),
                                    ),
                                  ),
                                ],
                              ),
                            ),

                          // Login button
                          ElevatedButton(
                            onPressed: isLoading ? null : _handleLogin,
                            style: ElevatedButton.styleFrom(
                              padding: const EdgeInsets.symmetric(vertical: 16),
                              minimumSize: const Size.fromHeight(
                                  48), // Large touch target
                              shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(12),
                              ),
                            ),
                            child: isLoading
                                ? Row(
                                    mainAxisAlignment: MainAxisAlignment.center,
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      const SizedBox(
                                        height: 20,
                                        width: 20,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2,
                                          valueColor:
                                              AlwaysStoppedAnimation<Color>(
                                            Colors.white,
                                          ),
                                        ),
                                      ),
                                      const SizedBox(width: 12),
                                      Text(
                                        l10n.pleaseWait,
                                        style: const TextStyle(fontSize: 16),
                                      ),
                                    ],
                                  )
                                : Text(
                                    l10n.login,
                                    style: const TextStyle(fontSize: 16),
                                  ),
                          ),
                          const SizedBox(height: 24),

                          // Register link
                          Row(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              Text(l10n.dontHaveAccount),
                              TextButton(
                                onPressed: () {
                                  Navigator.of(context).push(
                                    MaterialPageRoute<void>(
                                      builder: (_) => const RegisterScreen(),
                                    ),
                                  );
                                },
                                child: Text(l10n.register),
                              ),
                            ],
                          ),
                        ],
                      );
                    },
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
