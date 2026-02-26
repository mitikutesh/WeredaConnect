import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';

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

      case 'payment':
      case 'user':
        // Future entity types - not yet implemented
        throw ConflictResolutionException(
          'Conflict resolution not yet implemented for ${operation.entityType}',
        );

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
      // Find the local CropListing entity
      final listing = await _isar.cropListings
          .filter()
          .listingIdEqualTo(operation.entityId)
          .findFirst();

      if (listing == null) {
        throw ConflictResolutionException(
          'CropListing not found in local database: ${operation.entityId}',
        );
      }

      // Update local entity with server data
      await _isar.writeTxn(() async {
        // Update all fields from server
        listing.farmerId = serverData['farmerId'] as String;
        listing.cropType = serverData['cropType'] as String;
        listing.quantity = (serverData['quantity'] as num).toDouble();
        listing.pricePerKg = (serverData['pricePerKg'] as num).toDouble();

        // Update location if present
        final locationData = serverData['location'] as Map<String, dynamic>?;
        if (locationData != null) {
          listing.location = LatLng()
            ..latitude = (locationData['latitude'] as num).toDouble()
            ..longitude = (locationData['longitude'] as num).toDouble();
        }

        listing.description = serverData['description'] as String;
        listing.locationName = (serverData['locationName'] as String?) ?? '';
        listing.audioNoteRef = serverData['audioNoteRef'] as String?;

        // Parse status enum
        final statusStr = serverData['status'] as String;
        listing.status = ListingStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => ListingStatus.draft,
        );

        // Update timestamps (ensure UTC)
        listing.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        listing.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        // CRITICAL: Update version to server version
        listing.version = serverVersion;

        await _isar.cropListings.put(listing);
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
      final delivery = await _isar.deliverys
          .filter()
          .deliveryIdEqualTo(operation.entityId)
          .findFirst();

      if (delivery == null) {
        throw ConflictResolutionException(
          'Delivery not found in local database: ${operation.entityId}',
        );
      }

      await _isar.writeTxn(() async {
        delivery.tripId = serverData['tripId'] as String;
        delivery.listingId = serverData['listingId'] as String;
        delivery.farmerId = serverData['farmerId'] as String;
        delivery.farmerName = serverData['farmerName'] as String;
        delivery.truckerId = serverData['truckerId'] as String;
        delivery.truckerName = serverData['truckerName'] as String;
        delivery.buyerId = serverData['buyerId'] as String;
        delivery.buyerName = serverData['buyerName'] as String;
        delivery.qrToken = serverData['qrToken'] as String;

        // Parse status enum
        final statusStr = serverData['status'] as String;
        delivery.status = DeliveryStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => DeliveryStatus.pending,
        );

        // GPS fields
        final pickupData = serverData['gpsPickup'] as Map<String, dynamic>?;
        if (pickupData != null) {
          delivery.gpsPickup = LatLng()
            ..latitude = (pickupData['latitude'] as num).toDouble()
            ..longitude = (pickupData['longitude'] as num).toDouble();
        } else {
          delivery.gpsPickup = null;
        }

        final dropoffData = serverData['gpsDropoff'] as Map<String, dynamic>?;
        if (dropoffData != null) {
          delivery.gpsDropoff = LatLng()
            ..latitude = (dropoffData['latitude'] as num).toDouble()
            ..longitude = (dropoffData['longitude'] as num).toDouble();
        } else {
          delivery.gpsDropoff = null;
        }

        delivery.pickupPhotoRef = serverData['pickupPhotoRef'] as String?;
        delivery.dropoffPhotoRef = serverData['dropoffPhotoRef'] as String?;
        delivery.disputeReason = serverData['disputeReason'] as String?;

        // Timestamps
        final pickedUpAtStr = serverData['pickedUpAt'] as String?;
        delivery.pickedUpAt = pickedUpAtStr != null
            ? DateTime.parse(pickedUpAtStr).toUtc()
            : null;

        final confirmedAtStr = serverData['confirmedAt'] as String?;
        delivery.confirmedAt = confirmedAtStr != null
            ? DateTime.parse(confirmedAtStr).toUtc()
            : null;

        delivery.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        delivery.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        delivery.version = serverVersion;

        await _isar.deliverys.put(delivery);
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
      final trip = await _isar.trips
          .filter()
          .tripIdEqualTo(operation.entityId)
          .findFirst();

      if (trip == null) {
        throw ConflictResolutionException(
          'Trip not found in local database: ${operation.entityId}',
        );
      }

      await _isar.writeTxn(() async {
        trip.truckerId = serverData['truckerId'] as String;
        trip.truckerName = serverData['truckerName'] as String;
        trip.truckerPhone = serverData['truckerPhone'] as String;
        trip.fromWoreda = serverData['fromWoreda'] as String;
        trip.toWoreda = serverData['toWoreda'] as String;
        trip.departureDate =
            DateTime.parse(serverData['departureDate'] as String).toUtc();
        trip.capacity = (serverData['capacity'] as num).toDouble();
        trip.pricePerQuintal =
            (serverData['pricePerQuintal'] as num).toDouble();

        // Parse status enum
        final statusStr = serverData['status'] as String;
        trip.status = TripStatus.values.firstWhere(
          (e) => e.name == statusStr,
          orElse: () => TripStatus.open,
        );

        // Parse matched listings
        final matchedRaw = serverData['matchedListings'] as List<dynamic>?;
        trip.matchedListings = matchedRaw?.cast<String>() ?? <String>[];

        // Timestamps
        trip.createdAt =
            DateTime.parse(serverData['createdAt'] as String).toUtc();
        trip.updatedAt =
            DateTime.parse(serverData['updatedAt'] as String).toUtc();

        // CRITICAL: Update version to server version
        trip.version = serverVersion;

        await _isar.trips.put(trip);
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
}

/// Exception thrown when conflict resolution fails.
class ConflictResolutionException implements Exception {
  final String message;

  const ConflictResolutionException(this.message);

  @override
  String toString() => 'ConflictResolutionException: $message';
}
