import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_qr_issuer.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';

/// The trucker's pickup scan of the farmer's QR, the one wiring trip mode
/// and the trip's page share (T-2). The scan screen pops when the pickup is
/// confirmed.
Future<void> openPickupScan(BuildContext context, Delivery delivery) =>
    Navigator.of(context).push<void>(
      MaterialPageRoute(
        builder: (_) => QrScanScreen(
          delivery: delivery,
          verificationType: VerificationType.pickup,
          qrService: QrVerificationService(
            deviceKeyService: DeviceSigningKeyService(),
          ),
          gpsService: GpsCaptureService(),
          photoManager: PhotoManager(),
        ),
      ),
    );

/// The trucker's drop-off QR for the buyer, read for the signed-in
/// [userId]: the trucker's own code kept on this phone since the pickup
/// (ESC-35), sent to the server again; a new one made there when none is
/// kept (picked up on another phone, or on an older version); or the
/// farmer's token an older version kept at pickup (T-3), which still
/// works. Nothing opens for anyone but the delivery's trucker. [repository]
/// defaults to the app's [DeliveryBloc] one.
Future<void> openDropoffQr(
  BuildContext context,
  Delivery delivery, {
  required String userId,
  DropoffHandoffStore? store,
  BaseDeliveryRepository? repository,
  QrVerificationService? qrService,
}) async {
  if (userId.isEmpty || userId != delivery.truckerId) return;
  final handoffs = store ?? SecureDropoffHandoffStore();
  final issuer = DropoffQrIssuer(
    qrService: qrService ??
        QrVerificationService(deviceKeyService: DeviceSigningKeyService()),
    repository: repository ?? context.read<DeliveryBloc>().repository,
    store: handoffs,
  );
  final kept = await handoffs.read(userId, delivery.deliveryId);
  if (!context.mounted) return;
  await Navigator.of(context).push<void>(
    MaterialPageRoute(
      builder: (_) => QrGenerateScreen.dropoff(
        delivery: delivery,
        userId: userId,
        dropoff: kept,
        issuer: issuer,
      ),
    ),
  );
}
