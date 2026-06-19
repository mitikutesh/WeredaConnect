import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen displaying CBE bank transfer instructions after order creation.
///
/// Shows bank details, transfer reference, and amount.
/// Allows buyer to enter CBE Transaction ID and submit payment proof.
class PaymentInstructionsScreen extends StatefulWidget {
  final Order order;

  const PaymentInstructionsScreen({
    required this.order,
    super.key,
  });

  @override
  State<PaymentInstructionsScreen> createState() =>
      _PaymentInstructionsScreenState();
}

class _PaymentInstructionsScreenState extends State<PaymentInstructionsScreen> {
  final _transactionIdController = TextEditingController();
  final _formKey = GlobalKey<FormState>();

  @override
  void dispose() {
    _transactionIdController.dispose();
    super.dispose();
  }

  String get _reference => widget.order.orderId.substring(0, 8).toUpperCase();

  void _submitPayment() {
    if (!_formKey.currentState!.validate()) return;

    context.read<OrderBloc>().add(
          SubmitPayment(
            orderId: widget.order.orderId,
            cbeTransactionId: _transactionIdController.text.trim(),
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final amount = widget.order.orderTotal ?? widget.order.totalPrice;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.paymentInstructions)),
      body: BlocListener<OrderBloc, OrderState>(
        listener: (context, state) {
          if (state is PaymentSubmitted) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.paymentSubmittedSuccess)),
            );
            // Pop back to the orders list
            Navigator.of(context)
              ..pop() // pop payment instructions
              ..pop(); // pop place order (if still in stack) or crop detail
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        child: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Form(
              key: _formKey,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // ── Instructions ───────────────────────────────────
                  Text(
                    l10n.cbePaymentInstructions,
                    style: theme.textTheme.bodyLarge,
                  ),
                  const SizedBox(height: 24),

                  // ── Bank details card ──────────────────────────────
                  Card(
                    margin: EdgeInsets.zero,
                    child: Padding(
                      padding: const EdgeInsets.all(16),
                      child: Column(
                        children: [
                          _DetailRow(
                            icon: Icons.account_balance,
                            label: l10n.bankName,
                            value: l10n.bankName,
                            showCopy: false,
                          ),
                          const Divider(height: 24),
                          _DetailRow(
                            icon: Icons.credit_card,
                            label: l10n.cbeAccountNumber,
                            value: l10n.cbeAccountValue,
                            showCopy: true,
                          ),
                          const Divider(height: 24),
                          _DetailRow(
                            icon: Icons.tag,
                            label: l10n.transferReference,
                            value: _reference,
                            showCopy: true,
                          ),
                          const Divider(height: 24),
                          _DetailRow(
                            icon: Icons.payments,
                            label: l10n.paymentAmount,
                            value: l10n.etbCurrency(
                              amount.toStringAsFixed(2),
                            ),
                            showCopy: false,
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(height: 24),

                  // ── Transaction ID input ───────────────────────────
                  TextFormField(
                    controller: _transactionIdController,
                    decoration: InputDecoration(
                      labelText: l10n.enterTransactionId,
                      border: const OutlineInputBorder(),
                      prefixIcon: const Icon(Icons.receipt_long),
                    ),
                    validator: (value) {
                      if (value == null || value.trim().isEmpty) {
                        return l10n.transactionIdRequired;
                      }
                      return null;
                    },
                  ),
                  const SizedBox(height: 24),

                  // ── Action buttons ─────────────────────────────────
                  BlocBuilder<OrderBloc, OrderState>(
                    builder: (context, state) {
                      final isLoading = state is OrderLoading;
                      return Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          FilledButton(
                            onPressed: isLoading ? null : _submitPayment,
                            style: FilledButton.styleFrom(
                              minimumSize: const Size.fromHeight(48),
                            ),
                            child: isLoading
                                ? const SizedBox(
                                    height: 20,
                                    width: 20,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                      color: Colors.white,
                                    ),
                                  )
                                : Text(l10n.submitPayment),
                          ),
                          const SizedBox(height: 12),
                          TextButton(
                            onPressed: isLoading
                                ? null
                                : () => Navigator.of(context).pop(),
                            style: TextButton.styleFrom(
                              minimumSize: const Size.fromHeight(48),
                            ),
                            child: Text(l10n.payLater),
                          ),
                        ],
                      );
                    },
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final bool showCopy;

  const _DetailRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.showCopy,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        Icon(icon, size: 20, color: theme.colorScheme.primary),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                label,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
              const SizedBox(height: 2),
              Text(
                value,
                style: theme.textTheme.bodyLarge
                    ?.copyWith(fontWeight: FontWeight.w500),
              ),
            ],
          ),
        ),
        if (showCopy)
          IconButton(
            icon: const Icon(Icons.copy, size: 18),
            onPressed: () {
              Clipboard.setData(ClipboardData(text: value));
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(
                  content: Text(
                    AppLocalizations.of(context)!.copiedToClipboard,
                  ),
                ),
              );
            },
          ),
      ],
    );
  }
}
