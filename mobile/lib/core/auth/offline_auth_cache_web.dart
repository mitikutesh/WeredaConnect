import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';

/// Web implementation of OfflineAuthCache using FlutterSecureStorage.
///
/// On web, Isar is unavailable. This implementation persists tokens and
/// user profiles to FlutterSecureStorage (which uses localStorage on web)
/// so that authentication survives page refreshes.
class OfflineAuthCache {
  static const _storage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  static String _tokensKey(String phone) => 'cached_tokens_$phone';
  static String _userKey(String phone) => 'cached_user_$phone';

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
  }) async {
    // Persist tokens
    await _storage.write(
      key: _tokensKey(phoneNumber),
      value: jsonEncode(tokens.toJson()),
    );
    // Persist user profile
    await _storage.write(
      key: _userKey(phoneNumber),
      value: jsonEncode({
        'userId': userId,
        'phoneNumber': phoneNumber,
        'name': name,
        'role': role.name,
        'woreda': woreda,
        if (fullName != null) 'fullName': fullName,
        if (displayName != null) 'displayName': displayName,
      }),
    );
  }

  Future<void> saveUserToIsar({
    required String userId,
    required String phoneNumber,
    required String name,
    required UserRole role,
    required String woreda,
    String? fullName,
    String? displayName,
  }) async {
    await _storage.write(
      key: _userKey(phoneNumber),
      value: jsonEncode({
        'userId': userId,
        'phoneNumber': phoneNumber,
        'name': name,
        'role': role.name,
        'woreda': woreda,
        if (fullName != null) 'fullName': fullName,
        if (displayName != null) 'displayName': displayName,
      }),
    );
  }

  Future<bool> verifyPassword(String phoneNumber, String password) async =>
      false;

  Future<User?> getCachedUser(String phoneNumber) async {
    final raw = await _storage.read(key: _userKey(phoneNumber));
    if (raw == null || raw.isEmpty) return null;
    final m = jsonDecode(raw) as Map<String, dynamic>;
    // F-34: treat an unrecognized role as a cache miss (return null) rather than
    // silently defaulting to farmer, so callers never infer a wrong role.
    UserRole? role;
    for (final e in UserRole.values) {
      if (e.name == m['role']) {
        role = e;
        break;
      }
    }
    if (role == null) return null;
    return User()
      ..userId = m['userId'] as String? ?? ''
      ..phoneNumber = m['phoneNumber'] as String? ?? ''
      ..name = m['name'] as String? ?? ''
      ..role = role
      ..woreda = m['woreda'] as String? ?? '';
  }

  Future<AuthTokens?> getCachedTokens(String phoneNumber) async {
    final raw = await _storage.read(key: _tokensKey(phoneNumber));
    if (raw == null || raw.isEmpty) return null;
    final m = jsonDecode(raw) as Map<String, dynamic>;
    return AuthTokens.fromJson(m);
  }

  Future<void> updateTokens(String phoneNumber, AuthTokens tokens) async {
    await _storage.write(
      key: _tokensKey(phoneNumber),
      value: jsonEncode(tokens.toJson()),
    );
  }

  Future<bool> hasCachedCredentials(String phoneNumber) async {
    final tokens = await _storage.read(key: _tokensKey(phoneNumber));
    return tokens != null && tokens.isNotEmpty;
  }

  Future<void> clearCredentials(String phoneNumber) async {
    await _storage.delete(key: _tokensKey(phoneNumber));
    await _storage.delete(key: _userKey(phoneNumber));
  }

  Future<void> clearAllCredentials() async {
    await _storage.deleteAll();
  }
}

/// Exception thrown when rate limit is exceeded for offline login attempts.
class RateLimitException implements Exception {
  final String message;
  final Duration lockoutRemaining;

  RateLimitException(this.message, this.lockoutRemaining);

  @override
  String toString() => 'RateLimitException: $message';
}
