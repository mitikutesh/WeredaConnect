import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';

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
