import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
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

/// Escrow detail screen showing status, amount, timeline, and actions.
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
  static final _amountFormat = NumberFormat('#,##0.00', 'en_US');

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
      // Refresh immediately when the buyer returns — via the deep-link
      // (weredaconnect://payment/complete) or app resume — instead of waiting
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
    // Returning from the Telebirr browser/app — check status right away.
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
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(AppLocalizations.of(context)!.escrowPaymentStatus),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            onPressed: _loadEscrow,
          ),
        ],
      ),
      body: BlocConsumer<EscrowBloc, EscrowState>(
        listener: (context, state) {
          // Stop polling once the escrow resolves; confirm to the buyer when
          // the payment is captured (initiated → escrowed).
          if (state is EscrowDetailLoaded &&
              state.payment.status != PaymentStatus.initiated) {
            _pollTimer?.cancel();
            _resolved = true;
            if (widget.awaitingPayment &&
                state.payment.status == PaymentStatus.escrowed) {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(
                  content: Text(
                    AppLocalizations.of(context)!.escrowPaymentConfirmed,
                  ),
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
                      size: 64,
                      color: AppTheme.statusError,
                    ),
                    const SizedBox(height: 16),
                    Text(
                      localizeEscrowError(
                        AppLocalizations.of(context)!,
                        state.code,
                        state.message,
                      ),
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: 16),
                    FilledButton(
                      onPressed: _loadEscrow,
                      child: Text(AppLocalizations.of(context)!.retry),
                    ),
                  ],
                ),
              ),
            );
          }

          if (state is! EscrowDetailLoaded) {
            return const Center(child: CircularProgressIndicator());
          }

          return _buildContent(context, theme, state.payment);
        },
      ),
    );
  }

  Widget _buildContent(
    BuildContext context,
    ThemeData theme,
    Payment payment,
  ) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final staleMinutes =
        DateTime.now().toUtc().difference(payment.updatedAt).inMinutes;

    return RefreshIndicator(
      onRefresh: () async {
        context.read<EscrowBloc>().add(
              RefreshEscrow(escrowId: payment.paymentId),
            );
      },
      child: SingleChildScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(24),
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
              const SizedBox(height: 16),
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
            const SizedBox(height: 24),

            // Amount breakdown
            Text(
              l10n.escrowTotalAmount,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    _DetailRow(
                      label: l10n.escrowTotalAmount,
                      value: l10n.escrowEtbAmount(
                        _amountFormat.format(payment.amount),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _DetailRow(
                      label: l10n.escrowCommission('2.5'),
                      value: l10n.escrowEtbAmount(
                        _amountFormat.format(payment.commission),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _DetailRow(
                      label: l10n.escrowFarmerReceives,
                      value: l10n.escrowEtbAmount(
                        _amountFormat.format(payment.farmerPayout),
                      ),
                      valueColor: AppTheme.onStatusSuccessContainer,
                    ),
                    // Transport leg (SIM-15): total = commission + farmer +
                    // trucker. Hidden for orders without a trucker split.
                    if ((payment.truckerPayout ?? 0) > 0) ...[
                      const SizedBox(height: 8),
                      _DetailRow(
                        label: l10n.escrowTruckerReceives,
                        value: l10n.escrowEtbAmount(
                          _amountFormat.format(payment.truckerPayout),
                        ),
                        valueColor: AppTheme.onStatusSuccessContainer,
                      ),
                    ],
                  ],
                ),
              ),
            ),
            const SizedBox(height: 24),

            // Details section
            Text(
              l10n.escrowDeliveryLabel,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),
            _InfoRow(
              icon: Icons.inventory_2,
              label: l10n.escrowDeliveryLabel,
              value:
                  '#${payment.deliveryId.length > 8 ? payment.deliveryId.substring(0, 8) : payment.deliveryId}',
            ),
            _InfoRow(
              icon: Icons.calendar_today,
              label: l10n.escrowCreatedLabel,
              value: clock.dateTime(payment.createdAt),
            ),
            if (payment.expiresAt != null)
              _InfoRow(
                icon: Icons.timer,
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
            const SizedBox(height: 24),

            // Timeline
            Text(
              l10n.escrowTimeline,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 12),
            EscrowTimeline(payment: payment),

            // Dispute info (if disputed)
            if (payment.status == PaymentStatus.disputed) ...[
              const SizedBox(height: 24),
              _DisputeInfoCard(payment: payment, theme: theme),
            ],

            // Raise dispute button (only if escrowed + within 48h window)
            if (_canDispute(payment)) ...[
              const SizedBox(height: 24),
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
                  minimumSize: const Size.fromHeight(48),
                ),
              ),
            ],

            // Stale data indicator (> 1 hour)
            if (staleMinutes > 60) ...[
              const SizedBox(height: 16),
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Icon(
                    Icons.info_outline,
                    size: 14,
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                  ),
                  const SizedBox(width: 4),
                  Text(
                    l10n.escrowMayBeOutdated,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                    ),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
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
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: theme.colorScheme.primaryContainer.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const SizedBox(
                height: 20,
                width: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: Text(
                  l10n.escrowAwaitingPayment,
                  style: theme.textTheme.bodyMedium,
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
  final ThemeData theme;

  const _DisputeInfoCard({required this.payment, required this.theme});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.statusError.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppTheme.statusError.withValues(alpha: 0.2)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.warning, color: AppTheme.onStatusErrorContainer),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.escrowDisputeDetailsTitle,
                style: theme.textTheme.titleMedium?.copyWith(
                  color: AppTheme.onStatusErrorContainer,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ],
          ),
          if (payment.disputeReason != null) ...[
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context)!
                  .escrowDisputeReasonPrefix(payment.disputeReason!),
            ),
          ],
          if (payment.disputeDescription != null) ...[
            const SizedBox(height: 4),
            Text(
              '"${payment.disputeDescription}"',
              style: theme.textTheme.bodyMedium?.copyWith(
                fontStyle: FontStyle.italic,
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

  const _DetailRow({
    required this.label,
    required this.value,
    this.valueColor,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(label, style: Theme.of(context).textTheme.bodyLarge),
        Text(
          value,
          style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                fontWeight: FontWeight.w600,
                color: valueColor,
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
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Icon(
            icon,
            size: 18,
            color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
          ),
          const SizedBox(width: 12),
          SizedBox(
            width: 80,
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
          ),
          Expanded(
            child: ClockText(
              clock ?? ClockLabel.text(value),
              style: theme.textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}
