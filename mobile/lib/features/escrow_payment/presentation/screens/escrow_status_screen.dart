import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/escrow_payment/domain/payout_expectation.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/raise_dispute_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/payout_expected_text.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Escrow detail screen showing status, amount, timeline, and actions, laid
/// out like the boards' pushed pages: a back square and the title, then
/// white cards with uppercase section labels, in the phone-width column.
///
/// Loads a single escrow by ID or delivery ID. Supports pull-to-refresh
/// and shows a stale data indicator when cache is older than 1 hour.
class EscrowStatusScreen extends StatefulWidget {
  final String? escrowId;
  final String? deliveryId;

  /// When true (buyer just initiated an async Telebirr payment), auto-open the
  /// Telebirr H5 checkout and poll GET /escrow/{id} until it leaves `initiated`.
  final bool awaitingPayment;

  /// Telebirr H5 checkout URL to (re)open while awaiting payment. Only set on
  /// the buyer's post-initiate hand-off for a real (async) gateway.
  final String? toPayUrl;

  const EscrowStatusScreen({
    this.escrowId,
    this.deliveryId,
    this.awaitingPayment = false,
    this.toPayUrl,
    super.key,
  }) : assert(escrowId != null || deliveryId != null);

  @override
  State<EscrowStatusScreen> createState() => _EscrowStatusScreenState();
}

class _EscrowStatusScreenState extends State<EscrowStatusScreen>
    with WidgetsBindingObserver {
  // Poll the escrow while the buyer completes payment in Telebirr. The webhook
  // (server-side) is the source of truth; polling just surfaces the flip to the
  // buyer. Bounded so a never-completed payment doesn't poll forever.
  static const _pollInterval = Duration(seconds: 4);
  static const _pollMaxDuration = Duration(minutes: 3);

  Timer? _pollTimer;
  DateTime? _pollStartedAt;
  StreamSubscription<Uri>? _linkSub;
  // Set once the escrow leaves `initiated`; stops any further polling/refresh.
  bool _resolved = false;
  bool _watchingReturn = false;

  @override
  void initState() {
    super.initState();
    _loadEscrow();
    if (widget.awaitingPayment && widget.escrowId != null) {
      // Open Telebirr after first frame (needs a valid context for messenger).
      WidgetsBinding.instance.addPostFrameCallback((_) => _launchTelebirr());
      _startPolling();
      // Refresh immediately when the buyer returns, via the deep-link
      // (weredaconnect://payment/complete) or app resume, instead of waiting
      // for the next poll tick.
      WidgetsBinding.instance.addObserver(this);
      TelebirrReturnWatchers.add();
      _watchingReturn = true;
      _linkSub = AppLinks().uriLinkStream.listen(
        (uri) {
          if (isTelebirrReturnLink(uri)) _refreshNow();
        },
        onError: (Object _) {},
      );
    }
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _linkSub?.cancel();
    if (widget.awaitingPayment) {
      WidgetsBinding.instance.removeObserver(this);
    }
    if (_watchingReturn) TelebirrReturnWatchers.remove();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Returning from the Telebirr browser/app: check status right away.
    if (state == AppLifecycleState.resumed && widget.awaitingPayment) {
      _refreshNow();
    }
  }

  /// Fire an immediate silent refresh and make sure polling is running.
  void _refreshNow() {
    if (!mounted || _resolved || widget.escrowId == null) return;
    if (_pollTimer == null || !_pollTimer!.isActive) {
      _startPolling();
    }
    context.read<EscrowBloc>().add(RefreshEscrow(escrowId: widget.escrowId!));
  }

  void _startPolling() {
    _pollStartedAt = DateTime.now();
    _pollTimer?.cancel();
    _pollTimer = Timer.periodic(_pollInterval, (timer) {
      if (!mounted || _resolved || widget.escrowId == null) {
        timer.cancel();
        return;
      }
      // Give up after the cap; the buyer can still pull-to-refresh.
      if (_pollStartedAt != null &&
          DateTime.now().difference(_pollStartedAt!) > _pollMaxDuration) {
        timer.cancel();
        return;
      }
      context.read<EscrowBloc>().add(
            RefreshEscrow(escrowId: widget.escrowId!),
          );
    });
  }

  /// Open the Telebirr H5 checkout in the external browser / Telebirr app.
  Future<void> _launchTelebirr() async {
    await openTelebirrCheckout(context, widget.toPayUrl);
  }

  void _loadEscrow() {
    if (widget.escrowId != null) {
      context.read<EscrowBloc>().add(
            LoadEscrowDetail(escrowId: widget.escrowId!),
          );
    } else if (widget.deliveryId != null) {
      context.read<EscrowBloc>().add(
            LoadEscrowByDelivery(deliveryId: widget.deliveryId!),
          );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          child: Column(
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 16, 20, 8),
                child: Row(
                  children: [
                    FwSquareButton(
                      onTap: () => Navigator.of(context).maybePop(),
                      semanticLabel:
                          MaterialLocalizations.of(context).backButtonTooltip,
                      child: const Icon(
                        Icons.arrow_back,
                        size: 20,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: FwPageHeader(
                        padding: EdgeInsets.zero,
                        title: l10n.escrowPaymentStatus,
                      ),
                    ),
                    const SizedBox(width: 12),
                    FwSquareButton(
                      onTap: _loadEscrow,
                      semanticLabel: l10n.retry,
                      child: const Icon(
                        Icons.refresh,
                        size: 22,
                        color: AppTheme.soilDark,
                      ),
                    ),
                  ],
                ),
              ),
              Expanded(
                child: BlocConsumer<EscrowBloc, EscrowState>(
                  listener: (context, state) {
                    // Stop polling once the escrow resolves; confirm to the
                    // buyer when the payment is captured (initiated → escrowed).
                    if (state is EscrowDetailLoaded &&
                        state.payment.status != PaymentStatus.initiated) {
                      _pollTimer?.cancel();
                      _resolved = true;
                      if (widget.awaitingPayment &&
                          state.payment.status == PaymentStatus.escrowed) {
                        ScaffoldMessenger.of(context).showSnackBar(
                          SnackBar(
                            content: Text(l10n.escrowPaymentConfirmed),
                          ),
                        );
                      }
                    }
                  },
                  builder: (context, state) {
                    if (state is EscrowLoading) {
                      return const Center(child: CircularProgressIndicator());
                    }

                    if (state is EscrowError) {
                      return Center(
                        child: Padding(
                          padding: const EdgeInsets.all(24),
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              const Icon(
                                Icons.error_outline,
                                size: 56,
                                color: AppTheme.statusError,
                              ),
                              const SizedBox(height: 16),
                              Text(
                                localizeEscrowError(
                                  l10n,
                                  state.code,
                                  state.message,
                                ),
                                style: FwText.cardTitle,
                                textAlign: TextAlign.center,
                              ),
                              const SizedBox(height: 16),
                              FilledButton(
                                onPressed: _loadEscrow,
                                child: Text(l10n.retry),
                              ),
                            ],
                          ),
                        ),
                      );
                    }

                    if (state is! EscrowDetailLoaded) {
                      return const Center(child: CircularProgressIndicator());
                    }

                    return _buildContent(context, state.payment);
                  },
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildContent(BuildContext context, Payment payment) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final staleMinutes =
        DateTime.now().toUtc().difference(payment.updatedAt).inMinutes;
    String etb(double? amount) => l10n.etbCurrency(formatEtbAmount(amount ?? 0));

    return RefreshIndicator(
      onRefresh: () async {
        context.read<EscrowBloc>().add(
              RefreshEscrow(escrowId: payment.paymentId),
            );
      },
      child: SingleChildScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(20, 8, 20, 28),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Awaiting-payment banner: buyer initiated an async Telebirr payment
            // and it hasn't been confirmed yet. Offers a re-open action.
            if (widget.awaitingPayment &&
                payment.status == PaymentStatus.initiated) ...[
              _AwaitingPaymentBanner(
                onOpenAgain: widget.toPayUrl != null ? _launchTelebirr : null,
              ),
              const SizedBox(height: 12),
            ],

            // Status badge (large)
            EscrowStatusBadge(
              status: payment.status,
              large: true,
              refundPending: payment.isRefundPending,
            ),
            // FRD-13: the payee's expected payout day. Only the farmer's and
            // the trucker's reads carry the date.
            if (expectedPayoutAt(payment) case final payoutAt?) ...[
              const SizedBox(height: 12),
              PayoutExpectedText(payoutAt: payoutAt, center: true),
            ],
            const SizedBox(height: 20),

            // Amount breakdown
            FwSectionLabel(
              l10n.escrowTotalAmount,
              padding: const EdgeInsets.only(bottom: 8),
            ),
            FwCard(
              child: Column(
                children: [
                  _DetailRow(
                    label: l10n.escrowTotalAmount,
                    value: etb(payment.amount),
                    emphasis: true,
                  ),
                  const Divider(height: 20, color: AppTheme.segmentTrack),
                  _DetailRow(
                    label: l10n.escrowCommission('2.5'),
                    value: etb(payment.commission),
                  ),
                  const SizedBox(height: 8),
                  _DetailRow(
                    label: l10n.escrowFarmerReceives,
                    value: etb(payment.farmerPayout),
                    valueColor: AppTheme.fieldGreen,
                  ),
                  // Transport leg (SIM-15): total = commission + farmer +
                  // trucker. Hidden for orders without a trucker split.
                  if ((payment.truckerPayout ?? 0) > 0) ...[
                    const SizedBox(height: 8),
                    _DetailRow(
                      label: l10n.escrowTruckerReceives,
                      value: etb(payment.truckerPayout),
                      valueColor: AppTheme.fieldGreen,
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 20),

            // Details section
            FwSectionLabel(
              l10n.escrowDeliveryLabel,
              padding: const EdgeInsets.only(bottom: 8),
            ),
            FwCard(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Column(
                children: [
                  // An order-funded escrow has no delivery until the trucker
                  // creates one: name the order instead of a blank "#".
                  _InfoRow(
                    icon: Icons.inventory_2_outlined,
                    label: l10n.escrowDeliveryLabel,
                    value: _reference(payment, l10n),
                  ),
                  _InfoRow(
                    icon: Icons.calendar_today_outlined,
                    label: l10n.escrowCreatedLabel,
                    value: clock.dateTime(payment.createdAt),
                  ),
                  if (payment.expiresAt != null)
                    _InfoRow(
                      icon: Icons.timer_outlined,
                      label: l10n.escrowExpiresLabel,
                      clock: clock.deadline(
                        payment.expiresAt!,
                        date: DateFormat.yMMMd(),
                      ),
                    ),
                  if (payment.telebirrRef != null)
                    _InfoRow(
                      icon: Icons.link,
                      label: l10n.escrowTelebirrRefLabel,
                      value: payment.telebirrRef!.length > 20
                          ? '${payment.telebirrRef!.substring(0, 20)}...'
                          : payment.telebirrRef!,
                    ),
                ],
              ),
            ),
            const SizedBox(height: 20),

            // Timeline
            FwSectionLabel(
              l10n.escrowTimeline,
              padding: const EdgeInsets.only(bottom: 8),
            ),
            FwCard(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 4),
              child: EscrowTimeline(payment: payment),
            ),

            // Dispute info (if disputed)
            if (payment.status == PaymentStatus.disputed) ...[
              const SizedBox(height: 20),
              _DisputeInfoCard(payment: payment),
            ],

            // Raise dispute button (only if escrowed + within 48h window)
            if (_canDispute(payment)) ...[
              const SizedBox(height: 20),
              OutlinedButton.icon(
                onPressed: () {
                  Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => RaiseDisputeScreen(
                        escrowId: payment.paymentId,
                        amount: payment.amount,
                        deliveryId: payment.deliveryId,
                        disputeWindowEndsAt: payment.disputeWindowEndsAt,
                      ),
                    ),
                  );
                },
                icon: const Icon(Icons.warning_amber),
                label: Text(l10n.escrowRaiseDispute),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.onStatusWarningContainer,
                  side: const BorderSide(
                    color: AppTheme.onStatusWarningContainer,
                  ),
                  minimumSize: const Size.fromHeight(52),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(16),
                  ),
                ),
              ),
            ],

            // Stale data indicator (> 1 hour)
            if (staleMinutes > 60) ...[
              const SizedBox(height: 16),
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  const Icon(
                    Icons.info_outline,
                    size: 14,
                    color: AppTheme.mutedSteel,
                  ),
                  const SizedBox(width: 4),
                  Text(l10n.escrowMayBeOutdated, style: FwText.body),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  static String _short(String id) => id.length > 8 ? id.substring(0, 8) : id;

  /// The delivery this escrow backs, else its order, else a dash.
  static String _reference(Payment payment, AppLocalizations l10n) {
    if (payment.deliveryId.isNotEmpty) return '#${_short(payment.deliveryId)}';
    final orderId = payment.orderId;
    if (orderId != null && orderId.isNotEmpty) {
      return l10n.escrowOrderShort(_short(orderId));
    }
    return '-';
  }

  bool _canDispute(Payment payment) {
    if (payment.status != PaymentStatus.escrowed) return false;
    if (payment.disputeWindowEndsAt == null) return false;
    return DateTime.now().toUtc().isBefore(payment.disputeWindowEndsAt!);
  }
}

/// Banner shown while the buyer's async Telebirr payment is pending
/// confirmation. Shows a spinner + explanation and a re-open action.
class _AwaitingPaymentBanner extends StatelessWidget {
  final Future<void> Function()? onOpenAgain;

  const _AwaitingPaymentBanner({this.onOpenAgain});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.actionCream,
        borderRadius: BorderRadius.circular(16),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const SizedBox(
                height: 20,
                width: 20,
                child: CircularProgressIndicator(
                  strokeWidth: 2,
                  color: AppTheme.money,
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: Text(
                  l10n.escrowAwaitingPayment,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.actionInk,
                  ),
                ),
              ),
            ],
          ),
          if (onOpenAgain != null) ...[
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerRight,
              child: TextButton.icon(
                onPressed: () {
                  onOpenAgain!();
                },
                style: TextButton.styleFrom(foregroundColor: AppTheme.money),
                icon: const Icon(Icons.open_in_new, size: 18),
                label: Text(l10n.escrowOpenPaymentAgain),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _DisputeInfoCard extends StatelessWidget {
  final Payment payment;

  const _DisputeInfoCard({required this.payment});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.statusErrorContainer,
        borderRadius: BorderRadius.circular(16),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(
                Icons.warning_amber,
                color: AppTheme.onStatusErrorContainer,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  l10n.escrowDisputeDetailsTitle,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.onStatusErrorContainer,
                  ),
                ),
              ),
            ],
          ),
          if (payment.disputeReason != null) ...[
            const SizedBox(height: 8),
            Text(
              l10n.escrowDisputeReasonPrefix(payment.disputeReason!),
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: AppTheme.onStatusErrorContainer,
              ),
            ),
          ],
          if (payment.disputeDescription != null) ...[
            const SizedBox(height: 4),
            Text(
              '"${payment.disputeDescription}"',
              style: const TextStyle(
                fontSize: 14,
                fontStyle: FontStyle.italic,
                color: AppTheme.onStatusErrorContainer,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  final String label;
  final String value;
  final Color? valueColor;

  /// The total: bigger value, like the boards' "You receive" line.
  final bool emphasis;

  const _DetailRow({
    required this.label,
    required this.value,
    this.valueColor,
    this.emphasis = false,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      crossAxisAlignment: CrossAxisAlignment.baseline,
      textBaseline: TextBaseline.alphabetic,
      children: [
        Flexible(
          child: Text(
            label,
            style: TextStyle(
              fontSize: emphasis ? 13 : 14,
              fontWeight: FontWeight.w500,
              color: emphasis ? AppTheme.mutedSteel : AppTheme.inkSecondary,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Text(
          value,
          style: TextStyle(
            fontSize: emphasis ? 22 : 14,
            fontWeight: emphasis ? FontWeight.w800 : FontWeight.w700,
            letterSpacing: emphasis ? -0.4 : 0,
            color: valueColor ?? AppTheme.soilDark,
          ),
        ),
      ],
    );
  }
}

class _InfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;

  /// A deadline value, shown with its small 24-hour time (LT-03); replaces
  /// [value].
  final ClockLabel? clock;

  const _InfoRow({
    required this.icon,
    required this.label,
    this.value = '',
    this.clock,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Icon(icon, size: 18, color: AppTheme.mutedSteel),
          const SizedBox(width: 12),
          SizedBox(
            width: 84,
            child: Text(label, style: FwText.body),
          ),
          Expanded(
            child: ClockText(
              clock ?? ClockLabel.text(value),
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: AppTheme.soilDark,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
