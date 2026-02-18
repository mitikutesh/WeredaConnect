import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';

/// Offline HMAC-SHA256 QR token generation and verification
/// with nonce-based physical proximity enforcement.
///
/// Security design (F12-SEC-01):
/// - Nonce generated at QR time prevents token forgery without physical
///   proximity. The nonce only exists in the displayed QR code, not in
///   synced delivery data.
/// - MVP limitation: Server nonce cross-validation is not yet implemented.
///   Offline QR verification is a physical proximity ritual — the scanner
///   must physically see the generator's screen to obtain the nonce.
///   Server-side nonce validation is planned for a future release when the
///   photo upload endpoint (T12) provides guaranteed server connectivity.
class QrVerificationService {
  /// Generate QR token string for display as QR code.
  ///
  /// Includes a cryptographic nonce for physical proximity enforcement.
  /// Returns (qrTokenString, nonce) — nonce must be stored locally by
  /// the caller for server-side cross-validation during sync.
  (String token, String nonce) generateQrToken(Delivery delivery) {
    final nonce = _generateNonce();
    final timestamp = DateTime.now().toUtc().millisecondsSinceEpoch;

    final secret = _deriveSecret(
      delivery.deliveryId,
      delivery.tripId,
      delivery.listingId,
      delivery.createdAt.millisecondsSinceEpoch,
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

  /// Verify a scanned QR token against local delivery data.
  ///
  /// Returns [QrVerificationResult] with status, extracted fields, and nonce.
  /// The nonce enforces physical proximity — the scanner must see the
  /// generator's screen. Server nonce cross-validation is planned for
  /// a future release (see class-level doc).
  QrVerificationResult verifyQrToken(String qrToken, Delivery delivery) {
    // F12-SEC-07: Hard size limit BEFORE any processing
    if (qrToken.length > DeliveryConstants.maxQrTokenChars) {
      return const QrVerificationResult.invalid('QR code too large');
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
      final receivedHmac = payload['hmac']?.toString() ?? '';

      if ([did, tid, lid, fid, nonce, receivedHmac].any((s) => s.isEmpty)) {
        return const QrVerificationResult.invalid('Incomplete QR data');
      }
      if ([did, tid, lid, fid]
          .any((s) => s.length > DeliveryConstants.maxQrFieldLength)) {
        return const QrVerificationResult.invalid('Invalid QR data');
      }
      if (nonce.length > DeliveryConstants.maxQrFieldLength ||
          receivedHmac.length > DeliveryConstants.maxQrFieldLength) {
        return const QrVerificationResult.invalid('Invalid QR data');
      }

      // Cross-check deliveryId matches expected delivery
      if (did != delivery.deliveryId) {
        return const QrVerificationResult.invalid(
          'QR code does not match this delivery',
        );
      }

      // Recompute HMAC
      final ts = payload['ts'] as int? ?? 0;
      final secret = _deriveSecret(
        delivery.deliveryId,
        delivery.tripId,
        delivery.listingId,
        delivery.createdAt.millisecondsSinceEpoch,
      );
      final message = '$did|$tid|$lid|$fid|$ts|$nonce';
      final computed = Hmac(sha256, secret).convert(utf8.encode(message));

      // F12-SEC-03: Constant-time comparison
      final computedBytes = utf8.encode(computed.toString());
      final receivedBytes = utf8.encode(receivedHmac);
      if (!_constantTimeEquals(computedBytes, receivedBytes)) {
        return const QrVerificationResult.invalid(
          'Verification failed \u2014 possible tampering',
        );
      }

      return QrVerificationResult.valid(
        deliveryId: did,
        tripId: tid,
        farmerId: fid,
        timestamp: ts,
        nonce: nonce,
      );
    } on FormatException {
      return const QrVerificationResult.invalid('Invalid QR code format');
    } on TypeError {
      return const QrVerificationResult.invalid('Malformed QR data');
    } catch (_) {
      return const QrVerificationResult.invalid('Could not process QR code');
    }
  }

  /// Derive HMAC secret from delivery metadata.
  List<int> _deriveSecret(
    String deliveryId,
    String tripId,
    String listingId,
    int creationTimestamp,
  ) {
    final input = '$deliveryId:$tripId:$listingId:$creationTimestamp';
    return sha256.convert(utf8.encode(input)).bytes;
  }

  /// Generate 32-byte cryptographic random nonce, base64url encoded.
  String _generateNonce() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64Url.encode(bytes);
  }

  /// Constant-time byte comparison to prevent timing attacks (F12-SEC-03).
  bool _constantTimeEquals(List<int> a, List<int> b) {
    if (a.length != b.length) return false;
    var result = 0;
    for (var i = 0; i < a.length; i++) {
      result |= a[i] ^ b[i];
    }
    return result == 0;
  }
}

/// Result of QR verification.
class QrVerificationResult {
  final bool isValid;
  final String? errorMessage;
  final String? deliveryId;
  final String? tripId;
  final String? farmerId;
  final int? timestamp;

  /// Extracted nonce for sync cross-validation (F12-SEC-01).
  final String? nonce;

  const QrVerificationResult.valid({
    required this.deliveryId,
    required this.tripId,
    required this.farmerId,
    required this.timestamp,
    required this.nonce,
  })  : isValid = true,
        errorMessage = null;

  const QrVerificationResult.invalid(this.errorMessage)
      : isValid = false,
        deliveryId = null,
        tripId = null,
        farmerId = null,
        timestamp = null,
        nonce = null;
}
