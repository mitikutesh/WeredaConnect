import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Display QR code for farmers to show to truckers, or to the buyer on a
/// self-pickup delivery.
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
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd();
    final delivery = widget.delivery;

    // Self-pickup: the buyer scans it at the farm, there is no trucker.
    final instruction =
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType
            ? l10n.deliveryQrShowInstructionBuyer
            : l10n.deliveryQrShowInstruction;

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.deliveryShowQrCode),
        elevation: 0,
      ),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            children: [
              const SizedBox(height: 16),
              // Instruction
              Text(
                instruction,
                style: theme.textTheme.titleMedium,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 24),
              // QR code or loading indicator
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.circular(16),
                  boxShadow: [
                    BoxShadow(
                      color: Colors.black.withValues(alpha: 0.1),
                      blurRadius: 8,
                      offset: const Offset(0, 2),
                    ),
                  ],
                ),
                child: _isGenerating || _token == null
                    ? const SizedBox(
                        width: 280,
                        height: 280,
                        child: Center(child: CircularProgressIndicator()),
                      )
                    : QrImageView(
                        data: _token!,
                        size: 280,
                        errorCorrectionLevel: QrErrorCorrectLevel.M,
                      ),
              ),
              const SizedBox(height: 24),
              // Delivery info
              Text(
                l10n.deliveryIdLabel(delivery.deliveryId.substring(0, 8)),
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                delivery.farmerName,
                style: theme.textTheme.bodyMedium,
              ),
              const SizedBox(height: 4),
              Text(
                dateFormat.format(delivery.createdAt),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
              const SizedBox(height: 24),
              // Regenerate button
              OutlinedButton.icon(
                onPressed: _isGenerating ? null : _generateToken,
                icon: const Icon(Icons.refresh),
                label: Text(l10n.deliveryRegenerateQr),
              ),
              const SizedBox(height: 16),
              // Info text
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color:
                      theme.colorScheme.primaryContainer.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Row(
                  children: [
                    Icon(
                      Icons.info_outline,
                      size: 20,
                      color: theme.colorScheme.primary,
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        l10n.deliveryQrOfflineNote,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.primary,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
