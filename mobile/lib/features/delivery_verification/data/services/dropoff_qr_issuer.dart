import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

/// The trucker's own drop-off code (ESC-35): a fresh nonce made on the
/// trucker's phone, kept there ([DropoffHandoffStore]) and registered with
/// the server, which checks the buyer's confirm against it.
///
/// It is made right after the pickup is confirmed, so on a phone the
/// registration travels in the queue behind the pickup. A registration never
/// replaces a code the server already keeps (one made on another phone, or
/// later), so a stale or second device cannot void the code the buyer holds.
/// Only the trucker's New drop-off code ([issue] with `renew`) replaces it,
/// after the trucker confirmed that any other code stops working. Opening a
/// kept code sends it again (the same nonce changes nothing on the server),
/// so a registration that did not get through from web is retried. Nothing
/// here logs a nonce or code.
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

  /// Make, keep and send a new drop-off code for [delivery]'s trucker: the
  /// code, and what the server made of it. [renew] replaces a code the
  /// server keeps (New drop-off code); without it the code is stored only
  /// while none is. The code is returned even when it did not get through
  /// (it is sent again on the next open). Throws only when no code could
  /// be made.
  Future<(KeptHandoff, DropoffCodeRegistration)> issue(
    Delivery delivery, {
    bool renew = false,
  }) async {
    final (token, nonce) = await _qr.generateQrToken(delivery, dropoff: true);
    final kept = KeptHandoff(
      token: token,
      code: HandoffCode.derive(nonce: nonce, deliveryId: delivery.deliveryId),
      nonce: nonce,
    );
    await _store.keep(delivery.truckerId, delivery.deliveryId, kept);
    return (kept, await _send(delivery, nonce, renew: renew));
  }

  /// Send [kept]'s nonce to the server again, never replacing another code.
  /// [DropoffCodeRegistration.notStored] for a farmer's token kept before
  /// ESC-35 (nothing to send). Never throws.
  Future<DropoffCodeRegistration> register(
    Delivery delivery,
    KeptHandoff kept,
  ) async {
    final nonce = kept.nonce;
    if (!kept.isTruckers || nonce == null) {
      return DropoffCodeRegistration.notStored;
    }
    return _send(delivery, nonce, renew: false);
  }

  Future<DropoffCodeRegistration> _send(
    Delivery delivery,
    String nonce, {
    required bool renew,
  }) async {
    try {
      return await _repository.persistDropoffQrNonce(
        deliveryId: delivery.deliveryId,
        nonce: nonce,
        truckerId: delivery.truckerId,
        renew: renew,
      );
    } on Object catch (e) {
      debugPrint('[Delivery] drop-off code not registered: ${e.runtimeType}');
      return DropoffCodeRegistration.failed;
    }
  }
}
