import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';

/// Service for resolving sync conflicts when server reports version mismatch.
///
/// Strategy: Server wins (accept server version, update local Isar).
///
/// When the server returns a conflict (status: 'conflict'), it means:
/// - Client's version was behind server's version
/// - Server provides authoritative data in 'serverData' field
/// - Client must accept server version and update local entity
///
/// Conflict resolution flow:
/// 1. Server returns status: 'conflict' with serverData and serverVersion
/// 2. ConflictResolver extracts the entity from serverData
/// 3. Local Isar record is updated with server version
/// 4. Sync operation is marked as synced (not failed)
///
/// Ethiopian context:
/// - Conflicts are rare in offline-first scenarios (most users work solo)
/// - When they occur (e.g., family sharing a device), server wins is safest
/// - Alternative strategies (merge, manual) add complexity for minimal benefit
///
/// See: `specs/contracts/sync-protocol.md`
class ConflictResolver {
  final Isar _isar;

  ConflictResolver(this._isar);

  /// Resolve a conflict by accepting the server's version.
  ///
  /// This method:
  /// 1. Parses the server data based on entity type
  /// 2. Updates the local Isar entity with server version
  /// 3. Returns the resolved entity data for logging/audit
  ///
  /// Parameters:
  /// - [operation]: The sync operation that conflicted
  /// - [serverData]: The server's authoritative entity data
  /// - [serverVersion]: The server's current entity version
  ///
  /// Returns the resolved entity as Map for audit logging.
  ///
  /// Throws [ConflictResolutionException] if:
  /// - Entity type is unsupported
  /// - Server data is malformed
  /// - Local entity not found (shouldn't happen)
  Future<Map<String, dynamic>> resolve({
    required SyncOperation operation,
    required Map<String, dynamic> serverData,
    required int serverVersion,
  }) async {
    switch (operation.entityType) {
      case 'crop_listing':
        return _resolveCropListing(operation, serverData, serverVersion);

      case 'delivery':
        return _resolveDelivery(operation, serverData, serverVersion);

      case 'trip':
        return _resolveTrip(operation, serverData, serverVersion);

      case 'order':
        return _resolveOrder(operation, serverData, serverVersion);

      case 'payment':
        return _resolvePayment(operation, serverData, serverVersion);

      case 'user':
        return _resolveUser(operation, serverData, serverVersion);

      case 'rating':
        // Ratings are create-only and immutable, so a conflict only means the
        // server already has this rating. Accept the server copy — there is
        // nothing to reconcile locally.
        return serverData;

      default:
        throw ConflictResolutionException(
          'Unknown entity type: ${operation.entityType}',
        );
    }
  }

  /// Resolve a CropListing conflict by accepting server version.
  Future<Map<String, dynamic>> _resolveCropListing(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      // Find the local CropListing entity (may be null if deleted locally)
      var listing = await _isar.cropListings
          .filter()
          .listingIdEqualTo(operation.entityId)
          .findFirst();

      // Update or create local entity with server data
      await _isar.writeTxn(() async {
        listing ??= CropListing()..listingId = operation.entityId;

        listing!.farmerId = serverData['farmerId'] as String;
        listing!.farmerName = (serverData['farmerName'] as String?) ?? '';
        listing!.cropType = serverData['cropType'] as String;
        listing!.quantity = (serverData['quantity'] as num).toDouble();
        listing!.pricePerKg = (serverData['pricePerKg'] as num).toDouble();

        // Update location if present
        final locationData = serverData['location'] as Map<String, dynamic>?;
        if (locationData != null) {
          listing!.location = LatLng()
            ..latitude = (locationData['latitude'] as num).toDouble()
            ..longitude = (locationData['longitude'] as num).toDouble();
        }

        listing!.description = serverData['description'] as String;
        listing!.locationName = (serverData['locationName'] as String?) ?? '';
        listing!.audioNoteRef = serverData['audioNoteRef'] as String?;

        // Parse status enum
        final statusStr = serverData['status'] as String;
        listing!.status = ListingStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => ListingStatus.draft,
        );

        // Update timestamps (ensure UTC)
        listing!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        listing!.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        // CRITICAL: Update version to server version
        listing!.version = serverVersion;

        await _isar.cropListings.put(listing!);
      });

      // Return resolved data for audit logging
      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve CropListing conflict: $e',
      );
    }
  }

  /// Resolve a Delivery conflict by accepting server version.
  Future<Map<String, dynamic>> _resolveDelivery(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      var delivery = await _isar.deliverys
          .filter()
          .deliveryIdEqualTo(operation.entityId)
          .findFirst();

      await _isar.writeTxn(() async {
        delivery ??= Delivery()..deliveryId = operation.entityId;

        // Null-safe casts: self-pickup deliveries carry no trucker (and no
        // qrToken), so a hard `as String` on those threw a TypeError and the
        // conflict failed to auto-resolve instead of applying server-wins.
        delivery!.tripId = serverData['tripId'] as String? ?? '';
        delivery!.orderId =
            serverData['orderId'] as String? ?? delivery!.orderId;
        // Keeps a self-pickup a self-pickup (the buyer's scan button and the
        // pending-to-delivered confirmation key on it).
        delivery!.deliveryType =
            serverData['deliveryType'] as String? ?? delivery!.deliveryType;
        delivery!.listingId = serverData['listingId'] as String? ?? '';
        delivery!.farmerId = serverData['farmerId'] as String? ?? '';
        delivery!.farmerName = serverData['farmerName'] as String? ?? '';
        delivery!.truckerId = serverData['truckerId'] as String? ?? '';
        delivery!.truckerName = serverData['truckerName'] as String? ?? '';
        delivery!.buyerId = serverData['buyerId'] as String? ?? '';
        delivery!.buyerName = serverData['buyerName'] as String? ?? '';
        delivery!.qrToken = serverData['qrToken'] as String? ?? '';

        // Parse status enum
        final statusStr = serverData['status'] as String;
        delivery!.status = DeliveryStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => DeliveryStatus.pending,
        );

        // GPS fields
        final pickupData = serverData['gpsPickup'] as Map<String, dynamic>?;
        if (pickupData != null) {
          delivery!.gpsPickup = LatLng()
            ..latitude = (pickupData['latitude'] as num).toDouble()
            ..longitude = (pickupData['longitude'] as num).toDouble();
        } else {
          delivery!.gpsPickup = null;
        }

        final dropoffData = serverData['gpsDropoff'] as Map<String, dynamic>?;
        if (dropoffData != null) {
          delivery!.gpsDropoff = LatLng()
            ..latitude = (dropoffData['latitude'] as num).toDouble()
            ..longitude = (dropoffData['longitude'] as num).toDouble();
        } else {
          delivery!.gpsDropoff = null;
        }

        delivery!.pickupPhotoRef = serverData['pickupPhotoRef'] as String?;
        delivery!.dropoffPhotoRef = serverData['dropoffPhotoRef'] as String?;
        delivery!.disputeReason = serverData['disputeReason'] as String?;

        // Timestamps
        final pickedUpAtStr = serverData['pickedUpAt'] as String?;
        delivery!.pickedUpAt = pickedUpAtStr != null
            ? DateTime.parse(pickedUpAtStr).toUtc()
            : null;

        final confirmedAtStr = serverData['confirmedAt'] as String?;
        delivery!.confirmedAt = confirmedAtStr != null
            ? DateTime.parse(confirmedAtStr).toUtc()
            : null;

        delivery!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        delivery!.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        delivery!.version = serverVersion;

        await _isar.deliverys.put(delivery!);
      });

      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve Delivery conflict: $e',
      );
    }
  }

  /// Resolve a Trip conflict by accepting server version.
  Future<Map<String, dynamic>> _resolveTrip(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      var trip = await _isar.trips
          .filter()
          .tripIdEqualTo(operation.entityId)
          .findFirst();

      await _isar.writeTxn(() async {
        trip ??= Trip()..tripId = operation.entityId;

        trip!.truckerId = serverData['truckerId'] as String;
        trip!.truckerName = serverData['truckerName'] as String;
        trip!.truckerPhone = serverData['truckerPhone'] as String;
        trip!.fromWoreda = serverData['fromWoreda'] as String;
        trip!.toWoreda = serverData['toWoreda'] as String;
        trip!.departureDate =
            DateTime.parse(serverData['departureDate'] as String).toUtc();
        trip!.capacity = (serverData['capacity'] as num).toDouble();
        trip!.pricePerQuintal =
            (serverData['pricePerQuintal'] as num).toDouble();
        trip!.vehiclePlate = serverData['vehiclePlate'] as String?;
        trip!.vehicleDescription = serverData['vehicleDescription'] as String?;

        // Parse status enum
        final statusStr = serverData['status'] as String;
        trip!.status = TripStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => TripStatus.open,
        );

        // Parse matched listings
        final matchedRaw = serverData['matchedListings'] as List<dynamic>?;
        trip!.matchedListings = matchedRaw?.cast<String>() ?? <String>[];

        // Timestamps
        trip!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        trip!.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        // CRITICAL: Update version to server version
        trip!.version = serverVersion;

        await _isar.trips.put(trip!);
      });

      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve Trip conflict: $e',
      );
    }
  }

  /// Resolve an Order conflict by accepting server version.
  Future<Map<String, dynamic>> _resolveOrder(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      var order = await _isar.orders
          .filter()
          .orderIdEqualTo(operation.entityId)
          .findFirst();

      await _isar.writeTxn(() async {
        order ??= Order()..orderId = operation.entityId;

        order!.listingId = serverData['listingId'] as String;
        order!.buyerId = serverData['buyerId'] as String;
        order!.farmerId = serverData['farmerId'] as String;
        order!.buyerName = serverData['buyerName'] as String;
        order!.farmerName = serverData['farmerName'] as String;
        order!.cropType = serverData['cropType'] as String;
        order!.quantity = (serverData['quantity'] as num).toDouble();
        order!.pricePerUnit = (serverData['pricePerUnit'] as num).toDouble();
        order!.totalPrice = (serverData['totalPrice'] as num).toDouble();

        // Parse status enum. Server sends snake_case; map to the camelCase enum.
        final statusStr = orderStatusFromWire(serverData['status'] as String);
        order!.status = OrderStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => OrderStatus.created,
        );

        // Timestamps
        order!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();

        final updatedAtStr = serverData['updatedAt'] as String?;
        order!.updatedAt =
            updatedAtStr != null ? DateTime.parse(updatedAtStr).toUtc() : null;

        final respondedAtStr = serverData['respondedAt'] as String?;
        order!.respondedAt = respondedAtStr != null
            ? DateTime.parse(respondedAtStr).toUtc()
            : null;

        final cancelledAtStr = serverData['cancelledAt'] as String?;
        order!.cancelledAt = cancelledAtStr != null
            ? DateTime.parse(cancelledAtStr).toUtc()
            : null;

        final paymentVerifiedAtStr = serverData['paymentVerifiedAt'] as String?;
        order!.paymentVerifiedAt = paymentVerifiedAtStr != null
            ? DateTime.parse(paymentVerifiedAtStr).toUtc()
            : null;

        // ESC-06: the server-stamped pickup deadline (Cancel after a transport failure).
        order!.pickupDeadlineAt = DateTime.tryParse(
          serverData['pickupDeadlineAt'] as String? ?? '',
        )?.toUtc();

        // Optional fields
        order!.rejectionReason = serverData['rejectionReason'] as String?;
        order!.cancelReason = serverData['cancelReason'] as String?;
        order!.tripId = serverData['tripId'] as String?;
        order!.truckerId = serverData['truckerId'] as String?;
        order!.truckerName = serverData['truckerName'] as String?;
        order!.paymentMethod = serverData['paymentMethod'] as String?;
        order!.cbeTransactionId = serverData['cbeTransactionId'] as String?;

        // Numeric optional fields
        order!.cropCost = (serverData['cropCost'] as num?)?.toDouble();
        order!.transportCost =
            (serverData['transportCost'] as num?)?.toDouble();
        order!.commissionRate =
            (serverData['commissionRate'] as num?)?.toDouble();
        order!.commissionAmount =
            (serverData['commissionAmount'] as num?)?.toDouble();
        order!.orderTotal = (serverData['orderTotal'] as num?)?.toDouble();

        // CRITICAL: Update version to server version
        order!.version = serverVersion;

        await _isar.orders.put(order!);
      });

      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve Order conflict: $e',
      );
    }
  }

  /// Resolve a Payment conflict by accepting server version.
  Future<Map<String, dynamic>> _resolvePayment(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      var payment = await _isar.payments
          .filter()
          .paymentIdEqualTo(operation.entityId)
          .findFirst();

      await _isar.writeTxn(() async {
        payment ??= Payment()..paymentId = operation.entityId;

        payment!.deliveryId = serverData['deliveryId'] as String;
        payment!.farmerId = serverData['farmerId'] as String;
        payment!.buyerId = serverData['buyerId'] as String;
        payment!.amount = (serverData['amount'] as num).toDouble();
        payment!.commission = (serverData['commission'] as num).toDouble();
        payment!.commissionRate =
            (serverData['commissionRate'] as num).toDouble();
        payment!.farmerPayout = (serverData['farmerPayout'] as num).toDouble();
        payment!.truckerPayout =
            (serverData['truckerPayout'] as num?)?.toDouble();
        payment!.truckerId = serverData['truckerId'] as String?;
        payment!.orderId = serverData['orderId'] as String?;
        payment!.refundPending = serverData['refundPending'] as bool? ?? false;
        payment!.refundReason = serverData['refundReason'] as String?;
        payment!.telebirrRef = serverData['telebirrRef'] as String?;

        // Parse status enum
        final statusStr = serverData['status'] as String;
        payment!.status = PaymentStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => PaymentStatus.initiated,
        );

        // Timestamps
        payment!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        payment!.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        final expiresAtStr = serverData['expiresAt'] as String?;
        payment!.expiresAt =
            expiresAtStr != null ? DateTime.parse(expiresAtStr).toUtc() : null;

        final disputeWindowEndsAtStr =
            serverData['disputeWindowEndsAt'] as String?;
        payment!.disputeWindowEndsAt = disputeWindowEndsAtStr != null
            ? DateTime.parse(disputeWindowEndsAtStr).toUtc()
            : null;

        final releasedAtStr = serverData['releasedAt'] as String?;
        payment!.releasedAt = releasedAtStr != null
            ? DateTime.parse(releasedAtStr).toUtc()
            : null;

        final refundedAtStr = serverData['refundedAt'] as String?;
        payment!.refundedAt = refundedAtStr != null
            ? DateTime.parse(refundedAtStr).toUtc()
            : null;

        final completedAtStr = serverData['completedAt'] as String?;
        payment!.completedAt = completedAtStr != null
            ? DateTime.parse(completedAtStr).toUtc()
            : null;

        // Dispute fields
        payment!.disputeReason = serverData['disputeReason'] as String?;
        payment!.disputeDescription =
            serverData['disputeDescription'] as String?;

        payment!.version = serverVersion;

        await _isar.payments.put(payment!);
      });

      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve Payment conflict: $e',
      );
    }
  }

  /// Resolve a User conflict by accepting server version.
  Future<Map<String, dynamic>> _resolveUser(
    SyncOperation operation,
    Map<String, dynamic> serverData,
    int serverVersion,
  ) async {
    try {
      var user = await _isar.users
          .filter()
          .userIdEqualTo(operation.entityId)
          .findFirst();

      final existingUser = user;
      await _isar.writeTxn(() async {
        user ??= User()..userId = operation.entityId;

        user!.phoneNumber = serverData['phoneNumber'] as String;
        user!.name = serverData['name'] as String;
        user!.fullName = serverData['fullName'] as String?;
        user!.displayName = serverData['displayName'] as String?;

        // Parse role enum. Fail closed: an unknown or missing role never
        // defaults to farmer (that mis-scoped truckers/buyers); keep the role
        // we already have, and refuse to create a user without one.
        final roleStr = (serverData['role'] as String?)?.toLowerCase();
        UserRole? serverRole;
        for (final r in UserRole.values) {
          if (r.name.toLowerCase() == roleStr) serverRole = r;
        }
        if (serverRole != null) {
          user!.role = serverRole;
        } else if (existingUser == null) {
          throw ConflictResolutionException(
            'User conflict carried an unrecognized role: $roleStr',
          );
        }

        user!.woreda = serverData['woreda'] as String;
        user!.faydaId = serverData['faydaId'] as String?;
        user!.isVerified = serverData['isVerified'] as bool? ?? false;
        user!.faydaVerified = serverData['faydaVerified'] as bool? ?? false;

        final faydaVerifiedAtStr = serverData['faydaVerifiedAt'] as String?;
        user!.faydaVerifiedAt = faydaVerifiedAtStr != null
            ? DateTime.parse(faydaVerifiedAtStr).toUtc()
            : null;

        // Timestamps
        user!.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        user!.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        user!.version = serverVersion;

        await _isar.users.put(user!);
      });

      return {
        'entityType': operation.entityType,
        'entityId': operation.entityId,
        'action': operation.action,
        'serverVersion': serverVersion,
        'resolvedAt': DateTime.now().toUtc().toIso8601String(),
      };
    } on ConflictResolutionException {
      rethrow;
    } on Object catch (e) {
      throw ConflictResolutionException(
        'Failed to resolve User conflict: $e',
      );
    }
  }
}

/// Exception thrown when conflict resolution fails.
class ConflictResolutionException implements Exception {
  final String message;

  const ConflictResolutionException(this.message);

  @override
  String toString() => 'ConflictResolutionException: $message';
}
