import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';

/// Web-only order repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
class WebOrderRepository extends BaseOrderRepository {
  static const _uuid = Uuid();
  final ApiClient _api;

  WebOrderRepository({required ApiClient api}) : _api = api;

  @override
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{'farmerId': farmerId};
    // Order status crosses the wire in snake_case; statusFilter is the camelCase
    // enum name, so translate it or the server (which matches stored snake_case)
    // returns nothing for multi-word statuses (e.g. "In Transit" tab empty).
    if (statusFilter != null) {
      params['status'] = orderStatusToWire(statusFilter);
    }
    final data = await _api.get<Map<String, dynamic>>(
      '/orders',
      queryParameters: params,
    );
    return _filterByStatus(_parseOrders(data), statusFilter);
  }

  @override
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{'buyerId': buyerId};
    // Order status crosses the wire in snake_case; statusFilter is the camelCase
    // enum name, so translate it or the server (which matches stored snake_case)
    // returns nothing for multi-word statuses (e.g. "In Transit" tab empty).
    if (statusFilter != null) {
      params['status'] = orderStatusToWire(statusFilter);
    }
    final data = await _api.get<Map<String, dynamic>>(
      '/orders',
      queryParameters: params,
    );
    return _filterByStatus(_parseOrders(data), statusFilter);
  }

  @override
  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    if (role == 'farmer') {
      return getOrdersByFarmer(userId, statusFilter: statusFilter);
    }
    // Truckers see orders where they are assigned as transporter
    if (role == 'trucker') {
      return _getOrdersByTrucker(userId, statusFilter: statusFilter);
    }
    return getOrdersByBuyer(userId, statusFilter: statusFilter);
  }

  Future<List<Order>> _getOrdersByTrucker(
    String truckerId, {
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{'truckerId': truckerId};
    // Order status crosses the wire in snake_case; statusFilter is the camelCase
    // enum name, so translate it or the server (which matches stored snake_case)
    // returns nothing for multi-word statuses (e.g. "In Transit" tab empty).
    if (statusFilter != null) {
      params['status'] = orderStatusToWire(statusFilter);
    }
    final data = await _api.get<Map<String, dynamic>>(
      '/orders',
      queryParameters: params,
    );
    return _filterByStatus(_parseOrders(data), statusFilter);
  }

  /// Defensive client-side status filter. The server SHOULD honour the `status`
  /// query param, but a lagging deployment that ignores it would return the
  /// user's full order list for every tab — matching the reported "Pending
  /// orders also show under Accepted" bug. Filtering here keeps the status tabs
  /// correct regardless of server behaviour; it's a no-op once the server
  /// filters, and cheap because a single user's order set is small.
  List<Order> _filterByStatus(List<Order> orders, String? statusFilter) {
    if (statusFilter == null) return orders;
    return orders.where((o) => o.status.name == statusFilter).toList();
  }

  @override
  Future<Order?> getOrderById(String orderId) async {
    try {
      return await fetchOrder(orderId);
    } on Exception {
      return null;
    }
  }

  /// `GET /orders/{id}` with errors propagating (offline, 404, 403), unlike
  /// [getOrderById]. The order checkout (ESC-44) must tell "offline" from
  /// "no such order".
  Future<Order?> fetchOrder(String orderId) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/orders/${Uri.encodeComponent(orderId)}',
    );
    // GET /orders/{id} returns {"order": {...}} (GetOrderResponse, camelCase).
    // Accept the `item` envelope and a bare object too, so a response-shape
    // change never silently degrades into a spurious "order not found" that
    // the detail screen surfaces as "An error occurred".
    final envelope = data['order'] ?? data['item'];
    if (envelope is Map<String, dynamic>) return _parseOrderMap(envelope);
    if (data.containsKey('orderId')) return _parseOrderMap(data);
    return null;
  }

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
  }) async {
    // UUID like the mobile repositories: the server rejects other id
    // shapes (for example a delivery's tripId must be a UUID).
    final orderId = _uuid.v4();
    final now = DateTime.now().toUtc().toIso8601String();

    final payload = <String, dynamic>{
      'listingId': listingId,
      'buyerId': buyerId,
      'farmerId': farmerId,
      'buyerName': buyerName,
      'farmerName': farmerName,
      'cropType': cropType,
      'quantity': quantity,
      'pricePerUnit': pricePerUnit,
      'status': orderStatusToWire(OrderStatus.created.name),
      if (tripId != null) 'tripId': tripId,
      if (truckerId != null) 'truckerId': truckerId,
      if (truckerName != null) 'truckerName': truckerName,
      if (transportCost != null) 'transportCost': transportCost,
    };

    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'order',
            'action': 'create',
            'entityId': orderId,
            'timestamp': now,
            'idempotencyKey': 'web_create_$orderId',
            'data': payload,
          },
        ],
      },
    );
    // Surface server-side rejections instead of silently reporting success
    // for an order that was never persisted.
    ensureSyncSucceeded(response);

    final cropCost = quantity * pricePerUnit;
    final rate = commissionRate ?? 0.05;
    final commissionAmount = cropCost * rate;

    return Order()
      ..orderId = orderId
      ..listingId = listingId
      ..buyerId = buyerId
      ..farmerId = farmerId
      ..buyerName = buyerName
      ..farmerName = farmerName
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerUnit = pricePerUnit
      ..totalPrice = cropCost + (transportCost ?? 0) + commissionAmount
      ..status = OrderStatus.created
      ..createdAt = DateTime.now().toUtc()
      ..version = 1
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..cropCost = cropCost
      ..transportCost = transportCost
      ..commissionRate = rate
      ..commissionAmount = commissionAmount
      ..orderTotal = cropCost + (transportCost ?? 0) + commissionAmount;
  }

  /// The server applies an order update only when the payload's `version`
  /// matches the version it holds; a versionless order update is rejected
  /// (sync-protocol.md 1.3.0, SIM-08). Web keeps no local copy, so read the
  /// current version first, exactly like the trip repository does.
  Future<int> _currentOrderVersion(String orderId) async =>
      (await _currentOrder(orderId)).version;

  Future<Order> _currentOrder(String orderId) async {
    final order = await getOrderById(orderId);
    if (order == null) {
      throw NotFoundException('Order $orderId not found.');
    }
    return order;
  }

  /// One order `update` through `/sync`, refused results surfaced.
  Future<void> _sendOrderUpdate(
    String orderId,
    String kind,
    Map<String, dynamic> data,
  ) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'order',
            'action': 'update',
            'entityId': orderId,
            'timestamp': now,
            'idempotencyKey': 'web_${kind}_${orderId}_$now',
            'data': data,
          },
        ],
      },
    );
    ensureSyncSucceeded(response);
  }

  @override
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  }) async {
    final order = await getOrderById(orderId);
    if (order == null) {
      throw NotFoundException('Order $orderId not found.');
    }
    // Already submitted: nothing to send again (mobile does the same). The
    // checkout screen takes it from here.
    if (order.status == OrderStatus.paymentSubmitted) return;
    final version = order.version;
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'order',
            'action': 'update',
            'entityId': orderId,
            'timestamp': now,
            'idempotencyKey': 'web_payment_${orderId}_$now',
            'data': {
              'status': orderStatusToWire(OrderStatus.paymentSubmitted.name),
              'paymentMethod': paymentMethod,
              if (cbeTransactionId != null)
                'cbeTransactionId': cbeTransactionId,
              'version': version,
            },
          },
        ],
      },
    );
    // Surface server-side rejections instead of silently reporting success
    // for a payment that was never recorded.
    ensureSyncSucceeded(response);
  }

  @override
  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  }) async {
    final order = await _currentOrder(orderId);
    if (order.status == OrderStatus.cancelled) return;
    // ESC-15: the same cancel rules as mobile (a funded order waits for the
    // farmer and the trucker).
    checkOrderCancelAllowed(order, userId, DateTime.now().toUtc());
    final version = order.version;
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'order',
            'action': 'update',
            'entityId': orderId,
            'timestamp': now,
            'idempotencyKey': 'web_cancel_${orderId}_$now',
            'data': {
              'status': orderStatusToWire(OrderStatus.cancelled.name),
              if (reason != null) 'cancelReason': reason,
              'version': version,
            },
          },
        ],
      },
    );
    // Surface server-side rejections (e.g. ORDER_UNAUTHORIZED, invalid
    // status transition) instead of reporting a cancellation that never
    // happened on the server.
    ensureSyncSucceeded(response);
  }

  /// ESC-15: the farmer command; the server answers a repeat with success.
  @override
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final version = await _currentOrderVersion(orderId);
    // /sync returns HTTP 200 with a per-operation result — surface server-side
    // rejections instead of reporting an accept that never happened.
    await _sendOrderUpdate(orderId, 'accept', {
      'farmerResponse': CommitmentCommand.accept,
      'version': version,
    });
  }

  @override
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  }) async {
    final version = await _currentOrderVersion(orderId);
    // Surface server-side rejections instead of reporting a reject that never happened.
    await _sendOrderUpdate(orderId, 'reject', {
      'farmerResponse': CommitmentCommand.decline,
      if (reason != null) 'rejectionReason': reason,
      'version': version,
    });
  }

  /// ESC-15: the trucker command, with the mobile guards. The server's
  /// `AUTH_040` on a drop-out surfaces as a step-up error.
  @override
  Future<void> respondAsTrucker(
    String orderId,
    String truckerId, {
    required bool accept,
    String? declineReason,
  }) async {
    final order = await _currentOrder(orderId);
    if (truckerId.isEmpty || order.truckerId != truckerId) {
      throw const OrderActionNotAllowedException(
        'Only the trucker of this order can answer its transport request.',
      );
    }
    if (accept && truckerResponseOf(order) == CommitmentResponse.accepted) {
      return;
    }
    if (!canTruckerRespond(order, truckerId) &&
        !(!accept && canTruckerDropOut(order, truckerId))) {
      throw const OrderActionNotAllowedException(
        'This transport request is not open: the farmer has not accepted '
        'yet, or it was already answered.',
      );
    }
    await _sendOrderUpdate(orderId, accept ? 'truck_accept' : 'truck_decline', {
      'truckerResponse':
          accept ? CommitmentCommand.accept : CommitmentCommand.decline,
      if (!accept && declineReason != null) 'declineReason': declineReason,
      'version': order.version,
    });
  }

  List<Order> _parseOrders(Map<String, dynamic> data) {
    final items = data['orders'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
    return items
        .map((item) => _parseOrderMap(item as Map<String, dynamic>))
        .toList();
  }

  Order _parseOrderMap(Map<String, dynamic> m) {
    final order = Order()
      ..orderId = m['orderId'] as String? ?? ''
      ..listingId = m['listingId'] as String? ?? ''
      ..buyerId = m['buyerId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..buyerName = m['buyerName'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      ..quantity = (m['quantity'] as num?)?.toDouble() ?? 0
      ..pricePerUnit = (m['pricePerUnit'] as num?)?.toDouble() ?? 0
      ..totalPrice = (m['totalPrice'] as num?)?.toDouble() ?? 0
      ..status = _parseOrderStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..respondedAt = DateTime.tryParse(m['respondedAt'] as String? ?? '')
      ..rejectionReason = m['rejectionReason'] as String?
      ..version = (m['version'] as num?)?.toInt() ?? 1
      ..tripId = m['tripId'] as String?
      ..truckerId = m['truckerId'] as String?
      ..truckerName = m['truckerName'] as String?
      ..cropCost = (m['cropCost'] as num?)?.toDouble()
      ..transportCost = (m['transportCost'] as num?)?.toDouble()
      ..commissionRate = (m['commissionRate'] as num?)?.toDouble()
      ..commissionAmount = (m['commissionAmount'] as num?)?.toDouble()
      ..orderTotal = (m['orderTotal'] as num?)?.toDouble()
      ..paymentMethod = m['paymentMethod'] as String?
      ..cbeTransactionId = m['cbeTransactionId'] as String?
      ..paymentVerifiedAt =
          DateTime.tryParse(m['paymentVerifiedAt'] as String? ?? '')
      ..cancelledAt = DateTime.tryParse(m['cancelledAt'] as String? ?? '')
      ..cancelReason = m['cancelReason'] as String?
      ..updatedAt = DateTime.tryParse(m['updatedAt'] as String? ?? '')
      ..pickupDeadlineAt =
          DateTime.tryParse(m['pickupDeadlineAt'] as String? ?? '')?.toUtc()
      // ESC-48: contact flag and the parties' phones (open orders only).
      ..contactOpen = m['contactOpen'] as bool?
      ..buyerPhone = m['buyerPhone'] as String?
      ..farmerPhone = m['farmerPhone'] as String?
      ..truckerPhone = m['truckerPhone'] as String?;
    // ESC-15: who has answered and until when.
    applyOrderCommitmentFields(order, m);
    return order;
  }

  OrderStatus _parseOrderStatus(String? s) {
    // Server sends order status in snake_case; map it back to the camelCase enum.
    // A status this build does not know is `unknown` (shown as "Updating").
    final name = s == null ? null : orderStatusFromWire(s);
    return OrderStatus.values.firstWhere(
      (e) => e.name == name,
      orElse: () => OrderStatus.unknown,
    );
  }
}
