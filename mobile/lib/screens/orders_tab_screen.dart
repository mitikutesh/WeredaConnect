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
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_commitment_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// Orders tab (design canvas page M; no board of its own, a sibling of
/// the Market and My Crops boards). Page header (person · place, the 42 px
/// "Orders" with the role's sketch, bell and account), the status filter as
/// the Market's pills, then one [OrderCard] per order with the first one
/// raised, in the phone-width column at every window size.
///
/// The buyer has no Home tab: what waits for the buyer (pay now, pay
/// again, decide on transport, check in at the farm, confirm a delivery)
/// leads the list here as Action needed cards ([ActionNeededList]), most
/// urgent first. The farmer and the trucker read theirs on Home.
///
/// Reads from OrderBloc (provided at app root) via LoadUserOrders; the
/// server scopes the list to the caller (buyer, farmer or trucker).
/// A filter Home asks the Orders tab to open on (the Pending Orders tile
/// asks for [ordersFilterYourTurn]). The tab takes it once and clears it.
final ValueNotifier<String?> ordersFilterRequest = ValueNotifier<String?>(null);

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

  /// E-6: an Accept or Reject from a card is under way, from the tap until
  /// the list is loaded again. The list stays on screen meanwhile and when
  /// the answer fails (a snackbar says why), instead of the loading and
  /// error views of a reload.
  bool _cardAction = false;

  /// The list last loaded, shown during [_cardAction].
  OrdersLoaded? _lastLoaded;

  /// Counts the order reloads: the buyer's Action needed cards read again
  /// with the list.
  int _reloads = 0;

  /// How many Action needed cards the buyer's list leads with.
  static const int _buyerActionCards = 3;

  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _userRole = authState.role.name;
    }
    final requested = ordersFilterRequest.value;
    if (requested != null) {
      ordersFilterRequest.value = null;
      _selectedStatus = requested;
      _filterOrders(requested);
      _loadHeldOrders();
    } else {
      _loadOrders();
    }
    ordersFilterRequest.addListener(_onFilterRequest);
  }

  @override
  void dispose() {
    ordersFilterRequest.removeListener(_onFilterRequest);
    super.dispose();
  }

  /// Home asked again while this tab was kept alive (the phone's bar).
  void _onFilterRequest() {
    final requested = ordersFilterRequest.value;
    if (requested == null || !mounted) return;
    ordersFilterRequest.value = null;
    _applyFilter(requested);
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) return authState.userId;
    return null;
  }

  UserRole? _role() {
    final authState = context.read<AuthBloc>().state;
    return authState is AuthAuthenticated ? authState.role : null;
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

  bool _isTrucker() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.role == UserRole.trucker;
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
    setState(() {
      _selectedStatus = status;
      _cardAction = false;
    });
    _filterOrders(status);
  }

  void _filterOrders(String? status) {
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
    setState(() => _reloads++);
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
      sketchRole: authState is AuthAuthenticated ? authState.role : null,
      trailing: const HeaderActions(),
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
                    color: AppTheme.ink,
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
        // What Home's Pending Orders tile counts: the farmer's answer is
        // owed. "Pending" below is the order status (not paid yet).
        if (_isFarmer())
          FwFilterOption(
            value: ordersFilterYourTurn,
            label: l10n.ordersFilterWaitingForYou,
          ),
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
          // The one large drawing an empty state may carry.
          if (_role() case final role?) RoleSketch.emptyState(role: role),
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
    if (state is OrdersLoaded) _lastLoaded = state;
    final loaded =
        state is OrdersLoaded ? state : (_cardAction ? _lastLoaded : null);

    if (loaded == null && state is OrderLoading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (loaded == null && state is OrderError) {
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

    if (loaded == null || loaded.orders.isEmpty) return _buildEmpty(l10n);

    final isFarmer = _isFarmer();
    final isTrucker = _isTrucker();
    // The buyer's Action needed cards lead the unfiltered list (the buyer
    // has no Home tab to read them on).
    final userId = _getUserId() ?? '';
    final lead =
        _isBuyer() && _selectedStatus == null && userId.isNotEmpty ? 1 : 0;
    return RefreshIndicator(
      // Preserve the active status tab (not reset to "All"), and hold
      // the refresh spinner until the reload actually finishes.
      onRefresh: () async {
        _applyFilter(_selectedStatus);
        _loadHeldOrders();
        setState(() => _reloads++);
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
        itemCount: loaded.orders.length + lead,
        // No gap under the lead when nothing waits (it draws nothing).
        separatorBuilder: (_, index) => SizedBox(height: index < lead ? 0 : 12),
        itemBuilder: (context, index) {
          if (index < lead) {
            return Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: ActionNeededList(
                key: ValueKey('orders_action_needed_$_reloads'),
                userId: userId,
                role: UserRole.buyer,
                maxItems: _buyerActionCards,
                // The buyer may have paid or confirmed: read the list again.
                onRefresh: () {
                  _applyFilter(_selectedStatus);
                  _loadHeldOrders();
                },
              ),
            );
          }
          final order = loaded.orders[index - lead];
          return OrderCard(
            order: order,
            // The list leads with its first order, unless the buyer's
            // Action needed cards already do.
            raised: index == 0,
            isFarmerView: isFarmer,
            isTruckerView: isTrucker,
            // A cached `held` row is stale once the order moved on.
            underReview: _heldOrderIds.contains(order.orderId) &&
                orderHoldableStatuses.contains(order.status),
            acceptEnabled: _heldChecked,
            // Neither answer while one from a card runs.
            actionsEnabled: state is! OrderLoading,
            onTap: () => _openOrderDetail(order.orderId),
            onAccept: isFarmer
                ? () {
                    _cardAction = true;
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
                    if (reason == null || !context.mounted) return;
                    // FarmerRejectConfirmM: nothing is rejected before
                    // the farmer confirms.
                    final confirmed = await showRejectOrderConfirmSheet(
                      context,
                      reason: declineReasonLabel(l10n, reason),
                    );
                    if (!confirmed || bloc.isClosed) return;
                    _cardAction = true;
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
        if (state is OrdersLoaded) _cardAction = false;
        if (_cardAction && state is OrderError) {
          // E-6: the answer failed; the list stays, this says why.
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeErrorCode(state.code, l10n, message: state.message),
              ),
            ),
          );
        }
        if (_cardAction && (state is OrderAccepted || state is OrderRejected)) {
          // E-5: sent, or saved on the phone to send later.
          showSavedOrSent(
            context,
            sent: state is OrderAccepted
                ? l10n.orderAcceptedSent
                : l10n.orderRejectedSent,
          ).ignore();
        }
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
