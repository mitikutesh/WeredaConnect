import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';

/// Offline HMAC-SHA256 QR token generation and structural parsing
/// with device-local key + server nonce cross-validation.
///
/// Security design (F12-SEC-01 fix):
/// - Device-local 32-byte random key stored in FlutterSecureStorage is
///   included in HMAC derivation. Other participants cannot forge tokens.
/// - Scanner performs structural validation only (no HMAC recomputation).
/// - Physical proximity is enforced by optical QR scan + server-side
///   nonce cross-validation.
class QrVerificationService {
  final DeviceSigningKeyService _deviceKeyService;

  QrVerificationService({required DeviceSigningKeyService deviceKeyService})
      : _deviceKeyService = deviceKeyService;

  /// Generate QR token string for display as QR code.
  ///
  /// Includes a cryptographic nonce for physical proximity enforcement
  /// and a device-local key in the HMAC derivation.
  /// Returns (qrTokenString, nonce) — nonce must be persisted for
  /// server-side cross-validation during sync.
  Future<(String token, String nonce)> generateQrToken(
    Delivery delivery,
  ) async {
    final nonce = _generateNonce();
    final timestamp = DateTime.now().toUtc().millisecondsSinceEpoch;

    final deviceKey = await _deviceKeyService.getKey();
    final secret = _deriveSecret(
      delivery.deliveryId,
      delivery.tripId,
      delivery.listingId,
      delivery.createdAt.millisecondsSinceEpoch,
      deviceKey,
    );

    final message =
        '${delivery.deliveryId}|${delivery.tripId}|${delivery.listingId}|${delivery.farmerId}|$timestamp|$nonce';
    final hmacResult = Hmac(sha256, secret).convert(utf8.encode(message));

    final payload = <String, dynamic>{
      'did': delivery.deliveryId,
      'tid': delivery.tripId,
      'lid': delivery.listingId,
      'fid': delivery.farmerId,
      'ts': timestamp,
      'n': nonce,
      'hmac': hmacResult.toString(),
    };

    final token = base64Url.encode(utf8.encode(jsonEncode(payload)));
    return (token, nonce);
  }

  /// Parse a scanned QR token — structural validation only.
  ///
  /// Returns [QrParseResult] with extracted fields and nonce.
  /// Does NOT recompute HMAC (scanner doesn't have generator's device key).
  /// Physical proximity is enforced by optical scan + server nonce matching.
  QrParseResult parseQrToken(String qrToken, Delivery delivery) {
    // F12-SEC-07: Hard size limit BEFORE any processing
    if (qrToken.length > DeliveryConstants.maxQrTokenChars) {
      return const QrParseResult.invalid('QR code too large');
    }

    // F12-SEC-07: Wrap all parsing in try-catch
    try {
      final decoded =
          utf8.decode(base64Url.decode(base64Url.normalize(qrToken)));
      final payload = jsonDecode(decoded) as Map<String, dynamic>;

      // F12-SEC-07: Validate field presence and string length bounds
      final did = payload['did']?.toString() ?? '';
      final tid = payload['tid']?.toString() ?? '';
      final lid = payload['lid']?.toString() ?? '';
      final fid = payload['fid']?.toString() ?? '';
      final nonce = payload['n']?.toString() ?? '';
      final hmac = payload['hmac']?.toString() ?? '';

      if ([did, tid, lid, fid, nonce, hmac].any((s) => s.isEmpty)) {
        return const QrParseResult.invalid('Incomplete QR data');
      }
      if ([did, tid, lid, fid]
          .any((s) => s.length > DeliveryConstants.maxQrFieldLength)) {
        return const QrParseResult.invalid('Invalid QR data');
      }
      if (nonce.length > DeliveryConstants.maxQrFieldLength ||
          hmac.length > DeliveryConstants.maxQrFieldLength) {
        return const QrParseResult.invalid('Invalid QR data');
      }

      // Cross-check deliveryId matches expected delivery
      if (did != delivery.deliveryId) {
        return const QrParseResult.invalid(
          'QR code does not match this delivery',
        );
      }

      final ts = payload['ts'] is int ? payload['ts'] as int : 0;

      return QrParseResult.valid(
        deliveryId: did,
        tripId: tid,
        farmerId: fid,
        timestamp: ts,
        nonce: nonce,
      );
    } on FormatException {
      return const QrParseResult.invalid('Invalid QR code format');
    } on Exception catch (_) {
      return const QrParseResult.invalid('Could not process QR code');
    }
  }

  /// Derive HMAC secret from delivery metadata + device-local key.
  List<int> _deriveSecret(
    String deliveryId,
    String tripId,
    String listingId,
    int creationTimestamp,
    String deviceKey,
  ) {
    final input =
        '$deliveryId:$tripId:$listingId:$creationTimestamp:$deviceKey';
    return sha256.convert(utf8.encode(input)).bytes;
  }

  /// Generate 32-byte cryptographic random nonce, base64url encoded.
  String _generateNonce() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64Url.encode(bytes);
  }
}

/// Result of QR structural parsing.
class QrParseResult {
  final bool isValid;
  final String? errorMessage;
  final String? deliveryId;
  final String? tripId;
  final String? farmerId;
  final int? timestamp;

  /// Extracted nonce for sync cross-validation.
  final String? nonce;

  const QrParseResult.valid({
    required this.deliveryId,
    required this.tripId,
    required this.farmerId,
    required this.timestamp,
    required this.nonce,
  })  : isValid = true,
        errorMessage = null;

  const QrParseResult.invalid(this.errorMessage)
      : isValid = false,
        deliveryId = null,
        tripId = null,
        farmerId = null,
        timestamp = null,
        nonce = null;
}
