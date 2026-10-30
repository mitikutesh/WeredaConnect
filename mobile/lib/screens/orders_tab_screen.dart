import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
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
import 'package:wereda_connect/features/order/presentation/widgets/order_commitment_section.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Orders tab: a sibling of the My Crops board. Page header (person ·
/// place, "Orders", profile square), the status filter as the Market's pill
/// chips, then one [OrderCard] per order, in the phone-width column at
/// every window size.
///
/// Reads from OrderBloc (provided at app root) via LoadUserOrders; the
/// server scopes the list to the caller (buyer, farmer or trucker).
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

  void _openProfile() {
    Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => const ProfileScreen()),
    );
  }

  Widget _buildHeader(AppLocalizations l10n) {
    final authState = context.read<AuthBloc>().state;
    final header = FwPageHeader(
      padding: EdgeInsets.zero,
      eyebrow: fwPersonPlaceEyebrow(
        authState,
        _isBuyer() ? l10n.trackYourOrders : l10n.manageOrders,
      ),
      title: l10n.orders,
      trailing: FwSquareButton(
        onTap: _openProfile,
        semanticLabel: l10n.profile,
        child: const Icon(
          Icons.person_outline,
          size: 22,
          color: AppTheme.soilDark,
        ),
      ),
    );
    // A tab child draws under the status bar; a pushed screen has SafeArea.
    final top = widget.showBackButton ? 0.0 : MediaQuery.paddingOf(context).top;
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 16 + top, 20, 12),
      child: widget.showBackButton
          ? Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                FwSquareButton(
                  onTap: () => Navigator.of(context).maybePop(),
                  semanticLabel:
                      MaterialLocalizations.of(context).backButtonTooltip,
                  child: const Icon(
                    Icons.arrow_back,
                    size: 20,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(child: header),
              ],
            )
          : header,
    );
  }

  Widget _buildFilters(AppLocalizations l10n) {
    return FwFilterPillRow<String?>(
      options: [
        FwFilterOption(value: null, label: l10n.deliveryFilterAll),
        FwFilterOption(value: 'created', label: l10n.orderPending),
        FwFilterOption(value: 'accepted', label: l10n.orderAccepted),
        FwFilterOption(value: 'funded', label: l10n.orderFunded),
        FwFilterOption(value: 'inTransit', label: l10n.deliveryFilterInTransit),
        FwFilterOption(value: 'delivered', label: l10n.deliveryFilterDelivered),
        // ESC-09: paid-out orders move on from delivered with their money.
        FwFilterOption(value: 'disbursing', label: l10n.statusDisbursing),
        FwFilterOption(value: 'settled', label: l10n.statusSettled),
        FwFilterOption(value: 'cancelled', label: l10n.orderCancelled),
      ],
      selected: _selectedStatus,
      onSelected: _applyFilter,
    );
  }

  Widget _buildEmpty(AppLocalizations l10n) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(
            Icons.receipt_long_outlined,
            size: 56,
            color: AppTheme.mutedSteel,
          ),
          const SizedBox(height: 16),
          Text(l10n.noOrders, style: FwText.cardTitle),
          const SizedBox(height: 4),
          Text(l10n.ordersWillAppear, style: FwText.body),
        ],
      ),
    );
  }

  Widget _buildList(
    BuildContext context,
    AppLocalizations l10n,
    OrderState state,
  ) {
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
                style: FwText.cardTitle,
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

    if (state is! OrdersLoaded) return _buildEmpty(l10n);
    if (state.orders.isEmpty) return _buildEmpty(l10n);

    final isFarmer = _isFarmer();
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
        padding: const EdgeInsets.fromLTRB(
          20,
          12,
          20,
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
                ? () async {
                    // The farmer says why; the buyer is refunded.
                    final bloc = context.read<OrderBloc>();
                    final reason = await showDeclineReasonSheet(
                      context,
                      warning: l10n.farmerRejectRefundNote,
                      reasons: farmerDeclineReasons,
                    );
                    if (reason == null || bloc.isClosed) return;
                    bloc.add(
                      RejectOrder(
                        orderId: order.orderId,
                        farmerId: userId,
                        declineReason: reason,
                      ),
                    );
                  }
                : null,
          );
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

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
      child: FwContentWidth(
        child: Column(
          children: [
            _buildHeader(l10n),
            const OfflineBanner(),
            _buildFilters(l10n),
            Expanded(
              child: BlocBuilder<OrderBloc, OrderState>(
                builder: (context, state) => _buildList(context, l10n, state),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
