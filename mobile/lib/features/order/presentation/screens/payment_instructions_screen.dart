import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/widgets/kyc_required_dialog.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for paying for an order after it is created.
///
/// Buyers choose a payment method — Commercial Bank of Ethiopia (CBE) or
/// Telebirr. Both are currently **mocked** (demo mode): the buyer may enter a
/// real reference, but if left blank a mock reference is generated so the order
/// can progress without a real transfer. See [_PaymentMethod].
class PaymentInstructionsScreen extends StatefulWidget {
  final Order order;

  /// Replaces the Fayda screen the KYC prompt opens (tests).
  final WidgetBuilder? kycVerificationScreen;

  const PaymentInstructionsScreen({
    required this.order,
    this.kycVerificationScreen,
    super.key,
  });

  @override
  State<PaymentInstructionsScreen> createState() =>
      _PaymentInstructionsScreenState();
}

/// Supported (mocked) payment rails for an order.
enum _PaymentMethod {
  cbe('cbe_transfer'),
  telebirr('telebirr');

  const _PaymentMethod(this.wireValue);

  /// Value persisted on the order + sent to the server.
  final String wireValue;
}

class _PaymentInstructionsScreenState extends State<PaymentInstructionsScreen> {
  final _transactionIdController = TextEditingController();

  _PaymentMethod _method = _PaymentMethod.cbe;

  @override
  void dispose() {
    _transactionIdController.dispose();
    super.dispose();
  }

  String get _reference => widget.order.orderId.substring(0, 8).toUpperCase();

  void _submitPayment() {
    // Demo mode: both rails are mocked. Use the entered reference if the buyer
    // supplied one, otherwise generate a mock reference so the order can
    // progress (a real integration would collect a genuine transaction id).
    final entered = _transactionIdController.text.trim();
    final reference = entered.isNotEmpty
        ? entered
        : '${_method == _PaymentMethod.telebirr ? 'MOCK-TBR' : 'MOCK-CBE'}'
            '-$_reference';

    context.read<OrderBloc>().add(
          SubmitPayment(
            orderId: widget.order.orderId,
            cbeTransactionId: reference,
            paymentMethod: _method.wireValue,
          ),
        );
  }

  Future<void> _retryAfterStepUp() async {
    final ok = await showStepUpDialog(context);
    if (ok && mounted) _submitPayment();
  }

  /// KYC gate: verified as this account's owner → send the same payment
  /// again; anything else → the payment stays unsent.
  Future<void> _retryAfterKyc(String message) async {
    final reason = KycReason.fromMessage(message);
    final verified = await showKycRequiredDialog(
      context,
      reason: reason,
      verificationScreen: widget.kycVerificationScreen,
    );
    if (!mounted) return;
    if (verified) {
      _submitPayment();
    } else if (reason.buyerCanFix) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(AppLocalizations.of(context)!.kycPaymentNotSent),
        ),
      );
    }
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
          } else if (state is OrderError &&
              state.code == AppErrorCode.stepUpRequired) {
            // Web submits straight to /sync: confirm the password, then
            // submit the same payment again.
            _retryAfterStepUp();
          } else if (state is OrderError &&
              state.code == AppErrorCode.kycRequired) {
            _retryAfterKyc(state.message);
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        child: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // ── Payment method selector ─────────────────────────
                Text(
                  l10n.selectPaymentMethod,
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 8),
                Card(
                  margin: EdgeInsets.zero,
                  child: Column(
                    children: [
                      _PaymentMethodTile(
                        icon: Icons.account_balance,
                        label: l10n.paymentMethodCbe,
                        selected: _method == _PaymentMethod.cbe,
                        onTap: () =>
                            setState(() => _method = _PaymentMethod.cbe),
                      ),
                      const Divider(height: 1),
                      _PaymentMethodTile(
                        icon: Icons.phone_android,
                        label: l10n.paymentMethodTelebirr,
                        selected: _method == _PaymentMethod.telebirr,
                        onTap: () =>
                            setState(() => _method = _PaymentMethod.telebirr),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 16),

                // ── Demo/mock notice ────────────────────────────────
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.tertiaryContainer
                        .withValues(alpha: 0.4),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Row(
                    children: [
                      Icon(
                        Icons.info_outline,
                        size: 18,
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.7),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          l10n.mockPaymentNotice,
                          style: theme.textTheme.bodySmall,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 24),

                // ── Method-specific content ─────────────────────────
                if (_method == _PaymentMethod.cbe) ...[
                  Text(
                    l10n.cbePaymentInstructions,
                    style: theme.textTheme.bodyLarge,
                  ),
                  const SizedBox(height: 24),
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

                  // Optional transaction id (mock generates one when blank).
                  TextField(
                    controller: _transactionIdController,
                    decoration: InputDecoration(
                      labelText: l10n.transactionIdOptional,
                      border: const OutlineInputBorder(),
                      prefixIcon: const Icon(Icons.receipt_long),
                    ),
                  ),
                  const SizedBox(height: 24),
                ] else ...[
                  Text(
                    l10n.telebirrPayInstructions,
                    style: theme.textTheme.bodyLarge,
                  ),
                  const SizedBox(height: 24),
                  Card(
                    margin: EdgeInsets.zero,
                    child: Padding(
                      padding: const EdgeInsets.all(16),
                      child: Column(
                        children: [
                          _DetailRow(
                            icon: Icons.phone_android,
                            label: l10n.paymentMethodTelebirr,
                            value: l10n.telebirrPayment,
                            showCopy: false,
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
                ],

                // ── Action buttons ──────────────────────────────────
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
    );
  }
}

/// A selectable payment-method row (radio-style, without the deprecated
/// RadioListTile group API).
class _PaymentMethodTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _PaymentMethodTile({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ListTile(
      onTap: onTap,
      leading: Icon(icon),
      title: Text(label),
      trailing: Icon(
        selected ? Icons.radio_button_checked : Icons.radio_button_off,
        color: selected
            ? theme.colorScheme.primary
            : theme.colorScheme.onSurface.withValues(alpha: 0.4),
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
