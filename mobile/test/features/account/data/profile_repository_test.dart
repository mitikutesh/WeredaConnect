import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
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
  late _MockApiClient api;
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
    api = _MockApiClient();
    repository = ProfileRepository(syncQueue: syncQueue, api: api);
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

  // FUP-315 (sync-protocol.md 1.72.0): after a refused edit the phone shows
  // the server's profile again.
  group('revertRefusedEdit', () {
    void serverHas(Map<String, dynamic> user) {
      when(() => api.get<Map<String, dynamic>>('/user/profile'))
          .thenAnswer((_) async => {'user': user});
    }

    /// Queues an edit and returns its operation id.
    Future<String> queueEdit(ProfileChanges changes) async {
      // The idempotency key has millisecond resolution.
      await Future<void>.delayed(const Duration(milliseconds: 2));
      await repository.save(userId: 'farmer-1', changes: changes);
      return (await syncQueue.operationsFor('user', 'farmer-1'))
          .last
          .operationId;
    }

    test("puts the server's profile back, not the refused name", () async {
      serverHas({
        'userId': 'farmer-1',
        'displayName': 'Abebe',
        'woreda': 'Adama',
      });
      final refused = await queueEdit(
        const ProfileChanges(displayName: 'Abebe 0911223344'),
      );
      await syncQueue.markAsRejected(refused);

      final applied = <ProfileValues>[];
      await repository.revertRefusedEdit(
        'farmer-1',
        apply: (values) async => applied.add(values),
      );

      expect(applied, [
        const ProfileValues(displayName: 'Abebe', woreda: 'Adama'),
      ]);
      verify(() => api.get<Map<String, dynamic>>('/user/profile')).called(1);
    });

    test('an edit still waiting on the phone stays on top', () async {
      serverHas({
        'userId': 'farmer-1',
        'displayName': 'Abebe',
        'woreda': 'Adama',
        'role': 'farmer',
      });
      final refused = await queueEdit(
        const ProfileChanges(displayName: 'Abebe 0911223344'),
      );
      await syncQueue.markAsRejected(refused);
      await queueEdit(const ProfileChanges(woreda: 'Meki'));
      // A language change carries no profile text.
      await Future<void>.delayed(const Duration(milliseconds: 2));
      await syncQueue.enqueue(
        userId: 'farmer-1',
        entityType: 'user',
        entityId: 'farmer-1',
        action: 'update',
        data: jsonEncode({'preferredLanguage': 'am'}),
        operationId: 'op-language',
      );

      final applied = <ProfileValues>[];
      await repository.revertRefusedEdit(
        'farmer-1',
        apply: (values) async => applied.add(values),
      );

      expect(applied, [
        const ProfileValues(
          displayName: 'Abebe',
          woreda: 'Meki',
          role: 'farmer',
        ),
      ]);
    });

    test('a failed read changes nothing', () async {
      when(() => api.get<Map<String, dynamic>>('/user/profile'))
          .thenThrow(const NetworkException('offline'));

      var applied = 0;
      await repository.revertRefusedEdit(
        'farmer-1',
        apply: (_) async => applied++,
      );

      expect(applied, 0);
    });

    test("another account's answer changes nothing", () async {
      serverHas({
        'userId': 'farmer-2',
        'displayName': 'Kebede',
        'woreda': 'Meki',
      });

      var applied = 0;
      await repository.revertRefusedEdit(
        'farmer-1',
        apply: (_) async => applied++,
      );

      expect(applied, 0);
    });
  });
}
