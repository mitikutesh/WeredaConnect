import 'package:wereda_connect/features/delivery_verification/data/mock/buyer_mock_deliveries.dart';
import 'package:wereda_connect/features/delivery_verification/data/mock/farmer_mock_deliveries.dart';
import 'package:wereda_connect/features/delivery_verification/data/mock/trucker_mock_deliveries.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';

/// Seeds mock deliveries for the current user's [role].
///
/// When [statusFilter] is null (All), seeds one delivery per status
/// (pending, pickedUp, inTransit, delivered) for a mixed view.
/// When [statusFilter] is set, seeds 3 deliveries all at that status.
///
/// Debug-only helper used by the "Seed Test Deliveries" button on the
/// delivery list screen.
Future<void> seedMockDeliveries({
  required DeliveryRepository repo,
  required String userId,
  required String role,
  String? statusFilter,
}) async {
  switch (role) {
    case 'farmer':
      await seedFarmerMockDeliveries(repo, userId, statusFilter: statusFilter);
    case 'trucker':
      await seedTruckerMockDeliveries(repo, userId, statusFilter: statusFilter);
    case 'buyer':
      await seedBuyerMockDeliveries(repo, userId, statusFilter: statusFilter);
  }
}
