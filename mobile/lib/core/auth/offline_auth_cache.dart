import 'dart:convert';
import 'dart:math';

import 'package:cryptography/cryptography.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';

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
/// - Rate limiting: 5 failed attempts → 15 minute lockout
///
/// Ethiopian context:
/// - Enables login during poor connectivity (common in rural areas)
/// - Shared devices: Each user has separate cached credentials + rate limits
/// - Low-literacy users: Simple password requirements (minimum 6 chars)
/// - Offline brute force protection critical on shared devices
class OfflineAuthCache {
  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  // PBKDF2 parameters (OWASP recommendations for 2026)
  static const _pbkdf2Iterations = 100000;
  static const _pbkdf2KeyLength = 32; // 256 bits
  static const _saltLength = 32; // 32 bytes

  // Rate limiting parameters (prevent offline brute force)
  static const _maxFailedAttempts = 5;
  static const _lockoutDuration = Duration(minutes: 15);

  // In-memory tracking of failed attempts
  // NOTE: This is intentionally in-memory to reset on app restart
  // For persistent tracking across restarts, store in Isar User schema
  final Map<String, List<DateTime>> _failedAttempts = {};

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
  }) async {
    try {
      // Generate device-specific salt (or retrieve existing)
      final salt = await _getSalt(phoneNumber);

      // Hash password with PBKDF2
      final passwordHash = await _hashPassword(password, salt);

      // Store password hash in secure storage
      await _secureStorage.write(
        key: _passwordHashKey(phoneNumber),
        value: passwordHash,
      );

      // Store tokens in secure storage
      await _secureStorage.write(
        key: _tokensKey(phoneNumber),
        value: jsonEncode(tokens.toJson()),
      );

      // Store user profile in encrypted Isar database
      final isar = IsarService.instance.db;
      await isar.writeTxn(() async {
        final user = User()
          ..userId = userId
          ..phoneNumber = phoneNumber
          ..name = name
          ..role = role
          ..woreda = woreda
          ..createdAt = DateTime.now().toUtc()
          ..updatedAt = DateTime.now().toUtc()
          ..version = 1;

        await isar.users.put(user);
      });
    } catch (e) {
      throw Exception('Failed to cache credentials: $e');
    }
  }

  /// Verify password against cached hash for offline login.
  ///
  /// Returns true if password matches cached hash, false otherwise.
  ///
  /// Security features:
  /// - Constant-time comparison to prevent timing attacks
  /// - Rate limiting: 5 failed attempts → 15 minute lockout
  /// - Failed attempts tracked per phone number
  ///
  /// Throws [RateLimitException] if too many failed attempts.
  Future<bool> verifyPassword(String phoneNumber, String password) async {
    try {
      // SECURITY: Check rate limit BEFORE password verification
      if (_isRateLimited(phoneNumber)) {
        final lockoutRemaining = _getLockoutRemainingTime(phoneNumber);
        throw RateLimitException(
          'Too many failed login attempts. Try again in ${lockoutRemaining.inMinutes} minutes.',
          lockoutRemaining,
        );
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
        _recordFailedAttempt(phoneNumber);
      } else {
        // Clear failed attempts on successful login
        _clearFailedAttempts(phoneNumber);
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
      return await isar.users.where().phoneNumberEqualTo(phoneNumber).findFirst();
    } on Exception {
      return null;
    }
  }

  /// Get cached authentication tokens.
  ///
  /// Returns null if tokens are not cached.
  Future<AuthTokens?> getCachedTokens(String phoneNumber) async {
    try {
      final tokensJson = await _secureStorage.read(
        key: _tokensKey(phoneNumber),
      );

      if (tokensJson == null) {
        return null;
      }

      final json = jsonDecode(tokensJson) as Map<String, dynamic>;
      return AuthTokens.fromJson(json);
    } on Exception {
      return null;
    }
  }

  /// Update cached tokens (e.g., after token refresh).
  Future<void> updateTokens(String phoneNumber, AuthTokens tokens) async {
    try {
      await _secureStorage.write(
        key: _tokensKey(phoneNumber),
        value: jsonEncode(tokens.toJson()),
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

      // Delete user from Isar
      final isar = IsarService.instance.db;
      await isar.writeTxn(() async {
        // Use where clause for indexed field (phoneNumber has unique index)
        final user =
            await isar.users.where().phoneNumberEqualTo(phoneNumber).findFirst();
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
  bool _constantTimeEquals(String a, String b) {
    if (a.length != b.length) {
      return false;
    }

    int result = 0;
    for (int i = 0; i < a.length; i++) {
      result |= a.codeUnitAt(i) ^ b.codeUnitAt(i);
    }

    return result == 0;
  }

  /// Check if user is rate limited due to too many failed attempts.
  bool _isRateLimited(String phoneNumber) {
    final attempts = _failedAttempts[phoneNumber] ?? [];
    final recentAttempts = attempts.where(
      (time) => DateTime.now().difference(time) < _lockoutDuration,
    ).toList();

    // Update the list to only include recent attempts
    if (recentAttempts.length < attempts.length) {
      _failedAttempts[phoneNumber] = recentAttempts;
    }

    return recentAttempts.length >= _maxFailedAttempts;
  }

  /// Get remaining lockout duration for rate-limited user.
  Duration _getLockoutRemainingTime(String phoneNumber) {
    final attempts = _failedAttempts[phoneNumber] ?? [];
    if (attempts.isEmpty) {
      return Duration.zero;
    }

    // Find the oldest attempt within lockout window
    final oldestAttempt = attempts
        .where((time) => DateTime.now().difference(time) < _lockoutDuration)
        .reduce((a, b) => a.isBefore(b) ? a : b);

    final lockoutEnd = oldestAttempt.add(_lockoutDuration);
    final remaining = lockoutEnd.difference(DateTime.now());

    return remaining.isNegative ? Duration.zero : remaining;
  }

  /// Record a failed login attempt for rate limiting.
  void _recordFailedAttempt(String phoneNumber) {
    final attempts = _failedAttempts[phoneNumber] ?? [];
    attempts.add(DateTime.now());
    _failedAttempts[phoneNumber] = attempts;

    // Log for security monitoring
    // TODO: Send to analytics/monitoring service in production
    // ignore: avoid_print
    print('[SECURITY] Failed login attempt for $phoneNumber. '
        'Total recent attempts: ${attempts.length}');
  }

  /// Clear failed login attempts after successful authentication.
  void _clearFailedAttempts(String phoneNumber) {
    _failedAttempts.remove(phoneNumber);
  }

  // Secure storage key helpers
  String _passwordHashKey(String phoneNumber) => 'password_hash_$phoneNumber';
  String _saltKey(String phoneNumber) => 'salt_$phoneNumber';
  String _tokensKey(String phoneNumber) => 'tokens_$phoneNumber';
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
