import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
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
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Which deliveries the segmented switch shows (Buyer · Deliveries board):
/// Active is everything not cancelled and not yet past its check window.
enum _Segment { active, delivered, all }

/// Deliveries tab (Fieldway: `android/BuyerDeliveries.dc.html` and, from a
/// desktop width, `web/LiveDeliveries.dc.html`). Reads the viewer's own
/// deliveries from the app-root DeliveryBloc via LoadUserDeliveries.
///
/// The list is one column at every phone and tablet width: the page header,
/// the Active / Delivered / All switch, then the deliveries under uppercase
/// stage labels (ON THE ROAD NOW, WAITING FOR PICKUP, DELIVERED, ...). The
/// buyer's and the farmer's own deliveries on the road are live trip cards
/// (LT-11); the trucker gets the same stages with plain cards, never the
/// buyer's map. From a desktop width the web board applies: a list column
/// with the selected delivery's live trip beside it, a summary line and a
/// search box; the trucker (no live trip pane) keeps the centred column.
class DeliveriesTabScreen extends StatefulWidget {
  const DeliveriesTabScreen({super.key});

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

  /// Active (Buyer · Deliveries board): everything not cancelled that is
  /// not settled yet, so a delivered one stays here through the buyer's 48 h
  /// check window ("Release pending") and moves to Delivered after it.
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
            FwPageHeader(
              eyebrow: _ownerLine() ?? l10n.trackDeliveries,
              title: l10n.deliveries,
              trailing: _profileButton(context, l10n),
            ),
            const OfflineBanner(),
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
      body = Padding(
        padding: const EdgeInsets.symmetric(horizontal: 32),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(
              width: 380,
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
            const SizedBox(width: 24),
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
        border: Border.all(color: AppTheme.whisperBorder),
      ),
      child: Row(
        children: [
          const Icon(Icons.search, size: 18, color: AppTheme.mutedSteel),
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
                color: AppTheme.soilDark,
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Shared pieces ───────────────────────────────────────────────────

  Widget _profileButton(BuildContext context, AppLocalizations l10n) {
    return FwSquareButton(
      semanticLabel: l10n.profile,
      onTap: () => Navigator.push(
        context,
        MaterialPageRoute<void>(builder: (_) => const ProfileScreen()),
      ),
      child: const Icon(
        Icons.person_outline,
        size: 20,
        color: AppTheme.soilDark,
      ),
    );
  }

  Widget _segmented(List<Delivery> all, AppLocalizations l10n) {
    final active = all.where(_isActive).length;
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

  /// The deliveries under their stage labels, or the loading, error and
  /// empty states. [split] is the desktop list column: a tap there selects
  /// the delivery for the pane when it has one.
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
      return _EmptyState(
        title: l10n.noDeliveries,
        hint: l10n.deliveryNoDeliveriesHint,
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
    if (deliveries.isEmpty) {
      if (state.deliveries.isNotEmpty && _searchQuery.trim().isNotEmpty) {
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
        l10n.liveTripOnTheRoadNow,
        inStage({DeliveryStatus.pickedUp, DeliveryStatus.inTransit}),
      ),
      (l10n.liveTripWaitingForPickup, inStage({DeliveryStatus.pending})),
      (l10n.deliveryFilterDelivered, inStage({DeliveryStatus.delivered})),
      (l10n.deliveryStatusDisputed, inStage({DeliveryStatus.disputed})),
      (l10n.deliveryStatusCancelled, inStage({DeliveryStatus.cancelled})),
    ];

    Widget card(Delivery delivery) {
      final isSelected = split && delivery.deliveryId == selected?.deliveryId;
      if (_isOwnLiveTrip(delivery, userId)) {
        return OnTheRoadCard(
          key: ValueKey('on-road-${delivery.deliveryId}'),
          delivery: delivery,
          currentUserId: userId,
          selected: isSelected,
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
        onTap: hasPane
            ? () => setState(() => _selectedId = delivery.deliveryId)
            : () => _openDeliveryDetail(delivery, userId),
      );
    }

    final items = <Widget>[
      for (final (label, stage) in stages)
        if (stage.isNotEmpty) ...[
          FwSectionLabel(label, padding: const EdgeInsets.only(top: 6)),
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

/// The tab's empty, initial and error states: a truck, a line, a hint.
class _EmptyState extends StatelessWidget {
  final String title;
  final String? hint;
  final Widget? action;

  const _EmptyState({required this.title, this.hint, this.action});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.local_shipping_outlined,
              size: 56,
              color: AppTheme.textMuted.withAlpha(150),
            ),
            const SizedBox(height: 16),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: AppTheme.textDark,
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
