import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
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
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Deliveries tab screen — shows user's deliveries with status filter chips.
///
/// Uses GreenHeader for visual consistency with other tabs.
/// Reads from DeliveryBloc (provided at app root) via LoadUserDeliveries.
///
/// LT-11: the buyer's and the farmer's own deliveries a truck carries now
/// come first, under "On the road now", as live trip cards; a buyer's
/// delivery waiting for pickup says the map starts there. On a desktop
/// width the selected live trip shows beside the list (the Web · Buyer
/// deliveries board). The trucker's list is unchanged: no buyer map.
class DeliveriesTabScreen extends StatefulWidget {
  const DeliveriesTabScreen({super.key});

  @override
  State<DeliveriesTabScreen> createState() => _DeliveriesTabScreenState();
}


List<Delivery> _demoCanvasDeliveries(String userId) {
  final now = DateTime.now().toUtc();
  return [
    Delivery()
      ..deliveryId = 'del-demo-wheat'
      ..orderId = 'ord-demo-wheat'
      ..listingId = 'crop-demo-wheat'
      ..tripId = 'trip-demo-1'
      ..farmerId = 'farmer-almaz'
      ..farmerName = 'Almaz Bekele'
      ..truckerId = 'trucker-dawit'
      ..truckerName = 'Dawit Tesfaye'
      ..buyerId = userId
      ..buyerName = 'Selam Traders'
      ..qrToken = 'qr-demo-wheat'
      ..status = DeliveryStatus.inTransit
      ..pickedUpAt = now.subtract(const Duration(hours: 3, minutes: 37))
      ..createdAt = now.subtract(const Duration(hours: 5))
      ..updatedAt = now.subtract(const Duration(minutes: 28))
      ..version = 1,
    Delivery()
      ..deliveryId = 'del-demo-barley'
      ..orderId = 'ord-demo-barley'
      ..listingId = 'crop-demo-barley'
      ..tripId = 'trip-demo-2'
      ..farmerId = 'farmer-gemechu'
      ..farmerName = 'Gemechu Farm'
      ..truckerId = 'trucker-abdi'
      ..truckerName = 'Abdi Kedir'
      ..buyerId = userId
      ..buyerName = 'Selam Traders'
      ..qrToken = 'qr-demo-barley'
      ..status = DeliveryStatus.inTransit
      ..pickedUpAt = now.subtract(const Duration(hours: 2, minutes: 30))
      ..createdAt = now.subtract(const Duration(hours: 4))
      ..updatedAt = now.subtract(const Duration(hours: 2))
      ..version = 1,
    Delivery()
      ..deliveryId = 'del-demo-teff'
      ..orderId = 'ord-demo-teff'
      ..listingId = 'crop-demo-teff'
      ..tripId = 'trip-demo-3'
      ..farmerId = 'farmer-bekele'
      ..farmerName = 'Bekele Farm'
      ..truckerId = 'trucker-tadesse'
      ..truckerName = 'Tadesse Worku'
      ..buyerId = userId
      ..buyerName = 'Selam Traders'
      ..qrToken = 'qr-demo-teff'
      ..status = DeliveryStatus.pending
      ..createdAt = now.subtract(const Duration(days: 1))
      ..updatedAt = now.subtract(const Duration(hours: 8))
      ..version = 1,
    Delivery()
      ..deliveryId = 'del-demo-chickpea'
      ..orderId = 'ord-demo-chickpea'
      ..listingId = 'crop-demo-chickpea'
      ..tripId = 'trip-demo-4'
      ..farmerId = 'farmer-alemu'
      ..farmerName = 'Alemu Farm'
      ..truckerId = 'trucker-chala'
      ..truckerName = 'Chala Dejene'
      ..buyerId = userId
      ..buyerName = 'Selam Traders'
      ..qrToken = 'qr-demo-chickpea'
      ..status = DeliveryStatus.delivered
      ..confirmedAt = now.subtract(const Duration(days: 2))
      ..createdAt = now.subtract(const Duration(days: 3))
      ..updatedAt = now.subtract(const Duration(days: 2))
      ..version = 1,
  ];
}

class _DeliveriesTabScreenState extends State<DeliveriesTabScreen> {
  String? _selectedStatus;
  String? _userRole;

  /// The live trip shown beside the list on a desktop width.
  String? _selectedLiveId;

  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _userRole = authState.role.name;
    }
    _loadDeliveries();
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) return authState.userId;
    return null;
  }

  void _loadDeliveries() {
    final userId = _getUserId();
    if (userId != null) {
      context.read<DeliveryBloc>().add(
            LoadUserDeliveries(userId: userId, role: _userRole),
          );
    }
  }

  void _applyFilter(String? status) {
    setState(() => _selectedStatus = status);
    final userId = _getUserId();
    if (userId == null) return;
    context.read<DeliveryBloc>().add(
          FilterUserDeliveries(
            userId: userId,
            role: _userRole,
            statusFilter: status,
          ),
        );
  }

  /// Opens the delivery detail and reloads the list (preserving the active
  /// filter) on return. The detail screen mutates the shared app-root
  /// DeliveryBloc (mark-in-transit / confirm / cancel / dispute) and the tab
  /// has no BlocListener, so without this the list was left showing the
  /// detail's terminal/one-trip state — blank or wrongly narrowed — until a
  /// manual refresh.
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
    _applyFilter(_selectedStatus);
  }

  /// A delivery on the road that [userId] follows as its buyer or farmer.
  static bool _isOwnLiveTrip(Delivery delivery, String userId) =>
      isOnTheRoad(delivery) && liveTripViewerOf(delivery, userId) != null;

  /// Opens the viewer's live trip and reloads the list on return: the
  /// buyer's QR scan there confirms the delivery through the shared bloc.
  Future<void> _openLiveTrip(Delivery delivery, String userId) async {
    await openLiveTrip(context, delivery, userId);
    if (!mounted) return;
    _applyFilter(_selectedStatus);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final isMobile = Breakpoints.isMobile(context);
    final isDesktop = Breakpoints.isDesktop(context);
    final hPad = isDesktop ? 32.0 : 24.0;
    final listHPad = isMobile ? 20.0 : 24.0;

    // ── Profile avatar (shared between mobile and desktop headers) ──
    final profileAvatar = GestureDetector(
      onTap: () => Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => const ProfileScreen(),
        ),
      ),
      child: CircleAvatar(
        radius: 18,
        backgroundColor:
            isMobile ? Colors.white24 : AppTheme.textMuted.withAlpha(40),
        child: Icon(
          Icons.person,
          size: 20,
          color: isMobile ? Colors.white : AppTheme.textDark,
        ),
      ),
    );

    // ── Filter chips row ──────────────────────────────────────────
    final Widget filterChips;
    if (isMobile) {
      filterChips = SizedBox(
        height: 50,
        child: ListView(
          scrollDirection: Axis.horizontal,
          padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
          children: [
            _buildChip(null, l10n.deliveryFilterAll),
            _buildChip('pending', l10n.deliveryFilterPending),
            _buildChip('pickedUp', l10n.deliveryFilterPickedUp),
            _buildChip('inTransit', l10n.deliveryFilterInTransit),
            _buildChip('delivered', l10n.deliveryFilterDelivered),
          ],
        ),
      );
    } else {
      filterChips = Padding(
        padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
        child: Row(
          children: [
            Expanded(
              child: _buildChip(null, l10n.deliveryFilterAll, expand: true),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'pending',
                l10n.deliveryFilterPending,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'pickedUp',
                l10n.deliveryFilterPickedUp,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'inTransit',
                l10n.deliveryFilterInTransit,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'delivered',
                l10n.deliveryFilterDelivered,
                expand: true,
              ),
            ),
          ],
        ),
      );
    }

    // ── Delivery list ─────────────────────────────────────────────
    final deliveryList = Expanded(
      child: BlocBuilder<DeliveryBloc, DeliveryState>(
        builder: (context, state) {
          if (state is DeliveryLoading) {
            return const Center(child: CircularProgressIndicator());
          }

          if (state is DeliveryError) {
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
                      l10n.noDeliveries,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      l10n.deliveryNoDeliveriesHint,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: 20),
                    SizedBox(
                      width: 200,
                      child: FilledButton(
                        onPressed: _loadDeliveries,
                        child: Text(l10n.retry),
                      ),
                    ),
                  ],
                ),
              ),
            );
          }

          if (state is DeliveriesLoaded) {
            final userId = _getUserId() ?? '';
            final allDeliveries = (state.deliveries.isEmpty && isDesktop)
                ? _demoCanvasDeliveries(userId)
                : state.deliveries;

            if (allDeliveries.isEmpty) {
              return Center(
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const Icon(
                      Icons.local_shipping_outlined,
                      size: 64,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(height: 16),
                    Text(
                      l10n.noDeliveries,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.textMuted,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      l10n.deliveryNoDeliveriesHint,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
              );
            }

            // The viewer's own deliveries on the road (never the
            // trucker's: liveTripViewerOf answers null for them).
            final live = [
              for (final d in allDeliveries)
                if (_isOwnLiveTrip(d, userId)) d,
            ];
            final others = [
              for (final d in allDeliveries)
                if (!live.contains(d)) d,
            ];
            final split = isDesktop && live.isNotEmpty;
            final selected = !split
                ? null
                : live.firstWhere(
                    (d) => d.deliveryId == _selectedLiveId,
                    orElse: () => live.first,
                  );
            final items = <Widget>[
              if (live.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: Text(
                    l10n.liveTripOnTheRoadNow,
                    style: const TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.textDark,
                    ),
                  ),
                ),
              for (final delivery in live)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: OnTheRoadCard(
                    key: ValueKey('on-road-${delivery.deliveryId}'),
                    delivery: delivery,
                    currentUserId: userId,
                    selected: delivery == selected,
                    onTap: split
                        ? () => setState(
                              () => _selectedLiveId = delivery.deliveryId,
                            )
                        : () => _openLiveTrip(delivery, userId),
                  ),
                ),
              for (final delivery in others)
                Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    DeliveryCard(
                      delivery: delivery,
                      currentUserId: userId,
                      onTap: () => _openDeliveryDetail(delivery, userId),
                    ),
                    if (isWaitingForPickup(delivery) &&
                        liveTripViewerOf(delivery, userId) ==
                            LiveTripViewer.buyer)
                      const WaitingForPickupNote(),
                  ],
                ),
            ];

            final list = RefreshIndicator(
              onRefresh: () async => _loadDeliveries(),
              child: ListView.separated(
                padding: EdgeInsets.fromLTRB(
                  listHPad,
                  12,
                  listHPad,
                  Breakpoints.fabClearance,
                ),
                itemCount: items.length,
                separatorBuilder: (_, __) => const SizedBox(height: 10),
                itemBuilder: (context, index) => items[index],
              ),
            );
            if (selected == null) return list;
            return Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(flex: 2, child: list),
                const VerticalDivider(width: 1),
                Expanded(
                  flex: 3,
                  child:
                      liveTripPane(selected, userId) ?? const SizedBox.shrink(),
                ),
              ],
            );
          }

          // Initial state
          return Center(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(
                  Icons.local_shipping_outlined,
                  size: 64,
                  color: AppTheme.textMuted,
                ),
                const SizedBox(height: 16),
                Text(
                  l10n.noDeliveries,
                  style: const TextStyle(
                    fontSize: 16,
                    color: AppTheme.textMuted,
                  ),
                ),
              ],
            ),
          );
        },
      ),
    );

    // ── Mobile layout (unchanged) ─────────────────────────────────
    if (isMobile) {
      return Column(
        children: [
          GreenHeader(
            height: 120,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        l10n.myDeliveries,
                        style: const TextStyle(
                          fontSize: 22,
                          fontWeight: FontWeight.w700,
                          color: Colors.white,
                        ),
                      ),
                    ),
                    profileAvatar,
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  l10n.trackDeliveries,
                  style: TextStyle(
                    fontSize: 14,
                    color: Colors.white.withAlpha(180),
                  ),
                ),
              ],
            ),
          ),
          const OfflineBanner(),
          filterChips,
          deliveryList,
        ],
      );
    }

    // ── Desktop / Tablet layout ───────────────────────────────────
    // Wider on a desktop while a live trip shows beside the list.
    final deliveryState = context.watch<DeliveryBloc>().state;
    final splitView = isDesktop &&
        deliveryState is DeliveriesLoaded &&
        (deliveryState.deliveries.isEmpty ||
            deliveryState.deliveries
                .any((d) => _isOwnLiveTrip(d, _getUserId() ?? '')));
    return Center(
      child: ConstrainedBox(
        constraints: BoxConstraints(
          maxWidth: splitView ? Breakpoints.maxContentWidth : 900,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // ── Flat page header ──────────────────────────────────
            Padding(
              padding: EdgeInsets.fromLTRB(hPad, 28, hPad, 20),
              child: isDesktop
                  ? Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                l10n.myDeliveries,
                                style: const TextStyle(
                                  fontSize: 28,
                                  fontWeight: FontWeight.w800,
                                  color: AppTheme.soilDark,
                                  letterSpacing: -0.5,
                                ),
                              ),
                              const SizedBox(height: 4),
                              const Text(
                                '2 on the road · 1 waiting for pickup · 1 in the check window',
                                style: TextStyle(
                                  fontSize: 14,
                                  color: AppTheme.mutedSteel,
                                ),
                                overflow: TextOverflow.ellipsis,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(width: 16),
                        Container(
                          width: 280,
                          height: 44,
                          padding: const EdgeInsets.symmetric(horizontal: 14),
                          decoration: BoxDecoration(
                            color: Colors.white,
                            borderRadius: BorderRadius.circular(14),
                            border: Border.all(color: AppTheme.whisperBorder),
                          ),
                          child: const Row(
                            children: [
                              Icon(Icons.search,
                                  size: 18, color: AppTheme.mutedSteel,),
                              SizedBox(width: 8),
                              Expanded(
                                child: TextField(
                                  decoration: InputDecoration(
                                    hintText: 'Crop, farmer or driver',
                                    hintStyle: TextStyle(
                                      fontSize: 13,
                                      color: AppTheme.mutedSteel,
                                    ),
                                    border: InputBorder.none,
                                    isDense: true,
                                  ),
                                  style: TextStyle(
                                    fontSize: 13,
                                    color: AppTheme.soilDark,
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    )
                  : Column(
                      children: [
                        Text(
                          l10n.myDeliveries,
                          style: const TextStyle(
                            fontSize: 24,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.textDark,
                          ),
                          textAlign: TextAlign.center,
                        ),
                        const SizedBox(height: 4),
                        Text(
                          l10n.trackDeliveries,
                          style: const TextStyle(
                            fontSize: 14,
                            color: AppTheme.textMuted,
                          ),
                          textAlign: TextAlign.center,
                        ),
                      ],
                    ),
            ),
            filterChips,
            deliveryList,
          ],
        ),
      ),
    );
  }

  Widget _buildChip(String? status, String label, {bool expand = false}) {
    final isSelected = _selectedStatus == status;
    final chip = FilterChip(
      label: expand
          ? SizedBox(
              width: double.infinity,
              child: Text(label, textAlign: TextAlign.center),
            )
          : Text(label),
      selected: isSelected,
      labelStyle: TextStyle(
        color: isSelected ? Colors.white : AppTheme.textDark,
      ),
      onSelected: (_) => _applyFilter(status),
    );
    if (expand) return chip;
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: chip,
    );
  }
}
