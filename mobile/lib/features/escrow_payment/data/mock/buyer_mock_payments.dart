import 'dart:math';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/payment.dart';

const _uuid = Uuid();

const _farmerNames = [
  'Abebe Bekele',
  'Mulugeta Tadesse',
  'Alemayehu Worku',
  'Girma Desalegn',
];

/// Realistic ETB amounts for agricultural produce.
const _amounts = [2500.0, 5000.0, 15000.0, 7800.0];

/// Seeds mock payments where [userId] is the buyer (paying money).
///
/// When [statusFilter] is null, seeds one payment per status (mixed view).
/// When [statusFilter] is set, seeds 3 payments all at that status.
Future<void> seedBuyerMockPayments(
  Isar isar,
  String userId, {
  String? statusFilter,
}) async {
  if (statusFilter == null) {
    await _seedMixed(isar, userId);
  } else {
    await _seedForStatus(isar, userId, statusFilter);
  }
}

Future<void> _seedMixed(Isar isar, String userId) async {
  await _createAtStatus(isar, userId, 'escrowed', 0);
  await _createAtStatus(isar, userId, 'released', 1);
  await _createAtStatus(isar, userId, 'disputed', 2);
  await _createAtStatus(isar, userId, 'refunded', 3);
}

Future<void> _seedForStatus(Isar isar, String userId, String status) async {
  for (var i = 0; i < 3; i++) {
    await _createAtStatus(isar, userId, status, i);
  }
}

Future<void> _createAtStatus(
  Isar isar,
  String userId,
  String status,
  int nameIndex,
) async {
  final idx = nameIndex % _farmerNames.length;
  final amount = _amounts[idx];
  final commission = max(amount * 0.025, 5.0);
  final now = DateTime.now().toUtc();

  final payment = Payment()
    ..paymentId = _uuid.v4()
    ..deliveryId = _uuid.v4()
    ..farmerId = _uuid.v4()
    ..buyerId = userId
    ..amount = amount
    ..commission = commission
    ..commissionRate = 0.025
    ..farmerPayout = amount - commission
    ..telebirrRef = 'TBR-${_uuid.v4().substring(0, 8).toUpperCase()}'
    ..status = PaymentStatus.values.byName(status)
    ..createdAt = now.subtract(Duration(hours: nameIndex * 6))
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..version = 1;

  if (status == 'released' || status == 'completed') {
    payment.releasedAt = now.subtract(const Duration(hours: 2));
  }
  if (status == 'completed') {
    payment.completedAt = now.subtract(const Duration(hours: 1));
  }
  if (status == 'disputed') {
    payment.disputeReason = 'damaged_goods';
    payment.disputeDescription = 'Crops arrived damaged during transport';
    payment.disputeWindowEndsAt = now.add(const Duration(hours: 24));
  }
  if (status == 'refunded') {
    payment.refundedAt = now.subtract(const Duration(hours: 3));
  }

  await isar.writeTxn(() async {
    await isar.payments.put(payment);
  });
}
