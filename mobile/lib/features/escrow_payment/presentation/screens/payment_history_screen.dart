import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Filterable list of user's escrow transactions with pending intents.
///
/// Features:
/// - Role filter chips (All / As Buyer / As Farmer)
/// - Status filter chips (All / Escrowed / Released / Disputed / Refunded)
/// - Pending intents section with cancel button
/// - Pull-to-refresh
/// - Empty states for no payments / no matching payments
class PaymentHistoryScreen extends StatefulWidget {
  const PaymentHistoryScreen({super.key});

  @override
  State<PaymentHistoryScreen> createState() => _PaymentHistoryScreenState();
}

class _PaymentHistoryScreenState extends State<PaymentHistoryScreen> {
  String? _roleFilter;
  String? _statusFilter;

  @override
  void initState() {
    super.initState();
    context.read<EscrowBloc>().add(const LoadPaymentHistory());
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.escrowPaymentHistory)),
      body: BlocListener<EscrowBloc, EscrowState>(
        listener: (context, state) {
          if (state is IntentCancelled) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.escrowIntentCancelled)),
            );
            // Reload history after cancellation
            context.read<EscrowBloc>().add(
                  LoadPaymentHistory(
                    statusFilter: _statusFilter,
                    roleFilter: _roleFilter,
                  ),
                );
          }
        },
        child: Column(
          children: [
            // Role filter chips
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Row(
                children: [
                  _FilterChip(
                    label: l10n.escrowFilterAll,
                    selected: _roleFilter == null,
                    onSelected: () => _applyRoleFilter(null),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowFilterBuyer,
                    selected: _roleFilter == 'buyer',
                    onSelected: () => _applyRoleFilter('buyer'),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowFilterFarmer,
                    selected: _roleFilter == 'farmer',
                    onSelected: () => _applyRoleFilter('farmer'),
                  ),
                ],
              ),
            ),
            // Status filter chips
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: Row(
                children: [
                  _FilterChip(
                    label: l10n.escrowFilterAll,
                    selected: _statusFilter == null,
                    onSelected: () => _applyStatusFilter(null),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowStatusEscrowed,
                    selected: _statusFilter == 'escrowed',
                    onSelected: () => _applyStatusFilter('escrowed'),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowStatusReleased,
                    selected: _statusFilter == 'released',
                    onSelected: () => _applyStatusFilter('released'),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowStatusDisputed,
                    selected: _statusFilter == 'disputed',
                    onSelected: () => _applyStatusFilter('disputed'),
                  ),
                  const SizedBox(width: 8),
                  _FilterChip(
                    label: l10n.escrowStatusRefunded,
                    selected: _statusFilter == 'refunded',
                    onSelected: () => _applyStatusFilter('refunded'),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 8),
            // Content
            Expanded(
              child: BlocBuilder<EscrowBloc, EscrowState>(
                builder: (context, state) {
                  if (state is EscrowLoading) {
                    return const Center(child: CircularProgressIndicator());
                  }

                  if (state is EscrowError) {
                    return Center(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const Icon(
                            Icons.error_outline,
                            size: 48,
                            color: Colors.red,
                          ),
                          const SizedBox(height: 12),
                          Text(state.message),
                        ],
                      ),
                    );
                  }

                  if (state is! PaymentHistoryLoaded) {
                    return const Center(child: CircularProgressIndicator());
                  }

                  final hasContent = state.payments.isNotEmpty ||
                      state.pendingIntents.isNotEmpty;

                  if (!hasContent) {
                    return _EmptyState(
                      hasFilter: _statusFilter != null || _roleFilter != null,
                      theme: theme,
                    );
                  }

                  return _HistoryContent(
                    state: state,
                    statusFilter: _statusFilter,
                    roleFilter: _roleFilter,
                    onCancelIntent: _showCancelConfirmation,
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _applyRoleFilter(String? role) {
    setState(() => _roleFilter = role);
    _dispatchFilter();
  }

  void _applyStatusFilter(String? status) {
    setState(() => _statusFilter = status);
    _dispatchFilter();
  }

  void _dispatchFilter() {
    context.read<EscrowBloc>().add(
          FilterPaymentHistory(
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
              style: TextButton.styleFrom(foregroundColor: Colors.red),
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
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;

    return RefreshIndicator(
      onRefresh: () async {
        context.read<EscrowBloc>().add(
              RefreshPaymentHistory(
                statusFilter: statusFilter,
                roleFilter: roleFilter,
              ),
            );
      },
      child: ListView(
        children: [
          // Pending intents section
          if (state.pendingIntents.isNotEmpty) ...[
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              child: Text(
                l10n.escrowPendingSection(state.pendingIntents.length),
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
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
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              child: Text(
                l10n.escrowHistorySection,
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
            ),
            for (final payment in state.payments)
              _PaymentCard(
                payment: payment,
                onTap: () {
                  Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => EscrowStatusScreen(
                        escrowId: payment.paymentId,
                      ),
                    ),
                  );
                },
              ),
          ],
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  final bool hasFilter;
  final ThemeData theme;

  const _EmptyState({required this.hasFilter, required this.theme});

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
            size: 64,
            color: theme.colorScheme.outline,
          ),
          const SizedBox(height: 16),
          Text(
            hasFilter ? l10n.escrowNoMatchingPayments : l10n.escrowNoPayments,
            style: theme.textTheme.bodyLarge?.copyWith(
              color: theme.colorScheme.outline,
            ),
          ),
        ],
      ),
    );
  }
}

class _FilterChip extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onSelected;

  const _FilterChip({
    required this.label,
    required this.selected,
    required this.onSelected,
  });

  @override
  Widget build(BuildContext context) {
    return FilterChip(
      label: Text(label),
      selected: selected,
      onSelected: (_) => onSelected(),
      showCheckmark: false,
    );
  }
}

class _PaymentCard extends StatelessWidget {
  final Payment payment;
  final VoidCallback? onTap;

  const _PaymentCard({required this.payment, this.onTap});

  static final _dateFormat = DateFormat.yMMMd();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    AmountDisplay(amount: payment.amount),
                    const SizedBox(height: 4),
                    Text(
                      'Del. #${payment.deliveryId.length > 8 ? payment.deliveryId.substring(0, 8) : payment.deliveryId}'
                      ' · ${_dateFormat.format(payment.createdAt)}',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
              ),
              EscrowStatusBadge(status: payment.status),
            ],
          ),
        ),
      ),
    );
  }
}
