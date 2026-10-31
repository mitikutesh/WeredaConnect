import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb, visibleForTesting;
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
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
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
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';

/// Home screen: the Farmer and Trucker home boards (specs/design-canvas),
/// the buyer on the farmer's light pattern with buyer content.
///
/// Layout: header (light with the place and the bell; dark for the trucker
/// with the trip card inside it) then Action needed, the farmer's goods on
/// the road, AT A GLANCE tiles, the buyer's active intents and one Recent
/// line. One composition at every width, centred by [FwContentWidth] on a
/// wide window.
///
/// Uses a [_refreshKey] counter to force overview/recent widgets to reload
/// when returning from create screens (FAB or a tile).
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
    final wide = !Breakpoints.isMobile(context);

    final sections = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // ── Action needed: the order waiting for this user (ESC-15) ──
        _buildActionNeeded(role: role, userId: userId),

        // ── Farmer: "Your wheat is on the road" (LT-11) ──────────
        if (role == UserRole.farmer) _buildFarmerOnTheRoad(userId: userId),

        // ── At a glance: counts and amounts, every tile tappable ──
        FwSectionLabel(
          l10n.atAGlance,
          padding: const EdgeInsets.only(top: 16, bottom: 10),
        ),
        _buildDashboard(role: role, userId: userId, l10n: l10n),

        // ── Buyer: active buying intents ────────────────────────
        if (role == UserRole.buyer)
          Padding(
            padding: const EdgeInsets.only(top: 14),
            child: _ActiveIntentsSection(
              key: ValueKey('intents_$_refreshKey'),
              userId: userId,
              l10n: l10n,
              onRefresh: _refreshData,
            ),
          ),

        // ── Recent: the latest event, one line ──────────────────
        Padding(
          padding: const EdgeInsets.only(top: 12),
          child: _buildRecent(role: role, userId: userId, l10n: l10n),
        ),
        const SizedBox(height: Breakpoints.fabClearance),
      ],
    );

    final Widget body;
    if (role == UserRole.trucker) {
      // The Trucker · Home board: a dark header holding the trip card, the
      // chalk page starting under it with rounded top corners. On a wide
      // window the header is a rounded dark panel at the top of the column.
      final header = Container(
        margin: wide ? const EdgeInsets.fromLTRB(20, 16, 20, 0) : null,
        decoration: BoxDecoration(
          color: AppTheme.soilDark,
          borderRadius: wide ? BorderRadius.circular(28) : null,
        ),
        child: _TruckerHomeHeader(
          key: ValueKey('header_$_refreshKey'),
          userName: userName,
          userId: userId,
          isOnline: widget.isOnline,
          l10n: l10n,
          onNavigateToTab: widget.onNavigateToTab,
          onRefresh: _refreshData,
        ),
      );
      body = Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          header,
          if (wide)
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 12, 20, 0),
              child: sections,
            )
          else
            Container(
              color: AppTheme.soilDark,
              child: Container(
                decoration: const BoxDecoration(
                  color: AppTheme.chalkBackground,
                  borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
                ),
                padding: const EdgeInsets.fromLTRB(20, 20, 20, 0),
                child: sections,
              ),
            ),
        ],
      );
    } else {
      body = Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _LightHomeHeader(
            key: ValueKey('header_$_refreshKey'),
            userName: userName,
            userId: userId,
            woreda: woreda,
            isOnline: widget.isOnline,
            l10n: l10n,
            onNavigateToTab: widget.onNavigateToTab,
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: sections,
          ),
        ],
      );
    }

    return SingleChildScrollView(child: FwContentWidth(child: body));
  }

  Widget _buildActionNeeded({
    required UserRole role,
    required String userId,
  }) {
    return ActionNeededList(
      key: ValueKey('action_needed_$_refreshKey'),
      userId: userId,
      role: role,
      onRefresh: _refreshData,
      onShowAll: () => _openHomeDest(
        context,
        role: role,
        dest: _HomeDest.orders,
        onNavigateToTab: widget.onNavigateToTab,
        onRefresh: _refreshData,
      ),
    );
  }

  /// LT-11: the farmer's own deliveries a truck carries now; nothing (not
  /// even the [padding]) when none is on the road.
  Widget _buildFarmerOnTheRoad({
    required String userId,
    EdgeInsets padding = const EdgeInsets.only(top: 12),
  }) {
    return FarmerOnTheRoadSection(
      key: ValueKey('farmer_on_road_$_refreshKey'),
      userId: userId,
      padding: padding,
    );
  }

  Widget _buildDashboard({
    required UserRole role,
    required String userId,
    required AppLocalizations l10n,
  }) {
    return _OverviewStats(
      key: ValueKey('overview_$_refreshKey'),
      role: role,
      userId: userId,
      l10n: l10n,
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
}

/// The Farmer · Home board's header (the buyer's too): the place line, the
/// greeting, the bell in a square, then the Synced chip. Messages and
/// Profile open from the bell's sheet (neither has a bar tab).
class _LightHomeHeader extends StatelessWidget {
  final String userName;
  final String userId;
  final String woreda;
  final bool isOnline;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;

  const _LightHomeHeader({
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
    return SafeArea(
      bottom: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          FwPageHeader(
            padding: const EdgeInsets.fromLTRB(20, 20, 20, 0),
            eyebrowWidget: woreda.isEmpty ? null : _PlaceLine(woreda: woreda),
            title: l10n.welcomeBackUser(userName),
            trailing: NotificationBell(
              userId: userId,
              l10n: l10n,
              isOnline: isOnline,
              square: true,
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 10, 12, 0),
            child: Row(
              children: [
                _SyncChip(isOnline: isOnline),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The Trucker · Home board's dark header: the greeting, "Today" with the
/// Synced chip, and the trip card ([TruckerHeroCard]). The board draws no
/// icons here; the bell (with Messages and Profile in its sheet) is on the
/// trucker's other tabs.
class _TruckerHomeHeader extends StatelessWidget {
  final String userName;
  final String userId;
  final bool isOnline;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _TruckerHomeHeader({
    required this.userName,
    required this.userId,
    required this.isOnline,
    required this.l10n,
    this.onNavigateToTab,
    this.onRefresh,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      bottom: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 22),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.welcomeBackUser(userName),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 13,
                      color: AppTheme.soilMuted,
                    ),
                  ),
                ),
              ],
            ),
            Row(
              children: [
                Expanded(
                  child: Semantics(
                    header: true,
                    child: Text(
                      l10n.homeToday,
                      style: const TextStyle(
                        fontSize: 26,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.6,
                        height: 1.15,
                        color: Colors.white,
                      ),
                    ),
                  ),
                ),
                _SyncChip(isOnline: isOnline, dark: true),
              ],
            ),
            const SizedBox(height: 16),
            TruckerHeroCard(
              key: ValueKey('hero_$userId'),
              userId: userId,
              onRefresh: onRefresh,
            ),
          ],
        ),
      ),
    );
  }
}

/// The header's place line, "Meki · Oromia": the user's woreda with its
/// region from the bundled place list; the woreda alone when it is not in
/// the list.
class _PlaceLine extends StatefulWidget {
  final String woreda;

  const _PlaceLine({required this.woreda});

  @override
  State<_PlaceLine> createState() => _PlaceLineState();
}

class _PlaceLineState extends State<_PlaceLine> {
  late final Future<List<Place>> _places = loadPlaces();

  @override
  Widget build(BuildContext context) {
    final languageCode = Localizations.localeOf(context).languageCode;
    return FutureBuilder<List<Place>>(
      future: _places,
      builder: (context, snapshot) => Text(
        homePlaceLine(widget.woreda, snapshot.data ?? const [], languageCode),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: FwText.body,
      ),
    );
  }
}

/// "Meki · Oromia" for [woreda] when [places] knows it and its region, else
/// [woreda] as stored. A city that is its own region ("Dire Dawa") shows
/// once. Names compare through [normalizeWoreda], the rule every woreda
/// comparison uses.
@visibleForTesting
String homePlaceLine(String woreda, List<Place> places, String languageCode) {
  final key = normalizeWoreda(woreda);
  if (key.isEmpty) return woreda;
  for (final place in places) {
    final known = normalizeWoreda(place.displayEn) == key ||
        normalizeWoreda(place.en) == key ||
        place.alts.any((alt) => normalizeWoreda(alt) == key);
    if (!known) continue;
    final name = place.displayName(languageCode);
    if (place.region.isEmpty || normalizeWoreda(place.region) == key) {
      return name;
    }
    return '$name · ${place.region}';
  }
  return woreda;
}

/// The boards' Synced chip: a check on a green tint (soil ground with
/// pale green on the trucker's dark header); Offline on grey.
class _SyncChip extends StatelessWidget {
  final bool isOnline;
  final bool dark;

  const _SyncChip({required this.isOnline, this.dark = false});

  /// On a phone the chip opens what waits to sync (the queue lives there).
  void _showSyncStatus(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final executor = context.read<SyncExecutor>();
    showModalBottomSheet<void>(
      context: context,
      // Over the shell's bar and create button, not under them.
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) => SyncStatusSheet(
        isOnline: isOnline,
        l10n: l10n,
        executor: executor,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final label = isOnline ? l10n.synced : l10n.offline;
    final (background, foreground) = dark
        ? isOnline
            ? (AppTheme.soilCard, AppTheme.soilGreen)
            : (AppTheme.soilTrack, AppTheme.soilMuted)
        : isOnline
            ? (AppTheme.fieldGreenTint, AppTheme.fieldGreen)
            : (AppTheme.segmentTrack, AppTheme.mutedSteel);

    final chip = Container(
      height: 28,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            isOnline ? Icons.check : Icons.cloud_off_outlined,
            size: 13,
            color: foreground,
          ),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w700,
              color: foreground,
            ),
          ),
        ],
      ),
    );
    if (kIsWeb) return chip;
    return Semantics(
      button: true,
      child: InkWell(
        key: const Key('home_sync_chip'),
        borderRadius: BorderRadius.circular(999),
        onTap: () => _showSyncStatus(context),
        child: chip,
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
      // My Trips is a trucker tab; a buyer (ALL trips) gets a pushed screen.
      await tabOrPush(RoleTab.myTrips, const BrowseTripsScreen());
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

/// One At a Glance tile (the Farmer and Trucker home boards): the label
/// over a count or an amount, and on the 2 by 2 grid a line under it. The
/// role's lead tile is filled field green. Every tile opens its section; a
/// tile without a [value] is a plain shortcut with its [icon].
class _GlanceTile extends StatelessWidget {
  final String label;
  final String? value;

  /// [value] is an amount ("4,500 ETB"): set smaller than a count.
  final bool amount;
  final String? sub;
  final IconData? icon;
  final bool emphasized;

  /// The trucker's three-across row: tighter padding, radius 16, no sub.
  final bool compact;
  final VoidCallback onTap;

  const _GlanceTile({
    required this.label,
    required this.onTap,
    this.value,
    this.amount = false,
    this.sub,
    this.icon,
    this.emphasized = false,
    this.compact = false,
  });

  @override
  Widget build(BuildContext context) {
    final ink = emphasized ? Colors.white : AppTheme.soilDark;
    final muted = emphasized ? const Color(0xFFCFE3D6) : AppTheme.mutedSteel;
    final value = this.value;
    final sub = this.sub;
    final valueSize =
        amount ? (compact ? 16.0 : 20.0) : (compact ? 22.0 : 26.0);

    return Semantics(
      button: true,
      child: Material(
        color: emphasized ? AppTheme.fieldGreen : AppTheme.surfaceWhite,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(compact ? 16 : 18),
          side: emphasized
              ? BorderSide.none
              : const BorderSide(color: AppTheme.whisperBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Padding(
            padding: EdgeInsets.all(compact ? 12 : 14),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                    color: muted,
                  ),
                ),
                if (value != null)
                  Padding(
                    padding: EdgeInsets.only(
                      top: amount ? (compact ? 6 : 4) : 2,
                      bottom: amount && !compact ? 3 : 0,
                    ),
                    child: FittedBox(
                      fit: BoxFit.scaleDown,
                      alignment: AlignmentDirectional.centerStart,
                      child: Text(
                        value,
                        maxLines: 1,
                        style: TextStyle(
                          fontSize: valueSize,
                          fontWeight: FontWeight.w800,
                          letterSpacing: -0.5,
                          height: 1.2,
                          color: ink,
                        ),
                      ),
                    ),
                  )
                else
                  Padding(
                    padding: const EdgeInsets.only(top: 6, bottom: 4),
                    child:
                        Icon(icon ?? Icons.arrow_forward, size: 26, color: ink),
                  ),
                if (!compact && sub != null)
                  Text(
                    sub,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w500,
                      color: muted,
                    ),
                  ),
              ],
            ),
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
  final double gap;

  const _DashboardGrid({
    required this.tiles,
    required this.columns,
    this.gap = 10,
  });

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
            padding: EdgeInsets.only(top: r == 0 ? 0 : gap),
            child: IntrinsicHeight(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  for (var c = 0; c < rows[r].length; c++) ...[
                    if (c > 0) SizedBox(width: gap),
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

/// What the At a Glance tiles show for a role: counts, and the money held in
/// escrow for this user (the farmer's crop share, the trucker's transport
/// pay, the buyer's full payments), summed on the device from the orders.
class _HomeStats {
  final int listings;
  final int activeListings;
  final int reservedListings;
  final int pendingOrders;
  final int activeOrders;

  /// Deliveries on the road now (picked up or in transit).
  final int inTransit;
  final int trips;
  final int matched;
  final double escrowEtb;

  const _HomeStats({
    this.listings = 0,
    this.activeListings = 0,
    this.reservedListings = 0,
    this.pendingOrders = 0,
    this.activeOrders = 0,
    this.inTransit = 0,
    this.trips = 0,
    this.matched = 0,
    this.escrowEtb = 0,
  });
}

/// "At a glance": the role's tiles in one grid (three across for the
/// trucker, two by two otherwise), every tile opening the screen it
/// summarises.
class _OverviewStats extends StatefulWidget {
  final UserRole role;
  final String userId;
  final AppLocalizations l10n;
  final ValueChanged<int>? onNavigateToTab;
  final VoidCallback? onRefresh;

  const _OverviewStats({
    required this.role,
    required this.userId,
    required this.l10n,
    super.key,
    this.onNavigateToTab,
    this.onRefresh,
  });

  @override
  State<_OverviewStats> createState() => _OverviewStatsState();
}

class _OverviewStatsState extends State<_OverviewStats> {
  late Future<_HomeStats> _statsFuture;

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
    final compact = widget.role == UserRole.trucker;
    return FutureBuilder<_HomeStats>(
      future: _statsFuture,
      builder: (context, snapshot) {
        final stats = snapshot.data ?? const _HomeStats();
        return _DashboardGrid(
          tiles: _tilesForRole(widget.role, stats),
          columns: compact ? 3 : 2,
          gap: compact ? 8 : 10,
        );
      },
    );
  }

  Future<_HomeStats> _loadStats(String userId, UserRole role) async {
    if (userId.isEmpty) return const _HomeStats();
    if (kIsWeb) return _loadWebStats(userId, role);
    return _loadIsarStats(userId, role);
  }

  Future<_HomeStats> _loadWebStats(String userId, UserRole role) async {
    try {
      final api = ApiClient();
      if (!api.isInitialized) {
        debugPrint('[OverviewStats] ApiClient not initialized');
        return const _HomeStats();
      }

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
      // line and the Action needed card use, so ApiClient folds the
      // concurrent identical reads into a single request (SIM-21).
      switch (role) {
        case UserRole.farmer:
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
          final crops = _apiItems(await cropsFuture, alt: 'crops');
          return _HomeStats(
            listings: crops.length,
            // The API says "available" for an active listing.
            activeListings: _countByStatus(crops, 'available') +
                _countByStatus(crops, 'active'),
            reservedListings: _countByStatus(crops, 'reserved'),
            pendingOrders: _countOrdersByStatus(orderItems, const {'funded'}),
            escrowEtb: _sumOrders(
              orderItems,
              escrowHeldOrderStatusNames,
              (o) => farmerEscrowShare(
                subtotal:
                    wireDouble(o['quantity']) * wireDouble(o['pricePerUnit']),
                cropCost: wireDouble(o['cropCost']),
              ),
            ),
            inTransit: _countOnTheRoad(
              _apiItems(await deliveriesFuture, alt: 'deliveries'),
            ),
          );
        case UserRole.buyer:
          // The server scopes a buyer via mine=true (ParticipantIndex); a
          // buyerId= filter is not a delivery query parameter.
          final deliveriesFuture = safeGet(
            '/deliveries',
            queryParameters: {'mine': 'true', 'limit': '50'},
          );
          final cropsFuture =
              safeGet('/crops', queryParameters: {'status': 'available'});
          final orderItems =
              await _fetchOrderPages(safeGet, {'buyerId': userId});
          return _HomeStats(
            listings: _countApiItems(await cropsFuture, alt: 'crops'),
            activeOrders:
                _countOrdersByStatus(orderItems, activeOrderStatusNames),
            escrowEtb: _sumOrders(
              orderItems,
              escrowHeldOrderStatusNames,
              (o) => orderEscrowValue(
                subtotal:
                    wireDouble(o['quantity']) * wireDouble(o['pricePerUnit']),
                orderTotal: wireDouble(o['orderTotal']),
              ),
            ),
            inTransit: _countOnTheRoad(
              _apiItems(await deliveriesFuture, alt: 'deliveries'),
            ),
          );
        case UserRole.trucker:
          // One trips read: the status counts come from the page. The
          // escrow tile sums the transport pay of the orders assigned to
          // this trucker (truckerId=, the Action needed card's read).
          final tripsFuture = safeGet(
            '/trips',
            queryParameters: {'truckerId': userId, 'limit': '50'},
          );
          final orderItems =
              await _fetchOrderPages(safeGet, {'truckerId': userId});
          final trips = _apiItems(await tripsFuture, alt: 'trips');
          return _HomeStats(
            trips: trips.length,
            matched: _countByStatus(trips, 'matched'),
            escrowEtb: _sumOrders(
              orderItems,
              escrowHeldOrderStatusNames,
              (o) =>
                  o['truckerId'] == userId ? wireDouble(o['transportCost']) : 0,
            ),
          );
      }
    } on Object catch (e) {
      debugPrint('[OverviewStats] Web stats failed: $e');
      return const _HomeStats();
    }
  }

  static List<dynamic> _apiItems(Map<String, dynamic> data, {String? alt}) =>
      data[alt] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];

  static int _countApiItems(Map<String, dynamic> data, {String? alt}) =>
      _apiItems(data, alt: alt).length;

  /// Rows whose wire `status` (camelCase for trips) equals [status].
  static int _countByStatus(List<dynamic> items, String status) =>
      items.where((item) => item is Map && item['status'] == status).length;

  /// Deliveries on the road now (camelCase statuses on the wire).
  static int _countOnTheRoad(List<dynamic> items) =>
      _countByStatus(items, 'pickedUp') + _countByStatus(items, 'inTransit');

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

  /// The order rows whose wire status (snake_case, order_status_wire.dart)
  /// normalizes to one of [statusNames]. Malformed rows are skipped rather
  /// than zeroing the whole stats load.
  static Iterable<Map<dynamic, dynamic>> _ordersIn(
    List<dynamic> items,
    Set<String> statusNames,
  ) sync* {
    for (final item in items) {
      if (item is! Map) continue;
      final status = item['status'];
      if (status is String &&
          statusNames.contains(orderStatusFromWire(status))) {
        yield item;
      }
    }
  }

  static int _countOrdersByStatus(
    List<dynamic> items,
    Set<String> statusNames,
  ) =>
      _ordersIn(items, statusNames).length;

  /// ETB over the order rows in [statusNames], [value] of each.
  static double _sumOrders(
    List<dynamic> items,
    Set<String> statusNames,
    double Function(Map<dynamic, dynamic> order) value,
  ) =>
      _ordersIn(items, statusNames).fold(0, (sum, o) => sum + value(o));

  Future<_HomeStats> _loadIsarStats(String userId, UserRole role) async {
    try {
      final isar = IsarService.instance.db;
      const onTheRoad = [DeliveryStatus.pickedUp, DeliveryStatus.inTransit];

      switch (role) {
        case UserRole.farmer:
          final listings = await isar.cropListings
              .filter()
              .farmerIdEqualTo(userId)
              .findAll();
          final held = await isar.orders
              .filter()
              .farmerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                escrowHeldOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .findAll();
          final pending = await isar.orders
              .filter()
              .farmerIdEqualTo(userId)
              .statusEqualTo(OrderStatus.funded)
              .count();
          final inTransit = await isar.deliverys
              .filter()
              .farmerIdEqualTo(userId)
              .anyOf<DeliveryStatus, QAfterFilterCondition>(
                onTheRoad,
                (q, s) => q.statusEqualTo(s),
              )
              .count();
          return _HomeStats(
            listings: listings.length,
            activeListings:
                listings.where((l) => l.status == ListingStatus.active).length,
            reservedListings: listings
                .where((l) => l.status == ListingStatus.reserved)
                .length,
            pendingOrders: pending,
            escrowEtb: held.fold(
              0,
              (sum, o) =>
                  sum +
                  farmerEscrowShare(
                    subtotal: o.quantity * o.pricePerUnit,
                    cropCost: o.cropCost,
                  ),
            ),
            inTransit: inTransit,
          );
        case UserRole.buyer:
          final held = await isar.orders
              .filter()
              .buyerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                escrowHeldOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .findAll();
          final active = await isar.orders
              .filter()
              .buyerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                activeOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .count();
          final listings = await isar.cropListings
              .filter()
              .statusEqualTo(ListingStatus.active)
              .count();
          final inTransit = await isar.deliverys
              .filter()
              .buyerIdEqualTo(userId)
              .anyOf<DeliveryStatus, QAfterFilterCondition>(
                onTheRoad,
                (q, s) => q.statusEqualTo(s),
              )
              .count();
          return _HomeStats(
            listings: listings,
            activeOrders: active,
            escrowEtb: held.fold(
              0,
              (sum, o) =>
                  sum +
                  orderEscrowValue(
                    subtotal: o.quantity * o.pricePerUnit,
                    orderTotal: o.orderTotal,
                  ),
            ),
            inTransit: inTransit,
          );
        case UserRole.trucker:
          final trips =
              await isar.trips.filter().truckerIdEqualTo(userId).count();
          final matched = await isar.trips
              .filter()
              .truckerIdEqualTo(userId)
              .statusEqualTo(TripStatus.matched)
              .count();
          final held = await isar.orders
              .filter()
              .truckerIdEqualTo(userId)
              .anyOf<OrderStatus, QAfterFilterCondition>(
                escrowHeldOrderStatuses,
                (q, s) => q.statusEqualTo(s),
              )
              .findAll();
          return _HomeStats(
            trips: trips,
            matched: matched,
            escrowEtb: held.fold(0, (sum, o) => sum + (o.transportCost ?? 0)),
          );
      }
    } on Object catch (e) {
      debugPrint('[OverviewStats] Isar stats failed: $e');
      return const _HomeStats();
    }
  }

  List<Widget> _tilesForRole(UserRole role, _HomeStats stats) {
    final l10n = widget.l10n;
    final etb = l10n.etbCurrency(birrText(stats.escrowEtb));

    Widget tile(
      _HomeDest dest, {
      required String label,
      String? value,
      bool amount = false,
      String? sub,
      IconData? icon,
      bool emphasized = false,
      bool compact = false,
    }) {
      return _GlanceTile(
        label: label,
        value: value,
        amount: amount,
        sub: sub,
        icon: icon,
        emphasized: emphasized,
        compact: compact,
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
        // Farmer · Home board: My Crops leads, filled; Sell Crops is the
        // floating button.
        return [
          tile(
            _HomeDest.myCrops,
            label: l10n.myCrops,
            value: '${stats.listings}',
            sub: l10n.homeTileActiveReserved(
              stats.activeListings,
              stats.reservedListings,
            ),
            emphasized: true,
          ),
          tile(
            _HomeDest.orders,
            label: l10n.pendingOrders,
            value: '${stats.pendingOrders}',
            sub: l10n.homeTileWaitingForYou,
          ),
          tile(
            _HomeDest.payments,
            label: l10n.escrow,
            value: etb,
            amount: true,
            sub: l10n.homeTileHeldForYou,
          ),
          tile(
            _HomeDest.deliveries,
            label: l10n.deliveries,
            value: '${stats.inTransit}',
            sub: l10n.homeTileInTransit,
          ),
        ];
      case UserRole.trucker:
        // Trucker · Home board: three across, the Escrow tile is the
        // trucker's transport pay held for their orders.
        return [
          tile(
            _HomeDest.myTrips,
            label: l10n.myTrips,
            value: '${stats.trips}',
            compact: true,
          ),
          tile(
            _HomeDest.myTrips,
            label: l10n.matchedLoads,
            value: '${stats.matched}',
            compact: true,
          ),
          tile(
            _HomeDest.payments,
            label: l10n.escrow,
            value: etb,
            amount: true,
            compact: true,
          ),
        ];
      case UserRole.buyer:
        // No buyer board: the farmer's grid with buyer content. Find Crops
        // leads (the full row); buyers browse ALL crops and ALL trips.
        return [
          tile(
            _HomeDest.findCrops,
            label: l10n.findCrops,
            value: '${stats.listings}',
            sub: l10n.homeTileAvailableNow,
            emphasized: true,
          ),
          tile(
            _HomeDest.findTrips,
            label: l10n.findTrips,
            icon: Icons.route_outlined,
            sub: l10n.homeTileBrowseAllTrips,
          ),
          tile(
            _HomeDest.orders,
            label: l10n.activeOrders,
            value: '${stats.activeOrders}',
            sub: l10n.homeTileInProgress,
          ),
          tile(
            _HomeDest.payments,
            label: l10n.escrow,
            value: etb,
            amount: true,
            sub: l10n.homeTileHeldInEscrow,
          ),
          tile(
            _HomeDest.deliveries,
            label: l10n.deliveries,
            value: '${stats.inTransit}',
            sub: l10n.homeTileInTransit,
          ),
        ];
    }
  }
}

/// Recent: the latest event of the user's listings, orders, deliveries
/// and trips as one line (the Trucker · Home board's "Recent: pickup
/// scanned at Koka 09:40"); nothing when there is none yet.
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
        if (items.isEmpty) return const SizedBox.shrink();
        final item = items.first;
        return _RecentLineCard(
          item: item,
          l10n: widget.l10n,
          onTap: () => _openItem(item),
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
            description: l10n.deliveryStatusActivity(
              deliveryStatusLabel(d.status, l10n),
            ),
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
          items.add(
            _ActivityItem(
              dest: _HomeDest.orders,
              description: orderActivityDescription(
                l10n,
                CropType.labelFor(o.cropType, l10n),
                o.status,
              ),
              timestamp: o.createdAt,
              type: isDeliveredOrderActivity(o.status)
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
          items.add(
            _ActivityItem(
              dest: _HomeDest.myTrips,
              description: tripActivityDescription(
                l10n,
                t.fromWoreda,
                t.toWoreda,
                t.status,
              ),
              timestamp: t.createdAt,
              type: t.status == TripStatus.delivered
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
          final status = orderStatusFromApi(m['status'] as String?);
          final cropType = CropType.labelFor(
            m['cropType'] as String? ?? '',
            l10n,
          );
          items.add(
            _ActivityItem(
              dest: _HomeDest.orders,
              description: orderActivityDescription(l10n, cropType, status),
              timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                  DateTime.now(),
              type: isDeliveredOrderActivity(status)
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
        final status = deliveryStatusFromApi(m['status'] as String?);
        items.add(
          _ActivityItem(
            dest: _HomeDest.deliveries,
            description: l10n.deliveryStatusActivity(
              deliveryStatusLabel(status, l10n),
            ),
            timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                DateTime.now(),
            type: status == DeliveryStatus.delivered
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
            final status = tripStatusFromApi(m['status'] as String?);
            final from = m['fromWoreda'] as String? ?? '';
            final to = m['toWoreda'] as String? ?? '';
            items.add(
              _ActivityItem(
                dest: _HomeDest.myTrips,
                description: tripActivityDescription(l10n, from, to, status),
                timestamp: DateTime.tryParse(m['createdAt'] as String? ?? '') ??
                    DateTime.now(),
                type: status == TripStatus.delivered
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

/// Parses an order status from an API response. The server sends order
/// statuses in snake_case (`payment_submitted`), so the token goes through
/// [orderStatusFromWire] first; dropping that step makes every multi-word
/// status read as [OrderStatus.unknown]. A status this build does not know
/// is [OrderStatus.unknown] too.
@visibleForTesting
OrderStatus orderStatusFromApi(String? raw) {
  final name = raw == null ? null : orderStatusFromWire(raw);
  return OrderStatus.values.firstWhere(
    (e) => e.name == name,
    orElse: () => OrderStatus.unknown,
  );
}

/// Parses a camelCase trip status from an API response, falling back to
/// [TripStatus.open] for a missing or unknown value.
@visibleForTesting
TripStatus tripStatusFromApi(String? raw) {
  return TripStatus.values.firstWhere(
    (e) => e.name == raw,
    orElse: () => TripStatus.open,
  );
}

/// Parses a camelCase delivery status from an API response, falling back to
/// [DeliveryStatus.pending] for a missing or unknown value.
@visibleForTesting
DeliveryStatus deliveryStatusFromApi(String? raw) {
  return DeliveryStatus.values.firstWhere(
    (e) => e.name == raw,
    orElse: () => DeliveryStatus.pending,
  );
}

/// Whether a recent-activity order line shows the success mark: the goods
/// arrived. ESC-09: after delivered the order follows its money to disbursing
/// and settled, which are done trades too.
@visibleForTesting
bool isDeliveredOrderActivity(OrderStatus status) =>
    deliveredOrderStatuses.contains(status);

/// Recent-activity line for an order, with the status in the user's language.
@visibleForTesting
String orderActivityDescription(
  AppLocalizations l10n,
  String cropLabel,
  OrderStatus status,
) =>
    '${l10n.orders}: $cropLabel (${orderStatusLabel(status, l10n)})';

/// Recent-activity title for a payment, with the status in the user's
/// language (it showed the enum name, such as "held payment").
@visibleForTesting
String paymentActivityTitle(AppLocalizations l10n, PaymentStatus status) =>
    l10n.paymentStatusTitle(escrowStatusLabel(status, l10n));

/// Recent-activity line for a trip, with the status in the user's language.
@visibleForTesting
String tripActivityDescription(
  AppLocalizations l10n,
  String from,
  String to,
  TripStatus status,
) =>
    '${l10n.myTrips}: $from → $to (${tripStatusLabel(status, l10n)})';

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

/// The one-line Recent card: a mark for the event's kind, "Recent: ..." and
/// how long ago.
class _RecentLineCard extends StatelessWidget {
  final _ActivityItem item;
  final AppLocalizations l10n;
  final VoidCallback onTap;

  const _RecentLineCard({
    required this.item,
    required this.l10n,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final (icon, color) = switch (item.type) {
      _ActivityType.info => (Icons.info_outline, AppTheme.dropoffBlue),
      _ActivityType.success => (Icons.check, AppTheme.fieldGreen),
      _ActivityType.pending => (Icons.schedule, AppTheme.statusTransit),
    };

    final timeDiff = DateTime.now().difference(item.timestamp);
    final timeLabel = timeDiff.inMinutes < 60
        ? l10n.minutesAgo(timeDiff.inMinutes)
        : timeDiff.inHours < 24
            ? l10n.hoursAgo(timeDiff.inHours)
            : l10n.daysAgo(timeDiff.inDays);

    return Semantics(
      button: true,
      child: Material(
        color: AppTheme.surfaceWhite,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: AppTheme.whisperBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          key: const Key('home_recent_line'),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            child: Row(
              children: [
                Icon(icon, size: 18, color: color),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    l10n.homeRecentLine(item.description),
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.unselectedNav,
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
                    color: AppTheme.mutedSteel,
                  ),
                ),
                const SizedBox(width: 2),
                const Icon(
                  Icons.chevron_right,
                  size: 18,
                  color: AppTheme.mutedSteel,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
