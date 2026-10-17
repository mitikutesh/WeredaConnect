import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/contact_phone_purge.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';

CropListing _crop(String id, String farmerId, String phone) => CropListing()
  ..listingId = id
  ..farmerId = farmerId
  ..farmerPhone = phone
  ..cropType = 'teff'
  ..quantity = 10
  ..pricePerKg = 45
  ..location = (LatLng()
    ..latitude = 9
    ..longitude = 38.7)
  ..locationName = 'Adama'
  ..description = ''
  ..status = ListingStatus.active
  ..createdAt = DateTime.utc(2026, 10)
  ..updatedAt = DateTime.utc(2026, 10)
  ..version = 1;

Trip _trip(String id, String truckerId, String phone) => Trip()
  ..tripId = id
  ..truckerId = truckerId
  ..truckerName = 'Trucker'
  ..truckerPhone = phone
  ..fromWoreda = 'Adama'
  ..toWoreda = 'Addis Ababa'
  ..departureDate = DateTime.utc(2026, 10, 20)
  ..capacity = 50
  ..pricePerQuintal = 100
  ..status = TripStatus.open
  ..matchedListings = <String>[]
  ..createdAt = DateTime.utc(2026, 10)
  ..updatedAt = DateTime.utc(2026, 10)
  ..version = 1;

Message _message(String id) => Message()
  ..messageId = id
  ..conversationId = 'crop:crop-1:buyer-1:farmer-1'
  ..contextType = 'crop'
  ..contextId = 'crop-1'
  ..senderId = 'buyer-1'
  ..recipientId = 'farmer-1'
  ..senderName = 'Buyer'
  ..senderPhone = '+251911000001'
  ..recipientName = 'Farmer'
  ..recipientPhone = '+251911000002'
  ..body = 'hello'
  ..createdAt = DateTime.utc(2026, 10)
  ..version = 1;

/// ESC-48: the counterpart numbers earlier versions cached leave the device.
void main() {
  late Isar isar;
  late Directory tempDir;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('contact_purge_test_');
    isar = await Isar.open(
      [CropListingSchema, TripSchema, MessageSchema],
      directory: tempDir.path,
      name: 'test_contact_purge_${DateTime.now().microsecondsSinceEpoch}',
    );
    await isar.writeTxn(() async {
      await isar.cropListings.putAll([
        _crop('crop-own', 'me', '+251911000100'),
        _crop('crop-other', 'farmer-2', '+251911000200'),
      ]);
      await isar.trips.putAll([
        _trip('trip-own', 'me', '+251911000300'),
        _trip('trip-other', 'trucker-2', '+251911000400'),
      ]);
      await isar.messages.put(_message('msg-1'));
    });
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<String> cropPhone(String id) async =>
      (await isar.cropListings.filter().listingIdEqualTo(id).findFirst())!
          .farmerPhone;
  Future<String> tripPhone(String id) async =>
      (await isar.trips.filter().tripIdEqualTo(id).findFirst())!.truckerPhone;

  test('clears other users\' numbers and every message number, keeps own',
      () async {
    final changed = await ContactPhonePurge.run(isar, currentUserId: 'me');

    expect(changed, 3);
    expect(await cropPhone('crop-other'), isEmpty);
    expect(await tripPhone('trip-other'), isEmpty);
    expect(await cropPhone('crop-own'), '+251911000100');
    expect(await tripPhone('trip-own'), '+251911000300');
    final message =
        await isar.messages.filter().messageIdEqualTo('msg-1').findFirst();
    expect(message!.senderPhone, isNull);
    expect(message.recipientPhone, isNull);
    expect(message.body, 'hello');
  });

  test('a second run writes nothing', () async {
    await ContactPhonePurge.run(isar, currentUserId: 'me');

    expect(await ContactPhonePurge.run(isar, currentUserId: 'me'), 0);
  });

  test('without a signed-in user only messages are cleared', () async {
    final changed = await ContactPhonePurge.run(isar);

    expect(changed, 1);
    expect(await cropPhone('crop-other'), '+251911000200');
    expect(await tripPhone('trip-other'), '+251911000400');
  });
}
