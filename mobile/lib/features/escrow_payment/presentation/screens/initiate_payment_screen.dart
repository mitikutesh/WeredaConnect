import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_cards.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Buyer payment initiation screen with commission breakdown (a sibling of
/// the Buyer · Telebirr payment board: the amount in the green card, the
/// breakdown card, the escrow and Fayda notes, Confirm pinned at the foot).
///
/// Supports both online (immediate) and offline (queued intent) flows.
class InitiatePaymentScreen extends StatelessWidget {
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final String farmerName;
  final double amount;

  /// Replaces the Fayda screen the Fayda step-up opens (tests).
  final WidgetBuilder? faydaVerificationScreen;

  const InitiatePaymentScreen({
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.farmerName,
    required this.amount,
    this.faydaVerificationScreen,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final commission = max(amount * 0.025, 5.0);
    final farmerPayout = amount - commission;
    final shortId =
        deliveryId.length > 8 ? deliveryId.substring(0, 8) : deliveryId;

    return BlocListener<EscrowBloc, EscrowState>(
      listener: (context, state) {
        if (state is PaymentInitiated) {
          final toPayUrl = state.toPayUrl;
          if (toPayUrl != null && toPayUrl.isNotEmpty) {
            // Async (real Telebirr) gateway: hand off to the status screen,
            // which opens the Telebirr H5 checkout and polls GET /escrow/{id}
            // until the webhook flips it to `escrowed`.
            Navigator.of(context).pushReplacement(
              MaterialPageRoute<void>(
                builder: (_) => EscrowStatusScreen(
                  escrowId: state.payment.paymentId,
                  awaitingPayment: true,
                  toPayUrl: toPayUrl,
                ),
              ),
            );
          } else {
            // Mock/sync gateway: funds already held (`escrowed`).
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(
                content: Text(l10n.escrowPaymentSuccess),
              ),
            );
            Navigator.of(context).pushReplacement(
              MaterialPageRoute<void>(
                builder: (_) => EscrowStatusScreen(
                  escrowId: state.payment.paymentId,
                ),
              ),
            );
          }
        } else if (state is PaymentQueued) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.escrowPaymentQueued),
            ),
          );
          Navigator.of(context).pop();
        } else if (state is EscrowError && state.code == stepUpRequiredCode) {
          // Payment with an old session: confirm the password, then send the
          // same payment again.
          _retryAfterStepUp(context);
        } else if (state is EscrowError &&
            state.code == faydaStepUpRequiredCode) {
          // Above 10,000 ETB: a fresh Fayda login instead of the password.
          _retryAfterFaydaStepUp(context);
        } else if (state is EscrowError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeEscrowError(
                  AppLocalizations.of(context)!,
                  state.code,
                  state.message,
                ),
              ),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        body: SafeArea(
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FwDetailHeader(title: l10n.escrowPayForDelivery),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // The amount, with who is paid for which delivery.
                        PaymentAmountCard(
                          amount: amount,
                          detail: '${l10n.escrowPaymentTo(farmerName)} · '
                              '${l10n.escrowDeliveryNumber(shortId)}',
                        ),
                        const SizedBox(height: 14),

                        // Amount breakdown card
                        FwCard(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              FwMoneyRow(
                                label: l10n.escrowTotalAmount,
                                amount: l10n.etbCurrency(
                                  formatEtbAmount(amount),
                                ),
                              ),
                              const SizedBox(height: 9),
                              FwMoneyRow(
                                label: l10n.escrowCommission(
                                  (commission / amount * 100)
                                      .toStringAsFixed(1),
                                ),
                                amount: l10n.etbCurrency(
                                  formatEtbAmount(commission),
                                ),
                              ),
                              const SizedBox(height: 9),
                              FwMoneyRow(
                                label: l10n.escrowFarmerReceives,
                                amount: l10n.etbCurrency(
                                  formatEtbAmount(farmerPayout),
                                ),
                                total: true,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 14),

                        // Escrow protection info
                        FwNoteBox(
                          icon: Icons.shield,
                          child: Text(l10n.escrowProtectionInfo),
                        ),

                        // Fayda ID warning (advisory, server enforces)
                        if (amount > 10000) ...[
                          const SizedBox(height: 12),
                          FwNoteBox.cream(
                            icon: Icons.warning_amber,
                            child: Text(l10n.escrowFaydaWarning),
                          ),
                        ],
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        // Confirm, pinned at the foot (56dp tall, a large touch target).
        bottomNavigationBar: BlocBuilder<EscrowBloc, EscrowState>(
          builder: (context, state) {
            final isLoading = state is EscrowLoading;
            return FwBottomActions(
              children: [
                FilledButton.icon(
                  onPressed:
                      isLoading ? null : () => _showConfirmation(context),
                  icon: isLoading
                      ? const SizedBox(
                          height: 20,
                          width: 20,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            valueColor: AlwaysStoppedAnimation<Color>(
                              Colors.white,
                            ),
                          ),
                        )
                      : const Icon(Icons.check),
                  label: Text(l10n.escrowConfirmPayment),
                  style: FilledButton.styleFrom(
                    // The money button is soil dark, as on the board.
                    backgroundColor: AppTheme.soilDark,
                    minimumSize: const Size.fromHeight(56),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
                FwActionNote(l10n.escrowFundsHeldInfo),
              ],
            );
          },
        ),
      ),
    );
  }

  InitiatePayment get _paymentEvent => InitiatePayment(
        deliveryId: deliveryId,
        buyerId: buyerId,
        farmerId: farmerId,
        amount: amount,
      );

  Future<void> _retryAfterStepUp(BuildContext context) async {
    final ok = await showStepUpDialog(context);
    if (ok && context.mounted) {
      context.read<EscrowBloc>().add(_paymentEvent);
    }
  }

  Future<void> _retryAfterFaydaStepUp(BuildContext context) async {
    final outcome = await showFaydaStepUpDialog(
      context,
      verificationScreen: faydaVerificationScreen,
    );
    if (outcome == StepUpOutcome.verified && context.mounted) {
      context.read<EscrowBloc>().add(_paymentEvent);
    }
  }

  void _showConfirmation(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        final dl10n = AppLocalizations.of(context)!;
        return AlertDialog(
          title: Text(dl10n.escrowConfirmPayment),
          content: Text(
            dl10n.escrowConfirmPaymentDialog(
              formatEtbAmount(amount),
              farmerName,
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(dl10n.cancel),
            ),
            FilledButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<EscrowBloc>().add(_paymentEvent);
              },
              child: Text(dl10n.escrowPayButton),
            ),
          ],
        );
      },
    );
  }
}
