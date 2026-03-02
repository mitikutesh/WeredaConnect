import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

/// BLoC for managing order state.
///
/// Handles loading, filtering, accepting, and rejecting orders.
/// All operations go through the offline-first OrderRepository.
class OrderBloc extends Bloc<OrderEvent, OrderState> {
  final OrderRepository _repository;

  OrderBloc({required OrderRepository repository})
      : _repository = repository,
        super(const OrderInitial()) {
    on<LoadUserOrders>(_onLoadUserOrders);
    on<FilterOrders>(_onFilterOrders);
    on<AcceptOrder>(_onAcceptOrder);
    on<RejectOrder>(_onRejectOrder);
  }

  Future<void> _onLoadUserOrders(
    LoadUserOrders event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      final orders = await _repository.getOrdersByUser(
        event.userId,
        role: event.role,
      );
      emit(OrdersLoaded(orders: orders));
    } on Exception catch (e) {
      emit(OrderError(message: e.toString()));
    }
  }

  Future<void> _onFilterOrders(
    FilterOrders event,
    Emitter<OrderState> emit,
  ) async {
    emit(const OrderLoading());
    try {
      final orders = await _repository.getOrdersByUser(
        event.userId,
        role: event.role,
        statusFilter: event.statusFilter,
      );
      emit(OrdersLoaded(orders: orders, statusFilter: event.statusFilter));
    } on Exception catch (e) {
      emit(OrderError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(OrderError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(OrderError(message: e.toString()));
    }
  }
}
