import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// Reset password screen — collects verification code and new password.
///
/// Drawn in the Join boards' language (no board of its own): square back
/// button, icon tile, title and explanation, labelled fields, the button at
/// the foot.
///
/// Features:
/// - 6-digit verification code input
/// - New password + confirm password fields
/// - Password visibility toggle
/// - On success: shows SnackBar and navigates back to login
/// - All strings from l10n
class ResetPasswordScreen extends StatefulWidget {
  /// Phone number that the verification code was sent to.
  final String phoneNumber;

  const ResetPasswordScreen({required this.phoneNumber, super.key});

  @override
  State<ResetPasswordScreen> createState() => _ResetPasswordScreenState();
}

class _ResetPasswordScreenState extends State<ResetPasswordScreen> {
  final _formKey = GlobalKey<FormState>();
  final _codeController = TextEditingController();
  final _passwordController = TextEditingController();
  final _confirmPasswordController = TextEditingController();

  bool _obscurePassword = true;
  bool _obscureConfirmPassword = true;
  String? _errorMessage;

  @override
  void dispose() {
    _codeController.dispose();
    _passwordController.dispose();
    _confirmPasswordController.dispose();
    super.dispose();
  }

  void _handleResetPassword() {
    setState(() {
      _errorMessage = null;
    });

    if (!_formKey.currentState!.validate()) {
      return;
    }

    context.read<AuthBloc>().add(
          AuthResetPasswordRequested(
            phoneNumber: widget.phoneNumber,
            confirmationCode: _codeController.text.trim(),
            newPassword: _passwordController.text,
          ),
        );
  }

  /// Pushed on mobile: pop. On the web this screen replaced /login.
  void _back() {
    if (Navigator.of(context).canPop()) {
      Navigator.of(context).pop();
    } else if (kIsWeb) {
      context.go('/login');
    }
  }

  Widget _eye({required bool obscured, required VoidCallback onPressed}) {
    return IconButton(
      icon: Icon(obscured ? Icons.visibility_off : Icons.visibility),
      onPressed: onPressed,
      iconSize: 22,
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthPasswordResetSuccess) {
          // Show success message and navigate back to login
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.passwordResetSuccess),
              backgroundColor: AppTheme.statusSuccess,
            ),
          );
          // Return to login. The two platforms navigate differently and each
          // needs its own dismissal:
          // - Web: the forgot/reset screens REPLACE the /login route (GoRouter.go),
          //   so there is no route to pop back to — go('/login') is required.
          // - Mobile: they are pushed as pageless routes ON TOP of the /login
          //   GoRouter page, so go('/login') targets that same anchor and leaves
          //   this screen mounted; popUntil(isFirst) is required to dismiss them.
          if (kIsWeb) {
            context.go('/login');
          } else {
            Navigator.of(context).popUntil((route) => route.isFirst);
          }
        } else if (state is AuthError) {
          setState(() {
            _errorMessage = localizeAuthError(
              l10n,
              state.errorCode,
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
            child: Form(
              key: _formKey,
              child: Builder(
                builder: (context) {
                  final isLoading =
                      context.watch<AuthBloc>().state is AuthLoading;
                  return JoinPage(
                    body: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Align(
                          alignment: AlignmentDirectional.centerStart,
                          child: JoinBackButton(onTap: _back),
                        ),
                        const SizedBox(height: 24),
                        const Align(
                          alignment: AlignmentDirectional.centerStart,
                          child: JoinIconTile(icon: Icons.lock_reset),
                        ),
                        const SizedBox(height: 14),
                        JoinTitle(
                          title: l10n.resetPassword,
                          subtitle: l10n.enterResetCode,
                        ),
                        const SizedBox(height: 26),

                        // Verification code input
                        LabeledField(
                          label: l10n.verificationCode,
                          field: TextFormField(
                            controller: _codeController,
                            decoration: labeledFieldDecoration(),
                            keyboardType: TextInputType.number,
                            inputFormatters: [
                              FilteringTextInputFormatter.digitsOnly,
                              LengthLimitingTextInputFormatter(6),
                            ],
                            validator: (value) {
                              if (value == null || value.isEmpty) {
                                return l10n.codeRequired;
                              }
                              return null;
                            },
                          ),
                        ),
                        const SizedBox(height: 14),

                        // New password input
                        LabeledField(
                          label: l10n.newPassword,
                          field: TextFormField(
                            controller: _passwordController,
                            decoration: labeledFieldDecoration().copyWith(
                              counterText: '',
                              suffixIcon: _eye(
                                obscured: _obscurePassword,
                                onPressed: () => setState(
                                  () => _obscurePassword = !_obscurePassword,
                                ),
                              ),
                            ),
                            obscureText: _obscurePassword,
                            maxLength: 128,
                            validator: (value) {
                              if (value == null ||
                                  value.isEmpty ||
                                  value.length < 8) {
                                return l10n.passwordTooShort;
                              }
                              return null;
                            },
                          ),
                        ),
                        const SizedBox(height: 14),

                        // Confirm new password input
                        LabeledField(
                          label: l10n.confirmNewPassword,
                          field: TextFormField(
                            controller: _confirmPasswordController,
                            decoration: labeledFieldDecoration().copyWith(
                              counterText: '',
                              suffixIcon: _eye(
                                obscured: _obscureConfirmPassword,
                                onPressed: () => setState(
                                  () => _obscureConfirmPassword =
                                      !_obscureConfirmPassword,
                                ),
                              ),
                            ),
                            obscureText: _obscureConfirmPassword,
                            maxLength: 128,
                            validator: (value) {
                              if (value == null ||
                                  value.isEmpty ||
                                  value != _passwordController.text) {
                                return l10n.passwordsDoNotMatch;
                              }
                              return null;
                            },
                          ),
                        ),
                        if (_errorMessage != null) ...[
                          const SizedBox(height: 16),
                          JoinErrorBanner(message: _errorMessage!),
                        ],
                      ],
                    ),
                    footer: SizedBox(
                      height: 52,
                      child: ElevatedButton(
                        onPressed: isLoading ? null : _handleResetPassword,
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
                                  Text(l10n.pleaseWait),
                                ],
                              )
                            : Text(l10n.resetPassword),
                      ),
                    ),
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
  }
}
