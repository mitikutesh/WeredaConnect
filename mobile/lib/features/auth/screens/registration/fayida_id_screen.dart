import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 2: Fayda eKYC Verification via OIDC (Fieldway Canvas).
class FayidaIdScreen extends StatelessWidget {
  final VoidCallback onFaydaVerifyRequested;
  final VoidCallback? onCancelVerification;
  final bool isVerifying;

  /// Text under the spinner while verifying; defaults to "Verifying...".
  final String? verifyingMessage;
  final bool isVerified;
  final String? verifiedName;
  final String? errorMessage;

  /// Replaces the "required to register" line.
  final String? promptMessage;

  const FayidaIdScreen({
    required this.onFaydaVerifyRequested,
    this.onCancelVerification,
    this.isVerifying = false,
    this.verifyingMessage,
    this.isVerified = false,
    this.verifiedName,
    this.errorMessage,
    this.promptMessage,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.faydaVerifyTitle,
                style: const TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                  letterSpacing: -0.5,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                promptMessage ?? l10n.faydaVerifyDescription,
                style: const TextStyle(
                  fontSize: 15,
                  height: 1.4,
                  color: AppTheme.mutedSteel,
                ),
              ),
              const SizedBox(height: 8),

              // Error banner if any
              if (errorMessage != null) ...[
                Container(
                  width: double.infinity,
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  decoration: BoxDecoration(
                    color: const Color(0xFFFDF2F2),
                    border: Border.all(color: const Color(0xFFF8B4B4)),
                    borderRadius: BorderRadius.circular(14),
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
                          errorMessage!,
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
                const SizedBox(height: 8),
              ],

              // Digital ID Simulated Card from Canvas
              Container(
                width: double.infinity,
                padding:
                    const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                decoration: BoxDecoration(
                  color:
                      isVerified ? const Color(0xFF193B2B) : AppTheme.soilDark,
                  borderRadius: BorderRadius.circular(24),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text(
                          l10n.faydaDigitalIdLabel.toUpperCase(),
                          style: const TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w800,
                            letterSpacing: 1.2,
                            color: Color(0xFFB9C2BD),
                          ),
                        ),
                        Icon(
                          isVerified ? Icons.verified : Icons.security,
                          color: const Color(0xFF8FC5A4),
                          size: 24,
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        Container(
                          width: 48,
                          height: 56,
                          decoration: BoxDecoration(
                            color: const Color(0xFF22302A),
                            borderRadius: BorderRadius.circular(12),
                          ),
                          child: const Icon(
                            Icons.person,
                            color: Color(0xFF9AA59F),
                            size: 32,
                          ),
                        ),
                        const SizedBox(width: 16),
                        Expanded(
                          child: isVerified && verifiedName != null
                              ? Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(
                                      verifiedName!,
                                      style: const TextStyle(
                                        fontSize: 17,
                                        fontWeight: FontWeight.w800,
                                        color: Colors.white,
                                      ),
                                    ),
                                    const SizedBox(height: 4),
                                    Text(
                                      l10n.faydaVerifiedWith,
                                      style: const TextStyle(
                                        fontSize: 12,
                                        color: Color(0xFF8FC5A4),
                                        fontWeight: FontWeight.w600,
                                      ),
                                    ),
                                  ],
                                )
                              : Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Container(
                                      height: 10,
                                      width: 140,
                                      decoration: BoxDecoration(
                                        color: const Color(0xFF33433B),
                                        borderRadius:
                                            BorderRadius.circular(999),
                                      ),
                                    ),
                                    const SizedBox(height: 8),
                                    Container(
                                      height: 10,
                                      width: 100,
                                      decoration: BoxDecoration(
                                        color: const Color(0xFF33433B),
                                        borderRadius:
                                            BorderRadius.circular(999),
                                      ),
                                    ),
                                    const SizedBox(height: 8),
                                    Container(
                                      height: 10,
                                      width: 120,
                                      decoration: BoxDecoration(
                                        color: const Color(0xFF33433B),
                                        borderRadius:
                                            BorderRadius.circular(999),
                                      ),
                                    ),
                                  ],
                                ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Text(
                      l10n.faydaLegalNameNote,
                      style: const TextStyle(
                        fontSize: 13,
                        color: Color(0xFFB9C2BD),
                        height: 1.4,
                      ),
                    ),
                  ],
                ),
              ),

              const SizedBox(height: 8),

              // Security notice pill box
              Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                decoration: BoxDecoration(
                  color: const Color(0xFFE6ECF5),
                  borderRadius: BorderRadius.circular(16),
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Icon(
                      Icons.info_outline,
                      size: 18,
                      color: Color(0xFF2E4C7D),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        l10n.faydaRequired,
                        style: const TextStyle(
                          fontSize: 13,
                          height: 1.4,
                          color: Color(0xFF2E4C7D),
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ),
                  ],
                ),
              ),

              const SizedBox(height: 10),

              // Action button
              if (isVerifying)
                Center(
                  child: Column(
                    children: [
                      const CircularProgressIndicator(
                        valueColor:
                            AlwaysStoppedAnimation<Color>(AppTheme.fieldGreen),
                      ),
                      const SizedBox(height: 8),
                      Text(
                        verifyingMessage ?? l10n.faydaVerifying,
                        style: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                      if (onCancelVerification != null) ...[
                        const SizedBox(height: 8),
                        TextButton(
                          onPressed: onCancelVerification,
                          child: Text(
                            l10n.cancel,
                            style: const TextStyle(color: AppTheme.mutedSteel),
                          ),
                        ),
                      ],
                    ],
                  ),
                )
              else
                SizedBox(
                  width: double.infinity,
                  height: 54,
                  child: ElevatedButton.icon(
                    onPressed: onFaydaVerifyRequested,
                    style: ElevatedButton.styleFrom(
                      backgroundColor:
                          isVerified ? AppTheme.fieldGreen : AppTheme.soilDark,
                      foregroundColor: Colors.white,
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(16),
                      ),
                      elevation: 0,
                    ),
                    icon: Icon(
                      isVerified ? Icons.check : Icons.security,
                      size: 20,
                    ),
                    label: Text(
                      isVerified
                          ? l10n.faydaVerifiedSuccess
                          : (errorMessage != null
                              ? l10n.retry
                              : l10n.verifyWithFayda),
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
