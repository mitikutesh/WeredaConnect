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

  /// QR token for verification at pickup/dropoff
  /// Farmer scans QR to confirm pickup, buyer scans to confirm delivery
  late String qrToken;

  /// GPS coordinates at pickup location (farmer's location)
  /// Null until trucker confirms pickup
  LatLng? gpsPickup;

  /// GPS coordinates at dropoff location (buyer's location)
  /// Null until trucker confirms delivery
  LatLng? gpsDropoff;

  /// Reference to photo evidence file
  /// Format: 'photos/{deliveryId}/{timestamp}.jpg'
  /// Null if no photo taken (optional but recommended)
  String? photoEvidenceRef;

  /// Current status of the delivery
  @Index()
  @Enumerated(EnumType.name)
  late DeliveryStatus status;

  /// Timestamp when delivery was confirmed (UTC)
  /// Null until buyer confirms receipt
  DateTime? confirmedAt;

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
}
