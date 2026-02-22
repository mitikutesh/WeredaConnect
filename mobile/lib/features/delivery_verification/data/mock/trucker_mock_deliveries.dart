import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';

const _uuid = Uuid();

const _farmerNames = [
  'Abebe Bekele',
  'Mulugeta Tadesse',
  'Alemayehu Worku',
  'Girma Desalegn',
];
const _buyerNames = [
  'Addis Wholesale',
  'Merkato Traders',
  'Bahir Dar Produce',
  'Hawassa Market',
];

/// Seeds mock deliveries where [userId] is the trucker.
///
/// When [statusFilter] is null, seeds one delivery per status (mixed view).
/// When [statusFilter] is set, seeds 3 deliveries all at that status.
Future<void> seedTruckerMockDeliveries(
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

Future<void> _seedMixed(DeliveryRepository repo, String userId) async {
  await _createAtStatus(repo, userId, 'pending', 0);
  await _createAtStatus(repo, userId, 'pickedUp', 1);
  await _createAtStatus(repo, userId, 'inTransit', 2);
  await _createAtStatus(repo, userId, 'delivered', 3);
  await _createAtStatus(repo, userId, 'disputed', 0);
  await _createAtStatus(repo, userId, 'cancelled', 1);
}

Future<void> _seedForStatus(
  DeliveryRepository repo,
  String userId,
  String status,
) async {
  for (var i = 0; i < 3; i++) {
    await _createAtStatus(repo, userId, status, i);
  }
}

Future<void> _createAtStatus(
  DeliveryRepository repo,
  String userId,
  String status,
  int nameIndex,
) async {
  final buyerId = _uuid.v4();
  final idx = nameIndex % _farmerNames.length;

  final delivery = await repo.createDelivery(
    tripId: _uuid.v4(),
    listingId: _uuid.v4(),
    farmerId: _uuid.v4(),
    farmerName: _farmerNames[idx],
    truckerId: userId,
    truckerName: 'You (Trucker)',
    buyerId: buyerId,
    buyerName: _buyerNames[idx],
    qrToken: 'debug-seed-trucker-${_uuid.v4().substring(0, 8)}',
  );

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
      truckerId: userId,
    );
  }
  if (needsTransit) {
    await repo.markInTransit(
      deliveryId: delivery.deliveryId,
      truckerId: userId,
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
      reason: 'Delivery route was changed without approval',
    );
  }
  if (status == 'cancelled') {
    await repo.cancelDelivery(
      deliveryId: delivery.deliveryId,
      userId: userId,
    );
  }
}
