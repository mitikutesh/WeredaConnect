import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/domain/payout_expectation.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/payout_expected_text.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

/// Filterable list of the user's escrow transactions with pending intents,
/// in the Fieldway list language: the escrow summary card (held, released),
/// the Market's pill chips as the status filter, uppercase section labels
/// and one white card per payment.
///
/// Features:
/// - Status filter (All / Escrowed / Released / Completed / Disputed / Refunded)
/// - Pending intents section with cancel button
/// - Pull-to-refresh
class PaymentHistoryScreen extends StatefulWidget {
  /// When true, skips Scaffold/AppBar for use as embedded tab content.
  final bool embedded;

  const PaymentHistoryScreen({this.embedded = false, super.key});

  @override
  State<PaymentHistoryScreen> createState() => _PaymentHistoryScreenState();
}

class _PaymentHistoryScreenState extends State<PaymentHistoryScreen> {
  String? _statusFilter;

  /// The caller's role, forwarded to the server as `?role=`. Without it the
  /// server's history query defaults to the BUYER GSI, so a farmer/trucker saw
  /// zero payouts. Mirrors OrdersTabScreen's `_userRole` plumbing.
  String? _roleFilter;

  /// The last unfiltered page of payments: the summary card sums these, so
  /// a status filter narrows the rows but not the totals.

  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _roleFilter = authState.role.name;
      final bloc = context.read<EscrowBloc>();
      // Render the local cache immediately (fast, offline-friendly)...
      bloc.add(
        LoadPaymentHistory(
          userId: authState.userId,
          roleFilter: _roleFilter,
        ),
      );
      // ...then pull from the server so a fresh device (empty local cache) isn't
      // stuck on an empty history. refreshHistory falls back to the cache offline.
      // On web there is NO local cache: LoadPaymentHistory already hits the server
      // (getHistory delegates to refreshHistory), so a second refresh here would be
      // a duplicate network call; skip it on web.
      if (!kIsWeb) {
        bloc.add(
          RefreshPaymentHistory(
            userId: authState.userId,
            roleFilter: _roleFilter,
          ),
        );
      }
    }
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.userId;
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final content = BlocListener<EscrowBloc, EscrowState>(
      listener: (context, state) {
        if (state is IntentCancelled) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.escrowIntentCancelled)),
          );
          // Reload history after cancellation
          final userId = _getUserId();
          if (userId != null) {
            context.read<EscrowBloc>().add(
                  LoadPaymentHistory(
                    userId: userId,
                    statusFilter: _statusFilter,
                    roleFilter: _roleFilter,
                  ),
                );
          }
        }
      },
      child: BlocBuilder<EscrowBloc, EscrowState>(
        builder: (context, state) {
          return Column(
            children: [
              _buildFilterChips(l10n),
              const SizedBox(height: 4),
              Expanded(child: _buildBody(context, l10n, state)),
            ],
          );
        },
      ),
    );

    if (widget.embedded) return content;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.escrowPaymentHistory)),
      body: Padding(
        padding: const EdgeInsets.only(top: 12),
        child: FwContentWidth(child: content),
      ),
    );
  }

  Widget _buildBody(
    BuildContext context,
    AppLocalizations l10n,
    EscrowState state,
  ) {
    if (state is EscrowLoading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (state is EscrowError) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 32),
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
                localizeEscrowError(l10n, state.code, state.message),
                style: FwText.cardTitle,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 20),
              SizedBox(
                width: 200,
                child: FilledButton(
                  onPressed: () {
                    final authState = context.read<AuthBloc>().state;
                    if (authState is AuthAuthenticated) {
                      context.read<EscrowBloc>().add(
                            LoadPaymentHistory(
                              userId: authState.userId,
                              statusFilter: _statusFilter,
                              roleFilter: _roleFilter,
                            ),
                          );
                    }
                  },
                  child: Text(l10n.retry),
                ),
              ),
            ],
          ),
        ),
      );
    }

    if (state is! PaymentHistoryLoaded) {
      return const Center(child: CircularProgressIndicator());
    }

    final hasContent =
        state.payments.isNotEmpty || state.pendingIntents.isNotEmpty;

    if (!hasContent) {
      return _EmptyState(hasFilter: _statusFilter != null);
    }

    return _HistoryContent(
      state: state,
      statusFilter: _statusFilter,
      roleFilter: _roleFilter,
      onCancelIntent: _showCancelConfirmation,
    );
  }

  Widget _buildFilterChips(AppLocalizations l10n) {
    return FwFilterPillRow<String?>(
      options: [
        FwFilterOption(value: null, label: l10n.escrowFilterAll),
        FwFilterOption(value: 'escrowed', label: l10n.escrowStatusEscrowed),
        FwFilterOption(value: 'released', label: l10n.escrowStatusReleased),
        // A released payout settles to `completed`; without this chip a farmer
        // could never filter to their actually-settled payouts.
        FwFilterOption(value: 'completed', label: l10n.escrowStatusCompleted),
        FwFilterOption(value: 'disputed', label: l10n.escrowStatusDisputed),
        FwFilterOption(value: 'refunded', label: l10n.escrowStatusRefunded),
      ],
      selected: _statusFilter,
      onSelected: _applyStatusFilter,
    );
  }

  void _applyStatusFilter(String? status) {
    setState(() => _statusFilter = status);
    _dispatchFilter();
  }

  void _dispatchFilter() {
    final userId = _getUserId();
    if (userId == null) return;
    context.read<EscrowBloc>().add(
          FilterPaymentHistory(
            userId: userId,
            statusFilter: _statusFilter,
            roleFilter: _roleFilter,
          ),
        );
  }

  void _showCancelConfirmation(String idempotencyKey) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        final dl10n = AppLocalizations.of(context)!;
        return AlertDialog(
          title: Text(dl10n.escrowCancelIntent),
          content: Text(dl10n.escrowCancelIntentDialog),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(dl10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<EscrowBloc>().add(
                      CancelPaymentIntent(idempotencyKey: idempotencyKey),
                    );
              },
              style:
                  TextButton.styleFrom(foregroundColor: AppTheme.statusError),
              child: Text(dl10n.escrowCancelIntent),
            ),
          ],
        );
      },
    );
  }
}

class _HistoryContent extends StatelessWidget {
  final PaymentHistoryLoaded state;
  final String? statusFilter;
  final String? roleFilter;
  final void Function(String idempotencyKey) onCancelIntent;

  const _HistoryContent({
    required this.state,
    required this.onCancelIntent,
    this.statusFilter,
    this.roleFilter,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return RefreshIndicator(
      onRefresh: () async {
        final authState = context.read<AuthBloc>().state;
        if (authState is AuthAuthenticated) {
          context.read<EscrowBloc>().add(
                RefreshPaymentHistory(
                  userId: authState.userId,
                  statusFilter: statusFilter,
                  roleFilter: roleFilter,
                ),
              );
        }
      },
      child: ListView(
        padding: const EdgeInsets.only(bottom: Breakpoints.fabClearance),
        children: [
          // Pending intents section
          if (state.pendingIntents.isNotEmpty) ...[
            FwSectionLabel(
              l10n.escrowPendingSection(state.pendingIntents.length),
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 5),
            ),
            for (final intent in state.pendingIntents)
              PendingIntentCard(
                intent: intent,
                onCancel: () => onCancelIntent(intent.idempotencyKey),
              ),
            const SizedBox(height: 8),
          ],
          // History section
          if (state.payments.isNotEmpty) ...[
            FwSectionLabel(
              l10n.escrowHistorySection,
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 5),
            ),
            for (final payment in state.payments)
              _PaymentCard(
                payment: payment,
                viewerRole: roleFilter,
                // Open the detail under its OWN scoped EscrowBloc (reusing the
                // same repository) so its load lifecycle can't poison this
                // still-mounted list: the shared-bloc trap that otherwise
                // left the history stuck on a spinner/error on return. Reload
                // (preserving filters) when the detail route pops.
                onTap: () async {
                  final escrowBloc = context.read<EscrowBloc>();
                  await Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => BlocProvider<EscrowBloc>(
                        create: (_) =>
                            EscrowBloc(repository: escrowBloc.repository),
                        child: EscrowStatusScreen(
                          escrowId: payment.paymentId,
                        ),
                      ),
                    ),
                  );
                  if (!context.mounted) return;
                  final authState = context.read<AuthBloc>().state;
                  if (authState is AuthAuthenticated) {
                    escrowBloc.add(
                      FilterPaymentHistory(
                        userId: authState.userId,
                        statusFilter: statusFilter,
                        roleFilter: roleFilter,
                      ),
                    );
                  }
                },
              ),
            Padding(
              padding: const EdgeInsets.fromLTRB(24, 8, 24, 0),
              child: Text(l10n.listsPaymentsNote, style: FwText.body),
            ),
          ],
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  final bool hasFilter;

  const _EmptyState({required this.hasFilter});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            hasFilter
                ? Icons.filter_alt_off
                : Icons.account_balance_wallet_outlined,
            size: 56,
            color: AppTheme.mutedSteel,
          ),
          const SizedBox(height: 16),
          Text(
            hasFilter ? l10n.escrowNoMatchingPayments : l10n.escrowNoPayments,
            style: FwText.cardTitle,
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }
}

/// One escrow row: the amount this party takes home, the delivery or order
/// it backs with its day, the payout day for a payee, and the status pill.
class _PaymentCard extends StatelessWidget {
  final Payment payment;

  /// `UserRole.name` of the viewer: each party sees only its own leg of the
  /// held amount (buyer: total, farmer: crop leg, trucker: transport leg).
  final String? viewerRole;
  final VoidCallback? onTap;

  const _PaymentCard({required this.payment, this.viewerRole, this.onTap});

  static final _dateFormat = DateFormat.yMMMd();

  static String _short(String id) => id.length > 8 ? id.substring(0, 8) : id;

  /// The delivery when one is linked, else the backing order (an order-funded
  /// escrow has no delivery until the trucker creates one; this used to render
  /// a blank "Del. #"). Null when neither is known.
  String? _reference(AppLocalizations l10n) {
    if (payment.deliveryId.isNotEmpty) {
      return l10n.escrowDeliveryShort(_short(payment.deliveryId));
    }
    final orderId = payment.orderId;
    if (orderId != null && orderId.isNotEmpty) {
      return l10n.escrowOrderShort(_short(orderId));
    }
    return null;
  }

  String _subtitle(AppLocalizations l10n) {
    final reference = _reference(l10n);
    final date = _dateFormat.format(dayInEthiopia(payment.createdAt));
    return reference == null ? date : '$reference · $date';
  }

  /// FRD-13: the farmer's and the trucker's own payout day; never on a
  /// buyer's row.
  DateTime? get _payoutAt => viewerRole == 'farmer' || viewerRole == 'trucker'
      ? expectedPayoutAt(payment)
      : null;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final payoutAt = _payoutAt;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 5),
      child: FwCard(
        onTap: onTap,
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: AmountDisplay(
                    amount: payment.payoutFor(viewerRole ?? 'buyer'),
                  ),
                ),
                const SizedBox(width: 10),
                EscrowStatusBadge(
                  status: payment.status,
                  refundPending: payment.isRefundPending,
                ),
              ],
            ),
            const SizedBox(height: 3),
            Text(_subtitle(l10n), style: FwText.body),
            if (payoutAt != null) ...[
              const SizedBox(height: 4),
              PayoutExpectedText(payoutAt: payoutAt),
            ],
          ],
        ),
      ),
    );
  }
}
