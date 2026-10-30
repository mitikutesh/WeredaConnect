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
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
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
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Full order detail screen (the Buyer · Order detail and Farmer · Answer a
/// new order boards): the order named in the header with its status, who
/// has agreed, the timeline, the contact card, the role's actions, and the
/// answer the viewer owes pinned at the foot.
///
/// Loads order by ID via [LoadOrderById] event.
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
      // The order's own header names it (the boards' light header) with
      // Back beside it; the body is a Scaffold of its own so the answer the
      // viewer owes can sit pinned at the foot.
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
          if (state is SingleOrderLoaded) {
            return _OrderDetailBody(
              order: state.order,
              underReview: _underReview,
              holdChecked: _holdChecked,
            );
          }
          return SafeArea(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const FwDetailHeader(),
                Expanded(
                  child: Center(
                    child: state is OrderError
                        ? Padding(
                            padding: const EdgeInsets.all(24),
                            child: Text(
                              localizeErrorCode(state.code, l10n),
                              textAlign: TextAlign.center,
                            ),
                          )
                        : state is OrderLoading
                            ? const CircularProgressIndicator()
                            : const SizedBox.shrink(),
                  ),
                ),
              ],
            ),
          );
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

    final isFarmer = userId == order.farmerId;
    final isBuyer = userId == order.buyerId;
    final isTrucker = !isFarmer && !isBuyer && userId == order.truckerId;
    final farmerAnswering =
        userRole == UserRole.farmer && canFarmerRespond(order, userId);
    final answerDeadline = commitmentDeadlineFor(order, userId);
    // LT-11: before pickup the buyer and the farmer of a trucked order learn
    // where the map will be.
    final mapNote = !isTrucker &&
        orderHasTrip(order) &&
        (order.status == OrderStatus.funded ||
            order.status == OrderStatus.accepted);

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: SingleChildScrollView(
          child: FwContentWidth(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // What, with whom and the status (Buyer · Order detail and
                // Farmer · Answer a new order boards).
                _OrderHeader(
                  order: order,
                  userId: userId,
                  farmerAnswering: farmerAnswering,
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      // FRD-06: the money is frozen until an admin decides.
                      if (underReview) ...[
                        const EscrowStatusBadge(
                          status: PaymentStatus.held,
                          large: true,
                        ),
                        const SizedBox(height: 12),
                      ],

                      // The answer the viewer owes (the farmer's on a new
                      // order, the trucker's on a transport request), by when.
                      if (answerDeadline != null &&
                          order.status == OrderStatus.funded) ...[
                        FwNoteBox.cream(
                          icon: Icons.schedule,
                          child: ClockText(
                            clock.deadline(
                              answerDeadline,
                              date: DateFormat.MMMd(),
                              sentence: l10n.commitmentAnswerBy,
                            ),
                            style: const TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                        const SizedBox(height: 12),
                      ],

                      // The farmer's money and the order's facts; the
                      // trucker's pay. The buyer's total closes the page.
                      if (isFarmer) ...[
                        _FarmerReceiveCard(order: order),
                        const SizedBox(height: 12),
                      ] else if (isTrucker) ...[
                        _TruckerPayCard(order: order),
                        const SizedBox(height: 12),
                      ],

                      // ESC-06: when the crops must be collected by, for all
                      // three parties of an accepted order.
                      if (order.status == OrderStatus.accepted &&
                          order.pickupDeadlineAt != null) ...[
                        FwNoteBox.cream(
                          icon: Icons.schedule,
                          child: Row(
                            children: [
                              Text(l10n.orderPickupDeadline),
                              const SizedBox(width: 8),
                              Expanded(
                                child: ClockText(
                                  clock.deadline(
                                    order.pickupDeadlineAt!,
                                    date: DateFormat.yMMMd(),
                                  ),
                                  textAlign: TextAlign.end,
                                ),
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 12),
                      ],

                      // ── Who has agreed (ESC-15) ──────────────────────
                      OrderCommitmentSection(
                        order: order,
                        userId: userId,
                        hideViewerDeadline: answerDeadline != null,
                      ),

                      // Farmer · Answer a new order: what accepting and
                      // rejecting do, above the pinned buttons.
                      if (farmerAnswering) ...[
                        Text(
                          l10n.farmerAnswerNote,
                          style: const TextStyle(
                            fontSize: 13,
                            height: 1.5,
                            color: AppTheme.mutedSteel,
                          ),
                        ),
                        const SizedBox(height: 12),
                      ],

                      // ── Status timeline ──────────────────────────────
                      _StatusTimeline(
                        status: order.status,
                        tripOrder: orderHasTrip(order),
                        l10n: l10n,
                      ),
                      const SizedBox(height: 12),

                      // ── Contact (ESC-48) ─────────────────────────────
                      _OrderContactSection(order: order, userId: userId),

                      if (mapNote) ...[
                        FwNoteBox.cream(
                          key: const Key('order_live_trip_hint'),
                          icon: Icons.place_outlined,
                          child: Text(l10n.liveTripAfterPickupHint),
                        ),
                        const SizedBox(height: 12),
                      ],

                      // ── Role-based actions ───────────────────────────
                      ..._buildActions(
                        context,
                        order: order,
                        userId: userId,
                        userRole: userRole,
                        l10n: l10n,
                      ),

                      // The buyer's money: held in escrow, or the total.
                      if (isBuyer) _BuyerTotalRow(order: order),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
      bottomNavigationBar: _answerActions(
        context,
        order: order,
        userId: userId,
        userRole: userRole,
        l10n: l10n,
      ),
    );
  }

  /// The answer the viewer owes, pinned at the foot: the buyer pays (Pay
  /// Now on created and pending_payment, Pay again on payment_failed,
  /// Continue payment on payment_submitted: the live Telebirr checkout,
  /// ESC-44), the farmer accepts or rejects a new order, the trucker
  /// accepts or declines the transport request once the farmer accepted
  /// (ESC-15). Farmers and truckers never see the payment actions.
  Widget? _answerActions(
    BuildContext context, {
    required Order order,
    required String userId,
    required UserRole userRole,
    required AppLocalizations l10n,
  }) {
    final isOrderBuyer = userRole == UserRole.buyer && order.buyerId == userId;
    final payLabel = switch (order.status) {
      OrderStatus.created || OrderStatus.pendingPayment => l10n.payNow,
      OrderStatus.paymentFailed => l10n.payAgain,
      OrderStatus.paymentSubmitted => l10n.continuePayment,
      _ => null,
    };
    if (isOrderBuyer && payLabel != null) {
      return FwBottomActions(
        children: [
          FilledButton(
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
            style: _primaryStyle,
            child: Text(payLabel),
          ),
        ],
      );
    }

    // Farmer + funded and not answered yet → Reject / Accept (ESC-15: a
    // farmer who accepted a trip order waits for the trucker). Under review
    // the server refuses the accept (FRD-03), so only Reject is offered;
    // Accept stays disabled until the hold check has answered.
    if (userRole == UserRole.farmer && canFarmerRespond(order, userId)) {
      return FwBottomActions(
        children: [
          _AnswerRow(
            decline: OutlinedButton(
              onPressed: () => _rejectAsFarmer(context, order, userId),
              style: _secondaryStyle,
              child: Text(l10n.rejectOrder),
            ),
            accept: underReview
                ? null
                : FilledButton(
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
                    style: _primaryStyle,
                    child: Text(l10n.acceptOrder),
                  ),
          ),
        ],
      );
    }

    // ESC-15: the trucker answers the transport request once the farmer
    // accepted. Like the farmer's, the accept waits for the hold check and is
    // not offered while the money is under review.
    if (canTruckerRespond(order, userId)) {
      return FwBottomActions(
        children: [
          _AnswerRow(
            decline: OutlinedButton(
              onPressed: () => _declineTransport(context, order, userId),
              style: _secondaryStyle,
              child: Text(l10n.truckerDeclineRequest),
            ),
            accept: underReview
                ? null
                : FilledButton(
                    onPressed: holdChecked
                        ? () => context.read<OrderBloc>().add(
                              TruckerAcceptOrder(
                                orderId: order.orderId,
                                truckerId: userId,
                              ),
                            )
                        : null,
                    style: _primaryStyle,
                    child: Text(l10n.truckerAcceptRequest),
                  ),
          ),
        ],
      );
    }
    return null;
  }

  static final ButtonStyle _primaryStyle = FilledButton.styleFrom(
    minimumSize: const Size.fromHeight(56),
    textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
  );

  static final ButtonStyle _secondaryStyle = OutlinedButton.styleFrom(
    minimumSize: const Size.fromHeight(56),
    textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
  );

  /// The actions that sit in the page below the cards: the delivery
  /// (create, open, QR, live trip), rating, and the cancels.
  List<Widget> _buildActions(
    BuildContext context, {
    required Order order,
    required String userId,
    required UserRole userRole,
    required AppLocalizations l10n,
  }) {
    final widgets = <Widget>[];

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

  /// The farmer declines a new order, saying why (Farmer · Answer a new
  /// order board); the buyer is refunded.
  Future<void> _rejectAsFarmer(
    BuildContext context,
    Order order,
    String userId,
  ) async {
    final l10n = AppLocalizations.of(context)!;
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
/// Reject and Accept (or Decline and Accept transport) side by side at the
/// foot, as on the Farmer · Answer a new order board; Reject alone while the
/// money is under review.
class _AnswerRow extends StatelessWidget {
  final Widget decline;
  final Widget? accept;

  const _AnswerRow({required this.decline, required this.accept});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(child: decline),
        if (accept != null) ...[
          const SizedBox(width: 10),
          Expanded(child: accept!),
        ],
      ],
    );
  }
}

/// The page header: Back, what the order is (crop and weight) with whom it
/// is with, and the status pill. A farmer answering a new order reads "New
/// order" with the crop under it and no pill, as on the board.
/// "Thu, Oct 30": the trip's day on the Ethiopian calendar day, as the trip
/// cards write it.
String _tripDay(DateTime at) => DateFormat.MMMEd().format(dayInEthiopia(at));

class _OrderHeader extends StatelessWidget {
  final Order order;
  final String userId;
  final bool farmerAnswering;

  const _OrderHeader({
    required this.order,
    required this.userId,
    required this.farmerAnswering,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final isFarmer = userId == order.farmerId;
    final isTrucker = !isFarmer && userId == order.truckerId;
    final farmer = partyDisplayName(order.farmerName, l10n);
    final buyer = partyDisplayName(order.buyerName, l10n);
    final what = '${CropType.labelFor(order.cropType, l10n)} · '
        '${l10n.quantityKg(wholeNumber(order.quantity))}';

    final String title;
    final String subtitle;
    if (farmerAnswering) {
      title = l10n.detailNewOrderTitle;
      subtitle = l10n.detailFromYourListing(what);
    } else if (isFarmer) {
      title = what;
      subtitle = '${l10n.buyer}: $buyer';
    } else if (isTrucker) {
      title = what;
      subtitle = '$farmer → $buyer';
    } else {
      title = what;
      final pickup = order.pickupDate;
      subtitle = pickup == null
          ? l10n.liveTripFrom(farmer)
          : '${l10n.liveTripFrom(farmer)} · '
              '${l10n.orderTripOn(_tripDay(pickup))}';
    }

    return FwDetailHeader(
      key: const Key('order_summary_hero'),
      title: title,
      subtitle: subtitle,
      trailing: farmerAnswering
          ? null
          : StatusChip(
              label: orderStatusLabel(order.status, l10n),
              kind: orderStatusKind(order.status),
            ),
    );
  }
}

/// Farmer · Answer a new order: what the farmer receives, the weight times
/// the price with the money's state, then the buyer and the pickup.
class _FarmerReceiveCard extends StatelessWidget {
  final Order order;

  const _FarmerReceiveCard({required this.order});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final held = escrowHeldOrderStatuses.contains(order.status);
    final trucker = order.truckerName?.trim() ?? '';
    final truck = trucker.isNotEmpty
        ? l10n.detailTruckOf(partyDisplayName(trucker, l10n))
        : l10n.trucker;
    final day = order.pickupDate;
    final pickup = orderHasTrip(order)
        ? (day == null ? truck : '${_tripDay(day)} · $truck')
        : l10n.detailBuyerCollects;
    return FwCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Expanded(
                child: Text(
                  l10n.orderYouReceive,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.mutedSteel,
                  ),
                ),
              ),
              Text(
                l10n.etbCurrency(
                  formatEtbAmount(order.cropCost ?? order.totalPrice),
                ),
                style: const TextStyle(
                  fontSize: 24,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.5,
                  color: AppTheme.soilDark,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: Text(
                  '${l10n.quantityKg(wholeNumber(order.quantity))} × '
                  '${l10n.etbCurrency(formatEtbAmount(order.pricePerUnit))}',
                  style: const TextStyle(
                    fontSize: 14,
                    color: AppTheme.unselectedNav,
                  ),
                ),
              ),
              if (held)
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(
                      Icons.lock_outline,
                      size: 14,
                      color: AppTheme.fieldGreen,
                    ),
                    const SizedBox(width: 6),
                    Text(
                      l10n.detailPaidIntoEscrow,
                      style: const TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.fieldGreen,
                      ),
                    ),
                  ],
                ),
            ],
          ),
          const SizedBox(height: 12),
          const Divider(color: AppTheme.segmentTrack),
          const SizedBox(height: 12),
          _FactRow(
            label: l10n.buyer,
            value: partyDisplayName(order.buyerName, l10n),
          ),
          const SizedBox(height: 8),
          _FactRow(label: l10n.detailPickupLabel, value: pickup),
        ],
      ),
    );
  }
}

/// A label left, its value bold right.
class _FactRow extends StatelessWidget {
  final String label;
  final String value;

  const _FactRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: const TextStyle(fontSize: 14, color: AppTheme.mutedSteel),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            value,
            textAlign: TextAlign.end,
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w700,
              color: AppTheme.soilDark,
            ),
          ),
        ),
      ],
    );
  }
}

/// The trucker's transport pay for this order, held with the rest.
class _TruckerPayCard extends StatelessWidget {
  final Order order;

  const _TruckerPayCard({required this.order});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final held = escrowHeldOrderStatuses.contains(order.status);
    return FwCard(
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.orderYourTransportPay,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.mutedSteel,
                  ),
                ),
                if (held) ...[
                  const SizedBox(height: 4),
                  Row(
                    children: [
                      const Icon(
                        Icons.lock_outline,
                        size: 14,
                        color: AppTheme.fieldGreen,
                      ),
                      const SizedBox(width: 6),
                      Text(
                        l10n.detailPaidIntoEscrow,
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.fieldGreen,
                        ),
                      ),
                    ],
                  ),
                ],
              ],
            ),
          ),
          Text(
            l10n.etbCurrency(formatEtbAmount(order.transportCost ?? 0)),
            style: const TextStyle(
              fontSize: 24,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.5,
              color: AppTheme.soilDark,
            ),
          ),
        ],
      ),
    );
  }
}

/// Buyer · Order detail: the money at the foot of the page, "Held in
/// escrow" while it is, else the order's total.
class _BuyerTotalRow extends StatelessWidget {
  final Order order;

  const _BuyerTotalRow({required this.order});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final held = escrowHeldOrderStatuses.contains(order.status);
    return FwCard(
      key: const Key('order_buyer_total'),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          Icon(
            held ? Icons.lock_outline : Icons.receipt_long_outlined,
            size: 18,
            color: AppTheme.unselectedNav,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              held ? l10n.detailHeldInEscrow : l10n.orderTotal,
              style: const TextStyle(
                fontSize: 14,
                color: AppTheme.unselectedNav,
              ),
            ),
          ),
          Text(
            l10n.etbCurrency(
              formatEtbAmount(order.orderTotal ?? order.totalPrice),
            ),
            style: const TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w800,
              color: AppTheme.soilDark,
            ),
          ),
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

/// Order Timeline as the board draws it: a card with four steps as short
/// bars (done in green, the current one in gold, the rest grey) and the
/// steps still to come named under them. Tapping a step says what it means.
class _StatusTimeline extends StatelessWidget {
  final OrderStatus status;
  final AppLocalizations l10n;

  /// A trucker carries the crop: the board's Live trip step follows
  /// Accepted (a self-pickup order has no truck to follow).
  final bool tripOrder;

  const _StatusTimeline({
    required this.status,
    required this.l10n,
    this.tripOrder = false,
  });

  /// How many steps the card shows at once.
  static const int _window = 4;

  @override
  Widget build(BuildContext context) {
    final steps = _timelineSteps;
    final currentIndex = steps.indexWhere((s) => s.status == status);
    // Keep the current step in view with one step ahead of it.
    var start = 0;
    if (steps.length > _window && currentIndex >= _window - 1) {
      start = (currentIndex - (_window - 2)).clamp(0, steps.length - _window);
    }
    final shown = steps.skip(start).take(_window).toList();
    final later = steps.skip(start + shown.length).map((s) => s.label).toList();

    return FwCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            header: true,
            child: Text(l10n.orderTimeline, style: _cardTitle),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              for (final step in shown) ...[
                Expanded(
                  child: _TimelineStepTile(
                    step: step,
                    done:
                        currentIndex >= 0 && steps.indexOf(step) < currentIndex,
                    current: steps.indexOf(step) == currentIndex,
                  ),
                ),
                if (step != shown.last) const SizedBox(width: 6),
              ],
            ],
          ),
          if (later.isNotEmpty) ...[
            const SizedBox(height: 10),
            Text(
              l10n.detailTimelineThen(_joinNames(later)),
              style: const TextStyle(
                fontSize: 12,
                height: 1.4,
                color: AppTheme.mutedSteel,
              ),
            ),
          ] else ...[
            const SizedBox(height: 10),
            Text(
              l10n.detailTimelineTapStep,
              style: const TextStyle(
                fontSize: 12,
                height: 1.4,
                color: AppTheme.mutedSteel,
              ),
            ),
          ],
        ],
      ),
    );
  }

  /// "A, B and C" in the page's language's own joiner.
  String _joinNames(List<String> names) {
    if (names.length == 1) return names.single;
    final head = names.sublist(0, names.length - 1).join(', ');
    return l10n.detailListAnd(head, names.last);
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

    // Happy path. The payment steps between Created and Funded only show
    // while the order is in one of them, so a paid order reads Created,
    // Funded, Accepted... as on the board; disbursing likewise.
    final paying = status == OrderStatus.pendingPayment ||
        status == OrderStatus.paymentSubmitted;
    return [
      _TimelineStep(
        OrderStatus.created,
        l10n.statusCreated,
        l10n.statusCreatedInfo,
      ),
      if (paying) ...[
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
      ],
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
      // No status of its own: done once the pickup is confirmed.
      if (tripOrder)
        _TimelineStep(
          null,
          l10n.liveTripTitle,
          l10n.liveTripAfterPickupHint,
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
      if (status == OrderStatus.disbursing)
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

const TextStyle _cardTitle = TextStyle(
  fontSize: 15,
  fontWeight: FontWeight.w800,
  color: AppTheme.soilDark,
);

/// One step of the timeline: a 4 px bar over its name. What the step means
/// shows on hover (web, desktop) or tap (touch).
class _TimelineStepTile extends StatelessWidget {
  final _TimelineStep step;
  final bool done;
  final bool current;

  const _TimelineStepTile({
    required this.step,
    required this.done,
    required this.current,
  });

  @override
  Widget build(BuildContext context) {
    final bar = done
        ? AppTheme.fieldGreen
        : current
            ? AppTheme.accentSoft
            : AppTheme.whisperBorder;
    final ink = current
        ? AppTheme.money
        : done
            ? AppTheme.soilDark
            : AppTheme.mutedSteel;
    return Tooltip(
      message: step.info,
      triggerMode: TooltipTriggerMode.tap,
      showDuration: const Duration(seconds: 8),
      margin: const EdgeInsets.symmetric(horizontal: 24),
      child: Semantics(
        button: true,
        selected: current,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              height: 4,
              decoration: BoxDecoration(
                color: bar,
                borderRadius: BorderRadius.circular(999),
              ),
            ),
            const SizedBox(height: 6),
            Text(
              step.label,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 11,
                fontWeight: done || current ? FontWeight.w700 : FontWeight.w600,
                color: ink,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// ESC-48: the parties of an order reach each other here, and only here, once
/// the farmer accepted the paid order and until the money goes back
/// (order-lifecycle.md 1.18.0). The board's contact card: one row per
/// counterparty with their initials, name and role, Message and Call (the
/// sheet offers Call and Copy when the server sent the number, and Message,
/// an order thread). A trucker not yet committed is a locked row saying when
/// contact opens. Before any contact opens, a note says when it will; after
/// a cancel, reject or refund nothing shows.
class _OrderContactSection extends StatelessWidget {
  final Order order;
  final String userId;

  const _OrderContactSection({required this.order, required this.userId});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

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
        padding: const EdgeInsets.only(bottom: 12),
        child: ContactLockedNote(
          text: order.status == OrderStatus.funded
              ? l10n.contactAfterAcceptNote
              : l10n.contactAfterEscrowNote,
        ),
      );
    }

    final truckerId = order.truckerId ?? '';
    final parties = <({String id, String name, String role, Key key})>[
      if (order.buyerId != userId)
        (
          id: order.buyerId,
          name: order.buyerName,
          role: l10n.buyer,
          key: const Key('order_contact_buyer'),
        ),
      if (order.farmerId != userId)
        (
          id: order.farmerId,
          name: order.farmerName,
          role: l10n.farmer,
          key: const Key('order_contact_farmer'),
        ),
      if (truckerId.isNotEmpty && truckerId != userId && truckerCommitted)
        (
          id: truckerId,
          name: order.truckerName ?? '',
          role: l10n.trucker,
          key: const Key('order_contact_trucker'),
        ),
    ];
    // The trucker asked but not yet answered: contact opens when they do.
    final truckerPending = truckerId.isNotEmpty &&
        truckerId != userId &&
        !truckerCommitted &&
        truckerResponseOf(order) == CommitmentResponse.pending;
    if (parties.isEmpty && !truckerPending) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FwCard(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
        child: Column(
          children: [
            for (final party in parties)
              _ContactRow(
                key: party.key,
                name: party.name.isNotEmpty
                    ? partyDisplayName(party.name, l10n)
                    : party.role,
                role: party.role,
                divider: party != parties.last || truckerPending,
                onOpen: () =>
                    _openSheet(context, party.id, party.name, party.role),
                onMessage: () => _message(context, party.id, party.name),
              ),
            if (truckerPending)
              _LockedContactRow(
                title: l10n.trucker,
                note: l10n.detailContactOpensWhenTruckerAccepts,
              ),
          ],
        ),
      ),
    );
  }

  void _openSheet(BuildContext context, String id, String name, String role) {
    final l10n = AppLocalizations.of(context)!;
    showContactActionsSheet(
      context,
      name: name.isNotEmpty ? partyDisplayName(name, l10n) : role,
      roleLabel: role,
      phone: orderPartyPhone(order, id),
      onMessage: () => _message(context, id, name),
    );
  }

  void _message(BuildContext context, String id, String name) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ConversationScreen(
          contextType: 'order',
          contextId: order.orderId,
          counterpartId: id,
          counterpartName: name,
        ),
      ),
    );
  }
}

/// A counterparty on the contact card: initials, name and role, Message and
/// Call squares. Tapping the row opens the contact sheet (Call, Copy,
/// Message), as does Call; Message opens the thread.
class _ContactRow extends StatelessWidget {
  final String name;
  final String role;
  final bool divider;
  final VoidCallback onOpen;
  final VoidCallback onMessage;

  const _ContactRow({
    required this.name,
    required this.role,
    required this.divider,
    required this.onOpen,
    required this.onMessage,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final initials = name
        .split(RegExp(r'\s+'))
        .where((part) => part.isNotEmpty)
        .take(2)
        .map((part) => part.characters.first.toUpperCase())
        .join();
    return Container(
      constraints: const BoxConstraints(minHeight: 60),
      decoration: divider
          ? const BoxDecoration(
              border: Border(bottom: BorderSide(color: AppTheme.segmentTrack)),
            )
          : null,
      child: Row(
        children: [
          Expanded(
            child: InkWell(
              onTap: onOpen,
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  children: [
                    CircleAvatar(
                      radius: 19,
                      backgroundColor: AppTheme.fieldGreenTint,
                      child: Text(
                        initials,
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w800,
                          color: AppTheme.fieldGreen,
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            name,
                            style: const TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w700,
                              color: AppTheme.soilDark,
                            ),
                          ),
                          Text(
                            role,
                            style: const TextStyle(
                              fontSize: 12,
                              color: AppTheme.mutedSteel,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
          const SizedBox(width: 8),
          FwSquareButton(
            semanticLabel: l10n.detailMessageParty(name),
            onTap: onMessage,
            child: const Icon(
              Icons.chat_bubble_outline,
              size: 18,
              color: AppTheme.soilDark,
            ),
          ),
          const SizedBox(width: 8),
          Semantics(
            button: true,
            label: l10n.detailCallParty(name),
            child: Material(
              color: AppTheme.fieldGreen,
              borderRadius: BorderRadius.circular(14),
              clipBehavior: Clip.antiAlias,
              child: InkWell(
                onTap: onOpen,
                child: const SizedBox(
                  width: 44,
                  height: 44,
                  child:
                      Icon(Icons.call_outlined, size: 18, color: Colors.white),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The trucker's row while contact is not open yet.
class _LockedContactRow extends StatelessWidget {
  final String title;
  final String note;

  const _LockedContactRow({required this.title, required this.note});

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 60),
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          const CircleAvatar(
            radius: 19,
            backgroundColor: AppTheme.segmentTrack,
            child:
                Icon(Icons.lock_outline, size: 16, color: AppTheme.mutedSteel),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                ),
                Text(
                  note,
                  style: const TextStyle(
                    fontSize: 12,
                    color: AppTheme.mutedSteel,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _TimelineStep {
  /// Null for a step with no order status of its own (Live trip): never the
  /// current step.
  final OrderStatus? status;
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
    return FwNoteBox.cream(
      icon: Icons.warning_amber,
      child: Text(text),
    );
  }
}
