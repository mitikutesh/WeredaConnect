import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/crop_listing/data/mock/farmer_mock_listings.dart';

const _uuid = Uuid();

/// Seeds mock crop listings for the current user's [role].
///
/// When [statusFilter] is null (All), seeds one listing per status
/// (draft, active, sold, expired) for a mixed view.
/// When [statusFilter] is set, seeds 3 listings all at that status.
///
/// Writes directly to Isar (bypassing [CropListingRepository]) because
/// the repo has no status-transition methods and always sets status=active.
/// This matches the pattern used by [seedMockPayments].
Future<void> seedMockCropListings({
  required Isar isar,
  required String userId,
  required String role,
  String? statusFilter,
}) async {
  switch (role) {
    case 'farmer':
      await seedFarmerMockListings(isar, userId, statusFilter: statusFilter);
    case 'buyer':
    case 'trucker':
      // Buyers/truckers browse active listings from other farmers.
      await seedFarmerMockListings(
        isar,
        _uuid.v4(),
        statusFilter: 'active',
      );
  }
}
