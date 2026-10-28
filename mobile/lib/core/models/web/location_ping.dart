/// Web-safe LocationPing model (no Isar dependency).
///
/// Mirrors [core/database/schemas/location_ping.dart] without Isar
/// annotations or generated code. On web the check-in engine keeps these in
/// memory for the open page only (no queue, no background).
class LocationPing {
  int id = 0;

  late String pingId;
  late String tripId;
  late String truckerId;
  late String kind;
  String? source;
  late DateTime capturedAt;
  double? latitude;
  double? longitude;
  double? accuracyM;
  bool mocked = false;
  DateTime? pausedUntil;
}
