import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

P _unusedDeserializeProp<P>(
  IsarReader reader,
  int propertyId,
  int offset,
  Map<Type, List<int>> allOffsets,
) =>
    throw UnsupportedError('not used by this test');

/// The `SyncOperation` schema as shipped before 1.17.0 (no `reverted`
/// property), so the test can write rows the way an older app build did.
/// Same collection name, id and indexes as the generated schema.
final _schemaBefore117 = CollectionSchema<SyncOperation>(
  name: r'SyncOperation',
  id: SyncOperationSchema.id,
  properties: const {
    'action': PropertySchema(id: 0, name: 'action', type: IsarType.string),
    'createdAt':
        PropertySchema(id: 1, name: 'createdAt', type: IsarType.dateTime),
    'data': PropertySchema(id: 2, name: 'data', type: IsarType.string),
    'entityId': PropertySchema(id: 3, name: 'entityId', type: IsarType.string),
    'entityType':
        PropertySchema(id: 4, name: 'entityType', type: IsarType.string),
    'idempotencyKey':
        PropertySchema(id: 5, name: 'idempotencyKey', type: IsarType.string),
    'operationId':
        PropertySchema(id: 6, name: 'operationId', type: IsarType.string),
    'retryCount':
        PropertySchema(id: 7, name: 'retryCount', type: IsarType.long),
    'status': PropertySchema(
      id: 8,
      name: 'status',
      type: IsarType.string,
      enumMap: {
        'pending': 'pending',
        'syncing': 'syncing',
        'synced': 'synced',
        'failed': 'failed',
      },
    ),
  },
  estimateSize: (op, offsets, _) {
    var bytes = offsets.last;
    for (final value in [
      op.action,
      op.data,
      op.entityId,
      op.entityType,
      op.idempotencyKey,
      op.operationId,
      op.status.name,
    ]) {
      bytes += 3 + value.length * 3;
    }
    return bytes;
  },
  serialize: (op, writer, offsets, _) {
    writer
      ..writeString(offsets[0], op.action)
      ..writeDateTime(offsets[1], op.createdAt)
      ..writeString(offsets[2], op.data)
      ..writeString(offsets[3], op.entityId)
      ..writeString(offsets[4], op.entityType)
      ..writeString(offsets[5], op.idempotencyKey)
      ..writeString(offsets[6], op.operationId)
      ..writeLong(offsets[7], op.retryCount)
      ..writeString(offsets[8], op.status.name);
  },
  deserialize: (id, reader, offsets, _) => SyncOperation()
    ..id = id
    ..action = reader.readString(offsets[0])
    ..createdAt = reader.readDateTime(offsets[1])
    ..data = reader.readString(offsets[2])
    ..entityId = reader.readString(offsets[3])
    ..entityType = reader.readString(offsets[4])
    ..idempotencyKey = reader.readString(offsets[5])
    ..operationId = reader.readString(offsets[6])
    ..retryCount = reader.readLong(offsets[7])
    ..status = SyncStatus.values.byName(reader.readString(offsets[8])),
  deserializeProp: _unusedDeserializeProp,
  idName: 'id',
  indexes: SyncOperationSchema.indexes,
  links: const {},
  embeddedSchemas: const {},
  getId: (op) => op.id,
  getLinks: (op) => [],
  attach: (collection, id, op) => op.id = id,
  version: SyncOperationSchema.version,
);

/// FUP-01: `reverted` was added to the queue in 1.17.0. A row written by an
/// older build stores no byte for it, which an Isar filter reads as null, not
/// false, so the failed count must not filter on `reverted == false`.
void main() {
  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  test('failed operations queued by an older build still count', () async {
    final dir = Directory.systemTemp.createTempSync('sync_queue_upgrade_');
    addTearDown(() {
      if (dir.existsSync()) dir.deleteSync(recursive: true);
    });
    final name = 'sync_queue_upgrade_${DateTime.now().microsecondsSinceEpoch}';

    final before = await Isar.open(
      [_schemaBefore117],
      directory: dir.path,
      name: name,
    );
    await before.writeTxn(() async {
      for (final (id, retryCount) in [
        ('op-rejected', SyncQueue.maxRetries + 1),
        ('op-exhausted', SyncQueue.maxRetries),
      ]) {
        await before.collection<SyncOperation>().put(
              SyncOperation()
                ..operationId = id
                ..idempotencyKey = 'farmer-1:$id'
                ..entityType = 'crop_listing'
                ..entityId = 'crop-1'
                ..action = 'create'
                ..data = '{}'
                ..status = SyncStatus.failed
                ..retryCount = retryCount
                ..createdAt = DateTime.utc(2026, 10),
            );
      }
    });
    await before.close();

    final isar = await Isar.open(
      [SyncOperationSchema],
      directory: dir.path,
      name: name,
    );
    addTearDown(() => isar.close(deleteFromDisk: true));
    final queue = SyncQueue(isar);

    // The trap this guards against.
    expect(
      await isar.syncOperations.filter().revertedEqualTo(false).count(),
      0,
    );
    expect((await queue.getOperation('op-rejected'))!.reverted, isFalse);
    expect(await queue.getFailedCount(), 2);

    // A revert after the upgrade still finds and flags the old row.
    expect(await queue.markRevertedFor('crop_listing', 'crop-1'), 1);
    expect(await queue.getFailedCount(), 1);
  });
}
