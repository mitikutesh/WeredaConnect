import 'package:isar/isar.dart';

part 'crop_listing.g.dart';

/// Represents a crop listing posted by a farmer.
/// Offline-first: Farmers can create listings without connectivity.
///
/// Ethiopian context:
/// - cropType uses local names (teff, barley, wheat, maize, chickpea, etc.)
/// - quantity measured in quintals (100kg, standard Ethiopian unit)
/// - audioNoteRef points to local voice recording (Amharic/Oromo description)
@collection
class CropListing {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned listing ID (UUID)
  @Index(unique: true)
  late String listingId;

  /// User ID of the farmer who created this listing
  @Index()
  late String farmerId;

  /// Display name of the farmer who created this listing.
  /// Empty for legacy listings created before this field was added.
  String farmerName = '';

  /// Farmer's phone number, denormalized so a buyer can contact them.
  /// Mirrors [Trip.truckerPhone]. Empty for legacy listings or when the
  /// server hasn't supplied it.
  String farmerPhone = '';

  /// Type of crop (teff, wheat, barley, maize, chickpea, etc.)
  /// Stored in English but displayed in localized language
  @Index()
  late String cropType;

  /// Quantity available in quintals (1 quintal = 100kg)
  /// Ethiopian farmers commonly use quintal as measurement
  late double quantity;

  /// Price per kilogram in Ethiopian Birr (ETB)
  late double pricePerKg;

  /// Geographic location of the crop (farm location)
  late LatLng location;

  /// Human-readable location name (woreda/kebele)
  /// Entered by the farmer, used for browse filtering
  @Index()
  late String locationName;

  /// Canonical place id when [locationName] was chosen via the place selector.
  /// Local-only hint (not synced): [locationName] stays the source of truth for
  /// matching. Null for free-text / off-list entries.
  String? placeId;

  /// Text description of the crop condition/quality
  /// Supports Amharic, Afan Oromo, and English
  late String description;

  /// Farmer's reliability score in [0, 1] (listing-conversion track record),
  /// server-injected and denormalized onto the listing. Null when the server
  /// hasn't supplied it (old listings / farmers with no history); the matcher
  /// treats null as a neutral default. Never client-set.
  double? farmerReliability;

  /// Reference to local audio file (voice note)
  /// Format: 'audio/{userId}/{listingId}.m4a'
  /// Null if farmer didn't record voice note
  String? audioNoteRef;

  /// The listing's photos in display order, the first being the cover
  /// (crop-listing-api.md 1.6.0: 2 to 4 on every new listing). Empty for
  /// listings made before photos existed.
  List<CropPhoto> photos = [];

  /// Current status of the listing
  @Index()
  @Enumerated(EnumType.name)
  late ListingStatus status;

  /// Timestamp when listing was created (UTC)
  @Index()
  late DateTime createdAt;

  /// Timestamp of last update (UTC)
  late DateTime updatedAt;

  /// Version number for optimistic concurrency control
  late int version;
}

/// Geographic coordinate (latitude/longitude)
/// Embedded object (not a separate collection)
@embedded
class LatLng {
  /// Latitude in decimal degrees
  /// Ethiopian range: ~3° to 15° North
  late double latitude;

  /// Longitude in decimal degrees
  /// Ethiopian range: ~33° to 48° East
  late double longitude;
}

/// One photo of a crop listing (crop-listing-api.md 1.6.0).
///
/// The server keeps two renditions per photo, a 320 px thumb and a 1024 px
/// full image, read through short-lived presigned URLs. The farmer's own
/// device also keeps the photo it took, so the listing shows it offline.
@embedded
class CropPhoto {
  /// UUID made on the device; names the photo on the server.
  String photoId = '';

  /// Presigned URL of the 320 px thumb, from the last server read. Expires
  /// (6 hours), so images are cached by [photoId], never by this URL.
  String? thumbUrl;

  /// Presigned URL of the 1024 px image. Only `GET /crops/{id}` hands it out
  /// (1 hour), so it is usually null in the marketplace cache.
  String? fullUrl;

  /// The photo as this device took it (app-private file). Null for photos
  /// read from the server.
  String? localPath;

  /// MIME type of [localPath] (`image/jpeg`, `image/png` or `image/webp`).
  String contentType = 'image/jpeg';

  /// Length of [localPath] in bytes, signed into the upload URL.
  int sizeBytes = 0;

  /// Whether [localPath] reached the incoming bucket. The sync executor holds
  /// the listing's create until every photo is uploaded.
  bool uploaded = false;
}

/// Status of a crop listing in its lifecycle
enum ListingStatus {
  /// Farmer is still editing, not visible to truckers/buyers
  draft,

  /// Published and available for matching
  active,

  /// Matched with a trip, pending pickup
  reserved,

  /// Successfully sold and delivered
  sold,

  /// Listing expired (farmer-set expiration or 30 days)
  expired,
}
