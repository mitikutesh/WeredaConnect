import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/presentation/open_live_trip.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// Which deliveries the segmented switch shows (Buyer · Deliveries board):
/// Active is everything not cancelled and not yet past its check window.
enum _Segment { active, delivered, all }

/// Deliveries tab (design canvas page M: `BuyerDeliveriesM`,
/// `BuyerDeliveriesEmptyM` and, from a desktop width, `WebDeliveriesM`).
/// The buyer lands here. Reads the viewer's own deliveries from the
/// app-root DeliveryBloc via LoadUserDeliveries.
///
/// The list is one column at every phone and tablet width: the page header
/// with the role's sketch, the Active / Delivered / All switch, then the
/// deliveries under uppercase stage labels with an icon (ON THE ROAD NOW,
/// WAITING FOR PICKUP, DELIVERED, ...). The buyer's own delivery on the
/// road is the raised card with the map, the farmer's the sand card with
/// the route ribbon (LT-11); the trucker gets the same stages with plain
/// cards, never the buyer's map. With no delivery at all the tab shows the
/// role's large sketch and where to go next. From a desktop width the web board applies: a list column
/// with the selected delivery's live trip beside it, a summary line and a
/// search box; the trucker (no live trip pane) keeps the centred column.
/// The deliveries list as a pushed page with a back bar, for a role whose
/// bar has no Deliveries tab (the farmer: Home's tile and the account
/// sheet open it).
class DeliveriesPage extends StatelessWidget {
  const DeliveriesPage({this.onNavigateToTab, super.key});

  /// Switches the shell under this page to one of the role's bar tabs;
  /// the page closes itself first. Without it the empty state shows no
  /// buttons on a phone.
  final ValueChanged<int>? onNavigateToTab;

  @override
  Widget build(BuildContext context) {
    final toTab = onNavigateToTab;
    return Scaffold(
      appBar: AppBar(),
      body: DeliveriesTabScreen(
        onNavigateToTab: toTab == null
            ? null
            : (index) {
                Navigator.of(context).maybePop();
                toTab(index);
              },
      ),
    );
  }
}

class DeliveriesTabScreen extends StatefulWidget {
  const DeliveriesTabScreen({this.onNavigateToTab, super.key});

  /// Switches the shell to the bar tab at this index of the role's tabs
  /// ([roleTabIndex]), as Home's tiles do: the empty state's "Find crops"
  /// and "See my orders" use it. On the web the router is used instead.
  final ValueChanged<int>? onNavigateToTab;

  @override
  State<DeliveriesTabScreen> createState() => _DeliveriesTabScreenState();
}

class _DeliveriesTabScreenState extends State<DeliveriesTabScreen> {
  _Segment _segment = _Segment.active;
  String? _userRole;

  /// The delivery shown beside the list on a desktop width.
  String? _selectedId;

  /// Desktop search: narrows the loaded list by crop, place or party name.
  String _searchQuery = '';

  @override
  void initState() {
    super.initState();
    _userRole = _auth?.role.name;
    _loadDeliveries();
  }

  AuthAuthenticated? get _auth {
    final state = context.read<AuthBloc>().state;
    return state is AuthAuthenticated ? state : null;
  }

  void _loadDeliveries() {
    final userId = _auth?.userId;
    if (userId != null) {
      context.read<DeliveryBloc>().add(
            LoadUserDeliveries(userId: userId, role: _userRole),
          );
    }
  }

  /// Opens the delivery detail and reloads the list on return. The detail
  /// mutates the shared app-root DeliveryBloc (mark-in-transit / confirm /
  /// cancel / dispute) and the tab has no BlocListener, so without this the
  /// list was left showing the detail's terminal state until a manual
  /// refresh.
  Future<void> _openDeliveryDetail(Delivery delivery, String userId) async {
    await Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (_) => DeliveryDetailScreen(
          delivery: delivery,
          currentUserId: userId,
        ),
      ),
    );
    if (!mounted) return;
    _loadDeliveries();
  }

  /// Opens the viewer's live trip and reloads the list on return: the
  /// buyer's QR scan there confirms the delivery through the shared bloc.
  Future<void> _openLiveTrip(Delivery delivery, String userId) async {
    await openLiveTrip(context, delivery, userId);
    if (!mounted) return;
    _loadDeliveries();
  }

  /// A delivery on the road that [userId] follows as its buyer or farmer.
  static bool _isOwnLiveTrip(Delivery delivery, String userId) =>
      isOnTheRoad(delivery) && liveTripViewerOf(delivery, userId) != null;

  /// Active (Buyer · Deliveries board) lists everything not cancelled that
  /// is not settled yet, so a delivered one stays here through the buyer's
  /// 48 h check window ("Release pending") and leaves after it; Delivered
  /// lists every delivered one. As on the board, the Active count is only
  /// what is still moving ("Active · 2" over the two loads and the release
  /// pending card), so no delivery is counted twice.
  static bool _isActive(Delivery delivery) {
    if (delivery.status == DeliveryStatus.cancelled) return false;
    if (delivery.status != DeliveryStatus.delivered) return true;
    return DeliveryCard.reportWindowEnd(delivery, DateTime.now().toUtc()) !=
        null;
  }

  bool _inSegment(Delivery delivery) => switch (_segment) {
        _Segment.active => _isActive(delivery),
        _Segment.delivered => delivery.status == DeliveryStatus.delivered,
        _Segment.all => true,
      };

  bool _matchesSearch(Delivery delivery, AppLocalizations l10n) {
    final query = _searchQuery.trim().toLowerCase();
    if (query.isEmpty) return true;
    final crop = delivery.cropType;
    return [
      delivery.farmerName,
      delivery.buyerName,
      delivery.truckerName,
      delivery.pickupPlace ?? '',
      if (crop != null) crop,
      if (crop != null) CropType.labelFor(crop, l10n),
    ].any((text) => text.toLowerCase().contains(query));
  }

  /// "Selam Traders · Merkato": the viewer and their place (the board's
  /// eyebrow); null when the profile has neither.
  String? _ownerLine() {
    final auth = _auth;
    if (auth == null) return null;
    final parts = [auth.userName.trim(), (auth.woreda ?? '').trim()]
        .where((part) => part.isNotEmpty);
    return parts.isEmpty ? null : parts.join(' · ');
  }

  /// "2 on the road · 1 waiting for pickup · 1 in the check window" (the
  /// web board's line under the title), or the tab's tagline when none.
  String _summaryLine(List<Delivery> all, AppLocalizations l10n) {
    final now = DateTime.now().toUtc();
    final onTheRoad = all.where(isOnTheRoad).length;
    final waiting = all.where((d) => d.status == DeliveryStatus.pending).length;
    final inWindow =
        all.where((d) => DeliveryCard.reportWindowEnd(d, now) != null).length;
    final parts = [
      if (onTheRoad > 0) l10n.delivSummaryOnTheRoad(onTheRoad),
      if (waiting > 0) l10n.delivSummaryWaiting(waiting),
      if (inWindow > 0) l10n.delivSummaryCheckWindow(inWindow),
    ];
    return parts.isEmpty ? l10n.trackDeliveries : parts.join(' · ');
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final userId = _auth?.userId ?? '';

    return BlocBuilder<DeliveryBloc, DeliveryState>(
      builder: (context, state) {
        final all =
            state is DeliveriesLoaded ? state.deliveries : const <Delivery>[];
        if (Breakpoints.isDesktop(context)) {
          return _desktop(context, l10n, state, all, userId);
        }
        return _column(context, l10n, state, all, userId);
      },
    );
  }

  // ── Phone and tablet: the board's column ────────────────────────────

  Widget _column(
    BuildContext context,
    AppLocalizations l10n,
    DeliveryState state,
    List<Delivery> all,
    String userId,
  ) {
    return SafeArea(
      bottom: false,
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _DeliveriesHeader(
              eyebrow: _ownerLine() ?? l10n.trackDeliveries,
              title: l10n.deliveries,
              // One sketch per screen: the empty state draws the large
              // one, so the header leaves its own out.
              role:
                  state is DeliveriesLoaded && all.isEmpty ? null : _auth?.role,
              trailing: _profileButton(context, l10n),
            ),
            const OfflineBanner(),
            if (all.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 2, 20, 4),
                child: _segmented(all, l10n),
              ),
            Expanded(
              child: _list(
                context,
                l10n,
                state,
                all.where(_inSegment).toList(),
                userId,
                split: false,
              ),
            ),
          ],
        ),
      ),
    );
  }

  // ── Desktop: the Web · Buyer deliveries board ───────────────────────

  Widget _desktop(
    BuildContext context,
    AppLocalizations l10n,
    DeliveryState state,
    List<Delivery> all,
    String userId,
  ) {
    final shown = all.where((d) => _matchesSearch(d, l10n)).toList();
    // The live trip pane exists for the buyer and the farmer of a truck
    // delivery; the trucker (and a list of self-pickups) keeps one column.
    final withPane = [
      for (final d in shown)
        if (liveTripViewerOf(d, userId) != null) d,
    ];
    final split = withPane.isNotEmpty;
    final selected = !split
        ? null
        : withPane.firstWhere(
            (d) => d.deliveryId == _selectedId,
            orElse: () => withPane.firstWhere(
              isOnTheRoad,
              orElse: () => withPane.first,
            ),
          );

    final header = Padding(
      padding: const EdgeInsets.fromLTRB(32, 28, 32, 16),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Semantics(
                  header: true,
                  child: Text(
                    l10n.deliveries,
                    style: FwText.pageTitle.copyWith(fontSize: 28),
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  _summaryLine(all, l10n),
                  style: FwText.body.copyWith(fontSize: 14),
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          const SizedBox(width: 16),
          _searchBox(l10n),
        ],
      ),
    );

    final Widget body;
    if (split) {
      // The web board's proportions: a narrow list, the pane gets the rest
      // (at 1280 px about 660 px, close to the board's, for its two columns).
      body = Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(
              width: 336,
              child: _list(
                context,
                l10n,
                state,
                shown,
                userId,
                split: true,
                selected: selected,
              ),
            ),
            const SizedBox(width: 16),
            Expanded(
              child: liveTripPane(selected!, userId) ?? const SizedBox.shrink(),
            ),
          ],
        ),
      );
    } else {
      body = FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (all.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 4, 20, 4),
                child: _segmented(all, l10n),
              ),
            Expanded(
              child: _list(
                context,
                l10n,
                state,
                shown.where(_inSegment).toList(),
                userId,
                split: false,
              ),
            ),
          ],
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        header,
        const OfflineBanner(),
        Expanded(child: body),
      ],
    );
  }

  Widget _searchBox(AppLocalizations l10n) {
    return Container(
      width: 330,
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: BoxDecoration(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: AppTheme.line),
      ),
      child: Row(
        children: [
          const Icon(Icons.search, size: 18, color: AppTheme.slate),
          const SizedBox(width: 8),
          Expanded(
            child: TextField(
              key: const Key('deliveries_search'),
              onChanged: (value) => setState(() => _searchQuery = value),
              decoration: InputDecoration(
                hintText: l10n.deliveriesSearchHint,
                hintStyle: FwText.body,
                border: InputBorder.none,
                isDense: true,
              ),
              style: const TextStyle(
                fontSize: 13,
                color: AppTheme.ink,
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Shared pieces ───────────────────────────────────────────────────

  Widget _profileButton(BuildContext context, AppLocalizations l10n) {
    return const HeaderActions();
  }

  Widget _segmented(List<Delivery> all, AppLocalizations l10n) {
    final active = all
        .where(
          (d) =>
              d.status != DeliveryStatus.cancelled &&
              d.status != DeliveryStatus.delivered,
        )
        .length;
    final delivered =
        all.where((d) => d.status == DeliveryStatus.delivered).length;
    return FwSegmented<_Segment>(
      segments: [
        FwSegment(
          value: _Segment.active,
          label: l10n.delivSegmentActive,
          count: active > 0 ? active : null,
        ),
        FwSegment(
          value: _Segment.delivered,
          label: l10n.deliveryFilterDelivered,
          count: delivered > 0 ? delivered : null,
        ),
        FwSegment(value: _Segment.all, label: l10n.deliveryFilterAll),
      ],
      selected: _segment,
      onChanged: (segment) => setState(() => _segment = segment),
    );
  }

  /// A way to the role's [tab] from here, or null when there is none: the
  /// router on the web, the shell's tab switch on a phone (only when the
  /// shell handed one in and [tab] is on the role's bar).
  VoidCallback? _toTab(RoleTab tab) {
    if (kIsWeb) return () => context.go(tab.route);
    final role = _auth?.role;
    final switchTab = widget.onNavigateToTab;
    final index = role == null ? null : roleTabIndex(role, tab);
    if (switchTab == null || index == null) return null;
    return () => switchTab(index);
  }

  /// The empty state in the viewer's own words: the buyer follows a truck
  /// here and is sent to the market or their orders; the farmer waits for
  /// a driver; the trucker's own loads show here.
  Widget _noDeliveries(AppLocalizations l10n) {
    final role = _auth?.role;
    ({String label, VoidCallback onTap})? action(String label, RoleTab tab) {
      final go = _toTab(tab);
      return go == null ? null : (label: label, onTap: go);
    }

    return switch (role) {
      UserRole.farmer => _NoDeliveries(
          role: role,
          body: l10n.delivEmptyBodyFarmer,
          primary: null,
          secondary: action(l10n.delivEmptySeeOrders, RoleTab.orders),
        ),
      UserRole.trucker => _NoDeliveries(
          role: role,
          body: l10n.delivEmptyBodyTrucker,
          primary: null,
          secondary: action(l10n.delivEmptySeeTrips, RoleTab.myTrips),
        ),
      UserRole.buyer || null => _NoDeliveries(
          role: role,
          body: l10n.delivEmptyBodyBuyer,
          primary: action(l10n.delivEmptyFindCrops, RoleTab.market),
          secondary: action(l10n.delivEmptySeeOrders, RoleTab.orders),
        ),
    };
  }

  /// The deliveries under their stage labels, or the loading, error and
  /// empty states. [split] is the desktop list column of the web board: the
  /// same stage order with no labels and the compact cards, and a tap there
  /// selects the delivery for the pane when it has one.
  Widget _list(
    BuildContext context,
    AppLocalizations l10n,
    DeliveryState state,
    List<Delivery> deliveries,
    String userId, {
    required bool split,
    Delivery? selected,
  }) {
    if (state is DeliveryLoading) {
      return const Center(child: CircularProgressIndicator());
    }
    if (state is DeliveryError) {
      // E-6: a failed read is not "No deliveries yet".
      return _EmptyState(
        key: const Key('deliveries_load_failed'),
        icon: Icons.error_outline,
        iconColor: AppTheme.statusError,
        title: l10n.deliveriesLoadFailed,
        hint: l10n.checkConnectionRetry,
        action: SizedBox(
          width: 200,
          child: FilledButton(
            onPressed: _loadDeliveries,
            child: Text(l10n.retry),
          ),
        ),
      );
    }
    if (state is! DeliveriesLoaded) {
      return _EmptyState(title: l10n.noDeliveries);
    }
    if (state.deliveries.isEmpty) return _noDeliveries(l10n);
    if (deliveries.isEmpty) {
      if (_searchQuery.trim().isNotEmpty) {
        // Only the search hid them: say so, not "no deliveries".
        return Center(
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 32),
            child: Text(
              l10n.deliveriesNoSearchMatch,
              textAlign: TextAlign.center,
              style: FwText.body.copyWith(fontSize: 15),
            ),
          ),
        );
      }
      // Only the switch hid them (nothing delivered yet, say).
      return _EmptyState(
        title: l10n.noDeliveries,
        hint: l10n.deliveryNoDeliveriesHint,
      );
    }

    // The stages in the order a delivery lives, each under its label.
    List<Delivery> inStage(Set<DeliveryStatus> statuses) => [
          for (final d in deliveries)
            if (statuses.contains(d.status)) d,
        ];
    final stages = [
      (
        Icons.local_shipping_outlined,
        l10n.liveTripOnTheRoadNow,
        inStage({DeliveryStatus.pickedUp, DeliveryStatus.inTransit}),
      ),
      (
        Icons.inventory_2_outlined,
        l10n.liveTripWaitingForPickup,
        inStage({DeliveryStatus.pending}),
      ),
      (
        Icons.check_circle_outline,
        l10n.deliveryFilterDelivered,
        inStage({DeliveryStatus.delivered}),
      ),
      (
        Icons.report_problem_outlined,
        l10n.deliveryStatusDisputed,
        inStage({DeliveryStatus.disputed}),
      ),
      (
        Icons.block,
        l10n.deliveryStatusCancelled,
        inStage({DeliveryStatus.cancelled}),
      ),
    ];

    Widget card(Delivery delivery) {
      final isSelected = split && delivery.deliveryId == selected?.deliveryId;
      if (_isOwnLiveTrip(delivery, userId)) {
        return OnTheRoadCard(
          key: ValueKey('on-road-${delivery.deliveryId}'),
          delivery: delivery,
          currentUserId: userId,
          selected: isSelected,
          compact: split,
          onTap: split
              ? () => setState(() => _selectedId = delivery.deliveryId)
              : () => _openLiveTrip(delivery, userId),
        );
      }
      final hasPane = split && liveTripViewerOf(delivery, userId) != null;
      return DeliveryCard(
        key: ValueKey('delivery-${delivery.deliveryId}'),
        delivery: delivery,
        currentUserId: userId,
        selected: isSelected,
        compact: split,
        onTap: hasPane
            ? () => setState(() => _selectedId = delivery.deliveryId)
            : () => _openDeliveryDetail(delivery, userId),
      );
    }

    final items = <Widget>[
      for (final (icon, label, stage) in stages)
        if (stage.isNotEmpty) ...[
          if (!split) _StageLabel(icon: icon, label: label),
          for (final delivery in stage) card(delivery),
        ],
    ];

    final hPad = split ? 0.0 : 20.0;
    return RefreshIndicator(
      onRefresh: () async => _loadDeliveries(),
      child: ListView.separated(
        padding: EdgeInsets.fromLTRB(hPad, 12, hPad, Breakpoints.fabClearance),
        itemCount: items.length,
        separatorBuilder: (_, __) => const SizedBox(height: 10),
        itemBuilder: (context, index) => items[index],
      ),
    );
  }
}

/// The tab's header on a phone (the Buyer deliveries board): the viewer
/// and their place over the 42 px title, the bell and account buttons at
/// the end, and under them, beside the title, the role's sketch. This tab
/// draws the sketch larger than [FwPageHeader] does (the board: the one
/// place the header sketch is that large); it is left out when the title
/// leaves it no room, with large text and with reduced effects.
class _DeliveriesHeader extends StatelessWidget {
  const _DeliveriesHeader({
    required this.eyebrow,
    required this.title,
    required this.role,
    required this.trailing,
  });

  final String eyebrow;
  final String title;
  final UserRole? role;
  final Widget trailing;

  static const double _sketchWidth = 112;
  static const double _gap = 16;

  @override
  Widget build(BuildContext context) {
    final role = this.role;
    return LayoutBuilder(
      builder: (context, constraints) {
        var sketch = false;
        if (role != null && RoleSketch.sketchesShown(context)) {
          final painter = TextPainter(
            text: TextSpan(
              text: title,
              style: DefaultTextStyle.of(context).style.merge(FwText.pageTitle),
            ),
            textDirection: Directionality.of(context),
            textScaler: MediaQuery.textScalerOf(context),
            maxLines: 1,
          )..layout();
          sketch =
              constraints.maxWidth - 40 - painter.width >= _sketchWidth + _gap;
          painter.dispose();
        }
        return Stack(
          children: [
            Padding(
              padding: EdgeInsets.fromLTRB(20, 16, 20, sketch ? 26 : 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          eyebrow,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w500,
                            color: AppTheme.slate,
                          ),
                        ),
                      ),
                      const SizedBox(width: 12),
                      trailing,
                    ],
                  ),
                  // One line: large text shrinks the title instead of
                  // breaking it inside the word.
                  Semantics(
                    header: true,
                    child: FittedBox(
                      fit: BoxFit.scaleDown,
                      alignment: AlignmentDirectional.centerStart,
                      child: Text(title, maxLines: 1, style: FwText.pageTitle),
                    ),
                  ),
                ],
              ),
            ),
            if (sketch)
              Positioned(
                right: 12,
                bottom: 2,
                child: RoleSketch(
                  key: const Key('deliveries_header_sketch'),
                  role: role!,
                  width: _sketchWidth,
                ),
              ),
          ],
        );
      },
    );
  }
}

/// A stage's label with its icon ("ON THE ROAD NOW" behind a truck).
class _StageLabel extends StatelessWidget {
  const _StageLabel({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 1),
            child: Icon(icon, size: 16, color: AppTheme.slate),
          ),
          const SizedBox(width: 8),
          Expanded(child: FwSectionLabel(label)),
        ],
      ),
    );
  }
}

/// No delivery at all (the Buyer deliveries empty board): the role's large
/// sketch, a title, what will show here, and where to go meanwhile.
class _NoDeliveries extends StatelessWidget {
  const _NoDeliveries({
    required this.role,
    required this.body,
    required this.primary,
    required this.secondary,
  });

  final UserRole? role;
  final String body;

  /// The orange action (the buyer's "Find crops"); null for none.
  final ({String label, VoidCallback onTap})? primary;

  /// The outlined action ("See my orders"); null for none.
  final ({String label, VoidCallback onTap})? secondary;

  static const _buttonText = TextStyle(
    fontFamily: AppTheme.fontFamily,
    fontFamilyFallback: AppTheme.ethiopicFontFallback,
    fontSize: 14,
    fontWeight: FontWeight.w800,
  );

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final role = this.role;
    final primary = this.primary;
    final secondary = this.secondary;
    return LayoutBuilder(
      builder: (context, constraints) => SingleChildScrollView(
        key: const Key('deliveries_empty'),
        padding:
            const EdgeInsets.fromLTRB(28, 12, 28, Breakpoints.fabClearance),
        child: ConstrainedBox(
          constraints: BoxConstraints(
            minHeight: (constraints.maxHeight - 12 - Breakpoints.fabClearance)
                .clamp(0.0, double.infinity),
          ),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (role != null) ...[
                    Center(child: RoleSketch.emptyState(role: role)),
                    const SizedBox(height: 14),
                  ],
                  Semantics(
                    header: true,
                    child: Text(
                      l10n.delivEmptyTitle,
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        fontSize: 24,
                        height: 1.15,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.5,
                        color: AppTheme.ink,
                      ),
                    ),
                  ),
                  const SizedBox(height: 14),
                  Text(
                    body,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                      fontSize: 15,
                      height: 1.45,
                      color: AppTheme.slate,
                    ),
                  ),
                  if (primary != null) ...[
                    const SizedBox(height: 24),
                    FilledButton.icon(
                      key: const Key('deliveries_empty_primary'),
                      onPressed: primary.onTap,
                      icon: const Icon(Icons.search, size: 20),
                      label: Text(primary.label, textAlign: TextAlign.center),
                    ),
                  ],
                  if (secondary != null) ...[
                    SizedBox(height: primary == null ? 24 : 14),
                    OutlinedButton(
                      key: const Key('deliveries_empty_secondary'),
                      onPressed: secondary.onTap,
                      style: OutlinedButton.styleFrom(
                        minimumSize: const Size.fromHeight(48),
                        textStyle: _buttonText,
                      ),
                      child: Text(
                        secondary.label,
                        textAlign: TextAlign.center,
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The tab's empty, initial and error states: a truck (an error mark for
/// the error), a line, a hint.
class _EmptyState extends StatelessWidget {
  final String title;
  final String? hint;
  final Widget? action;
  final IconData icon;
  final Color? iconColor;

  const _EmptyState({
    required this.title,
    this.hint,
    this.action,
    this.icon = Icons.local_shipping_outlined,
    this.iconColor,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              icon,
              size: 56,
              color: iconColor ?? AppTheme.slate.withAlpha(150),
            ),
            const SizedBox(height: 16),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: AppTheme.ink,
              ),
            ),
            if (hint != null) ...[
              const SizedBox(height: 4),
              Text(
                hint!,
                textAlign: TextAlign.center,
                style: FwText.body,
              ),
            ],
            if (action != null) ...[
              const SizedBox(height: 20),
              action!,
            ],
          ],
        ),
      ),
    );
  }
}
