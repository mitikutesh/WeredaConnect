/// Delivery verification constants.
///
/// All magic numbers for F-12 Delivery Verification are defined here
/// to ensure consistency between services, repository, and presentation.
abstract final class DeliveryConstants {
  /// Maximum QR token size in characters (base64url encoded).
  /// 500 bytes payload base64-encoded is ~670 chars; 700 gives margin.
  static const int maxQrTokenChars = 700;

  /// Maximum QR token size in bytes.
  static const int maxQrTokenBytes = 500;

  /// Maximum length of individual QR payload fields (IDs, nonce, hmac).
  static const int maxQrFieldLength = 100;

  /// GPS proximity threshold in km.
  static const double gpsProximityThresholdKm = 5.0;

  /// GPS capture timeout.
  static const Duration gpsCaptureTimeout = Duration(seconds: 15);

  /// Ethiopian latitude bounds (southern tip).
  static const double minLatitude = 3.0;

  /// Ethiopian latitude bounds (northern tip).
  static const double maxLatitude = 15.0;

  /// Ethiopian longitude bounds (western border).
  static const double minLongitude = 33.0;

  /// Ethiopian longitude bounds (eastern tip).
  static const double maxLongitude = 48.0;

  /// Photo max file size in bytes (200KB).
  static const int maxPhotoSizeBytes = 200 * 1024;

  /// Photo max width in pixels.
  static const int maxPhotoWidth = 800;

  /// Photo min height in pixels.
  static const int minPhotoHeight = 600;

  /// Photo WebP compression quality (0-100).
  static const int photoQuality = 75;

  /// Photo fallback quality if initial compression exceeds size limit.
  static const int photoFallbackQuality = 60;

  /// Dispute window duration from delivery confirmation.
  static const Duration disputeWindow = Duration(hours: 48);

  /// Maximum dispute reason length.
  static const int maxDisputeReasonLength = 500;

  /// QR error correction level.
  /// M = 15% error tolerance (good balance of size vs readability).
  /// Maps to QrErrorCorrectLevel.M in qr_flutter.
  static const int qrErrorCorrectionLevel = 0;

  /// UUID v4 validation pattern (lowercase hex).
  static final RegExp uuidRegex = RegExp(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
  );
}
