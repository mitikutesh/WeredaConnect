import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';

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
    ..createdAt = DateTime.utc(2026, 2, 15, 10, 0, 0)
    ..updatedAt = DateTime.utc(2026, 2, 15, 10, 0, 0)
    ..version = 1;
}

void main() {
  group('QrVerificationService', () {
    late QrVerificationService service;

    setUp(() {
      service = QrVerificationService();
    });

    group('generateQrToken', () {
      test('returns token and nonce', () {
        final delivery = _createTestDelivery();
        final (token, nonce) = service.generateQrToken(delivery);

        expect(token, isNotEmpty);
        expect(nonce, isNotEmpty);

        // Token should be valid base64url
        expect(
          () => base64Url.decode(base64Url.normalize(token)),
          returnsNormally,
        );
      });

      test('returns different tokens for different deliveries', () {
        final delivery1 = _createTestDelivery();
        final delivery2 = _createTestDelivery()
          ..deliveryId = '880e8400-e29b-41d4-a716-446655440099';

        final (token1, _) = service.generateQrToken(delivery1);
        final (token2, _) = service.generateQrToken(delivery2);

        expect(token1, isNot(token2));
      });

      test('returns different nonces each call', () {
        final delivery = _createTestDelivery();

        final (_, nonce1) = service.generateQrToken(delivery);
        final (_, nonce2) = service.generateQrToken(delivery);

        expect(nonce1, isNot(nonce2));
      });
    });

    group('verifyQrToken', () {
      test('validates correctly signed token', () {
        final delivery = _createTestDelivery();
        final (token, nonce) = service.generateQrToken(delivery);

        final result = service.verifyQrToken(token, delivery);

        expect(result.isValid, isTrue);
        expect(result.deliveryId, delivery.deliveryId);
        expect(result.tripId, delivery.tripId);
        expect(result.farmerId, delivery.farmerId);
        expect(result.nonce, nonce);
        expect(result.timestamp, isNotNull);
        expect(result.errorMessage, isNull);
      });

      test('rejects token for wrong delivery', () {
        final deliveryA = _createTestDelivery();
        final deliveryB = _createTestDelivery()
          ..deliveryId = '880e8400-e29b-41d4-a716-446655440099'
          ..tripId = '990e8400-e29b-41d4-a716-446655440003';

        final (token, _) = service.generateQrToken(deliveryA);

        final result = service.verifyQrToken(token, deliveryB);

        expect(result.isValid, isFalse);
        expect(result.errorMessage, contains('does not match'));
      });

      test('rejects empty string', () {
        final delivery = _createTestDelivery();

        final result = service.verifyQrToken('', delivery);

        expect(result.isValid, isFalse);
      });

      test('rejects oversized token', () {
        final delivery = _createTestDelivery();
        final oversized = 'A' * (DeliveryConstants.maxQrTokenChars + 1);

        final result = service.verifyQrToken(oversized, delivery);

        expect(result.isValid, isFalse);
        expect(result.errorMessage, contains('too large'));
      });

      test('rejects malformed base64', () {
        final delivery = _createTestDelivery();

        final result = service.verifyQrToken('not-valid-base64!!!', delivery);

        expect(result.isValid, isFalse);
      });

      test('rejects tampered HMAC', () {
        final delivery = _createTestDelivery();
        final (token, _) = service.generateQrToken(delivery);

        // Decode, tamper HMAC, re-encode
        final decoded =
            utf8.decode(base64Url.decode(base64Url.normalize(token)));
        final payload = jsonDecode(decoded) as Map<String, dynamic>;
        payload['hmac'] = 'tampered_${payload['hmac']}';
        final tampered = base64Url.encode(utf8.encode(jsonEncode(payload)));

        final result = service.verifyQrToken(tampered, delivery);

        expect(result.isValid, isFalse);
        expect(result.errorMessage, contains('tampering'));
      });

      test('returns nonce in valid result', () {
        final delivery = _createTestDelivery();
        final (token, nonce) = service.generateQrToken(delivery);

        final result = service.verifyQrToken(token, delivery);

        expect(result.isValid, isTrue);
        expect(result.nonce, isNotNull);
        expect(result.nonce, nonce);
      });
    });
  });
}
