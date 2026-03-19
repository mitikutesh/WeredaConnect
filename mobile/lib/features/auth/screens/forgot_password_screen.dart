import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/reset_password_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Forgot password screen — collects phone number and requests reset code.
///
/// Features:
/// - Ethiopian phone number input (accepts +251, 00251, or 0 prefix)
/// - Sends verification code to user's phone via BLoC
/// - Navigates to ResetPasswordScreen on success
/// - All strings from l10n
///
/// Ethiopian context:
/// - Phone number formats: +251XXXXXXXXX, 00251XXXXXXXXX, 0XXXXXXXXX
/// - Large touch targets for low-end devices
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
    context.read<AuthBloc>().add(
          AuthForgotPasswordRequested(phoneNumber: normalizedPhone!),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

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
        appBar: AppBar(
          title: Text(l10n.forgotPassword),
          elevation: 0,
        ),
        body: SafeArea(
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: SingleChildScrollView(
                padding: const EdgeInsets.all(24.0),
                child: _buildForm(context, l10n, theme),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildForm(
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
              const SizedBox(height: 32),

              // Lock icon
              Icon(
                Icons.lock_reset,
                size: 80,
                color: theme.colorScheme.primary,
              ),
              const SizedBox(height: 24),

              // Instructions
              Text(
                l10n.enterPhoneForReset,
                style: theme.textTheme.bodyLarge,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 32),

              // Phone number input
              TextFormField(
                controller: _phoneController,
                decoration: InputDecoration(
                  labelText: l10n.phoneNumber,
                  prefixIcon: const Icon(Icons.phone),
                  hintText: l10n.ethiopianPhoneExample,
                  border: const OutlineInputBorder(),
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
              const SizedBox(height: 24),

              // Error message
              if (_errorMessage != null)
                Container(
                  padding: const EdgeInsets.all(12),
                  margin: const EdgeInsets.only(bottom: 16),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.errorContainer,
                    border: Border.all(
                      color: theme.colorScheme.error.withAlpha(80),
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

              // Send reset code button
              ElevatedButton(
                onPressed: isLoading ? null : _handleSendCode,
                style: ElevatedButton.styleFrom(
                  padding: const EdgeInsets.symmetric(vertical: 16),
                  minimumSize: const Size.fromHeight(48),
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
                        l10n.sendResetCode,
                        style: const TextStyle(fontSize: 16),
                      ),
              ),
            ],
          );
        },
      ),
    );
  }
}
