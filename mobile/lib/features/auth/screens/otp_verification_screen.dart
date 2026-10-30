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
    // The code boxes outline the next box only while the field has focus.
    _focusNode.addListener(_onFocusChanged);
  }

  void _onFocusChanged() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _countdown?.cancel();
    _codeController.dispose();
    _focusNode
      ..removeListener(_onFocusChanged)
      ..dispose();
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
          // The page title below names the screen (Register OTP board).
        ),
        body: Align(
          alignment: Alignment.topCenter,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
              child: BlocBuilder<AuthBloc, AuthState>(
                builder: (context, state) {
                  final isLoading = state is AuthLoading;
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Align(
                        alignment: AlignmentDirectional.centerStart,
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
                        style: const TextStyle(
                          fontSize: 15,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                      const SizedBox(height: 20),
                      Text(
                        l10n.enterOtp,
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.unselectedNav,
                        ),
                      ),
                      const SizedBox(height: 8),
                      // One box per digit over the real field, which keeps
                      // the keyboard, paste and SMS autofill.
                      SizedBox(
                        height: 56,
                        child: Stack(
                          children: [
                            Positioned.fill(
                              child: Opacity(
                                opacity: 0,
                                alwaysIncludeSemantics: true,
                                child: TextField(
                                  key: const Key('otp_code_field'),
                                  controller: _codeController,
                                  focusNode: _focusNode,
                                  autofocus: true,
                                  enabled: !isLoading,
                                  keyboardType: TextInputType.number,
                                  autofillHints: const [
                                    AutofillHints.oneTimeCode,
                                  ],
                                  maxLength: OtpVerificationScreen.codeLength,
                                  showCursor: false,
                                  enableInteractiveSelection: false,
                                  inputFormatters: [
                                    FilteringTextInputFormatter.digitsOnly,
                                    LengthLimitingTextInputFormatter(
                                      OtpVerificationScreen.codeLength,
                                    ),
                                  ],
                                  decoration: InputDecoration(
                                    labelText: l10n.enterOtp,
                                    counterText: '',
                                    border: InputBorder.none,
                                  ),
                                  onChanged: _onCodeChanged,
                                  onSubmitted: (_) => _submit(),
                                ),
                              ),
                            ),
                            Positioned.fill(
                              child: IgnorePointer(
                                child: _CodeBoxes(
                                  code: _codeController.text,
                                  focused: _focusNode.hasFocus,
                                  error: _errorMessage != null,
                                ),
                              ),
                            ),
                          ],
                        ),
                      ),
                      if (_errorMessage != null) ...[
                        const SizedBox(height: 6),
                        Text(
                          _errorMessage!,
                          style: TextStyle(
                            fontSize: 12,
                            color: Theme.of(context).colorScheme.error,
                          ),
                        ),
                      ],
                      const SizedBox(height: 8),
                      Row(
                        children: [
                          Expanded(
                            child: _expiresAt == null
                                ? const SizedBox.shrink()
                                : ClockText(
                                    clockFormatOf(context).deadline(
                                      _expiresAt!,
                                      sentence: l10n.otpExpiresAt,
                                    ),
                                    style: const TextStyle(
                                      fontSize: 13,
                                      color: AppTheme.textMuted,
                                    ),
                                  ),
                          ),
                          TextButton(
                            key: const Key('otp_resend_button'),
                            onPressed: (_secondsLeft <= 0 && !isLoading)
                                ? _resend
                                : null,
                            child: Text(
                              _secondsLeft > 0
                                  ? l10n.resendOtpIn(_secondsLeft)
                                  : l10n.resendOtp,
                              style: const TextStyle(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                        ],
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
                        child: Text(
                          l10n.otpFaydaNumberNote,
                          style: const TextStyle(
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

/// The code as six boxes (Register OTP board): typed digits, and the next
/// box to fill outlined while the field has focus.
class _CodeBoxes extends StatelessWidget {
  const _CodeBoxes({
    required this.code,
    required this.focused,
    required this.error,
  });

  final String code;
  final bool focused;
  final bool error;

  @override
  Widget build(BuildContext context) {
    const length = OtpVerificationScreen.codeLength;
    return Row(
      children: [
        for (var i = 0; i < length; i++) ...[
          if (i > 0) const SizedBox(width: 8),
          Expanded(
            child: Container(
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: AppTheme.surfaceWhite,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(
                  width: 1.5,
                  color: error
                      ? Theme.of(context).colorScheme.error
                      : i < code.length ||
                              (focused && i == code.length.clamp(0, length - 1))
                          ? AppTheme.fieldGreen
                          : AppTheme.whisperBorder,
                ),
              ),
              child: Text(
                i < code.length ? code[i] : '',
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                ),
              ),
            ),
          ),
        ],
      ],
    );
  }
}
