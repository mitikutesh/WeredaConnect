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
}
