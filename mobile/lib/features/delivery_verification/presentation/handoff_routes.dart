import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

/// The trucker's drop-off QR for the buyer (T-3): the farmer's token kept
/// from this phone's pickup scan, or only the code typed at pickup, read for
/// the signed-in [userId]. Nothing opens for anyone but the delivery's
/// trucker. Without a kept one (picked up on another phone, or before this
/// app version) it says so.
Future<void> openDropoffQr(
  BuildContext context,
  Delivery delivery, {
  required String userId,
  DropoffHandoffStore? store,
}) async {
  if (userId.isEmpty || userId != delivery.truckerId) return;
  final kept = await (store ?? SecureDropoffHandoffStore())
      .read(userId, delivery.deliveryId);
  if (!context.mounted) return;
  if (kept == null) {
    final l10n = AppLocalizations.of(context)!;
    ScaffoldMessenger.maybeOf(context)
      ?..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(l10n.deliveryDropoffQrMissing)));
    return;
  }
  await Navigator.of(context).push<void>(
    MaterialPageRoute(
      builder: (_) => QrGenerateScreen.dropoff(
        delivery: delivery,
        userId: userId,
        dropoff: kept,
      ),
    ),
  );
}
