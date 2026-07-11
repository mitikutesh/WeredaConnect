import 'package:wereda_connect/core/models/crop_listing.dart';

/// Abstract base for crop listing repositories.
///
/// Extracted from CropListingRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
/// No Isar or SyncQueue types leak into this contract.
abstract class BaseCropListingRepository {
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
    String? farmerPhone,
    String? placeId,
  });

  Future<List<CropListing>> getListingsByFarmer(String farmerId);

  Future<List<CropListing>> getListingsByFarmerFiltered({
    required String farmerId,
    String? statusFilter,
    String? cropTypeFilter,
  });

  Future<List<CropListing>> getActiveListings({
    String? cropTypeFilter,
    String? weredaFilter,
    double? priceMin,
    double? priceMax,
  });

  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  });

  /// A single listing by id, or null when it isn't reachable (not synced
  /// locally / not visible to the caller / offline on web). Used by chat
  /// threads to open the crop they're linked to.
  Future<CropListing?> getListingById(String listingId);
}
