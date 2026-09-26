import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';

/// Audit K3: a user conflict with an unknown role must fail closed instead
/// of silently turning the account into a farmer.
void main() {
  late Isar isar;
  late Directory tempDir;
  late ConflictResolver resolver;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('conflict_role_test_');
    isar = await Isar.open(
      [UserSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'conflict_role_${DateTime.now().microsecondsSinceEpoch}',
    );
    resolver = ConflictResolver(isar);
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  SyncOperation userOp(String userId) => SyncOperation()
    ..operationId = 'op-$userId'
    ..idempotencyKey = '$userId:user:$userId:update:1'
    ..entityType = 'user'
    ..entityId = userId
    ..action = 'update'
    ..data = '{}'
    ..status = SyncStatus.syncing
    ..retryCount = 0
    ..createdAt = DateTime.utc(2026, 9, 25);

  Map<String, dynamic> serverUser(String role) => {
        'phoneNumber': '+251911234567',
        'name': 'Chaltu',
        'role': role,
        'woreda': 'Adama',
        'isVerified': true,
        'faydaVerified': true,
        'faydaVerifiedAt': '2026-09-01T10:00:00Z',
        'createdAt': '2026-01-01T00:00:00Z',
        'updatedAt': '2026-09-25T00:00:00Z',
      };

  Future<void> seedUser(String userId, UserRole role) async {
    await isar.writeTxn(() async {
      await isar.users.put(
        User()
          ..userId = userId
          ..phoneNumber = '+251911234567'
          ..name = 'Chaltu'
          ..role = role
          ..woreda = 'Adama'
          ..createdAt = DateTime.utc(2026)
          ..updatedAt = DateTime.utc(2026)
          ..version = 1,
      );
    });
  }

  test('an unknown role keeps the existing local role', () async {
    await seedUser('u-1', UserRole.buyer);

    await resolver.resolve(
      operation: userOp('u-1'),
      serverData: serverUser('superuser'),
      serverVersion: 5,
    );

    final user = await isar.users.filter().userIdEqualTo('u-1').findFirst();
    expect(user!.role, UserRole.buyer);
    expect(user.faydaVerified, isTrue);
    expect(user.version, 5);
  });

  test('a known role is applied (case-insensitive)', () async {
    await seedUser('u-2', UserRole.buyer);

    await resolver.resolve(
      operation: userOp('u-2'),
      serverData: serverUser('Trucker'),
      serverVersion: 2,
    );

    final user = await isar.users.filter().userIdEqualTo('u-2').findFirst();
    expect(user!.role, UserRole.trucker);
  });

  test('an unknown role for a user we do not have is refused', () async {
    await expectLater(
      resolver.resolve(
        operation: userOp('u-3'),
        serverData: serverUser('admin'),
        serverVersion: 1,
      ),
      throwsA(isA<ConflictResolutionException>()),
    );
    expect(await isar.users.filter().userIdEqualTo('u-3').findFirst(), isNull);
  });
}
