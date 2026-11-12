import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ESC-31: the confirm a buyer gets before ordering from a farmer or
/// trucker whose public tier is low (spec 4.11, reputation-api.md 1.2).
///
/// Resolves true at once, with no dialog, when neither party is tagged, so
/// ordering is unchanged while the server sends no reputation copy. With a
/// tag it says who is tagged and why in one plain sentence, and lets the
/// buyer go back or order anyway; dismissing the dialog is going back.
Future<bool> confirmLowReputationOrder(
  BuildContext context, {
  required bool farmerLow,
  required bool truckerLow,
}) async {
  if (!farmerLow && !truckerLow) return true;
  final l10n = AppLocalizations.of(context)!;
  final body = farmerLow && truckerLow
      ? l10n.reputationLowOrderBoth
      : farmerLow
          ? l10n.reputationLowOrderFarmer
          : l10n.reputationLowOrderTrucker;
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      key: const Key('low_reputation_order_dialog'),
      title: Text(l10n.reputationLowOrderTitle),
      content: Text(body),
      actions: [
        TextButton(
          key: const Key('low_reputation_order_back'),
          onPressed: () => Navigator.of(dialogContext).pop(false),
          child: Text(l10n.back),
        ),
        FilledButton(
          key: const Key('low_reputation_order_continue'),
          onPressed: () => Navigator.of(dialogContext).pop(true),
          child: Text(l10n.reputationLowOrderContinue),
        ),
      ],
    ),
  );
  return confirmed ?? false;
}
