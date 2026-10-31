import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_file_store.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';

/// Offline-first repository for crop listing CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class CropListingRepository extends BaseCropListingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  final CropPhotoFileStore _photoFiles;
  static const _uuid = Uuid();

  CropListingRepository({
    required Isar isar,
    required SyncQueue syncQueue,
    CropPhotoFileStore? photoFiles,
  })  : _isar = isar,
        _syncQueue = syncQueue,
        _photoFiles = photoFiles ?? CropPhotoFileStore();

  /// Create a new crop listing.
  ///
  /// Keeps the photos in app-private files, saves to Isar with status=active
  /// and version=1, then enqueues a sync operation for server upload. The
  /// photos themselves go up just before that operation is sent
  /// (`CropPhotoSyncPreflight`), so a listing made offline keeps them.
  @override
  Future<CropListing> createListing({
    required String farmerId,
    required String cropType,
    required double quantity,
    required double pricePerKg,
    required double latitude,
    required double longitude,
    required String locationName,
    required String description,
    required List<CropPhotoDraft> photos,
    String? audioNoteRef,
    String? woreda,
    String? farmerName,
    String? farmerPhone,
    String? placeId,
    String? listingId,
  }) async {
    BaseCropListingRepository.checkPhotoCount(photos);
    final id = listingId ?? _uuid.v4();
    final now = DateTime.now().toUtc();

    final storedPhotos = <CropPhoto>[];
    for (final photo in photos) {
      final path = await _photoFiles.save(id, photo);
      storedPhotos.add(
        CropPhoto()
          ..photoId = photo.photoId
          ..localPath = path
          ..contentType = photo.contentType
          ..sizeBytes = photo.sizeBytes,
      );
    }

    final location = LatLng()
      ..latitude = latitude
      ..longitude = longitude;

    final listing = CropListing()
      ..listingId = id
      ..farmerId = farmerId
      ..farmerName = farmerName ?? ''
      ..farmerPhone = farmerPhone ?? ''
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = location
      ..locationName = locationName
      ..placeId = placeId
      ..description = description
      ..audioNoteRef = audioNoteRef
      ..photos = storedPhotos
      ..status = ListingStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.cropListings.put(listing);
    });

    // Enqueue sync operation
    final data = jsonEncode({
      'listingId': id,
      'farmerId': farmerId,
      'farmerName': farmerName ?? '',
      'farmerPhone': farmerPhone ?? '',
      'cropType': cropType,
      'quantity': quantity,
      'pricePerKg': pricePerKg,
      'latitude': latitude,
      'longitude': longitude,
      'locationName': locationName,
      'description': description,
      'audioNoteRef': audioNoteRef,
      'woreda': woreda ?? '',
      'photoIds': [for (final photo in photos) photo.photoId],
      'status': listing.status.name,
      'createdAt': now.toIso8601String(),
      'version': 1,
    });

    // The local Isar write above is authoritative (offline-first). Enqueue
    // failures (queue full / duplicate idempotency key) must NOT surface as a
    // save failure — the listing is persisted; the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: farmerId,
        entityType: 'crop_listing',
        entityId: id,
        action: 'create',
        data: data,
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[CropListingRepository] listing saved locally but sync enqueue '
        'failed: $e',
      );
    }

    return listing;
  }

  /// Get all listings for a specific farmer, sorted by createdAt descending.
  @override
  Future<List<CropListing>> getListingsByFarmer(String farmerId) async {
    return _isar.cropListings
        .filter()
        .farmerIdEqualTo(farmerId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  @override
  Future<CropListing?> getListingById(String listingId) {
    return _isar.cropListings.filter().listingIdEqualTo(listingId).findFirst();
  }

  /// Get listings for a farmer, optionally filtered by status and crop type.
  @override
  Future<List<CropListing>> getListingsByFarmerFiltered({
    required String farmerId,
    String? statusFilter,
    String? cropTypeFilter,
  }) async {
    var query = _isar.cropListings.filter().farmerIdEqualTo(farmerId);

    if (statusFilter != null) {
      final status = ListingStatus.values.byName(statusFilter);
      query = query.statusEqualTo(status);
    }

    if (cropTypeFilter != null) {
      query = query.cropTypeEqualTo(cropTypeFilter);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get all active listings with optional filters.
  ///
  /// [cropTypeFilter]: exact match on the crop type key.
  /// [searchQuery]: the Buyer's search box, matched against crop, farmer and
  /// place by [BaseCropListingRepository.filterBySearch] (SIM-51).
  /// [priceMin] / [priceMax]: price per kg range in ETB.
  ///
  /// The search runs in memory after the Isar query so its rules (any field
  /// per word, every word required, crop names in all three languages) stay
  /// identical to the web repository.
  @override
  Future<List<CropListing>> getActiveListings({
    String? cropTypeFilter,
    String? searchQuery,
    double? priceMin,
    double? priceMax,
  }) async {
    var query = _isar.cropListings.filter().statusEqualTo(ListingStatus.active);

    if (cropTypeFilter != null) {
      query = query.cropTypeEqualTo(cropTypeFilter);
    }

    if (priceMin != null) {
      query = query.pricePerKgGreaterThan(priceMin, include: true);
    }

    if (priceMax != null) {
      query = query.pricePerKgLessThan(priceMax, include: true);
    }

    final results = await query.sortByCreatedAtDesc().findAll();
    return BaseCropListingRepository.filterBySearch(results, searchQuery);
  }

  /// Mark a listing as expired (soft delete).
  @override
  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  }) async {
    final listing = await _isar.cropListings
        .filter()
        .listingIdEqualTo(listingId)
        .findFirst();

    if (listing == null) return;
    if (listing.farmerId != farmerId) return;

    final now = DateTime.now().toUtc();

    // The server's optimistic-concurrency check (sync-protocol.md) expects the
    // version the client *had* before the change — the version the server still
    // holds. Capture it before bumping and send THAT; the server applies the op
    // when `clientVersion == serverVersion` and stores `clientVersion + 1`.
    // Sending the already-incremented version instead guarantees a version
    // conflict, after which the server-wins ConflictResolver reverts the local
    // status, so the delete silently un-expires the listing.
    final baseVersion = listing.version;

    await _isar.writeTxn(() async {
      listing.status = ListingStatus.expired;
      listing.updatedAt = now;
      // Bump to what the server will store on success, keeping the two in
      // lockstep so any later transition sends the correct base version.
      listing.version = baseVersion + 1;
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
        'version': baseVersion,
      }),
      operationId: _uuid.v4(),
    );
  }
}
