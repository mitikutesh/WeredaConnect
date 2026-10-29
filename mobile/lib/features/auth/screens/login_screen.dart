import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/forgot_password_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

/// Login screen with Ethiopian phone input and offline mode support.
///
/// Features:
/// - Language switch (English, Amharic, Afaan Oromoo) inside the form card,
///   on every layout, so the user picks a language before logging in
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

  /// Bumped on every [AuthError], see the error banner.
  int _errorSerial = 0;

  /// Per-field autovalidation, switched on by the first submit (see
  /// [_handleLogin]).
  AutovalidateMode _autovalidateMode = AutovalidateMode.disabled;

  @override
  void dispose() {
    _phoneController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  bool _otpScreenOpen = false;

  Future<void> _openOtpScreen(AuthOtpVerificationRequired state) async {
    _otpScreenOpen = true;
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => OtpVerificationScreen(
          phoneNumber: state.phoneNumber,
          expiresAt: state.expiresAt,
        ),
      ),
    );
    _otpScreenOpen = false;
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
    // Clear previous errors. No live validation while the user types the
    // first time; after the first submit every field the user edits is
    // re-checked, so a corrected field clears its error right away.
    setState(() {
      _errorMessage = null;
      _autovalidateMode = AutovalidateMode.onUserInteraction;
    });

    // Validate form
    if (!_formKey.currentState!.validate()) {
      return;
    }

    // Normalize phone to canonical E.164 before dispatching
    final normalizedPhone =
        normalizeEthiopianPhone(_phoneController.text.trim());
    if (normalizedPhone == null) {
      // Unreachable when the validator passed; guard instead of asserting
      // so a refactor can never dispatch an unnormalized phone.
      return;
    }
    context.read<AuthBloc>().add(
          AuthLoginRequested(
            phoneNumber: normalizedPhone,
            password: _passwordController.text,
          ),
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
          // Close the OTP screen this login opened, if any.
          if (_otpScreenOpen && Navigator.of(context).canPop()) {
            _otpScreenOpen = false;
            Navigator.of(context).pop();
          }
          // Pop self if pushed on top of AuthGate (e.g. from WelcomeScreen).
          // AuthGate will then show MainNavigation.
          if (Navigator.of(context).canPop()) {
            Navigator.of(context).pop();
          }
        } else if (state is AuthOtpVerificationRequired) {
          // Login for an account whose phone was never confirmed: the bloc
          // already sent a fresh code. Only the topmost screen opens the OTP
          // screen (registration handles its own).
          if (!_otpScreenOpen && (ModalRoute.of(context)?.isCurrent ?? true)) {
            _openOtpScreen(state);
          }
        } else if (state is AuthError) {
          setState(() {
            _errorSerial++;
            _errorMessage = _localizeAuthError(
              state.errorCode,
              state.errorMessage,
              retryAfterSeconds: state.retryAfterSeconds,
            );
          });
        }
      },
      child: Scaffold(
        backgroundColor: AppTheme.chalkBackground,
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
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 440),
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 36),
          child: _buildLoginForm(context, l10n, theme),
        ),
      ),
    );
  }

  // ── Tablet: centered card layout ────────────────────────────────────

  Widget _buildTabletLayout(
    BuildContext context,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    return Center(
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
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 24),
          child: _buildLoginForm(context, l10n, theme),
        ),
      ),
    );
  }

  // ── Shared login form (used by all layouts) ─────────────────────────

  static Widget _roleBadge(String label, Color bg, Color fg) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w700,
          color: fg,
        ),
      ),
    );
  }

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
              // Top branding & offline badge
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
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
                        l10n.appName,
                        style: const TextStyle(
                          fontSize: 17,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.3,
                          color: AppTheme.soilDark,
                        ),
                      ),
                    ],
                  ),
                  Container(
                    height: 30,
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                    decoration: BoxDecoration(
                      color: AppTheme.fieldGreenTint,
                      borderRadius: BorderRadius.circular(999),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const Icon(
                          Icons.check,
                          size: 14,
                          color: AppTheme.fieldGreen,
                        ),
                        const SizedBox(width: 4),
                        Text(
                          l10n.offlineReady,
                          style: const TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.fieldGreen,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 24),

              // Title and subtitle matching Welcome.dc.html
              Text(
                l10n.welcomeMessage,
                style: const TextStyle(
                  fontSize: 30,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                  letterSpacing: -0.9,
                  height: 1.1,
                ),
              ),
              const SizedBox(height: 10),
              Text(
                l10n.aboutTagline,
                style: const TextStyle(
                  fontSize: 15,
                  color: AppTheme.mutedSteel,
                ),
              ),
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                children: [
                  _roleBadge(
                    l10n.farmer,
                    const Color(0xFFE3EEE6),
                    AppTheme.fieldGreen,
                  ),
                  _roleBadge(
                    l10n.trucker,
                    const Color(0xFFFAEED8),
                    const Color(0xFF8A5410),
                  ),
                  _roleBadge(
                    l10n.buyer,
                    const Color(0xFFE6ECF5),
                    const Color(0xFF2E4C7D),
                  ),
                ],
              ),
              const SizedBox(height: 20),

              // White Card for inputs
              Container(
                padding: const EdgeInsets.all(20),
                decoration: BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.circular(24),
                  border: Border.all(color: AppTheme.whisperBorder),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // Language first: the user may not read English yet.
                    const LanguageSegmentedPicker(),
                    const SizedBox(height: 16),

                    // Phone number input
                    TextFormField(
                      controller: _phoneController,
                      autovalidateMode: _autovalidateMode,
                      decoration: InputDecoration(
                        labelText: l10n.phoneNumber,
                        prefixIcon: const Icon(Icons.phone_outlined),
                        hintText: l10n.ethiopianPhoneExample,
                        border: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide:
                              const BorderSide(color: Color(0xFFD6D3C8)),
                        ),
                        enabledBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide:
                              const BorderSide(color: Color(0xFFD6D3C8)),
                        ),
                        focusedBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide: const BorderSide(
                            color: AppTheme.fieldGreen,
                            width: 2,
                          ),
                        ),
                        contentPadding: const EdgeInsets.symmetric(
                          horizontal: 16,
                          vertical: 16,
                        ),
                        errorMaxLines: 10,
                      ),
                      keyboardType: TextInputType.phone,
                      inputFormatters: [
                        FilteringTextInputFormatter.allow(RegExp(r'[+\d]')),
                      ],
                      validator: (value) {
                        if (value == null || value.isEmpty) {
                          return l10n.phoneNumberRequired;
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
                      autovalidateMode: _autovalidateMode,
                      decoration: InputDecoration(
                        labelText: l10n.password,
                        prefixIcon: const Icon(Icons.lock_outline),
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
                          iconSize: 22,
                        ),
                        border: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide:
                              const BorderSide(color: Color(0xFFD6D3C8)),
                        ),
                        enabledBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide:
                              const BorderSide(color: Color(0xFFD6D3C8)),
                        ),
                        focusedBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(14),
                          borderSide: const BorderSide(
                            color: AppTheme.fieldGreen,
                            width: 2,
                          ),
                        ),
                        contentPadding: const EdgeInsets.symmetric(
                          horizontal: 16,
                          vertical: 16,
                        ),
                        errorMaxLines: 10,
                      ),
                      obscureText: _obscurePassword,
                      maxLength: 128,
                      validator: (value) {
                        if (value == null || value.isEmpty) {
                          return l10n.fieldRequired;
                        }
                        return null;
                      },
                    ),

                    // Forgot password link
                    Align(
                      alignment: Alignment.centerRight,
                      child: TextButton(
                        onPressed: () {
                          final phone = _phoneController.text.trim();
                          if (kIsWeb) {
                            final query = phone.isNotEmpty
                                ? '?phone=${Uri.encodeComponent(phone)}'
                                : '';
                            GoRouter.of(context).go('/forgot-password$query');
                          } else {
                            Navigator.of(context).push(
                              MaterialPageRoute<void>(
                                builder: (_) => ForgotPasswordScreen(
                                  initialPhoneNumber: phone,
                                ),
                              ),
                            );
                          }
                        },
                        child: Text(
                          l10n.forgotPassword,
                          style: const TextStyle(
                            color: AppTheme.fieldGreen,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 12),

                    // Error banner if any
                    if (_errorMessage != null)
                      KeyedSubtree(
                        key: ValueKey<int>(_errorSerial),
                        child: Semantics(
                          key: const Key('login_error_banner'),
                          container: true,
                          liveRegion: true,
                          child: Container(
                            padding: const EdgeInsets.all(12),
                            margin: const EdgeInsets.only(bottom: 16),
                            decoration: BoxDecoration(
                              color: const Color(0xFFFDF2F2),
                              border: Border.all(
                                color: const Color(0xFFF8B4B4),
                              ),
                              borderRadius: BorderRadius.circular(12),
                            ),
                            child: Row(
                              children: [
                                const Icon(
                                  Icons.error_outline,
                                  color: AppTheme.statusError,
                                  size: 20,
                                ),
                                const SizedBox(width: 10),
                                Expanded(
                                  child: Text(
                                    _errorMessage!,
                                    style: const TextStyle(
                                      color: AppTheme.statusError,
                                      fontSize: 13,
                                      fontWeight: FontWeight.w600,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),

                    // Login button
                    SizedBox(
                      height: 52,
                      child: ElevatedButton(
                        onPressed: isLoading ? null : _handleLogin,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: AppTheme.fieldGreen,
                          foregroundColor: Colors.white,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(16),
                          ),
                          elevation: 0,
                        ),
                        child: isLoading
                            ? const SizedBox(
                                height: 20,
                                width: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  valueColor: AlwaysStoppedAnimation<Color>(
                                    Colors.white,
                                  ),
                                ),
                              )
                            : Text(
                                l10n.login,
                                style: const TextStyle(
                                  fontSize: 16,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                      ),
                    ),
                  ],
                ),
              ),

              const SizedBox(height: 16),

              // Register button
              SizedBox(
                height: 48,
                child: OutlinedButton(
                  onPressed: () {
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => const RegisterScreen(),
                      ),
                    );
                  },
                  style: OutlinedButton.styleFrom(
                    backgroundColor: Colors.white,
                    side: const BorderSide(color: Color(0xFFD6D3C8)),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(16),
                    ),
                  ),
                  child: Text(
                    l10n.loginNoAccountRegister,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.soilDark,
                    ),
                  ),
                ),
              ),

              const SizedBox(height: 14),
              Text(
                '${l10n.loginEscrowFooter} · ${l10n.telebirrPayment}',
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.mutedSteel,
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}
