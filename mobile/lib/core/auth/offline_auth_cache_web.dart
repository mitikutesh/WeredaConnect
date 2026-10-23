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
  static String _cachedAtKey(String phone) => 'cached_at_$phone';

  /// Offline session TTL — mirrors the mobile cache's 48h stolen-device
  /// mitigation (S3-03). A web session older than this is treated as a cache
  /// miss so the user must re-authenticate online.
  static const _offlineSessionTtl = Duration(hours: 48);

  /// True when the cached session for [phone] is older than the TTL. A missing
  /// timestamp (pre-existing cache) is treated as not-expired to avoid logging
  /// everyone out on upgrade; new logins always stamp it.
  Future<bool> _isExpired(String phone) async {
    final raw = await _storage.read(key: _cachedAtKey(phone));
    if (raw == null || raw.isEmpty) return false;
    final cachedAt = DateTime.tryParse(raw);
    if (cachedAt == null) return false;
    return DateTime.now().toUtc().difference(cachedAt.toUtc()) >
        _offlineSessionTtl;
  }

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
    String? drivingLicensePath,
    String? buyerType,
    String? companyName,
    bool? faydaVerified,
    DateTime? faydaVerifiedAt,
  }) async {
    // Persist tokens
    await _storage.write(
      key: _tokensKey(phoneNumber),
      value: jsonEncode(tokens.toJson()),
    );
    // Stamp the cache time so the 48h offline session TTL can be enforced on read.
    await _storage.write(
      key: _cachedAtKey(phoneNumber),
      value: DateTime.now().toUtc().toIso8601String(),
    );
    // Persist user profile
    await saveUserToIsar(
      userId: userId,
      phoneNumber: phoneNumber,
      name: name,
      role: role,
      woreda: woreda,
      fullName: fullName,
      displayName: displayName,
      drivingLicensePath: drivingLicensePath,
      buyerType: buyerType,
      companyName: companyName,
      faydaVerified: faydaVerified,
      faydaVerifiedAt: faydaVerifiedAt,
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
    String? drivingLicensePath,
    String? buyerType,
    String? companyName,
    bool? faydaVerified,
    DateTime? faydaVerifiedAt,
  }) async {
    // Null verification fields mean "not reported": keep the cached values
    // (platform pair with the Isar implementation).
    final existing = await _readUserJson(phoneNumber) ?? const {};
    final verified = faydaVerified ?? existing['faydaVerified'] as bool?;
    final verifiedAt = faydaVerified == null
        ? existing['faydaVerifiedAt'] as String?
        : faydaVerifiedAt?.toUtc().toIso8601String();
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
        // F-32: role-specific fields (platform pair with the Isar implementation).
        if (drivingLicensePath != null)
          'drivingLicensePath': drivingLicensePath,
        if (buyerType != null) 'buyerType': buyerType,
        if (companyName != null) 'companyName': companyName,
        if (verified != null) 'faydaVerified': verified,
        if (verifiedAt != null) 'faydaVerifiedAt': verifiedAt,
      }),
    );
  }

  /// Record a successful Fayda link on the cached user (profile badge).
  Future<void> updateVerification({
    required String userId,
    required String phoneNumber,
    required bool faydaVerified,
    DateTime? faydaVerifiedAt,
  }) async {
    final existing = await _readUserJson(phoneNumber);
    if (existing == null || existing['userId'] != userId) return;
    existing['faydaVerified'] = faydaVerified;
    if (faydaVerifiedAt != null) {
      existing['faydaVerifiedAt'] = faydaVerifiedAt.toUtc().toIso8601String();
    } else {
      existing.remove('faydaVerifiedAt');
    }
    await _storage.write(
      key: _userKey(phoneNumber),
      value: jsonEncode(existing),
    );
  }

  Future<Map<String, dynamic>?> _readUserJson(String phoneNumber) async {
    final raw = await _storage.read(key: _userKey(phoneNumber));
    if (raw == null || raw.isEmpty) return null;
    try {
      final decoded = jsonDecode(raw);
      return decoded is Map<String, dynamic> ? decoded : null;
    } on FormatException {
      return null;
    }
  }

  Future<bool> verifyPassword(String phoneNumber, String password) async =>
      false;

  Future<User?> getCachedUser(String phoneNumber) async {
    // Enforce the 48h offline session TTL (stolen-device mitigation).
    if (await _isExpired(phoneNumber)) return null;
    final raw = await _storage.read(key: _userKey(phoneNumber));
    if (raw == null || raw.isEmpty) return null;
    try {
      final m = jsonDecode(raw) as Map<String, dynamic>;
      // F-34: treat an unrecognized role as a cache miss (return null) rather
      // than silently defaulting to farmer, so callers never infer a wrong role.
      UserRole? role;
      for (final e in UserRole.values) {
        if (e.name == m['role']) {
          role = e;
          break;
        }
      }
      if (role == null) return null;
      final faydaVerified = m['faydaVerified'] as bool? ?? false;
      return User()
        ..userId = m['userId'] as String? ?? ''
        ..phoneNumber = m['phoneNumber'] as String? ?? ''
        ..name = m['name'] as String? ?? ''
        ..fullName = m['fullName'] as String?
        ..displayName = m['displayName'] as String?
        ..role = role
        ..woreda = m['woreda'] as String? ?? ''
        ..faydaVerified = faydaVerified
        ..isVerified = faydaVerified
        ..faydaVerifiedAt =
            DateTime.tryParse(m['faydaVerifiedAt'] as String? ?? '');
    } on Object {
      // Corrupt or shape-mismatched cache (valid JSON that isn't the expected
      // shape) makes the `as Map`/field casts throw a TypeError (an Error, not
      // an Exception). Treat any failure as a cache miss so callers degrade to
      // re-login instead of crashing auto-login. Mirrors the mobile cache; do
      // NOT narrow to `on Exception` — that would let the TypeError escape the
      // caller's `on Exception` and hang the web app on the splash screen.
      return null;
    }
  }

  Future<AuthTokens?> getCachedTokens(String phoneNumber) async {
    // Enforce the 48h offline session TTL (stolen-device mitigation).
    if (await _isExpired(phoneNumber)) return null;
    final raw = await _storage.read(key: _tokensKey(phoneNumber));
    if (raw == null || raw.isEmpty) return null;
    try {
      final m = jsonDecode(raw) as Map<String, dynamic>;
      return AuthTokens.fromJson(m);
    } on Object {
      // Corrupt or shape-mismatched cache (valid JSON missing a required field)
      // makes AuthTokens.fromJson throw a TypeError (an Error, not an
      // Exception). Treat any failure as "no usable cached tokens" so callers
      // degrade to re-login instead of crashing offline/auto-login. Mirrors the
      // mobile cache; do NOT narrow to `on Exception` — that would let the
      // TypeError escape the caller's `on Exception` and hang the web app.
      return null;
    }
  }

  Future<void> updateTokens(String phoneNumber, AuthTokens tokens) async {
    await _storage.write(
      key: _tokensKey(phoneNumber),
      value: jsonEncode(tokens.toJson()),
    );
    // Re-stamp the session timestamp on refresh so an actively-used session is
    // not force-logged-out 48h after the ORIGINAL login (mirrors the mobile
    // cache's updateTokens, which resets the TTL on refresh).
    await _storage.write(
      key: _cachedAtKey(phoneNumber),
      value: DateTime.now().toUtc().toIso8601String(),
    );
  }

  Future<bool> hasCachedCredentials(String phoneNumber) async {
    final tokens = await _storage.read(key: _tokensKey(phoneNumber));
    return tokens != null && tokens.isNotEmpty;
  }

  Future<void> clearCredentials(String phoneNumber) async {
    await _storage.delete(key: _tokensKey(phoneNumber));
    await _storage.delete(key: _userKey(phoneNumber));
    await _storage.delete(key: _cachedAtKey(phoneNumber));
  }

  /// Remove everything this browser keeps for [phoneNumber]'s account once
  /// it is deleted. Web keeps no offline-login counter, so this is the same
  /// as [clearCredentials] (platform pair with the Isar implementation).
  Future<void> forgetAccount(String phoneNumber) =>
      clearCredentials(phoneNumber);

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
