import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:geolocator/geolocator.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
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
/// Phases: Scanning (the camera in a rounded panel on the soil dark page, as
/// the Trip detail board's header), Typing (T-1: the nine-digit code typed
/// when the camera fails, its check digit checked on the phone) and Read
/// (GPS + photo on the chalk page, Confirm pinned at the foot). The phone
/// only reads the QR or checks the code's check digit; the server compares
/// it with the farmer's nonce once the confirm reaches it, so the copy says
/// so instead of "verified". A typed code needs a GPS fix.
///
/// FRD-11: the location is stamped at the scan. Confirm waits while the
/// fix is taken (at most [gpsWaitCap]), so a quick tap cannot skip it; a
/// phone that gets no fix still confirms a scanned QR without one.
///
/// A trucker's confirmed pickup keeps the farmer's token (or the typed code)
/// on the phone ([DropoffHandoffStore]) for the drop-off QR (T-3).
///
/// Confirming a DELIVERY (buyer delivery confirm and buyer self-pickup
/// confirm) releases the farmer's escrow, so it first asks the device to
/// confirm the user (biometric or phone PIN / pattern). A device without a
/// lock screen proceeds with a logged warning; the server stays the
/// authority. Pickup confirmation (trucker) is not gated.
class QrScanScreen extends StatefulWidget {
  /// The longest Confirm waits for a fix. The capture itself stops after
  /// 15 seconds; this also covers a permission prompt nobody answers.
  static const gpsWaitCap = Duration(seconds: 30);

  final Delivery delivery;
  final VerificationType verificationType;
  final QrVerificationService qrService;
  final GpsCaptureService gpsService;
  final PhotoManager photoManager;

  /// Device authentication before a delivery confirm. Defaults to the real
  /// `local_auth` prompt; tests inject a fake.
  final LocalAuthGate? localAuthGate;

  /// Where a confirmed pickup keeps the handoff for the drop-off QR.
  /// Defaults to secure storage; tests inject a memory store.
  final DropoffHandoffStore? handoffStore;

  const QrScanScreen({
    required this.delivery,
    required this.verificationType,
    required this.qrService,
    required this.gpsService,
    required this.photoManager,
    this.localAuthGate,
    this.handoffStore,
    super.key,
  });

  @override
  State<QrScanScreen> createState() => _QrScanScreenState();
}

enum _ScanPhase { scanning, typing, verified, invalid }

class _QrScanScreenState extends State<QrScanScreen> {
  _ScanPhase _phase = _ScanPhase.scanning;
  MobileScannerController? _scannerController;
  String? _errorCode;

  // Verified phase data
  GpsCaptureResult? _gpsResult;
  String? _photoRef;

  /// The photo's bytes for the preview on web (FRD-12), where it has no
  /// file and went up already.
  Uint8List? _photoPreview;
  String? _scannedNonce;
  String? _scannedToken;
  bool _isCapturingGps = false;

  /// Counts captures, so one started before a reset cannot land after it.
  int _gpsRun = 0;
  bool _isConfirming = false;

  // Typing phase (T-1)
  final _codeController = TextEditingController();
  String? _codeError;

  /// The checked nine digits once a typed code passed its check digit.
  String? _typedCode;

  bool get _typed => _typedCode != null;

  bool get _gpsReady => _gpsResult?.isAvailable ?? false;

  @override
  void initState() {
    super.initState();
    _scannerController = MobileScannerController();
  }

  @override
  void dispose() {
    _scannerController?.dispose();
    _codeController.dispose();
    super.dispose();
  }

  /// The camera failed to start (permission, no camera, busy): start it
  /// again. The controller keeps the error until a start succeeds.
  Future<void> _retryCamera() async {
    try {
      await _scannerController?.start();
    } on Object catch (e) {
      debugPrint('[QrScan] camera restart failed: $e');
    }
  }

  /// The app's own page in the phone's settings, where the camera is
  /// allowed. Not on web: the browser's site settings hold it.
  Future<void> _openSettings() async {
    try {
      await Geolocator.openAppSettings();
    } on Object catch (e) {
      debugPrint('[QrScan] settings did not open: $e');
    }
  }

  /// "Type the code instead": the camera stops, the code field opens.
  void _openTyping() {
    _stopCamera();
    setState(() {
      _phase = _ScanPhase.typing;
      _codeError = null;
    });
  }

  void _stopCamera() {
    final controller = _scannerController;
    if (controller == null) return;
    controller.stop().catchError((Object e) {
      debugPrint('[QrScan] camera stop failed: $e');
    });
  }

  /// The check digit catches a mistyped digit or two swapped ones here,
  /// offline. Whether the code is the farmer's is for the server to say.
  void _checkTypedCode() {
    final l10n = AppLocalizations.of(context)!;
    final code = HandoffCode.normalize(_codeController.text);
    if (code.length != HandoffCode.length) {
      setState(() => _codeError = l10n.qrTypeCodeIncomplete);
      return;
    }
    if (!HandoffCode.isValid(code)) {
      setState(() => _codeError = l10n.qrTypeCodeTypo);
      return;
    }
    FocusScope.of(context).unfocus();
    setState(() {
      _phase = _ScanPhase.verified;
      _typedCode = code;
      _scannedNonce = null;
      _scannedToken = null;
      _codeError = null;
    });
    _captureGps();
  }

  /// A confirmed pickup of a trucker delivery: keep what the farmer showed,
  /// so the drop-off QR can show it to the buyer. Never fails the pickup.
  Future<void> _keepHandoff() async {
    final delivery = widget.delivery;
    if (widget.verificationType != VerificationType.pickup ||
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType ||
        delivery.truckerId.isEmpty) {
      return;
    }
    final nonce = _scannedNonce;
    final handoff = KeptHandoff(
      token: _scannedToken,
      code: nonce != null
          ? HandoffCode.derive(nonce: nonce, deliveryId: delivery.deliveryId)
          : _typedCode,
    );
    try {
      await (widget.handoffStore ?? SecureDropoffHandoffStore())
          .keep(delivery.truckerId, delivery.deliveryId, handoff);
    } on Object catch (e) {
      debugPrint('[QrScan] handoff not kept: $e');
    }
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
        _scannedToken = rawValue;
        _typedCode = null;
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
    final run = ++_gpsRun;
    setState(() => _isCapturingGps = true);
    GpsCaptureResult result;
    try {
      result = await widget.gpsService
          .captureCurrentPosition()
          .timeout(QrScanScreen.gpsWaitCap);
    } on Object catch (e) {
      // Confirm waits for this capture, so it must always end.
      debugPrint('[QrScan] location capture failed: $e');
      result = const GpsCaptureResult.unavailable('GPS signal not available');
    }
    if (mounted && run == _gpsRun) {
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

    if (!mounted) return;
    if (result.success) {
      setState(() {
        // FRD-12: the phone's file (the sync queue uploads it after the
        // handoff), or on web the key of the photo that went up already.
        _photoRef = result.photoRef;
        _photoPreview = result.previewBytes;
      });
    } else if (!result.cancelled) {
      // The photo is optional: the handoff can still be confirmed.
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(AppLocalizations.of(context)!.deliveryPhotoFailed),
        ),
      );
    }
  }

  Future<void> _confirm() async {
    if (_isConfirming || _isCapturingGps) return;
    // A typed code needs a fix: the server refuses it without one on a
    // paid delivery, so the button waits for it.
    if (_typed && !_gpsReady) return;
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
              scannedQrCode: _typedCode,
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
              scannedQrCode: _typedCode,
              tripId: widget.delivery.tripId,
            ),
          );
    }
  }

  /// Back to the camera from the result or the typed code. The scanner is
  /// mounted again and starts the camera itself (auto start); starting it
  /// here too raced that start ("controller initializing").
  void _resetScanner() {
    _gpsRun++;
    setState(() {
      _phase = _ScanPhase.scanning;
      _errorCode = null;
      _gpsResult = null;
      _isCapturingGps = false;
      _photoRef = null;
      _photoPreview = null;
      _scannedNonce = null;
      _scannedToken = null;
      _typedCode = null;
      _codeError = null;
    });
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
          if (state is PickupConfirmed) _keepHandoff();
          // Mobile queues the confirm: a typed code is only checked once
          // it reaches the server (web waited for the answer already).
          if (_typed && !kIsWeb) {
            ScaffoldMessenger.maybeOf(context)?.showSnackBar(
              SnackBar(
                content: Text(
                  state is PickupConfirmed
                      ? l10n.qrPickupSavedCheckOnline
                      : l10n.qrDeliverySavedCheckOnline,
                ),
              ),
            );
          }
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
            _ScanPhase.typing => _buildTypingPhase(l10n),
            _ScanPhase.verified => _buildVerifiedPhase(l10n),
            _ScanPhase.invalid => _buildInvalidPhase(l10n),
          },
        ),
        bottomNavigationBar: switch (_phase) {
          _ScanPhase.scanning => null,
          _ScanPhase.typing => FwBottomActions(
              children: [
                FilledButton(
                  key: const Key('qr_code_check'),
                  onPressed: _checkTypedCode,
                  style: _primaryStyle,
                  child: Text(l10n.qrTypeCodeCheck),
                ),
                TextButton.icon(
                  key: const Key('qr_code_back_to_camera'),
                  onPressed: _resetScanner,
                  icon: const Icon(Icons.qr_code_scanner),
                  label: Text(l10n.deliveryScanQr),
                ),
              ],
            ),
          _ScanPhase.verified => FwBottomActions(
              children: [
                FilledButton(
                  key: const Key('qr_confirm'),
                  onPressed:
                      _isConfirming || _isCapturingGps || (_typed && !_gpsReady)
                          ? null
                          : _confirm,
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
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 0),
              child: ClipRRect(
                borderRadius: BorderRadius.circular(24),
                child: MobileScanner(
                  controller: _scannerController,
                  onDetect: _onBarcodeDetected,
                  // T-1: a camera that cannot start says why and how to fix
                  // it, with the typed code next to it.
                  errorBuilder: (context, error) => QrCameraErrorView(
                    permissionDenied: error.errorCode ==
                        MobileScannerErrorCode.permissionDenied,
                    onRetry: _retryCamera,
                    onOpenSettings: kIsWeb ? null : _openSettings,
                    onTypeCode: _openTyping,
                  ),
                  // Drawn only over a running preview.
                  overlayBuilder: (context, constraints) => Stack(
                    fit: StackFit.expand,
                    children: [
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
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 12, 20, 16),
            child: OutlinedButton.icon(
              key: const Key('qr_type_code'),
              onPressed: _openTyping,
              icon: const Icon(Icons.dialpad, size: 18),
              label: Text(l10n.qrTypeCodeInstead),
              style: OutlinedButton.styleFrom(
                backgroundColor: Colors.transparent,
                foregroundColor: Colors.white,
                side: const BorderSide(color: Color(0xFF4A5A52)),
                minimumSize: const Size.fromHeight(52),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 15,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  /// Who shows the code, and at a delivery the warning to type it only
  /// with the crops there.
  String _typingHint(AppLocalizations l10n) {
    if (_isPickup) return l10n.qrTypeCodePickupHint;
    return widget.delivery.deliveryType ==
            DeliveryConstants.selfPickupDeliveryType
        ? l10n.qrTypeCodeSelfPickupHint
        : l10n.qrTypeCodeDropoffHint;
  }

  /// T-1: the nine digits, typed. Digits only, so any keyboard works.
  Widget _buildTypingPhase(AppLocalizations l10n) {
    return SingleChildScrollView(
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            FwDetailHeader(
              title: l10n.qrTypeCodeTitle,
              subtitle: _deliveryLabel(l10n),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
              child: FwCard(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Text(
                      _typingHint(l10n),
                      style: const TextStyle(
                        fontSize: 14,
                        height: 1.45,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(height: 16),
                    TextField(
                      key: const Key('qr_code_field'),
                      controller: _codeController,
                      autofocus: true,
                      keyboardType: TextInputType.number,
                      textInputAction: TextInputAction.done,
                      inputFormatters: const [_HandoffCodeFormatter()],
                      onSubmitted: (_) => _checkTypedCode(),
                      style: const TextStyle(
                        fontSize: 24,
                        fontWeight: FontWeight.w800,
                        letterSpacing: 4,
                        color: AppTheme.soilDark,
                      ),
                      decoration: InputDecoration(
                        labelText: l10n.qrTypeCodeLabel,
                        errorText: _codeError,
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
                            _typed
                                ? l10n.deliveryCodeTypedCheckedOnline
                                : l10n.deliveryQrReadCheckedOnline,
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
                      previewBytes: _photoPreview,
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
    final unavailable = Row(
      children: [
        const Icon(
          Icons.warning_amber,
          size: 18,
          color: AppTheme.actionInk,
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Text(
            _typed ? l10n.qrTypedCodeNeedsGps : l10n.deliveryGpsUnavailable,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: AppTheme.actionInk,
            ),
          ),
        ),
      ],
    );
    if (!_typed) return unavailable;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        unavailable,
        const SizedBox(height: 8),
        OutlinedButton.icon(
          key: const Key('qr_retry_gps'),
          onPressed: _captureGps,
          icon: const Icon(Icons.my_location, size: 18),
          label: Text(l10n.qrRetryLocation),
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

/// Keeps only the code's digits as they are typed (ASCII, or Arabic-Indic
/// and Persian digits mapped to them), at most nine.
class _HandoffCodeFormatter extends TextInputFormatter {
  const _HandoffCodeFormatter();

  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) {
    var digits = HandoffCode.normalize(newValue.text);
    if (digits.length > HandoffCode.length) {
      digits = digits.substring(0, HandoffCode.length);
    }
    return TextEditingValue(
      text: digits,
      selection: TextSelection.collapsed(offset: digits.length),
    );
  }
}

/// The camera could not start (T-1): why, how to allow it, Try again, and
/// the typed code. On the scan panel's dark ground.
class QrCameraErrorView extends StatelessWidget {
  const QrCameraErrorView({
    required this.permissionDenied,
    required this.onRetry,
    required this.onTypeCode,
    this.onOpenSettings,
    super.key,
  });

  /// The user (or the browser) refused the camera, rather than a camera
  /// that is missing or busy.
  final bool permissionDenied;
  final VoidCallback onRetry;
  final VoidCallback onTypeCode;

  /// Opens the app's page in the phone's settings; null on web.
  final VoidCallback? onOpenSettings;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final title = permissionDenied
        ? l10n.qrCameraDeniedTitle
        : l10n.qrCameraUnavailableTitle;
    final text = permissionDenied
        ? (onOpenSettings == null
            ? l10n.qrCameraDeniedTextWeb
            : l10n.qrCameraDeniedText)
        : l10n.qrCameraUnavailableText;
    final buttonStyle = OutlinedButton.styleFrom(
      backgroundColor: Colors.transparent,
      foregroundColor: Colors.white,
      side: const BorderSide(color: Color(0xFF4A5A52)),
      minimumSize: const Size.fromHeight(48),
    );

    return ColoredBox(
      key: const Key('qr_camera_error'),
      color: AppTheme.soilCard,
      child: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Icon(
                permissionDenied ? Icons.no_photography : Icons.videocam_off,
                size: 40,
                color: Colors.white,
              ),
              const SizedBox(height: 12),
              Text(
                title,
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 17,
                  fontWeight: FontWeight.w800,
                  color: Colors.white,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                text,
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 14,
                  height: 1.45,
                  color: AppTheme.soilMuted,
                ),
              ),
              const SizedBox(height: 16),
              if (permissionDenied && onOpenSettings != null) ...[
                FilledButton.icon(
                  key: const Key('qr_camera_settings'),
                  onPressed: onOpenSettings,
                  icon: const Icon(Icons.settings, size: 18),
                  label: Text(l10n.qrCameraOpenSettings),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                  ),
                ),
                const SizedBox(height: 8),
              ],
              OutlinedButton.icon(
                key: const Key('qr_camera_retry'),
                onPressed: onRetry,
                icon: const Icon(Icons.refresh, size: 18),
                label: Text(l10n.deliveryTryAgain),
                style: buttonStyle,
              ),
              const SizedBox(height: 8),
              OutlinedButton.icon(
                key: const Key('qr_camera_type_code'),
                onPressed: onTypeCode,
                icon: const Icon(Icons.dialpad, size: 18),
                label: Text(l10n.qrTypeCodeInstead),
                style: buttonStyle,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
