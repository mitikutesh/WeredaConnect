import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_attached.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';

const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _localPickup =
    '/data/user/0/app/files/photos/$_deliveryId/pickup-1700000000000.webp';
const _localDropoff =
    '/data/user/0/app/files/photos/$_deliveryId/dropoff-1700000000000.webp';
const _key = 'uploads/photos/$_deliveryId/pickup-1700000000000.webp';

/// FRD-12: once the server attached a handoff photo, the delivery row points
/// at the server's key and the phone's files of that handoff are deleted.
void main() {
  late Isar isar;
  late Directory tempDir;
  late List<(String, PhotoType)> deleted;
  late DeliveryPhotoAttachedCleanup cleanup;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('photo_attached_test_');
    isar = await Isar.open(
      [DeliverySchema],
      directory: tempDir.path,
      name: 'photo_attached_${DateTime.now().microsecondsSinceEpoch}',
    );
    deleted = [];
    cleanup = DeliveryPhotoAttachedCleanup(
      isar: isar,
      deleteFiles: (deliveryId, type) async => deleted.add((deliveryId, type)),
    );
    await isar.writeTxn(() async {
      await isar.deliverys.put(
        Delivery()
          ..deliveryId = _deliveryId
          ..tripId = 'trip-1'
          ..listingId = 'crop-1'
          ..farmerId = 'farmer-1'
          ..farmerName = 'Kedir'
          ..truckerId = 'trucker-1'
          ..truckerName = 'Geleta'
          ..buyerId = 'buyer-1'
          ..buyerName = 'Dawit'
          ..qrToken = ''
          ..status = DeliveryStatus.delivered
          ..pickupPhotoRef = _localPickup
          ..dropoffPhotoRef = _localDropoff
          ..createdAt = DateTime.utc(2026, 11, 7)
          ..updatedAt = DateTime.utc(2026, 11, 7)
          ..version = 3,
      );
    });
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<Delivery> stored() async => (await isar.deliverys
      .filter()
      .deliveryIdEqualTo(_deliveryId)
      .findFirst())!;

  test('the row takes the key and the handoff files are deleted', () async {
    await cleanup(
      deliveryId: _deliveryId,
      type: 'pickup',
      photoRef: _key,
      localPath: _localPickup,
    );

    expect((await stored()).pickupPhotoRef, _key);
    // The other handoff's photo still waits: untouched.
    expect((await stored()).dropoffPhotoRef, _localDropoff);
    expect(deleted, [(_deliveryId, PhotoType.pickup)]);
  });

  test('a key the row has from the server stays', () async {
    const serverKey = 'uploads/photos/$_deliveryId/pickup-1600000000000.webp';
    await isar.writeTxn(() async {
      final row = await stored();
      row.pickupPhotoRef = serverKey;
      await isar.deliverys.put(row);
    });

    await cleanup(
      deliveryId: _deliveryId,
      type: 'pickup',
      photoRef: _key,
      localPath: _localPickup,
    );

    expect((await stored()).pickupPhotoRef, serverKey);
    expect(deleted, [(_deliveryId, PhotoType.pickup)]);
  });

  test('a delivery gone from the phone still loses its files', () async {
    await cleanup(
      deliveryId: '660e8400-e29b-41d4-a716-446655440099',
      type: 'dropoff',
      photoRef: _key,
      localPath: null,
    );

    expect(deleted.single.$2, PhotoType.dropoff);
  });
}
