# Encryption Strategy Decision

## Status
APPROVED

## Date
2026-02-14

## Context
WeredaConnect stores sensitive user data locally in an Isar database for offline-first functionality. This data includes:
- User credentials (cached for offline login)
- Financial information (crop prices, payment amounts)
- Personal information (phone numbers, names, locations)
- Transaction history

Ethiopian context:
- Devices are often shared among family members or cooperative workers
- Low-end Android devices may not have hardware security modules
- Users may not use device lock screens consistently

## Decision

### 1. Database Encryption
**Technology**: Isar built-in encryption with AES-256

**Key Management**:
- 256-bit (32-byte) encryption key generated using cryptographically secure RNG
- Key stored in Android Keystore via `flutter_secure_storage`
- Key persists across app restarts but not device resets
- One key per device (not per user)

**Implementation**:
```dart
// Generate key on first use
final key = List<int>.generate(32, (_) => Random.secure().nextInt(256));

// Store in Android Keystore
await FlutterSecureStorage().write(
  key: 'isar_encryption_key',
  value: base64Encode(key),
);

// Open database with encryption
await Isar.open(
  schemas,
  encryptionCipher: IsarCipher(key),
);
```

### 2. Token Storage
**Technology**: `flutter_secure_storage` with Android Keystore backing

**Tokens stored**:
- Access token (JWT, 15-minute lifetime)
- Refresh token (30-day lifetime)
- ID token (user profile)

**Implementation**:
```dart
const storage = FlutterSecureStorage(
  aOptions: AndroidOptions(encryptedSharedPreferences: true),
);

await storage.write(key: 'access_token', value: token);
```

### 3. Password Hashing (Offline Auth)
**Technology**: PBKDF2 with HMAC-SHA256

**Parameters**:
- Iterations: 100,000 (recommended by OWASP for 2026)
- Salt: 32 bytes, device-specific (stored in secure storage)
- Output: 32 bytes (256 bits)

**Implementation**:
```dart
final pbkdf2 = Pbkdf2(
  macAlgorithm: Hmac.sha256(),
  iterations: 100000,
  bits: 256,
);

final hash = await pbkdf2.deriveKey(
  secretKey: SecretKey(utf8.encode(password)),
  nonce: salt,
);
```

**Why not bcrypt/scrypt?**
- PBKDF2 is FIPS-approved and widely supported
- bcrypt/scrypt have limited Flutter/Dart support
- We control iteration count for future-proofing

## Key Rotation Procedure

### When to Rotate
- Annually as part of scheduled maintenance
- In response to security incident
- When device is transferred to new user
- User explicitly requests it

### Rotation Steps
1. **Preparation**:
   - Ensure device is online (data will sync from server)
   - Create backup of current database (optional)
   - Warn user about temporary app unavailability

2. **Data Export**:
   ```dart
   final users = await isar.users.where().findAll();
   final crops = await isar.cropListings.where().findAll();
   // ... export all collections
   ```

3. **Key Rotation**:
   ```dart
   // Close current database
   await isar.close();

   // Delete old key
   await secureStorage.delete(key: 'isar_encryption_key');

   // Generate new key (IsarService will auto-generate on next init)
   await IsarService.instance.initialize();
   ```

4. **Data Import**:
   ```dart
   // Write data to new database
   await isar.writeTxn(() async {
     await isar.users.putAll(users);
     await isar.cropListings.putAll(crops);
     // ... import all collections
   });
   ```

5. **Verification**:
   - Verify record counts match
   - Test critical user flows
   - Sync with server to ensure consistency

6. **Completion**:
   - Clear old database file (if it exists)
   - Log rotation event for audit trail

### Automation Considerations
- Key rotation can be triggered remotely via server notification
- Rotation should only occur when device is online and charging
- UI should show progress bar during rotation
- If rotation fails, rollback to old database (if backup exists)

## Security Considerations

### Threats Mitigated
- ✅ Data at rest protection (encrypted database)
- ✅ Token theft (secure storage with Keystore)
- ✅ Shared device access (encryption prevents casual browsing)
- ✅ Device theft (data unreadable without unlock)

### Threats NOT Mitigated
- ❌ Root/jailbreak attacks (attacker with root can extract keys)
- ❌ Memory dumps while app is running (data is decrypted in memory)
- ❌ Screen recording (UI shows data in plaintext)
- ❌ Malware on device (can intercept data in memory)

### Mitigation for Unaddressed Threats
- Require device lock screen (Android Security Settings check)
- Implement app-level PIN/biometric lock for sensitive operations
- Use ProGuard/R8 to obfuscate code
- Detect rooted devices and warn users
- Implement certificate pinning to prevent MITM

## Trade-offs

### Performance Impact
- **Database operations**: ~5-10% slower (encryption overhead)
- **Startup time**: +50-100ms (key retrieval from Keystore)
- **Battery usage**: Negligible (AES is hardware-accelerated on most devices)

### User Experience Impact
- **Positive**: Users feel more secure about data privacy
- **Negative**: Database becomes unusable if key is lost (e.g., after factory reset)
- **Mitigation**: Sync with server allows data recovery

### Development Complexity
- **Positive**: Isar encryption is transparent (no schema changes)
- **Negative**: Key rotation requires careful implementation
- **Mitigation**: Comprehensive testing and rollback procedures

## Alternatives Considered

### 1. No Encryption
**Pros**: Simpler, faster, no key management
**Cons**: Unacceptable security risk, violates data protection best practices
**Decision**: Rejected

### 2. SQLCipher (SQLite encryption)
**Pros**: Industry standard, well-tested
**Cons**: Requires switching from Isar to SQLite, more complex setup
**Decision**: Rejected (Isar encryption is sufficient)

### 3. File-level encryption (Android's encrypted storage)
**Pros**: Automatic, OS-managed
**Cons**: Only works when device is locked, not suitable for background sync
**Decision**: Rejected (too restrictive for offline-first use case)

### 4. User-provided password for encryption
**Pros**: Most secure (key never stored on device)
**Cons**: Poor UX (must enter password every time), incompatible with background sync
**Decision**: Rejected (offline-first requires automatic unlock)

## Compliance

### Data Protection Laws (Ethiopian Context)
- Ethiopia does not have comprehensive data protection law as of 2026
- We follow GDPR principles for international best practices
- Encryption is considered "appropriate technical measures" under GDPR Article 32

### Payment Card Industry (PCI) Compliance
- Not applicable (we use Telebirr for payments, not card data)
- Telebirr transaction IDs are considered sensitive but not PCI-regulated

### OWASP Mobile Top 10
- M2 (Insecure Data Storage): ✅ Mitigated with encryption
- M4 (Insecure Authentication): ✅ Mitigated with secure token storage
- M9 (Reverse Engineering): ⚠️ Partially mitigated with obfuscation

## Testing Strategy

### Unit Tests
- Key generation produces 32-byte keys
- Key storage and retrieval works correctly
- Invalid keys are rejected
- Multiple initialize() calls return same instance

### Integration Tests
- Database opens successfully with encryption
- Data written to encrypted DB can be read back
- Database file is binary (not JSON-readable)
- Key persists across app restarts
- Key rotation preserves all data

### Manual Testing
- Test on device without hardware security module
- Test on shared device with multiple users
- Test key behavior after factory reset
- Test performance on low-end device (2GB RAM)

### Security Testing
- Attempt to read database file without key (should fail)
- Verify key is not visible in logcat
- Verify key is not in shared preferences
- Test with rooted device (warn if detected)

## References
- [Isar Encryption Documentation](https://isar.dev/encryption.html)
- [flutter_secure_storage](https://pub.dev/packages/flutter_secure_storage)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [Android Keystore System](https://developer.android.com/training/articles/keystore)
- [NIST SP 800-132 PBKDF2 Recommendations](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-132.pdf)

## Version History
- **1.0.0** (2026-02-14): Initial encryption strategy
- Future: Add key rotation automation (v2.0.0)

## Approval
- [x] Architect: Approved
- [x] Security: Approved (see SECURITY_AUDIT_2026-02-10.md)
- [x] Mobile Agent: Implemented
