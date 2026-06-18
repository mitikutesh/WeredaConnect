import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Buyer payment initiation screen with commission breakdown.
///
/// Shows delivery summary, amount with commission calculation,
/// Fayda ID warning for amounts > 10,000 ETB, and a large confirm button.
/// Supports both online (immediate) and offline (queued intent) flows.
class InitiatePaymentScreen extends StatelessWidget {
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final String farmerName;
  final double amount;

  const InitiatePaymentScreen({
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.farmerName,
    required this.amount,
    super.key,
  });

  static final _formatter = NumberFormat('#,##0.00', 'en_US');

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final commission = max(amount * 0.025, 5.0);
    final farmerPayout = amount - commission;

    return BlocListener<EscrowBloc, EscrowState>(
      listener: (context, state) {
        if (state is PaymentInitiated) {
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
        } else if (state is PaymentQueued) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.escrowPaymentQueued),
            ),
          );
          Navigator.of(context).pop();
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
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(title: Text(l10n.escrowPayForDelivery)),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // Escrow protection info
                Container(
                  padding: const EdgeInsets.all(16),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.primaryContainer
                        .withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Row(
                    children: [
                      Icon(
                        Icons.shield,
                        size: 48,
                        color: theme.colorScheme.primary,
                      ),
                      const SizedBox(width: 16),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              l10n.escrowPayments,
                              style: theme.textTheme.titleMedium?.copyWith(
                                fontWeight: FontWeight.bold,
                              ),
                            ),
                            const SizedBox(height: 4),
                            Text(
                              l10n.escrowProtectionInfo,
                              style: theme.textTheme.bodyMedium,
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 24),

                // Delivery and farmer info
                Text(
                  l10n.escrowDeliveryNumber(
                    deliveryId.length > 8
                        ? deliveryId.substring(0, 8)
                        : deliveryId,
                  ),
                  style: theme.textTheme.bodyLarge,
                ),
                const SizedBox(height: 4),
                Text(
                  l10n.escrowPaymentTo(farmerName),
                  style: theme.textTheme.bodyLarge?.copyWith(
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 24),

                // Amount breakdown card
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      children: [
                        _BreakdownRow(
                          label: l10n.escrowTotalAmount,
                          value:
                              l10n.escrowEtbAmount(_formatter.format(amount)),
                          style: theme.textTheme.headlineSmall?.copyWith(
                            fontWeight: FontWeight.bold,
                          ),
                        ),
                        const Divider(height: 24),
                        _BreakdownRow(
                          label: l10n.escrowCommission(
                            (commission / amount * 100).toStringAsFixed(1),
                          ),
                          value: l10n.escrowEtbAmount(
                            _formatter.format(commission),
                          ),
                          style: theme.textTheme.bodyLarge?.copyWith(
                            color: theme.colorScheme.onSurface
                                .withValues(alpha: 0.6),
                          ),
                        ),
                        const Divider(height: 24),
                        _BreakdownRow(
                          label: l10n.escrowFarmerReceives,
                          value: l10n.escrowEtbAmount(
                            _formatter.format(farmerPayout),
                          ),
                          style: theme.textTheme.titleLarge?.copyWith(
                            color: Colors.green.shade700,
                            fontWeight: FontWeight.bold,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 16),

                // Fayda ID warning (advisory, server enforces)
                if (amount > 10000)
                  Container(
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(
                      color: Colors.orange.withValues(alpha: 0.1),
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(
                        color: Colors.orange.withValues(alpha: 0.3),
                      ),
                    ),
                    child: Row(
                      children: [
                        const Icon(Icons.warning_amber, color: Colors.orange),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Text(
                            l10n.escrowFaydaWarning,
                            style: theme.textTheme.bodyMedium?.copyWith(
                              color: Colors.orange.shade800,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                const SizedBox(height: 32),

                // Confirm button (56dp height — large touch target)
                BlocBuilder<EscrowBloc, EscrowState>(
                  builder: (context, state) {
                    final isLoading = state is EscrowLoading;
                    return FilledButton.icon(
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
                        minimumSize: const Size.fromHeight(56),
                        textStyle: const TextStyle(fontSize: 18),
                      ),
                    );
                  },
                ),
                const SizedBox(height: 16),

                // Info footer
                Row(
                  children: [
                    Icon(
                      Icons.info_outline,
                      size: 16,
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        l10n.escrowFundsHeldInfo,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurface
                              .withValues(alpha: 0.5),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
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
              _formatter.format(amount),
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
                context.read<EscrowBloc>().add(
                      InitiatePayment(
                        deliveryId: deliveryId,
                        buyerId: buyerId,
                        farmerId: farmerId,
                        amount: amount,
                      ),
                    );
              },
              child: Text(dl10n.escrowPayButton),
            ),
          ],
        );
      },
    );
  }
}

class _BreakdownRow extends StatelessWidget {
  final String label;
  final String value;
  final TextStyle? style;

  const _BreakdownRow({
    required this.label,
    required this.value,
    this.style,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(label, style: Theme.of(context).textTheme.bodyLarge),
        Text(value, style: style),
      ],
    );
  }
}
