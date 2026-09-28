import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';
// The device build gets isar via isar_service.dart's re-export, but the web
// build swaps in isar_service_web.dart which does NOT re-export it — keep the
// direct import so QAfterFilterCondition resolves on both platforms.
// ignore: unnecessary_import
import 'package:isar/isar.dart' show QAfterFilterCondition;
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_queue_web.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/messages_badge_button.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/intent_card.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// Home screen — compact dashboard matching mockup.
///
/// Layout: green header → banner → at a glance (tappable counts and
/// shortcuts) → role card → recent.
/// Designed to fit in one screen view.
///
/// Uses a [_refreshKey] counter to force overview/recent widgets to reload
/// when returning from create screens (FAB or the "Sell Crops" tile).
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
  StreamSubscription<dynamic>? _syncSub;
  StreamSubscription<dynamic>? _authSub;

  /// Track the last userId so we can detect auth transitions on web.
  String _lastUserId = '';

  @override
  void initState() {
    super.initState();
    if (kIsWeb) {
      // On web there is no SyncExecutor. Listen to AuthBloc instead so that
      // when authentication completes after a browser refresh the dashboard
      // child widgets are forced to reload with the correct userId.
      _authSub = context.read<AuthBloc>().stream.listen((state) {
        if (state is AuthAuthenticated && state.userId != _lastUserId) {
          _lastUserId = state.userId;
          _refreshData();
        }
      });
    } else {
      _syncSub = context.read<SyncExecutor>().onSyncResult.listen((_) {
        _refreshData();
      });
    }
  }

  @override
  void dispose() {
    _syncSub?.cancel();
    _authSub?.cancel();
    super.dispose();
  }

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

    final windowClass = Breakpoints.of(context);

    // Mobile: keep the exact current layout
    if (windowClass == WindowClass.mobile) {
      return _buildMobileLayout(
        l10n: l10n,
        role: role,
        userName: userName,
        userId: userId,
        woreda: woreda,
      );
    }

    // Tablet / Desktop: flat header, reorganised content
    return _buildWideLayout(
      context: context,
      l10n: l10n,
      role: role,
      userName: userName,
      userId: userId,
      woreda: woreda,
      windowClass: windowClass,
    );
  }

  // ── Mobile layout ──────────────────────────────────────────────
  Widget _buildMobileLayout({
    required AppLocalizations l10n,
    required UserRole role,
    required String userName,
    required String userId,
    required String woreda,
  }) {
    final roleCard = _buildRoleCard(role: role, userId: userId, l10n: l10n);
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
            role: role,
            l10n: l10n,
            onNavigateToTab: widget.onNavigateToTab,
            onRefresh: _refreshData,
          ),

          // ── At a glance: counts + shortcuts, every tile tappable ──
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: _SectionTitle(l10n.atAGlance, fontSize: 16),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 0),
            child: _buildDashboard(role: role, userId: userId, l10n: l10n),
          ),

          // ── Trucker hero card / buyer active intents ──────────────
          if (roleCard != null)
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
              child: roleCard,
            ),

          // ── Recent ───────────────────────────────────────────────
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: _SectionTitle(l10n.recentActivity, fontSize: 16),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 16),
            child: _buildRecent(role: role, userId: userId, l10n: l10n),
          ),
        ],
      ),
    );
  }

  Widget _buildDashboard({
    required UserRole role,
    required String userId,
    required AppLocalizations l10n,
    int columns = 2,
  }) {
    return _OverviewStats(
      key: ValueKey('overview_$_refreshKey'),
      role: role,
      userId: userId,
      l10n: l10n,
      columns: columns,
      onNavigateToTab: widget.onNavigateToTab,
      onRefresh: _refreshData,
    );
  }

  Widget _buildRecent({
    required UserRole role,
    required String userId,
    required AppLocalizations l10n,
  }) {
    return _RecentActivityList(
      key: ValueKey('recent_$_refreshKey'),
      role: role,
      userId: userId,
      l10n: l10n,
      onNavigateToTab: widget.onNavigateToTab,
      onRefresh: _refreshData,
    );
  }

  /// The role's companion card: the trucker's current trip or the buyer's
  /// active intents. Farmers have none.
  Widget? _buildRoleCard({
    required UserRole role,
    required String userId,
    required AppLocalizations l10n,
  }) {
    return switch (role) {
      UserRole.trucker => TruckerHeroCard(
          key: ValueKey('hero_$_refreshKey'),
          userId: userId,
          onRefresh: _refreshData,
        ),
      UserRole.buyer => _ActiveIntentsSection(
          key: ValueKey('intents_$_refreshKey'),
          userId: userId,
          l10n: l10n,
          onRefresh: _refreshData,
        ),
      UserRole.farmer => null,
    };
  }

  // ── Desktop / Tablet layout ────────────────────────────────────
  Widget _buildWideLayout({
    required BuildContext context,
    required AppLocalizations l10n,
    required UserRole role,
    required String userName,
    required String userId,
    required String woreda,
    required WindowClass windowClass,
  }) {
    final isDesktop = windowClass == WindowClass.desktop;
    final maxWidth = isDesktop ? 1200.0 : 800.0;
    final hPadding = isDesktop ? 32.0 : 24.0;

    return SingleChildScrollView(
      child: Center(
        child: ConstrainedBox(
          constraints: BoxConstraints(maxWidth: maxWidth),
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: hPadding),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // ── Flat page header (no green curve) ────────────
                _DesktopPageHeader(
                  userName: userName,
                  woreda: woreda,
                  isOnline: widget.isOnline,
                  l10n: l10n,
                ),

                const SizedBox(height: 24),

                // ── Main content ─────────────────────────────────
                if (isDesktop)
                  _buildDesktopTwoColumn(
                    l10n: l10n,
                    role: role,
                    userId: userId,
                  )
                else
                  _buildTabletSingleColumn(
                    l10n: l10n,
                    role: role,
                    userId: userId,
                  ),

                const SizedBox(height: 24),
              ],
            ),
          ),
        ),
      ),
    );
  }

  // ── Desktop: full-width dashboard, then recent + role card ─────
  Widget _buildDesktopTwoColumn({
    required AppLocalizations l10n,
    required UserRole role,
    required String userId,
  }) {
    final roleCard = _buildRoleCard(role: role, userId: userId, l10n: l10n);
    final recent = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _SectionTitle(l10n.recentActivity),
        const SizedBox(height: 12),
        _buildRecent(role: role, userId: userId, l10n: l10n),
      ],
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _SectionTitle(l10n.atAGlance),
        const SizedBox(height: 12),
        _buildDashboard(role: role, userId: userId, l10n: l10n, columns: 3),
        const SizedBox(height: 24),
        if (roleCard == null)
          recent
        else
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(flex: 3, child: recent),
              const SizedBox(width: 24),
              Expanded(flex: 2, child: roleCard),
            ],
          ),
      ],
    );
  }

  // ── Tablet: single column ──────────────────────────────────────
  Widget _buildTabletSingleColumn({
    required AppLocalizations l10n,
    required UserRole role,
    required String userId,
  }) {
    final roleCard = _buildRoleCard(role: role, userId: userId, l10n: l10n);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _SectionTitle(l10n.atAGlance),
        const SizedBox(height: 12),
        _buildDashboard(role: role, userId: userId, l10n: l10n, columns: 3),
        if (roleCard != null) ...[
          const SizedBox(height: 16),
          roleCard,
        ],
        const SizedBox(height: 20),
        _SectionTitle(l10n.recentActivity),
        const SizedBox(height: 12),
        _buildRecent(role: role, userId: userId, l10n: l10n),
      ],
    );
  }
}

/// Home section heading.
class _SectionTitle extends StatelessWidget {
  final String text;
  final double fontSize;

  const _SectionTitle(this.text, {this.fontSize = 18});

  @override
  Widget build(BuildContext context) {
    return Text(
      text,
      style: TextStyle(
        fontSize: fontSize,
        fontWeight: FontWeight.w600,
        color: AppTheme.textDark,
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
  final UserRole role;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _HomeHeaderWithBanner({
    required this.userName,
    required this.userId,
    required this.woreda,
    required this.isOnline,
    required this.role,
    required this.l10n,
    this.onNavigateToTab,
    this.onRefresh,
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
                      // Cloud sync status icon (tappable). Web has no
                      // SyncExecutor provided and no offline queue, so the icon
                      // is meaningless there AND its initState read of
                      // SyncExecutor throws ProviderNotFoundException — which
                      // crashed the whole Home screen on a narrow (<600px) web
                      // viewport, since the mobile layout is width- not
                      // platform-selected. Omit it on web.
                      if (!kIsWeb) ...[
                        _SyncStatusIcon(isOnline: isOnline),
                        const SizedBox(width: 14),
                      ],
                      // In-app chat inbox (with unread badge)
                      const MessagesBadgeButton(color: Colors.white),
                      const SizedBox(width: 4),
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
            child: _BannerCard(
              role: role,
              l10n: l10n,
              onRefresh: onRefresh,
            ),
          ),
        ],
      ),
    );
  }
}

/// Banner card — tappable CTA for farmers, static slogan for other roles.
class _BannerCard extends StatelessWidget {
  final UserRole role;
  final AppLocalizations l10n;
  final VoidCallback? onRefresh;

  const _BannerCard({
    required this.role,
    required this.l10n,
    this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final isFarmer = role == UserRole.farmer;
    final headline = isFarmer ? l10n.sellYourHarvest : l10n.bannerHeadline;
    final subtitle = isFarmer ? l10n.tapToListCrops : l10n.bannerSubtitle;

    final card = Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          colors: isFarmer
              ? [AppTheme.primary, AppTheme.primaryDeep]
              : [AppTheme.accentGold, AppTheme.accentGoldLight],
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
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  headline,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w700,
                    color: Colors.white,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  subtitle,
                  style: TextStyle(
                    fontSize: 13,
                    color: Colors.white.withAlpha(220),
                  ),
                ),
              ],
            ),
          ),
          if (isFarmer)
            Container(
              width: 36,
              height: 36,
              decoration: BoxDecoration(
                color: Colors.white.withAlpha(50),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Icon(Icons.add, color: Colors.white, size: 22),
            ),
        ],
      ),
    );

    if (!isFarmer) return card;

    return GestureDetector(
      onTap: () async {
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateCropListingScreen(),
          ),
        );
        onRefresh?.call();
      },
      child: card,
    );
  }
}

/// Flat page header for desktop/tablet — replaces the curved green header.
///
/// On wide screens the sidebar already provides green branding, so this
/// header uses a white/surface background with the user name as headline
/// and location + sync status inline.
class _DesktopPageHeader extends StatelessWidget {
  final String userName;
  final String woreda;
  final bool isOnline;
  final AppLocalizations l10n;

  const _DesktopPageHeader({
    required this.userName,
    required this.woreda,
    required this.isOnline,
    required this.l10n,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(0, 28, 0, 20),
      decoration: const BoxDecoration(
        color: AppTheme.surfaceWhite,
        border: Border(
          bottom: BorderSide(color: AppTheme.borderLight),
        ),
      ),
      child: Column(
        children: [
          Text(
            l10n.welcomeBackUser(userName),
            style: const TextStyle(
              fontSize: 24,
              fontWeight: FontWeight.w700,
              color: AppTheme.textDark,
            ),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 6),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const Icon(
                Icons.location_on,
                size: 14,
                color: AppTheme.textMuted,
              ),
              const SizedBox(width: 4),
              Text(
                woreda.isNotEmpty ? woreda : l10n.wereda,
                style: const TextStyle(
                  fontSize: 13,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(width: 16),
              _DesktopSyncChip(isOnline: isOnline),
            ],
          ),
        ],
      ),
    );
  }
}

/// Compact sync status chip for the desktop/tablet flat header.
class _DesktopSyncChip extends StatelessWidget {
  final bool isOnline;

  const _DesktopSyncChip({required this.isOnline});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final label = isOnline ? l10n.synced : l10n.offline;
    final color = isOnline ? AppTheme.statusSuccess : AppTheme.textMuted;
    final icon =
        isOnline ? Icons.cloud_done_outlined : Icons.cloud_off_outlined;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: color.withAlpha(20),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: color),
          const SizedBox(width: 4),
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: color,
            ),
          ),
        ],
      ),
    );
  }
}

/// Places a Home dashboard tile or recent-activity row can open.
enum _HomeDest {
  sellCrops,
  myCrops,
  myTrips,
  findCrops,
  findTrips,
  orders,
  deliveries,
  payments,
}

/// Opens [dest] for [role]: go_router on web; on mobile a bar tab where
/// the destination is one of [role]'s tabs, else a pushed screen.
Future<void> _openHomeDest(
  BuildContext context, {
  required UserRole role,
  required _HomeDest dest,
  ValueChanged<int>? onNavigateToTab,
  VoidCallback? onRefresh,
}) async {
  Future<void> push(Widget screen) async {
    await Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => screen),
    );
    onRefresh?.call();
  }

  Future<void> webOrPush(String route, Widget screen) async {
    if (kIsWeb) {
      context.go(route);
      return;
    }
    await push(screen);
  }

  Future<void> tabOrPush(RoleTab tab, Widget? screen) async {
    if (kIsWeb) {
      context.go(tab.route);
      return;
    }
    final index = roleTabIndex(role, tab);
    if (index != null) {
      onNavigateToTab?.call(index);
    } else if (screen != null) {
      await push(screen);
    }
  }

  switch (dest) {
    case _HomeDest.sellCrops:
      await push(const CreateCropListingScreen());
    case _HomeDest.myCrops:
      await webOrPush(
        '/my-crops',
        const BrowseListingsScreen(ownerMode: true),
      );
    case _HomeDest.myTrips:
    case _HomeDest.findTrips:
      await webOrPush('/trips', const BrowseTripsScreen());
    case _HomeDest.findCrops:
      await webOrPush('/find-crops', const FindCropsScreen());
    case _HomeDest.orders:
      // Orders isn't a trucker tab (truckers act on deliveries), so it
      // pushes for them.
      await tabOrPush(
        RoleTab.orders,
        const Scaffold(body: OrdersTabScreen(showBackButton: true)),
      );
    case _HomeDest.deliveries:
      await tabOrPush(RoleTab.deliveries, null);
    case _HomeDest.payments:
      // Payments is only a trucker tab; farmers and buyers get the
      // payment history screen.
      await tabOrPush(RoleTab.payments, const PaymentHistoryScreen());
  }
}

/// One Home dashboard tile. Every tile opens its section; a tile with a
/// [count] also summarises it, one without is a plain shortcut.
class _DashboardTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color color;
  final int? count;

  /// Filled style for the role's primary verb (Sell Crops, Find Crops).
  final bool emphasized;
  final VoidCallback onTap;

  const _DashboardTile({
    required this.icon,
    required this.label,
    required this.color,
    required this.onTap,
    this.count,
    this.emphasized = false,
  });

  @override
  Widget build(BuildContext context) {
    final background = emphasized ? color : AppTheme.surfaceWhite;
    final foreground = emphasized ? Colors.white : AppTheme.textDark;
    final muted = emphasized ? Colors.white70 : AppTheme.textMuted;
    final count = this.count;

    return Material(
      color: background,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: emphasized ? null : Border.all(color: AppTheme.borderLight),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Row(
                children: [
                  Container(
                    width: 36,
                    height: 36,
                    decoration: BoxDecoration(
                      color: emphasized
                          ? Colors.white.withAlpha(40)
                          : color.withAlpha(20),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Icon(
                      icon,
                      size: 20,
                      color: emphasized ? Colors.white : color,
                    ),
                  ),
                  const Spacer(),
                  if (count != null)
                    Text(
                      '$count',
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w700,
                        color: emphasized ? Colors.white : color,
                      ),
                    ),
                ],
              ),
              const SizedBox(height: 10),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: foreground,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  Icon(Icons.chevron_right, size: 18, color: muted),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Lays [tiles] out in rows of [columns]. A remainder leads on its own
/// (wider) row instead of dangling at the end, so the role's first tile,
/// its primary action, gets the extra width.
class _DashboardGrid extends StatelessWidget {
  final List<Widget> tiles;
  final int columns;

  const _DashboardGrid({required this.tiles, required this.columns});

  @override
  Widget build(BuildContext context) {
    final lead = tiles.length % columns;
    final rows = <List<Widget>>[
      if (lead > 0) tiles.sublist(0, lead),
      for (var i = lead; i < tiles.length; i += columns)
        tiles.sublist(i, i + columns),
    ];

    return Column(
      children: [
        for (var r = 0; r < rows.length; r++)
          Padding(
            padding: EdgeInsets.only(top: r == 0 ? 0 : 10),
            child: IntrinsicHeight(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  for (var c = 0; c < rows[r].length; c++) ...[
                    if (c > 0) const SizedBox(width: 10),
                    Expanded(child: rows[r][c]),
                  ],
                ],
              ),
            ),
          ),
      ],
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

    return BlocListener<BuyerIntentBloc, BuyerIntentState>(
      listenWhen: (prev, curr) => curr is BuyerIntentCreated,
      listener: (context, state) {
        // After a new intent is created, reload the active intents list.
        context
            .read<BuyerIntentBloc>()
            .add(LoadActiveIntents(buyerId: widget.userId));
      },
      child: BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
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
      ),
    );
  }
}

/// "At a glance" dashboard: the role's counts and shortcuts in one grid,
/// every tile opening the screen it summarises.
class _OverviewStats extends StatefulWidget {
  final UserRole role;
  final String userId;
  final AppLocalizations l10n;
  final int columns;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _OverviewStats({
    required this.role,
    required this.userId,
    required this.l10n,
    super.key,
    this.columns = 2,
    this.onNavigateToTab,
    this.onRefresh,
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
      setState(() {
        _statsFuture = _loadStats(widget.userId, widget.role);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<Map<String, int>>(
      future: _statsFuture,
      builder: (context, snapshot) {
        final stats = snapshot.data ?? {};
        return _DashboardGrid(
          tiles: _tilesForRole(widget.role, stats),
          columns: widget.columns,
        );
      },
    );
  }

  Future<Map<String, int>> _loadStats(String userId, UserRole role) async {
    if (userId.isEmpty) return {};
    if (kIsWeb) return _loadWebStats(userId, role);
    return _loadIsarStats(userId, role);
  }

  Future<Map<String, int>> _loadWebStats(
    String userId,
    UserRole role,
  ) async {
    try {
      final api = ApiClient();
      if (!api.isInitialized) {
        debugPrint('[OverviewStats] ApiClient not initialized');
        return {};
      }
      final result = <String, int>{};

      // Each call is isolated so one failing endpoint doesn't zero-out all stats.
      Future<Map<String, dynamic>> safeGet(
        String path, {
        Map<String, dynamic>? queryParameters,
      }) async {
        try {
          return await api.get<Map<String, dynamic>>(
            path,
            queryParameters: queryParameters,
          );
        } on Object catch (e) {
          debugPrint('[OverviewStats] $path failed: $e');
          return <String, dynamic>{};
        }
      }

      // One GET per resource, with the same path and params the Recent
      // Activity section and the trucker hero card use, so ApiClient folds
      // the concurrent identical reads into a single request (SIM-21).
      if (role == UserRole.farmer) {
        final cropsFuture = safeGet(
          '/crops',
          queryParameters: {'farmerId': userId, 'limit': '50'},
        );
        final deliveriesFuture = safeGet(
          '/deliveries',
          queryParameters: {'farmerId': userId, 'limit': '50'},
        );
        final orderItems =
            await _fetchOrderPages(safeGet, {'farmerId': userId});
        result['listings'] = _countApiItems(await cropsFuture, alt: 'crops');
        result['pendingOrders'] =
            _countOrdersByStatus(orderItems, const {'funded'});
        result['payments'] =
            _countOrdersByStatus(orderItems, escrowHeldOrderStatusNames);
        result['deliveries'] =
            _countApiItems(await deliveriesFuture, alt: 'deliveries');
      } else if (role == UserRole.buyer) {
        // The server scopes a buyer via mine=true (ParticipantIndex); a
        // buyerId= filter is not a delivery query parameter.
        final deliveriesFuture = safeGet(
          '/deliveries',
          queryParameters: {'mine': 'true', 'limit': '50'},
        );
        final cropsFuture =
            safeGet('/crops', queryParameters: {'status': 'available'});
        final orderItems = await _fetchOrderPages(safeGet, {'buyerId': userId});
        result['deliveries'] =
            _countApiItems(await deliveriesFuture, alt: 'deliveries');
        result['activeOrders'] =
            _countOrdersByStatus(orderItems, activeOrderStatusNames);
        result['payments'] =
            _countOrdersByStatus(orderItems, escrowHeldOrderStatusNames);
        result['listings'] = _countApiItems(await cropsFuture, alt: 'crops');
      } else if (role == UserRole.trucker) {
        // One trips read (was three status-filtered ones): the status
        // counts come from the page. Deliveries are scoped by truckerId
        // (TruckerIdCreatedAtIndex, SIM-16).
        final tripsFuture = safeGet(
          '/trips',
          queryParameters: {'truckerId': userId, 'limit': '50'},
        );
        final deliveriesFuture = safeGet(
          '/deliveries',
          queryParameters: {'truckerId': userId, 'limit': '50'},
        );
        final trips = _apiItems(await tripsFuture, alt: 'trips');
        result['trips'] = trips.length;
        result['inTransit'] = _countByStatus(trips, 'inTransit');
        result['matched'] = _countByStatus(trips, 'matched');
        result['deliveries'] =
            _countApiItems(await deliveriesFuture, alt: 'deliveries');
      }

      return result;
    } on Object catch (e) {
      debugPrint('[OverviewStats] Web stats failed: $e');
      return {};
    }
  }

  static List<dynamic> _apiItems(Map<String, dynamic> data, {String? alt}) =>
      data[alt] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];

  static int _countApiItems(Map<String, dynamic> data, {String? alt}) =>
      _apiItems(data, alt: alt).length;

  /// Rows whose wire `status` (camelCase for trips) equals [status].
  static int _countByStatus(List<dynamic> items, String status) =>
      items.where((item) => item is Map && item['status'] == status).length;

  /// Fetch the user's orders following nextToken pagination so counts don't
  /// silently truncate at one page. Bounded at 5 pages (250 orders) to cap
  /// dashboard cost on 3G.
  static Future<List<dynamic>> _fetchOrderPages(
    Future<Map<String, dynamic>> Function(
      String path, {
      Map<String, dynamic>? queryParameters,
    }) get,
    Map<String, String> params,
  ) async {
    final items = <dynamic>[];
    String? nextToken;
    for (var page = 0; page < 5; page++) {
      final data = await get(
        '/orders',
        queryParameters: {
          ...params,
          'limit': '50',
          if (nextToken != null) 'nextToken': nextToken,
        },
      );
      final list = data['orders'] ?? data['items'];
      if (list is List) items.addAll(list);
      final token = data['nextToken'];
      if (token is! String || token.isEmpty) break;
      nextToken = token;
    }
    return items;
  }

  /// Count orders whose wire status (snake_case, order_status_wire.dart)
  /// normalizes to one of [statusNames]. Malformed rows are skipped rather
  /// than zeroing the whole stats load.
  static int _countOrdersByStatus(
    List<dynamic> items,
    Set<String> statusNames,
  ) {
    return items.where((item) {
      if (item is! Map) return false;
      final status = item['status'];
      return status is String &&
          statusNames.contains(orderStatusFromWire(status));
    }).length;
  }

  Future<Map<String, int>> _loadIsarStats(
    String userId,
    UserRole role,
  ) async {
    try {
      final isar = IsarService.instance.db;
      final result = <String, int>{};

      if (role == UserRole.farmer) {
        final futures = await Future.wait<int>([
          isar.cropListings.filter().farmerIdEqualTo(userId).count(),
          isar.orders
              .filter()
              .farmerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                escrowHeldOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .count(),
          isar.orders
              .filter()
              .farmerIdEqualTo(userId)
              .statusEqualTo(OrderStatus.funded)
              .count(),
          isar.deliverys.filter().farmerIdEqualTo(userId).count(),
        ]);
        result['listings'] = futures[0];
        result['payments'] = futures[1];
        result['pendingOrders'] = futures[2];
        result['deliveries'] = futures[3];
      } else if (role == UserRole.buyer) {
        final futures = await Future.wait<int>([
          isar.deliverys.filter().buyerIdEqualTo(userId).count(),
          isar.orders
              .filter()
              .buyerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                escrowHeldOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .count(),
          isar.orders
              .filter()
              .buyerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                activeOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .count(),
          isar.cropListings
              .filter()
              .statusEqualTo(ListingStatus.active)
              .count(),
        ]);
        result['deliveries'] = futures[0];
        result['payments'] = futures[1];
        result['activeOrders'] = futures[2];
        result['listings'] = futures[3];
      } else if (role == UserRole.trucker) {
        final futures = await Future.wait<int>([
          isar.trips.filter().truckerIdEqualTo(userId).count(),
          isar.trips
              .filter()
              .truckerIdEqualTo(userId)
              .statusEqualTo(TripStatus.inTransit)
              .count(),
          isar.trips
              .filter()
              .truckerIdEqualTo(userId)
              .statusEqualTo(TripStatus.matched)
              .count(),
          isar.deliverys.filter().truckerIdEqualTo(userId).count(),
        ]);
        result['trips'] = futures[0];
        result['inTransit'] = futures[1];
        result['matched'] = futures[2];
        result['deliveries'] = futures[3];
      }

      return result;
    } on Object catch (e) {
      debugPrint('[OverviewStats] Isar stats failed: $e');
      return {};
    }
  }

  List<Widget> _tilesForRole(UserRole role, Map<String, int> stats) {
    final l10n = widget.l10n;

    Widget tile(
      _HomeDest dest, {
      required IconData icon,
      required String label,
      required Color color,
      String? stat,
      bool emphasized = false,
    }) {
      return _DashboardTile(
        icon: icon,
        label: label,
        color: color,
        count: stat == null ? null : stats[stat] ?? 0,
        emphasized: emphasized,
        onTap: () => _openHomeDest(
          context,
          role: role,
          dest: dest,
          onNavigateToTab: widget.onNavigateToTab,
          onRefresh: widget.onRefresh,
        ),
      );
    }

    switch (role) {
      case UserRole.farmer:
        return [
          tile(
            _HomeDest.sellCrops,
            icon: Icons.agriculture,
            label: l10n.sellCrops,
            color: AppTheme.primaryGreen,
            emphasized: true,
          ),
          tile(
            _HomeDest.myCrops,
            icon: Icons.eco,
            label: l10n.myCrops,
            color: AppTheme.statusTransit,
            stat: 'listings',
          ),
          tile(
            _HomeDest.orders,
            icon: Icons.receipt_long_outlined,
            label: l10n.pendingOrders,
            color: AppTheme.accentGold,
            stat: 'pendingOrders',
          ),
          tile(
            _HomeDest.payments,
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.escrow,
            color: AppTheme.primaryGreen,
            stat: 'payments',
          ),
          tile(
            _HomeDest.deliveries,
            icon: Icons.local_shipping_outlined,
            label: l10n.deliveries,
            color: AppTheme.statusInfo,
            stat: 'deliveries',
          ),
        ];
      case UserRole.trucker:
        return [
          tile(
            _HomeDest.myTrips,
            icon: Icons.route,
            label: l10n.myTrips,
            color: AppTheme.primaryGreen,
            stat: 'trips',
          ),
          tile(
            _HomeDest.myTrips,
            icon: Icons.local_shipping,
            label: l10n.inTransit,
            color: AppTheme.statusTransit,
            stat: 'inTransit',
          ),
          tile(
            _HomeDest.myTrips,
            icon: Icons.handshake_outlined,
            label: l10n.matchedLoads,
            color: AppTheme.accentGold,
            stat: 'matched',
          ),
          tile(
            _HomeDest.deliveries,
            icon: Icons.inventory_2_outlined,
            label: l10n.deliveries,
            color: AppTheme.statusInfo,
            stat: 'deliveries',
          ),
          tile(
            _HomeDest.orders,
            icon: Icons.receipt_long_outlined,
            label: l10n.myOrders,
            color: AppTheme.statusInfo,
          ),
          tile(
            _HomeDest.payments,
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.myPayments,
            color: AppTheme.accentGold,
          ),
        ];
      case UserRole.buyer:
        return [
          tile(
            _HomeDest.findCrops,
            icon: Icons.search,
            label: l10n.findCrops,
            color: AppTheme.primaryGreen,
            stat: 'listings',
            emphasized: true,
          ),
          tile(
            _HomeDest.findTrips,
            icon: Icons.route_outlined,
            label: l10n.findTrips,
            color: AppTheme.statusTransit,
          ),
          tile(
            _HomeDest.orders,
            icon: Icons.receipt_long_outlined,
            label: l10n.activeOrders,
            color: AppTheme.statusInfo,
            stat: 'activeOrders',
          ),
          tile(
            _HomeDest.payments,
            icon: Icons.account_balance_wallet_outlined,
            label: l10n.escrow,
            color: AppTheme.accentGold,
            stat: 'payments',
          ),
          tile(
            _HomeDest.deliveries,
            icon: Icons.inventory_2_outlined,
            label: l10n.deliveries,
            color: AppTheme.primaryGreen,
            stat: 'deliveries',
          ),
        ];
    }
  }
}

/// Recent activity feed.
class _RecentActivityList extends StatefulWidget {
  final UserRole role;
  final String userId;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _RecentActivityList({
    required this.role,
    required this.userId,
    required this.l10n,
    super.key,
    this.onNavigateToTab,
    this.onRefresh,
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
      setState(() {
        _activityFuture = _loadActivity();
      });
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
              .map(
                (item) => _ActivityCard(
                  item: item,
                  l10n: widget.l10n,
                  onTap: () => _openItem(item),
                ),
              )
              .toList(),
        );
      },
    );
  }

  Future<void> _openItem(_ActivityItem item) async {
    final intent = item.intent;
    if (intent != null) {
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
      return;
    }
    final dest = item.dest;
    if (dest == null || !mounted) return;
    await _openHomeDest(
      context,
      role: widget.role,
      dest: dest,
      onNavigateToTab: widget.onNavigateToTab,
      onRefresh: widget.onRefresh,
    );
  }

  Future<List<_ActivityItem>> _loadActivity() async {
    if (kIsWeb) return _loadWebActivity();
    try {
      final isar = IsarService.instance.db;
      final items = <_ActivityItem>[];
      final l10n = widget.l10n;
      final userId = widget.userId;

      // Recent listings — scoped to this farmer. Never fall back to an
      // unscoped query: that would surface OTHER farmers' crops in the feed,
      // violating the role-visibility rule (a farmer must not see others' crops).
      final listings = userId.isEmpty
          ? <CropListing>[]
          : await isar.cropListings
              .where()
              .farmerIdEqualTo(userId)
              .sortByCreatedAtDesc()
              .limit(3)
              .findAll();
      for (final l in listings) {
        items.add(
          _ActivityItem(
            dest: _HomeDest.myCrops,
            description: l10n.listingCreatedActivity(
              CropType.labelFor(l.cropType, l10n),
            ),
            timestamp: l.createdAt,
            type: _ActivityType.info,
          ),
        );
      }

      // Recent deliveries — filter to this user's participation BEFORE the
      // limit. The old code took the 3 globally-newest then filtered, so a
      // device holding any newer non-participant rows would hide this user's
      // own deliveries (the limit-before-filter bug).
      final userDeliveries = userId.isEmpty
          ? <Delivery>[]
          : await isar.deliverys
              .filter()
              .farmerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId)
              .or()
              .truckerIdEqualTo(userId)
              .sortByCreatedAtDesc()
              .limit(3)
              .findAll();
      for (final d in userDeliveries) {
        items.add(
          _ActivityItem(
            dest: _HomeDest.deliveries,
            description: l10n.deliveryStatusActivity(d.status.name),
            timestamp: d.createdAt,
            type: d.status == DeliveryStatus.delivered
                ? _ActivityType.success
                : _ActivityType.pending,
          ),
        );
      }

      // Recent orders (buyer placed / farmer incoming / trucker assigned)
      if (userId.isNotEmpty) {
        final orders = await isar.orders
            .filter()
            .buyerIdEqualTo(userId)
            .or()
            .farmerIdEqualTo(userId)
            .or()
            .truckerIdEqualTo(userId)
            .sortByCreatedAtDesc()
            .limit(3)
            .findAll();
        for (final o in orders) {
          final status = o.status.name;
          items.add(
            _ActivityItem(
              dest: _HomeDest.orders,
              description:
                  '${l10n.orders}: ${CropType.labelFor(o.cropType, l10n)} ($status)',
              timestamp: o.createdAt,
              type: status == 'delivered'
                  ? _ActivityType.success
                  : _ActivityType.pending,
            ),
          );
        }

        // Recent trips (trucker's own; empty for other roles)
        final trips = await isar.trips
            .filter()
            .truckerIdEqualTo(userId)
            .sortByCreatedAtDesc()
            .limit(3)
            .findAll();
        for (final t in trips) {
          final status = t.status.name;
          items.add(
            _ActivityItem(
              dest: _HomeDest.myTrips,
              description:
                  '${l10n.myTrips}: ${t.fromWoreda} → ${t.toWoreda} ($status)',
              timestamp: t.createdAt,
              type: status == 'delivered'
                  ? _ActivityType.success
                  : _ActivityType.info,
            ),
          );
        }
      }

      items.sort((a, b) => b.timestamp.compareTo(a.timestamp));
      return items.take(3).toList();
    } on Object {
      return [];
    }
  }

  Future<List<_ActivityItem>> _loadWebActivity() async {
    try {
      final api = ApiClient();
      final userId = widget.userId;
      if (userId.isEmpty) return [];
      final l10n = widget.l10n;
      final items = <_ActivityItem>[];

      final authState = context.read<AuthBloc>().state;
      final role =
          authState is AuthAuthenticated ? authState.role : UserRole.farmer;

      // Capture BLoC state before async gap to avoid lint warning
      final intentState =
          role == UserRole.buyer ? context.read<BuyerIntentBloc>().state : null;

      // One GET per resource, scoped to the caller's own role and using the
      // same path and params as the Overview stats (and the trucker hero
      // card), so ApiClient folds the concurrent identical reads into one
      // request (SIM-21). Orders used to be fetched twice for every role
      // (buyerId= and farmerId=), and never by truckerId.
      Future<Map<String, dynamic>> safeGet(
        String path,
        Map<String, dynamic> params,
      ) =>
          api
              .get<Map<String, dynamic>>(path, queryParameters: params)
              .catchError((_) => <String, dynamic>{});
      final partyParam = switch (role) {
        UserRole.farmer => 'farmerId',
        UserRole.buyer => 'buyerId',
        UserRole.trucker => 'truckerId',
      };
      final results = await Future.wait([
        safeGet('/orders', {partyParam: userId, 'limit': '50'}),
        // Deliveries: farmerId= (FarmerIdCreatedAtIndex), mine=true (buyer
        // via ParticipantIndex), truckerId= (TruckerIdCreatedAtIndex, SIM-16).
        safeGet('/deliveries', {
          if (role == UserRole.buyer) 'mine': 'true' else partyParam: userId,
          'limit': '50',
        }),
        // Role-specific: trips for truckers, crops for farmers.
        switch (role) {
          UserRole.trucker =>
            safeGet('/trips', {'truckerId': userId, 'limit': '50'}),
          UserRole.farmer =>
            safeGet('/crops', {'farmerId': userId, 'limit': '50'}),
          UserRole.buyer =>
            Future<Map<String, dynamic>>.value(<String, dynamic>{}),
        },
      ]);

      // Parse orders
      for (final resp in [results[0]]) {
        final orders = resp['orders'] as List<dynamic>? ??
            resp['items'] as List<dynamic>? ??
            [];
        for (final o in orders.take(3)) {
          final m = o as Map<String, dynamic>;
          final status = m['status'] as String? ?? 'created';
          final cropType = CropType.labelFor(
            m['cropType'] as String? ?? '',
            l10n,
          );
          items.add(
            _ActivityItem(
              dest: _HomeDest.orders,
              description: '${l10n.orders}: $cropType ($status)',
              timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                  DateTime.now(),
              type: status == 'delivered'
                  ? _ActivityType.success
                  : _ActivityType.pending,
            ),
          );
        }
      }

      // Parse deliveries
      final deliveries = results[1]['deliveries'] as List<dynamic>? ??
          results[1]['items'] as List<dynamic>? ??
          [];
      for (final d in deliveries.take(3)) {
        final m = d as Map<String, dynamic>;
        final status = m['status'] as String? ?? '';
        items.add(
          _ActivityItem(
            dest: _HomeDest.deliveries,
            description: l10n.deliveryStatusActivity(status),
            timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                DateTime.now(),
            type: status == 'delivered'
                ? _ActivityType.success
                : _ActivityType.pending,
          ),
        );
      }

      // Parse role-specific data
      {
        if (role == UserRole.trucker) {
          final trips = results[2]['trips'] as List<dynamic>? ??
              results[2]['items'] as List<dynamic>? ??
              [];
          for (final t in trips.take(3)) {
            final m = t as Map<String, dynamic>;
            final status = m['status'] as String? ?? 'open';
            final from = m['fromWoreda'] as String? ?? '';
            final to = m['toWoreda'] as String? ?? '';
            items.add(
              _ActivityItem(
                dest: _HomeDest.myTrips,
                description: '${l10n.myTrips}: $from → $to ($status)',
                timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                    DateTime.now(),
                type: status == 'delivered'
                    ? _ActivityType.success
                    : _ActivityType.info,
              ),
            );
          }
        } else if (role == UserRole.farmer) {
          final crops = results[2]['crops'] as List<dynamic>? ??
              results[2]['items'] as List<dynamic>? ??
              [];
          for (final c in crops.take(3)) {
            final m = c as Map<String, dynamic>;
            final cropType = CropType.labelFor(
              m['cropType'] as String? ?? '',
              l10n,
            );
            items.add(
              _ActivityItem(
                dest: _HomeDest.myCrops,
                description: l10n.listingCreatedActivity(cropType),
                timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                    DateTime.now(),
                type: _ActivityType.info,
              ),
            );
          }
        }
      }

      // Buyer intents (in-memory on web, not from API)
      if (role == UserRole.buyer) {
        if (intentState is ActiveIntentsLoaded) {
          for (final intent in intentState.intents.take(3)) {
            final cropLabel =
                CropType.fromKey(intent.cropType).displayName(l10n);
            items.add(
              _ActivityItem(
                intent: intent,
                description:
                    '${l10n.activeBuyingIntents}: $cropLabel · ${intent.quantityNeeded.toStringAsFixed(0)} ${l10n.quintals}',
                timestamp: intent.createdAt,
                type: _ActivityType.info,
              ),
            );
          }
        }
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

  /// Screen a tap opens, or null when [intent] is set.
  final _HomeDest? dest;

  /// Buyer intent whose match results a tap opens.
  final BuyerIntent? intent;

  _ActivityItem({
    required this.description,
    required this.timestamp,
    required this.type,
    this.dest,
    this.intent,
  });
}

class _ActivityCard extends StatelessWidget {
  final _ActivityItem item;
  final AppLocalizations l10n;
  final VoidCallback onTap;

  const _ActivityCard({
    required this.item,
    required this.l10n,
    required this.onTap,
  });

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
      child: Material(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(12),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(12),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            decoration: BoxDecoration(
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
                const SizedBox(width: 4),
                const Icon(
                  Icons.chevron_right,
                  size: 18,
                  color: AppTheme.textMuted,
                ),
              ],
            ),
          ),
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
        color: AppTheme.statusError,
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
    if (kIsWeb) return;
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
            subtitle: widget.l10n.syncFailureCount(failedCount),
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
            subtitle: widget.l10n.syncPendingCount(pendingCount),
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
            title: widget.l10n.paymentStatusTitle(p.status.name),
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
    final authState = context.read<AuthBloc>().state;
    final role =
        authState is AuthAuthenticated ? authState.role : UserRole.farmer;
    switch (item.action) {
      case _NotifAction.syncStatus:
        _openSyncStatusSheet();
      case _NotifAction.deliveriesTab:
        final index = roleTabIndex(role, RoleTab.deliveries);
        if (index != null) widget.onNavigateToTab?.call(index);
      case _NotifAction.paymentsTab:
        // Payments is only a trucker tab; other roles push history.
        final index = roleTabIndex(role, RoleTab.payments);
        if (index != null) {
          widget.onNavigateToTab?.call(index);
        } else {
          Navigator.push(
            context,
            MaterialPageRoute<void>(
              builder: (_) => const PaymentHistoryScreen(),
            ),
          );
        }
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
