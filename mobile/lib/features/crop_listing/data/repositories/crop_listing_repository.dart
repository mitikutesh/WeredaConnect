import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Offline-first repository for crop listing CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class CropListingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  CropListingRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Create a new crop listing.
  ///
  /// Generates a UUID, saves to Isar with status=active and version=1,
  /// then enqueues a sync operation for server upload.
  Future<CropListing> createListing({
    required String farmerId,
    required String cropType,
    required double quantity,
    required double pricePerKg,
    required double latitude,
    required double longitude,
    required String description,
    String? audioNoteRef,
  }) async {
    final listingId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final location = LatLng()
      ..latitude = latitude
      ..longitude = longitude;

    final listing = CropListing()
      ..listingId = listingId
      ..farmerId = farmerId
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = location
      ..description = description
      ..audioNoteRef = audioNoteRef
      ..status = ListingStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.cropListings.put(listing);
    });

    // Enqueue sync operation
    final data = jsonEncode({
      'listingId': listingId,
      'farmerId': farmerId,
      'cropType': cropType,
      'quantity': quantity,
      'pricePerKg': pricePerKg,
      'latitude': latitude,
      'longitude': longitude,
      'description': description,
      'audioNoteRef': audioNoteRef,
      'status': listing.status.name,
      'createdAt': now.toIso8601String(),
      'version': 1,
    });

    await _syncQueue.enqueue(
      userId: farmerId,
      entityType: 'crop_listing',
      entityId: listingId,
      action: 'create',
      data: data,
      operationId: _uuid.v4(),
    );

    return listing;
  }

  /// Get all listings for a specific farmer, sorted by createdAt descending.
  Future<List<CropListing>> getListingsByFarmer(String farmerId) async {
    return _isar.cropListings
        .filter()
        .farmerIdEqualTo(farmerId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get all active listings, optionally filtered by crop type.
  Future<List<CropListing>> getActiveListings({String? cropTypeFilter}) async {
    var query = _isar.cropListings.filter().statusEqualTo(ListingStatus.active);

    if (cropTypeFilter != null) {
      query = query.cropTypeEqualTo(cropTypeFilter);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Mark a listing as expired (soft delete).
  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  }) async {
    final listing = await _isar.cropListings
        .filter()
        .listingIdEqualTo(listingId)
        .findFirst();

    if (listing == null) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      listing.status = ListingStatus.expired;
      listing.updatedAt = now;
      listing.version += 1;
      await _isar.cropListings.put(listing);
    });

    await _syncQueue.enqueue(
      userId: farmerId,
      entityType: 'crop_listing',
      entityId: listingId,
      action: 'delete',
      data: jsonEncode({
        'listingId': listingId,
        'status': ListingStatus.expired.name,
        'updatedAt': now.toIso8601String(),
        'version': listing.version,
      }),
      operationId: _uuid.v4(),
    );
  }
}
