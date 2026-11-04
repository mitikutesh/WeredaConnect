import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/live_trip/data/track_snapshot_store.dart';

/// What the live trip layer drops when a user signs out on purpose or the
/// account is deleted: the stored live trip snapshots (LT-10) and the
/// trucker's kept drop-off handoffs (T-3, on web that storage is the
/// browser's). Passed to the AuthBloc as its `forgetTrackSnapshots`, which it
/// calls on an explicit logout and on account deletion only.
///
/// Never on a session that lapsed (the 48 h offline window at start, a
/// refresh token the server refused, a failed check): a trucker offline for
/// days on a long trip must keep the drop-off QR, because nothing can renew
/// the code after pickup.
Future<void> Function(String userId) signOutForgetter(
  DropoffHandoffStore handoffs, {
  Future<void> Function(String userId)? forgetTrackSnapshots,
}) {
  final forgetTracks =
      forgetTrackSnapshots ?? TrackSnapshotStore.forgetStoredUser;
  return (userId) async {
    try {
      await forgetTracks(userId);
    } finally {
      await handoffs.forgetAll();
    }
  };
}
