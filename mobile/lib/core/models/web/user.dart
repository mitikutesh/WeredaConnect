/// Web-safe User model (no Isar dependency).
class User {
  int id = 0;

  late String userId;
  late String phoneNumber;
  late String name;
  String? fullName;
  String? displayName;

  String get effectiveDisplayName => displayName ?? name;

  String? faydaId;
  String? faydaIdFrontImagePath;
  String? faydaIdBackImagePath;
  String? drivingLicensePath;
  String? buyerType;
  String? companyName;
  bool isVerified = false;
  bool faydaVerified = false;
  DateTime? faydaVerifiedAt;
  late UserRole role;
  late String woreda;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}

/// User roles in the WeredaConnect marketplace.
enum UserRole {
  farmer,
  trucker,
  buyer,
}
