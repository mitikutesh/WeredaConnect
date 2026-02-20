import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/raise_dispute_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Escrow detail screen showing status, amount, timeline, and actions.
///
/// Loads a single escrow by ID or delivery ID. Supports pull-to-refresh
/// and shows a stale data indicator when cache is older than 1 hour.
class EscrowStatusScreen extends StatefulWidget {
  final String? escrowId;
  final String? deliveryId;

  const EscrowStatusScreen({
    this.escrowId,
    this.deliveryId,
    super.key,
  }) : assert(escrowId != null || deliveryId != null);

  @override
  State<EscrowStatusScreen> createState() => _EscrowStatusScreenState();
}

class _EscrowStatusScreenState extends State<EscrowStatusScreen> {
  static final _dateFormat = DateFormat.yMMMd().add_jm();
  static final _amountFormat = NumberFormat('#,##0.00', 'en_US');

  @override
  void initState() {
    super.initState();
    _loadEscrow();
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
            onPressed: () {
              if (widget.escrowId != null) {
                context.read<EscrowBloc>().add(
                      RefreshEscrow(escrowId: widget.escrowId!),
                    );
              }
            },
          ),
        ],
      ),
      body: BlocBuilder<EscrowBloc, EscrowState>(
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
                      color: Colors.red,
                    ),
                    const SizedBox(height: 16),
                    Text(state.message, textAlign: TextAlign.center),
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
            // Status badge (large)
            EscrowStatusBadge(status: payment.status, large: true),
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
                      valueColor: Colors.green.shade700,
                    ),
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
              value: _dateFormat.format(payment.createdAt),
            ),
            if (payment.expiresAt != null)
              _InfoRow(
                icon: Icons.timer,
                label: l10n.escrowExpiresLabel,
                value: _dateFormat.format(payment.expiresAt!),
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
                  foregroundColor: Colors.orange.shade800,
                  side: BorderSide(color: Colors.orange.shade800),
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

class _DisputeInfoCard extends StatelessWidget {
  final Payment payment;
  final ThemeData theme;

  const _DisputeInfoCard({required this.payment, required this.theme});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: Colors.red.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: Colors.red.withValues(alpha: 0.2)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.warning, color: Colors.red.shade700),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.escrowDisputeDetailsTitle,
                style: theme.textTheme.titleMedium?.copyWith(
                  color: Colors.red.shade700,
                  fontWeight: FontWeight.bold,
                ),
              ),
            ],
          ),
          if (payment.disputeReason != null) ...[
            const SizedBox(height: 8),
            Text('Reason: ${payment.disputeReason}'),
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

  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
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
          Expanded(child: Text(value, style: theme.textTheme.bodyMedium)),
        ],
      ),
    );
  }
}
