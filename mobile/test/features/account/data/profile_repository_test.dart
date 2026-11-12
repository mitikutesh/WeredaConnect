import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/account/data/repositories/profile_repository.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// SIM-56: on the phone a profile edit is a queued `user` update, so an edit
/// made offline is sent later by the sync queue.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;
  late ProfileRepository repository;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('profile_repo_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'test_profile_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
    repository = ProfileRepository(syncQueue: syncQueue, api: _MockApiClient());
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  test('an edit is queued as a user update of the caller', () async {
    expect(await repository.hasUnsentEdit('buyer-1'), isFalse);

    final outcome = await repository.save(
      userId: 'buyer-1',
      changes: const ProfileChanges(woreda: 'Meki', companyName: 'Meki Grain'),
    );

    expect(outcome, ProfileSaveOutcome.queued);
    final op = (await syncQueue.getPendingOperations()).single;
    expect(op.entityType, 'user');
    expect(op.entityId, 'buyer-1');
    expect(op.action, 'update');
    expect(op.idempotencyKey, startsWith('buyer-1:user:buyer-1:update:'));
    expect(
      jsonDecode(op.data),
      {'woreda': 'Meki', 'companyName': 'Meki Grain'},
    );
    expect(await repository.hasUnsentEdit('buyer-1'), isTrue);
    expect(await repository.hasUnsentEdit('buyer-2'), isFalse);
  });
}
