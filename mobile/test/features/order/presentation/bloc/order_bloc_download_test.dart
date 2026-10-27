import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

/// Verifies the mobile order-download path: an [OrderBloc] wired with a
/// [MarketplaceSync] must pull the user's orders from the server into local
/// storage BEFORE reading them (so a farmer sees incoming buyer orders), and
/// must stay offline-first — a failed pull still surfaces the local cache.
void main() {
  Order buildOrder(String id) => Order()
    ..orderId = id
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..buyerName = 'Buyer'
    ..farmerName = 'Farmer'
    ..cropType = 'wheat'
    ..quantity = 10
    ..pricePerUnit = 100
    ..totalPrice = 1000
    ..status = OrderStatus.created
    ..createdAt = DateTime.utc(2026)
    ..version = 1;

  group('OrderBloc order download', () {
    test('pulls orders (scoped to userId+role) before reading, then loads',
        () async {
      final sync = _SpyMarketplaceSync();
      final repo = _FakeOrderRepository([buildOrder('o-1')], spy: sync);
      final bloc = OrderBloc(repository: repo, marketplaceSync: sync);
      addTearDown(bloc.close);

      final states = <OrderState>[];
      final sub = bloc.stream.listen(states.add);
      addTearDown(sub.cancel);

      bloc.add(const LoadUserOrders(userId: 'farmer-1', role: 'farmer'));
      await waitForStates(states, 2);

      // The pull ran first, scoped to the caller.
      expect(sync.calls, [('farmer-1', 'farmer')]);
      // ...and it happened before the local read.
      expect(sync.pulledBeforeRead, isTrue);
      // Terminal state carries the local rows.
      expect(states.last, isA<OrdersLoaded>());
      expect((states.last as OrdersLoaded).orders.single.orderId, 'o-1');
    });

    test('offline-first: a failed pull still emits OrdersLoaded (not error)',
        () async {
      final repo = _FakeOrderRepository([buildOrder('o-2')]);
      final sync = _SpyMarketplaceSync(fail: true);
      final bloc = OrderBloc(repository: repo, marketplaceSync: sync);
      addTearDown(bloc.close);

      final states = <OrderState>[];
      final sub = bloc.stream.listen(states.add);
      addTearDown(sub.cancel);

      bloc.add(const LoadUserOrders(userId: 'buyer-1', role: 'buyer'));
      await waitForStates(states, 2);

      expect(states.whereType<OrderError>(), isEmpty);
      expect(states.last, isA<OrdersLoaded>());
      expect((states.last as OrdersLoaded).orders.single.orderId, 'o-2');
    });
  });
}

class _SpyMarketplaceSync implements MarketplaceSync {
  _SpyMarketplaceSync({this.fail = false});

  final bool fail;
  final List<(String, String?)> calls = [];
  bool pulledBeforeRead = false;

  @override
  Future<void> pullOrders({required String userId, String? role}) async {
    calls.add((userId, role));
    if (fail) throw Exception('offline');
  }

  @override
  Future<Order?> pullOrder(String orderId) async => null;

  @override
  Future<void> pullCrops() async {}

  @override
  Future<void> pullTrips() async {}

  @override
  Future<void> pullDeliveries() async {}

  @override
  Future<void> pullAll() async {}
}

/// Minimal fake — only the read path used by [OrderBloc.LoadUserOrders] is
/// exercised; everything else is unused in these tests.
class _FakeOrderRepository extends BaseOrderRepository {
  _FakeOrderRepository(this._orders, {this.spy});

  final List<Order> _orders;
  final _SpyMarketplaceSync? spy;

  @override
  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    spy?.pulledBeforeRead = spy!.calls.isNotEmpty;
    return _orders;
  }

  @override
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) =>
      throw UnimplementedError();

  @override
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) =>
      throw UnimplementedError();

  @override
  Future<Order?> getOrderById(String orderId) => throw UnimplementedError();

  @override
  Future<Order> createOrder({
    required String listingId,
    required String buyerId,
    required String farmerId,
    required String buyerName,
    required String farmerName,
    required String cropType,
    required double quantity,
    required double pricePerUnit,
    String? tripId,
    String? truckerId,
    String? truckerName,
    double? transportCost,
    double? commissionRate,
  }) =>
      throw UnimplementedError();

  @override
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  }) =>
      throw UnimplementedError();

  @override
  Future<void> cancelOrder(String orderId, String userId, {String? reason}) =>
      throw UnimplementedError();

  @override
  Future<void> acceptOrder(String orderId, String farmerId) =>
      throw UnimplementedError();

  @override
  Future<void> rejectOrder(String orderId, String farmerId, {String? reason}) =>
      throw UnimplementedError();

  @override
  Future<void> respondAsTrucker(
    String orderId,
    String truckerId, {
    required bool accept,
    String? declineReason,
  }) =>
      throw UnimplementedError();
}
