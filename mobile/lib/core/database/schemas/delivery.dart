import 'package:isar/isar.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart'; // Import for LatLng

part 'delivery.g.dart';

/// Represents a delivery event linking a trip to a crop listing.
/// Tracks the physical handoff of crops from farmer → trucker → buyer.
///
/// Ethiopian context:
/// - QR code verification for low-literacy users
/// - GPS proof for trust in low-trust environments
/// - Photo evidence due to limited legal recourse in disputes
@collection
class Delivery {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned delivery ID (UUID)
  @Index(unique: true)
  late String deliveryId;

  /// Trip ID this delivery is part of
  @Index()
  late String tripId;

  /// Crop listing ID being delivered
  @Index()
  late String listingId;

  /// Farmer's user ID
  @Index()
  late String farmerId;

  /// Farmer's display name
  late String farmerName;

  /// Trucker's user ID
  @Index()
  late String truckerId;

  /// Trucker's display name
  late String truckerName;

  /// Buyer's user ID
  @Index()
  late String buyerId;

  /// Buyer's display name
  late String buyerName;

  /// QR token for verification at pickup/dropoff
  late String qrToken;

  /// QR nonce persisted by generator for server-side cross-validation.
  String? qrNonce;

  /// Delivery type. `null`/absent = a normal trucker delivery; `self_pickup` =
  /// the buyer collects at the farm (no trucker). Governs who may create the
  /// delivery and how it transitions (see server SyncFunction).
  String? deliveryType;

  /// The order this delivery fulfils (SIM-14). Set on local create and from the
  /// server DTO on pull; null only for deliveries pulled from a server that
  /// predates the field.
  String? orderId;

  /// GPS coordinates at pickup location (farmer's location)
  LatLng? gpsPickup;

  /// GPS coordinates at dropoff location (buyer's location)
  LatLng? gpsDropoff;

  /// Reference to pickup photo evidence file
  String? pickupPhotoRef;

  /// Reference to dropoff photo evidence file
  String? dropoffPhotoRef;

  /// Current status of the delivery
  @Index()
  @Enumerated(EnumType.name)
  late DeliveryStatus status;

  /// Timestamp when trucker picked up crops (UTC)
  DateTime? pickedUpAt;

  /// Timestamp when delivery was confirmed by buyer (UTC)
  DateTime? confirmedAt;

  /// Reason for dispute, if status is disputed
  String? disputeReason;

  /// When this delivery was created (UTC)
  late DateTime createdAt;

  /// When this delivery was last updated (UTC)
  late DateTime updatedAt;

  /// Version number for optimistic concurrency control
  late int version;
}

/// Status of a delivery in its lifecycle
enum DeliveryStatus {
  /// Delivery created, waiting for pickup
  pending,

  /// Trucker has picked up crops from farmer
  pickedUp,

  /// Crops are in transit to buyer
  inTransit,

  /// Buyer has confirmed receipt of crops
  delivered,

  /// Dispute raised (wrong quantity, quality issue, etc.)
  disputed,

  /// Delivery was cancelled
  cancelled,
}
