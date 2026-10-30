import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/photo_evidence_widget.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// QR scan screen for pickup and delivery verification.
///
/// Two phases: Scanning (the camera in a rounded panel on the soil dark
/// page, as the Trip detail board's header) and Verified (GPS + photo on
/// the chalk page, Confirm pinned at the foot). Uses offline HMAC-SHA256
/// verification, no internet needed.
///
/// Confirming a DELIVERY (buyer delivery confirm and buyer self-pickup
/// confirm) releases the farmer's escrow, so it first asks the device to
/// confirm the user (biometric or phone PIN / pattern). A device without a
/// lock screen proceeds with a logged warning; the server stays the
/// authority. Pickup confirmation (trucker) is not gated.
class QrScanScreen extends StatefulWidget {
  final Delivery delivery;
  final VerificationType verificationType;
  final QrVerificationService qrService;
  final GpsCaptureService gpsService;
  final PhotoManager photoManager;

  /// Device authentication before a delivery confirm. Defaults to the real
  /// `local_auth` prompt; tests inject a fake.
  final LocalAuthGate? localAuthGate;

  const QrScanScreen({
    required this.delivery,
    required this.verificationType,
    required this.qrService,
    required this.gpsService,
    required this.photoManager,
    this.localAuthGate,
    super.key,
  });

  @override
  State<QrScanScreen> createState() => _QrScanScreenState();
}

enum _ScanPhase { scanning, verified, invalid }

class _QrScanScreenState extends State<QrScanScreen> {
  _ScanPhase _phase = _ScanPhase.scanning;
  MobileScannerController? _scannerController;
  String? _errorCode;

  // Verified phase data
  GpsCaptureResult? _gpsResult;
  String? _photoRef;
  String? _scannedNonce;
  bool _isCapturingGps = false;
  bool _isConfirming = false;

  @override
  void initState() {
    super.initState();
    _scannerController = MobileScannerController();
  }

  @override
  void dispose() {
    _scannerController?.dispose();
    super.dispose();
  }

  void _onBarcodeDetected(BarcodeCapture capture) {
    if (_phase != _ScanPhase.scanning) return;

    final barcodes = capture.barcodes;
    if (barcodes.isEmpty) return;

    final rawValue = barcodes.first.rawValue;
    if (rawValue == null) return;

    _scannerController?.stop();

    final result = widget.qrService.parseQrToken(rawValue, widget.delivery);
    if (result.isValid) {
      setState(() {
        _phase = _ScanPhase.verified;
        _scannedNonce = result.nonce;
      });
      _captureGps();
    } else {
      setState(() {
        _phase = _ScanPhase.invalid;
        _errorCode = result.errorCode;
      });
    }
  }

  Future<void> _captureGps() async {
    setState(() => _isCapturingGps = true);
    final result = await widget.gpsService.captureCurrentPosition();
    if (mounted) {
      setState(() {
        _gpsResult = result;
        _isCapturingGps = false;
      });
    }
  }

  Future<void> _capturePhoto() async {
    final photoType = widget.verificationType == VerificationType.pickup
        ? PhotoType.pickup
        : PhotoType.dropoff;

    final result = await widget.photoManager.captureAndCompress(
      deliveryId: widget.delivery.deliveryId,
      type: photoType,
    );

    if (mounted && result.success) {
      setState(() {
        _photoRef = result.localPath;
      });
    }
  }

  Future<void> _confirm() async {
    if (_isConfirming) return;
    setState(() => _isConfirming = true);

    if (widget.verificationType == VerificationType.delivery) {
      final l10n = AppLocalizations.of(context)!;
      final gate = widget.localAuthGate ?? DeviceLocalAuthGate();
      final outcome = await gate.authenticate(l10n.qrConfirmAuthReason);
      if (!mounted) return;
      if (!outcome.allowsProceed) {
        setState(() => _isConfirming = false);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(l10n.qrConfirmAuthFailed)),
        );
        return;
      }
    }

    LatLng? gps;
    if (_gpsResult != null && _gpsResult!.isAvailable) {
      gps = LatLng()
        ..latitude = _gpsResult!.latitude!
        ..longitude = _gpsResult!.longitude!;
    }

    if (widget.verificationType == VerificationType.pickup) {
      context.read<DeliveryBloc>().add(
            ConfirmPickup(
              deliveryId: widget.delivery.deliveryId,
              truckerId: widget.delivery.truckerId,
              gpsPickup: gps,
              photoRef: _photoRef,
              scannedQrNonce: _scannedNonce,
              tripId: widget.delivery.tripId,
            ),
          );
    } else {
      context.read<DeliveryBloc>().add(
            ConfirmDelivery(
              deliveryId: widget.delivery.deliveryId,
              buyerId: widget.delivery.buyerId,
              gpsDropoff: gps,
              photoRef: _photoRef,
              scannedQrNonce: _scannedNonce,
              tripId: widget.delivery.tripId,
            ),
          );
    }
  }

  void _resetScanner() {
    setState(() {
      _phase = _ScanPhase.scanning;
      _errorCode = null;
      _gpsResult = null;
      _photoRef = null;
      _scannedNonce = null;
    });
    _scannerController?.start();
  }

  bool get _isPickup => widget.verificationType == VerificationType.pickup;

  String _title(AppLocalizations l10n) =>
      _isPickup ? l10n.deliveryConfirmPickup : l10n.deliveryConfirmDelivery;

  String _deliveryLabel(AppLocalizations l10n) =>
      l10n.deliveryIdLabel(widget.delivery.deliveryId.substring(0, 8));

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final scanning = _phase == _ScanPhase.scanning;

    return BlocListener<DeliveryBloc, DeliveryState>(
      listener: (context, state) {
        if (state is PickupConfirmed || state is DeliveryConfirmed) {
          Navigator.of(context).pop(true);
        } else if (state is DeliveryError) {
          setState(() => _isConfirming = false);
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeDeliveryError(
                  AppLocalizations.of(context)!,
                  state.code,
                ),
              ),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        // The camera sits on the soil dark page; the result pages are chalk.
        backgroundColor: scanning ? AppTheme.soilDark : null,
        body: SafeArea(
          bottom: scanning,
          child: switch (_phase) {
            _ScanPhase.scanning => _buildScanningPhase(l10n),
            _ScanPhase.verified => _buildVerifiedPhase(l10n),
            _ScanPhase.invalid => _buildInvalidPhase(l10n),
          },
        ),
        bottomNavigationBar: switch (_phase) {
          _ScanPhase.scanning => null,
          _ScanPhase.verified => FwBottomActions(
              children: [
                FilledButton(
                  onPressed: _isConfirming ? null : _confirm,
                  style: _primaryStyle,
                  child: _isConfirming
                      ? const SizedBox(
                          height: 20,
                          width: 20,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            valueColor:
                                AlwaysStoppedAnimation<Color>(Colors.white),
                          ),
                        )
                      : Text(_title(l10n)),
                ),
              ],
            ),
          _ScanPhase.invalid => FwBottomActions(
              children: [
                FilledButton.icon(
                  onPressed: _resetScanner,
                  icon: const Icon(Icons.refresh),
                  label: Text(l10n.deliveryTryAgain),
                  style: _primaryStyle,
                ),
              ],
            ),
        },
      ),
    );
  }

  static final ButtonStyle _primaryStyle = FilledButton.styleFrom(
    minimumSize: const Size.fromHeight(56),
    textStyle: const TextStyle(
      fontFamily: AppTheme.fontFamily,
      fontFamilyFallback: AppTheme.ethiopicFontFallback,
      fontSize: 16,
      fontWeight: FontWeight.w800,
    ),
  );

  /// The camera in a rounded panel under the dark header, the scan frame
  /// over it and the instruction at its foot.
  Widget _buildScanningPhase(AppLocalizations l10n) {
    return FwContentWidth(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _DarkHeader(
            title: _title(l10n),
            subtitle: l10n.deliveryScanQr,
            trailing: Semantics(
              button: true,
              label: l10n.deliveryToggleFlash,
              child: Material(
                color: AppTheme.soilCard,
                borderRadius: BorderRadius.circular(14),
                clipBehavior: Clip.antiAlias,
                child: InkWell(
                  onTap: () => _scannerController?.toggleTorch(),
                  child: const SizedBox(
                    width: 44,
                    height: 44,
                    child: Icon(Icons.flash_on, size: 20, color: Colors.white),
                  ),
                ),
              ),
            ),
          ),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 20),
              child: ClipRRect(
                borderRadius: BorderRadius.circular(24),
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    MobileScanner(
                      controller: _scannerController,
                      onDetect: _onBarcodeDetected,
                    ),
                    // Scan frame overlay
                    Center(
                      child: Container(
                        width: 250,
                        height: 250,
                        decoration: BoxDecoration(
                          border: Border.all(color: Colors.white, width: 2),
                          borderRadius: BorderRadius.circular(16),
                        ),
                      ),
                    ),
                    // Instruction text
                    Positioned(
                      bottom: 20,
                      left: 20,
                      right: 20,
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 16,
                          vertical: 12,
                        ),
                        decoration: BoxDecoration(
                          color: Colors.black.withValues(alpha: 0.7),
                          borderRadius: BorderRadius.circular(16),
                        ),
                        child: Text(
                          l10n.deliveryQrPointCamera,
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: 15,
                            fontWeight: FontWeight.w700,
                          ),
                          textAlign: TextAlign.center,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  /// The code passed: the GPS fix and the photo to add, Confirm at the foot.
  Widget _buildVerifiedPhase(AppLocalizations l10n) {
    final photoType = _isPickup ? PhotoType.pickup : PhotoType.dropoff;

    return SingleChildScrollView(
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            FwDetailHeader(
              title: _title(l10n),
              subtitle: _deliveryLabel(l10n),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FwCard(
                    child: Row(
                      children: [
                        Container(
                          width: 44,
                          height: 44,
                          decoration: BoxDecoration(
                            color: AppTheme.fieldGreenTint,
                            borderRadius: BorderRadius.circular(14),
                          ),
                          child: const Icon(
                            Icons.check_circle,
                            size: 24,
                            color: AppTheme.fieldGreen,
                          ),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Text(
                            l10n.deliveryQrVerified,
                            style: const TextStyle(
                              fontSize: 15,
                              fontWeight: FontWeight.w800,
                              color: AppTheme.fieldGreen,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 16),

                  // GPS section
                  FwSectionLabel(
                    l10n.deliveryGpsCaptured,
                    padding: const EdgeInsets.only(bottom: 8),
                  ),
                  FwCard(child: _buildGpsContent(l10n)),
                  const SizedBox(height: 16),

                  // Photo section
                  FwCard(
                    child: PhotoEvidenceWidget(
                      photoRef: _photoRef,
                      photoType: photoType,
                      deliveryId: widget.delivery.deliveryId,
                      onCapture: _capturePhoto,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildGpsContent(AppLocalizations l10n) {
    if (_isCapturingGps) {
      return Row(
        children: [
          const SizedBox(
            height: 16,
            width: 16,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
          const SizedBox(width: 10),
          Text(
            l10n.deliveryGpsCapturing,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: AppTheme.mutedSteel,
            ),
          ),
        ],
      );
    }
    final gps = _gpsResult;
    if (gps != null && gps.isAvailable) {
      return Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Padding(
            padding: EdgeInsets.only(top: 2),
            child:
                Icon(Icons.location_on, size: 18, color: AppTheme.fieldGreen),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${gps.latitude!.toStringAsFixed(4)}, ${gps.longitude!.toStringAsFixed(4)}',
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                ),
                Text(
                  l10n.deliveryGpsAccuracy(gps.accuracy!.toStringAsFixed(0)),
                  style: const TextStyle(
                    fontSize: 12,
                    color: AppTheme.mutedSteel,
                  ),
                ),
              ],
            ),
          ),
        ],
      );
    }
    return Row(
      children: [
        const Icon(
          Icons.warning_amber,
          size: 18,
          color: AppTheme.actionInk,
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Text(
            l10n.deliveryGpsUnavailable,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: AppTheme.actionInk,
            ),
          ),
        ),
      ],
    );
  }

  String _localizeQrError(AppLocalizations l10n) {
    return switch (_errorCode) {
      QrErrorCode.tooLarge => l10n.deliveryQrTooLarge,
      QrErrorCode.incomplete => l10n.deliveryQrIncomplete,
      QrErrorCode.invalidData => l10n.deliveryQrInvalidData,
      QrErrorCode.wrongDelivery => l10n.deliveryQrWrongDelivery,
      QrErrorCode.invalidFormat => l10n.deliveryQrInvalidFormat,
      QrErrorCode.processFailed => l10n.deliveryQrProcessFailed,
      _ => l10n.deliveryQrFailed,
    };
  }

  /// The code failed: why, with Try Again at the foot.
  Widget _buildInvalidPhase(AppLocalizations l10n) {
    return SingleChildScrollView(
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            FwDetailHeader(
              title: _title(l10n),
              subtitle: _deliveryLabel(l10n),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
              child: FwCard(
                padding: const EdgeInsets.all(24),
                child: Column(
                  children: [
                    Container(
                      width: 56,
                      height: 56,
                      decoration: BoxDecoration(
                        color: AppTheme.statusErrorContainer,
                        borderRadius: BorderRadius.circular(18),
                      ),
                      child: const Icon(
                        Icons.cancel,
                        size: 28,
                        color: AppTheme.statusError,
                      ),
                    ),
                    const SizedBox(height: 14),
                    Text(
                      l10n.deliveryQrFailed,
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.statusError,
                      ),
                    ),
                    const SizedBox(height: 6),
                    Text(
                      _localizeQrError(l10n),
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        fontSize: 13,
                        height: 1.5,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The Trip detail board's dark header, with the back square on the soil
/// card colour, the title in white and a trailing square (the torch).
class _DarkHeader extends StatelessWidget {
  final String title;
  final String subtitle;
  final Widget trailing;

  const _DarkHeader({
    required this.title,
    required this.subtitle,
    required this.trailing,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 8),
      child: Row(
        children: [
          const FwBackButton(dark: true),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  subtitle,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.soilMuted,
                  ),
                ),
                Semantics(
                  header: true,
                  child: Text(
                    title,
                    style:
                        FwDetailHeader.titleStyle.copyWith(color: Colors.white),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          trailing,
        ],
      ),
    );
  }
}
