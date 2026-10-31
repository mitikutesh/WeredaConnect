/// Web-safe CropListing model (no Isar dependency).
///
/// Mirrors [core/database/schemas/crop_listing.dart] without Isar annotations
/// or generated code, so it compiles to JavaScript without 64-bit integer errors.
class CropListing {
  int id = 0;

  late String listingId;
  late String farmerId;
  String farmerName = '';
  String farmerPhone = '';
  late String cropType;
  late double quantity;
  late double pricePerKg;
  late LatLng location;
  late String locationName;
  String? placeId;
  late String description;
  double? farmerReliability;
  String? audioNoteRef;
  List<CropPhoto> photos = [];
  late ListingStatus status;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}

/// Geographic coordinate (latitude/longitude).
class LatLng {
  late double latitude;
  late double longitude;
}

/// One photo of a crop listing (see the Isar schema for the field docs).
class CropPhoto {
  String photoId = '';
  String? thumbUrl;
  String? fullUrl;
  String? localPath;
  String contentType = 'image/jpeg';
  int sizeBytes = 0;
  bool uploaded = false;
}

/// Status of a crop listing in its lifecycle.
enum ListingStatus {
  draft,
  active,
  reserved,
  sold,
  expired,
}
