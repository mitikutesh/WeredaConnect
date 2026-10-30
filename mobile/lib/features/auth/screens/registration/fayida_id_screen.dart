import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// Step 2: Fayda eKYC Verification via OIDC (the RegisterFayda board).
///
/// The title, the dark Digital ID card and the blue note scroll; the
/// "Verify with Fayda" button sits at the foot with "Needs internet for this
/// step." under it, and gives way to a spinner while Fayda answers. Once
/// verified the card shows the legal name and the button is gone (the flow's
/// Continue takes its place). Also the body of the link-later screen.
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

    return Column(
      children: [
        Expanded(
          child: FwContentWidth(
            maxWidth: 480,
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(20, 22, 20, 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  JoinTitle(
                    title: l10n.faydaVerifyTitle,
                    subtitle: promptMessage ?? l10n.faydaVerifyDescription,
                  ),
                  const SizedBox(height: 24),

                  // Error banner if any
                  if (errorMessage != null) ...[
                    JoinErrorBanner(message: errorMessage!),
                    const SizedBox(height: 12),
                  ],

                  _DigitalIdCard(
                    verified: isVerified,
                    verifiedName: verifiedName,
                  ),
                  const SizedBox(height: 18),

                  JoinNote.info(text: l10n.faydaRequired),

                  if (isVerified) ...[
                    const SizedBox(height: 18),
                    Center(
                      child: JoinStatusPill(label: l10n.faydaVerifiedSuccess),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),

        // The step's own action at the foot, until Fayda has answered.
        if (!isVerified)
          FwContentWidth(
            maxWidth: 480,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 28),
              child: isVerifying
                  ? Column(
                      children: [
                        const CircularProgressIndicator(
                          valueColor: AlwaysStoppedAnimation<Color>(
                            AppTheme.fieldGreen,
                          ),
                        ),
                        const SizedBox(height: 10),
                        Text(
                          verifyingMessage ?? l10n.faydaVerifying,
                          textAlign: TextAlign.center,
                          style: const TextStyle(
                            fontSize: 14,
                            color: AppTheme.mutedSteel,
                          ),
                        ),
                        if (onCancelVerification != null) ...[
                          const SizedBox(height: 4),
                          TextButton(
                            onPressed: onCancelVerification,
                            child: Text(
                              l10n.cancel,
                              style:
                                  const TextStyle(color: AppTheme.mutedSteel),
                            ),
                          ),
                        ],
                      ],
                    )
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        SizedBox(
                          height: 56,
                          child: FilledButton.icon(
                            onPressed: onFaydaVerifyRequested,
                            style: FilledButton.styleFrom(
                              backgroundColor: AppTheme.soilDark,
                              foregroundColor: Colors.white,
                            ),
                            icon: const Icon(Icons.shield_outlined, size: 20),
                            label: Text(
                              errorMessage != null
                                  ? l10n.retry
                                  : l10n.verifyWithFayda,
                            ),
                          ),
                        ),
                        const SizedBox(height: 8),
                        Text(
                          l10n.joinFaydaNeedsInternet,
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
      ],
    );
  }
}

/// The board's dark Digital ID card: the label and shield, a photo
/// placeholder beside three grey lines (or the verified legal name), and
/// the note that the legal name comes from Fayda.
class _DigitalIdCard extends StatelessWidget {
  final bool verified;
  final String? verifiedName;

  const _DigitalIdCard({required this.verified, this.verifiedName});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final showName = verified && verifiedName != null;

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: verified ? const Color(0xFF193B2B) : AppTheme.soilDark,
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
                  color: AppTheme.soilMuted,
                ),
              ),
              Icon(
                verified ? Icons.verified_outlined : Icons.shield_outlined,
                color: AppTheme.soilGreen,
                size: 26,
              ),
            ],
          ),
          const SizedBox(height: 18),
          Row(
            children: [
              Container(
                width: 64,
                height: 76,
                decoration: BoxDecoration(
                  color: AppTheme.soilCard,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: const Icon(
                  Icons.person_outline,
                  color: Color(0xFF9AA59F),
                  size: 30,
                ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: showName
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
                              color: AppTheme.soilGreen,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                        ],
                      )
                    : const Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          _PlaceholderLine(fraction: 0.8),
                          SizedBox(height: 8),
                          _PlaceholderLine(fraction: 0.6),
                          SizedBox(height: 8),
                          _PlaceholderLine(fraction: 0.7),
                        ],
                      ),
              ),
            ],
          ),
          const SizedBox(height: 18),
          Text(
            l10n.faydaLegalNameNote,
            style: const TextStyle(
              fontSize: 13,
              color: AppTheme.soilMuted,
              height: 1.4,
            ),
          ),
        ],
      ),
    );
  }
}

class _PlaceholderLine extends StatelessWidget {
  final double fraction;

  const _PlaceholderLine({required this.fraction});

  @override
  Widget build(BuildContext context) {
    return FractionallySizedBox(
      alignment: Alignment.centerLeft,
      widthFactor: fraction,
      child: Container(
        height: 10,
        decoration: BoxDecoration(
          color: AppTheme.soilTrack,
          borderRadius: BorderRadius.circular(999),
        ),
      ),
    );
  }
}
