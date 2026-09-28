import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 2: Fayda eKYC Verification via OIDC (required to register).
///
/// States:
/// - Pending: "Verify with Fayda" button
/// - Verifying: spinner while browser/callback in progress
/// - Verified: green checkmark with verified name
/// - Error: red panel above the card, whose button becomes "Retry"
class FayidaIdScreen extends StatelessWidget {
  final VoidCallback onFaydaVerifyRequested;
  final VoidCallback? onCancelVerification;
  final bool isVerifying;

  /// Text under the spinner while verifying; defaults to "Verifying...".
  final String? verifyingMessage;
  final bool isVerified;
  final String? verifiedName;
  final String? errorMessage;

  const FayidaIdScreen({
    required this.onFaydaVerifyRequested,
    this.onCancelVerification,
    this.isVerifying = false,
    this.verifyingMessage,
    this.isVerified = false,
    this.verifiedName,
    this.errorMessage,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.faydaVerifyTitle,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.textDark,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.faydaVerifyDescription,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 32),

              // Error message
              if (errorMessage != null) ...[
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: AppTheme.statusError.withAlpha(25),
                    border:
                        Border.all(color: AppTheme.statusError.withAlpha(50)),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Row(
                    children: [
                      const Icon(
                        Icons.error_outline,
                        color: AppTheme.statusError,
                        size: 20,
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          errorMessage!,
                          style: const TextStyle(
                            color: AppTheme.statusError,
                            fontSize: 13,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 16),
              ],

              // Verified state
              if (isVerified) ...[
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(20),
                  decoration: BoxDecoration(
                    color: AppTheme.statusSuccess.withAlpha(15),
                    border: Border.all(color: AppTheme.statusSuccess),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Column(
                    children: [
                      const Icon(
                        Icons.verified_user,
                        color: AppTheme.statusSuccess,
                        size: 48,
                      ),
                      const SizedBox(height: 12),
                      Text(
                        l10n.faydaVerifiedSuccess,
                        style: const TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.statusSuccess,
                        ),
                      ),
                      if (verifiedName != null) ...[
                        const SizedBox(height: 8),
                        Text(
                          verifiedName!,
                          style: const TextStyle(
                            fontSize: 18,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.textDark,
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ]

              // Verifying state
              else if (isVerifying) ...[
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(32),
                  decoration: BoxDecoration(
                    color: AppTheme.surfaceWhite,
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: AppTheme.borderLight),
                  ),
                  child: Column(
                    children: [
                      const SizedBox(
                        width: 40,
                        height: 40,
                        child: CircularProgressIndicator(
                          strokeWidth: 3,
                          color: AppTheme.primaryGreen,
                        ),
                      ),
                      const SizedBox(height: 16),
                      Text(
                        verifyingMessage ?? l10n.faydaVerifying,
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.textMuted,
                        ),
                      ),
                      if (onCancelVerification != null) ...[
                        const SizedBox(height: 20),
                        TextButton(
                          onPressed: onCancelVerification,
                          child: Text(
                            l10n.cancel,
                            style: const TextStyle(color: AppTheme.textMuted),
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ]

              // Pending state — show verify button
              else ...[
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(20),
                  decoration: BoxDecoration(
                    color: AppTheme.surfaceWhite,
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: AppTheme.borderLight),
                  ),
                  child: Column(
                    children: [
                      const Icon(
                        Icons.fingerprint,
                        size: 48,
                        color: AppTheme.primaryGreen,
                      ),
                      const SizedBox(height: 16),
                      Text(
                        l10n.faydaRequired,
                        style: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.textMuted,
                        ),
                        textAlign: TextAlign.center,
                      ),
                      const SizedBox(height: 20),
                      SizedBox(
                        width: double.infinity,
                        height: 48,
                        child: FilledButton.icon(
                          onPressed: onFaydaVerifyRequested,
                          icon: Icon(
                            errorMessage != null
                                ? Icons.refresh
                                : Icons.open_in_browser,
                          ),
                          label: Text(
                            errorMessage != null
                                ? l10n.retry
                                : l10n.verifyWithFayda,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
