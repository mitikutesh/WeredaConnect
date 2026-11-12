import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

/// The trucker's own drop-off code (ESC-35): a fresh nonce made on the
/// trucker's phone after pickup, kept there ([DropoffHandoffStore]) and
/// registered with the server, which checks the buyer's confirm against it.
///
/// It is made right after the pickup is confirmed, so on a phone the
/// registration travels in the queue behind the pickup; again when the
/// drop-off QR is opened and none is kept (picked up on another phone or an
/// older version); and by the trucker's New code, which replaces a lost or
/// locked one. Opening a kept code sends it again (the server keeps its
/// freshness and misses for the same nonce), so a registration that did
/// not get through from web is retried. Nothing here logs a nonce or code.
class DropoffQrIssuer {
  DropoffQrIssuer({
    required QrVerificationService qrService,
    required BaseDeliveryRepository repository,
    required DropoffHandoffStore store,
  })  : _qr = qrService,
        _repository = repository,
        _store = store;

  final QrVerificationService _qr;
  final BaseDeliveryRepository _repository;
  final DropoffHandoffStore _store;

  /// Make, keep and register a new drop-off code for [delivery]'s trucker:
  /// the code, and whether the server got it. The code is returned even
  /// when it did not (it is sent again on the next open). Throws only when
  /// no code could be made.
  Future<(KeptHandoff, bool)> issue(Delivery delivery) async {
    final (token, nonce) = await _qr.generateQrToken(delivery, dropoff: true);
    final kept = KeptHandoff(
      token: token,
      code: HandoffCode.derive(nonce: nonce, deliveryId: delivery.deliveryId),
      nonce: nonce,
    );
    await _store.keep(delivery.truckerId, delivery.deliveryId, kept);
    return (kept, await register(delivery, kept));
  }

  /// Send [kept]'s nonce to the server (again). False when it did not get
  /// through, or [kept] is the farmer's token kept before ESC-35 (nothing
  /// to register). Never throws.
  Future<bool> register(Delivery delivery, KeptHandoff kept) async {
    final nonce = kept.nonce;
    if (!kept.isTruckers || nonce == null) return false;
    try {
      await _repository.persistDropoffQrNonce(
        deliveryId: delivery.deliveryId,
        nonce: nonce,
        truckerId: delivery.truckerId,
      );
      return true;
    } on Object catch (e) {
      debugPrint('[Delivery] drop-off code not registered: ${e.runtimeType}');
      return false;
    }
  }
}
