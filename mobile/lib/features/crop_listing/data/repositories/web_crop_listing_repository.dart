import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/owner_reputation_wire.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_uploader.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';

/// Web-only crop listing repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
/// Write operations POST to /sync (same endpoint as mobile offline sync).
/// A create uploads its photos first (crop-listing-api.md 1.6.0): the web has
/// no queue to hold it, so a photo that does not go up fails the save.
class WebCropListingRepository extends BaseCropListingRepository {
  static const _uuid = Uuid();
  final ApiClient _api;
  final CropPhotoUploader _photoUploader;

  WebCropListingRepository({
    required ApiClient api,
    CropPhotoUploader? photoUploader,
  })  : _api = api,
        _photoUploader = photoUploader ?? CropPhotoUploader(api: api);

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
    final uploaded = await _photoUploader.upload(listingId: id, photos: photos);
    if (uploaded.length != photos.length) {
      throw CropPhotoUploadException(
        '${photos.length - uploaded.length} of ${photos.length} photos did '
        'not upload',
      );
    }
    final now = DateTime.now().toUtc().toIso8601String();

    final payload = <String, dynamic>{
      'farmerId': farmerId,
      if (farmerName != null) 'farmerName': farmerName,
      if (farmerPhone != null) 'farmerPhone': farmerPhone,
      'cropType': cropType,
      'quantity': quantity,
      'pricePerKg': pricePerKg,
      'latitude': latitude,
      'longitude': longitude,
      'locationName': locationName,
      'description': description,
      'status': 'available',
      'listingId': id,
      'createdAt': now,
      'photoIds': [for (final photo in photos) photo.photoId],
      if (audioNoteRef != null) 'audioNoteRef': audioNoteRef,
      if (woreda != null) 'woreda': woreda,
    };

    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'crop_listing',
            'action': 'create',
            'entityId': id,
            'timestamp': now,
            'idempotencyKey':
                '$farmerId:crop_listing:$id:create:${DateTime.now().toUtc().millisecondsSinceEpoch}',
            'data': payload,
          },
        ],
      },
    );
    // Surface server-side rejections (e.g. CROP_UNAUTHORIZED) instead of
    // silently reporting success for a listing that was never persisted.
    ensureSyncSucceeded(response);

    final listing = CropListing()
      ..listingId = id
      ..farmerId = farmerId
      ..farmerName = farmerName ?? ''
      ..farmerPhone = farmerPhone ?? ''
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = (LatLng()
        ..latitude = latitude
        ..longitude = longitude)
      ..locationName = locationName
      ..placeId = placeId
      ..description = description
      ..audioNoteRef = audioNoteRef
      ..photos = [
        for (final photo in photos)
          CropPhoto()
            ..photoId = photo.photoId
            ..contentType = photo.contentType
            ..sizeBytes = photo.sizeBytes
            ..uploaded = true,
      ]
      ..status = ListingStatus.active
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
    return listing;
  }

  /// The farmer's own listings, every page (FUP-126: this read the first
  /// page only, so a farmer with more than 20 listings did not see the rest).
  /// The server scopes a farmer to their own listings whatever is asked.
  @override
  Future<List<CropListing>> getListingsByFarmer(String farmerId) =>
      _allPages({'farmerId': farmerId});

  @override
  Future<List<CropListing>> getListingsByFarmerFiltered({
    required String farmerId,
    String? statusFilter,
    String? cropTypeFilter,
  }) =>
      _allPages({
        'farmerId': farmerId,
        if (statusFilter != null) 'status': statusFilter,
        if (cropTypeFilter != null) 'cropType': cropTypeFilter,
      });

  @override
  Future<List<CropListing>> getActiveListings({
    String? cropTypeFilter,
    String? searchQuery,
    double? priceMin,
    double? priceMax,
  }) async {
    // Buyer browse MUST return ALL matching crops (role-visibility rule), so
    // read every page. Server stores listings with status=available (not
    // "active").
    var listings = await _allPages({
      'status': 'available',
      if (cropTypeFilter != null) 'cropType': cropTypeFilter,
    });

    // The search box is free text over crop, farmer and place. It runs here,
    // not on the server, with the same rules as the Isar repo (SIM-51).
    listings = BaseCropListingRepository.filterBySearch(listings, searchQuery);

    // Client-side price filtering (server may not support price range)
    if (priceMin != null) {
      listings = listings.where((l) => l.pricePerKg >= priceMin).toList();
    }
    if (priceMax != null) {
      listings = listings.where((l) => l.pricePerKg <= priceMax).toList();
    }
    return listings;
  }

  /// Every page of `GET /crops` for [params], following `nextToken`, 50
  /// listings a page (the server's largest, crop-listing-api.md 1.9.0).
  /// Stops after [maxPages] pages as a guard against a token that never
  /// ends.
  Future<List<CropListing>> _allPages(Map<String, dynamic> params) async {
    final listings = <CropListing>[];
    String? nextToken;
    var pages = 0;
    do {
      final data = await _api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: {
          ...params,
          'limit': '50',
          if (nextToken != null) 'nextToken': nextToken,
        },
      );
      listings.addAll(_parseListings(data));
      nextToken = data['nextToken'] as String?;
      pages++;
    } while (nextToken != null && nextToken.isNotEmpty && pages < maxPages);
    return listings;
  }

  /// Most pages one list read follows (2,500 listings), as the web trip
  /// repository.
  @visibleForTesting
  static const int maxPages = 50;

  @override
  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  }) async {
    // The server deletes only at the version it holds (`#v = :expectedVersion`)
    // and reads a missing version as 0, so a versionless delete always
    // conflicted and silently did nothing. Web keeps no local copy: read it.
    final version = await _currentListingVersion(listingId);
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'crop_listing',
            'action': 'delete',
            'entityId': listingId,
            'timestamp': now,
            'idempotencyKey': 'web_delete_${listingId}_$now',
            'data': {'farmerId': farmerId, 'version': version},
          },
        ],
      },
    );
    ensureSyncSucceeded(response);
  }

  /// The listing's current server version. Unlike [getListingById] this
  /// throws when the read fails, so a delete never goes out with a guessed
  /// version.
  Future<int> _currentListingVersion(String listingId) async =>
      (await _currentListing(listingId)).version;

  /// The listing as the server holds it now; throws when the read fails.
  Future<CropListing> _currentListing(String listingId) async {
    final data = await _api.get<Map<String, dynamic>>('/crops/$listingId');
    final envelope = data['crop'] ?? data['item'];
    final map = envelope is Map<String, dynamic>
        ? envelope
        : (data.containsKey('cropId') || data.containsKey('listingId')
            ? data
            : null);
    if (map == null) {
      throw NotFoundException('Crop listing $listingId not found.');
    }
    return _parseListingMap(map);
  }

  /// SIM-56: straight to the server, at the version it holds now (web keeps
  /// no copy to version from). A refusal throws ([ensureSyncSucceeded]) and
  /// nothing changed.
  @override
  Future<CropListing> updateListing({
    required String listingId,
    required String farmerId,
    required double quantity,
    required double pricePerKg,
    required String description,
  }) async {
    final current = await _currentListing(listingId);
    if (current.farmerId != farmerId) {
      throw StateError('Crop listing $listingId is not this farmer\'s.');
    }
    final now = DateTime.now().toUtc();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'crop_listing',
            'action': 'update',
            'entityId': listingId,
            'timestamp': now.toIso8601String(),
            'idempotencyKey':
                '$farmerId:crop_listing:$listingId:update:${now.millisecondsSinceEpoch}',
            'data': {
              'listingId': listingId,
              'quantity': quantity,
              'pricePerKg': pricePerKg,
              'description': description,
              'version': current.version,
            },
          },
        ],
      },
    );
    ensureSyncSucceeded(response);
    return current
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..description = description
      ..updatedAt = now
      ..version = current.version + 1;
  }

  @override
  Future<CropListing?> getListingById(String listingId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>('/crops/$listingId');
      // GET /crops/{id} returns {"crop": {...}} (GetCropResponse, camelCase).
      // Accept the `item` envelope and a bare object too, so a response-shape
      // change never silently degrades into a spurious "not found".
      final envelope = data['crop'] ?? data['item'];
      if (envelope is Map<String, dynamic>) return _parseListingMap(envelope);
      if (data.containsKey('cropId') || data.containsKey('listingId')) {
        return _parseListingMap(data);
      }
      return null;
    } on Exception {
      return null;
    }
  }

  List<CropListing> _parseListings(Map<String, dynamic> data) {
    // API returns listings under "crops" key (not "items")
    final items = data['crops'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
    return items
        .map((item) => _parseListingMap(item as Map<String, dynamic>))
        .toList();
  }

  CropListing _parseListingMap(Map<String, dynamic> m) {
    return CropListing()
      // API uses "cropId"; sync-created listings use "listingId"
      ..listingId = m['listingId'] as String? ?? m['cropId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..farmerPhone = m['farmerPhone'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      // API uses "quantityKg"; sync payload uses "quantity"
      ..quantity = (m['quantity'] as num?)?.toDouble() ??
          (m['quantityKg'] as num?)?.toDouble() ??
          0
      ..pricePerKg = (m['pricePerKg'] as num?)?.toDouble() ?? 0
      ..location = (LatLng()
        ..latitude = (m['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (m['longitude'] as num?)?.toDouble() ?? 0)
      ..locationName =
          m['locationName'] as String? ?? m['woreda'] as String? ?? ''
      ..description = m['description'] as String? ?? ''
      ..readOwnerReputation(m)
      ..audioNoteRef = m['audioNoteRef'] as String?
      ..photos = parseCropPhotos(m['photos'])
      ..status = _parseListingStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  ListingStatus _parseListingStatus(String? s) {
    // API returns "available" which maps to ListingStatus.active
    if (s == 'available') return ListingStatus.active;
    return ListingStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => ListingStatus.active,
    );
  }
}
