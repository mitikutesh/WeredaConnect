import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/foundation.dart'
    show SynchronousFuture, kIsWeb, visibleForTesting;
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
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_queue_web.dart';
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
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/home_cards.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Home screen: the Farmer and Trucker home boards (specs/design-canvas,
/// page M), the buyer on the farmer's pattern with buyer content (the
/// shells no longer show Home to a buyer, who lands on Deliveries).
///
/// Layout, white ground for every role: the header ([_HomeHeader]), the
/// trucker's trip card, Action needed, the farmer's goods on the road, AT A
/// GLANCE tiles, the buyer's active intents, one Recent line and the
/// trucker's Payments row. One composition at every width, centred by
/// [FwContentWidth] on a wide window.
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

    final trucker = role == UserRole.trucker;
    final sections = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // ── Trucker: today's trip, the card the page leads with ──
        if (trucker)
          Padding(
            padding: const EdgeInsets.only(bottom: 18),
            child: TruckerHeroCard(
              key: ValueKey('hero_${userId}_$_refreshKey'),
              userId: userId,
              onRefresh: _refreshData,
            ),
          ),

        // ── Action needed: the order waiting for this user (ESC-15) ──
        _buildActionNeeded(role: role, userId: userId),

        // ── Farmer: "Your wheat is on the road" (LT-11) ──────────
        if (role == UserRole.farmer) _buildFarmerOnTheRoad(userId: userId),

        // ── At a glance: counts and amounts, every tile tappable ──
        Padding(
          padding: const EdgeInsets.only(top: 18, bottom: 10),
          child: Row(
            children: [
              const Icon(
                Icons.grid_view_rounded,
                size: 16,
                color: AppTheme.slate,
              ),
              const SizedBox(width: 8),
              Expanded(child: FwSectionLabel(l10n.atAGlance)),
            ],
          ),
        ),
        _buildDashboard(
          role: role,
          userId: userId,
          l10n: l10n,
          // Under the tiles: the buyer's active intents, then the latest
          // event as one line. The trucker's Payments row follows them.
          afterTiles: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
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
              Padding(
                padding: const EdgeInsets.only(top: 10),
                child: _buildRecent(role: role, userId: userId, l10n: l10n),
              ),
            ],
          ),
        ),
        const SizedBox(height: Breakpoints.fabClearance),
      ],
    );

    // White ground for every role (design canvas page M): the header, then
    // the sections in one column.
    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _HomeHeader(
          key: ValueKey('header_$_refreshKey'),
          role: role,
          userName: userName,
          userId: userId,
          woreda: woreda,
          isOnline: widget.isOnline,
          l10n: l10n,
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 18, 20, 0),
          child: sections,
        ),
      ],
    );

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
    Widget? afterTiles,
  }) {
    return _OverviewStats(
      key: ValueKey('overview_$_refreshKey'),
      role: role,
      userId: userId,
      l10n: l10n,
      afterTiles: afterTiles,
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

/// The first word of [userName], for the Farmer home board's large
/// greeting ("Almaz" of "Almaz Bekele"); empty when there is no name.
@visibleForTesting
String homeFirstName(String userName) {
  final name = userName.trim();
  return name.isEmpty ? '' : name.split(RegExp(r'\s+')).first;
}

/// The Home boards' header on the white ground (design canvas page M).
///
/// Farmer (and the buyer, who has no Home board): the place line and the
/// Synced pill, the bell and the account button; "Welcome back," small
/// over the person's first name at 42 px, the role's sketch beside it.
///
/// Trucker: "Welcome back, Dawit" small with the Synced pill and the
/// account button, then "Today" at 42 px beside the truck sketch. The
/// board draws no bell here (it is on the trucker's other tabs).
///
/// The account button opens what has no bar tab (Deliveries, Messages and
/// Payments for the farmer), Profile and Settings.
class _HomeHeader extends StatelessWidget {
  final UserRole role;
  final String userName;
  final String userId;
  final String woreda;
  final bool isOnline;
  final AppLocalizations l10n;

  const _HomeHeader({
    required this.role,
    required this.userName,
    required this.userId,
    required this.woreda,
    required this.isOnline,
    required this.l10n,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final trucker = role == UserRole.trucker;
    final firstName = homeFirstName(userName);
    // The line the header opens with: where the farmer is, or the
    // trucker's greeting.
    final Widget? lead = trucker
        ? Text(
            firstName.isEmpty
                ? l10n.homeWelcomeBack
                : l10n.welcomeBackUser(firstName),
            style: FwText.body,
          )
        : (woreda.isEmpty ? null : _PlaceLine(woreda: woreda));
    final chip = _SyncChip(isOnline: isOnline);
    return SafeArea(
      bottom: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: Row(
              children: [
                if (trucker) ...[
                  // The greeting, then the pill at the end by the account
                  // button. With large text the pill wraps, never overflows.
                  Expanded(child: lead ?? const SizedBox.shrink()),
                  const SizedBox(width: 10),
                  Flexible(child: chip),
                  if (!SidebarScope.of(context)) ...[
                    const SizedBox(width: 10),
                    const AccountButton(),
                  ],
                ] else ...[
                  // The place and the pill share what the buttons leave;
                  // with large text they wrap onto two lines.
                  Expanded(
                    child: Wrap(
                      spacing: 10,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [if (lead != null) lead, chip],
                    ),
                  ),
                  const SizedBox(width: 12),
                  HeaderActions(
                    bell: NotificationBell(
                      userId: userId,
                      l10n: l10n,
                      isOnline: isOnline,
                      square: true,
                    ),
                  ),
                ],
              ],
            ),
          ),
          FwPageHeader(
            padding: const EdgeInsets.fromLTRB(20, 2, 20, 0),
            eyebrow: trucker || firstName.isEmpty ? null : l10n.homeWelcomeBack,
            // Without a name the greeting itself is the title.
            title: trucker
                ? l10n.homeToday
                : (firstName.isEmpty
                    ? l10n.welcomeBackUser(userName)
                    : firstName),
            sketchRole: role,
          ),
        ],
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

/// The boards' Synced pill: a check on the role's tint; Offline on grey.
///
/// E-5: "Synced" only when online with nothing waiting. On a phone it
/// follows the sync queue through an Isar watch: "N waiting to sync" while
/// changes wait (offline too), "Sync Failed" while some failed, Offline
/// when there is no network. Web has no queue (writes go straight to the
/// API): the chip follows the browser's network.
class _SyncChip extends StatefulWidget {
  final bool isOnline;

  const _SyncChip({required this.isOnline});

  @override
  State<_SyncChip> createState() => _SyncChipState();
}

class _SyncChipState extends State<_SyncChip> {
  StreamSubscription<({int pending, int failed})>? _countsSub;
  StreamSubscription<List<ConnectivityResult>>? _webSub;
  ({int pending, int failed}) _counts = (pending: 0, failed: 0);

  /// The browser's network on web; null until it answered.
  bool? _webOnline;

  @override
  void initState() {
    super.initState();
    if (kIsWeb) {
      final connectivity = Connectivity();
      connectivity.checkConnectivity().then(_onWebConnectivity).ignore();
      _webSub = connectivity.onConnectivityChanged.listen(_onWebConnectivity);
      return;
    }
    SyncQueue? queue;
    try {
      queue = context.read<SyncQueue>();
    } on Object {
      // No queue above this widget: nothing is counted as waiting.
      queue = null;
    }
    _countsSub = queue?.watchCounts().listen(
      (counts) {
        if (mounted) setState(() => _counts = counts);
      },
      onError: (Object _) {},
    );
  }

  void _onWebConnectivity(List<ConnectivityResult> results) {
    final online = results.any((r) => r != ConnectivityResult.none);
    if (mounted) setState(() => _webOnline = online);
  }

  @override
  void dispose() {
    _countsSub?.cancel();
    _webSub?.cancel();
    super.dispose();
  }

  bool get _online =>
      kIsWeb ? (_webOnline ?? widget.isOnline) : widget.isOnline;

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
        isOnline: _online,
        l10n: l10n,
        executor: executor,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final isOnline = _online;
    final pending = _counts.pending;
    final failed = _counts.failed;
    final (String label, IconData icon) = pending > 0
        ? (
            l10n.syncPendingCount(pending),
            isOnline ? Icons.cloud_upload_outlined : Icons.cloud_off_outlined,
          )
        : !isOnline
            ? (l10n.offline, Icons.cloud_off_outlined)
            : failed > 0
                ? (l10n.syncFailed, Icons.sync_problem)
                : (l10n.synced, Icons.check);
    final (background, foreground) = pending > 0
        ? (AppTheme.orangeTint, AppTheme.orangeInk)
        : !isOnline
            ? (AppTheme.segmentTrack, AppTheme.inkSoft)
            : failed > 0
                ? (
                    AppTheme.statusErrorContainer,
                    AppTheme.onStatusErrorContainer
                  )
                : (context.roleColors.tint, context.roleColors.primary);

    // At least 26 tall, taller when the text is scaled up.
    final chip = Container(
      constraints: const BoxConstraints(minHeight: 26),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 12, color: foreground),
          const SizedBox(width: 5),
          Flexible(
            child: Text(
              label,
              key: const Key('home_sync_chip_label'),
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w800,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
    if (kIsWeb) return chip;
    // One button named by the state ("3 waiting to sync", "Offline",
    // "Synced"); the chip stays small, the tap area is 48 by 48.
    return Semantics(
      button: true,
      label: label,
      excludeSemantics: true,
      onTap: () => _showSyncStatus(context),
      child: ConstrainedBox(
        constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
        child: InkWell(
          key: const Key('home_sync_chip'),
          borderRadius: BorderRadius.circular(999),
          onTap: () => _showSyncStatus(context),
          child: Center(widthFactor: 1, heightFactor: 1, child: chip),
        ),
      ),
    );
  }
}

/// Places a Home dashboard tile or recent-activity row can open.
/// How many of the farmer's order rows (`GET /orders?farmerId=`) wait for
/// their answer: funded, the farmer's answer still pending (an unstamped
/// funded row is still theirs to answer). The wire side of
/// [canFarmerRespond], so Home's Pending Orders tile and the Orders tab's
/// "Waiting for you" agree.
@visibleForTesting
int farmerTurnOrderCount(List<dynamic> items) => items.where((item) {
      if (item is! Map) return false;
      final status = item['status'];
      if (status is! String ||
          orderStatusFromWire(status) != OrderStatus.funded.name) {
        return false;
      }
      final answer = item['farmerResponse'];
      return answer == null ||
          answer == '' ||
          answer == CommitmentResponse.pending;
    }).length;

enum _HomeDest {
  sellCrops,
  myCrops,
  myTrips,
  findCrops,
  findTrips,
  orders,

  /// Orders, opened on the orders waiting for the farmer's answer (the
  /// Pending Orders tile).
  ordersYourTurn,
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
      // My Crops is a farmer tab: the tile switches to it.
      await tabOrPush(
        RoleTab.myCrops,
        const BrowseListingsScreen(ownerMode: true),
      );
    case _HomeDest.myTrips:
    case _HomeDest.findTrips:
      // My Trips is a trucker tab; a buyer (ALL trips) gets a pushed screen.
      await tabOrPush(RoleTab.myTrips, const BrowseTripsScreen());
    case _HomeDest.findCrops:
      await webOrPush('/find-crops', const FindCropsScreen());
    case _HomeDest.ordersYourTurn:
      // The tile's count is these orders: the tab opens on exactly them.
      ordersFilterRequest.value = ordersFilterYourTurn;
      await tabOrPush(
        RoleTab.orders,
        const Scaffold(body: OrdersTabScreen(showBackButton: true)),
      );
    case _HomeDest.orders:
      // Orders isn't a trucker tab (truckers act on deliveries), so it
      // pushes for them.
      await tabOrPush(
        RoleTab.orders,
        const Scaffold(body: OrdersTabScreen(showBackButton: true)),
      );
    case _HomeDest.deliveries:
      // Deliveries left the farmer's bar: a pushed page for them.
      await tabOrPush(
        RoleTab.deliveries,
        // Its empty state's button ("See my orders") switches the tab
        // under it.
        DeliveriesPage(onNavigateToTab: onNavigateToTab),
      );
    case _HomeDest.payments:
      // Payments is no bar tab: the payment history screen.
      await tabOrPush(RoleTab.payments, const PaymentHistoryScreen());
  }
}

/// One At a Glance tile (the Farmer and Trucker home boards, design
/// canvas page M): the label over a count or an amount, a line under it on
/// the 2 by 2 grid and, on the farmer's Deliveries tile, a link in the
/// role's colour. The role's lead tile is black with white ink, the others
/// flat sand; an amount is in the role's colour. Every tile opens its
/// section; a tile without a [value] is a plain shortcut with its [icon].
class _GlanceTile extends StatelessWidget {
  final String label;
  final String? value;

  /// [value] is an amount ("4,500 ETB"): set smaller than a count, in the
  /// role's colour.
  final bool amount;
  final String? sub;

  /// What the tile opens, said under [sub] ("Track delivery").
  final String? link;
  final IconData? icon;
  final bool emphasized;

  /// The trucker's three-across row: no sub line.
  final bool compact;
  final VoidCallback onTap;

  const _GlanceTile({
    required this.label,
    required this.onTap,
    this.value,
    this.amount = false,
    this.sub,
    this.link,
    this.icon,
    this.emphasized = false,
    this.compact = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final role = context.roleColors;
    final ink = emphasized ? Colors.white : AppTheme.ink;
    final muted = emphasized ? Colors.white : AppTheme.inkSoft;
    final value = this.value;
    final sub = this.sub;
    final link = this.link;
    final valueSize =
        amount ? (compact ? 16.0 : 20.0) : (compact ? 24.0 : 26.0);

    return Semantics(
      button: true,
      child: Material(
        color: emphasized ? AppTheme.ink : AppTheme.sand,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(24),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Padding(
            padding: EdgeInsets.fromLTRB(14, 12, 14, compact ? 14 : 13),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: muted,
                  ),
                ),
                if (value != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 1),
                    child: ConstrainedBox(
                      // A count and an amount sit on the same 30 px line.
                      constraints: const BoxConstraints(minHeight: 30),
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
                            height: 1.15,
                            // On sand the role colour itself is about
                            // 3.8:1, too little at the compact 16 px:
                            // its dark step passes 4.5:1.
                            color: amount && !emphasized ? role.dark : ink,
                          ),
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
                  Padding(
                    padding: const EdgeInsets.only(top: 1),
                    child: Text(
                      sub,
                      style: TextStyle(
                        fontSize: 13,
                        height: 1.3,
                        color: muted,
                      ),
                    ),
                  ),
                if (link != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      link,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w800,
                        color: emphasized ? Colors.white : role.primary,
                      ),
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
                  color: AppTheme.ink,
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

  /// [listings] stopped short and the server has more (web, SIM-33): the
  /// tile reads "50+", never a short count as if it were all. The buyer's
  /// count is one page; the farmer's own count follows every page up to
  /// [_ownListMaxPages] (FUP-126).
  final bool moreListings;
  final int activeListings;
  final int reservedListings;
  final int pendingOrders;
  final int activeOrders;

  /// Deliveries on the road now (picked up or in transit).
  final int inTransit;
  final int trips;

  /// [trips] stopped at [_ownListMaxPages] pages and the server has more,
  /// as [moreListings].
  final bool moreTrips;
  final int matched;
  final double escrowEtb;

  const _HomeStats({
    this.listings = 0,
    this.moreListings = false,
    this.activeListings = 0,
    this.reservedListings = 0,
    this.pendingOrders = 0,
    this.activeOrders = 0,
    this.inTransit = 0,
    this.trips = 0,
    this.moreTrips = false,
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

  /// Under the tiles (the buyer's intents, the Recent line). The trucker's
  /// Payments row, which shows the escrow these stats read, comes after it.
  final Widget? afterTiles;

  const _OverviewStats({
    required this.role,
    required this.userId,
    required this.l10n,
    super.key,
    this.onNavigateToTab,
    this.onRefresh,
    this.afterTiles,
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
    _pullThenReload();
  }

  @override
  void didUpdateWidget(covariant _OverviewStats oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId || oldWidget.role != widget.role) {
      setState(() {
        _statsFuture = _loadStats(widget.userId, widget.role);
      });
      _pullThenReload();
    }
  }

  /// BUG-37: the tiles count what the phone holds, and they used to be read
  /// once, while the Action needed card's order pull was still on its way.
  /// After a login the phone holds nothing yet, so the tiles stayed at 0
  /// next to a card (and an Orders tab) that already showed the orders. Now
  /// the stored numbers paint first, the user's orders are pulled (the same
  /// request the card makes: the sync joins the two) and, for a farmer, the
  /// own listings (BUG-31), and the tiles are read again. Best effort:
  /// offline the stored numbers stand. Mobile only: web reads the server.
  Future<void> _pullThenReload() async {
    final userId = widget.userId;
    final role = widget.role;
    if (kIsWeb || userId.isEmpty) return;
    final MarketplaceSync? sync;
    try {
      sync = context.read<MarketplaceSync?>();
    } on Object {
      // No sync above this widget (widget tests): the stored numbers stand.
      return;
    }
    if (sync == null) return;
    try {
      await sync.pullOrders(userId: userId, role: role.name);
    } on Object {
      // Offline or a failed read: the stored orders are counted.
    }
    if (role == UserRole.farmer) {
      try {
        await sync.pullOwnCrops(farmerId: userId);
      } on Object {
        // Offline or a failed read: the stored listings are counted.
      }
    }
    if (!mounted || widget.userId != userId || widget.role != role) return;
    final _HomeStats stats;
    try {
      stats = await _loadIsarStats(userId, role);
    } on Object {
      // The first read's error chip (with Retry) already says so.
      return;
    }
    if (!mounted || widget.userId != userId || widget.role != role) return;
    // Already complete, so the tiles change without a loading placeholder.
    setState(() => _statsFuture = SynchronousFuture(stats));
  }

  void _retry() {
    setState(() {
      // Handled here too: a read that fails before the next build has
      // subscribed would otherwise surface as an uncaught error.
      _statsFuture = _loadStats(widget.userId, widget.role)..ignore();
    });
  }

  @override
  Widget build(BuildContext context) {
    final compact = widget.role == UserRole.trucker;
    return FutureBuilder<_HomeStats>(
      future: _statsFuture,
      builder: (context, snapshot) {
        // E-6: a count not read yet, or one that failed, is never a 0. A
        // retry reads as loading (the snapshot keeps the old error).
        final done = snapshot.connectionState == ConnectionState.done;
        final failed = done && snapshot.hasError;
        final unknown = !done
            ? _loadingValue
            : failed
                ? _failedValue
                : null;
        final stats = snapshot.data ?? const _HomeStats();
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (failed)
              Padding(
                padding: const EdgeInsets.only(bottom: 10),
                child: LoadErrorChip(
                  key: const Key('home_tiles_load_failed'),
                  message: widget.l10n.homeTilesLoadFailed,
                  retryLabel: widget.l10n.retry,
                  onRetry: _retry,
                ),
              ),
            _DashboardGrid(
              tiles: _tilesForRole(widget.role, stats, unknown: unknown),
              columns: compact ? 3 : 2,
              gap: compact ? 8 : 10,
            ),
            if (widget.afterTiles case final after?) after,
            // Trucker home board: Payments left the trucker's bar; this row
            // (and the account sheet) opens it.
            if (widget.role == UserRole.trucker)
              Padding(
                padding: const EdgeInsets.only(top: 10),
                child: _PaymentsRow(
                  detail: unknown != null
                      ? null
                      : widget.l10n
                          .homePaymentsInEscrow(birrText(stats.escrowEtb)),
                  onTap: () => _openHomeDest(
                    context,
                    role: widget.role,
                    dest: _HomeDest.payments,
                    onNavigateToTab: widget.onNavigateToTab,
                    onRefresh: widget.onRefresh,
                  ),
                ),
              ),
          ],
        );
      },
    );
  }

  /// A tile's value while the stats load.
  static const _loadingValue = '\u2026';

  /// A tile's value when the stats failed (the error chip says so).
  static const _failedValue = '-';

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

      // Each call is isolated so one failing endpoint does not stop the
      // others; any failure fails the stats below (E-6), never a 0.
      var failed = false;
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
          failed = true;
          return <String, dynamic>{};
        }
      }

      final _HomeStats stats;
      // One GET per resource, with the same path and params the Recent
      // line and the Action needed card use, so ApiClient folds the
      // concurrent identical reads into a single request (SIM-21).
      switch (role) {
        case UserRole.farmer:
          // FUP-126: every page of the farmer's own listings (up to
          // [_ownListMaxPages]), so the active and reserved counts are not
          // those of the first 50 only.
          final cropsFuture = fetchApiPages(
            safeGet,
            '/crops',
            listKey: 'crops',
            params: {'farmerId': userId},
            maxPages: _ownListMaxPages,
          );
          final deliveriesFuture = safeGet(
            '/deliveries',
            queryParameters: {'farmerId': userId, 'limit': '50'},
          );
          final orderItems =
              await _fetchOrderPages(safeGet, {'farmerId': userId});
          final cropPages = await cropsFuture;
          final crops = cropPages.items;
          stats = _HomeStats(
            listings: crops.length,
            moreListings: cropPages.more,
            // The API says "available" for an active listing.
            activeListings: _countByStatus(crops, 'available') +
                _countByStatus(crops, 'active'),
            reservedListings: _countByStatus(crops, 'reserved'),
            // Waiting for the farmer's answer, the Orders tab's "Waiting
            // for you" (and the action card's) rule, not every funded order.
            pendingOrders: farmerTurnOrderCount(orderItems),
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
          // One page of every farmer's available crops (the buyer sees
          // them all): the tile reads "50+" when there are more, rather than
          // reading every page on 3G for a count (SIM-33).
          final cropsFuture = safeGet(
            '/crops',
            queryParameters: {'status': 'available', 'limit': '50'},
          );
          final orderItems =
              await _fetchOrderPages(safeGet, {'buyerId': userId});
          final cropsPage = await cropsFuture;
          stats = _HomeStats(
            listings: _apiItems(cropsPage, alt: 'crops').length,
            moreListings: apiPageHasMore(cropsPage),
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
          // The trucker's own trips, every page up to [_ownListMaxPages]
          // like the farmer's listings (FUP-126), so the matched count is
          // not that of the first 50 only. The escrow tile sums the
          // transport pay of the orders assigned to this trucker
          // (truckerId=, the Action needed card's read).
          final tripsFuture = fetchApiPages(
            safeGet,
            '/trips',
            listKey: 'trips',
            params: {'truckerId': userId},
            maxPages: _ownListMaxPages,
          );
          final orderItems =
              await _fetchOrderPages(safeGet, {'truckerId': userId});
          final tripPages = await tripsFuture;
          final trips = tripPages.items;
          stats = _HomeStats(
            trips: trips.length,
            moreTrips: tripPages.more,
            matched: _countByStatus(trips, 'matched'),
            escrowEtb: _sumOrders(
              orderItems,
              escrowHeldOrderStatusNames,
              (o) =>
                  o['truckerId'] == userId ? wireDouble(o['transportCost']) : 0,
            ),
          );
      }
      if (failed) throw StateError('home stats incomplete');
      return stats;
    } on Object catch (e) {
      debugPrint('[OverviewStats] Web stats failed: $e');
      rethrow;
    }
  }

  static List<dynamic> _apiItems(Map<String, dynamic> data, {String? alt}) =>
      data[alt] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];

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
    final pages = await fetchApiPages(
      get,
      '/orders',
      listKey: 'orders',
      params: params,
      maxPages: 5,
    );
    return pages.items;
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
          final funded = await isar.orders
              .filter()
              .farmerIdEqualTo(userId)
              .statusEqualTo(OrderStatus.funded)
              .findAll();
          final pending =
              funded.where((o) => canFarmerRespond(o, userId)).length;
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
          // ESC-22: through the truckerId index, not a scan of every order.
          final held = await isar.orders
              .where()
              .truckerIdEqualTo(userId)
              .filter()
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
      // E-6: the tiles say it failed, never 0.
      debugPrint('[OverviewStats] Isar stats failed: $e');
      rethrow;
    }
  }

  /// The role's tiles. With [unknown] (loading or failed) every count and
  /// amount shows it instead, and no count line under a tile.
  List<Widget> _tilesForRole(
    UserRole role,
    _HomeStats stats, {
    String? unknown,
  }) {
    final l10n = widget.l10n;
    final etb = unknown ?? l10n.etbCurrency(birrText(stats.escrowEtb));
    String count(int value, {bool more = false}) =>
        unknown ?? glanceCountText(value, more: more);

    Widget tile(
      _HomeDest dest, {
      required String label,
      String? value,
      bool amount = false,
      String? sub,
      String? link,
      IconData? icon,
      bool emphasized = false,
      bool compact = false,
    }) {
      return _GlanceTile(
        key: Key('home_tile_${dest.name}'),
        label: label,
        value: value,
        amount: amount,
        sub: sub,
        link: link,
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
        // Farmer home board: My Crops leads, black; Sell is in the bar.
        // Deliveries is no bar tab for the farmer: its tile (and the "on
        // the road" card) is how the farmer gets there.
        return [
          tile(
            _HomeDest.myCrops,
            label: l10n.myCrops,
            value: count(stats.listings, more: stats.moreListings),
            sub: unknown != null
                ? null
                : l10n.homeTileActiveReserved(
                    stats.activeListings,
                    stats.reservedListings,
                  ),
            emphasized: true,
          ),
          tile(
            _HomeDest.ordersYourTurn,
            label: l10n.pendingOrders,
            value: count(stats.pendingOrders),
            sub: l10n.homeTileWaitingForYou,
          ),
          tile(
            _HomeDest.payments,
            label: l10n.escrow,
            value: etb,
            amount: true,
            sub: l10n.homeTileHeldUntilConfirmed,
          ),
          tile(
            _HomeDest.deliveries,
            label: l10n.deliveries,
            value: count(stats.inTransit),
            sub: l10n.homeTileInTransit,
            link: l10n.trackDelivery,
          ),
        ];
      case UserRole.trucker:
        // Trucker · Home board: three across, the Escrow tile is the
        // trucker's transport pay held for their orders.
        return [
          tile(
            _HomeDest.myTrips,
            label: l10n.myTrips,
            value: count(stats.trips, more: stats.moreTrips),
            compact: true,
          ),
          tile(
            _HomeDest.myTrips,
            label: l10n.matchedLoads,
            value: count(stats.matched),
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
            value: count(stats.listings, more: stats.moreListings),
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
            value: count(stats.activeOrders),
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
            value: count(stats.inTransit),
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
    // The line's text is built when it loads: another language needs it
    // built again, or it keeps the old one until a reload (BUG-24).
    if (oldWidget.userId != widget.userId ||
        oldWidget.l10n.localeName != widget.l10n.localeName) {
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

/// Most pages the web At a glance follows for the user's own crops or trips
/// (FUP-126): 500 rows, a cap on what the dashboard costs on 3G. More than
/// that reads "500+".
const int _ownListMaxPages = 10;

/// Every page of the list at [path] for [params], 50 rows a page, following
/// `nextToken` for at most [maxPages] pages. The first request is [params]
/// with `limit=50`, the same read as the Recent line, so ApiClient folds the
/// two (SIM-21). `more` is true when the read stopped at [maxPages] while the
/// server had another page. A failed page ([get] answers an empty map) ends
/// the read.
@visibleForTesting
Future<({List<dynamic> items, bool more})> fetchApiPages(
  Future<Map<String, dynamic>> Function(
    String path, {
    Map<String, dynamic>? queryParameters,
  }) get,
  String path, {
  required String listKey,
  required Map<String, String> params,
  required int maxPages,
}) async {
  final items = <dynamic>[];
  String? nextToken;
  for (var page = 0; page < maxPages; page++) {
    final data = await get(
      path,
      queryParameters: {
        ...params,
        'limit': '50',
        if (nextToken != null) 'nextToken': nextToken,
      },
    );
    final list = data[listKey] ?? data['items'];
    if (list is List) items.addAll(list);
    final token = data['nextToken'];
    if (token is! String || token.isEmpty) return (items: items, more: false);
    nextToken = token;
  }
  return (items: items, more: true);
}

/// Whether a list response has another page: its `hasMore` when it sends
/// one, else a non-empty `nextToken` (crop-listing-api.md, SIM-33).
@visibleForTesting
bool apiPageHasMore(Map<String, dynamic> page) {
  final hasMore = page['hasMore'];
  if (hasMore is bool) return hasMore;
  final token = page['nextToken'];
  return token is String && token.isNotEmpty;
}

/// An At a glance count: "20", or "50+" when it counts one page of more
/// ([apiPageHasMore]), so a first page never reads as the whole list.
@visibleForTesting
String glanceCountText(int count, {required bool more}) =>
    more ? '$count+' : '$count';

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

/// The Recent line (Trucker home board): an outlined pill with a mark for
/// the event's kind in the role's colour, "Recent: ..." and how long ago.
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
    final icon = switch (item.type) {
      _ActivityType.info => Icons.info_outline,
      _ActivityType.success => Icons.check,
      _ActivityType.pending => Icons.schedule,
    };

    final timeDiff = DateTime.now().difference(item.timestamp);
    final timeLabel = timeDiff.inMinutes < 60
        ? l10n.minutesAgo(timeDiff.inMinutes)
        : timeDiff.inHours < 24
            ? l10n.hoursAgo(timeDiff.inHours)
            : l10n.daysAgo(timeDiff.inDays);

    return Semantics(
      button: true,
      // A 44 tall pill in a 48 tall hit area.
      child: FwTapTarget(
        onTap: onTap,
        child: Material(
          color: Colors.transparent,
          shape: const StadiumBorder(
            side: BorderSide(color: AppTheme.fieldBorder),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            key: const Key('home_recent_line'),
            onTap: onTap,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
                child: Row(
                  children: [
                    Icon(icon, size: 16, color: context.roleColors.primary),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        l10n.homeRecentLine(item.description),
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.inkSoft,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      timeLabel,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.slate,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The trucker's Payments row (Trucker home board): a flat sand row with
/// the wallet, "Payments" over the transport pay held in escrow, and
/// "Open". Payments is no bar tab for the trucker; this row opens it.
class _PaymentsRow extends StatelessWidget {
  /// "4,500 ETB in escrow"; null while the amount is not known.
  final String? detail;
  final VoidCallback onTap;

  const _PaymentsRow({required this.detail, required this.onTap});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final detail = this.detail;
    return Semantics(
      button: true,
      child: FwCard.sand(
        key: const Key('home_payments_row'),
        padding: const EdgeInsetsDirectional.fromSTEB(16, 8, 14, 8),
        onTap: onTap,
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 40),
          child: Row(
            children: [
              const Icon(
                Icons.account_balance_wallet_outlined,
                size: 20,
                color: AppTheme.ink,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      l10n.payments,
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.15,
                        color: AppTheme.ink,
                      ),
                    ),
                    if (detail != null)
                      Text(
                        detail,
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.inkSoft,
                        ),
                      ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              HomeOpenMark(label: l10n.homeOpen, pill: false),
            ],
          ),
        ),
      ),
    );
  }
}
