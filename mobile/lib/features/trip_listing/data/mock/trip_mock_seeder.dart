import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/trip_listing/data/mock/trucker_mock_trips.dart';

const _uuid = Uuid();

/// Seeds mock trips for the current user's [role].
///
/// When [statusFilter] is null (All), seeds one trip per status
/// (open, matched, inTransit, delivered, cancelled) for a mixed view.
/// When [statusFilter] is set, seeds 3 trips all at that status.
///
/// Writes directly to Isar (bypassing [TripListingRepository]) because
/// the repo has no status-transition methods and always sets status=open.
/// This matches the pattern used by [seedMockPayments].
Future<void> seedMockTrips({
  required Isar isar,
  required String userId,
  required String role,
  String? statusFilter,
}) async {
  switch (role) {
    case 'trucker':
      await seedTruckerMockTrips(isar, userId, statusFilter: statusFilter);
    case 'farmer':
    case 'buyer':
      // Farmers/buyers browse open trips from other truckers.
      await seedTruckerMockTrips(
        isar,
        _uuid.v4(),
        statusFilter: 'open',
      );
  }
}
