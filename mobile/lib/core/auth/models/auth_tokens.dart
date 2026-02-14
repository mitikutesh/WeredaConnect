/// Authentication tokens from AWS Cognito or offline cache.
///
/// These tokens are used for:
/// - Access token: API authorization (15-minute lifetime)
/// - Refresh token: Renewing expired access tokens (30-day lifetime)
/// - ID token: User profile information
///
/// Ethiopian context:
/// - Long refresh token lifetime supports intermittent connectivity
/// - Tokens cached in secure storage for offline authentication
class AuthTokens {
  /// JWT access token for API authorization
  /// Expires after 15 minutes
  final String accessToken;

  /// Refresh token for obtaining new access tokens
  /// Expires after 30 days
  final String refreshToken;

  /// ID token containing user profile claims
  final String idToken;

  /// Token expiration timestamp (UTC)
  /// Used to determine when to refresh tokens
  final DateTime? expiresAt;

  const AuthTokens({
    required this.accessToken,
    required this.refreshToken,
    required this.idToken,
    this.expiresAt,
  });

  /// Create tokens from JSON (from API or secure storage)
  factory AuthTokens.fromJson(Map<String, dynamic> json) {
    return AuthTokens(
      accessToken: json['accessToken'] as String,
      refreshToken: json['refreshToken'] as String,
      idToken: json['idToken'] as String,
      expiresAt: json['expiresAt'] != null
          ? DateTime.parse(json['expiresAt'] as String)
          : null,
    );
  }

  /// Convert to JSON for storage
  Map<String, dynamic> toJson() {
    return {
      'accessToken': accessToken,
      'refreshToken': refreshToken,
      'idToken': idToken,
      'expiresAt': expiresAt?.toIso8601String(),
    };
  }

  /// Check if access token is expired
  bool get isExpired {
    if (expiresAt == null) return false;
    return DateTime.now().toUtc().isAfter(expiresAt!);
  }

  /// Check if access token will expire soon (within 1 minute)
  /// Used to proactively refresh tokens
  bool get willExpireSoon {
    if (expiresAt == null) return false;
    final oneMinuteFromNow = DateTime.now().toUtc().add(
          const Duration(minutes: 1),
        );
    return oneMinuteFromNow.isAfter(expiresAt!);
  }

  @override
  String toString() {
    return 'AuthTokens(expiresAt: $expiresAt, expired: $isExpired)';
  }
}
