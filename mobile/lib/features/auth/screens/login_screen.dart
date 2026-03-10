import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Login screen with Ethiopian phone input and offline mode support.
///
/// Features:
/// - Ethiopian phone number input (accepts +251, 00251, or 0 prefix)
/// - Password input with visibility toggle
/// - Offline mode indicator
/// - Large touch targets (48dp minimum per Material Design)
/// - Localized error messages (Amharic/Oromo/English)
///
/// Ethiopian context:
/// - Phone number formats: +251XXXXXXXXX, 00251XXXXXXXXX, 0XXXXXXXXX
/// - Simple error messages for low-literacy users
/// - Visual feedback (colors, icons) more than text
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _phoneController = TextEditingController();
  final _passwordController = TextEditingController();

  bool _obscurePassword = true;
  String? _errorMessage;

  @override
  void dispose() {
    _phoneController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  String _localizeAuthError(
    String errorCode,
    String fallback, {
    int? retryAfterSeconds,
  }) {
    final l10n = AppLocalizations.of(context)!;
    return localizeAuthError(
      l10n,
      errorCode,
      retryAfterSeconds: retryAfterSeconds,
    );
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

    // Normalize phone to canonical E.164 before dispatching
    final normalizedPhone =
        normalizeEthiopianPhone(_phoneController.text.trim());
    context.read<AuthBloc>().add(
          AuthLoginRequested(
            phoneNumber: normalizedPhone!,
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
    final windowClass = Breakpoints.of(context);

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
        // Show AppBar only on mobile (non-web) — desktop/tablet use inline controls
        appBar: windowClass == WindowClass.mobile && !kIsWeb
            ? AppBar(
                leading: IconButton(
                  icon: const Icon(Icons.arrow_back),
                  onPressed: () => _navigateBack(context, l10n),
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
              )
            : null,
        body: SafeArea(
          child: switch (windowClass) {
            WindowClass.desktop => _buildDesktopLayout(context, l10n, theme),
            WindowClass.tablet => _buildTabletLayout(context, l10n, theme),
            WindowClass.mobile => _buildMobileLayout(context, l10n, theme),
          },
        ),
      ),
    );
  }

  void _navigateBack(BuildContext context, AppLocalizations l10n) {
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
  }

  // ── Desktop: split-screen with hero panel left, form right ──────────

  Widget _buildDesktopLayout(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    return Row(
      children: [
        // Left hero/branding panel (45%)
        Expanded(
          flex: 45,
          child: _LoginHeroPanel(l10n: l10n),
        ),
        // Right form panel (55%)
        Expanded(
          flex: 55,
          child: Stack(
            children: [
              Container(
                color: AppTheme.surfaceWhite,
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 420),
                    child: SingleChildScrollView(
                      padding: const EdgeInsets.all(48.0),
                      child: _buildLoginForm(context, l10n, theme),
                    ),
                  ),
                ),
              ),
              // Language selector floating top-right
              Positioned(
                top: 16,
                right: 24,
                child: _LanguageButton(
                  onPressed: () => _showLanguageSelectionDialog(context),
                  tooltip: l10n.language,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  // ── Tablet: centered card layout ────────────────────────────────────

  Widget _buildTabletLayout(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    return Stack(
      children: [
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(32.0),
              child: Card(
                elevation: 4,
                shadowColor: Colors.black26,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(16),
                ),
                child: Padding(
                  padding: const EdgeInsets.all(36.0),
                  child: _buildLoginForm(context, l10n, theme),
                ),
              ),
            ),
          ),
        ),
        // Language selector floating top-right
        Positioned(
          top: 16,
          right: 24,
          child: _LanguageButton(
            onPressed: () => _showLanguageSelectionDialog(context),
            tooltip: l10n.language,
          ),
        ),
      ],
    );
  }

  // ── Mobile: original layout (no AppBar back button on web) ──────────

  Widget _buildMobileLayout(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24.0),
          child: _buildLoginForm(context, l10n, theme),
        ),
      ),
    );
  }

  // ── Shared login form (used by all layouts) ─────────────────────────

  Widget _buildLoginForm(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    return Form(
      key: _formKey,
      child: Builder(
        builder: (context) {
          final isLoading = context.watch<AuthBloc>().state is AuthLoading;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // App logo/icon
              const SizedBox(height: 16),
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
                  FilteringTextInputFormatter.allow(RegExp(r'[+\d]')),
                ],
                validator: (value) {
                  if (value == null || value.isEmpty) {
                    return l10n.phoneNumber;
                  }
                  if (!isValidEthiopianPhone(value)) {
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
                              onPressed: () => Navigator.pop(context),
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
                  minimumSize: const Size.fromHeight(48), // Large touch target
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
                              valueColor: AlwaysStoppedAnimation<Color>(
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
    );
  }
}

// ── Desktop hero/branding panel ─────────────────────────────────────────

/// Left-side branding panel shown on desktop login layout.
class _LoginHeroPanel extends StatelessWidget {
  final AppLocalizations l10n;

  const _LoginHeroPanel({required this.l10n});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            AppTheme.primaryGreenDark,
            AppTheme.primaryGreen,
          ],
        ),
      ),
      child: Center(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 48.0),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const Icon(
                Icons.agriculture,
                size: 100,
                color: Colors.white,
              ),
              const SizedBox(height: 24),
              const Text(
                'WeredaConnect',
                style: TextStyle(
                  fontSize: 32,
                  fontWeight: FontWeight.bold,
                  color: Colors.white,
                  letterSpacing: 0.5,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 12),
              Text(
                l10n.aboutTagline,
                style: TextStyle(
                  fontSize: 16,
                  color: Colors.white.withAlpha(200),
                  height: 1.5,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 48),
              // Feature icons row
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  _HeroFeatureIcon(
                    icon: Icons.cloud_off,
                    label: l10n.offlineReady,
                  ),
                  const SizedBox(width: 32),
                  _HeroFeatureIcon(
                    icon: Icons.lock_outline,
                    label: l10n.telebirrPayment,
                  ),
                  const SizedBox(width: 32),
                  _HeroFeatureIcon(
                    icon: Icons.translate,
                    label: l10n.language,
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Small feature icon with label for the hero panel.
class _HeroFeatureIcon extends StatelessWidget {
  final IconData icon;
  final String label;

  const _HeroFeatureIcon({required this.icon, required this.label});

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: Colors.white.withAlpha(30),
            shape: BoxShape.circle,
          ),
          child: Icon(icon, color: Colors.white, size: 24),
        ),
        const SizedBox(height: 8),
        SizedBox(
          width: 80,
          child: Text(
            label,
            style: TextStyle(
              fontSize: 12,
              color: Colors.white.withAlpha(200),
            ),
            textAlign: TextAlign.center,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
          ),
        ),
      ],
    );
  }
}

/// Floating language button for desktop/tablet layouts.
class _LanguageButton extends StatelessWidget {
  final VoidCallback onPressed;
  final String tooltip;

  const _LanguageButton({required this.onPressed, required this.tooltip});

  @override
  Widget build(BuildContext context) {
    return Material(
      elevation: 2,
      borderRadius: BorderRadius.circular(24),
      color: AppTheme.surfaceWhite,
      child: InkWell(
        onTap: onPressed,
        borderRadius: BorderRadius.circular(24),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(
                Icons.language,
                size: 20,
                color: AppTheme.primaryGreen,
              ),
              const SizedBox(width: 8),
              Text(
                tooltip,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.primaryGreen,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
