import 'package:wereda_connect/core/models/location_ping.dart';

/// What happened to a ping the check-in engine handed over.
enum PingSendResult {
  /// Waits in the sync queue (mobile) or for the next try (web).
  queued,

  /// The server stored it (web).
  sent,

  /// The server answered `LOCATION_SHARING_ENDED`: stop sharing for the trip.
  sharingEnded,

  /// Refused for good, or could not be queued: dropped.
  dropped,
}

/// Stores the trucker's check-ins and gets them to the server (LT-08). The
/// mobile repository writes Isar and the sync queue; the web one keeps them
/// in memory and posts `/sync` at once.
abstract class BaseLocationPingRepository {
  /// Store [ping] and send it (or queue it). [userId] is the trucker.
  Future<PingSendResult> record(LocationPing ping, {required String userId});

  /// The latest `fix` of [tripId] the phone recorded, or null.
  Future<LocationPing?> latestFix(String tripId, {required String userId});

  /// The latest ping of any kind of [tripId], or null.
  Future<LocationPing?> latest(String tripId, {required String userId});

  /// Every ping of [tripId] the phone keeps, oldest first.
  Future<List<LocationPing>> pingsFor(String tripId, {required String userId});

  /// How many pings of [tripId] still wait to reach the server.
  Future<int> unsentCount(String tripId, {required String userId});

  /// Drop the pings of [tripId] that still wait (its pickup was refused),
  /// only those captured after [capturedAfter] when it is given.
  Future<int> dropUnsent(
    String tripId, {
    required String userId,
    DateTime? capturedAfter,
  });

  /// Try again what waits (web). Returns the trips whose sharing ended. The
  /// mobile sync executor does this itself, so mobile returns nothing.
  Future<Set<String>> flush({required String userId});

  /// Remove rows captured before [cutoff] (the server keeps 60 days).
  Future<void> pruneBefore(DateTime cutoff);
}
