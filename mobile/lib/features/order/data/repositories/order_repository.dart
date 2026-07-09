import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';

/// Offline-first repository for order CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
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
          'status': OrderStatus.created.name,
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
    String cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  }) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();

    if (order == null) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.cbeTransactionId = cbeTransactionId;
      order.paymentMethod = paymentMethod;
      order.status = OrderStatus.paymentSubmitted;
      order.updatedAt = now;
      order.version += 1;
      await _isar.orders.put(order);
    });

    await _syncQueue.enqueue(
      userId: order.buyerId,
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      data: jsonEncode({
        'orderId': orderId,
        'cbeTransactionId': cbeTransactionId,
        'paymentMethod': paymentMethod,
        'status': OrderStatus.paymentSubmitted.name,
        'updatedAt': now.toIso8601String(),
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Cancel an order (buyer or farmer action).
  @override
  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  }) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();

    if (order == null) return;
    if (order.buyerId != userId && order.farmerId != userId) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.status = OrderStatus.cancelled;
      order.cancelledAt = now;
      order.cancelReason = reason;
      order.updatedAt = now;
      order.version += 1;
      await _isar.orders.put(order);
    });

    await _syncQueue.enqueue(
      userId: userId,
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      data: jsonEncode({
        'orderId': orderId,
        'status': OrderStatus.cancelled.name,
        'cancelledAt': now.toIso8601String(),
        if (reason != null) 'cancelReason': reason,
        'updatedAt': now.toIso8601String(),
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Accept an order (farmer action).
  @override
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();

    if (order == null || order.farmerId != farmerId) return;
    if (order.status != OrderStatus.funded) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.status = OrderStatus.accepted;
      order.respondedAt = now;
      order.updatedAt = now;
      order.version += 1;
      await _isar.orders.put(order);
    });

    await _syncQueue.enqueue(
      userId: farmerId,
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      data: jsonEncode({
        'orderId': orderId,
        'status': OrderStatus.accepted.name,
        'respondedAt': now.toIso8601String(),
        'updatedAt': now.toIso8601String(),
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Reject an order (farmer action).
  @override
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  }) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();

    if (order == null || order.farmerId != farmerId) return;
    if (order.status != OrderStatus.funded) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.status = OrderStatus.rejected;
      order.respondedAt = now;
      order.rejectionReason = reason;
      order.updatedAt = now;
      order.version += 1;
      await _isar.orders.put(order);
    });

    await _syncQueue.enqueue(
      userId: farmerId,
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      data: jsonEncode({
        'orderId': orderId,
        'status': OrderStatus.rejected.name,
        'respondedAt': now.toIso8601String(),
        if (reason != null) 'rejectionReason': reason,
        'updatedAt': now.toIso8601String(),
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }
}
