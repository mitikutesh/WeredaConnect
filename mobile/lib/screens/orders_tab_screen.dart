import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Orders tab screen — shows incoming/outgoing orders with status filter chips.
///
/// Uses GreenHeader for visual consistency with other tabs.
/// Reads from OrderBloc (provided at app root) via LoadUserOrders.
class OrdersTabScreen extends StatefulWidget {
  /// Shows a back button in the header when pushed as a standalone screen
  /// (roles whose tab bar doesn't include Orders reach it that way).
  final bool showBackButton;

  const OrdersTabScreen({this.showBackButton = false, super.key});

  @override
  State<OrdersTabScreen> createState() => _OrdersTabScreenState();
}

class _OrdersTabScreenState extends State<OrdersTabScreen> {
  String? _selectedStatus;
  String? _userRole;

  /// Orders whose escrow is held for review (FRD-06), read with the orders.
  Set<String> _heldOrderIds = const {};

  /// Whether that read has answered once; Accept waits for it.
  bool _heldChecked = false;

  /// Only the newest read may change the list.
  int _heldRead = 0;

  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _userRole = authState.role.name;
    }
    _loadOrders();
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) return authState.userId;
    return null;
  }

  bool _isFarmer() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.role == UserRole.farmer;
    }
    return false;
  }

  bool _isBuyer() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.role == UserRole.buyer;
    }
    return false;
  }

  void _loadOrders() {
    final userId = _getUserId();
    if (userId != null) {
      context.read<OrderBloc>().add(
            LoadUserOrders(userId: userId, role: _userRole),
          );
    }
    _loadHeldOrders();
  }

  /// Which of this party's orders are under review: their escrow is held
  /// (FRD-06). Read on open, on pull-to-refresh and after the detail screen,
  /// not on every filter tap. A failed read, or no escrow side above this
  /// screen, flags none.
  Future<void> _loadHeldOrders() async {
    final read = ++_heldRead;
    final userId = _getUserId();
    final role = _userRole;
    BaseEscrowRepository? repository;
    try {
      repository = context.read<EscrowBloc>().repository;
    } on Object {
      repository = null;
    }
    final held = userId == null || role == null || repository == null
        ? const <String>{}
        : await heldOrderIds(repository, userId: userId, role: role);
    if (!mounted || read != _heldRead) return;
    setState(() {
      _heldOrderIds = held;
      _heldChecked = true;
    });
  }

  void _applyFilter(String? status) {
    setState(() => _selectedStatus = status);
    final userId = _getUserId();
    if (userId == null) return;
    context.read<OrderBloc>().add(
          FilterOrders(
            userId: userId,
            role: _userRole,
            statusFilter: status,
          ),
        );
  }

  /// Opens the order-detail screen under its OWN scoped [OrderBloc].
  ///
  /// The detail screen loads a single order (LoadOrderById), which would
  /// otherwise drive this screen's shared app-root [OrderBloc] through
  /// Loading/Error states and leave the list stuck on an error+retry view when
  /// the user returns. A route-scoped bloc (reusing the same repository) keeps
  /// the detail lifecycle isolated. On return we reload so any status change
  /// made in the detail screen (accept/reject/cancel/pay) is reflected here.
  Future<void> _openOrderDetail(String orderId) async {
    final repository = context.read<OrderBloc>().repository;
    await Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (_) => BlocProvider<OrderBloc>(
          create: (_) => OrderBloc(repository: repository),
          child: OrderDetailScreen(orderId: orderId),
        ),
      ),
    );
    if (!mounted) return;
    // Preserve the active status tab (mirrors the Retry button) rather than
    // resetting to "All".
    _applyFilter(_selectedStatus);
    _loadHeldOrders();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final isFarmer = _isFarmer();

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
      child: const CircleAvatar(
        radius: 18,
        backgroundColor: Color(0xFFECEAE2),
        child: Icon(
          Icons.person,
          size: 20,
          color: AppTheme.soilDark,
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
            _buildChip('created', l10n.orderPending),
            _buildChip('accepted', l10n.orderAccepted),
            _buildChip('funded', l10n.orderFunded),
            _buildChip('inTransit', l10n.deliveryFilterInTransit),
            _buildChip('delivered', l10n.deliveryFilterDelivered),
            // ESC-09: paid-out orders move on from delivered with their money.
            _buildChip('disbursing', l10n.statusDisbursing),
            _buildChip('settled', l10n.statusSettled),
            _buildChip('cancelled', l10n.orderCancelled),
          ],
        ),
      );
    } else {
      filterChips = Padding(
        padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
        child: Wrap(
          spacing: 10,
          runSpacing: 8,
          children: [
            _buildChip(null, l10n.deliveryFilterAll),
            _buildChip('created', l10n.orderPending),
            _buildChip('accepted', l10n.orderAccepted),
            _buildChip('funded', l10n.orderFunded),
            _buildChip('inTransit', l10n.deliveryFilterInTransit),
            _buildChip('delivered', l10n.deliveryFilterDelivered),
            // ESC-09: paid-out orders move on from delivered with their money.
            _buildChip('disbursing', l10n.statusDisbursing),
            _buildChip('settled', l10n.statusSettled),
            _buildChip('cancelled', l10n.orderCancelled),
          ],
        ),
      );
    }

    // ── Order list ────────────────────────────────────────────────
    final orderList = Expanded(
      child: BlocBuilder<OrderBloc, OrderState>(
        builder: (context, state) {
          if (state is OrderLoading) {
            return const Center(child: CircularProgressIndicator());
          }

          if (state is OrderError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 32),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(
                      Icons.error_outline,
                      size: 56,
                      color: AppTheme.statusError,
                    ),
                    const SizedBox(height: 16),
                    Text(
                      localizeErrorCode(state.code, l10n),
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
                        // Retry preserves the currently selected status filter.
                        onPressed: () => _applyFilter(_selectedStatus),
                        child: Text(l10n.retry),
                      ),
                    ),
                  ],
                ),
              ),
            );
          }

          if (state is OrdersLoaded) {
            if (state.orders.isEmpty) {
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
                      l10n.noOrders,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.textMuted,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      l10n.ordersWillAppear,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
              );
            }

            return RefreshIndicator(
              // Preserve the active status tab (not reset to "All"), and hold
              // the refresh spinner until the reload actually finishes.
              onRefresh: () async {
                _applyFilter(_selectedStatus);
                _loadHeldOrders();
                await context.read<OrderBloc>().stream.firstWhere(
                      (s) => s is OrdersLoaded || s is OrderError,
                    );
              },
              child: ListView.separated(
                padding: EdgeInsets.fromLTRB(
                  listHPad,
                  12,
                  listHPad,
                  Breakpoints.fabClearance,
                ),
                itemCount: state.orders.length,
                separatorBuilder: (_, __) => const SizedBox(height: 10),
                itemBuilder: (context, index) {
                  final order = state.orders[index];
                  final userId = _getUserId() ?? '';
                  return OrderCard(
                    order: order,
                    isFarmerView: isFarmer,
                    // A cached `held` row is stale once the order moved on.
                    underReview: _heldOrderIds.contains(order.orderId) &&
                        orderHoldableStatuses.contains(order.status),
                    acceptEnabled: _heldChecked,
                    onTap: () => _openOrderDetail(order.orderId),
                    onAccept: isFarmer
                        ? () {
                            context.read<OrderBloc>().add(
                                  AcceptOrder(
                                    orderId: order.orderId,
                                    farmerId: userId,
                                  ),
                                );
                          }
                        : null,
                    onReject: isFarmer
                        ? () {
                            context.read<OrderBloc>().add(
                                  RejectOrder(
                                    orderId: order.orderId,
                                    farmerId: userId,
                                  ),
                                );
                          }
                        : null,
                  );
                },
              ),
            );
          }

          // Initial state
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
                  l10n.noOrders,
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

    // ── Mobile layout (Fieldway canvas) ──────────────────────────
    if (isMobile) {
      return BlocListener<OrderBloc, OrderState>(
        listener: (context, state) {
          if (state is OrderAccepted ||
              state is OrderRejected ||
              state is OrderCreated ||
              state is PaymentSubmitted ||
              state is OrderCancelled) {
            _loadOrders();
          }
        },
        child: Column(
          children: [
            Container(
              color: AppTheme.chalkBackground,
              padding: EdgeInsets.fromLTRB(
                listHPad,
                MediaQuery.of(context).padding.top + 16,
                listHPad,
                12,
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (widget.showBackButton)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: IconButton(
                        icon: const Icon(
                          Icons.arrow_back,
                          color: AppTheme.soilDark,
                        ),
                        tooltip: MaterialLocalizations.of(context)
                            .backButtonTooltip,
                        onPressed: () => Navigator.of(context).maybePop(),
                      ),
                    ),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          _isBuyer() ? l10n.trackYourOrders : l10n.manageOrders,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: AppTheme.mutedSteel,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          l10n.orders,
                          style: const TextStyle(
                            fontSize: 26,
                            fontWeight: FontWeight.w800,
                            letterSpacing: -0.5,
                            color: AppTheme.soilDark,
                          ),
                        ),
                      ],
                    ),
                  ),
                  profileAvatar,
                ],
              ),
            ),
            const OfflineBanner(),
            filterChips,
            orderList,
          ],
        ),
      );
    }

    // ── Desktop / Tablet layout ───────────────────────────────────
    return BlocListener<OrderBloc, OrderState>(
      listener: (context, state) {
        if (state is OrderAccepted ||
            state is OrderRejected ||
            state is OrderCreated ||
            state is PaymentSubmitted ||
            state is OrderCancelled) {
          _loadOrders();
        }
      },
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 900),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // ── Flat page header ──────────────────────────────────
              Padding(
                padding: EdgeInsets.fromLTRB(hPad, 28, hPad, 20),
                child: Column(
                  children: [
                    Text(
                      l10n.orders,
                      style: const TextStyle(
                        fontSize: 24,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.textDark,
                      ),
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _isBuyer() ? l10n.trackYourOrders : l10n.manageOrders,
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
              orderList,
            ],
          ),
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
      selectedColor: AppTheme.fieldGreen,
      backgroundColor: Colors.white,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(999),
        side: BorderSide(
          color: isSelected ? AppTheme.fieldGreen : AppTheme.whisperBorder,
        ),
      ),
      labelStyle: TextStyle(
        fontSize: 13,
        fontWeight: isSelected ? FontWeight.w700 : FontWeight.w600,
        color: isSelected ? Colors.white : AppTheme.soilDark,
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
