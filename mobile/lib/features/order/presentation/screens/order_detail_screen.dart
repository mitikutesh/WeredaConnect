import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparty_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Full order detail screen with status timeline and role-based actions.
///
/// Loads order by ID via [LoadOrderById] event. Shows price breakdown,
/// a Stepper-based status timeline, and contextual action buttons
/// based on the user's role and the order's current status.
class OrderDetailScreen extends StatefulWidget {
  final String orderId;

  const OrderDetailScreen({required this.orderId, super.key});

  @override
  State<OrderDetailScreen> createState() => _OrderDetailScreenState();
}

class _OrderDetailScreenState extends State<OrderDetailScreen> {
  @override
  void initState() {
    super.initState();
    context.read<OrderBloc>().add(LoadOrderById(orderId: widget.orderId));
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.orderDetails)),
      body: BlocConsumer<OrderBloc, OrderState>(
        listener: (context, state) {
          if (state is PaymentSubmitted) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.paymentSubmittedSuccess)),
            );
            // Reload order
            context
                .read<OrderBloc>()
                .add(LoadOrderById(orderId: widget.orderId));
          } else if (state is OrderAccepted || state is OrderRejected) {
            Navigator.of(context).pop();
          } else if (state is OrderCancelled) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.orderCancelledSuccess)),
            );
            Navigator.of(context).pop();
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        builder: (context, state) {
          if (state is OrderLoading) {
            return const Center(child: CircularProgressIndicator());
          }
          if (state is SingleOrderLoaded) {
            return _OrderDetailBody(order: state.order);
          }
          if (state is OrderError) {
            return Center(child: Text(localizeErrorCode(state.code, l10n)));
          }
          return const SizedBox.shrink();
        },
      ),
    );
  }
}

class _OrderDetailBody extends StatelessWidget {
  final Order order;

  const _OrderDetailBody({required this.order});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final dateFormat = DateFormat.yMMMd().add_jm();
    final authState = context.read<AuthBloc>().state;
    final userId = authState is AuthAuthenticated ? authState.userId : '';
    final userRole =
        authState is AuthAuthenticated ? authState.role : UserRole.buyer;

    // Authorization: only buyer, farmer, or trucker of this order
    final isAuthorized = userId == order.buyerId ||
        userId == order.farmerId ||
        userId == order.truckerId;
    if (!isAuthorized) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(
            l10n.unauthorizedAccess,
            style: theme.textTheme.bodyLarge,
            textAlign: TextAlign.center,
          ),
        ),
      );
    }

    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // ── Order info card ─────────────────────────────────
            Card(
              margin: EdgeInsets.zero,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    _InfoRow(
                      label: l10n.farmer,
                      value: order.farmerName,
                    ),
                    const Divider(height: 16),
                    _InfoRow(
                      label: l10n.buyer,
                      value: order.buyerName,
                    ),
                    const Divider(height: 16),
                    _InfoRow(
                      label: CropType.labelFor(order.cropType, l10n),
                      value: l10n.quantityKg(
                        order.quantity.toStringAsFixed(0),
                      ),
                    ),
                    const Divider(height: 16),
                    _InfoRow(
                      label: l10n.statusCreated,
                      value: dateFormat.format(order.createdAt),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 24),

            // ── Price breakdown ──────────────────────────────────
            Text(
              l10n.priceBreakdown,
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 12),
            Card(
              margin: EdgeInsets.zero,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    _PriceRow(
                      label: l10n.cropCost,
                      amount: order.cropCost ?? order.totalPrice,
                      l10n: l10n,
                    ),
                    if (order.transportCost != null &&
                        order.transportCost! > 0) ...[
                      const Divider(height: 16),
                      _PriceRow(
                        label: l10n.transportCost,
                        amount: order.transportCost!,
                        l10n: l10n,
                      ),
                    ],
                    const Divider(height: 16),
                    _PriceRow(
                      label: l10n.platformCommission,
                      amount: order.commissionAmount ?? 0,
                      l10n: l10n,
                    ),
                    const Divider(height: 16),
                    _PriceRow(
                      label: l10n.orderTotal,
                      amount: order.orderTotal ?? order.totalPrice,
                      l10n: l10n,
                      isBold: true,
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 24),

            // ── Status timeline ──────────────────────────────────
            Text(
              l10n.orderTimeline,
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 12),
            _StatusTimeline(status: order.status, l10n: l10n),
            const SizedBox(height: 24),

            // ── Role-based actions ───────────────────────────────
            ..._buildActions(
              context,
              order: order,
              userId: userId,
              userRole: userRole,
              l10n: l10n,
            ),
          ],
        ),
      ),
    );
  }

  List<Widget> _buildActions(
    BuildContext context, {
    required Order order,
    required String userId,
    required UserRole userRole,
    required AppLocalizations l10n,
  }) {
    final widgets = <Widget>[];

    // Buyer + created → Pay Now
    if (userRole == UserRole.buyer && order.status == OrderStatus.created) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton(
            onPressed: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => PaymentInstructionsScreen(order: order),
                ),
              );
            },
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
            child: Text(l10n.payNow),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Farmer + funded → Accept / Reject
    if (userRole == UserRole.farmer && order.status == OrderStatus.funded) {
      widgets.add(
        Row(
          children: [
            Expanded(
              child: OutlinedButton(
                onPressed: () {
                  context.read<OrderBloc>().add(
                        RejectOrder(
                          orderId: order.orderId,
                          farmerId: userId,
                        ),
                      );
                },
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.statusError,
                  side: const BorderSide(color: AppTheme.statusError),
                  minimumSize: const Size.fromHeight(48),
                ),
                child: Text(l10n.rejectOrder),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: FilledButton(
                onPressed: () {
                  context.read<OrderBloc>().add(
                        AcceptOrder(
                          orderId: order.orderId,
                          farmerId: userId,
                        ),
                      );
                },
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                ),
                child: Text(l10n.acceptOrder),
              ),
            ),
          ],
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // The order's delivery, for every party: the create action while none
    // exists, then "Open delivery" (and the farmer's pickup QR) once it does.
    final createAction = _deliveryCreateAction(
      context,
      order: order,
      userId: userId,
      userRole: userRole,
      l10n: l10n,
    );
    if (createAction != null || _deliveryStatuses.contains(order.status)) {
      widgets.add(
        _OrderDeliveryActions(
          // A new order status looks the delivery up again.
          key: ValueKey('order-delivery-${order.orderId}-${order.status.name}'),
          order: order,
          userId: userId,
          userRole: userRole,
          createAction: createAction,
        ),
      );
    }

    // Buyer + delivered/settled → rate the farmer (and trucker if any)
    if (userRole == UserRole.buyer &&
        (order.status == OrderStatus.delivered ||
            order.status == OrderStatus.settled)) {
      widgets.add(
        _RateCounterpartiesSection(order: order, buyerId: userId),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Cancellable statuses — only the BUYER may cancel (the server rejects a
    // cancel from anyone else), so don't show a dead button to farmers/truckers.
    const cancellable = {
      OrderStatus.created,
      OrderStatus.pendingPayment,
      OrderStatus.funded,
    };
    if (cancellable.contains(order.status) && order.buyerId == userId) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: OutlinedButton(
            onPressed: () => _showCancelDialog(context, order, userId, l10n),
            style: OutlinedButton.styleFrom(
              foregroundColor: AppTheme.statusError,
              side: const BorderSide(color: AppTheme.statusError),
              minimumSize: const Size.fromHeight(48),
            ),
            child: Text(l10n.cancelOrder),
          ),
        ),
      );
    }

    return widgets;
  }

  /// Order statuses at which the order can have a delivery to open.
  static const _deliveryStatuses = {
    OrderStatus.accepted,
    OrderStatus.pickupConfirmed,
    OrderStatus.inTransit,
    OrderStatus.delivered,
    OrderStatus.disbursing,
    OrderStatus.settled,
    OrderStatus.disputed,
    OrderStatus.disputeResolved,
  };

  /// The create-delivery action [userRole] has on [order], or null.
  ///
  /// Only while the order is accepted and before any delivery exists
  /// ([_OrderDeliveryActions] hides it once one does):
  /// - trucker on a trip-matched order → Create Delivery. The order already
  ///   carries the matched trip, trucker, farmer and buyer (set when the buyer
  ///   placed the order against this trucker's trip), so the delivery comes
  ///   straight from the order's own fields instead of a separate form. This
  ///   is what lets it work on web, where there is no local Isar to resolve
  ///   the listing from.
  /// - buyer with no trucker/trip → "Collect it myself" (self-pickup). The
  ///   buyer confirms it at the farm by scanning the farmer's QR. Offered once
  ///   the farmer has accepted (so the crop can actually move) and only when
  ///   no trucker is arranging transport.
  ({IconData icon, String label, VoidCallback onPressed})?
      _deliveryCreateAction(
    BuildContext context, {
    required Order order,
    required String userId,
    required UserRole userRole,
    required AppLocalizations l10n,
  }) {
    if (order.status != OrderStatus.accepted) return null;
    if (userRole == UserRole.trucker && order.tripId != null) {
      return (
        icon: Icons.local_shipping,
        label: l10n.createDelivery,
        onPressed: () => _createTruckerDelivery(context, order, userId),
      );
    }
    if (userRole == UserRole.buyer &&
        order.truckerId == null &&
        order.tripId == null) {
      return (
        icon: Icons.storefront,
        label: l10n.orderCollectMyself,
        onPressed: () => context.read<DeliveryBloc>().add(
              CreateSelfPickupDelivery(
                orderId: order.orderId,
                listingId: order.listingId,
                farmerId: order.farmerId,
                farmerName: order.farmerName,
                buyerId: userId,
                buyerName: order.buyerName,
              ),
            ),
      );
    }
    return null;
  }

  /// Trucker materializes a delivery from an accepted, trip-matched order.
  ///
  /// Everything the delivery needs is already on the order — trip, trucker,
  /// farmer, buyer and listing were all bound when the buyer placed the order
  /// against this trip — so we dispatch straight to the [DeliveryBloc] rather
  /// than routing through a form that re-reads the listing from local Isar
  /// (which does not exist on web). The [DeliveryBloc] repository handles the
  /// backend call: enqueue-and-sync on mobile, a direct `/sync` POST on web.
  void _createTruckerDelivery(
    BuildContext context,
    Order order,
    String userId,
  ) {
    // truckerName is a required, non-empty field server-side. It is normally
    // carried on the order from the matched trip; fall back to the signed-in
    // trucker's own name if it is somehow absent.
    final authState = context.read<AuthBloc>().state;
    final authName = authState is AuthAuthenticated ? authState.userName : '';
    final truckerName = (order.truckerName?.trim().isNotEmpty ?? false)
        ? order.truckerName!.trim()
        : authName;

    context.read<DeliveryBloc>().add(
          CreateDelivery(
            tripId: order.tripId!,
            orderId: order.orderId,
            listingId: order.listingId,
            farmerId: order.farmerId,
            farmerName:
                order.farmerName.isNotEmpty ? order.farmerName : order.farmerId,
            truckerId: order.truckerId ?? userId,
            truckerName: truckerName,
            buyerId: order.buyerId,
            buyerName: order.buyerName,
            qrToken: const Uuid().v4(),
          ),
        );
  }

  void _showCancelDialog(
    BuildContext context,
    Order order,
    String userId,
    AppLocalizations l10n,
  ) {
    final reasonController = TextEditingController();
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.cancelOrder),
          content: TextField(
            controller: reasonController,
            decoration: InputDecoration(
              hintText: l10n.cancelReason,
              border: const OutlineInputBorder(),
            ),
            maxLines: 3,
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: Text(l10n.cancel),
            ),
            FilledButton(
              onPressed: () {
                context.read<OrderBloc>().add(
                      CancelOrder(
                        orderId: order.orderId,
                        userId: userId,
                        reason: reasonController.text.trim().isEmpty
                            ? null
                            : reasonController.text.trim(),
                      ),
                    );
                Navigator.pop(dialogContext);
              },
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.statusError,
              ),
              child: Text(l10n.cancelOrder),
            ),
          ],
        );
      },
    );
  }
}

/// The order's delivery on the order detail screen, for every party.
///
/// Looks up the order's live (not cancelled) delivery among the user's own
/// deliveries: Isar on mobile, `GET /deliveries` paged on web (the order row
/// does not carry the delivery id to the client). While there is none it shows
/// [createAction] (see `_deliveryCreateAction`). Once there is one, every
/// party gets "Open delivery", and the farmer of a pending delivery also gets
/// "Show QR Code": the code the trucker or the self-pickup buyer scans.
///
/// A create the server refuses (for example "The order already has an active
/// delivery" after a second tap) looks again and opens the delivery it
/// finds; any other failure shows the error. The shared [DeliveryBloc] is
/// only listened to while this widget's own create is in flight, so other
/// screens' delivery states never show here.
class _OrderDeliveryActions extends StatefulWidget {
  final Order order;
  final String userId;
  final UserRole userRole;
  final ({IconData icon, String label, VoidCallback onPressed})? createAction;

  const _OrderDeliveryActions({
    required this.order,
    required this.userId,
    required this.userRole,
    required this.createAction,
    super.key,
  });

  @override
  State<_OrderDeliveryActions> createState() => _OrderDeliveryActionsState();
}

class _OrderDeliveryActionsState extends State<_OrderDeliveryActions> {
  Delivery? _delivery;
  bool _looking = true;
  bool _creating = false;

  @override
  void initState() {
    super.initState();
    _lookUp();
  }

  Future<Delivery?> _findDelivery() async {
    try {
      return await context
          .read<DeliveryBloc>()
          .repository
          .findActiveDeliveryForOrder(
            widget.order.orderId,
            userId: widget.userId,
            role: widget.userRole.name,
            orderCreatedAt: widget.order.createdAt,
          );
    } on Object catch (e) {
      // Offline or failed: offer the create action; the server refuses a
      // duplicate and the error path below looks again.
      debugPrint('[OrderDetail] delivery lookup failed: $e');
    }
    return null;
  }

  Future<void> _lookUp() async {
    final found = await _findDelivery();
    if (!mounted) return;
    setState(() {
      _delivery = found;
      _looking = false;
    });
  }

  Future<void> _open(Delivery delivery) async {
    await Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (_) => DeliveryDetailScreen(
          delivery: delivery,
          currentUserId: widget.userId,
        ),
      ),
    );
    // It may have been cancelled or moved on meanwhile.
    if (mounted) await _lookUp();
  }

  void _showQr(Delivery delivery) {
    Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (context) => QrGenerateScreen(
          delivery: delivery,
          qrService: QrVerificationService(
            deviceKeyService: DeviceSigningKeyService(),
          ),
          repository: context.read<DeliveryBloc>().repository,
          userId: widget.userId,
        ),
      ),
    );
  }

  Future<void> _onCreateResult(DeliveryState state) async {
    setState(() => _creating = false);
    if (state is DeliveryCreated) {
      setState(() => _delivery = state.delivery);
      await _open(state.delivery);
      return;
    }
    if (state is! DeliveryError) return;
    final existing = await _findDelivery();
    if (!mounted) return;
    if (existing != null) {
      setState(() => _delivery = existing);
      await _open(existing);
      return;
    }
    final l10n = AppLocalizations.of(context)!;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(localizeDeliveryError(l10n, state.code))),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final delivery = _delivery;
    final create = widget.createAction;

    if (_looking) {
      return const Padding(
        padding: EdgeInsets.only(bottom: 12),
        child: SizedBox(
          height: 48,
          child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
        ),
      );
    }

    final buttons = <Widget>[];
    if (delivery != null) {
      if (widget.userId == delivery.farmerId &&
          delivery.status == DeliveryStatus.pending) {
        buttons.add(
          FilledButton.icon(
            key: const Key('order_delivery_show_qr'),
            onPressed: () => _showQr(delivery),
            icon: const Icon(Icons.qr_code),
            label: Text(l10n.deliveryShowQr),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        );
      }
      buttons.add(
        OutlinedButton.icon(
          key: const Key('order_delivery_open'),
          onPressed: () => _open(delivery),
          icon: const Icon(Icons.local_shipping_outlined),
          label: Text(l10n.orderOpenDelivery),
          style: OutlinedButton.styleFrom(
            minimumSize: const Size.fromHeight(48),
          ),
        ),
      );
    } else if (create != null) {
      buttons.add(
        BlocListener<DeliveryBloc, DeliveryState>(
          listenWhen: (_, curr) =>
              _creating && (curr is DeliveryCreated || curr is DeliveryError),
          listener: (_, state) => _onCreateResult(state),
          child: FilledButton.icon(
            key: const Key('order_delivery_create'),
            onPressed: _creating
                ? null
                : () {
                    setState(() => _creating = true);
                    create.onPressed();
                  },
            icon: _creating
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : Icon(create.icon),
            label: Text(create.label),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final button in buttons) ...[
          button,
          const SizedBox(height: 12),
        ],
      ],
    );
  }
}

/// Buyer-only section (delivered/settled orders) to rate the farmer and, when
/// the order was transported, the trucker. Tracks which counterparties have
/// already been rated (local check) and hides them once done.
class _RateCounterpartiesSection extends StatefulWidget {
  final Order order;
  final String buyerId;

  const _RateCounterpartiesSection({
    required this.order,
    required this.buyerId,
  });

  @override
  State<_RateCounterpartiesSection> createState() =>
      _RateCounterpartiesSectionState();
}

class _RateCounterpartiesSectionState
    extends State<_RateCounterpartiesSection> {
  bool _farmerRated = false;
  bool _truckerRated = false;
  bool _loading = true;

  bool get _hasTrucker =>
      widget.order.truckerId != null && widget.order.truckerId!.isNotEmpty;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final repo = context.read<RatingBloc>().repository;
    final order = widget.order;
    final farmerRated = order.farmerId.isEmpty ||
        await repo.hasRated(orderId: order.orderId, rateeId: order.farmerId);
    var truckerRated = true;
    if (_hasTrucker) {
      truckerRated = await repo.hasRated(
        orderId: order.orderId,
        rateeId: order.truckerId!,
      );
    }
    if (!mounted) return;
    setState(() {
      _farmerRated = farmerRated;
      _truckerRated = truckerRated;
      _loading = false;
    });
  }

  Future<void> _rate(String rateeId, String role, String name) async {
    final result = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => BlocProvider.value(
        value: context.read<RatingBloc>(),
        child: RateCounterpartyDialog(
          orderId: widget.order.orderId,
          raterId: widget.buyerId,
          rateeId: rateeId,
          rateeRole: role,
          rateeName: name,
        ),
      ),
    );
    if (result == true) await _load();
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final order = widget.order;

    // Everything already rated → confirmation line.
    if (_farmerRated && (!_hasTrucker || _truckerRated)) {
      return Row(
        children: [
          const Icon(
            Icons.check_circle,
            color: AppTheme.statusSuccess,
            size: 18,
          ),
          const SizedBox(width: 8),
          Text(l10n.alreadyRated, style: theme.textTheme.bodyMedium),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (!_farmerRated && order.farmerId.isNotEmpty) ...[
          OutlinedButton.icon(
            onPressed: () => _rate(order.farmerId, 'farmer', order.farmerName),
            icon: const Icon(Icons.star_outline),
            label: Text(l10n.rateFarmer),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
          const SizedBox(height: 12),
        ],
        if (_hasTrucker && !_truckerRated) ...[
          OutlinedButton.icon(
            onPressed: () => _rate(
              order.truckerId!,
              'trucker',
              order.truckerName ?? '',
            ),
            icon: const Icon(Icons.star_outline),
            label: Text(l10n.rateTrucker),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
          const SizedBox(height: 12),
        ],
      ],
    );
  }
}

/// Status timeline using Stepper widget.
class _StatusTimeline extends StatelessWidget {
  final OrderStatus status;
  final AppLocalizations l10n;

  const _StatusTimeline({required this.status, required this.l10n});

  @override
  Widget build(BuildContext context) {
    final steps = _timelineSteps;
    final currentIndex = steps.indexWhere((s) => s.status == status);

    return Stepper(
      currentStep: currentIndex >= 0 ? currentIndex : 0,
      physics: const NeverScrollableScrollPhysics(),
      controlsBuilder: (_, __) => const SizedBox.shrink(),
      steps: steps.map((step) {
        final stepIndex = steps.indexOf(step);
        final isActive = currentIndex >= 0 && stepIndex <= currentIndex;
        final isCurrent = stepIndex == currentIndex;

        return Step(
          title: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Flexible(
                child: Text(
                  step.label,
                  style: TextStyle(
                    fontWeight: isCurrent ? FontWeight.bold : FontWeight.normal,
                  ),
                ),
              ),
              const SizedBox(width: 6),
              // What this step means: hover on web/desktop, tap on touch.
              Tooltip(
                message: step.info,
                triggerMode: TooltipTriggerMode.tap,
                showDuration: const Duration(seconds: 8),
                margin: const EdgeInsets.symmetric(horizontal: 24),
                child: Icon(
                  Icons.info_outline,
                  size: 16,
                  color: Theme.of(context)
                      .colorScheme
                      .onSurface
                      .withValues(alpha: 0.45),
                ),
              ),
            ],
          ),
          content: const SizedBox.shrink(),
          isActive: isActive,
          state: isActive ? StepState.complete : StepState.indexed,
        );
      }).toList(),
    );
  }

  List<_TimelineStep> get _timelineSteps {
    // Show different flows based on terminal status
    if (status == OrderStatus.cancelled) {
      return [
        _TimelineStep(
          OrderStatus.created,
          l10n.statusCreated,
          l10n.statusCreatedInfo,
        ),
        _TimelineStep(
          OrderStatus.cancelled,
          l10n.orderCancelled,
          l10n.orderCancelledInfo,
        ),
      ];
    }
    if (status == OrderStatus.rejected) {
      return [
        _TimelineStep(
          OrderStatus.created,
          l10n.statusCreated,
          l10n.statusCreatedInfo,
        ),
        _TimelineStep(
          OrderStatus.rejected,
          l10n.orderRejected,
          l10n.orderRejectedInfo,
        ),
      ];
    }
    if (status == OrderStatus.paymentFailed) {
      return [
        _TimelineStep(
          OrderStatus.created,
          l10n.statusCreated,
          l10n.statusCreatedInfo,
        ),
        _TimelineStep(
          OrderStatus.paymentSubmitted,
          l10n.statusPaymentSubmitted,
          l10n.statusPaymentSubmittedInfo,
        ),
        _TimelineStep(
          OrderStatus.paymentFailed,
          l10n.statusPaymentFailed,
          l10n.statusPaymentFailedInfo,
        ),
      ];
    }
    if (status == OrderStatus.disputed ||
        status == OrderStatus.disputeResolved) {
      return [
        _TimelineStep(
          OrderStatus.created,
          l10n.statusCreated,
          l10n.statusCreatedInfo,
        ),
        _TimelineStep(
          OrderStatus.funded,
          l10n.statusFunded,
          l10n.statusFundedInfo,
        ),
        _TimelineStep(
          OrderStatus.delivered,
          l10n.statusDelivered,
          l10n.statusDeliveredInfo,
        ),
        _TimelineStep(
          OrderStatus.disputed,
          l10n.statusDisputed,
          l10n.statusDisputedInfo,
        ),
        _TimelineStep(
          OrderStatus.disputeResolved,
          l10n.statusDisputeResolved,
          l10n.statusDisputeResolvedInfo,
        ),
      ];
    }

    // Happy path. Includes pendingPayment and disbursing so an order in either
    // state highlights the right step instead of collapsing to step 0 (the
    // Stepper falls back to index 0 when the status isn't in this list).
    return [
      _TimelineStep(
        OrderStatus.created,
        l10n.statusCreated,
        l10n.statusCreatedInfo,
      ),
      _TimelineStep(
        OrderStatus.pendingPayment,
        l10n.statusPendingPayment,
        l10n.statusPendingPaymentInfo,
      ),
      _TimelineStep(
        OrderStatus.paymentSubmitted,
        l10n.statusPaymentSubmitted,
        l10n.statusPaymentSubmittedInfo,
      ),
      _TimelineStep(
        OrderStatus.funded,
        l10n.statusFunded,
        l10n.statusFundedInfo,
      ),
      _TimelineStep(
        OrderStatus.accepted,
        l10n.orderAccepted,
        l10n.orderAcceptedInfo,
      ),
      _TimelineStep(
        OrderStatus.pickupConfirmed,
        l10n.statusPickupConfirmed,
        l10n.statusPickupConfirmedInfo,
      ),
      _TimelineStep(
        OrderStatus.inTransit,
        l10n.statusInTransit,
        l10n.statusInTransitInfo,
      ),
      _TimelineStep(
        OrderStatus.delivered,
        l10n.statusDelivered,
        l10n.statusDeliveredInfo,
      ),
      _TimelineStep(
        OrderStatus.disbursing,
        l10n.statusDisbursing,
        l10n.statusDisbursingInfo,
      ),
      _TimelineStep(
        OrderStatus.settled,
        l10n.statusSettled,
        l10n.statusSettledInfo,
      ),
    ];
  }
}

class _TimelineStep {
  final OrderStatus status;
  final String label;

  /// Plain-language explanation of what the step means, shown in a tooltip.
  final String info;

  const _TimelineStep(this.status, this.label, this.info);
}

class _InfoRow extends StatelessWidget {
  final String label;
  final String value;

  const _InfoRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
          ),
        ),
        Flexible(
          child: Text(
            value,
            style: theme.textTheme.bodyMedium
                ?.copyWith(fontWeight: FontWeight.w500),
            textAlign: TextAlign.end,
          ),
        ),
      ],
    );
  }
}

class _PriceRow extends StatelessWidget {
  final String label;
  final double amount;
  final AppLocalizations l10n;
  final bool isBold;

  const _PriceRow({
    required this.label,
    required this.amount,
    required this.l10n,
    this.isBold = false,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final style = isBold
        ? theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold)
        : theme.textTheme.bodyMedium;

    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(label, style: style),
        Text(
          l10n.etbCurrency(amount.toStringAsFixed(2)),
          style: style,
        ),
      ],
    );
  }
}
