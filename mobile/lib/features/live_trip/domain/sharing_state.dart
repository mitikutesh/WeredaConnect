/// The trucker's answer to "Share where the truck is?" for one trip.
enum SharingAnswer { shared, declined }

/// The phone's sharing state for one trip.
class TripSharing {
  const TripSharing({
    required this.answer,
    this.pausedUntil,
    this.ended = false,
  });

  final SharingAnswer answer;

  /// While paused: when sharing resumes by itself (UTC).
  final DateTime? pausedUntil;

  /// Sharing stopped for good: every delivery is done, or the server said
  /// `LOCATION_SHARING_ENDED`.
  final bool ended;

  TripSharing copyWith({
    SharingAnswer? answer,
    DateTime? pausedUntil,
    bool clearPause = false,
    bool? ended,
  }) =>
      TripSharing(
        answer: answer ?? this.answer,
        pausedUntil: clearPause ? null : (pausedUntil ?? this.pausedUntil),
        ended: ended ?? this.ended,
      );

  Map<String, Object> toJson() => {
        'answer': answer.name,
        if (pausedUntil != null)
          'pausedUntil': pausedUntil!.toUtc().toIso8601String(),
        if (ended) 'ended': true,
      };

  static TripSharing? fromJson(Object? json) {
    if (json is! Map) return null;
    final answer =
        SharingAnswer.values.where((a) => a.name == json['answer']).firstOrNull;
    if (answer == null) return null;
    return TripSharing(
      answer: answer,
      pausedUntil: DateTime.tryParse('${json['pausedUntil']}')?.toUtc(),
      ended: json['ended'] == true,
    );
  }
}

/// One trucker's sharing state: the trip the phone shares for now and the
/// answers per trip.
class SharingState {
  const SharingState({this.activeTripId, this.trips = const {}});

  /// The trip the cadence runs for (or that shows "Scans only").
  final String? activeTripId;

  final Map<String, TripSharing> trips;

  /// At most this many trips are remembered; the oldest ended ones go first.
  static const maxTrips = 20;

  TripSharing? get active => activeTripId == null ? null : trips[activeTripId!];

  SharingState withTrip(String tripId, TripSharing? sharing) {
    final next = Map<String, TripSharing>.of(trips);
    if (sharing == null) {
      next.remove(tripId);
    } else {
      next.remove(tripId);
      next[tripId] = sharing;
    }
    while (next.length > maxTrips) {
      final oldest = next.entries
              .where((e) => e.value.ended && e.key != activeTripId)
              .firstOrNull ??
          next.entries.where((e) => e.key != activeTripId).firstOrNull;
      if (oldest == null) break;
      next.remove(oldest.key);
    }
    return SharingState(activeTripId: activeTripId, trips: next);
  }

  SharingState withActive(String? tripId) =>
      SharingState(activeTripId: tripId, trips: trips);

  Map<String, Object> toJson() => {
        if (activeTripId != null) 'active': activeTripId!,
        'trips': {
          for (final e in trips.entries) e.key: e.value.toJson(),
        },
      };

  static SharingState fromJson(Object? json) {
    if (json is! Map) return const SharingState();
    final raw = json['trips'];
    final trips = <String, TripSharing>{};
    if (raw is Map) {
      for (final e in raw.entries) {
        final sharing = TripSharing.fromJson(e.value);
        if (e.key is String && sharing != null) {
          trips[e.key as String] = sharing;
        }
      }
    }
    final active = json['active'];
    return SharingState(
      activeTripId:
          active is String && trips.containsKey(active) ? active : null,
      trips: trips,
    );
  }
}

/// Keeps each trucker's [SharingState] across restarts.
abstract interface class SharingStateStore {
  Future<SharingState> load(String userId);
  Future<void> save(String userId, SharingState state);
  Future<void> forget(String userId);
}

/// In-memory store (tests, and the fallback when preferences fail).
class MemorySharingStateStore implements SharingStateStore {
  final Map<String, SharingState> _states = {};

  @override
  Future<SharingState> load(String userId) async =>
      _states[userId] ?? const SharingState();

  @override
  Future<void> save(String userId, SharingState state) async =>
      _states[userId] = state;

  @override
  Future<void> forget(String userId) async => _states.remove(userId);
}
