import 'dart:convert';

import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';

/// Mobile profile edits (SIM-56): the `user` update goes into the sync queue,
/// so an edit made offline is sent when the phone is online. The caller
/// writes the new values to the phone's copy of the profile.
class ProfileRepository extends BaseProfileRepository {
  ProfileRepository({required SyncQueue syncQueue, required super.api})
      : _syncQueue = syncQueue;

  final SyncQueue _syncQueue;

  static const _uuid = Uuid();

  @override
  Future<ProfileSaveOutcome> save({
    required String userId,
    required ProfileChanges changes,
  }) async {
    // A full queue throws: nothing was saved, and the screen says so.
    await _syncQueue.enqueue(
      userId: userId,
      entityType: userProfileEntityType,
      entityId: userId,
      action: 'update',
      data: jsonEncode(changes.toSyncData()),
      operationId: _uuid.v4(),
    );
    return ProfileSaveOutcome.queued;
  }

  @override
  Future<bool> hasUnsentEdit(String userId) =>
      _syncQueue.hasOpenOperationsFor(userProfileEntityType, userId);

  /// FUP-315 (sync-protocol.md 1.72.0): the server refused an edit of
  /// [userId]'s profile for good, so the phone shows the server's profile
  /// again (`GET /user/profile`), with the edits still waiting in the queue
  /// on top: their own answers settle them. [apply] writes it to the phone's
  /// copy and the session. When the read fails nothing changes, and the edit
  /// screen reads the profile again when it opens.
  Future<void> revertRefusedEdit(
    String userId, {
    required Future<void> Function(ProfileValues values) apply,
  }) async {
    final server = await fetch(userId);
    if (server == null) return;
    var values = server;
    for (final waiting in await _syncQueue.waitingOperationsFor(
      userProfileEntityType,
      userId,
    )) {
      values = values.apply(
        ProfileChanges.fromSyncData(jsonDecode(waiting.data)),
      );
    }
    await apply(values);
  }
}
