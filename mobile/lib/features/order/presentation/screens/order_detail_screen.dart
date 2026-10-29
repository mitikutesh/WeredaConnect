import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
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
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/presentation/open_live_trip.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_checkout_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_commitment_section.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparties_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/contact_actions_sheet.dart';
import 'package:wereda_connect/shared/widgets/contact_locked_note.dart';

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
  /// ESC-48: the order is read from the server at most once per screen to
  /// fetch the parties' phones of an open order the device has none for.
  bool _contactPullTried = false;

  /// FRD-06: the order's escrow is held for review.
  bool _underReview = false;

  /// Whether a hold check has answered (held, not held, or failed). Until
  /// then the farmer's Accept stays disabled, so it never shows enabled on
  /// an order whose escrow turns out to be held.
  bool _holdChecked = false;

  /// Only the newest hold check may change the screen: a reload (the
  /// contact pull, a payment) starts another one.
  int _holdCheck = 0;

  @override
  void initState() {
    super.initState();
    context.read<OrderBloc>().add(LoadOrderById(orderId: widget.orderId));
    // A bloc that already holds this order may emit nothing new for the
    // listener: check that order once the first frame is up.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final current = context.read<OrderBloc>().state;
      if (current is SingleOrderLoaded &&
          current.order.orderId == widget.orderId) {
        _checkHold(current.order);
      }
    });
  }

  /// ESC-48: an open order without the parties' phones on this device (the
  /// farmer accepted on this device, or the last pull predates the accept):
  /// read it from the server once. Mobile only; web reads the server already.
  /// Message works meanwhile.
  Future<void> _pullForContact(Order order) async {
    if (_contactPullTried) return;
    if (!isOrderContactOpen(order)) return;
    if (order.contactOpen != null &&
        (order.buyerPhone != null || order.farmerPhone != null)) {
      return;
    }
    _contactPullTried = true;
    final MarketplaceSync? sync;
    try {
      sync = context.read<MarketplaceSync?>();
    } on Object {
      return;
    }
    if (sync == null) return;
    final bloc = context.read<OrderBloc>();
    try {
      final server = await sync.pullOrder(order.orderId);
      if (server != null && mounted && !bloc.isClosed) {
        bloc.add(LoadOrderById(orderId: widget.orderId));
      }
    } on Object {
      // Offline: Call and Copy wait for the next pull.
    }
  }

  /// FRD-06: read whether the order's escrow is held; the parties then see
  /// "Under Review" and lose the actions the server refuses while it is. A
  /// failed read, or no escrow side above this screen, counts as not held.
  Future<void> _checkHold(Order order) async {
    final check = ++_holdCheck;
    var held = false;
    if (orderHoldableStatuses.contains(order.status)) {
      BaseEscrowRepository? repository;
      try {
        repository = context.read<EscrowBloc>().repository;
      } on Object {
        repository = null;
      }
      if (repository != null) {
        held = await isOrderEscrowHeld(repository, order.orderId);
      }
    }
    if (!mounted || check != _holdCheck) return;
    setState(() {
      _underReview = held;
      _holdChecked = true;
    });
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
          } else if (state is TruckerResponded) {
            // ESC-15: stay on the order, which now shows the next step
            // (Create Delivery after an accept).
            context
                .read<OrderBloc>()
                .add(LoadOrderById(orderId: widget.orderId));
          } else if (state is OrderCancelled) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.orderCancelledSuccess)),
            );
            Navigator.of(context).pop();
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          } else if (state is SingleOrderLoaded) {
            _pullForContact(state.order);
            _checkHold(state.order);
          }
        },
        builder: (context, state) {
          if (state is OrderLoading) {
            return const Center(child: CircularProgressIndicator());
          }
          if (state is SingleOrderLoaded) {
            return _OrderDetailBody(
              order: state.order,
              underReview: _underReview,
              holdChecked: _holdChecked,
            );
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

  /// The order's escrow is held for review (FRD-06).
  final bool underReview;

  /// Whether the hold check has answered; the farmer's Accept waits for it.
  final bool holdChecked;

  const _OrderDetailBody({
    required this.order,
    this.underReview = false,
    this.holdChecked = true,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final clock = clockFormatOf(context);
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
            // FRD-06: the money is frozen until an admin decides.
            if (underReview) ...[
              const EscrowStatusBadge(status: PaymentStatus.held, large: true),
              const SizedBox(height: 24),
            ],

            // What, with whom, and the viewer's money (Buyer · Order detail
            // and Farmer · Answer a new order boards).
            _OrderSummaryHero(order: order, userId: userId),
            const SizedBox(height: 16),

            // ── Order info card ─────────────────────────────────
            Card(
              margin: EdgeInsets.zero,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    _InfoRow(
                      label: l10n.farmer,
                      value: partyDisplayName(order.farmerName, l10n),
                    ),
                    const Divider(height: 16),
                    _InfoRow(
                      label: l10n.buyer,
                      value: partyDisplayName(order.buyerName, l10n),
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
                      value: clock.dateTime(order.createdAt),
                    ),
                    // ESC-06: when the crops must be collected by, for all
                    // three parties of an accepted order.
                    if (order.status == OrderStatus.accepted &&
                        order.pickupDeadlineAt != null) ...[
                      const Divider(height: 16),
                      _InfoRow(
                        label: l10n.orderPickupDeadline,
                        clock: clock.deadline(
                          order.pickupDeadlineAt!,
                          date: DateFormat.yMMMd(),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
            const SizedBox(height: 24),

            // ── Who has agreed (ESC-15) ──────────────────────────
            OrderCommitmentSection(order: order, userId: userId),

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

            // ── Contact (ESC-48) ─────────────────────────────────
            _OrderContactSection(order: order, userId: userId),

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

    // The order's buyer pays: Pay Now (created, pending_payment), Pay again
    // (payment_failed), Continue payment (payment_submitted: the live
    // Telebirr checkout, ESC-44; with mock payments it finds the order
    // funded). Farmers and truckers never see these.
    final isOrderBuyer = userRole == UserRole.buyer && order.buyerId == userId;
    final payLabel = switch (order.status) {
      OrderStatus.created || OrderStatus.pendingPayment => l10n.payNow,
      OrderStatus.paymentFailed => l10n.payAgain,
      OrderStatus.paymentSubmitted => l10n.continuePayment,
      _ => null,
    };
    if (isOrderBuyer && payLabel != null) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton(
            onPressed: () async {
              final orderBloc = context.read<OrderBloc>();
              await Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => order.status == OrderStatus.paymentSubmitted
                      ? OrderCheckoutScreen(order: order)
                      : PaymentInstructionsScreen(order: order),
                ),
              );
              // The payment may have moved the order on.
              if (!orderBloc.isClosed) {
                orderBloc.add(LoadOrderById(orderId: order.orderId));
              }
            },
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
            child: Text(payLabel),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Farmer + funded and not answered yet → Accept / Reject (ESC-15: a
    // farmer who accepted a trip order waits for the trucker). Under review
    // the server refuses the accept (FRD-03), so only Reject is offered;
    // Accept stays disabled until the hold check has answered.
    if (userRole == UserRole.farmer && canFarmerRespond(order, userId)) {
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
            if (!underReview) ...[
              const SizedBox(width: 12),
              Expanded(
                child: FilledButton(
                  onPressed: holdChecked
                      ? () {
                          context.read<OrderBloc>().add(
                                AcceptOrder(
                                  orderId: order.orderId,
                                  farmerId: userId,
                                ),
                              );
                        }
                      : null,
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                  ),
                  child: Text(l10n.acceptOrder),
                ),
              ),
            ],
          ],
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // ESC-15: the trucker answers the transport request once the farmer
    // accepted. Like the farmer's, the accept waits for the hold check and is
    // not offered while the money is under review.
    if (canTruckerRespond(order, userId)) {
      widgets.add(
        Row(
          children: [
            Expanded(
              child: OutlinedButton(
                onPressed: () => _declineTransport(context, order, userId),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.statusError,
                  side: const BorderSide(color: AppTheme.statusError),
                  minimumSize: const Size.fromHeight(48),
                ),
                child: Text(l10n.truckerDeclineRequest),
              ),
            ),
            if (!underReview) ...[
              const SizedBox(width: 12),
              Expanded(
                child: FilledButton(
                  onPressed: holdChecked
                      ? () => context.read<OrderBloc>().add(
                            TruckerAcceptOrder(
                              orderId: order.orderId,
                              truckerId: userId,
                            ),
                          )
                      : null,
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                  ),
                  child: Text(l10n.truckerAcceptRequest),
                ),
              ),
            ],
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

    // Buyer + delivered, disbursing or settled → rate the farmer (and trucker
    // if any). ESC-09: the order moves on to disbursing and settled after the
    // money; disbursing can last days while a manual payout waits.
    if (userRole == UserRole.buyer &&
        deliveredOrderStatuses.contains(order.status)) {
      widgets.add(
        RateCounterpartiesSection(
          orderId: order.orderId,
          buyerId: userId,
          farmerId: order.farmerId,
          farmerName: order.farmerName,
          truckerId: order.truckerId,
          truckerName: order.truckerName,
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // ESC-06: nobody collected the crops by the pickup deadline, a transport
    // failure. The buyer may cancel for a full refund; the farmer and the
    // trucker are told the buyer can. Not while the escrow is under review:
    // the server refuses that cancel (ORDER_CANCEL_NOT_ALLOWED).
    final transportFailed = !underReview &&
        isTransportFailureCancelOpen(
          order,
          DateTime.now().toUtc(),
        );
    if (transportFailed) {
      widgets.add(
        _TransportFailureNotice(
          text: order.buyerId == userId
              ? l10n.orderTransportFailedBuyerHint
              : l10n.orderTransportFailedPartyHint,
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Who may cancel (ESC-15, order-lifecycle.md 2.0.0): the buyer before
    // paying and after the transport fell through, not while the farmer and
    // the trucker decide; an accepted order only after a transport failure
    // (ESC-06). The farmer after the transport fell through (no penalty) or
    // before pickup (counts against them). Not while the money is under
    // review, which the server refuses.
    final buyerMayCancel = order.buyerId == userId &&
        (transportFailed ||
            (order.status != OrderStatus.accepted &&
                (order.status != OrderStatus.awaitingTransport ||
                    !underReview) &&
                canCancelOrder(order, userId, DateTime.now().toUtc())));
    final farmerMayCancel = order.farmerId == userId &&
        !underReview &&
        canCancelOrder(order, userId, DateTime.now().toUtc());
    if (buyerMayCancel || farmerMayCancel) {
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
            child: Text(
              farmerMayCancel && order.status == OrderStatus.awaitingTransport
                  ? l10n.farmerCancelWithoutPenalty
                  : l10n.cancelOrder,
            ),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // ESC-15: the committed trucker of an order not picked up yet may drop
    // out; the order then waits for the buyer.
    if (!underReview && canTruckerDropOut(order, userId)) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: OutlinedButton(
            onPressed: () => _dropOut(context, order, userId),
            style: OutlinedButton.styleFrom(
              foregroundColor: AppTheme.statusError,
              side: const BorderSide(color: AppTheme.statusError),
              minimumSize: const Size.fromHeight(48),
            ),
            child: Text(l10n.truckerDropOut),
          ),
        ),
      );
    }

    return widgets;
  }

  /// ESC-15: the trucker declines the transport request, with a reason.
  Future<void> _declineTransport(
    BuildContext context,
    Order order,
    String userId,
  ) async {
    final bloc = context.read<OrderBloc>();
    final reason = await showDeclineReasonSheet(context);
    if (reason == null || bloc.isClosed) return;
    bloc.add(
      TruckerDeclineOrder(
        orderId: order.orderId,
        truckerId: userId,
        declineReason: reason,
      ),
    );
  }

  /// ESC-15: the committed trucker drops out before pickup (fresh sign-in).
  Future<void> _dropOut(
    BuildContext context,
    Order order,
    String userId,
  ) async {
    final l10n = AppLocalizations.of(context)!;
    final bloc = context.read<OrderBloc>();
    final reason = await showDeclineReasonSheet(
      context,
      warning: l10n.truckerDropOutWarning,
    );
    if (reason == null || bloc.isClosed) return;
    bloc.add(
      TruckerDeclineOrder(
        orderId: order.orderId,
        truckerId: userId,
        declineReason: reason,
      ),
    );
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
        final reasonField = TextField(
          controller: reasonController,
          decoration: InputDecoration(
            hintText: l10n.cancelReason,
            border: const OutlineInputBorder(),
          ),
          maxLines: 3,
        );
        return AlertDialog(
          title: Text(l10n.cancelOrder),
          // ESC-06 / ESC-15: what the cancel means for the money and for the
          // farmer's record.
          content: switch (_cancelDialogBody(order, userId, l10n)) {
            final String body => Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(body),
                  const SizedBox(height: 12),
                  reasonField,
                ],
              ),
            null => reasonField,
          },
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

/// The cancel dialog's explanation for [userId] on [order], or null.
String? _cancelDialogBody(Order order, String userId, AppLocalizations l10n) {
  final isFarmer = userId == order.farmerId;
  return switch (order.status) {
    OrderStatus.accepted => isFarmer
        ? l10n.farmerCancelCommittedWarning
        : l10n.cancelOrderTransportFailedBody,
    OrderStatus.awaitingTransport => isFarmer
        ? l10n.farmerCancelAwaitingTransportBody
        : l10n.cancelOrderAwaitingTransportBody,
    _ => null,
  };
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
/// The order at a glance: crop and quantity, the other party, the status
/// and the viewer's own amount (the farmer's crop money, the buyer's total,
/// the trucker's transport pay), with whether it is held in escrow.
class _OrderSummaryHero extends StatelessWidget {
  final Order order;
  final String userId;

  const _OrderSummaryHero({required this.order, required this.userId});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final isFarmer = userId == order.farmerId;
    final isTrucker = !isFarmer && userId == order.truckerId;
    final farmer = partyDisplayName(order.farmerName, l10n);
    final buyer = partyDisplayName(order.buyerName, l10n);

    final (String subtitle, String amountLabel, double amount) = isFarmer
        ? (
            '${l10n.buyer}: $buyer',
            l10n.orderYouReceive,
            order.cropCost ?? order.totalPrice,
          )
        : isTrucker
            ? (
                '$farmer → $buyer',
                l10n.orderYourTransportPay,
                order.transportCost ?? 0,
              )
            : (
                l10n.liveTripFrom(farmer),
                l10n.orderTotal,
                order.orderTotal ?? order.totalPrice,
              );
    final held = escrowHeldOrderStatuses.contains(order.status);

    return Container(
      key: const Key('order_summary_hero'),
      width: double.infinity,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: AppTheme.fieldGreen,
        borderRadius: BorderRadius.circular(24),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Text(
                  '${CropType.labelFor(order.cropType, l10n)} · '
                  '${l10n.quantityKg(order.quantity.toStringAsFixed(0))}',
                  style: const TextStyle(
                    fontSize: 20,
                    fontWeight: FontWeight.w800,
                    color: Colors.white,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              StatusChip(
                label: orderStatusLabel(order.status, l10n),
                kind: orderStatusKind(order.status),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            subtitle,
            style: const TextStyle(fontSize: 13, color: Color(0xFFCFE3D6)),
          ),
          const SizedBox(height: 16),
          Text(
            amountLabel,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: Color(0xFFCFE3D6),
            ),
          ),
          const SizedBox(height: 2),
          Text(
            l10n.etbCurrency(amount.toStringAsFixed(0)),
            style: const TextStyle(
              fontSize: 30,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.6,
              color: Colors.white,
            ),
          ),
          if (held) ...[
            const SizedBox(height: 4),
            Text(
              l10n.escrowStatusEscrowed,
              style: const TextStyle(fontSize: 13, color: Color(0xFFCFE3D6)),
            ),
          ],
        ],
      ),
    );
  }
}

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
      // LT-11: the buyer and the farmer follow the truck from pickup on
      // (the trucker has their own trip mode, never this map).
      if (liveTripViewerOf(delivery, widget.userId) != null &&
          hasLiveTrip(delivery) &&
          !isWaitingForPickup(delivery)) {
        buttons.add(
          FilledButton.icon(
            key: const Key('order_delivery_live_trip'),
            onPressed: () async {
              await openLiveTrip(context, delivery, widget.userId);
              if (mounted) await _lookUp();
            },
            icon: const Icon(Icons.map_outlined),
            label: Text(l10n.liveTripTitle),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        );
      }
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

    // LT-11: before pickup the buyer and the farmer of a trucked order learn
    // where the map will be.
    final order = widget.order;
    final waiting = delivery == null
        ? order.status == OrderStatus.accepted && orderHasTrip(order)
        : isWaitingForPickup(delivery);
    final party = widget.userId.isNotEmpty &&
        (widget.userId == order.buyerId || widget.userId == order.farmerId);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final button in buttons) ...[
          button,
          const SizedBox(height: 12),
        ],
        if (waiting && party) ...[
          Row(
            key: const Key('order_live_trip_hint'),
            children: [
              const Icon(
                Icons.map_outlined,
                size: 18,
                color: AppTheme.textMuted,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  l10n.liveTripAfterPickupHint,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            ],
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
    // A status this build does not know: one "Updating" step, no guesses.
    if (status == OrderStatus.unknown) {
      return [
        _TimelineStep(
          OrderStatus.unknown,
          l10n.orderStatusUpdating,
          l10n.orderStatusUpdatingInfo,
        ),
      ];
    }
    // ESC-15: the money is held and the transport fell through.
    if (status == OrderStatus.awaitingTransport) {
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
          OrderStatus.awaitingTransport,
          l10n.statusAwaitingTransport,
          l10n.statusAwaitingTransportInfo,
        ),
      ];
    }
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

/// ESC-48: the parties of an order reach each other here, and only here, once
/// the farmer accepted the paid order and until the money goes back
/// (order-lifecycle.md 1.18.0). Each counterparty gets Call and Copy (when the
/// server sent the number) and Message (an order thread). Before that, a note
/// says when contact opens; after a cancel, reject or refund nothing shows.
class _OrderContactSection extends StatelessWidget {
  final Order order;
  final String userId;

  const _OrderContactSection({required this.order, required this.userId});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    // ESC-15: the trucker joins the contact only once committed to the
    // transport, and leaves it after declining or dropping out.
    final truckerCommitted =
        truckerResponseOf(order) == CommitmentResponse.accepted;
    final viewerIsTrucker = userId.isNotEmpty &&
        userId == order.truckerId &&
        userId != order.buyerId &&
        userId != order.farmerId;
    if (viewerIsTrucker && !truckerCommitted) return const SizedBox.shrink();

    if (!isOrderContactOpen(order)) {
      if (!contactNotYetOpenOrderStatuses.contains(order.status)) {
        return const SizedBox.shrink();
      }
      return Padding(
        padding: const EdgeInsets.only(bottom: 24),
        child: ContactLockedNote(
          text: order.status == OrderStatus.funded
              ? l10n.contactAfterAcceptNote
              : l10n.contactAfterEscrowNote,
        ),
      );
    }

    final truckerId = order.truckerId ?? '';
    final parties = <({String id, String name, String role, String label})>[
      if (order.buyerId != userId)
        (
          id: order.buyerId,
          name: order.buyerName,
          role: l10n.buyer,
          label: l10n.contactBuyer,
        ),
      if (order.farmerId != userId)
        (
          id: order.farmerId,
          name: order.farmerName,
          role: l10n.farmer,
          label: l10n.contactFarmer,
        ),
      if (truckerId.isNotEmpty && truckerId != userId && truckerCommitted)
        (
          id: truckerId,
          name: order.truckerName ?? '',
          role: l10n.trucker,
          label: l10n.contactTrucker,
        ),
    ];
    if (parties.isEmpty) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(bottom: 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            l10n.contact,
            style: theme.textTheme.titleMedium
                ?.copyWith(fontWeight: FontWeight.bold),
          ),
          for (final party in parties) ...[
            const SizedBox(height: 12),
            OutlinedButton.icon(
              onPressed: () {
                final navigator = Navigator.of(context);
                final name = party.name.isNotEmpty
                    ? partyDisplayName(party.name, l10n)
                    : party.role;
                showContactActionsSheet(
                  context,
                  name: name,
                  roleLabel: party.role,
                  phone: orderPartyPhone(order, party.id),
                  onMessage: () => navigator.push(
                    MaterialPageRoute<void>(
                      builder: (_) => ConversationScreen(
                        contextType: 'order',
                        contextId: order.orderId,
                        counterpartId: party.id,
                        counterpartName: party.name,
                      ),
                    ),
                  ),
                );
              },
              icon: const Icon(Icons.chat_outlined),
              label: Text(party.label),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _TimelineStep {
  final OrderStatus status;
  final String label;

  /// Plain-language explanation of what the step means, shown in a tooltip.
  final String info;

  const _TimelineStep(this.status, this.label, this.info);
}

/// ESC-06: the accepted order was not collected by its pickup deadline.
class _TransportFailureNotice extends StatelessWidget {
  final String text;

  const _TransportFailureNotice({required this.text});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border.all(color: AppTheme.statusWarning),
        borderRadius: BorderRadius.circular(8),
        color: AppTheme.statusWarningContainer,
      ),
      child: Row(
        children: [
          const Icon(
            Icons.warning_amber,
            color: AppTheme.onStatusWarningContainer,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              style: const TextStyle(color: AppTheme.onStatusWarningContainer),
            ),
          ),
        ],
      ),
    );
  }
}

class _InfoRow extends StatelessWidget {
  final String label;
  final String value;

  /// A deadline value, shown with its small 24-hour time (LT-03); replaces
  /// [value].
  final ClockLabel? clock;

  const _InfoRow({required this.label, this.value = '', this.clock});

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
          child: ClockText(
            clock ?? ClockLabel.text(value),
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
