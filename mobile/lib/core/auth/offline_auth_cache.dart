import 'dart:convert';
import 'dart:math';

import 'package:cryptography/cryptography.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/models/user.dart';

/// Manages offline authentication cache with PBKDF2 password hashing.
///
/// This service enables offline login by:
/// 1. Caching password hash (PBKDF2, 100,000 iterations) in secure storage
/// 2. Storing user profile in encrypted Isar database
/// 3. Caching authentication tokens in secure storage
/// 4. Rate limiting offline login attempts to prevent brute force attacks
///
/// Security notes:
/// - Password is NEVER stored in plaintext
/// - PBKDF2 hash with device-specific salt prevents rainbow table attacks
/// - 100,000 iterations (OWASP recommended for 2026)
/// - All data encrypted at rest (secure storage + Isar encryption)
/// - Rate limiting: exponential backoff from 1st failure (1min → 5min → 15min → 1hr)
/// - 5th failure → permanent lockout (credentials cleared, online re-auth required)
///
/// Ethiopian context:
/// - Enables login during poor connectivity (common in rural areas)
/// - Shared devices: Each user has separate cached credentials + rate limits
/// - Low-literacy users: Password requirements aligned with Cognito (minimum 8 chars + lowercase + digit)
/// - Offline brute force protection critical on shared devices
class OfflineAuthCache {
  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  // PBKDF2 parameters (OWASP recommendations for 2026)
  static const _pbkdf2Iterations = 100000;
  static const _pbkdf2KeyLength = 32; // 256 bits
  static const _saltLength = 32; // 32 bytes

  // Rate limiting parameters (prevent offline brute force — stolen-device mitigation)
  // Lockout starts from the 1st failed attempt (no free failures).
  static const _maxFailedAttempts = 1;

  // Exponential backoff lockout durations (by consecutive failure count)
  static const _lockoutTiers = [
    Duration(minutes: 1), // After 1st failure
    Duration(minutes: 5), // After 2nd failure
    Duration(minutes: 15), // After 3rd failure
    Duration(hours: 1), // After 4th failure
  ];

  // After this many failures → permanent lockout: clear credentials and
  // require online re-authentication (stolen-device mitigation S3-03).
  static const _permanentLockoutThreshold = 5;
  static const _dailyFailureWindow = Duration(hours: 24);

  // Offline session TTL — reduced from 7 days to 48 hours for stolen-device
  // mitigation (S3-03). Users must re-authenticate online after 48 hours.
  static const _offlineSessionTtl = Duration(hours: 48);

  // Rate limit storage key prefix
  static const _rateLimitKeyPrefix = 'rate_limit_';

  /// Cache user credentials for offline authentication.
  ///
  /// This should be called after successful online login to enable
  /// future offline logins.
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number (used as cache key)
  /// - [password]: User's password (will be hashed, never stored plaintext)
  /// - [userId]: Server-assigned user ID
  /// - [role]: User role (farmer, trucker, buyer)
  /// - [name]: User's display name
  /// - [woreda]: User's woreda (district)
  /// - [tokens]: Authentication tokens to cache
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
  }) async {
    // Store user profile in Isar first — required for profile screen.
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
    );

    // Cache password hash and tokens in secure storage for offline auth.
    // If this fails, the user is still logged in and profile works;
    // only offline login will be unavailable.
    try {
      final salt = await _getSalt(phoneNumber);
      final passwordHash = await _hashPassword(password, salt);

      await _secureStorage.write(
        key: _passwordHashKey(phoneNumber),
        value: passwordHash,
      );
      await _secureStorage.write(
        key: _tokensKey(phoneNumber),
        value: jsonEncode(tokens.toJson()),
      );
      await _secureStorage.write(
        key: _sessionTimestampKey(phoneNumber),
        value: DateTime.now().toUtc().toIso8601String(),
      );
    } catch (e) {
      throw Exception('Failed to cache credentials: $e');
    }
  }

  /// Save user profile to Isar database.
  ///
  /// Separated from [cacheCredentials] so the user profile is always
  /// available for display even if secure storage operations fail.
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
  }) async {
    final isar = IsarService.instance.db;
    await isar.writeTxn(() async {
      // Preserve existing Isar id and createdAt if user already exists
      final existing =
          await isar.users.where().userIdEqualTo(userId).findFirst();
      final user = User()
        ..id = existing?.id ?? Isar.autoIncrement
        ..userId = userId
        ..phoneNumber = phoneNumber
        ..name = name
        ..fullName = fullName
        ..displayName = displayName
        ..role = role
        ..woreda = woreda
        // F-32: role-specific fields. Preserve existing values when not supplied
        // (e.g. re-login passes null) so they aren't clobbered. Driving-license
        // is local-only; buyer type/company also persist server-side.
        ..drivingLicensePath =
            drivingLicensePath ?? existing?.drivingLicensePath
        ..buyerType = buyerType ?? existing?.buyerType
        ..companyName = companyName ?? existing?.companyName
        ..createdAt = existing?.createdAt ?? DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = (existing?.version ?? 0) + 1;

      await isar.users.put(user);
    });
  }

  /// Verify password against cached hash for offline login.
  ///
  /// Returns true if password matches cached hash, false otherwise.
  ///
  /// Security features:
  /// - Constant-time comparison to prevent timing attacks
  /// - Rate limiting: exponential backoff from 1st failure (1min → 5min → 15min → 1hr)
  /// - 5th failure → permanent lockout (credentials cleared, online re-auth required)
  /// - Failed attempts tracked per phone number
  ///
  /// Throws [RateLimitException] if too many failed attempts.
  Future<bool> verifyPassword(String phoneNumber, String password) async {
    try {
      // Check offline session TTL (48-hour expiry)
      if (await _isSessionExpired(phoneNumber)) {
        // Clear expired credentials
        await clearCredentials(phoneNumber);
        return false;
      }

      // SECURITY: Check rate limit BEFORE password verification
      if (await _isRateLimited(phoneNumber)) {
        final lockoutRemaining = await _getLockoutRemainingTime(phoneNumber);
        final message = lockoutRemaining >= const Duration(hours: 24)
            ? 'Too many failed login attempts. Please connect to the internet and log in online.'
            : 'Too many failed login attempts. Try again in ${lockoutRemaining.inMinutes} minutes.';
        throw RateLimitException(message, lockoutRemaining);
      }

      // Get cached password hash
      final cachedHash = await _secureStorage.read(
        key: _passwordHashKey(phoneNumber),
      );

      if (cachedHash == null) {
        return false; // No cached credentials
      }

      // Get salt
      final salt = await _getSalt(phoneNumber);

      // Hash input password
      final inputHash = await _hashPassword(password, salt);

      // Constant-time comparison (prevents timing attacks)
      final isValid = _constantTimeEquals(cachedHash, inputHash);

      // Track failed attempts
      if (!isValid) {
        await _recordFailedAttempt(phoneNumber);
      } else {
        // Clear failed attempts on successful login
        await _clearFailedAttempts(phoneNumber);
      }

      return isValid;
    } catch (e) {
      // Re-throw RateLimitException, return false for other errors
      if (e is RateLimitException) {
        rethrow;
      }
      return false;
    }
  }

  /// Get cached user profile from Isar database.
  ///
  /// Returns null if user is not cached.
  Future<User?> getCachedUser(String phoneNumber) async {
    try {
      final isar = IsarService.instance.db;
      // Use where clause for indexed field (phoneNumber has unique index)
      return await isar.users
          .where()
          .phoneNumberEqualTo(phoneNumber)
          .findFirst();
    } on Exception {
      return null;
    }
  }

  /// Get cached authentication tokens.
  ///
  /// Returns null if tokens are not cached.
  Future<AuthTokens?> getCachedTokens(String phoneNumber) async {
    try {
      // Check offline session TTL (48-hour expiry)
      if (await _isSessionExpired(phoneNumber)) {
        return null;
      }

      final tokensJson = await _secureStorage.read(
        key: _tokensKey(phoneNumber),
      );

      if (tokensJson == null) {
        return null;
      }

      final json = jsonDecode(tokensJson) as Map<String, dynamic>;
      return AuthTokens.fromJson(json);
    } on Object {
      // Corrupt or shape-mismatched cache (valid JSON missing a required
      // field) makes AuthTokens.fromJson throw a TypeError (an Error, not an
      // Exception). Treat any failure as "no usable cached tokens" so callers
      // degrade to re-login instead of crashing offline/auto-login.
      return null;
    }
  }

  /// Update cached tokens (e.g., after token refresh).
  ///
  /// Also resets the offline session TTL so users on flaky 3G who
  /// refresh tokens don't get forced to re-login prematurely.
  Future<void> updateTokens(String phoneNumber, AuthTokens tokens) async {
    try {
      await _secureStorage.write(
        key: _tokensKey(phoneNumber),
        value: jsonEncode(tokens.toJson()),
      );

      // Reset offline session TTL — a successful token refresh proves
      // the user had connectivity, so the 48-hour clock restarts.
      await _secureStorage.write(
        key: _sessionTimestampKey(phoneNumber),
        value: DateTime.now().toUtc().toIso8601String(),
      );
    } catch (e) {
      throw Exception('Failed to update tokens: $e');
    }
  }

  /// Check if user has cached credentials.
  Future<bool> hasCachedCredentials(String phoneNumber) async {
    final passwordHash = await _secureStorage.read(
      key: _passwordHashKey(phoneNumber),
    );
    return passwordHash != null;
  }

  /// Clear cached credentials for a specific user.
  ///
  /// This should be called on logout.
  Future<void> clearCredentials(String phoneNumber) async {
    try {
      // Delete password hash
      await _secureStorage.delete(key: _passwordHashKey(phoneNumber));

      // Delete salt
      await _secureStorage.delete(key: _saltKey(phoneNumber));

      // Delete tokens
      await _secureStorage.delete(key: _tokensKey(phoneNumber));

      // Delete session timestamp
      await _secureStorage.delete(key: _sessionTimestampKey(phoneNumber));

      // Delete user from Isar
      final isar = IsarService.instance.db;
      await isar.writeTxn(() async {
        // Use where clause for indexed field (phoneNumber has unique index)
        final user = await isar.users
            .where()
            .phoneNumberEqualTo(phoneNumber)
            .findFirst();
        if (user != null) {
          await isar.users.delete(user.id);
        }
      });
    } catch (e) {
      throw Exception('Failed to clear credentials: $e');
    }
  }

  /// Clear all cached credentials (for all users).
  ///
  /// This should be called during app reset or uninstall.
  Future<void> clearAllCredentials() async {
    try {
      // Clear all secure storage
      await _secureStorage.deleteAll();

      // Clear Isar database
      await IsarService.instance.clearAllData();
    } catch (e) {
      throw Exception('Failed to clear all credentials: $e');
    }
  }

  // ============================================================================
  // Private methods
  // ============================================================================

  /// Hash password using PBKDF2 with HMAC-SHA256.
  Future<String> _hashPassword(String password, List<int> salt) async {
    final pbkdf2 = Pbkdf2(
      macAlgorithm: Hmac.sha256(),
      iterations: _pbkdf2Iterations,
      bits: _pbkdf2KeyLength * 8,
    );

    final secretKey = SecretKey(utf8.encode(password));
    final hash = await pbkdf2.deriveKey(
      secretKey: secretKey,
      nonce: salt,
    );

    final hashBytes = await hash.extractBytes();
    return base64Encode(hashBytes);
  }

  /// Get or generate device-specific salt for password hashing.
  Future<List<int>> _getSalt(String phoneNumber) async {
    final saltKey = _saltKey(phoneNumber);
    final existingSalt = await _secureStorage.read(key: saltKey);

    if (existingSalt != null) {
      return base64Decode(existingSalt);
    }

    // Generate new salt
    final random = Random.secure();
    final salt = List<int>.generate(_saltLength, (_) => random.nextInt(256));

    // Store salt
    await _secureStorage.write(
      key: saltKey,
      value: base64Encode(salt),
    );

    return salt;
  }

  /// Constant-time string comparison to prevent timing attacks.
  ///
  /// Iterates over the longer string using 0 as fill for the shorter one,
  /// so the loop duration does not leak the length of either input.
  bool _constantTimeEquals(String a, String b) {
    final maxLen = a.length > b.length ? a.length : b.length;
    int result = a.length ^ b.length;
    for (int i = 0; i < maxLen; i++) {
      final ca = i < a.length ? a.codeUnitAt(i) : 0;
      final cb = i < b.length ? b.codeUnitAt(i) : 0;
      result |= ca ^ cb;
    }
    return result == 0;
  }

  /// Read persisted failed attempt timestamps from secure storage.
  Future<List<DateTime>> _readAttempts(String phoneNumber) async {
    final key = '$_rateLimitKeyPrefix$phoneNumber';
    final raw = await _secureStorage.read(key: key);
    if (raw == null || raw.isEmpty) return [];

    try {
      final list = (jsonDecode(raw) as List<dynamic>).cast<String>();
      return list.map(DateTime.parse).toList();
    } on FormatException {
      // Corrupt data — clear and return empty
      await _secureStorage.delete(key: key);
      return [];
    }
  }

  /// Write failed attempt timestamps to secure storage.
  Future<void> _writeAttempts(
    String phoneNumber,
    List<DateTime> attempts,
  ) async {
    final key = '$_rateLimitKeyPrefix$phoneNumber';
    if (attempts.isEmpty) {
      await _secureStorage.delete(key: key);
      return;
    }
    final encoded =
        jsonEncode(attempts.map((t) => t.toIso8601String()).toList());
    await _secureStorage.write(key: key, value: encoded);
  }

  /// Check if user is rate limited due to too many failed attempts.
  ///
  /// Implements exponential backoff from the 1st failure: lockout grows
  /// from 1 min up to 1 hour. After 5 failures the credentials are
  /// permanently cleared and online re-authentication is required
  /// (stolen-device mitigation S3-03).
  ///
  /// Persisted to secure storage so rate limits survive app restarts.
  Future<bool> _isRateLimited(String phoneNumber) async {
    final attempts = await _readAttempts(phoneNumber);
    if (attempts.isEmpty) return false;

    // Check failures within the tracking window
    final recentAttempts = attempts
        .where(
          (time) =>
              DateTime.now().toUtc().difference(time) < _dailyFailureWindow,
        )
        .toList();

    // Permanent lockout: 5+ failures → clear credentials, require online re-auth
    if (recentAttempts.length >= _permanentLockoutThreshold) {
      await clearCredentials(phoneNumber);
      await _clearFailedAttempts(phoneNumber);
      return true;
    }

    if (recentAttempts.length < _maxFailedAttempts) {
      return false; // Under threshold
    }

    // Determine lockout tier based on consecutive failures beyond threshold
    final tierIndex = (recentAttempts.length - _maxFailedAttempts)
        .clamp(0, _lockoutTiers.length - 1);
    final lockoutDuration = _lockoutTiers[tierIndex];

    // Check if most recent attempt is within lockout window
    final lastAttempt = recentAttempts.reduce((a, b) => a.isAfter(b) ? a : b);
    return DateTime.now().toUtc().difference(lastAttempt) < lockoutDuration;
  }

  /// Get remaining lockout duration for rate-limited user.
  Future<Duration> _getLockoutRemainingTime(String phoneNumber) async {
    final attempts = await _readAttempts(phoneNumber);
    if (attempts.isEmpty) return Duration.zero;

    final recentAttempts = attempts
        .where(
          (time) =>
              DateTime.now().toUtc().difference(time) < _dailyFailureWindow,
        )
        .toList();

    if (recentAttempts.isEmpty) return Duration.zero;

    // Permanent lockout: credentials already cleared, force online re-auth
    if (recentAttempts.length >= _permanentLockoutThreshold) {
      return const Duration(hours: 24);
    }

    if (recentAttempts.length < _maxFailedAttempts) {
      return Duration.zero;
    }

    final tierIndex = (recentAttempts.length - _maxFailedAttempts)
        .clamp(0, _lockoutTiers.length - 1);
    final lockoutDuration = _lockoutTiers[tierIndex];

    final lastAttempt = recentAttempts.reduce((a, b) => a.isAfter(b) ? a : b);
    final lockoutEnd = lastAttempt.add(lockoutDuration);
    final remaining = lockoutEnd.difference(DateTime.now().toUtc());

    return remaining.isNegative ? Duration.zero : remaining;
  }

  /// Record a failed login attempt for rate limiting.
  ///
  /// Persisted to secure storage so brute-force protection
  /// survives app restarts on shared devices.
  Future<void> _recordFailedAttempt(String phoneNumber) async {
    final attempts = await _readAttempts(phoneNumber);
    attempts.add(DateTime.now().toUtc());
    await _writeAttempts(phoneNumber, attempts);
  }

  /// Clear failed login attempts after successful authentication.
  Future<void> _clearFailedAttempts(String phoneNumber) async {
    await _secureStorage.delete(key: '$_rateLimitKeyPrefix$phoneNumber');
  }

  /// Check if offline session has expired (48-hour TTL).
  Future<bool> _isSessionExpired(String phoneNumber) async {
    final timestampStr = await _secureStorage.read(
      key: _sessionTimestampKey(phoneNumber),
    );

    if (timestampStr == null) {
      // No timestamp means legacy cache — allow but write timestamp now
      // so TTL starts from this point
      await _secureStorage.write(
        key: _sessionTimestampKey(phoneNumber),
        value: DateTime.now().toUtc().toIso8601String(),
      );
      return false;
    }

    try {
      final timestamp = DateTime.parse(timestampStr);
      return DateTime.now().toUtc().difference(timestamp) > _offlineSessionTtl;
    } on FormatException {
      return true;
    }
  }

  // Secure storage key helpers
  String _passwordHashKey(String phoneNumber) => 'password_hash_$phoneNumber';
  String _saltKey(String phoneNumber) => 'salt_$phoneNumber';
  String _tokensKey(String phoneNumber) => 'tokens_$phoneNumber';
  String _sessionTimestampKey(String phoneNumber) =>
      'session_timestamp_$phoneNumber';
}

/// Exception thrown when rate limit is exceeded for offline login attempts.
///
/// This prevents brute force attacks on cached credentials.
class RateLimitException implements Exception {
  final String message;
  final Duration lockoutRemaining;

  RateLimitException(this.message, this.lockoutRemaining);

  @override
  String toString() => 'RateLimitException: $message';
}
