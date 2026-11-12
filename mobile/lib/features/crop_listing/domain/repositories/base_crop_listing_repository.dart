import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';

/// Abstract base for crop listing repositories.
///
/// Extracted from CropListingRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
/// No Isar or SyncQueue types leak into this contract.
abstract class BaseCropListingRepository {
  /// Creates a listing with its [photos] (2 to 4, crop-listing-api.md 1.6.0;
  /// an [ArgumentError] otherwise). [listingId] is the id the form already
  /// made (it names the voice note and the photos); a new one when null.
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
  });

  /// Refuses a photo count the server would refuse (rule 9), before any
  /// write.
  static void checkPhotoCount(List<CropPhotoDraft> photos) {
    if (!CropPhotoRules.isValidCount(photos.length) ||
        photos.map((photo) => photo.photoId).toSet().length != photos.length) {
      throw ArgumentError.value(
        photos.length,
        'photos',
        'A listing needs ${CropPhotoRules.minPhotos} to '
            '${CropPhotoRules.maxPhotos} distinct photos',
      );
    }
  }

  Future<List<CropListing>> getListingsByFarmer(String farmerId);

  Future<List<CropListing>> getListingsByFarmerFiltered({
    required String farmerId,
    String? statusFilter,
    String? cropTypeFilter,
  });

  /// Every active listing (Buyer marketplace browse), narrowed by the crop
  /// chip, the search box ([searchQuery], see [filterBySearch]) and the price
  /// range.
  Future<List<CropListing>> getActiveListings({
    String? cropTypeFilter,
    String? searchQuery,
    double? priceMin,
    double? priceMax,
  });

  Future<void> deleteListing({
    required String listingId,
    required String farmerId,
  });

  /// SIM-56: the farmer's edit of [listing], their own, as the form showed
  /// it: how much is left ([quantity], quintals), the price ([pricePerKg],
  /// ETB) and the [description]. Only the non-null fields are sent (the
  /// `crop_listing` update takes partial data), at [listing]'s version: the
  /// form's base, never a version read at save time, so an edit made before
  /// an order took stock conflicts instead of putting that stock back
  /// (crop-listing-api.md 1.13.0). Returns the listing as saved.
  ///
  /// Throws `SyncConflictException` when the listing changed since the form
  /// read it, [StateError] when it is not [farmerId]'s or not found, and
  /// [ArgumentError] when nothing changes.
  Future<CropListing> updateListing({
    required CropListing listing,
    required String farmerId,
    double? quantity,
    double? pricePerKg,
    String? description,
  });

  /// The `data` of a SIM-56 edit: the listing id, the changed fields only,
  /// and the base [version]. Shared by the Isar and web repositories.
  static Map<String, dynamic> editPayload({
    required String listingId,
    required int version,
    double? quantity,
    double? pricePerKg,
    String? description,
  }) {
    if (quantity == null && pricePerKg == null && description == null) {
      throw ArgumentError('A listing edit must change something.');
    }
    return {
      'listingId': listingId,
      if (quantity != null) 'quantity': quantity,
      if (pricePerKg != null) 'pricePerKg': pricePerKg,
      if (description != null) 'description': description,
      'version': version,
    };
  }

  /// A single listing by id, or null when it isn't reachable (not synced
  /// locally / not visible to the caller / offline on web). Used by chat
  /// threads to open the crop they're linked to.
  Future<CropListing?> getListingById(String listingId);

  /// Narrows [listings] to the ones matching the Buyer's search box (SIM-51).
  ///
  /// The query is split on whitespace and every word must hit at least one
  /// field: the farmer's name, the place, or the crop (its stored key, or its
  /// name in English, Amharic or Afan Oromo). Matching is a case-insensitive
  /// substring, so "teff adama" finds teff sold in Adama. A null or blank
  /// query returns [listings] unchanged, and the order is kept. Both the Isar
  /// and the web repository call this so the two platforms search alike.
  static List<CropListing> filterBySearch(
    List<CropListing> listings,
    String? query,
  ) {
    final terms = (query ?? '')
        .toLowerCase()
        .split(RegExp(r'\s+'))
        .where((term) => term.isNotEmpty)
        .toList();
    if (terms.isEmpty) return listings;
    final cropKeysByTerm = [
      for (final term in terms) CropType.keysMatching(term),
    ];
    return listings.where((listing) {
      final farmer = listing.farmerName.toLowerCase();
      final place = listing.locationName.toLowerCase();
      final crop = listing.cropType.trim().toLowerCase();
      for (var i = 0; i < terms.length; i++) {
        final term = terms[i];
        final hit = farmer.contains(term) ||
            place.contains(term) ||
            crop.contains(term) ||
            cropKeysByTerm[i].contains(crop);
        if (!hit) return false;
      }
      return true;
    }).toList();
  }
}
