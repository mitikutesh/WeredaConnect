import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/widgets/kyc_required_dialog.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_checkout_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_cards.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Screen for paying for an order after it is created (the PaymentM board:
/// the amount on the raised card, how the payment confirms it is the buyer,
/// and the orange Pay pinned at the foot). A payment that could not be sent
/// for lack of a network takes the page as the PaymentOfflineM state.
///
/// Demo builds: Telebirr is the default method. The Commercial Bank of
/// Ethiopia (CBE) transfer is offered beside it only when a real account is
/// configured ([realCbeAccount], BUG-29). Both are **mocked**: the CBE buyer
/// may enter a real reference, but if left blank the mock reference
/// `MOCK-CBE-{orderId}` / `MOCK-TBR-{orderId}` is sent so the order can
/// progress without a real transfer. See [_PaymentMethod].
///
/// Live builds ([Environment.telebirrLive], ESC-44): Telebirr only (bank
/// transfers are ESC-46), no reference fields, and a total outside what one
/// Telebirr payment takes cannot be sent. Once the payment is submitted the
/// [OrderCheckoutScreen] opens Telebirr.
class PaymentInstructionsScreen extends StatefulWidget {
  final Order order;

  /// Replaces the Fayda screen the KYC and Fayda step-up prompts open (tests).
  final WidgetBuilder? kycVerificationScreen;

  /// Live Telebirr checkout instead of the demo screen. Defaults to
  /// [Environment.telebirrLive]; tests set it.
  final bool? telebirrLive;

  /// The order detail is the page under this one (Pay Now on the order):
  /// a paid order closes this screen and nothing else. Without it (straight
  /// from Place order) the paid order's detail takes this screen's place.
  /// Either way the buyer ends on the order (BUG-17, BUG-21).
  final bool fromOrderDetail;

  /// The CBE account buyers transfer to. Null reads the configured one
  /// (the `cbeAccountValue` string); tests set it. See [realCbeAccount].
  final String? cbeAccount;

  const PaymentInstructionsScreen({
    required this.order,
    this.kycVerificationScreen,
    this.telebirrLive,
    this.fromOrderDetail = false,
    this.cbeAccount,
    super.key,
  });

  @override
  State<PaymentInstructionsScreen> createState() =>
      _PaymentInstructionsScreenState();
}

/// [configured] when it is an account number a buyer can transfer to (digits
/// only, spaces and hyphens aside, at least 6 of them), else null. The
/// shipped value is the placeholder `1000XXXXXXXXXX`: with it, or with
/// nothing, the bank transfer is not offered at all, so no buyer is shown an
/// account that does not exist (BUG-29).
String? realCbeAccount(String? configured) {
  final account = configured?.trim() ?? '';
  final digits = account.replaceAll(RegExp(r'[\s-]'), '');
  return RegExp(r'^\d{6,}$').hasMatch(digits) ? account : null;
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

  late final bool _live = widget.telebirrLive ?? Environment.telebirrLive;

  /// Telebirr is the default wherever both are offered (BUG-29); the bank
  /// transfer is the buyer's own pick, and only with a real account.
  _PaymentMethod _method = _PaymentMethod.telebirr;

  /// What one Telebirr payment can take (UX check; the server enforces it).
  static const double _telebirrMinEtb = 100;
  static const double _telebirrMaxEtb = 500000;

  double get _amount => widget.order.orderTotal ?? widget.order.totalPrice;

  bool get _withinTelebirrLimits {
    final total = double.parse(_amount.toStringAsFixed(2));
    return total >= _telebirrMinEtb && total <= _telebirrMaxEtb;
  }

  @override
  void dispose() {
    _transactionIdController.dispose();
    super.dispose();
  }

  /// The transfer reference is the full order id, exactly as stored. It is
  /// unique per order and it is what the server keys the order payment on
  /// (`escrow-order-{orderId}`, the order checkout mapping). It used to be the
  /// first 8 characters upper-cased, which repeated across orders (SIM-54).
  /// A live Telebirr payment is matched by the server's merch_order_id, never
  /// by a reference the buyer types.
  String get _reference => widget.order.orderId;

  void _submitPayment() {
    if (_live) {
      // The server's checkout matches a live payment; no reference is sent.
      context.read<OrderBloc>().add(
            SubmitPayment(
              orderId: widget.order.orderId,
              paymentMethod: _PaymentMethod.telebirr.wireValue,
            ),
          );
      return;
    }
    // Demo mode: both rails are mocked. Use the entered reference if the buyer
    // supplied one, otherwise send MOCK-CBE-{orderId} / MOCK-TBR-{orderId} so
    // the order can progress (a real integration would collect a genuine
    // transaction id).
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

  /// The last submit did not reach the server (no network, or it timed
  /// out): the page shows the offline state until the buyer tries again or
  /// leaves (BUG-19: the same payment is offered again; the order is read
  /// first, so a payment that did arrive is not sent twice).
  bool _offline = false;

  /// Leave the payment for the order, as a paid order does.
  void _backToOrder() {
    if (widget.fromOrderDetail) {
      Navigator.of(context).pop();
    } else {
      replaceWithOrderDetail(context, widget.order.orderId);
    }
  }

  /// A password prompt is open. Only the server knows that a payment needs
  /// a fresh password (AUTH_040: the token's age and the risk tier), so the
  /// first submit is how the client learns it. This keeps the answer to one
  /// prompt and one resubmission (BUG-23).
  bool _stepUpOpen = false;

  Future<void> _retryAfterStepUp() async {
    if (_stepUpOpen) return;
    _stepUpOpen = true;
    final bool ok;
    try {
      ok = await showStepUpDialog(context);
    } finally {
      _stepUpOpen = false;
    }
    if (ok && mounted) _submitPayment();
  }

  /// Above 10,000 ETB the server wants a fresh Fayda login, not the password.
  Future<void> _retryAfterFaydaStepUp() async {
    final outcome = await showFaydaStepUpDialog(
      context,
      verificationScreen: widget.kycVerificationScreen,
    );
    if (outcome == StepUpOutcome.verified && mounted) _submitPayment();
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
    final amount = _amount;
    final cbeAccount =
        realCbeAccount(widget.cbeAccount ?? l10n.cbeAccountValue);

    return Scaffold(
      body: BlocListener<OrderBloc, OrderState>(
        listener: (context, state) {
          if (_offline && state is! OrderError && state is! OrderLoading) {
            setState(() => _offline = false);
          }
          if (state is PaymentSubmitted && _live) {
            // The checkout screen opens Telebirr once the payment is on the
            // server.
            Navigator.of(context).pushReplacement(
              MaterialPageRoute<void>(
                builder: (_) => OrderCheckoutScreen(
                  order: widget.order,
                  fromOrderDetail: widget.fromOrderDetail,
                ),
              ),
            );
          } else if (state is PaymentSubmitted) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.paymentSubmittedSuccess)),
            );
            // End on the order: back to its detail when that is the page
            // underneath, else the detail in this screen's place. Never a
            // second pop: an order opened by its address has no page under
            // its detail (BUG-17).
            if (widget.fromOrderDetail) {
              Navigator.of(context).pop();
            } else {
              replaceWithOrderDetail(context, widget.order.orderId);
            }
          } else if (state is OrderError &&
              state.code == AppErrorCode.stepUpRequired) {
            // Web submits straight to /sync: confirm the password, then
            // submit the same payment again.
            _retryAfterStepUp();
          } else if (state is OrderError &&
              state.code == AppErrorCode.faydaStepUpRequired) {
            _retryAfterFaydaStepUp();
          } else if (state is OrderError && isKycErrorCode(state.code)) {
            // Every KYC refusal opens the dialog: it offers Fayda only when
            // the buyer must verify, and names the farmer otherwise (SIM-49).
            _retryAfterKyc(state.message);
          } else if (state is OrderError &&
              (state.code == AppErrorCode.network ||
                  state.code == AppErrorCode.timeout)) {
            // BUG-19: a lost connection says so, as the full offline state,
            // and offers the same payment again.
            setState(() => _offline = true);
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        child: _offline
            ? SafeArea(
                child: BlocBuilder<OrderBloc, OrderState>(
                  builder: (context, state) => PaymentStateView.offline(
                    l10n: l10n,
                    onTryAgain: state is OrderLoading ? null : _submitPayment,
                    onBackToOrder: _backToOrder,
                  ),
                ),
              )
            : SafeArea(
                bottom: false,
                child: SingleChildScrollView(
                  child: FwContentWidth(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // The board closes the payment with an X, not Back.
                        PaymentHeader(
                          title: _live
                              ? l10n.orderCheckoutTitle
                              : l10n.paymentInstructions,
                          onClose: () => Navigator.of(context).pop(),
                        ),
                        Padding(
                          padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              PaymentAmountCard(
                                amount: amount,
                                detail: paymentAmountDetail(widget.order, l10n),
                              ),
                              const SizedBox(height: 18),
                              if (_live) ...[
                                // Telebirr payment board: how the payment asks the
                                // buyer to confirm it is them.
                                const ConfirmItsYouCard(),
                                if (!_withinTelebirrLimits) ...[
                                  const SizedBox(height: 14),
                                  Text(
                                    l10n.paymentTotalOutOfRange,
                                    style: theme.textTheme.bodyMedium?.copyWith(
                                      color: theme.colorScheme.error,
                                    ),
                                  ),
                                ],
                              ] else ...[
                                // ── Payment method selector ───────────────────
                                Text(
                                  l10n.selectPaymentMethod,
                                  style: _cardTitle,
                                ),
                                const SizedBox(height: 8),
                                FwCard(
                                  padding: EdgeInsets.zero,
                                  child: Column(
                                    children: [
                                      // Telebirr first: it is the default.
                                      _PaymentMethodTile(
                                        icon: Icons.phone_android,
                                        label: l10n.paymentMethodTelebirr,
                                        selected:
                                            _method == _PaymentMethod.telebirr,
                                        onTap: () => setState(
                                          () =>
                                              _method = _PaymentMethod.telebirr,
                                        ),
                                      ),
                                      // No real account, no bank transfer.
                                      if (cbeAccount != null) ...[
                                        const Divider(height: 1),
                                        _PaymentMethodTile(
                                          icon: Icons.account_balance,
                                          label: l10n.paymentMethodCbe,
                                          selected:
                                              _method == _PaymentMethod.cbe,
                                          onTap: () => setState(
                                            () => _method = _PaymentMethod.cbe,
                                          ),
                                        ),
                                      ],
                                    ],
                                  ),
                                ),
                                const SizedBox(height: 14),

                                // ── Demo/mock notice ──────────────────────────
                                FwNoteBox.cream(
                                  icon: Icons.info_outline,
                                  child: Text(l10n.mockPaymentNotice),
                                ),
                                const SizedBox(height: 14),

                                // ── Method-specific content ───────────────────
                                if (_method == _PaymentMethod.cbe &&
                                    cbeAccount != null) ...[
                                  Text(
                                    l10n.cbePaymentInstructions,
                                    style: theme.textTheme.bodyMedium,
                                  ),
                                  const SizedBox(height: 14),
                                  FwCard(
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
                                          value: cbeAccount,
                                          showCopy: true,
                                        ),
                                        const Divider(height: 24),
                                        _DetailRow(
                                          icon: Icons.tag,
                                          label: l10n.transferReference,
                                          value: _reference,
                                          showCopy: true,
                                        ),
                                      ],
                                    ),
                                  ),
                                  const SizedBox(height: 14),

                                  // Optional transaction id (mock generates one
                                  // when blank).
                                  TextField(
                                    controller: _transactionIdController,
                                    decoration: InputDecoration(
                                      labelText: l10n.transactionIdOptional,
                                      prefixIcon:
                                          const Icon(Icons.receipt_long),
                                    ),
                                  ),
                                ] else ...[
                                  Text(
                                    l10n.telebirrPayInstructions,
                                    style: theme.textTheme.bodyMedium,
                                  ),
                                  const SizedBox(height: 14),
                                  FwCard(
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
                                      ],
                                    ),
                                  ),
                                ],
                              ],
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
      ),
      // ── Pay, pinned at the foot ────────────────────────────────────
      bottomNavigationBar: _offline
          ? null
          : BlocBuilder<OrderBloc, OrderState>(
              builder: (context, state) {
                final isLoading = state is OrderLoading;
                final blocked = _live && !_withinTelebirrLimits;
                return FwBottomActions(
                  children: [
                    FilledButton(
                      // The page's one orange action (the theme's button).
                      onPressed: isLoading || blocked ? null : _submitPayment,
                      child: isLoading
                          ? const SizedBox(
                              height: 20,
                              width: 20,
                              child: CircularProgressIndicator(
                                strokeWidth: 2,
                                color: AppTheme.ink,
                              ),
                            )
                          : Text(
                              _live ? l10n.payWithTelebirr : l10n.submitPayment,
                            ),
                    ),
                    const SizedBox(height: 8),
                    OutlinedButton(
                      onPressed:
                          isLoading ? null : () => Navigator.of(context).pop(),
                      style: paymentSecondaryButtonStyle(),
                      child: Text(l10n.payLater),
                    ),
                    FwActionNote(l10n.escrowErrorOffline),
                  ],
                );
              },
            ),
    );
  }
}

const TextStyle _cardTitle = FwText.cardTitle;

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
    return ListTile(
      onTap: onTap,
      leading: Icon(icon),
      title: Text(label),
      trailing: Icon(
        selected ? Icons.radio_button_checked : Icons.radio_button_off,
        // The picked method's radio is black, as the join steps' radios.
        color: selected ? AppTheme.ink : AppTheme.fieldBorder,
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
            tooltip: AppLocalizations.of(context)!.copy,
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
