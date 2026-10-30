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
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

/// The Join board "Welcome and login" (specs/design-canvas/android/Welcome):
/// wordmark and Offline Ready pill, the welcome title and the three role
/// pills, then a white card with the language switch, the phone and
/// password fields and Login; Register and the escrow line sit at the foot.
///
/// One composition at every width: on a wide window the phone column is
/// centred on the chalk ground instead of a separate desktop layout.
///
/// Features:
/// - Language switch (English, Amharic, Afaan Oromoo) inside the form card,
///   so the user picks a language before logging in
/// - Ethiopian phone number input (accepts +251, 00251, or 0 prefix)
/// - Password input with visibility toggle
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

  void _openForgotPassword() {
    final phone = _phoneController.text.trim();
    if (kIsWeb) {
      final query =
          phone.isNotEmpty ? '?phone=${Uri.encodeComponent(phone)}' : '';
      GoRouter.of(context).go('/forgot-password$query');
    } else {
      Navigator.of(context).push(
        MaterialPageRoute<void>(
          builder: (_) => ForgotPasswordScreen(initialPhoneNumber: phone),
        ),
      );
    }
  }

  void _openRegister() {
    Navigator.of(context).push(
      MaterialPageRoute<void>(builder: (_) => const RegisterScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthAuthenticated) {
          // Close the OTP screen this login opened, if any.
          if (_otpScreenOpen && Navigator.of(context).canPop()) {
            _otpScreenOpen = false;
            Navigator.of(context).pop();
          }
          // Pop self if pushed on top of AuthGate. AuthGate will then show
          // MainNavigation.
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
        body: SafeArea(
          child: FwContentWidth(
            maxWidth: 480,
            child: JoinPage(
              padding: const EdgeInsets.fromLTRB(20, 24, 20, 28),
              body: _buildLoginForm(context, l10n),
              footer: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  SizedBox(
                    height: 48,
                    child: OutlinedButton(
                      onPressed: _openRegister,
                      style: OutlinedButton.styleFrom(
                        minimumSize: const Size.fromHeight(48),
                        side: const BorderSide(color: Color(0xFFD6D3C8)),
                      ),
                      child: Text(
                        l10n.loginNoAccountRegister,
                        textAlign: TextAlign.center,
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
              ),
            ),
          ),
        ),
      ),
    );
  }

  static Widget _roleBadge(String label, Color bg, Color fg) {
    return Container(
      height: 30,
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(999),
      ),
      // A min-width row: the pill hugs its label inside the Wrap.
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            label,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: fg,
            ),
          ),
        ],
      ),
    );
  }

  /// The board's input: a bold label above a 48 tall field, no icon inside.
  InputDecoration _fieldDecoration() => labeledFieldDecoration().copyWith(
        errorMaxLines: 10,
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      );

  Widget _buildLoginForm(BuildContext context, AppLocalizations l10n) {
    return Form(
      key: _formKey,
      child: Builder(
        builder: (context) {
          final isLoading = context.watch<AuthBloc>().state is AuthLoading;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Wordmark and the Offline Ready pill.
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Flexible(
                    child: Row(
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
                        Flexible(
                          child: Text(
                            l10n.appName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(
                              fontSize: 17,
                              fontWeight: FontWeight.w800,
                              letterSpacing: -0.3,
                              color: AppTheme.soilDark,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  JoinStatusPill(label: l10n.offlineReady),
                ],
              ),
              const SizedBox(height: 28),

              // Title, tagline and the role pills.
              Semantics(
                header: true,
                child: Text(
                  l10n.welcomeMessage,
                  style: const TextStyle(
                    fontSize: 30,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                    letterSpacing: -0.9,
                    height: 1.1,
                  ),
                ),
              ),
              const SizedBox(height: 10),
              Text(l10n.aboutTagline, style: joinSubtitleStyle),
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  _roleBadge(
                    l10n.farmer,
                    AppTheme.fieldGreenTint,
                    AppTheme.fieldGreen,
                  ),
                  _roleBadge(
                    l10n.trucker,
                    AppTheme.actionCream,
                    AppTheme.money,
                  ),
                  _roleBadge(
                    l10n.buyer,
                    AppTheme.dropoffBlueTint,
                    AppTheme.dropoffBlue,
                  ),
                ],
              ),
              const SizedBox(height: 24),

              // The white form card.
              Container(
                padding: const EdgeInsets.all(20),
                decoration: BoxDecoration(
                  color: AppTheme.surfaceWhite,
                  borderRadius: BorderRadius.circular(24),
                  border: Border.all(color: AppTheme.whisperBorder),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // Language first: the user may not read English yet.
                    const LanguageSegmentedPicker(),
                    const SizedBox(height: 14),

                    LabeledField(
                      label: l10n.phoneNumber,
                      field: TextFormField(
                        controller: _phoneController,
                        autovalidateMode: _autovalidateMode,
                        decoration: _fieldDecoration(),
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
                    ),
                    // The board's example line under the field: its own
                    // text, outside the field's semantics and not a helper,
                    // so a corrected field drops its error at once.
                    const SizedBox(height: 6),
                    Text(
                      l10n.ethiopianPhoneExample,
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                    const SizedBox(height: 14),

                    LabeledField(
                      label: l10n.password,
                      field: TextFormField(
                        controller: _passwordController,
                        autovalidateMode: _autovalidateMode,
                        decoration: _fieldDecoration().copyWith(
                          counterText: '',
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
                    ),
                    const SizedBox(height: 2),

                    // Forgot password link, right aligned as on the board.
                    Align(
                      alignment: AlignmentDirectional.centerEnd,
                      child: TextButton(
                        onPressed: _openForgotPassword,
                        style: TextButton.styleFrom(
                          minimumSize: const Size(0, 32),
                          padding: const EdgeInsets.symmetric(horizontal: 4),
                          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                        ),
                        child: Text(
                          l10n.forgotPassword,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.fieldGreen,
                            decoration: TextDecoration.underline,
                            decorationColor: AppTheme.fieldGreen,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),

                    // Error banner if any
                    if (_errorMessage != null)
                      KeyedSubtree(
                        key: ValueKey<int>(_errorSerial),
                        child: Padding(
                          padding: const EdgeInsets.only(bottom: 16),
                          child: JoinErrorBanner(
                            key: const Key('login_error_banner'),
                            message: _errorMessage!,
                          ),
                        ),
                      ),

                    // Login button (the theme's primary: 52 tall, radius 16).
                    SizedBox(
                      height: 52,
                      child: ElevatedButton(
                        onPressed: isLoading ? null : _handleLogin,
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
                            : Text(l10n.login),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}
