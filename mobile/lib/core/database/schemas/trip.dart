import 'package:isar/isar.dart';

part 'trip.g.dart';

/// Represents a transport trip posted by a trucker.
/// Offline-first: Truckers can post available capacity without connectivity.
///
/// Ethiopian context:
/// - fromWoreda/toWoreda use Ethiopian district names
/// - capacity measured in quintals (100kg units)
/// - Routes typically: rural woreda → urban markets (Addis Ababa, Adama, etc.)
@collection
class Trip {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned trip ID (UUID)
  @Index(unique: true)
  late String tripId;

  /// User ID of the trucker who posted this trip
  @Index()
  late String truckerId;

  /// Display name of the trucker
  late String truckerName;

  /// Contact phone number (E.164 format, e.g., +251912345678)
  /// Hidden from unauthenticated users in browse results
  late String truckerPhone;

  /// Vehicle license plate number (e.g., 3-AA-12345)
  String? vehiclePlate;

  /// Vehicle description (e.g., Isuzu FTR 10-ton)
  String? vehicleDescription;

  /// Origin woreda (district)
  /// Example: 'Bishoftu', 'Debre Zeit'
  late String fromWoreda;

  /// Destination woreda (district)
  /// Example: 'Addis Ababa', 'Adama'
  late String toWoreda;

  /// Canonical place ids when the woredas were chosen via the place selector.
  /// Local-only hints (not synced): [fromWoreda]/[toWoreda] stay the source of
  /// truth for matching. Null for free-text / off-list entries.
  String? fromPlaceId;
  String? toPlaceId;

  /// Scheduled departure date and time (UTC)
  /// Truckers typically plan 1-3 days ahead
  @Index()
  late DateTime departureDate;

  /// Available capacity in quintals (100kg)
  /// Typical Ethiopian truck: 100-200 quintals capacity
  late double capacity;

  /// Price per quintal for transport (in ETB)
  /// Market rate varies by distance and road conditions
  late double pricePerQuintal;

  /// ESC-15: quintals booked by paid orders, and the room left. Server-written;
  /// null on a trip nobody booked yet (the room left is then [capacity], see
  /// trip_capacity.dart).
  double? bookedQuintals;
  double? remainingQuintals;

  /// ESC-30: the trucker's reliability in whole percent (0 to 100), the
  /// server's `ownerReliabilityPct` copy on the row (reputation-api.md
  /// section 7). Null when the server sent none: the public reputation flag
  /// is off, the trucker has fewer than 3 orders, or the row has no copy yet.
  /// Orders the trip suggestions among equally priced trips. Never client-set.
  int? ownerReliabilityPct;

  /// ESC-31: the rest of the trucker's public reputation copy on the row
  /// (reputation-api.md section 7), sent only while the server's public
  /// reputation flag is on. All null when the server sent none; the cards
  /// then look as they did before. Never client-set, never sent back.
  ///
  /// The public tier: `good` or `low`.
  String? ownerTier;

  /// The buyers' star average minus the penalty deduction, one decimal.
  /// Null while the trucker has no ratings.
  double? ownerEffectiveRating;

  /// How many ratings are behind [ownerEffectiveRating].
  int? ownerRatingCount;

  /// When the server wrote this copy ("As of {date}" on the detail).
  DateTime? ownerReputationAt;

  /// Current status of the trip
  @Index()
  @Enumerated(EnumType.name)
  late TripStatus status;

  /// List of crop listing IDs matched to this trip
  /// Empty when status=open, populated when matched
  late List<String> matchedListings;

  /// Timestamp when trip was created (UTC)
  late DateTime createdAt;

  /// Timestamp of last update (UTC)
  late DateTime updatedAt;

  /// Version number for optimistic concurrency control
  late int version;
}

/// Status of a trip in its lifecycle
enum TripStatus {
  /// Trip posted, waiting for crop listings to match
  open,

  /// Trip matched with crop listings, awaiting pickup
  matched,

  /// Truck has picked up crops and is en route
  inTransit,

  /// Successfully delivered to destination
  delivered,

  /// Trip cancelled by trucker
  cancelled,
}
