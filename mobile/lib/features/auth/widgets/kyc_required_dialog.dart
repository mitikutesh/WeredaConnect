import 'package:flutter/material.dart';

import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Explains a payment blocked by the KYC gate (ORDER_KYC_REQUIRED) and
/// offers to open the Fayda verification screen.
///
/// [phoneNotVerified] picks the "phone not verified" wording over the
/// "Fayda needed above 10,000 ETB" one. Resolves to true when the user went
/// through verification successfully. [verificationScreen] replaces the
/// Fayda screen (tests).
Future<bool> showKycRequiredDialog(
  BuildContext context, {
  bool phoneNotVerified = false,
  WidgetBuilder? verificationScreen,
}) async {
  final l10n = AppLocalizations.of(context)!;
  final wantsVerify = await showDialog<bool>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      icon: const Icon(Icons.verified_user_outlined),
      title: Text(l10n.kycRequiredTitle),
      content: Text(
        phoneNotVerified
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
      builder: verificationScreen ?? (_) => const FaydaVerificationScreen(),
    ),
  );
  return verified ?? false;
}
