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
