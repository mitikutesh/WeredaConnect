import 'package:isar/isar.dart';

part 'buyer_intent.g.dart';

/// Represents a buyer's declared intent to purchase a specific crop.
/// Local-only for MVP: persisted in Isar, no server sync.
///
/// Ethiopian context:
/// - Buyers declare what they need (crop, quantity, budget, destination)
/// - System matches against local Isar DB (offline-first)
/// - Intent persists so buyer is notified when new matches sync
@collection
class BuyerIntent {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Client-generated intent ID (UUID)
  @Index(unique: true)
  late String intentId;

  /// User ID of the buyer who created this intent
  @Index()
  late String buyerId;

  /// Type of crop sought (CropType enum name, e.g. 'teff', 'wheat')
  @Index()
  late String cropType;

  /// Quantity needed in quintals (100kg units)
  late double quantityNeeded;

  /// Maximum acceptable price per kilogram in ETB
  late double maxPricePerKg;

  /// Buyer-editable delivery destination (woreda name)
  /// Defaults to buyer's registered woreda
  late String deliveryWoreda;

  /// Canonical place id when [deliveryWoreda] was chosen via the place selector.
  /// Local-only hint (not synced): the display name in [deliveryWoreda] stays
  /// the source of truth for matching. Null for free-text / off-list entries.
  String? placeId;

  /// Optional date by which the buyer needs delivery. Drives trip date-fit:
  /// trips departing after this date are dropped from the suggested-trip
  /// pairing. Null = no deadline (any trip qualifies).
  DateTime? neededByDate;

  /// Current status of the intent
  @Index()
  @Enumerated(EnumType.name)
  late BuyerIntentStatus status;

  /// Timestamp when intent was created (UTC)
  late DateTime createdAt;

  /// Timestamp of last update (UTC)
  late DateTime updatedAt;

  /// Cached count of matching crop listings
  int matchCount = 0;

  /// When matches were last computed
  DateTime? lastMatchedAt;

  /// When the buyer last viewed match results
  DateTime? lastViewedAt;

  /// Match count at the moment the buyer last viewed results (high-water mark).
  /// Snapshotted by `updateLastViewed`. The "new matches" alert is the
  /// positive delta `matchCount - lastSeenMatchCount`, so it fires only when
  /// genuinely new matches appear — never on a plain re-sync, and clamps to
  /// zero when the count drops.
  int lastSeenMatchCount = 0;

  /// Version number for optimistic concurrency control
  late int version;
}

/// Status of a buyer intent in its lifecycle
enum BuyerIntentStatus {
  /// Intent is active and being matched against listings
  active,

  /// Intent has been fulfilled (buyer placed an order)
  fulfilled,

  /// Intent expired (buyer cancelled or timed out)
  expired,
}
