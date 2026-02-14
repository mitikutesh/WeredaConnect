import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// OTP verification screen for phone number verification.
///
/// Features:
/// - 6-digit OTP input
/// - Resend OTP functionality
/// - Countdown timer
/// - Auto-submit when 6 digits entered
///
/// Ethiopian context:
/// - Large input fields for low-end devices
/// - Clear visual feedback
/// - Simple instructions
class OtpVerificationScreen extends StatefulWidget {
  final String phoneNumber;
  final String password;

  const OtpVerificationScreen({
    required this.phoneNumber, required this.password, super.key,
  });

  @override
  State<OtpVerificationScreen> createState() => _OtpVerificationScreenState();
}

class _OtpVerificationScreenState extends State<OtpVerificationScreen> {
  final _otpController = TextEditingController();
  final _authService = AuthService();

  bool _isLoading = false;
  bool _canResend = false;
  int _countdown = 60;
  String? _errorMessage;

  @override
  void initState() {
    super.initState();
    _startCountdown();
  }

  @override
  void dispose() {
    _otpController.dispose();
    super.dispose();
  }

  void _startCountdown() {
    setState(() {
      _countdown = 60;
      _canResend = false;
    });

    () async {
      while (_countdown > 0) {
        await Future<void>.delayed(const Duration(seconds: 1));
        if (!mounted) return;

        setState(() {
          _countdown--;
        });
      }

      if (mounted) {
        setState(() {
          _canResend = true;
        });
      }
    }();
  }

  Future<void> _handleVerifyOtp() async {
    // Clear previous errors
    setState(() {
      _errorMessage = null;
    });

    // Validate OTP
    final otp = _otpController.text.trim();
    if (otp.length != 6) {
      setState(() {
        _errorMessage = 'Please enter 6-digit code';
      });
      return;
    }

    setState(() {
      _isLoading = true;
    });

    try {
      // Verify OTP
      final result = await _authService.verifyOtp(
        phoneNumber: widget.phoneNumber,
        otp: otp,
      );

      if (!mounted) return;

      if (result.isSuccess) {
        // Now login with the password to cache credentials
        final loginResult = await _authService.loginOnline(
          phoneNumber: widget.phoneNumber,
          password: widget.password,
        );

        if (!mounted) return;

        if (loginResult.isSuccess) {
          // Show success message
          final l10n = AppLocalizations.of(context)!;
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.registrationSuccess),
              backgroundColor: Colors.green,
              behavior: SnackBarBehavior.floating,
            ),
          );

          // TODO: Navigate to home screen
          // Navigator.of(context).pushAndRemoveUntil(
          //   MaterialPageRoute(builder: (_) => HomeScreen()),
          //   (route) => false,
          // );
        }
      } else {
        // Show error message
        setState(() {
          _errorMessage = result.errorMessage ??
              result.errorCode?.userMessage ??
              'Verification failed';
        });
      }
    } finally {
      if (mounted) {
        setState(() {
          _isLoading = false;
        });
      }
    }
  }

  Future<void> _handleResendOtp() async {
    // TODO: Implement resend OTP
    final l10n = AppLocalizations.of(context)!;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.comingSoon)),
    );
    _startCountdown();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.verifyOtp),
        elevation: 0,
      ),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(24.0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const SizedBox(height: 32),

              // Icon
              Icon(
                Icons.sms,
                size: 80,
                color: theme.primaryColor,
              ),
              const SizedBox(height: 24),

              // Instructions
              Text(
                l10n.enterOtp,
                style: theme.textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 16),
              Text(
                l10n.otpSent,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: Colors.grey.shade600,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              Text(
                widget.phoneNumber,
                style: theme.textTheme.bodyLarge?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 48),

              // OTP input
              TextFormField(
                controller: _otpController,
                decoration: const InputDecoration(
                  border: OutlineInputBorder(),
                  contentPadding: EdgeInsets.symmetric(
                    horizontal: 16,
                    vertical: 20,
                  ),
                  counterText: '',
                ),
                keyboardType: TextInputType.number,
                maxLength: 6,
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 24,
                  letterSpacing: 8,
                  fontWeight: FontWeight.bold,
                ),
                inputFormatters: [
                  FilteringTextInputFormatter.digitsOnly,
                ],
                onChanged: (value) {
                  // Auto-submit when 6 digits entered
                  if (value.length == 6) {
                    _handleVerifyOtp();
                  }
                },
              ),
              const SizedBox(height: 24),

              // Error message
              if (_errorMessage != null)
                Container(
                  padding: const EdgeInsets.all(12),
                  margin: const EdgeInsets.only(bottom: 16),
                  decoration: BoxDecoration(
                    color: Colors.red.shade50,
                    border: Border.all(color: Colors.red.shade200),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Row(
                    children: [
                      Icon(Icons.error_outline, color: Colors.red.shade700),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Text(
                          _errorMessage!,
                          style: TextStyle(color: Colors.red.shade700),
                        ),
                      ),
                    ],
                  ),
                ),

              // Verify button
              ElevatedButton(
                onPressed: _isLoading ? null : _handleVerifyOtp,
                style: ElevatedButton.styleFrom(
                  padding: const EdgeInsets.symmetric(vertical: 16),
                  minimumSize: const Size.fromHeight(48),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(8),
                  ),
                ),
                child: _isLoading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          valueColor:
                              AlwaysStoppedAnimation<Color>(Colors.white),
                        ),
                      )
                    : Text(
                        l10n.verifyOtp,
                        style: const TextStyle(fontSize: 16),
                      ),
              ),
              const SizedBox(height: 24),

              // Resend OTP
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  if (!_canResend)
                    Text(
                      'Resend code in $_countdown seconds',
                      style: TextStyle(color: Colors.grey.shade600),
                    )
                  else
                    TextButton(
                      onPressed: _handleResendOtp,
                      child: Text(l10n.resendOtp),
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
