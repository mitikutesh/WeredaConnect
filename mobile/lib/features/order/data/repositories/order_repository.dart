import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';

/// Offline-first repository for order CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
///
/// The update actions refuse instead of returning quietly: a missing order
/// throws [NotFoundException], the wrong party or status throws
/// [OrderActionNotAllowedException], and a failed enqueue puts the local
/// order back and rethrows. Repeating an action the order already shows
/// (accept on `accepted`, ...) is a no-op success.
class OrderRepository extends BaseOrderRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  OrderRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Get orders for a farmer, optionally filtered by status.
  @override
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().farmerIdEqualTo(farmerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.created,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get orders placed by a buyer, optionally filtered by status.
  @override
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().buyerIdEqualTo(buyerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.created,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get all orders for a user (as farmer or buyer).
  @override
  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    if (role == 'farmer') {
      return getOrdersByFarmer(userId, statusFilter: statusFilter);
    } else if (role == 'buyer') {
      return getOrdersByBuyer(userId, statusFilter: statusFilter);
    } else if (role == 'trucker') {
      // Truckers see orders where they are the assigned transporter. Without this
      // branch a trucker fell through to the farmer/buyer-only query and saw none of
      // their assigned orders (the web repo already handles this — keep them in sync).
      return _getOrdersByTrucker(userId, statusFilter: statusFilter);
    }

    // All roles: single query using group/or
    var query = _isar.orders.filter().group(
          (q) => q.farmerIdEqualTo(userId).or().buyerIdEqualTo(userId),
        );

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.created,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get orders where the user is the assigned trucker, optionally by status.
  Future<List<Order>> _getOrdersByTrucker(
    String truckerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().truckerIdEqualTo(truckerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.created,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get a single order by its orderId.
  @override
  Future<Order?> getOrderById(String orderId) async {
    return _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
  }

  /// Create a new order (buyer action).
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
    final orderId = _uuid.v4();
    final now = DateTime.now().toUtc();
    final totalPrice = quantity * pricePerUnit;
    final cropCost = totalPrice;
    final effectiveCommissionRate = commissionRate ?? 0.05;
    final commissionAmount = cropCost * effectiveCommissionRate;
    final orderTotal = cropCost + (transportCost ?? 0) + commissionAmount;

    final order = Order()
      ..orderId = orderId
      ..listingId = listingId
      ..buyerId = buyerId
      ..farmerId = farmerId
      ..buyerName = buyerName
      ..farmerName = farmerName
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerUnit = pricePerUnit
      ..totalPrice = totalPrice
      ..status = OrderStatus.created
      ..createdAt = now
      ..version = 1
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..cropCost = cropCost
      ..transportCost = transportCost
      ..commissionRate = effectiveCommissionRate
      ..commissionAmount = commissionAmount
      ..orderTotal = orderTotal
      ..paymentMethod = 'cbe_transfer'
      ..updatedAt = now;

    await _isar.writeTxn(() async {
      await _isar.orders.put(order);
    });

    // The local Isar write above is authoritative (offline-first). Enqueue
    // failures (queue full / duplicate idempotency key) must NOT surface as a
    // save failure — the order is persisted; the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'create',
        data: jsonEncode({
          'orderId': orderId,
          'listingId': listingId,
          'buyerId': buyerId,
          'farmerId': farmerId,
          'buyerName': buyerName,
          'farmerName': farmerName,
          'cropType': cropType,
          'quantity': quantity,
          'pricePerUnit': pricePerUnit,
          'totalPrice': totalPrice,
          'cropCost': cropCost,
          'transportCost': transportCost,
          'commissionRate': effectiveCommissionRate,
          'commissionAmount': commissionAmount,
          'orderTotal': orderTotal,
          'paymentMethod': 'cbe_transfer',
          if (tripId != null) 'tripId': tripId,
          if (truckerId != null) 'truckerId': truckerId,
          if (truckerName != null) 'truckerName': truckerName,
          'status': orderStatusToWire(OrderStatus.created.name),
          'createdAt': now.toIso8601String(),
          'updatedAt': now.toIso8601String(),
          'version': 1,
        }),
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[OrderRepository] order saved locally but sync enqueue failed: $e',
      );
    }

    return order;
  }

  /// Submit payment proof (buyer action).
  @override
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  }) async {
    final order = await _findOrder(orderId);
    // Already submitted (and still waiting to sync): nothing to send again.
    if (order.status == OrderStatus.paymentSubmitted) return;

    final now = DateTime.now().toUtc();
    // Send the version the client HAD before this change: the server checks
    // `#v = :expectedVersion`, so sending the post-increment value would always
    // conflict and get reverted by server-wins (same off-by-one fixed for trips).
    final baseVersion = order.version;
    final statusBefore = order.status;

    await _writeThenEnqueue(
      order,
      (o) {
        o.cbeTransactionId = cbeTransactionId;
        o.paymentMethod = paymentMethod;
        o.status = OrderStatus.paymentSubmitted;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: order.buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          if (cbeTransactionId != null) 'cbeTransactionId': cbeTransactionId,
          'paymentMethod': paymentMethod,
          'status': orderStatusToWire(OrderStatus.paymentSubmitted.name),
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
          // Client-only (not sent): where a refused payment goes back to.
          statusBeforePaymentKey: statusBefore.name,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// The client-only key of a queued payment that records the order's
  /// status before it ([revertRejectedPaymentSubmission]). The sync executor
  /// never sends `_` keys.
  static const statusBeforePaymentKey = '_statusBefore';

  /// Undo an optimistic payment submission the server's KYC gate rejected
  /// (`ORDER_KYC_REQUIRED`), so the order does not stay stuck in
  /// `paymentSubmitted` and the buyer can pay again after verifying.
  ///
  /// The server never applied the update, so the order goes back to the
  /// status it had before [submitPayment] (with the version from then): the
  /// one the refused operation's [sentData] recorded (`payment_failed` for a
  /// retry, ESC-44), else, for operations queued before that was recorded,
  /// `accepted` once the farmer responded, else `created` (payment can be
  /// submitted straight after placing the order). No-op when the order moved
  /// on in the meantime (e.g. a later pull).
  Future<void> revertRejectedPaymentSubmission(
    String orderId, {
    String? sentData,
  }) async {
    final recorded = _statusBeforePayment(sentData);
    await _isar.writeTxn(() async {
      final order =
          await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
      if (order == null || order.status != OrderStatus.paymentSubmitted) {
        return;
      }
      order
        ..status = recorded ??
            (order.respondedAt != null
                ? OrderStatus.accepted
                : OrderStatus.created)
        ..cbeTransactionId = null
        ..paymentMethod = null
        ..version = order.version > 1 ? order.version - 1 : order.version
        ..updatedAt = DateTime.now().toUtc();
      await _isar.orders.put(order);
    });
  }

  static OrderStatus? _statusBeforePayment(String? sentData) {
    if (sentData == null) return null;
    try {
      final data = jsonDecode(sentData);
      final name = data is Map ? data[statusBeforePaymentKey] : null;
      for (final status in OrderStatus.values) {
        if (status.name == name && status != OrderStatus.paymentSubmitted) {
          return status;
        }
      }
    } on FormatException {
      // Fall back to the older rule.
    }
    return null;
  }

  /// Cancel an order (buyer or farmer action).
  @override
  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  }) async {
    final order = await _findOrder(orderId);
    if (order.buyerId != userId && order.farmerId != userId) {
      throw const OrderActionNotAllowedException(
        'Only the buyer or the farmer can cancel this order.',
      );
    }
    if (order.status == OrderStatus.cancelled) return;

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = OrderStatus.cancelled;
        o.cancelledAt = now;
        o.cancelReason = reason;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: userId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'status': orderStatusToWire(OrderStatus.cancelled.name),
          'cancelledAt': now.toIso8601String(),
          if (reason != null) 'cancelReason': reason,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// Accept an order (farmer action).
  @override
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final order = await _findOrder(orderId);
    _checkFarmerResponse(order, farmerId);
    if (order.status == OrderStatus.accepted) return;
    _checkFunded(order);

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = OrderStatus.accepted;
        o.respondedAt = now;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: farmerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'status': orderStatusToWire(OrderStatus.accepted.name),
          'respondedAt': now.toIso8601String(),
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// Reject an order (farmer action).
  @override
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  }) async {
    final order = await _findOrder(orderId);
    _checkFarmerResponse(order, farmerId);
    if (order.status == OrderStatus.rejected) return;
    _checkFunded(order);

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = OrderStatus.rejected;
        o.respondedAt = now;
        o.rejectionReason = reason;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: farmerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'status': orderStatusToWire(OrderStatus.rejected.name),
          'respondedAt': now.toIso8601String(),
          if (reason != null) 'rejectionReason': reason,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  Future<Order> _findOrder(String orderId) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
    if (order == null) throw NotFoundException('Order $orderId not found.');
    return order;
  }

  /// Only the order's farmer accepts or rejects it.
  static void _checkFarmerResponse(Order order, String farmerId) {
    if (order.farmerId != farmerId) {
      throw const OrderActionNotAllowedException(
        'Only the farmer of this order can respond to it.',
      );
    }
  }

  /// The farmer responds to a paid order only.
  static void _checkFunded(Order order) {
    if (order.status != OrderStatus.funded) {
      throw OrderActionNotAllowedException(
        'The order is ${order.status.name}; only a funded order can be '
        'accepted or rejected.',
      );
    }
  }

  /// Apply [change] to the local order, then queue its sync operation. When
  /// queueing fails (queue full, duplicate key) the local order is put back
  /// as it was and the error is rethrown, so the caller never reports a
  /// change that will not reach the server.
  Future<void> _writeThenEnqueue(
    Order order,
    void Function(Order order) change,
    Future<void> Function() enqueue,
  ) async {
    final before = (
      status: order.status,
      respondedAt: order.respondedAt,
      rejectionReason: order.rejectionReason,
      cancelledAt: order.cancelledAt,
      cancelReason: order.cancelReason,
      cbeTransactionId: order.cbeTransactionId,
      paymentMethod: order.paymentMethod,
      updatedAt: order.updatedAt,
      version: order.version,
    );
    await _isar.writeTxn(() async {
      change(order);
      await _isar.orders.put(order);
    });
    try {
      await enqueue();
    } on Object {
      await _isar.writeTxn(() async {
        order
          ..status = before.status
          ..respondedAt = before.respondedAt
          ..rejectionReason = before.rejectionReason
          ..cancelledAt = before.cancelledAt
          ..cancelReason = before.cancelReason
          ..cbeTransactionId = before.cbeTransactionId
          ..paymentMethod = before.paymentMethod
          ..updatedAt = before.updatedAt
          ..version = before.version;
        await _isar.orders.put(order);
      });
      rethrow;
    }
  }
}
