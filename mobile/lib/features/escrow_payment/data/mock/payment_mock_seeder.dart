import 'package:isar/isar.dart';

import 'package:wereda_connect/features/escrow_payment/data/mock/buyer_mock_payments.dart';
import 'package:wereda_connect/features/escrow_payment/data/mock/farmer_mock_payments.dart';

/// Seeds mock payments for the current user's [role].
///
/// When [statusFilter] is null (All), seeds one payment per status
/// (escrowed, released, disputed, refunded) for a mixed view.
/// When [statusFilter] is set, seeds 3 payments all at that status.
///
/// Truckers don't participate in escrow payments, so only
/// farmer and buyer roles generate data.
Future<void> seedMockPayments({
  required Isar isar,
  required String userId,
  required String role,
  String? statusFilter,
}) async {
  switch (role) {
    case 'farmer':
      await seedFarmerMockPayments(isar, userId, statusFilter: statusFilter);
    case 'buyer':
      await seedBuyerMockPayments(isar, userId, statusFilter: statusFilter);
    case 'trucker':
      // Truckers don't participate in escrow — no payments to seed.
      break;
  }
}
