import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';

/// Fake in-memory implementation for tests.
class FakeDeviceSigningKeyService extends DeviceSigningKeyService {
  final String _key;

  FakeDeviceSigningKeyService({
    String key = 'dGVzdC1kZXZpY2Uta2V5LWZvci10ZXN0aW5n',
  })  : _key = key,
        super(storage: null);

  @override
  Future<String> getKey() async => _key;
}

Delivery _createTestDelivery() {
  return Delivery()
    ..deliveryId = '550e8400-e29b-41d4-a716-446655440000'
    ..tripId = '660e8400-e29b-41d4-a716-446655440001'
    ..listingId = '770e8400-e29b-41d4-a716-446655440002'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Test Farmer'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Test Trucker'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Test Buyer'
    ..qrToken = ''
    ..status = DeliveryStatus.pending
    ..createdAt = DateTime.utc(2026, 2, 15, 10)
    ..updatedAt = DateTime.utc(2026, 2, 15, 10)
    ..version = 1;
}

void main() {
  group('QrVerificationService', () {
    late QrVerificationService service;
    late FakeDeviceSigningKeyService fakeKeyService;

    setUp(() {
      fakeKeyService = FakeDeviceSigningKeyService();
      service = QrVerificationService(deviceKeyService: fakeKeyService);
    });

    group('generateQrToken', () {
      test('returns token and nonce', () async {
        final delivery = _createTestDelivery();
        final (token, nonce) = await service.generateQrToken(delivery);

        expect(token, isNotEmpty);
        expect(nonce, isNotEmpty);

        // Token should be valid base64url
        expect(
          () => base64Url.decode(base64Url.normalize(token)),
          returnsNormally,
        );
      });

      test('returns different tokens for different deliveries', () async {
        final delivery1 = _createTestDelivery();
        final delivery2 = _createTestDelivery()
          ..deliveryId = '880e8400-e29b-41d4-a716-446655440099';

        final (token1, _) = await service.generateQrToken(delivery1);
        final (token2, _) = await service.generateQrToken(delivery2);

        expect(token1, isNot(token2));
      });

      test('returns different nonces each call', () async {
        final delivery = _createTestDelivery();

        final (_, nonce1) = await service.generateQrToken(delivery);
        final (_, nonce2) = await service.generateQrToken(delivery);

        expect(nonce1, isNot(nonce2));
      });

      test('different device keys produce different tokens', () async {
        final delivery = _createTestDelivery();

        final service1 = QrVerificationService(
          deviceKeyService: FakeDeviceSigningKeyService(key: 'key-alpha'),
        );
        final service2 = QrVerificationService(
          deviceKeyService: FakeDeviceSigningKeyService(key: 'key-beta'),
        );

        final (token1, _) = await service1.generateQrToken(delivery);
        final (token2, _) = await service2.generateQrToken(delivery);

        // Decode payloads to compare HMACs
        final payload1 = jsonDecode(
          utf8.decode(base64Url.decode(base64Url.normalize(token1))),
        ) as Map<String, dynamic>;
        final payload2 = jsonDecode(
          utf8.decode(base64Url.decode(base64Url.normalize(token2))),
        ) as Map<String, dynamic>;

        expect(payload1['hmac'], isNot(payload2['hmac']));
      });
    });

    group('parseQrToken', () {
      test('parses correctly structured token', () async {
        final delivery = _createTestDelivery();
        final (token, nonce) = await service.generateQrToken(delivery);

        final result = service.parseQrToken(token, delivery);

        expect(result.isValid, isTrue);
        expect(result.deliveryId, delivery.deliveryId);
        expect(result.tripId, delivery.tripId);
        expect(result.farmerId, delivery.farmerId);
        expect(result.nonce, nonce);
        expect(result.timestamp, isNotNull);
        expect(result.errorMessage, isNull);
      });

      test('rejects token for wrong delivery', () async {
        final deliveryA = _createTestDelivery();
        final deliveryB = _createTestDelivery()
          ..deliveryId = '880e8400-e29b-41d4-a716-446655440099'
          ..tripId = '990e8400-e29b-41d4-a716-446655440003';

        final (token, _) = await service.generateQrToken(deliveryA);

        final result = service.parseQrToken(token, deliveryB);

        expect(result.isValid, isFalse);
        expect(result.errorMessage, contains('does not match'));
      });

      test('rejects empty string', () {
        final delivery = _createTestDelivery();

        final result = service.parseQrToken('', delivery);

        expect(result.isValid, isFalse);
      });

      test('rejects oversized token', () {
        final delivery = _createTestDelivery();
        final oversized = 'A' * (DeliveryConstants.maxQrTokenChars + 1);

        final result = service.parseQrToken(oversized, delivery);

        expect(result.isValid, isFalse);
        expect(result.errorMessage, contains('too large'));
      });

      test('rejects malformed base64', () {
        final delivery = _createTestDelivery();

        final result = service.parseQrToken('not-valid-base64!!!', delivery);

        expect(result.isValid, isFalse);
      });

      test('returns nonce in valid result', () async {
        final delivery = _createTestDelivery();
        final (token, nonce) = await service.generateQrToken(delivery);

        final result = service.parseQrToken(token, delivery);

        expect(result.isValid, isTrue);
        expect(result.nonce, isNotNull);
        expect(result.nonce, nonce);
      });

      test('parses token from different device key (no HMAC check)', () async {
        final delivery = _createTestDelivery();
        // Generate with one device key
        final otherService = QrVerificationService(
          deviceKeyService: FakeDeviceSigningKeyService(key: 'other-device'),
        );
        final (token, nonce) = await otherService.generateQrToken(delivery);

        // Parse with a different device key — should still succeed
        // because parseQrToken only does structural validation
        final result = service.parseQrToken(token, delivery);

        expect(result.isValid, isTrue);
        expect(result.nonce, nonce);
      });
    });
  });
}
