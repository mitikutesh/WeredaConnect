import 'package:isar/isar.dart';

part 'user.g.dart';

/// Represents a user in the WeredaConnect system.
/// This is the offline-first local cache of user data.
///
/// Ethiopian context:
/// - phoneNumber uses +251 format (Ethiopian country code)
/// - woreda is the Ethiopian administrative district
/// - faydaId links to Fayda microfinance for future credit scoring
@collection
class User {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned user ID (UUID)
  @Index(unique: true)
  late String userId;

  /// Ethiopian phone number in E.164 format (+251XXXXXXXXX)
  /// Example: +251911234567
  @Index(unique: true)
  late String phoneNumber;

  /// User's full name (supports Amharic/Afan Oromo scripts)
  late String name;

  /// Optional Fayda microfinance ID for credit scoring
  /// Null if user hasn't linked Fayda account
  String? faydaId;

  /// Local file path for Fayida ID front scan image
  String? faydaIdFrontImagePath;

  /// Local file path for Fayida ID back scan image
  String? faydaIdBackImagePath;

  /// Local file path for driving license scan (trucker only)
  String? drivingLicensePath;

  /// Buyer type: "private" or "company" (buyer only)
  String? buyerType;

  /// Company name when buyerType is "company"
  String? companyName;

  /// Whether user has been verified (ID + OTP)
  bool isVerified = false;

  /// User role in the marketplace
  /// Values: 'farmer', 'trucker', 'buyer'
  @Index()
  @Enumerated(EnumType.name)
  late UserRole role;

  /// Ethiopian woreda (district) for location-based matching
  /// Example: 'Adama', 'Bishoftu', 'Debre Zeit'
  @Index()
  late String woreda;

  /// Timestamp when user was created (UTC)
  late DateTime createdAt;

  /// Timestamp of last update (UTC)
  late DateTime updatedAt;

  /// Version number for optimistic concurrency control
  /// Incremented on each update for conflict resolution
  late int version;
}

/// User roles in the WeredaConnect marketplace
enum UserRole {
  /// Farmer who lists crops for sale
  farmer,

  /// Trucker who provides transport services
  trucker,

  /// Buyer (wholesaler/retailer) who purchases crops
  buyer,
}
