import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
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
/// [QrGenerateScreen.dropoff] variant is the trucker's drop-off QR (T-3): it
/// shows the farmer's token kept from the pickup scan (or only the code
/// typed at pickup) as it is, generating and sending nothing.
class QrGenerateScreen extends StatefulWidget {
  final Delivery delivery;
  final QrVerificationService? qrService;
  final BaseDeliveryRepository? repository;
  final String userId;

  /// The trucker's kept pickup handoff; null on the farmer's QR.
  final KeptHandoff? dropoff;

  const QrGenerateScreen({
    required this.delivery,
    required QrVerificationService this.qrService,
    required BaseDeliveryRepository this.repository,
    required this.userId,
    super.key,
  }) : dropoff = null;

  /// The trucker's drop-off QR for the buyer.
  const QrGenerateScreen.dropoff({
    required this.delivery,
    required this.userId,
    required KeptHandoff this.dropoff,
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

  bool get _isDropoff => widget.dropoff != null;

  @override
  void initState() {
    super.initState();
    final dropoff = widget.dropoff;
    if (dropoff != null) {
      _token = dropoff.token;
      _code = dropoff.code;
      _isGenerating = false;
      // Without a QR the code is all there is to show.
      _showCode = dropoff.token == null;
      return;
    }
    _generateToken();
  }

  Future<void> _generateToken() async {
    setState(() => _isGenerating = true);

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
                      // pickup was confirmed with a typed code).
                      if (_isDropoff && _token == null)
                        FwNoteBox(
                          icon: Icons.dialpad,
                          child: Text(l10n.deliveryDropoffCodeOnly),
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
                      const SizedBox(height: 12),
                      FwNoteBox(
                        icon: Icons.qr_code_2,
                        child: Text(
                          _isDropoff
                              ? l10n.deliveryDropoffQrNote
                              : l10n.deliveryQrOfflineNote,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
      // The drop-off QR is the farmer's: nothing to regenerate here.
      bottomNavigationBar: _isDropoff
          ? null
          : FwBottomActions(
              children: [
                OutlinedButton.icon(
                  onPressed: _isGenerating ? null : _generateToken,
                  icon: const Icon(Icons.refresh),
                  label: Text(l10n.deliveryRegenerateQr),
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
