import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// Confirms the SMS OTP sent after registration (or after a login for an
/// account whose phone was never confirmed).
///
/// - One 6-digit field; the code is submitted automatically once complete.
/// - "Resend code" unlocks after [resendCooldown] (or the server's
///   `retryAfterSeconds` after a rate limit).
/// - Success is handled by the screen that pushed this one: AuthBloc emits
///   AuthAuthenticated and the owner pops back to the app.
class OtpVerificationScreen extends StatefulWidget {
  final String phoneNumber;
  final DateTime? expiresAt;

  /// Wait before the first resend is allowed.
  final Duration resendCooldown;

  const OtpVerificationScreen({
    required this.phoneNumber,
    this.expiresAt,
    this.resendCooldown = const Duration(seconds: 60),
    super.key,
  });

  /// Length of the code the server sends.
  static const codeLength = 6;

  @override
  State<OtpVerificationScreen> createState() => _OtpVerificationScreenState();
}

class _OtpVerificationScreenState extends State<OtpVerificationScreen> {
  final _codeController = TextEditingController();
  final _focusNode = FocusNode();
  Timer? _countdown;
  late int _secondsLeft;
  DateTime? _expiresAt;
  String? _errorMessage;
  String? _lastSubmitted;

  late final AuthBloc _authBloc;

  @override
  void initState() {
    super.initState();
    _authBloc = context.read<AuthBloc>();
    _expiresAt = widget.expiresAt;
    _startCountdown(widget.resendCooldown.inSeconds);
  }

  @override
  void dispose() {
    _countdown?.cancel();
    _codeController.dispose();
    _focusNode.dispose();
    // Back, or popped after success: the held password is no longer needed.
    if (!_authBloc.isClosed) _authBloc.add(const AuthOtpAbandoned());
    super.dispose();
  }

  void _startCountdown(int seconds) {
    _countdown?.cancel();
    setState(() => _secondsLeft = seconds);
    if (seconds <= 0) return;
    _countdown = Timer.periodic(const Duration(seconds: 1), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      setState(() => _secondsLeft--);
      if (_secondsLeft <= 0) timer.cancel();
    });
  }

  bool get _isComplete =>
      _codeController.text.length == OtpVerificationScreen.codeLength;

  void _onCodeChanged(String value) {
    if (_errorMessage != null) setState(() => _errorMessage = null);
    // Auto-submit once all digits are in, but never resend the same wrong
    // code twice in a row.
    if (value.length == OtpVerificationScreen.codeLength &&
        value != _lastSubmitted) {
      _submit();
    } else {
      setState(() {});
    }
  }

  void _submit() {
    if (!_isComplete) return;
    if (context.read<AuthBloc>().state is AuthLoading) return;
    _lastSubmitted = _codeController.text;
    FocusScope.of(context).unfocus();
    context.read<AuthBloc>().add(
          AuthVerifyOtpRequested(
            otpCode: _codeController.text,
            phoneNumber: widget.phoneNumber,
          ),
        );
  }

  void _resend() {
    setState(() => _errorMessage = null);
    context
        .read<AuthBloc>()
        .add(AuthResendOtpRequested(phoneNumber: widget.phoneNumber));
  }

  void _onAuthState(BuildContext context, AuthState state) {
    final l10n = AppLocalizations.of(context)!;
    if (state is AuthOtpVerificationRequired && state.resentAt != null) {
      _codeController.clear();
      _lastSubmitted = null;
      _expiresAt = state.expiresAt;
      _startCountdown(widget.resendCooldown.inSeconds);
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(l10n.otpResent)));
      _focusNode.requestFocus();
    } else if (state is AuthError) {
      setState(() {
        _errorMessage = localizeAuthError(
          l10n,
          state.errorCode,
          retryAfterSeconds: state.retryAfterSeconds,
        );
      });
      if (state.errorCode == AuthErrorCode.rateLimitExceeded &&
          state.retryAfterSeconds != null) {
        _startCountdown(state.retryAfterSeconds!);
      }
      if (state.errorCode == AuthErrorCode.otpInvalidOrExpired ||
          state.errorCode == AuthErrorCode.otpInvalidFormat) {
        _codeController.clear();
        _lastSubmitted = null;
        _focusNode.requestFocus();
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: _onAuthState,
      child: Scaffold(
        backgroundColor: AppTheme.chalkBackground,
        appBar: AppBar(
          backgroundColor: Colors.transparent,
          elevation: 0,
          foregroundColor: AppTheme.textDark,
          title: Text(l10n.otpVerificationTitle),
        ),
        body: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: BlocBuilder<AuthBloc, AuthState>(
                builder: (context, state) {
                  final isLoading = state is AuthLoading;
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Center(
                        child: Container(
                          width: 56,
                          height: 56,
                          decoration: BoxDecoration(
                            color: const Color(0xFFE3EEE6),
                            borderRadius: BorderRadius.circular(18),
                          ),
                          child: const Icon(
                            Icons.sms_outlined,
                            size: 28,
                            color: AppTheme.fieldGreen,
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      Text(
                        l10n.otpVerificationTitle,
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontSize: 26,
                          fontWeight: FontWeight.w800,
                          color: AppTheme.soilDark,
                          letterSpacing: -0.4,
                        ),
                      ),
                      const SizedBox(height: 6),
                      Text(
                        l10n.otpSentTo(widget.phoneNumber),
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontSize: 15,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                      if (_expiresAt != null) ...[
                        const SizedBox(height: 4),
                        ClockText(
                          clockFormatOf(context).deadline(
                            _expiresAt!,
                            sentence: l10n.otpExpiresAt,
                          ),
                          textAlign: TextAlign.center,
                          style: const TextStyle(
                            fontSize: 13,
                            color: AppTheme.textMuted,
                          ),
                        ),
                      ],
                      const SizedBox(height: 24),
                      TextField(
                        key: const Key('otp_code_field'),
                        controller: _codeController,
                        focusNode: _focusNode,
                        autofocus: true,
                        enabled: !isLoading,
                        keyboardType: TextInputType.number,
                        textAlign: TextAlign.center,
                        autofillHints: const [AutofillHints.oneTimeCode],
                        maxLength: OtpVerificationScreen.codeLength,
                        inputFormatters: [
                          FilteringTextInputFormatter.digitsOnly,
                          LengthLimitingTextInputFormatter(
                            OtpVerificationScreen.codeLength,
                          ),
                        ],
                        style: const TextStyle(
                          fontSize: 28,
                          letterSpacing: 12,
                          fontWeight: FontWeight.w600,
                        ),
                        decoration: InputDecoration(
                          labelText: l10n.enterOtp,
                          hintText: l10n.otpCodeHint,
                          counterText: '',
                          errorText: _errorMessage,
                          errorMaxLines: 3,
                          border: const OutlineInputBorder(),
                        ),
                        onChanged: _onCodeChanged,
                        onSubmitted: (_) => _submit(),
                      ),
                      const SizedBox(height: 20),
                      // Canvas info card
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 16,
                          vertical: 14,
                        ),
                        decoration: BoxDecoration(
                          color: Colors.white,
                          borderRadius: BorderRadius.circular(16),
                          border: Border.all(color: const Color(0xFFE3E1D8)),
                        ),
                        child: const Text(
                          'We ask for this only when the number differs from your Fayda record.',
                          style: TextStyle(
                            fontSize: 13,
                            height: 1.4,
                            color: Color(0xFF3F4843),
                          ),
                        ),
                      ),
                      const SizedBox(height: 20),
                      SizedBox(
                        height: 56,
                        child: FilledButton(
                          key: const Key('otp_verify_button'),
                          style: FilledButton.styleFrom(
                            backgroundColor: AppTheme.fieldGreen,
                            foregroundColor: Colors.white,
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(16),
                            ),
                          ),
                          onPressed:
                              (_isComplete && !isLoading) ? _submit : null,
                          child: isLoading
                              ? const SizedBox(
                                  width: 20,
                                  height: 20,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                    color: Colors.white,
                                  ),
                                )
                              : Text(
                                  l10n.verifyOtp,
                                  style: const TextStyle(
                                    fontSize: 16,
                                    fontWeight: FontWeight.w800,
                                  ),
                                ),
                        ),
                      ),
                      const SizedBox(height: 12),
                      TextButton(
                        key: const Key('otp_resend_button'),
                        onPressed:
                            (_secondsLeft <= 0 && !isLoading) ? _resend : null,
                        child: Text(
                          _secondsLeft > 0
                              ? l10n.resendOtpIn(_secondsLeft)
                              : l10n.resendOtp,
                        ),
                      ),
                    ],
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
