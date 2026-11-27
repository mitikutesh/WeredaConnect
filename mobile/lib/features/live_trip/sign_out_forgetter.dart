import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/live_trip/data/track_snapshot_store.dart';
import 'package:wereda_connect/features/reputation/data/reputation_cache.dart';

/// What the live trip layer drops when a user signs out on purpose or the
/// account is deleted: that user's stored live trip snapshots (LT-10) and
/// their kept drop-off handoffs (T-3, on web that storage is the browser's).
/// Another trucker who uses the same phone keeps theirs. Passed to the AuthBloc as its `forgetTrackSnapshots`, which it
/// calls on an explicit logout and on account deletion only.
///
/// ESC-31: the same call drops the user's saved own reputation, their saved
/// events and their unsent appeal drafts, so the next person on a shared
/// phone never reads them.
///
/// Never on a session that lapsed (the 48 h offline window at start, a
/// refresh token the server refused, a failed check): a trucker offline for
/// days on a long trip must keep the drop-off QR, because nothing can renew
/// the code after pickup.
Future<void> Function(String userId) signOutForgetter(
  DropoffHandoffStore handoffs, {
  Future<void> Function(String userId)? forgetTrackSnapshots,
  Future<void> Function(String userId)? forgetReputation,
}) {
  final forgetTracks =
      forgetTrackSnapshots ?? TrackSnapshotStore.forgetStoredUser;
  final forgetOwnReputation =
      forgetReputation ?? ReputationCache.forgetStoredUser;
  return (userId) async {
    // Each store is cleared even when another one fails.
    try {
      await forgetTracks(userId);
    } finally {
      try {
        await handoffs.forgetUser(userId);
      } finally {
        await forgetOwnReputation(userId);
      }
    }
  };
}
