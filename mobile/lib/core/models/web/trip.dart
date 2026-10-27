/// Web-safe Trip model (no Isar dependency).
class Trip {
  int id = 0;

  late String tripId;
  late String truckerId;
  late String truckerName;
  late String truckerPhone;
  String? vehiclePlate;
  String? vehicleDescription;
  late String fromWoreda;
  late String toWoreda;
  String? fromPlaceId;
  String? toPlaceId;
  late DateTime departureDate;
  late double capacity;
  late double pricePerQuintal;

  /// ESC-15: booked quintals and the room left (server-written; null = none booked).
  double? bookedQuintals;
  double? remainingQuintals;
  late TripStatus status;
  late List<String> matchedListings;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}

/// Status of a trip in its lifecycle.
enum TripStatus {
  open,
  matched,
  inTransit,
  delivered,
  cancelled,
}
