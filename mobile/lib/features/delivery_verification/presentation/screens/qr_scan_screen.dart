import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
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

/// QR scan screen for pickup and delivery verification.
///
/// Two phases: Scanning (camera preview) and Verified (GPS + photo + confirm).
/// Uses offline HMAC-SHA256 verification — no internet needed.
class QrScanScreen extends StatefulWidget {
  final Delivery delivery;
  final VerificationType verificationType;
  final QrVerificationService qrService;
  final GpsCaptureService gpsService;
  final PhotoManager photoManager;

  const QrScanScreen({
    required this.delivery,
    required this.verificationType,
    required this.qrService,
    required this.gpsService,
    required this.photoManager,
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

  void _confirm() {
    setState(() => _isConfirming = true);

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

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final isPickup = widget.verificationType == VerificationType.pickup;
    final title = isPickup
        ? '${l10n.deliveryScanQr} (${l10n.deliveryConfirmPickup})'
        : '${l10n.deliveryScanQr} (${l10n.deliveryConfirmDelivery})';

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
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(title),
          elevation: 0,
          actions: [
            if (_phase == _ScanPhase.scanning)
              IconButton(
                icon: const Icon(Icons.flash_on),
                onPressed: () => _scannerController?.toggleTorch(),
                tooltip: l10n.deliveryToggleFlash,
              ),
          ],
        ),
        body: SafeArea(
          child: switch (_phase) {
            _ScanPhase.scanning => _buildScanningPhase(),
            _ScanPhase.verified => _buildVerifiedPhase(),
            _ScanPhase.invalid => _buildInvalidPhase(),
          },
        ),
      ),
    );
  }

  Widget _buildScanningPhase() {
    return Stack(
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
          bottom: 80,
          left: 24,
          right: 24,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
            decoration: BoxDecoration(
              color: Colors.black.withValues(alpha: 0.7),
              borderRadius: BorderRadius.circular(8),
            ),
            child: Text(
              AppLocalizations.of(context)!.deliveryQrPointCamera,
              style: const TextStyle(color: Colors.white, fontSize: 16),
              textAlign: TextAlign.center,
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildVerifiedPhase() {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final isPickup = widget.verificationType == VerificationType.pickup;
    final photoType = isPickup ? PhotoType.pickup : PhotoType.dropoff;

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        children: [
          // Green checkmark
          const Icon(
            Icons.check_circle,
            color: Colors.green,
            size: 64,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.deliveryQrVerified,
            style: theme.textTheme.headlineSmall?.copyWith(
              fontWeight: FontWeight.bold,
              color: Colors.green,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            l10n.deliveryIdLabel(
              widget.delivery.deliveryId.substring(0, 8),
            ),
            style: theme.textTheme.bodyLarge,
          ),
          const SizedBox(height: 24),

          // GPS section
          _buildGpsSection(theme),
          const SizedBox(height: 24),

          // Photo section
          PhotoEvidenceWidget(
            photoRef: _photoRef,
            photoType: photoType,
            deliveryId: widget.delivery.deliveryId,
            onCapture: _capturePhoto,
          ),
          const SizedBox(height: 32),

          // Confirm button
          SizedBox(
            width: double.infinity,
            child: FilledButton(
              onPressed: _isConfirming ? null : _confirm,
              style: FilledButton.styleFrom(
                backgroundColor: Colors.green,
                minimumSize: const Size.fromHeight(48),
              ),
              child: _isConfirming
                  ? const SizedBox(
                      height: 20,
                      width: 20,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        valueColor: AlwaysStoppedAnimation<Color>(Colors.white),
                      ),
                    )
                  : Text(
                      isPickup
                          ? l10n.deliveryConfirmPickup
                          : l10n.deliveryConfirmDelivery,
                    ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildGpsSection(ThemeData theme) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.location_on, size: 20),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context)!.deliveryGpsCaptured,
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          if (_isCapturingGps)
            Row(
              children: [
                const SizedBox(
                  height: 16,
                  width: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
                const SizedBox(width: 8),
                Text(AppLocalizations.of(context)!.deliveryGpsCapturing),
              ],
            )
          else if (_gpsResult != null && _gpsResult!.isAvailable)
            Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${_gpsResult!.latitude!.toStringAsFixed(4)}, ${_gpsResult!.longitude!.toStringAsFixed(4)}',
                  style: theme.textTheme.bodyMedium,
                ),
                Text(
                  AppLocalizations.of(context)!.deliveryGpsAccuracy(
                    _gpsResult!.accuracy!.toStringAsFixed(0),
                  ),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ],
            )
          else
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: Colors.orange.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  const Icon(
                    Icons.warning_amber,
                    color: Colors.orange,
                    size: 20,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context)!.deliveryGpsUnavailable,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: Colors.orange.shade800,
                      ),
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
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

  Widget _buildInvalidPhase() {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(
              Icons.cancel,
              color: Colors.red,
              size: 64,
            ),
            const SizedBox(height: 16),
            Text(
              l10n.deliveryQrFailed,
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.bold,
                color: Colors.red,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              _localizeQrError(l10n),
              style: theme.textTheme.bodyLarge,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 32),
            FilledButton.icon(
              onPressed: _resetScanner,
              icon: const Icon(Icons.refresh),
              label: Text(l10n.deliveryTryAgain),
            ),
          ],
        ),
      ),
    );
  }
}
