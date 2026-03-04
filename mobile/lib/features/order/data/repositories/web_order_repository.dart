import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';

/// Web-only order repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
class WebOrderRepository extends BaseOrderRepository {
  final ApiClient _api;

  WebOrderRepository({required ApiClient api}) : _api = api;

  @override
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{'farmerId': farmerId};
    if (statusFilter != null) params['status'] = statusFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/orders',
      queryParameters: params,
    );
    return _parseOrders(data);
  }

  @override
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{'buyerId': buyerId};
    if (statusFilter != null) params['status'] = statusFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/orders',
      queryParameters: params,
    );
    return _parseOrders(data);
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
    return getOrdersByBuyer(userId, statusFilter: statusFilter);
  }

  @override
  Future<Order?> getOrderById(String orderId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>('/orders/$orderId');
      if (data.containsKey('orderId')) return _parseOrderMap(data);
      if (data.containsKey('item')) {
        return _parseOrderMap(data['item'] as Map<String, dynamic>);
      }
      return null;
    } on Exception {
      return null;
    }
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
    final orderId =
        'ord_${DateTime.now().millisecondsSinceEpoch}_${buyerId.hashCode.abs()}';
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
      'status': 'created',
      if (tripId != null) 'tripId': tripId,
      if (truckerId != null) 'truckerId': truckerId,
      if (truckerName != null) 'truckerName': truckerName,
      if (transportCost != null) 'transportCost': transportCost,
    };

    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'order',
            'operationType': 'create',
            'entityId': orderId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_create_$orderId',
            'data': payload,
          },
        ],
      },
    );

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

  @override
  Future<void> submitPayment(String orderId, String cbeTransactionId) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'order',
            'operationType': 'update',
            'entityId': orderId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_payment_${orderId}_$now',
            'data': {
              'status': 'paymentSubmitted',
              'paymentMethod': 'cbe_transfer',
              'cbeTransactionId': cbeTransactionId,
            },
          },
        ],
      },
    );
  }

  @override
  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'order',
            'operationType': 'update',
            'entityId': orderId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_cancel_${orderId}_$now',
            'data': {
              'status': 'cancelled',
              if (reason != null) 'cancelReason': reason,
            },
          },
        ],
      },
    );
  }

  @override
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'order',
            'operationType': 'update',
            'entityId': orderId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_accept_${orderId}_$now',
            'data': {'status': 'accepted'},
          },
        ],
      },
    );
  }

  @override
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'order',
            'operationType': 'update',
            'entityId': orderId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_reject_${orderId}_$now',
            'data': {
              'status': 'rejected',
              if (reason != null) 'rejectionReason': reason,
            },
          },
        ],
      },
    );
  }

  List<Order> _parseOrders(Map<String, dynamic> data) {
    final items = data['items'] as List<dynamic>? ?? [];
    return items
        .map((item) => _parseOrderMap(item as Map<String, dynamic>))
        .toList();
  }

  Order _parseOrderMap(Map<String, dynamic> m) {
    return Order()
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
      ..updatedAt = DateTime.tryParse(m['updatedAt'] as String? ?? '');
  }

  OrderStatus _parseOrderStatus(String? s) {
    return OrderStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => OrderStatus.created,
    );
  }
}
