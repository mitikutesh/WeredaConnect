import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';

/// Web stub for OfflineAuthCache — no Isar dependency.
///
/// On web, authentication is always online. Offline caching is not needed.
class OfflineAuthCache {
  Future<void> cacheCredentials({
    required String phoneNumber,
    required String password,
    required String userId,
    required UserRole role,
    required String name,
    required String woreda,
    required AuthTokens tokens,
    String? fullName,
    String? displayName,
  }) async {}

  Future<void> saveUserToIsar({
    required String userId,
    required String phoneNumber,
    required String name,
    required UserRole role,
    required String woreda,
    String? fullName,
    String? displayName,
  }) async {}

  Future<bool> verifyPassword(String phoneNumber, String password) async =>
      false;

  Future<User?> getCachedUser(String phoneNumber) async => null;

  Future<AuthTokens?> getCachedTokens(String phoneNumber) async => null;

  Future<void> updateTokens(String phoneNumber, AuthTokens tokens) async {}

  Future<bool> hasCachedCredentials(String phoneNumber) async => false;

  Future<void> clearCredentials(String phoneNumber) async {}

  Future<void> clearAllCredentials() async {}
}

/// Exception thrown when rate limit is exceeded for offline login attempts.
class RateLimitException implements Exception {
  final String message;
  final Duration lockoutRemaining;

  RateLimitException(this.message, this.lockoutRemaining);

  @override
  String toString() => 'RateLimitException: $message';
}
