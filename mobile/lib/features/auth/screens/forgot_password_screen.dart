import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/reset_password_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// Forgot password screen — collects phone number and requests reset code.
///
/// Drawn in the Join boards' language (no board of its own): square back
/// button, icon tile, title and explanation, the phone field with its label
/// above, and the button at the foot.
///
/// Features:
/// - Ethiopian phone number input (accepts +251, 00251, or 0 prefix)
/// - Sends verification code to user's phone via BLoC
/// - Navigates to ResetPasswordScreen on success
/// - All strings from l10n
class ForgotPasswordScreen extends StatefulWidget {
  /// Optional pre-filled phone number (from login screen).
  final String? initialPhoneNumber;

  const ForgotPasswordScreen({super.key, this.initialPhoneNumber});

  @override
  State<ForgotPasswordScreen> createState() => _ForgotPasswordScreenState();
}

class _ForgotPasswordScreenState extends State<ForgotPasswordScreen> {
  final _formKey = GlobalKey<FormState>();
  final _phoneController = TextEditingController();
  String? _errorMessage;

  @override
  void initState() {
    super.initState();
    if (widget.initialPhoneNumber != null &&
        widget.initialPhoneNumber!.isNotEmpty) {
      _phoneController.text = widget.initialPhoneNumber!;
    }
  }

  @override
  void dispose() {
    _phoneController.dispose();
    super.dispose();
  }

  void _handleSendCode() {
    setState(() {
      _errorMessage = null;
    });

    if (!_formKey.currentState!.validate()) {
      return;
    }

    final normalizedPhone =
        normalizeEthiopianPhone(_phoneController.text.trim());
    if (normalizedPhone == null) return;
    context.read<AuthBloc>().add(
          AuthForgotPasswordRequested(phoneNumber: normalizedPhone),
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

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthPasswordResetCodeSent) {
          // Navigate to reset password screen with phone number
          Navigator.of(context).pushReplacement(
            MaterialPageRoute<void>(
              builder: (_) => ResetPasswordScreen(
                phoneNumber: state.phoneNumber,
              ),
            ),
          );
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
                          title: l10n.forgotPassword,
                          subtitle: l10n.enterPhoneForReset,
                        ),
                        const SizedBox(height: 26),
                        LabeledField(
                          label: l10n.phoneNumber,
                          field: TextFormField(
                            controller: _phoneController,
                            decoration: labeledFieldDecoration().copyWith(
                              helperText: l10n.ethiopianPhoneExample,
                              helperMaxLines: 2,
                            ),
                            keyboardType: TextInputType.phone,
                            inputFormatters: [EthiopianPhoneInputFormatter()],
                            validator: (value) {
                              if (value == null ||
                                  value.isEmpty ||
                                  !isValidEthiopianPhone(value)) {
                                return l10n.invalidPhoneFormat;
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
                        onPressed: isLoading ? null : _handleSendCode,
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
                            : Text(l10n.sendResetCode),
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
