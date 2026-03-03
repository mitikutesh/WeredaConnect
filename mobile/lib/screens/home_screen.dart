import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/intent_card.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/stat_tile.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// Home screen — compact dashboard matching mockup.
///
/// Layout: green header → banner → quick actions → overview → recent.
/// Designed to fit in one screen view.
///
/// Uses a [_refreshKey] counter to force overview/recent widgets to reload
/// when returning from create screens (FAB or quick-action "Sell" button).
class HomeScreen extends StatefulWidget {
  final ValueChanged<int>? onNavigateToTab;
  final bool isOnline;

  const HomeScreen({
    super.key,
    this.onNavigateToTab,
    this.isOnline = true,
  });

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  /// Incrementing key that forces overview/recent widgets to recreate
  /// their state (and reload data from Isar) when it changes.
  int _refreshKey = 0;

  void _refreshData() {
    if (mounted) setState(() => _refreshKey++);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.watch<AuthBloc>().state;
    final role =
        authState is AuthAuthenticated ? authState.role : UserRole.farmer;
    final userName = authState is AuthAuthenticated ? authState.userName : '';
    final userId = authState is AuthAuthenticated ? authState.userId : '';
    final woreda =
        authState is AuthAuthenticated ? (authState.woreda ?? '') : '';

    return SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // ── Green header with banner ──────────────────────────────
          _HomeHeaderWithBanner(
            key: ValueKey('header_$_refreshKey'),
            userName: userName,
            userId: userId,
            woreda: woreda,
            isOnline: widget.isOnline,
            l10n: l10n,
            onNavigateToTab: widget.onNavigateToTab,
          ),

          // ── Quick actions (no section title, like mockup) ────────
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 12, 20, 0),
            child: _QuickActionsGrid(
              role: role,
              l10n: l10n,
              onNavigateToTab: widget.onNavigateToTab,
              onRefresh: _refreshData,
            ),
          ),

          // ── Active Buying Intents (buyer only) ────────────────────
          if (role == UserRole.buyer)
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
              child: _ActiveIntentsSection(
                key: ValueKey('intents_$_refreshKey'),
                userId: userId,
                l10n: l10n,
                onRefresh: _refreshData,
              ),
            ),

          // ── Overview ─────────────────────────────────────────────
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: Text(
              l10n.overview,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: AppTheme.textDark,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 0),
            child: _OverviewStats(
              key: ValueKey('overview_$_refreshKey'),
              role: role,
              userId: userId,
              l10n: l10n,
            ),
          ),

          // ── Recent ───────────────────────────────────────────────
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: Text(
              l10n.recentActivity,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: AppTheme.textDark,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 16),
            child: _RecentActivityList(
              key: ValueKey('recent_$_refreshKey'),
              userId: userId,
              l10n: l10n,
            ),
          ),
        ],
      ),
    );
  }
}

/// Combined green header + gold banner, matching mockup.
///
/// The banner overlaps the bottom edge of the green header —
/// upper half inside green, lower half below, creating a layered look.
class _HomeHeaderWithBanner extends StatelessWidget {
  final String userName;
  final String userId;
  final String woreda;
  final bool isOnline;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;

  const _HomeHeaderWithBanner({
    required this.userName,
    required this.userId,
    required this.woreda,
    required this.isOnline,
    required this.l10n,
    this.onNavigateToTab,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    const greenHeight = 155.0;
    // Space below green for the banner overflow (bottom half of banner)
    const bannerOverflow = 28.0;

    return SizedBox(
      height: greenHeight + bannerOverflow + MediaQuery.of(context).padding.top,
      child: Stack(
        children: [
          // Green background
          Container(
            height: greenHeight + MediaQuery.of(context).padding.top,
            decoration: const BoxDecoration(
              color: AppTheme.primaryGreen,
              borderRadius: BorderRadius.only(
                bottomLeft: Radius.circular(24),
                bottomRight: Radius.circular(24),
              ),
            ),
          ),
          // Header content (location, name)
          SafeArea(
            bottom: false,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  const SizedBox(height: 8),
                  // Location + notification row
                  Row(
                    children: [
                      const Icon(
                        Icons.location_on,
                        size: 16,
                        color: Colors.white70,
                      ),
                      const SizedBox(width: 4),
                      Text(
                        woreda.isNotEmpty ? woreda : l10n.wereda,
                        style: const TextStyle(
                          fontSize: 13,
                          color: Colors.white70,
                        ),
                      ),
                      const Spacer(),
                      // Cloud sync status icon (tappable)
                      _SyncStatusIcon(isOnline: isOnline),
                      const SizedBox(width: 14),
                      // Notification bell (functional)
                      _NotificationIcon(
                        userId: userId,
                        l10n: l10n,
                        isOnline: isOnline,
                        onNavigateToTab: onNavigateToTab,
                      ),
                      const SizedBox(width: 10),
                      // Profile icon
                      GestureDetector(
                        onTap: () => Navigator.push(
                          context,
                          MaterialPageRoute<void>(
                            builder: (_) => const ProfileScreen(),
                          ),
                        ),
                        child: const CircleAvatar(
                          radius: 16,
                          backgroundColor: Colors.white24,
                          child: Icon(
                            Icons.person,
                            size: 18,
                            color: Colors.white,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 10),
                  // User name
                  Text(
                    userName,
                    style: const TextStyle(
                      fontSize: 22,
                      fontWeight: FontWeight.w700,
                      color: Colors.white,
                    ),
                  ),
                ],
              ),
            ),
          ),
          // Banner — overlaps bottom edge of green header
          Positioned(
            left: 20,
            right: 20,
            bottom: 0,
            child: Container(
              padding: const EdgeInsets.symmetric(
                horizontal: 16,
                vertical: 12,
              ),
              decoration: BoxDecoration(
                gradient: const LinearGradient(
                  colors: [AppTheme.accentGold, AppTheme.accentGoldLight],
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                ),
                borderRadius: BorderRadius.circular(12),
                boxShadow: [
                  BoxShadow(
                    color: Colors.black.withAlpha(25),
                    blurRadius: 8,
                    offset: const Offset(0, 2),
                  ),
                ],
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    l10n.bannerHeadline,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: Colors.white,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    l10n.bannerSubtitle,
                    style: TextStyle(
                      fontSize: 13,
                      color: Colors.white.withAlpha(220),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Quick action grid — role-specific, compact tiles.
class _QuickActionsGrid extends StatelessWidget {
  final UserRole role;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _QuickActionsGrid({
    required this.role,
    required this.l10n,
    required this.onNavigateToTab,
    this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final actions = _actionsForRole(role, context);

    return GridView.count(
      crossAxisCount: 3,
      shrinkWrap: true,
      physics: const NeverScrollableScrollPhysics(),
      mainAxisSpacing: 10,
      crossAxisSpacing: 10,
      childAspectRatio: 0.9,
      children: actions,
    );
  }

  List<Widget> _actionsForRole(UserRole role, BuildContext context) {
    switch (role) {
      case UserRole.farmer:
        return [
          _QuickActionTile(
            icon: Icons.list_alt,
            label: l10n.myListings,
            color: AppTheme.primaryGreen,
            onTap: () async {
              await Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => const BrowseListingsScreen(ownerMode: true),
                ),
              );
              onRefresh?.call();
            },
          ),
          _QuickActionTile(
            icon: Icons.local_shipping_outlined,
            label: l10n.trackDelivery,
            color: AppTheme.statusTransit,
            onTap: () => onNavigateToTab?.call(2),
          ),
          _QuickActionTile(
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.checkBalance,
            color: AppTheme.accentGold,
            onTap: () => onNavigateToTab?.call(3),
          ),
        ];
      case UserRole.trucker:
        return [
          _QuickActionTile(
            icon: Icons.route,
            label: l10n.myTrips,
            color: AppTheme.primaryGreen,
            onTap: () async {
              await Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => const BrowseTripsScreen(),
                ),
              );
              onRefresh?.call();
            },
          ),
          _QuickActionTile(
            icon: Icons.local_shipping_outlined,
            label: l10n.trackDelivery,
            color: AppTheme.statusTransit,
            onTap: () => onNavigateToTab?.call(2),
          ),
          _QuickActionTile(
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.checkBalance,
            color: AppTheme.accentGold,
            onTap: () => onNavigateToTab?.call(3),
          ),
        ];
      case UserRole.buyer:
        return [
          _QuickActionTile(
            icon: Icons.search,
            label: l10n.findCrops,
            color: AppTheme.primaryGreen,
            onTap: () async {
              await Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => const CreateIntentScreen(),
                ),
              );
              onRefresh?.call();
            },
          ),
          _QuickActionTile(
            icon: Icons.local_shipping_outlined,
            label: l10n.trackDelivery,
            color: AppTheme.statusTransit,
            onTap: () => onNavigateToTab?.call(2),
          ),
          _QuickActionTile(
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.checkBalance,
            color: AppTheme.accentGold,
            onTap: () => onNavigateToTab?.call(3),
          ),
        ];
    }
  }
}

/// Single quick action tile — icon + label, compact.
class _QuickActionTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color color;
  final VoidCallback onTap;

  const _QuickActionTile({
    required this.icon,
    required this.label,
    required this.color,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          decoration: BoxDecoration(
            color: AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: AppTheme.borderLight),
          ),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Container(
                width: 44,
                height: 44,
                decoration: BoxDecoration(
                  color: color.withAlpha(20),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Icon(icon, size: 24, color: color),
              ),
              const SizedBox(height: 6),
              Text(
                label,
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.textDark,
                ),
                textAlign: TextAlign.center,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Active buying intents section for the buyer home screen.
///
/// Shows up to 3 active intents with match count badges.
/// Tapping an intent navigates to match results.
class _ActiveIntentsSection extends StatefulWidget {
  final String userId;
  final AppLocalizations l10n;
  final VoidCallback? onRefresh;

  const _ActiveIntentsSection({
    required this.userId,
    required this.l10n,
    this.onRefresh,
    super.key,
  });

  @override
  State<_ActiveIntentsSection> createState() => _ActiveIntentsSectionState();
}

class _ActiveIntentsSectionState extends State<_ActiveIntentsSection> {
  @override
  void initState() {
    super.initState();
    context
        .read<BuyerIntentBloc>()
        .add(LoadActiveIntents(buyerId: widget.userId));
  }

  @override
  Widget build(BuildContext context) {
    final l10n = widget.l10n;

    return BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
      buildWhen: (prev, curr) => curr is ActiveIntentsLoaded,
      builder: (context, state) {
        if (state is! ActiveIntentsLoaded || state.intents.isEmpty) {
          return const SizedBox.shrink();
        }

        final intents = state.intents.take(3).toList();

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.activeBuyingIntents,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: AppTheme.textDark,
              ),
            ),
            const SizedBox(height: 8),
            ...intents.map(
              (intent) => Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: IntentCard(
                  intent: intent,
                  onTap: () async {
                    await Navigator.push(
                      context,
                      MaterialPageRoute<void>(
                        builder: (_) => BlocProvider.value(
                          value: context.read<BuyerIntentBloc>(),
                          child: MatchResultsScreen(intent: intent),
                        ),
                      ),
                    );
                    widget.onRefresh?.call();
                  },
                ),
              ),
            ),
            // "New Buying Intent" button
            Center(
              child: TextButton.icon(
                onPressed: () async {
                  await Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => const CreateIntentScreen(),
                    ),
                  );
                  widget.onRefresh?.call();
                },
                icon: const Icon(Icons.add, size: 16),
                label: Text(l10n.newBuyingIntent),
              ),
            ),
          ],
        );
      },
    );
  }
}

/// 2x2 overview stats grid.
class _OverviewStats extends StatefulWidget {
  final UserRole role;
  final String userId;
  final AppLocalizations l10n;

  const _OverviewStats({
    required this.role,
    required this.userId,
    required this.l10n,
    super.key,
  });

  @override
  State<_OverviewStats> createState() => _OverviewStatsState();
}

class _OverviewStatsState extends State<_OverviewStats> {
  late Future<Map<String, int>> _statsFuture;

  @override
  void initState() {
    super.initState();
    _statsFuture = _loadStats(widget.userId, widget.role);
  }

  @override
  void didUpdateWidget(covariant _OverviewStats oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId || oldWidget.role != widget.role) {
      _statsFuture = _loadStats(widget.userId, widget.role);
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<Map<String, int>>(
      future: _statsFuture,
      builder: (context, snapshot) {
        final stats = snapshot.data ?? {};
        final tiles = _tilesForRole(widget.role, stats);

        return GridView.count(
          crossAxisCount: 2,
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          mainAxisSpacing: 8,
          crossAxisSpacing: 8,
          childAspectRatio: 2.4,
          children: tiles,
        );
      },
    );
  }

  Future<Map<String, int>> _loadStats(String userId, UserRole role) async {
    try {
      final isar = IsarService.instance.db;
      final listings = userId.isNotEmpty
          ? await isar.cropListings.where().farmerIdEqualTo(userId).count()
          : await isar.cropListings.count();
      final deliveries = userId.isNotEmpty
          ? await isar.deliverys
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .count()
          : await isar.deliverys.count();
      final payments = userId.isNotEmpty
          ? await isar.payments
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .count()
          : await isar.payments.count();

      // Count orders relevant to the user's role
      int pendingOrders = 0;
      int activeOrders = 0;
      if (role == UserRole.farmer && userId.isNotEmpty) {
        pendingOrders = await isar.orders
            .filter()
            .farmerIdEqualTo(userId)
            .statusEqualTo(OrderStatus.funded)
            .count();
      } else if (role == UserRole.buyer && userId.isNotEmpty) {
        activeOrders = await isar.orders
            .filter()
            .buyerIdEqualTo(userId)
            .group(
              (q) => q
                  .statusEqualTo(OrderStatus.created)
                  .or()
                  .statusEqualTo(OrderStatus.pendingPayment)
                  .or()
                  .statusEqualTo(OrderStatus.paymentSubmitted)
                  .or()
                  .statusEqualTo(OrderStatus.funded)
                  .or()
                  .statusEqualTo(OrderStatus.accepted),
            )
            .count();
      }

      return {
        'listings': listings,
        'deliveries': deliveries,
        'payments': payments,
        'pendingOrders': pendingOrders,
        'activeOrders': activeOrders,
      };
    } on Object {
      return {};
    }
  }

  List<Widget> _tilesForRole(UserRole role, Map<String, int> stats) {
    final l10n = widget.l10n;
    switch (role) {
      case UserRole.farmer:
        return [
          StatTile(
            value: '${stats['listings'] ?? 0}',
            label: l10n.listings,
            icon: Icons.trending_up,
          ),
          StatTile(
            value: '${stats['pendingOrders'] ?? 0}',
            label: l10n.pendingOrders,
            icon: Icons.receipt_long_outlined,
            valueColor: AppTheme.accentGold,
          ),
          StatTile(
            value: '${stats['payments'] ?? 0}',
            label: l10n.escrow,
            icon: Icons.account_balance_wallet_outlined,
            valueColor: AppTheme.primaryGreen,
          ),
          StatTile(
            value: '${stats['deliveries'] ?? 0}',
            label: l10n.deliveries,
            icon: Icons.local_shipping_outlined,
            valueColor: AppTheme.primaryGreen,
          ),
        ];
      case UserRole.trucker:
        return [
          StatTile(
            value: '${stats['listings'] ?? 0}',
            label: l10n.myTrips,
            icon: Icons.local_shipping,
          ),
          StatTile(
            value: '0',
            label: l10n.inTransit,
            icon: Icons.route,
            valueColor: AppTheme.statusTransit,
          ),
          StatTile(
            value: '0',
            label: l10n.matchedLoads,
            icon: Icons.handshake_outlined,
            valueColor: AppTheme.accentGold,
          ),
          StatTile(
            value: '${stats['deliveries'] ?? 0}',
            label: l10n.deliveries,
            icon: Icons.inventory_2_outlined,
          ),
        ];
      case UserRole.buyer:
        return [
          StatTile(
            value: '${stats['activeOrders'] ?? 0}',
            label: l10n.activeOrders,
            icon: Icons.receipt_long_outlined,
          ),
          StatTile(
            value: '${stats['payments'] ?? 0}',
            label: l10n.escrow,
            icon: Icons.account_balance_wallet_outlined,
            valueColor: AppTheme.accentGold,
          ),
          StatTile(
            value: '${stats['listings'] ?? 0}',
            label: l10n.farmersBrowsed,
            icon: Icons.people_outline,
            valueColor: AppTheme.statusInfo,
          ),
          StatTile(
            value: '${stats['deliveries'] ?? 0}',
            label: l10n.deliveries,
            icon: Icons.inventory_2_outlined,
          ),
        ];
    }
  }
}

/// Recent activity feed.
class _RecentActivityList extends StatefulWidget {
  final String userId;
  final AppLocalizations l10n;

  const _RecentActivityList({
    required this.userId,
    required this.l10n,
    super.key,
  });

  @override
  State<_RecentActivityList> createState() => _RecentActivityListState();
}

class _RecentActivityListState extends State<_RecentActivityList> {
  late Future<List<_ActivityItem>> _activityFuture;

  @override
  void initState() {
    super.initState();
    _activityFuture = _loadActivity();
  }

  @override
  void didUpdateWidget(covariant _RecentActivityList oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId) {
      _activityFuture = _loadActivity();
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<_ActivityItem>>(
      future: _activityFuture,
      builder: (context, snapshot) {
        final items = snapshot.data ?? [];

        if (items.isEmpty) {
          return WcCard(
            child: Center(
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 16),
                child: Column(
                  children: [
                    const Icon(
                      Icons.history,
                      size: 32,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(height: 6),
                    Text(
                      widget.l10n.noRecentActivity,
                      style: const TextStyle(
                        color: AppTheme.textMuted,
                        fontSize: 13,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          );
        }

        return Column(
          children: items
              .map((item) => _ActivityCard(item: item, l10n: widget.l10n))
              .toList(),
        );
      },
    );
  }

  Future<List<_ActivityItem>> _loadActivity() async {
    try {
      final isar = IsarService.instance.db;
      final items = <_ActivityItem>[];
      final l10n = widget.l10n;
      final userId = widget.userId;

      // Recent listings
      final listings = userId.isNotEmpty
          ? await isar.cropListings
              .where()
              .farmerIdEqualTo(userId)
              .sortByCreatedAtDesc()
              .limit(3)
              .findAll()
          : await isar.cropListings
              .where()
              .sortByCreatedAtDesc()
              .limit(3)
              .findAll();
      for (final l in listings) {
        items.add(
          _ActivityItem(
            description: l10n.listingCreatedActivity(l.cropType),
            timestamp: l.createdAt,
            type: _ActivityType.info,
          ),
        );
      }

      // Recent deliveries
      final deliveries =
          await isar.deliverys.where().sortByCreatedAtDesc().limit(3).findAll();
      final userDeliveries = userId.isNotEmpty
          ? deliveries
              .where((d) => d.farmerId == userId || d.buyerId == userId)
              .take(3)
              .toList()
          : deliveries;
      for (final d in userDeliveries) {
        items.add(
          _ActivityItem(
            description: l10n.deliveryStatusActivity(d.status.name),
            timestamp: d.createdAt,
            type: d.status == DeliveryStatus.delivered
                ? _ActivityType.success
                : _ActivityType.pending,
          ),
        );
      }

      items.sort((a, b) => b.timestamp.compareTo(a.timestamp));
      return items.take(3).toList();
    } on Object {
      return [];
    }
  }
}

enum _ActivityType { info, success, pending }

class _ActivityItem {
  final String description;
  final DateTime timestamp;
  final _ActivityType type;

  _ActivityItem({
    required this.description,
    required this.timestamp,
    required this.type,
  });
}

class _ActivityCard extends StatelessWidget {
  final _ActivityItem item;
  final AppLocalizations l10n;

  const _ActivityCard({required this.item, required this.l10n});

  @override
  Widget build(BuildContext context) {
    final (dotColor, _) = switch (item.type) {
      _ActivityType.info => (AppTheme.statusInfo, 'info'),
      _ActivityType.success => (AppTheme.statusSuccess, 'success'),
      _ActivityType.pending => (AppTheme.accentGold, 'pending'),
    };

    final timeDiff = DateTime.now().difference(item.timestamp);
    final timeLabel = timeDiff.inMinutes < 60
        ? l10n.minutesAgo(timeDiff.inMinutes)
        : timeDiff.inHours < 24
            ? l10n.hoursAgo(timeDiff.inHours)
            : l10n.daysAgo(timeDiff.inDays);

    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        decoration: BoxDecoration(
          color: AppTheme.surfaceWhite,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: AppTheme.borderLight),
        ),
        child: Row(
          children: [
            Container(
              width: 10,
              height: 10,
              decoration: BoxDecoration(
                color: dotColor,
                shape: BoxShape.circle,
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                item.description,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.textDark,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            const SizedBox(width: 8),
            Text(
              timeLabel,
              style: const TextStyle(
                fontSize: 12,
                color: AppTheme.textMuted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Tappable cloud icon that shows sync status and triggers manual sync.
///
/// - Online: cloud_done (white)
/// - Offline: cloud_off (dimmed)
///
/// On tap: shows a bottom sheet with sync queue status and retry option.
/// Live cloud sync icon that reflects real sync state.
///
/// States:
/// - **Offline** (cloud_off): No connectivity detected.
/// - **Syncing** (animated sync): SyncExecutor is actively processing.
/// - **All synced** (cloud_done + checkmark): Pending=0, Failed=0.
/// - **Pending** (cloud_upload): Items waiting to sync.
/// - **Failed** (cloud_off + error tint): Failed items need attention.
class _SyncStatusIcon extends StatefulWidget {
  final bool isOnline;

  const _SyncStatusIcon({required this.isOnline});

  @override
  State<_SyncStatusIcon> createState() => _SyncStatusIconState();
}

class _SyncStatusIconState extends State<_SyncStatusIcon>
    with SingleTickerProviderStateMixin {
  late final AnimationController _spinController;
  StreamSubscription<SyncBatchResult>? _syncSub;

  int _pendingCount = 0;
  int _failedCount = 0;
  bool _isSyncing = false;

  @override
  void initState() {
    super.initState();
    _spinController = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 1),
    );
    _loadCounts();
    _listenToSync();
  }

  void _listenToSync() {
    final executor = context.read<SyncExecutor>();
    _isSyncing = executor.isSyncing;
    if (_isSyncing) _spinController.repeat();

    _syncSub = executor.onSyncResult.listen((_) {
      _loadCounts();
    });
  }

  Future<void> _loadCounts() async {
    try {
      final queue = SyncQueue(IsarService.instance.db);
      final executor = context.read<SyncExecutor>();
      final pending = await queue.getPendingCount();
      final failed = await queue.getFailedCount();
      if (mounted) {
        setState(() {
          _pendingCount = pending;
          _failedCount = failed;
          _isSyncing = executor.isSyncing;
          if (_isSyncing) {
            _spinController.repeat();
          } else {
            _spinController.stop();
            _spinController.reset();
          }
        });
      }
    } on Object {
      // Ignore errors reading queue
    }
  }

  @override
  void didUpdateWidget(covariant _SyncStatusIcon oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.isOnline != widget.isOnline) {
      _loadCounts();
    }
  }

  @override
  void dispose() {
    _syncSub?.cancel();
    _spinController.dispose();
    super.dispose();
  }

  /// Determine the icon, color, and tooltip based on real state.
  ({IconData icon, Color color}) get _iconState {
    if (!widget.isOnline) {
      return (icon: Icons.cloud_off_outlined, color: Colors.white38);
    }
    if (_isSyncing) {
      return (icon: Icons.sync, color: Colors.white);
    }
    if (_failedCount > 0) {
      return (
        icon: Icons.cloud_off_outlined,
        color: const Color(0xFFFF8A80),
      );
    }
    if (_pendingCount > 0) {
      return (icon: Icons.cloud_upload_outlined, color: Colors.white70);
    }
    // All synced
    return (icon: Icons.cloud_done_outlined, color: Colors.white);
  }

  @override
  Widget build(BuildContext context) {
    final state = _iconState;

    return InkWell(
      onTap: () => _showSyncStatus(context),
      borderRadius: BorderRadius.circular(20),
      child: Padding(
        padding: const EdgeInsets.all(6),
        child: _isSyncing
            ? RotationTransition(
                turns: _spinController,
                child: Icon(state.icon, size: 22, color: state.color),
              )
            : Icon(state.icon, size: 22, color: state.color),
      ),
    );
  }

  Future<void> _showSyncStatus(BuildContext context) async {
    final l10n = AppLocalizations.of(context)!;
    final executor = context.read<SyncExecutor>();

    if (!context.mounted) return;

    showModalBottomSheet<void>(
      context: context,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) {
        return _SyncStatusSheet(
          isOnline: widget.isOnline,
          l10n: l10n,
          executor: executor,
          onSyncComplete: _loadCounts,
        );
      },
    );
  }
}

/// Bottom sheet showing live sync queue status with Sync Now / Retry actions.
class _SyncStatusSheet extends StatefulWidget {
  final bool isOnline;
  final AppLocalizations l10n;
  final SyncExecutor executor;
  final VoidCallback? onSyncComplete;

  const _SyncStatusSheet({
    required this.isOnline,
    required this.l10n,
    required this.executor,
    this.onSyncComplete,
  });

  @override
  State<_SyncStatusSheet> createState() => _SyncStatusSheetState();
}

class _SyncStatusSheetState extends State<_SyncStatusSheet> {
  int _pendingCount = 0;
  int _failedCount = 0;
  bool _isSyncing = false;
  String? _lastStatusMessage;
  StreamSubscription<SyncBatchResult>? _syncSub;

  @override
  void initState() {
    super.initState();
    _loadCounts();
    _isSyncing = widget.executor.isSyncing;
    _syncSub = widget.executor.onSyncResult.listen(_onSyncResult);
  }

  @override
  void dispose() {
    _syncSub?.cancel();
    super.dispose();
  }

  void _onSyncResult(SyncBatchResult result) {
    if (!mounted) return;
    _loadCounts();
    setState(() {
      _isSyncing = widget.executor.isSyncing;
      _lastStatusMessage = switch (result.status) {
        SyncBatchStatus.success => widget.l10n.syncComplete,
        SyncBatchStatus.partial =>
          '${result.succeeded} synced, ${result.failed} failed',
        SyncBatchStatus.empty => widget.l10n.syncComplete,
        SyncBatchStatus.skipped => null, // another sync in progress, no change
        SyncBatchStatus.noInternet => widget.l10n.offlineModeIndicator,
        SyncBatchStatus.networkError => 'Network error',
        SyncBatchStatus.authError => 'Authentication error',
        SyncBatchStatus.rateLimited => 'Rate limited — try later',
        SyncBatchStatus.error => result.errorMessage ?? 'Error',
      };
    });
  }

  Future<void> _loadCounts() async {
    try {
      final queue = SyncQueue(IsarService.instance.db);
      final pending = await queue.getPendingCount();
      final failed = await queue.getFailedCount();
      if (mounted) {
        setState(() {
          _pendingCount = pending;
          _failedCount = failed;
        });
      }
    } on Object {
      // Ignore
    }
  }

  Future<void> _syncNow() async {
    setState(() => _isSyncing = true);
    try {
      final result = await widget.executor.syncNow();
      if (mounted) {
        _onSyncResult(result);
        widget.onSyncComplete?.call();
      }
    } on Object {
      // Ensure _isSyncing resets even if executor throws unexpectedly
      if (mounted) {
        setState(() => _isSyncing = widget.executor.isSyncing);
      }
    }
  }

  Future<void> _retryFailed() async {
    final queue = SyncQueue(IsarService.instance.db);
    final retried = await queue.retryFailed();
    if (mounted && retried > 0) {
      await _syncNow();
    } else if (mounted) {
      // Refresh counts — failed ops may have been cleared
      _loadCounts();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = widget.l10n;

    // Determine header status
    final IconData statusIcon;
    final Color statusColor;
    final String statusText;

    if (_isSyncing) {
      statusIcon = Icons.sync;
      statusColor = AppTheme.statusInfo;
      statusText = l10n.syncInProgress;
    } else if (!widget.isOnline) {
      statusIcon = Icons.cloud_off;
      statusColor = AppTheme.textMuted;
      statusText = l10n.offlineModeIndicator;
    } else if (_failedCount > 0) {
      statusIcon = Icons.error_outline;
      statusColor = AppTheme.statusError;
      statusText = '$_failedCount ${l10n.syncFailed.toLowerCase()}';
    } else if (_pendingCount > 0) {
      statusIcon = Icons.cloud_upload;
      statusColor = AppTheme.accentGold;
      statusText = '$_pendingCount ${l10n.syncPending.toLowerCase()}';
    } else {
      statusIcon = Icons.cloud_done;
      statusColor = AppTheme.statusSuccess;
      statusText = l10n.syncComplete;
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(24, 16, 24, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            // Drag handle
            Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: AppTheme.textMuted.withAlpha(60),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(height: 20),
            // Status icon (animated when syncing)
            _isSyncing
                ? TweenAnimationBuilder<double>(
                    tween: Tween(begin: 0, end: 1),
                    duration: const Duration(seconds: 1),
                    builder: (_, value, child) => Transform.rotate(
                      angle: value * 2 * 3.14159,
                      child: child,
                    ),
                    child: Icon(statusIcon, size: 40, color: statusColor),
                    onEnd: () {
                      // Restart if still syncing
                      if (mounted) setState(() {});
                    },
                  )
                : Icon(statusIcon, size: 40, color: statusColor),
            const SizedBox(height: 10),
            Text(
              statusText,
              style: TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: statusColor,
              ),
            ),
            if (_lastStatusMessage != null &&
                _lastStatusMessage != statusText) ...[
              const SizedBox(height: 4),
              Text(
                _lastStatusMessage!,
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.textMuted,
                ),
              ),
            ],
            const SizedBox(height: 20),
            // Pending operations row
            _SyncInfoRow(
              icon: Icons.cloud_upload_outlined,
              label: l10n.syncPending,
              value: '$_pendingCount',
              color:
                  _pendingCount > 0 ? AppTheme.accentGold : AppTheme.textMuted,
            ),
            const SizedBox(height: 10),
            // Failed operations row
            _SyncInfoRow(
              icon: Icons.error_outline,
              label: l10n.syncFailed,
              value: '$_failedCount',
              color:
                  _failedCount > 0 ? AppTheme.statusError : AppTheme.textMuted,
            ),
            const SizedBox(height: 20),
            // Action buttons
            if (_failedCount > 0 && widget.isOnline)
              SizedBox(
                width: double.infinity,
                child: ElevatedButton.icon(
                  onPressed: _isSyncing ? null : _retryFailed,
                  icon: _isSyncing
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(Icons.refresh, size: 20),
                  label: Text(_isSyncing ? l10n.syncInProgress : l10n.retry),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: AppTheme.statusError,
                    foregroundColor: Colors.white,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(12),
                    ),
                  ),
                ),
              )
            else if (_pendingCount > 0 && widget.isOnline)
              SizedBox(
                width: double.infinity,
                child: ElevatedButton.icon(
                  onPressed: _isSyncing ? null : _syncNow,
                  icon: _isSyncing
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(Icons.sync, size: 20),
                  label: Text(
                    _isSyncing ? l10n.syncInProgress : l10n.syncNow,
                  ),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: AppTheme.primaryGreen,
                    foregroundColor: Colors.white,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(12),
                    ),
                  ),
                ),
              ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }
}

/// Single row in the sync status sheet.
class _SyncInfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final Color color;

  const _SyncInfoRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 20, color: color),
        const SizedBox(width: 10),
        Text(
          label,
          style: const TextStyle(
            fontSize: 14,
            color: AppTheme.textDark,
          ),
        ),
        const Spacer(),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
          decoration: BoxDecoration(
            color: color.withAlpha(20),
            borderRadius: BorderRadius.circular(12),
          ),
          child: Text(
            value,
            style: TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w600,
              color: color,
            ),
          ),
        ),
      ],
    );
  }
}

/// Notification bell icon with dynamic red dot indicator.
///
/// Shows a red dot when there are unread notifications (failed syncs,
/// new deliveries, pending payments). The dot disappears when the user
/// taps the bell and views the notification sheet.
class _NotificationIcon extends StatefulWidget {
  final String userId;
  final AppLocalizations l10n;
  final bool isOnline;
  final ValueChanged<int>? onNavigateToTab;

  const _NotificationIcon({
    required this.userId,
    required this.l10n,
    required this.isOnline,
    this.onNavigateToTab,
  });

  @override
  State<_NotificationIcon> createState() => _NotificationIconState();
}

class _NotificationIconState extends State<_NotificationIcon> {
  List<_NotifItem> _notifications = [];
  bool _hasUnread = false;
  late Future<void> _loadFuture;
  final Set<String> _dismissedKeys = {};

  @override
  void initState() {
    super.initState();
    _loadFuture = _loadNotifications();
  }

  @override
  void didUpdateWidget(covariant _NotificationIcon oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId) {
      _loadFuture = _loadNotifications();
    }
  }

  Future<void> _loadNotifications() async {
    final items = <_NotifItem>[];
    try {
      final isar = IsarService.instance.db;
      final syncQueue = SyncQueue(isar);
      final userId = widget.userId;

      // Failed sync operations
      final failedCount = await syncQueue.getFailedCount();
      if (failedCount > 0) {
        items.add(
          _NotifItem(
            icon: Icons.sync_problem,
            title: widget.l10n.syncFailed,
            subtitle: '$failedCount operations need attention',
            color: AppTheme.statusError,
            time: DateTime.now(),
            action: _NotifAction.syncStatus,
            key: 'sync_failed',
          ),
        );
      }

      // Pending sync operations
      final pendingCount = await syncQueue.getPendingCount();
      if (pendingCount > 0) {
        items.add(
          _NotifItem(
            icon: Icons.cloud_upload_outlined,
            title: widget.l10n.syncPending,
            subtitle: '$pendingCount waiting to sync',
            color: AppTheme.accentGold,
            time: DateTime.now(),
            action: _NotifAction.syncStatus,
            key: 'sync_pending',
          ),
        );
      }

      // Recent deliveries
      final deliveries = userId.isNotEmpty
          ? await isar.deliverys
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .sortByCreatedAtDesc()
              .limit(3)
              .findAll()
          : <Delivery>[];
      for (final d in deliveries) {
        items.add(
          _NotifItem(
            icon: d.status == DeliveryStatus.delivered
                ? Icons.check_circle_outline
                : Icons.local_shipping_outlined,
            title: widget.l10n.deliveryStatusActivity(d.status.name),
            subtitle: d.farmerName,
            color: d.status == DeliveryStatus.delivered
                ? AppTheme.statusSuccess
                : AppTheme.statusInfo,
            time: d.createdAt,
            action: _NotifAction.deliveriesTab,
            key: 'delivery_${d.deliveryId}',
          ),
        );
      }

      // Recent payments
      final payments = userId.isNotEmpty
          ? await isar.payments
              .where()
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .sortByCreatedAtDesc()
              .limit(2)
              .findAll()
          : <Payment>[];
      for (final p in payments) {
        items.add(
          _NotifItem(
            icon: Icons.account_balance_wallet_outlined,
            title: '${p.status.name} payment',
            subtitle: '${p.amount} ETB',
            color: p.status == PaymentStatus.released
                ? AppTheme.statusSuccess
                : AppTheme.accentGold,
            time: p.createdAt,
            action: _NotifAction.paymentsTab,
            key: 'payment_${p.paymentId}',
          ),
        );
      }

      items.sort((a, b) => b.time.compareTo(a.time));
    } on Object {
      // Silently ignore — show empty state
    }
    if (mounted) {
      setState(() {
        _notifications = items;
        _hasUnread = items.isNotEmpty;
      });
    }
  }

  List<_NotifItem> get _visibleNotifications =>
      _notifications.where((n) => !_dismissedKeys.contains(n.key)).toList();

  void _dismissNotification(String key) {
    setState(() => _dismissedKeys.add(key));
  }

  void _clearAllNotifications() {
    setState(() {
      _dismissedKeys.addAll(_notifications.map((n) => n.key));
    });
  }

  void _handleNotifTap(BuildContext sheetContext, _NotifItem item) {
    _dismissNotification(item.key);
    Navigator.of(sheetContext).pop(); // close sheet
    switch (item.action) {
      case _NotifAction.syncStatus:
        _openSyncStatusSheet();
      case _NotifAction.deliveriesTab:
        widget.onNavigateToTab?.call(2);
      case _NotifAction.paymentsTab:
        widget.onNavigateToTab?.call(3);
    }
  }

  void _openSyncStatusSheet() {
    final l10n = AppLocalizations.of(context)!;
    final executor = context.read<SyncExecutor>();
    showModalBottomSheet<void>(
      context: context,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) {
        return _SyncStatusSheet(
          isOnline: widget.isOnline,
          l10n: l10n,
          executor: executor,
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<void>(
      future: _loadFuture,
      builder: (context, _) {
        return InkWell(
          onTap: () => _showNotifications(context),
          borderRadius: BorderRadius.circular(20),
          child: Padding(
            padding: const EdgeInsets.all(4),
            child: Stack(
              children: [
                const Icon(
                  Icons.notifications_outlined,
                  size: 24,
                  color: Colors.white,
                ),
                if (_hasUnread)
                  Positioned(
                    right: 0,
                    top: 0,
                    child: Container(
                      width: 9,
                      height: 9,
                      decoration: BoxDecoration(
                        color: AppTheme.statusError,
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: AppTheme.primaryGreen,
                          width: 1.5,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        );
      },
    );
  }

  void _showNotifications(BuildContext context) {
    // Mark as read when opened
    setState(() => _hasUnread = false);

    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (sheetContext) {
        return StatefulBuilder(
          builder: (sheetContext, setSheetState) {
            final visible = _visibleNotifications;
            return DraggableScrollableSheet(
              expand: false,
              initialChildSize: 0.4,
              maxChildSize: 0.7,
              builder: (context, scrollController) {
                return Column(
                  children: [
                    // Drag handle + title
                    Padding(
                      padding: const EdgeInsets.fromLTRB(24, 16, 24, 12),
                      child: Column(
                        children: [
                          Container(
                            width: 40,
                            height: 4,
                            decoration: BoxDecoration(
                              color: AppTheme.textMuted.withAlpha(60),
                              borderRadius: BorderRadius.circular(2),
                            ),
                          ),
                          const SizedBox(height: 16),
                          Row(
                            children: [
                              Text(
                                widget.l10n.notifications,
                                style: const TextStyle(
                                  fontSize: 18,
                                  fontWeight: FontWeight.w700,
                                  color: AppTheme.textDark,
                                ),
                              ),
                              const Spacer(),
                              if (visible.isNotEmpty) ...[
                                GestureDetector(
                                  onTap: () {
                                    _clearAllNotifications();
                                    setSheetState(() {});
                                    Navigator.of(sheetContext).pop();
                                  },
                                  child: Text(
                                    widget.l10n.clearAll,
                                    style: const TextStyle(
                                      fontSize: 14,
                                      fontWeight: FontWeight.w600,
                                      color: AppTheme.statusError,
                                    ),
                                  ),
                                ),
                                const SizedBox(width: 12),
                                Text(
                                  '${visible.length}',
                                  style: const TextStyle(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600,
                                    color: AppTheme.textMuted,
                                  ),
                                ),
                              ],
                            ],
                          ),
                        ],
                      ),
                    ),
                    const Divider(height: 1),
                    // Notification list
                    Expanded(
                      child: visible.isEmpty
                          ? Center(
                              child: Column(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  Icon(
                                    Icons.notifications_none,
                                    size: 48,
                                    color: AppTheme.textMuted.withAlpha(80),
                                  ),
                                  const SizedBox(height: 12),
                                  Text(
                                    widget.l10n.noNotificationsYet,
                                    style: const TextStyle(
                                      fontSize: 16,
                                      fontWeight: FontWeight.w600,
                                      color: AppTheme.textMuted,
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Text(
                                    widget.l10n.noNotificationsDescription,
                                    style: TextStyle(
                                      fontSize: 13,
                                      color: AppTheme.textMuted.withAlpha(160),
                                    ),
                                  ),
                                ],
                              ),
                            )
                          : ListView.separated(
                              controller: scrollController,
                              padding: const EdgeInsets.symmetric(
                                horizontal: 20,
                                vertical: 12,
                              ),
                              itemCount: visible.length,
                              separatorBuilder: (_, __) =>
                                  const SizedBox(height: 8),
                              itemBuilder: (context, index) {
                                final item = visible[index];
                                return Dismissible(
                                  key: ValueKey(item.key),
                                  direction: DismissDirection.endToStart,
                                  onDismissed: (_) {
                                    _dismissNotification(item.key);
                                    setSheetState(() {});
                                  },
                                  background: Container(
                                    alignment: Alignment.centerRight,
                                    padding: const EdgeInsets.only(right: 20),
                                    decoration: BoxDecoration(
                                      color: AppTheme.statusError,
                                      borderRadius: BorderRadius.circular(12),
                                    ),
                                    child: const Icon(
                                      Icons.delete_outline,
                                      color: Colors.white,
                                    ),
                                  ),
                                  child: _NotifCard(
                                    item: item,
                                    l10n: widget.l10n,
                                    onTap: () =>
                                        _handleNotifTap(sheetContext, item),
                                  ),
                                );
                              },
                            ),
                    ),
                  ],
                );
              },
            );
          },
        );
      },
    );
  }
}

enum _NotifAction { syncStatus, deliveriesTab, paymentsTab }

class _NotifItem {
  final IconData icon;
  final String title;
  final String subtitle;
  final Color color;
  final DateTime time;
  final _NotifAction action;
  final String key;

  _NotifItem({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.color,
    required this.time,
    required this.action,
    required this.key,
  });
}

class _NotifCard extends StatelessWidget {
  final _NotifItem item;
  final AppLocalizations l10n;
  final VoidCallback? onTap;

  const _NotifCard({required this.item, required this.l10n, this.onTap});

  @override
  Widget build(BuildContext context) {
    final timeDiff = DateTime.now().difference(item.time);
    final timeLabel = timeDiff.inMinutes < 60
        ? l10n.minutesAgo(timeDiff.inMinutes)
        : timeDiff.inHours < 24
            ? l10n.hoursAgo(timeDiff.inHours)
            : l10n.daysAgo(timeDiff.inDays);

    return Material(
      color: AppTheme.surfaceWhite,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: AppTheme.borderLight),
          ),
          child: Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: item.color.withAlpha(20),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(item.icon, size: 20, color: item.color),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      item.title,
                      style: const TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 2),
                    Text(
                      item.subtitle,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.textMuted,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Text(
                timeLabel,
                style: const TextStyle(
                  fontSize: 11,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(width: 4),
              Icon(
                Icons.chevron_right,
                size: 18,
                color: AppTheme.textMuted.withAlpha(120),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
