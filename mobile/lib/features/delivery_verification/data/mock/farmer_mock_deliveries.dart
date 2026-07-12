import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';

const _uuid = Uuid();

/// Ethiopian farmer/trucker/buyer names for realistic mock data.
const _truckerNames = [
  'Kebede Transport',
  'Dawit Logistics',
  'Getachew Cargo',
  'Tesfaye Movers',
];
const _buyerNames = [
  'Addis Wholesale',
  'Merkato Traders',
  'Bahir Dar Produce',
  'Hawassa Market',
];

/// Seeds mock deliveries where [userId] is the farmer.
///
/// When [statusFilter] is null, seeds one delivery per status (mixed view).
/// When [statusFilter] is set, seeds 3 deliveries all at that status.
Future<void> seedFarmerMockDeliveries(
  DeliveryRepository repo,
  String userId, {
  String? statusFilter,
}) async {
  if (statusFilter == null) {
    await _seedMixed(repo, userId);
  } else {
    await _seedForStatus(repo, userId, statusFilter);
  }
}

/// Seeds one delivery per status: pending, pickedUp, inTransit, delivered,
/// disputed, cancelled.
Future<void> _seedMixed(DeliveryRepository repo, String userId) async {
  await _createAtStatus(repo, userId, 'pending', 0);
  await _createAtStatus(repo, userId, 'pickedUp', 1);
  await _createAtStatus(repo, userId, 'inTransit', 2);
  await _createAtStatus(repo, userId, 'delivered', 3);
  await _createAtStatus(repo, userId, 'disputed', 0);
  await _createAtStatus(repo, userId, 'cancelled', 1);
}

/// Seeds 3 deliveries all at the given [status].
Future<void> _seedForStatus(
  DeliveryRepository repo,
  String userId,
  String status,
) async {
  for (var i = 0; i < 3; i++) {
    await _createAtStatus(repo, userId, status, i);
  }
}

/// Creates a single delivery at the given [status] for a farmer user.
Future<void> _createAtStatus(
  DeliveryRepository repo,
  String userId,
  String status,
  int nameIndex,
) async {
  final truckerId = _uuid.v4();
  final buyerId = _uuid.v4();
  final idx = nameIndex % _truckerNames.length;

  final delivery = await repo.createDelivery(
    tripId: _uuid.v4(),
    orderId: 'order-test-001',
    listingId: _uuid.v4(),
    farmerId: userId,
    farmerName: 'You (Farmer)',
    truckerId: truckerId,
    truckerName: _truckerNames[idx],
    buyerId: buyerId,
    buyerName: _buyerNames[idx],
    qrToken: 'debug-seed-farmer-${_uuid.v4().substring(0, 8)}',
  );

  // Advance to the target status
  final needsPickup = status == 'pickedUp' ||
      status == 'inTransit' ||
      status == 'delivered' ||
      status == 'disputed';
  final needsTransit =
      status == 'inTransit' || status == 'delivered' || status == 'disputed';
  final needsDelivery = status == 'delivered' || status == 'disputed';

  if (needsPickup) {
    await repo.confirmPickup(
      deliveryId: delivery.deliveryId,
      truckerId: truckerId,
    );
  }
  if (needsTransit) {
    await repo.markInTransit(
      deliveryId: delivery.deliveryId,
      truckerId: truckerId,
    );
  }
  if (needsDelivery) {
    await repo.confirmDelivery(
      deliveryId: delivery.deliveryId,
      buyerId: buyerId,
    );
  }
  if (status == 'disputed') {
    await repo.raiseDispute(
      deliveryId: delivery.deliveryId,
      userId: userId,
      reason: 'Received 80kg instead of 100kg as listed',
    );
  }
  if (status == 'cancelled') {
    await repo.cancelDelivery(
      deliveryId: delivery.deliveryId,
      userId: userId,
    );
  }
}
