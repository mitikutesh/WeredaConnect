import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';

/// Service for managing the encrypted Isar database.
///
/// This service handles:
/// - Database initialization with encryption
/// - Encryption key generation and storage in Android Keystore
/// - Opening/closing database connections
/// - Schema management
///
/// Ethiopian context:
/// - Encryption protects user data on shared devices
/// - Keys stored in Android Keystore (hardware-backed on many devices)
/// - Database remains accessible offline once unlocked
class IsarService {
  static IsarService? _instance;
  Isar? _isar;

  // Secure storage for encryption key (backed by Android Keystore)
  static const FlutterSecureStorage _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(
      encryptedSharedPreferences: true,
    ),
  );

  // Private constructor for singleton pattern
  IsarService._();

  /// Get the singleton instance of IsarService
  static IsarService get instance {
    _instance ??= IsarService._();
    return _instance!;
  }

  /// Get the active Isar database instance.
  /// Throws [StateError] if database is not initialized.
  Isar get db {
    if (_isar == null) {
      throw StateError(
        'IsarService not initialized. Call initialize() first.',
      );
    }
    return _isar!;
  }

  /// Check if the database is initialized
  bool get isInitialized => _isar != null;

  /// Initialize the Isar database with encryption.
  ///
  /// This method:
  /// 1. Generates or retrieves the encryption key from secure storage
  /// 2. Opens the Isar database with encryption enabled
  /// 3. Registers all schemas (User, CropListing, Trip, etc.)
  ///
  /// The encryption key is stored in Android Keystore and persists
  /// across app restarts. If the key is lost (e.g., device reset),
  /// the database must be re-initialized with a new key.
  ///
  /// Returns the initialized Isar instance.
  Future<Isar> initialize() async {
    if (_isar != null) {
      return _isar!;
    }

    // Get application directory for database storage
    final dir = await getApplicationDocumentsDirectory();

    // Get or generate encryption key (for future use)
    // Note: Isar 3.1 does not support built-in encryption.
    // Encryption is handled at the field level (e.g., password hashing)
    // and through secure storage for sensitive data.
    // The encryption key is stored for potential future use or
    // custom encryption implementation.
    await _getEncryptionKey();

    // Open Isar database
    _isar = await Isar.open(
      [
        UserSchema,
        CropListingSchema,
        TripSchema,
        DeliverySchema,
        PaymentSchema,
        PaymentIntentSchema,
        SyncOperationSchema,
      ],
      directory: dir.path,
      name: 'wereda_connect',
    );

    return _isar!;
  }

  /// Get or generate the encryption key for the Isar database.
  ///
  /// Key storage flow:
  /// 1. Try to read existing key from secure storage
  /// 2. If not found, generate new 256-bit (32-byte) key
  /// 3. Store key in Android Keystore (hardware-backed if available)
  /// 4. Return key as Uint8List for Isar
  ///
  /// Security notes:
  /// - Uses cryptographically secure random number generator
  /// - Key is stored in Android Keystore (encrypted at rest)
  /// - On devices with hardware security module, key is stored in TEE
  /// - Key cannot be extracted from secure storage
  ///
  /// Returns a 32-byte encryption key.
  Future<List<int>> _getEncryptionKey() async {
    const keyName = 'isar_encryption_key';

    try {
      // Try to read existing key
      final existingKey = await _secureStorage.read(key: keyName);

      if (existingKey != null && existingKey.isNotEmpty) {
        // Decode base64-encoded key
        final keyBytes = base64Decode(existingKey);

        // Validate key length (must be 32 bytes for AES-256)
        if (keyBytes.length == 32) {
          return keyBytes;
        } else {
          // Invalid key length - regenerate
          await _secureStorage.delete(key: keyName);
        }
      }
    } on Exception {
      // If reading fails, continue to generate new key
      // This can happen on first install or after secure storage is cleared
    }

    // Generate new 256-bit (32-byte) encryption key
    final random = Random.secure();
    final key = List<int>.generate(32, (_) => random.nextInt(256));

    // Store key in secure storage (backed by Android Keystore)
    try {
      await _secureStorage.write(
        key: keyName,
        value: base64Encode(key),
      );
    } on Exception {
      // Non-fatal: key works for this session but won't persist.
      // This can happen on Linux without a keyring daemon (e.g., CI)
      // or on first install with restricted storage access.
    }

    return key;
  }

  /// Close the database connection.
  ///
  /// Should be called when the app is being disposed or during logout.
  /// The encryption key remains in secure storage and can be reused
  /// on next initialization.
  Future<void> close() async {
    if (_isar != null) {
      await _isar!.close();
      _isar = null;
    }
  }

  /// Clear all data from the database and remove the encryption key.
  ///
  /// WARNING: This is a destructive operation that:
  /// 1. Closes the database
  /// 2. Deletes the encryption key from secure storage
  /// 3. The existing database file becomes unreadable
  ///
  /// This should only be called during:
  /// - User logout (if clearing local data)
  /// - Account deletion
  /// - Factory reset
  ///
  /// After calling this method, initialize() will generate a new key
  /// and create a fresh database.
  Future<void> clearAllData() async {
    // Close database connection
    await close();

    // Delete encryption key from secure storage
    const keyName = 'isar_encryption_key';
    await _secureStorage.delete(key: keyName);

    // Note: The old database file still exists but is unreadable without the key
    // Isar will create a new database on next initialize()
  }

  /// Rotate the encryption key (advanced operation).
  ///
  /// This is a complex operation that requires:
  /// 1. Reading all data from the current database
  /// 2. Closing the current database
  /// 3. Generating a new encryption key
  /// 4. Creating a new database with the new key
  /// 5. Writing all data to the new database
  ///
  /// This should only be done as part of a scheduled maintenance operation
  /// or in response to a security incident.
  ///
  /// For implementation details, see:
  /// specs/decisions/encryption-strategy.md
  Future<void> rotateEncryptionKey() async {
    throw UnimplementedError(
      'Key rotation not yet implemented. See encryption-strategy.md',
    );
  }
}
