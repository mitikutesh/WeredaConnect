import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

/// Insert a test payment directly into Isar.
Future<Payment> _insertTestPayment(
  Isar isar, {
  String paymentId = 'esc-001',
  String deliveryId = 'del-001',
  String farmerId = 'farmer-001',
  String buyerId = 'buyer-001',
  double amount = 15000.0,
  PaymentStatus status = PaymentStatus.escrowed,
  DateTime? createdAt,
}) async {
  final now = createdAt ?? DateTime.now().toUtc();
  final payment = Payment()
    ..paymentId = paymentId
    ..deliveryId = deliveryId
    ..farmerId = farmerId
    ..buyerId = buyerId
    ..amount = amount
    ..commission = amount * 0.025 < 5 ? 5.0 : amount * 0.025
    ..commissionRate = 0.025
    ..farmerPayout = amount - (amount * 0.025 < 5 ? 5.0 : amount * 0.025)
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..version = 1;

  await isar.writeTxn(() async {
    await isar.payments.put(payment);
  });

  return payment;
}

void main() {
  group('EscrowRepository – data isolation', () {
    late Isar isar;
    late Directory tempDir;
    late MockApiClient apiClient;
    late MockConnectivityMonitor connectivityMonitor;
    late EscrowRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir =
          Directory.systemTemp.createTempSync('escrow_repo_isolation_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          PaymentIntentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'test_escrow_isolation_${DateTime.now().millisecondsSinceEpoch}',
      );
      apiClient = MockApiClient();
      connectivityMonitor = MockConnectivityMonitor();
      repository = EscrowRepository(
        isar: isar,
        apiClient: apiClient,
        connectivityMonitor: connectivityMonitor,
      );

      // Seed payments for two distinct farmer/buyer pairs
      await _insertTestPayment(
        isar,
        paymentId: 'esc-A1',
        deliveryId: 'del-A1',
        farmerId: 'farmer-A',
        buyerId: 'buyer-X',
        amount: 10000.0,
      );
      await _insertTestPayment(
        isar,
        paymentId: 'esc-A2',
        deliveryId: 'del-A2',
        farmerId: 'farmer-A',
        buyerId: 'buyer-X',
        amount: 20000.0,
        status: PaymentStatus.released,
      );
      await _insertTestPayment(
        isar,
        paymentId: 'esc-B1',
        deliveryId: 'del-B1',
        farmerId: 'farmer-B',
        buyerId: 'buyer-Y',
        amount: 30000.0,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('getHistory(userId: farmer-A) returns only farmer-A payments',
        () async {
      final payments = await repository.getHistory(userId: 'farmer-A');

      expect(payments.length, 2);
      expect(
        payments.every(
          (p) => p.farmerId == 'farmer-A' || p.buyerId == 'farmer-A',
        ),
        isTrue,
      );
      final ids = payments.map((p) => p.paymentId).toSet();
      expect(ids, containsAll(['esc-A1', 'esc-A2']));
    });

    test('getHistory(userId: farmer-B) returns only farmer-B payments',
        () async {
      final payments = await repository.getHistory(userId: 'farmer-B');

      expect(payments.length, 1);
      expect(payments.first.paymentId, 'esc-B1');
      expect(payments.first.farmerId, 'farmer-B');
    });

    test('getHistory(userId: buyer-X) returns payments where buyer-X is buyer',
        () async {
      final payments = await repository.getHistory(userId: 'buyer-X');

      expect(payments.length, 2);
      expect(
        payments.every((p) => p.buyerId == 'buyer-X'),
        isTrue,
      );
      final ids = payments.map((p) => p.paymentId).toSet();
      expect(ids, containsAll(['esc-A1', 'esc-A2']));
    });

    test('getHistory(userId: unrelated-user) returns empty list', () async {
      final payments = await repository.getHistory(userId: 'unrelated-user');

      expect(payments, isEmpty);
    });
  });
}
