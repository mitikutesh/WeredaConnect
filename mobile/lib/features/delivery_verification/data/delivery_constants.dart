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

  /// The last try when the fallback is still over the size limit (FRD-12):
  /// a smaller photo at a lower quality. Over the limit even then, the photo
  /// is dropped.
  static const int photoLastQuality = 45;

  /// Width of the last try.
  static const int photoLastWidth = 640;

  /// Height of the last try.
  static const int photoLastHeight = 480;

  /// Dispute window duration from delivery confirmation.
  static const Duration disputeWindow = Duration(hours: 48);

  /// Delivery type marking a buyer self-pickup (no trucker). Must match the
  /// server's `SelfPickupDeliveryType` value.
  static const String selfPickupDeliveryType = 'self_pickup';

  /// The tag the trucker's drop-off nonce starts with (ESC-35). A farmer's
  /// nonce is plain base64url, which never holds a dot, so the server can
  /// tell a drop-off QR whose nonce has not synced yet from a wrong one.
  /// Must match the server's `DropoffNoncePrefix`.
  static const String dropoffNoncePrefix = 'd.';

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

  /// Shape of a photo key issued by `POST /deliveries/{id}/photos`:
  /// `uploads/photos/{deliveryId}/{pickup|dropoff|arrival}-{ms}.{webp|jpg}`
  /// (the phone sends WebP, the browser JPEG; FRD-12; the trucker's arrival
  /// photo is ESC-36's). Mirrors the server's `DeliveryPhotoKeys`: a
  /// `delivery_photo` attach with any other key is refused, so a local file
  /// path must never be sent.
  static final RegExp serverPhotoRefRegex = RegExp(
    r'^uploads/photos/[a-zA-Z0-9\-]+/(pickup|dropoff|arrival)-\d+\.(webp|jpg)$',
  );

  /// True when [ref] is a server upload key that belongs to [deliveryId].
  static bool isServerPhotoRef(String ref, String deliveryId) =>
      serverPhotoRefRegex.hasMatch(ref) &&
      ref.startsWith('uploads/photos/$deliveryId/');

  /// True when [ref] is a photo file this phone took for [deliveryId]: an
  /// absolute path inside its `photos/{deliveryId}/` directory (the
  /// `PhotoManager` layout). The sync queue uploads such a file (FRD-12).
  static bool isLocalPhotoPath(String ref, String deliveryId) =>
      ref.startsWith('/') &&
      !ref.contains('..') &&
      ref.contains('/photos/$deliveryId/');
}
