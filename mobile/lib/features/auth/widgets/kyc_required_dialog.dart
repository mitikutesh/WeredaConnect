import 'package:flutter/material.dart';

import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Explains a payment blocked by the KYC gate (ORDER_KYC_REQUIRED).
///
/// When the buyer can fix it ([KycReason.buyerCanFix]) it offers Fayda and
/// opens the verification screen in payment mode, which checks the identity
/// is this account's own and closes itself on success. Resolves to true only
/// when that verification succeeded, so the caller sends the payment again;
/// false (cancelled, failed, wrong identity) means the payment stays unsent.
///
/// When the farmer is the one not verified, or the check is unavailable, it
/// only explains and resolves to false: verifying the buyer would not help.
/// [verificationScreen] replaces the Fayda screen (tests).
Future<bool> showKycRequiredDialog(
  BuildContext context, {
  KycReason reason = KycReason.buyerFayda,
  WidgetBuilder? verificationScreen,
}) async {
  final l10n = AppLocalizations.of(context)!;

  if (!reason.buyerCanFix) {
    await showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.info_outline),
        title: Text(l10n.kycRequiredTitle),
        content: Text(
          switch (reason) {
            KycReason.farmerPhone => l10n.kycFarmerPhoneMessage,
            KycReason.farmerFayda => l10n.kycFarmerFaydaMessage,
            _ => l10n.kycUnavailableMessage,
          },
        ),
        actions: [
          FilledButton(
            key: const Key('kyc_ok_button'),
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(l10n.ok),
          ),
        ],
      ),
    );
    return false;
  }

  final wantsVerify = await showDialog<bool>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      icon: const Icon(Icons.verified_user_outlined),
      title: Text(l10n.kycRequiredTitle),
      content: Text(
        reason == KycReason.buyerPhone
            ? l10n.kycRequiredPhoneMessage
            : l10n.kycRequiredFaydaMessage,
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop(false),
          child: Text(l10n.cancel),
        ),
        FilledButton.icon(
          key: const Key('kyc_verify_button'),
          onPressed: () => Navigator.of(dialogContext).pop(true),
          icon: const Icon(Icons.fingerprint),
          label: Text(l10n.verifyWithFayda),
        ),
      ],
    ),
  );
  if (wantsVerify != true || !context.mounted) return false;
  final verified = await Navigator.of(context).push<bool>(
    MaterialPageRoute<bool>(
      builder: verificationScreen ??
          (_) => FaydaVerificationScreen(
                forPayment: true,
                requirePhoneProof: reason == KycReason.buyerPhone,
              ),
    ),
  );
  return verified ?? false;
}
