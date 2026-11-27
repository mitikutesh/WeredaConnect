import 'package:isar/isar.dart';

part 'location_ping.g.dart';

/// One check-in (`fix`) or sharing event (`paused`, `resumed`, `declined`) the
/// trucker's phone recorded for a trip after pickup (LT-08,
/// `specs/contracts/live-trip-api.md` section 1).
///
/// Offline-first: written here when the phone records it, and sent as a
/// create-only `location_ping` operation through the sync queue. The row is
/// the phone's own log: the check-in engine reads the last fix and the last
/// capture time from it after a restart, and the trucker's trip screens show
/// it. A ping the queue coalesces away before it was sent (more than 24
/// waiting) is removed here too, so the log matches what the server gets.
@collection
class LocationPing {
  /// Isar auto-generated ID (internal use only).
  Id id = Isar.autoIncrement;

  /// The sync `entityId`: `{tripId}#{capturedAt in epoch ms}`.
  @Index(unique: true, replace: true)
  late String pingId;

  /// The trip the truck is driving.
  @Index()
  late String tripId;

  /// The trucker who recorded it (the account the queue sends it for).
  @Index()
  late String truckerId;

  /// `fix`, `paused`, `resumed` or `declined`.
  late String kind;

  /// For a `fix`: `hourly`, `scan`, `near_stop`, `manual`, `arrived` or
  /// `start`. Null for an event.
  String? source;

  /// When the phone took the fix or the driver acted (UTC, whole
  /// milliseconds).
  @Index()
  late DateTime capturedAt;

  /// Fix position (null for an event).
  double? latitude;
  double? longitude;

  /// Fix accuracy in metres (null for an event).
  double? accuracyM;

  /// The platform flagged a mock location.
  bool mocked = false;

  /// For `paused`: when sharing resumes by itself (UTC).
  DateTime? pausedUntil;
}
