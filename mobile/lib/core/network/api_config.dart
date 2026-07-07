/// API configuration for WeredaConnect backend.
///
/// This centralizes all API-related configuration:
/// - Base URL (environment-specific)
/// - Timeout settings (optimized for Ethiopian 3G)
/// - Endpoint paths
///
/// Ethiopian context:
/// - 30s timeouts account for slow rural connectivity
/// - Base URL must be provided via --dart-define (no hardcoded default)
class ApiConfig {
  /// API Gateway base URL.
  ///
  /// Can be overridden at build time:
  /// ```bash
  /// flutter run --dart-define=API_BASE_URL=https://dev.weredaconnect.et
  /// ```
  ///
  /// SECURITY: No default value — must be provided via --dart-define to prevent
  /// hardcoded URLs from leaking into source control.
  static const String baseUrl = String.fromEnvironment('API_BASE_URL');

  /// Connection timeout for initial TCP connection.
  ///
  /// 30s is reasonable for 3G networks in rural Ethiopia.
  static const Duration connectionTimeout = Duration(seconds: 30);

  /// Receive timeout for response data.
  ///
  /// 30s allows for slow data transfer on 3G.
  static const Duration receiveTimeout = Duration(seconds: 30);

  // ============================================================================
  // Authentication endpoints
  // ============================================================================

  /// POST /auth/register - Register new user
  static const String registerEndpoint = '/auth/register';

  /// POST /auth/fayda-verify - Exchange Fayda auth code for verification token
  static const String faydaVerifyEndpoint = '/auth/fayda-verify';

  /// POST /auth/login - Login with phone/password
  static const String loginEndpoint = '/auth/login';

  /// POST /auth/refresh - Refresh access token
  static const String refreshEndpoint = '/auth/refresh';

  /// POST /auth/logout - Logout and invalidate tokens
  static const String logoutEndpoint = '/auth/logout';

  /// POST /auth/forgot-password — send verification code for password reset
  static const String forgotPasswordEndpoint = '/auth/forgot-password';

  /// POST /auth/reset-password — reset password with verification code
  static const String resetPasswordEndpoint = '/auth/reset-password';

  // ============================================================================
  // Future endpoints (to be added in later sprints)
  // ============================================================================

  // Crop listings
  // static const String listCropsEndpoint = '/crops';
  // static const String createCropEndpoint = '/crops';

  // Transport requests
  // static const String listTransportsEndpoint = '/transports';
  // static const String createTransportEndpoint = '/transports';

  // ============================================================================
  // Escrow payment endpoints (F-13)
  // ============================================================================

  /// POST /escrow/initiate - Buyer initiates escrow payment
  static const String escrowInitiate = '/escrow/initiate';

  /// POST /escrow/dispute - Raise dispute on escrowed payment
  static const String escrowDispute = '/escrow/dispute';

  /// GET /escrow/{id} - Get escrow status (append /{id})
  static const String escrowStatus = '/escrow';

  /// GET /escrow/by-delivery/{deliveryId} - Get the escrow for a delivery (append /{deliveryId})
  static const String escrowByDelivery = '/escrow/by-delivery';

  /// GET /escrow/history - Get payment history
  static const String escrowHistory = '/escrow/history';
}
