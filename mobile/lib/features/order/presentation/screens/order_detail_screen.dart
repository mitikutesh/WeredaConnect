import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/create_delivery_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
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
                      label: order.cropType,
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

    // Trucker + accepted + has tripId → Create Delivery
    if (userRole == UserRole.trucker &&
        order.status == OrderStatus.accepted &&
        order.tripId != null) {
      widgets.add(
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: () => _navigateToCreateDelivery(context, order, l10n),
            icon: const Icon(Icons.local_shipping),
            label: Text(l10n.createDelivery),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ),
      );
      widgets.add(const SizedBox(height: 12));
    }

    // Buyer + paid + no trucker/trip → "Collect it myself" (self-pickup).
    // Creates a self-pickup delivery the buyer confirms at the farm by scanning
    // the farmer's QR. Only offered when no trucker is arranging transport.
    if (userRole == UserRole.buyer &&
        (order.status == OrderStatus.funded ||
            order.status == OrderStatus.paymentSubmitted) &&
        order.truckerId == null &&
        order.tripId == null) {
      widgets.add(
        BlocConsumer<DeliveryBloc, DeliveryState>(
          listenWhen: (_, curr) =>
              curr is DeliveryCreated &&
              curr.delivery.deliveryType ==
                  DeliveryConstants.selfPickupDeliveryType,
          listener: (context, state) {
            if (state is DeliveryCreated) {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => DeliveryDetailScreen(
                    delivery: state.delivery,
                    currentUserId: userId,
                  ),
                ),
              );
            }
          },
          builder: (context, state) {
            final isLoading = state is DeliveryLoading;
            return SizedBox(
              width: double.infinity,
              child: FilledButton.icon(
                onPressed: isLoading
                    ? null
                    : () => context.read<DeliveryBloc>().add(
                          CreateSelfPickupDelivery(
                            orderId: order.orderId,
                            listingId: order.listingId,
                            farmerId: order.farmerId,
                            farmerName: order.farmerName,
                            buyerId: userId,
                            buyerName: order.buyerName,
                          ),
                        ),
                icon: const Icon(Icons.storefront),
                label: Text(l10n.orderCollectMyself),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                ),
              ),
            );
          },
        ),
      );
      widgets.add(const SizedBox(height: 12));
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

  Future<void> _navigateToCreateDelivery(
    BuildContext context,
    Order order,
    AppLocalizations l10n,
  ) async {
    // Isar is unavailable on web (IsarService.instance.db is null), so this
    // offline-first delivery-creation flow cannot resolve the listing locally
    // there — degrade gracefully instead of dereferencing a null db.
    if (kIsWeb) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(l10n.listingNotFound)),
        );
      }
      return;
    }
    final isar = IsarService.instance.db;
    final listing = await isar.cropListings
        .filter()
        .listingIdEqualTo(order.listingId)
        .findFirst();

    if (listing == null) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(l10n.listingNotFound)),
        );
      }
      return;
    }

    if (context.mounted) {
      await Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => CreateDeliveryScreen(listing: listing),
        ),
      );
      // Reload so the newly created delivery (and the order's status change) is
      // reflected instead of leaving the stale pre-delivery order on screen.
      if (context.mounted) {
        context.read<OrderBloc>().add(LoadOrderById(orderId: order.orderId));
      }
    }
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
          title: Text(
            step.label,
            style: TextStyle(
              fontWeight: isCurrent ? FontWeight.bold : FontWeight.normal,
            ),
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
        _TimelineStep(OrderStatus.created, l10n.statusCreated),
        _TimelineStep(OrderStatus.cancelled, l10n.orderCancelled),
      ];
    }
    if (status == OrderStatus.rejected) {
      return [
        _TimelineStep(OrderStatus.created, l10n.statusCreated),
        _TimelineStep(OrderStatus.rejected, l10n.orderRejected),
      ];
    }
    if (status == OrderStatus.paymentFailed) {
      return [
        _TimelineStep(OrderStatus.created, l10n.statusCreated),
        _TimelineStep(
          OrderStatus.paymentSubmitted,
          l10n.statusPaymentSubmitted,
        ),
        _TimelineStep(OrderStatus.paymentFailed, l10n.statusPaymentFailed),
      ];
    }
    if (status == OrderStatus.disputed ||
        status == OrderStatus.disputeResolved) {
      return [
        _TimelineStep(OrderStatus.created, l10n.statusCreated),
        _TimelineStep(OrderStatus.funded, l10n.statusFunded),
        _TimelineStep(OrderStatus.delivered, l10n.statusDelivered),
        _TimelineStep(OrderStatus.disputed, l10n.statusDisputed),
        _TimelineStep(OrderStatus.disputeResolved, l10n.statusDisputeResolved),
      ];
    }

    // Happy path. Includes pendingPayment and disbursing so an order in either
    // state highlights the right step instead of collapsing to step 0 (the
    // Stepper falls back to index 0 when the status isn't in this list).
    return [
      _TimelineStep(OrderStatus.created, l10n.statusCreated),
      _TimelineStep(OrderStatus.pendingPayment, l10n.statusPendingPayment),
      _TimelineStep(
        OrderStatus.paymentSubmitted,
        l10n.statusPaymentSubmitted,
      ),
      _TimelineStep(OrderStatus.funded, l10n.statusFunded),
      _TimelineStep(OrderStatus.accepted, l10n.orderAccepted),
      _TimelineStep(
        OrderStatus.pickupConfirmed,
        l10n.statusPickupConfirmed,
      ),
      _TimelineStep(OrderStatus.inTransit, l10n.statusInTransit),
      _TimelineStep(OrderStatus.delivered, l10n.statusDelivered),
      _TimelineStep(OrderStatus.disbursing, l10n.statusDisbursing),
      _TimelineStep(OrderStatus.settled, l10n.statusSettled),
    ];
  }
}

class _TimelineStep {
  final OrderStatus status;
  final String label;

  const _TimelineStep(this.status, this.label);
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
