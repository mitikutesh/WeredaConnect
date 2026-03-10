import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';

/// Web-only crop listing repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
/// Write operations POST to /sync (same endpoint as mobile offline sync).
class WebCropListingRepository extends BaseCropListingRepository {
  static const _uuid = Uuid();
  final ApiClient _api;

  WebCropListingRepository({required ApiClient api}) : _api = api;

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
    String? audioNoteRef,
    String? woreda,
    String? farmerName,
  }) async {
    final listingId =
        'lst_${DateTime.now().millisecondsSinceEpoch}_${farmerId.hashCode.abs()}';
    final now = DateTime.now().toUtc().toIso8601String();

    final payload = <String, dynamic>{
      'farmerId': farmerId,
      if (farmerName != null) 'farmerName': farmerName,
      'cropType': cropType,
      'quantity': quantity,
      'pricePerKg': pricePerKg,
      'latitude': latitude,
      'longitude': longitude,
      'locationName': locationName,
      'description': description,
      'status': 'available',
      'listingId': listingId,
      'createdAt': now,
      if (audioNoteRef != null) 'audioNoteRef': audioNoteRef,
      if (woreda != null) 'woreda': woreda,
    };

    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'crop_listing',
            'action': 'create',
            'entityId': listingId,
            'timestamp': now,
            'idempotencyKey': 'web_create_$listingId',
            'data': payload,
          },
        ],
      },
    );

    final listing = CropListing()
      ..listingId = listingId
      ..farmerId = farmerId
      ..farmerName = farmerName ?? ''
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = (LatLng()
        ..latitude = latitude
        ..longitude = longitude)
      ..locationName = locationName
      ..description = description
      ..audioNoteRef = audioNoteRef
      ..status = ListingStatus.active
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
    return listing;
  }

  @override
  Future<List<CropListing>> getListingsByFarmer(String farmerId) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/crops',
      queryParameters: {'farmerId': farmerId},
    );
    return _parseListings(data);
  }

  @override
  Future<List<CropListing>> getListingsByFarmerFiltered({
    required String farmerId,
    String? statusFilter,
    String? cropTypeFilter,
  }) async {
    final params = <String, dynamic>{'farmerId': farmerId};
    if (statusFilter != null) params['status'] = statusFilter;
    if (cropTypeFilter != null) params['cropType'] = cropTypeFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/crops',
      queryParameters: params,
    );
    return _parseListings(data);
  }

  @override
  Future<List<CropListing>> getActiveListings({
    String? cropTypeFilter,
    String? weredaFilter,
    double? priceMin,
    double? priceMax,
  }) async {
    // Server stores listings with status=available (not "active")
    final params = <String, dynamic>{'status': 'available'};
    if (cropTypeFilter != null) params['cropType'] = cropTypeFilter;
    if (weredaFilter != null) params['woreda'] = weredaFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/crops',
      queryParameters: params,
    );
    var listings = _parseListings(data);

    // Client-side price filtering (server may not support price range)
    if (priceMin != null) {
      listings = listings.where((l) => l.pricePerKg >= priceMin).toList();
    }
    if (priceMax != null) {
      listings = listings.where((l) => l.pricePerKg <= priceMax).toList();
    }
    return listings;
  }

  @override
  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
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
            'data': {'farmerId': farmerId},
          },
        ],
      },
    );
  }

  List<CropListing> _parseListings(Map<String, dynamic> data) {
    // API returns listings under "crops" key (not "items")
    final items = data['crops'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
    return items.map((item) {
      final m = item as Map<String, dynamic>;
      return CropListing()
        // API uses "cropId"; sync-created listings use "listingId"
        ..listingId = m['listingId'] as String? ?? m['cropId'] as String? ?? ''
        ..farmerId = m['farmerId'] as String? ?? ''
        ..farmerName = m['farmerName'] as String? ?? ''
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
        ..audioNoteRef = m['audioNoteRef'] as String?
        ..status = _parseListingStatus(m['status'] as String?)
        ..createdAt =
            DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
        ..updatedAt =
            DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
        ..version = (m['version'] as num?)?.toInt() ?? 1;
    }).toList();
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
