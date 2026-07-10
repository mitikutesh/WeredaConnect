import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Filterable list of user's escrow transactions with pending intents.
///
/// Features:
/// - Status filter chips (All / Escrowed / Released / Disputed / Refunded)
/// - Pending intents section with cancel button
/// - Pull-to-refresh
/// - Debug seed button for mock data
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
      bloc.add(
        RefreshPaymentHistory(
          userId: authState.userId,
          roleFilter: _roleFilter,
        ),
      );
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
    final theme = Theme.of(context);
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
      child: Column(
        children: [
          // Status filter chips
          _buildFilterChips(l10n),
          const SizedBox(height: 8),
          // Content
          Expanded(
            child: BlocBuilder<EscrowBloc, EscrowState>(
              builder: (context, state) {
                if (state is EscrowLoading) {
                  return const Center(child: CircularProgressIndicator());
                }

                if (state is EscrowError) {
                  final l10n = AppLocalizations.of(context)!;
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
                            localizeEscrowError(
                              l10n,
                              state.code,
                              state.message,
                            ),
                            style: const TextStyle(
                              fontSize: 16,
                              fontWeight: FontWeight.w600,
                              color: AppTheme.textDark,
                            ),
                            textAlign: TextAlign.center,
                          ),
                          const SizedBox(height: 20),
                          SizedBox(
                            width: 200,
                            child: FilledButton(
                              onPressed: () {
                                final authState =
                                    context.read<AuthBloc>().state;
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

                final hasContent = state.payments.isNotEmpty ||
                    state.pendingIntents.isNotEmpty;

                if (!hasContent) {
                  return _EmptyState(
                    hasFilter: _statusFilter != null,
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
    );

    if (widget.embedded) return content;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.escrowPaymentHistory)),
      body: content,
    );
  }

  Widget _buildFilterChips(AppLocalizations l10n) {
    final isMobile = Breakpoints.isMobile(context);
    final listHPad = isMobile ? 16.0 : 24.0;

    final chips = <({String? status, String label})>[
      (status: null, label: l10n.escrowFilterAll),
      (status: 'escrowed', label: l10n.escrowStatusEscrowed),
      (status: 'released', label: l10n.escrowStatusReleased),
      // A released payout settles to `completed`; without this chip a farmer
      // could never filter to their actually-settled payouts.
      (status: 'completed', label: l10n.escrowStatusCompleted),
      (status: 'disputed', label: l10n.escrowStatusDisputed),
      (status: 'refunded', label: l10n.escrowStatusRefunded),
    ];

    if (isMobile) {
      return SizedBox(
        height: 50,
        child: ListView(
          scrollDirection: Axis.horizontal,
          padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
          children: chips.map((c) {
            return Padding(
              padding: const EdgeInsets.only(right: 8),
              child: _FilterChip(
                label: c.label,
                selected: _statusFilter == c.status,
                onSelected: () => _applyStatusFilter(c.status),
              ),
            );
          }).toList(),
        ),
      );
    }

    return Padding(
      padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
      child: Row(
        children: chips.asMap().entries.map((entry) {
          final c = entry.value;
          final isLast = entry.key == chips.length - 1;
          return Expanded(
            child: Padding(
              padding: EdgeInsets.only(right: isLast ? 0 : 10),
              child: _FilterChip(
                label: c.label,
                selected: _statusFilter == c.status,
                onSelected: () => _applyStatusFilter(c.status),
                expand: true,
              ),
            ),
          );
        }).toList(),
      ),
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
    final theme = Theme.of(context);
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
                // Open the detail under its OWN scoped EscrowBloc (reusing the
                // same repository) so its load lifecycle can't poison this
                // still-mounted list — the shared-bloc trap that otherwise
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
          ],
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  final bool hasFilter;
  final ThemeData theme;

  const _EmptyState({
    required this.hasFilter,
    required this.theme,
  });

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
  final bool expand;

  const _FilterChip({
    required this.label,
    required this.selected,
    required this.onSelected,
    this.expand = false,
  });

  @override
  Widget build(BuildContext context) {
    return FilterChip(
      label: expand
          ? SizedBox(
              width: double.infinity,
              child: Text(label, textAlign: TextAlign.center),
            )
          : Text(label),
      selected: selected,
      labelStyle: TextStyle(
        color: selected ? Colors.white : AppTheme.textDark,
      ),
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
                      '${AppLocalizations.of(context)!.escrowDeliveryShort(payment.deliveryId.length > 8 ? payment.deliveryId.substring(0, 8) : payment.deliveryId)}'
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
