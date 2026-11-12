import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_qr_issuer.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Display QR code for farmers to show to truckers, or to the buyer on a
/// self-pickup delivery (a sibling of the detail boards: the code in a
/// white card, the delivery under it, the offline note, Regenerate at the
/// foot).
///
/// Generates an HMAC-SHA256 QR token offline with a device-local key
/// and cryptographic nonce. No internet required.
///
/// "Camera not working?" reveals the nine-digit code of the same nonce
/// (T-1) with the warning to show it only to the person there. The
/// [QrGenerateScreen.dropoff] variant is the trucker's drop-off QR: the
/// trucker's own code kept since the pickup (ESC-35), sent to the server
/// again on open (never replacing a code kept there). None is made on open:
/// with none kept the screen says so, and New drop-off code, after the
/// trucker confirmed that any other code stops working, makes one that
/// replaces a lost, locked or other phone's code. A farmer's token an older
/// version kept at pickup (T-3) is shown as it is until then.
class QrGenerateScreen extends StatefulWidget {
  final Delivery delivery;
  final QrVerificationService? qrService;
  final BaseDeliveryRepository? repository;
  final String userId;

  /// The trucker's kept drop-off handoff; null on the farmer's QR, and on
  /// the drop-off QR when this phone keeps none.
  final KeptHandoff? dropoff;

  /// Makes and registers the trucker's drop-off code; null on the farmer's
  /// QR.
  final DropoffQrIssuer? issuer;

  const QrGenerateScreen({
    required this.delivery,
    required QrVerificationService this.qrService,
    required BaseDeliveryRepository this.repository,
    required this.userId,
    super.key,
  })  : dropoff = null,
        issuer = null;

  /// The trucker's drop-off QR for the buyer.
  const QrGenerateScreen.dropoff({
    required this.delivery,
    required this.userId,
    required this.dropoff,
    required DropoffQrIssuer this.issuer,
    super.key,
  })  : qrService = null,
        repository = null;

  @override
  State<QrGenerateScreen> createState() => _QrGenerateScreenState();
}

class _QrGenerateScreenState extends State<QrGenerateScreen> {
  String? _token;
  String? _code;
  bool _isGenerating = true;
  bool _showCode = false;

  /// The delivery left pending meanwhile (picked up, maybe synced from the
  /// trucker's phone first): its nonce is the drop-off's secret now, so the
  /// farmer's screen makes no new one (the server ignores one anyway).
  bool _pastPickup = false;

  /// The drop-off code shown is the trucker's own (ESC-35), not a farmer's
  /// token kept by an older version.
  bool _ownDropoff = false;

  /// What the server made of the trucker's drop-off code shown (web knows
  /// at once; a phone's queue reads as sent).
  DropoffCodeRegistration? _registration;

  bool get _isDropoff => widget.issuer != null;

  @override
  void initState() {
    super.initState();
    if (_isDropoff) {
      _isGenerating = false;
      final dropoff = widget.dropoff;
      if (dropoff == null) return;
      _showDropoff(dropoff);
      if (dropoff.isTruckers) _registerDropoff(dropoff);
      return;
    }
    _generateToken();
  }

  void _showDropoff(KeptHandoff dropoff) {
    _token = dropoff.token;
    _code = dropoff.code;
    _ownDropoff = dropoff.isTruckers;
    // Without a QR the code is all there is to show; a code already shown
    // stays shown for a new one.
    if (dropoff.token == null) _showCode = true;
  }

  /// Sends the kept code to the server again; the same nonce changes
  /// nothing there, one that did not get through from web gets there, and a
  /// code made since on another phone stays (this screen then says so).
  Future<void> _registerDropoff(KeptHandoff dropoff) async {
    final registration =
        await widget.issuer!.register(widget.delivery, dropoff);
    if (mounted) setState(() => _registration = registration);
  }

  /// New drop-off code: after the trucker confirmed that any code shown
  /// before stops working, a new one replaces whatever the server keeps.
  Future<void> _renewDropoff() async {
    final l10n = AppLocalizations.of(context)!;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.deliveryDropoffNewCodeConfirmTitle),
        content: Text(l10n.deliveryDropoffNewCodeConfirmBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.cancel),
          ),
          TextButton(
            key: const Key('dropoff_new_code_confirm'),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(l10n.deliveryDropoffNewCode),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    setState(() => _isGenerating = true);
    try {
      final (kept, registration) =
          await widget.issuer!.issue(widget.delivery, renew: true);
      if (mounted) {
        setState(() {
          _showDropoff(kept);
          _registration = registration;
          _isGenerating = false;
        });
      }
    } on Object catch (_) {
      if (mounted) {
        setState(() => _isGenerating = false);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppLocalizations.of(context)!.genericError)),
        );
      }
    }
  }

  /// The freshest status the phone can get (the local copy on mobile, the
  /// API on web), else the one the screen was opened with.
  Future<bool> _leftPending() async {
    var status = widget.delivery.status;
    try {
      final latest = await widget.repository!.getDeliveryById(
        widget.delivery.deliveryId,
        userId: widget.userId,
      );
      if (latest != null) status = latest.status;
    } on Object catch (_) {
      // Offline on web: the copy the screen was opened with decides.
    }
    return status != DeliveryStatus.pending;
  }

  Future<void> _generateToken() async {
    setState(() => _isGenerating = true);

    if (await _leftPending()) {
      if (mounted) {
        setState(() {
          _pastPickup = true;
          _token = null;
          _code = null;
          _isGenerating = false;
        });
      }
      return;
    }

    try {
      final (token, nonce) =
          await widget.qrService!.generateQrToken(widget.delivery);

      await widget.repository!.persistQrNonce(
        deliveryId: widget.delivery.deliveryId,
        qrNonce: nonce,
        userId: widget.userId,
      );

      if (mounted) {
        setState(() {
          _token = token;
          _code = HandoffCode.derive(
            nonce: nonce,
            deliveryId: widget.delivery.deliveryId,
          );
          _isGenerating = false;
        });
      }
    } on Object catch (_) {
      if (mounted) {
        setState(() => _isGenerating = false);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppLocalizations.of(context)!.genericError)),
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd();
    final delivery = widget.delivery;

    // Self-pickup: the buyer scans it at the farm, there is no trucker.
    final instruction = _isDropoff
        ? l10n.deliveryDropoffQrInstruction
        : delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType
            ? l10n.deliveryQrShowInstructionBuyer
            : l10n.deliveryQrShowInstruction;
    final code = _code;

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: SingleChildScrollView(
          child: FwContentWidth(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                FwDetailHeader(
                  title: _isDropoff
                      ? l10n.deliveryDropoffQrTitle
                      : l10n.deliveryShowQrCode,
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Text(
                        instruction,
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w600,
                          height: 1.4,
                          color: AppTheme.soilDark,
                        ),
                      ),
                      const SizedBox(height: 20),
                      // The code itself, in a white card (none when the
                      // pickup was confirmed with a typed code, or already
                      // happened).
                      if (_pastPickup)
                        FwNoteBox(
                          icon: Icons.check_circle_outline,
                          child: Text(l10n.deliveryQrAlreadyPickedUp),
                        )
                      else if (_isDropoff && !_isGenerating && _token == null)
                        // A code typed at pickup on an older version; or
                        // none kept on this phone (New drop-off code).
                        code != null
                            ? FwNoteBox(
                                icon: Icons.dialpad,
                                child: Text(l10n.deliveryDropoffCodeOnly),
                              )
                            : FwNoteBox(
                                key: const Key('dropoff_none_here'),
                                icon: Icons.qr_code_2,
                                child: Text(l10n.deliveryDropoffNoCodeHere),
                              )
                      else
                        Center(
                          child: Container(
                            padding: const EdgeInsets.all(16),
                            decoration: BoxDecoration(
                              color: AppTheme.surfaceWhite,
                              borderRadius: BorderRadius.circular(24),
                              border: Border.all(color: AppTheme.whisperBorder),
                            ),
                            child: _isGenerating || _token == null
                                ? const SizedBox(
                                    width: 280,
                                    height: 280,
                                    child: Center(
                                      child: CircularProgressIndicator(),
                                    ),
                                  )
                                : QrImageView(
                                    data: _token!,
                                    size: 280,
                                    errorCorrectionLevel: QrErrorCorrectLevel.M,
                                  ),
                          ),
                        ),
                      if (code != null) ...[
                        const SizedBox(height: 12),
                        if (_showCode)
                          _TypedCodeCard(code: code)
                        else
                          TextButton.icon(
                            key: const Key('qr_show_code'),
                            onPressed: () => setState(() => _showCode = true),
                            icon: const Icon(Icons.dialpad, size: 18),
                            label: Text(l10n.qrCameraNotWorking),
                          ),
                      ],
                      const SizedBox(height: 20),
                      // Which delivery the code belongs to.
                      FwCard(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              l10n.deliveryIdLabel(
                                delivery.deliveryId.substring(0, 8),
                              ),
                              style: const TextStyle(
                                fontSize: 15,
                                fontWeight: FontWeight.w800,
                                color: AppTheme.soilDark,
                              ),
                            ),
                            const SizedBox(height: 4),
                            Text(
                              partyDisplayName(delivery.farmerName, l10n),
                              style: const TextStyle(
                                fontSize: 14,
                                fontWeight: FontWeight.w600,
                                color: AppTheme.unselectedNav,
                              ),
                            ),
                            const SizedBox(height: 2),
                            Text(
                              dateFormat
                                  .format(dayInEthiopia(delivery.createdAt)),
                              style: const TextStyle(
                                fontSize: 12,
                                color: AppTheme.mutedSteel,
                              ),
                            ),
                          ],
                        ),
                      ),
                      // No note about a code while none is shown.
                      if (!_isDropoff || _token != null || code != null) ...[
                        const SizedBox(height: 12),
                        FwNoteBox(
                          icon: Icons.qr_code_2,
                          child: Text(
                            !_isDropoff
                                ? l10n.deliveryQrOfflineNote
                                : _ownDropoff
                                    ? l10n.deliveryDropoffQrOwnNote
                                    : l10n.deliveryDropoffQrNote,
                          ),
                        ),
                      ],
                      if (_isDropoff &&
                          _registration ==
                              DropoffCodeRegistration.superseded) ...[
                        const SizedBox(height: 12),
                        FwNoteBox(
                          key: const Key('dropoff_superseded'),
                          icon: Icons.warning_amber,
                          child: Text(l10n.deliveryDropoffQrSuperseded),
                        ),
                      ] else if (_isDropoff &&
                          (_registration == DropoffCodeRegistration.failed ||
                              _registration ==
                                  DropoffCodeRegistration.notStored)) ...[
                        const SizedBox(height: 12),
                        FwNoteBox(
                          key: const Key('dropoff_not_sent'),
                          icon: Icons.cloud_off,
                          child: Text(l10n.deliveryDropoffQrNotSent),
                        ),
                      ],
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
      // The farmer's QR after pickup has nothing to regenerate. The trucker
      // makes a new drop-off code here (ESC-35), which the server takes
      // until the buyer confirmed.
      bottomNavigationBar: _pastPickup
          ? null
          : FwBottomActions(
              children: [
                OutlinedButton.icon(
                  key: _isDropoff ? const Key('dropoff_new_code') : null,
                  onPressed: _isGenerating
                      ? null
                      : _isDropoff
                          ? _renewDropoff
                          : _generateToken,
                  icon: const Icon(Icons.refresh),
                  label: Text(
                    _isDropoff
                        ? l10n.deliveryDropoffNewCode
                        : l10n.deliveryRegenerateQr,
                  ),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size.fromHeight(52),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
              ],
            ),
    );
  }
}

/// The typed code of the shown QR (T-1), large, with the warning that it
/// is for the person standing there only.
class _TypedCodeCard extends StatelessWidget {
  const _TypedCodeCard({required this.code});

  final String code;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FwCard(
      key: const Key('qr_typed_code'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            l10n.qrShowCodeTitle,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: AppTheme.mutedSteel,
            ),
          ),
          const SizedBox(height: 6),
          SelectionContainer.disabled(
            child: Text(
              HandoffCode.format(code),
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 32,
                fontWeight: FontWeight.w800,
                letterSpacing: 2,
                color: AppTheme.soilDark,
                fontFeatures: [FontFeature.tabularFigures()],
              ),
            ),
          ),
          const SizedBox(height: 10),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Padding(
                padding: EdgeInsets.only(top: 1),
                child: Icon(
                  Icons.phone_disabled,
                  size: 18,
                  color: AppTheme.statusError,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  l10n.qrShowCodeWarning,
                  style: const TextStyle(
                    fontSize: 13,
                    height: 1.45,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.statusError,
                  ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
