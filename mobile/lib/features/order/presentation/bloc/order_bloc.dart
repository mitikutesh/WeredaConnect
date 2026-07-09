import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

/// BLoC for managing order state.
///
/// Handles loading, filtering, accepting, rejecting, creating,
/// payment submission, and cancellation of orders.
/// All operations go through the BaseOrderRepository (mobile or web).
class OrderBloc extends Bloc<OrderEvent, OrderState> {
  final BaseOrderRepository _repository;

  /// Mobile-only: pulls the user's orders from the server into Isar before the
  /// offline-first local read below, so a farmer sees incoming buyer orders (the
  /// sync engine only uploads own writes). Null on web (API-backed reads).
  final MarketplaceSync? _marketplaceSync;

  /// The repository backing this bloc.
  ///
  /// Exposed so a short-lived, route-scoped [OrderBloc] (the order-detail
  /// screen) can reuse the same repository instance instead of sharing this
  /// app-root bloc. Loading a single order must NOT drive the shared list bloc
  /// through Loading/Error/SingleOrderLoaded states — the still-mounted list
  /// screen renders whatever the shared bloc last emitted, which is why a
  /// failed detail load previously left the list stuck on an error+retry view.
  BaseOrderRepository get repository => _repository;

  OrderBloc({
    required BaseOrderRepository repository,
    MarketplaceSync? marketplaceSync,
  })  : _repository = repository,
        _marketplaceSync = marketplaceSync,
        super(const OrderInitial()) {
    on<LoadUserOrders>(_onLoadUserOrders);
    on<FilterOrders>(_onFilterOrders);
    on<AcceptOrder>(_onAcceptOrder);
    on<RejectOrder>(_onRejectOrder);
    on<CreateOrder>(_onCreateOrder);
    on<SubmitPayment>(_onSubmitPayment);
    on<CancelOrder>(_onCancelOrder);
    on<LoadOrderById>(_onLoadOrderById);
  }

  Future<void> _onLoadUserOrders(
    LoadUserOrders event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _refreshOrders(event.userId, event.role);
      final orders = await _repository.getOrdersByUser(
        event.userId,
        role: event.role,
      );
      emit(OrdersLoaded(orders: orders));
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onFilterOrders(
    FilterOrders event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _refreshOrders(event.userId, event.role);
      final orders = await _repository.getOrdersByUser(
        event.userId,
        role: event.role,
        statusFilter: event.statusFilter,
      );
      emit(OrdersLoaded(orders: orders, statusFilter: event.statusFilter));
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  /// Pull the user's orders from the server into local Isar so a farmer sees
  /// incoming buyer orders they didn't author. Best-effort: offline or a failed
  /// pull falls back to whatever is already cached locally (offline-first).
  /// No-op on web (null sync — the web repo reads the API directly).
  Future<void> _refreshOrders(String userId, String? role) async {
    final sync = _marketplaceSync;
    if (sync == null) return;
    try {
      await sync.pullOrders(userId: userId, role: role);
    } on Object {
      // Offline-first: ignore and read the local cache.
    }
  }

  Future<void> _onAcceptOrder(
    AcceptOrder event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _repository.acceptOrder(event.orderId, event.farmerId);
      emit(const OrderAccepted());
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onRejectOrder(
    RejectOrder event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _repository.rejectOrder(
        event.orderId,
        event.farmerId,
        reason: event.reason,
      );
      emit(const OrderRejected());
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onCreateOrder(
    CreateOrder event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      final order = await _repository.createOrder(
        listingId: event.listingId,
        buyerId: event.buyerId,
        farmerId: event.farmerId,
        buyerName: event.buyerName,
        farmerName: event.farmerName,
        cropType: event.cropType,
        quantity: event.quantity,
        pricePerUnit: event.pricePerUnit,
        tripId: event.tripId,
        truckerId: event.truckerId,
        truckerName: event.truckerName,
        transportCost: event.transportCost,
        commissionRate: event.commissionRate,
      );
      emit(OrderCreated(order: order));
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onSubmitPayment(
    SubmitPayment event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _repository.submitPayment(
        event.orderId,
        event.cbeTransactionId,
        paymentMethod: event.paymentMethod,
      );
      emit(const PaymentSubmitted());
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onCancelOrder(
    CancelOrder event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      await _repository.cancelOrder(
        event.orderId,
        event.userId,
        reason: event.reason,
      );
      emit(const OrderCancelled());
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onLoadOrderById(
    LoadOrderById event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      final order = await _repository.getOrderById(event.orderId);
      if (order != null) {
        emit(SingleOrderLoaded(order: order));
      } else {
        emit(
          const OrderError(
            message: 'Order not found',
            code: AppErrorCode.notFound,
          ),
        );
      }
    } on Object catch (e) {
      emit(OrderError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }
}
