import 'package:flutter/material.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

// The cards the Buyer · Telebirr payment board shares between the payment
// screens: the green amount card and "Confirm it's you".

/// The amount in the board's green card: "Amount", the sum large, and
/// what the order is with where the money sits.
class PaymentAmountCard extends StatelessWidget {
  final double amount;

  /// The line under the amount ("Wheat 1,500 kg · held in escrow until you
  /// confirm delivery"); the escrow note alone when null.
  final String? detail;

  const PaymentAmountCard({required this.amount, this.detail, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: AppTheme.fieldGreen,
        borderRadius: BorderRadius.circular(24),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.paymentAmount,
            style: const TextStyle(fontSize: 13, color: Color(0xFFCFE3D6)),
          ),
          const SizedBox(height: 4),
          Text(
            l10n.etbCurrency(formatEtbAmount(amount)),
            style: const TextStyle(
              fontSize: 36,
              fontWeight: FontWeight.w800,
              letterSpacing: -1,
              height: 1.15,
              color: Colors.white,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            detail ?? l10n.checkoutHeldInEscrowNote,
            style: const TextStyle(
              fontSize: 13,
              height: 1.4,
              color: Color(0xFFCFE3D6),
            ),
          ),
        ],
      ),
    );
  }
}

/// "Confirm it's you": payments over 10,000 ETB need Fayda, smaller ones
/// the password (AUTH_041 / AUTH_040). [method] names the one this payment
/// will use ("Confirmed with Fayda") under the title.
class ConfirmItsYouCard extends StatelessWidget {
  final String? method;

  const ConfirmItsYouCard({this.method, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      key: const Key('checkout_confirm_its_you'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: AppTheme.whisperBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: AppTheme.fieldGreenTint,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: const Icon(
                  Icons.verified_user_outlined,
                  size: 20,
                  color: AppTheme.fieldGreen,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      l10n.stepUpTitle,
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    if (method != null)
                      Text(
                        method!,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.fieldGreen,
                        ),
                      ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            l10n.checkoutConfirmItsYouNote,
            style: const TextStyle(
              fontSize: 13,
              height: 1.5,
              color: AppTheme.mutedSteel,
            ),
          ),
        ],
      ),
    );
  }
}

/// The board's status card: the step (a spinner, a check or an error
/// glyph) in bold, lines under it, and the card's own actions.
class PaymentStatusCard extends StatelessWidget {
  final String text;
  final bool busy;
  final bool error;
  final bool done;
  final List<Widget> children;

  const PaymentStatusCard({
    required this.text,
    this.busy = false,
    this.error = false,
    this.done = false,
    this.children = const [],
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final Widget leading;
    if (busy) {
      leading = const SizedBox(
        height: 22,
        width: 22,
        child: CircularProgressIndicator(
          strokeWidth: 3,
          color: AppTheme.accentSoft,
          backgroundColor: AppTheme.whisperBorder,
        ),
      );
    } else if (done) {
      leading = const Icon(
        Icons.check_circle,
        size: 22,
        color: AppTheme.statusSuccess,
      );
    } else {
      leading = Icon(
        error ? Icons.error_outline : Icons.info_outline,
        size: 22,
        color: error ? AppTheme.statusError : AppTheme.fieldGreen,
      );
    }
    return Semantics(
      liveRegion: true,
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: AppTheme.whisperBorder),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Padding(
                  padding: const EdgeInsets.only(top: 1),
                  child: leading,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    text,
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                      height: 1.4,
                      color: error ? AppTheme.statusError : AppTheme.soilDark,
                    ),
                  ),
                ),
              ],
            ),
            for (final child in children) ...[
              const SizedBox(height: 12),
              child,
            ],
          ],
        ),
      ),
    );
  }
}

/// A button inside a card (44 tall, radius 12): the board's "Check payment
/// status".
ButtonStyle paymentCardButtonStyle() => OutlinedButton.styleFrom(
      minimumSize: const Size.fromHeight(44),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      textStyle: const TextStyle(
        fontFamily: AppTheme.fontFamily,
        fontFamilyFallback: AppTheme.ethiopicFontFallback,
        fontSize: 14,
        fontWeight: FontWeight.w700,
      ),
    );
