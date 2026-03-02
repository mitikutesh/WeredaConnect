import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Offline-first repository for order CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class OrderRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  OrderRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Get orders for a farmer, optionally filtered by status.
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().farmerIdEqualTo(farmerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.pending,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get orders placed by a buyer, optionally filtered by status.
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().buyerIdEqualTo(buyerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.pending,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get all orders for a user (as farmer or buyer).
  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    if (role == 'farmer') {
      return getOrdersByFarmer(userId, statusFilter: statusFilter);
    } else if (role == 'buyer') {
      return getOrdersByBuyer(userId, statusFilter: statusFilter);
    }

    // All roles: single query using group/or
    var query = _isar.orders.filter().group(
          (q) => q.farmerIdEqualTo(userId).or().buyerIdEqualTo(userId),
        );

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.pending,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Create a new order (buyer action).
  Future<Order> createOrder({
    required String listingId,
    required String buyerId,
    required String farmerId,
    required String buyerName,
    required String farmerName,
    required String cropType,
    required double quantity,
    required double pricePerUnit,
  }) async {
    final orderId = _uuid.v4();
    final now = DateTime.now().toUtc();
    final totalPrice = quantity * pricePerUnit;

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
      ..status = OrderStatus.pending
      ..createdAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.orders.put(order);
    });

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
        'status': OrderStatus.pending.name,
        'createdAt': now.toIso8601String(),
        'version': 1,
      }),
      operationId: _uuid.v4(),
    );

    return order;
  }

  /// Accept an order (farmer action).
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final order = await _isar.orders
        .filter()
        .orderIdEqualTo(orderId)
        .findFirst();

    if (order == null || order.farmerId != farmerId) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.status = OrderStatus.accepted;
      order.respondedAt = now;
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
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Reject an order (farmer action).
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  }) async {
    final order = await _isar.orders
        .filter()
        .orderIdEqualTo(orderId)
        .findFirst();

    if (order == null || order.farmerId != farmerId) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      order.status = OrderStatus.rejected;
      order.respondedAt = now;
      order.rejectionReason = reason;
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
        'version': order.version,
      }),
      operationId: _uuid.v4(),
    );
  }
}
