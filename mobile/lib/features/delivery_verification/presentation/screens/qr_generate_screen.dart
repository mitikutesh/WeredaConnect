import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
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
class QrGenerateScreen extends StatefulWidget {
  final Delivery delivery;
  final QrVerificationService qrService;
  final BaseDeliveryRepository repository;
  final String userId;

  const QrGenerateScreen({
    required this.delivery,
    required this.qrService,
    required this.repository,
    required this.userId,
    super.key,
  });

  @override
  State<QrGenerateScreen> createState() => _QrGenerateScreenState();
}

class _QrGenerateScreenState extends State<QrGenerateScreen> {
  String? _token;
  bool _isGenerating = true;

  @override
  void initState() {
    super.initState();
    _generateToken();
  }

  Future<void> _generateToken() async {
    setState(() => _isGenerating = true);

    try {
      final (token, nonce) =
          await widget.qrService.generateQrToken(widget.delivery);

      await widget.repository.persistQrNonce(
        deliveryId: widget.delivery.deliveryId,
        qrNonce: nonce,
        userId: widget.userId,
      );

      if (mounted) {
        setState(() {
          _token = token;
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
    final instruction =
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType
            ? l10n.deliveryQrShowInstructionBuyer
            : l10n.deliveryQrShowInstruction;

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: SingleChildScrollView(
          child: FwContentWidth(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                FwDetailHeader(title: l10n.deliveryShowQrCode),
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
                      // The code itself, in a white card.
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
                        child: Text(l10n.deliveryQrOfflineNote),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
      bottomNavigationBar: FwBottomActions(
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
