import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/status_badge.dart';

/// Unified orders screen combining deliveries + payments.
///
/// Shows "My Orders" with tab filters: Active | Done | All.
/// Each order card shows status, progress, and amount.
class OrdersScreen extends StatefulWidget {
  const OrdersScreen({super.key});

  @override
  State<OrdersScreen> createState() => _OrdersScreenState();
}

class _OrdersScreenState extends State<OrdersScreen>
    with SingleTickerProviderStateMixin {
  late TabController _tabController;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 3, vsync: this);
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.watch<AuthBloc>().state;
    final userId = authState is AuthAuthenticated ? authState.userId : '';

    return Column(
      children: [
        // ── Header ──────────────────────────────────────────────────
        GreenHeader(
          height: 160,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const SizedBox(height: 8),
              Text(
                l10n.myOrders,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                l10n.trackTradesEscrow,
                style: TextStyle(
                  fontSize: 14,
                  color: Colors.white.withAlpha(180),
                ),
              ),
              const Spacer(),
              // Tab bar
              Container(
                decoration: BoxDecoration(
                  color: Colors.white.withAlpha(30),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: TabBar(
                  controller: _tabController,
                  indicatorSize: TabBarIndicatorSize.tab,
                  indicator: BoxDecoration(
                    color: Colors.white.withAlpha(50),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  labelColor: Colors.white,
                  unselectedLabelColor: Colors.white70,
                  labelStyle: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                  ),
                  dividerHeight: 0,
                  tabs: [
                    Tab(text: l10n.active),
                    Tab(text: l10n.done),
                    Tab(text: l10n.all),
                  ],
                ),
              ),
              const SizedBox(height: 12),
            ],
          ),
        ),

        // ── Order list ──────────────────────────────────────────────
        Expanded(
          child: TabBarView(
            controller: _tabController,
            children: [
              _OrderList(
                userId: userId,
                filter: _OrderFilter.active,
                l10n: l10n,
              ),
              _OrderList(
                userId: userId,
                filter: _OrderFilter.done,
                l10n: l10n,
              ),
              _OrderList(
                userId: userId,
                filter: _OrderFilter.all,
                l10n: l10n,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

enum _OrderFilter { active, done, all }

class _OrderList extends StatefulWidget {
  final String userId;
  final _OrderFilter filter;
  final AppLocalizations l10n;

  const _OrderList({
    required this.userId,
    required this.filter,
    required this.l10n,
  });

  @override
  State<_OrderList> createState() => _OrderListState();
}

class _OrderListState extends State<_OrderList> {
  late Future<List<_OrderItem>> _ordersFuture;

  @override
  void initState() {
    super.initState();
    _ordersFuture = _loadOrders(widget.userId, widget.filter);
  }

  @override
  void didUpdateWidget(covariant _OrderList oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId ||
        oldWidget.filter != widget.filter) {
      _ordersFuture = _loadOrders(widget.userId, widget.filter);
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<_OrderItem>>(
      future: _ordersFuture,
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const Center(child: CircularProgressIndicator());
        }

        final orders = snapshot.data ?? [];

        if (orders.isEmpty) {
          return Center(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(
                  Icons.receipt_long_outlined,
                  size: 64,
                  color: AppTheme.textMuted,
                ),
                const SizedBox(height: 16),
                Text(
                  widget.l10n.noOrders,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.textMuted,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  widget.l10n.ordersWillAppear,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.textMuted,
                  ),
                ),
              ],
            ),
          );
        }

        return ListView.separated(
          padding: const EdgeInsets.all(20),
          itemCount: orders.length,
          separatorBuilder: (_, __) => const SizedBox(height: 12),
          itemBuilder: (context, index) =>
              _OrderCard(order: orders[index], l10n: widget.l10n),
        );
      },
    );
  }

  Future<List<_OrderItem>> _loadOrders(
    String userId,
    _OrderFilter filter,
  ) async {
    try {
      final isar = IsarService.instance.db;
      final items = <_OrderItem>[];

      // Load deliveries scoped to user
      final deliveries = userId.isNotEmpty
          ? await isar.deliverys
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .findAll()
          : await isar.deliverys.where().findAll();
      for (final d in deliveries) {
        final isDone = d.status == DeliveryStatus.delivered ||
            d.status == DeliveryStatus.cancelled;
        if (filter == _OrderFilter.active && isDone) continue;
        if (filter == _OrderFilter.done && !isDone) continue;

        items.add(
          _OrderItem(
            id: d.deliveryId,
            isarId: d.id,
            status: d.status.name,
            timestamp: d.createdAt,
            buyerOrSeller: d.farmerId,
            isDelivery: true,
          ),
        );
      }

      // Load payments scoped to user
      final payments = userId.isNotEmpty
          ? await isar.payments
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .findAll()
          : await isar.payments.where().findAll();
      for (final p in payments) {
        final isDone = p.status == PaymentStatus.released ||
            p.status == PaymentStatus.completed;
        if (filter == _OrderFilter.active && isDone) continue;
        if (filter == _OrderFilter.done && !isDone) continue;

        items.add(
          _OrderItem(
            id: p.paymentId,
            isarId: p.id,
            status: p.status.name,
            amount: p.amount,
            timestamp: p.createdAt,
            buyerOrSeller: p.farmerId,
            isDelivery: false,
          ),
        );
      }

      // Sort by recency
      items.sort((a, b) => b.timestamp.compareTo(a.timestamp));
      return items;
    } on Object {
      return [];
    }
  }
}

class _OrderItem {
  final String id;
  final int isarId;
  final String status;
  final double? amount;
  final DateTime timestamp;
  final String buyerOrSeller;
  final bool isDelivery;

  _OrderItem({
    required this.id,
    required this.isarId,
    required this.status,
    required this.timestamp,
    required this.buyerOrSeller,
    required this.isDelivery,
    this.amount,
  });
}

class _OrderCard extends StatelessWidget {
  final _OrderItem order;
  final AppLocalizations l10n;

  const _OrderCard({required this.order, required this.l10n});

  @override
  Widget build(BuildContext context) {
    final (badgeType, statusLabel) = _statusInfo(order.status, l10n);
    final title = order.isDelivery
        ? l10n.deliveryTitle(order.isarId)
        : l10n.paymentTitle(order.isarId);

    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppTheme.borderLight),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Title + status badge
          Row(
            children: [
              Expanded(
                child: Text(
                  title,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.textDark,
                  ),
                ),
              ),
              StatusBadge(label: statusLabel, type: badgeType),
            ],
          ),
          const SizedBox(height: 10),
          // Progress bar
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(
              value: _progressForStatus(order.status),
              backgroundColor: AppTheme.borderLight,
              color: _colorForStatus(order.status),
              minHeight: 6,
            ),
          ),
          const SizedBox(height: 10),
          // Status steps
          _StatusSteps(status: order.status, l10n: l10n),
          const SizedBox(height: 10),
          // Bottom row: amount + chevron
          Row(
            children: [
              if (order.amount != null) ...[
                Text(
                  '${order.amount!.toStringAsFixed(0)} ${l10n.etbCurrency}',
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.primaryGreen,
                  ),
                ),
              ],
              const Spacer(),
              const Icon(
                Icons.chevron_right,
                color: AppTheme.textMuted,
                size: 20,
              ),
            ],
          ),
        ],
      ),
    );
  }

  static (StatusBadgeType, String) _statusInfo(
    String status,
    AppLocalizations l10n,
  ) {
    return switch (status) {
      'in_transit' || 'in-transit' => (StatusBadgeType.transit, l10n.inTransit),
      'delivered' || 'completed' || 'released' => (
          StatusBadgeType.success,
          l10n.done
        ),
      'pending' || 'created' => (
          StatusBadgeType.pending,
          l10n.deliveryStatusPending
        ),
      'funded' || 'escrowed' => (StatusBadgeType.info, l10n.statusFunded),
      'disputed' => (StatusBadgeType.error, l10n.statusDisputed),
      _ => (StatusBadgeType.pending, status),
    };
  }

  static double _progressForStatus(String status) {
    return switch (status) {
      'pending' || 'created' => 0.15,
      'funded' || 'escrowed' => 0.35,
      'assigned' => 0.5,
      'in_transit' || 'in-transit' => 0.7,
      'delivered' => 0.85,
      'completed' || 'released' => 1.0,
      _ => 0.1,
    };
  }

  static Color _colorForStatus(String status) {
    return switch (status) {
      'in_transit' || 'in-transit' => AppTheme.statusTransit,
      'delivered' || 'completed' || 'released' => AppTheme.statusSuccess,
      'disputed' => AppTheme.statusError,
      _ => AppTheme.accentGold,
    };
  }
}

class _StatusSteps extends StatelessWidget {
  final String status;
  final AppLocalizations l10n;

  const _StatusSteps({required this.status, required this.l10n});

  @override
  Widget build(BuildContext context) {
    final steps = [
      l10n.statusPlaced,
      l10n.statusFunded,
      l10n.statusAssigned,
      l10n.statusTransit,
      l10n.done,
    ];
    final currentIndex = _indexForStatus(status);

    return Row(
      children: steps.asMap().entries.map((entry) {
        final isCompleted = entry.key <= currentIndex;
        return Expanded(
          child: Container(
            padding: const EdgeInsets.symmetric(vertical: 3),
            margin:
                EdgeInsets.only(right: entry.key < steps.length - 1 ? 4 : 0),
            decoration: BoxDecoration(
              color: isCompleted
                  ? AppTheme.primaryGreen.withAlpha(25)
                  : AppTheme.borderLight.withAlpha(128),
              borderRadius: BorderRadius.circular(4),
            ),
            child: Text(
              entry.value,
              style: TextStyle(
                fontSize: 10,
                fontWeight: FontWeight.w500,
                color: isCompleted ? AppTheme.primaryGreen : AppTheme.textMuted,
              ),
              textAlign: TextAlign.center,
            ),
          ),
        );
      }).toList(),
    );
  }

  static int _indexForStatus(String status) {
    return switch (status) {
      'pending' || 'created' => 0,
      'funded' || 'escrowed' => 1,
      'assigned' => 2,
      'in_transit' || 'in-transit' => 3,
      'delivered' || 'completed' || 'released' => 4,
      _ => 0,
    };
  }
}
