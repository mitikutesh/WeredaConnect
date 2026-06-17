# Encryption Strategy Decision

## Status
APPROVED

## Date
2026-02-14

## Context
WeredaConnect stores sensitive user data locally in Isar for offline-first functionality: cached credentials, financial info, personal data, and transaction history.

Ethiopian context: devices are often shared among family/cooperative members, low-end devices may lack hardware security modules, users may not use device lock screens.

## Decision

### 1. Database Encryption
**Technology**: Isar built-in encryption with AES-256

**Key Management**:
- 256-bit encryption key generated using cryptographically secure RNG
- Key stored in Android Keystore via `flutter_secure_storage`
- Key persists across app restarts but not device resets
- One key per device (not per user)

**Implementation**: See `mobile/lib/core/database/isar_service.dart`

### 2. Token Storage
**Technology**: `flutter_secure_storage` with Android Keystore backing

**Tokens stored**: Access token (JWT, 15-min), Refresh token (7-day), ID token (user profile)

**Implementation**: See `mobile/lib/features/auth/data/`

### 3. Password Hashing (Offline Auth)
**Technology**: PBKDF2 with HMAC-SHA256

**Parameters**:
- Iterations: 100,000 (OWASP recommended for 2026)
- Salt: 32 bytes, device-specific (stored in secure storage)
- Output: 32 bytes (256 bits)

**Why not bcrypt/scrypt?** PBKDF2 is FIPS-approved, widely supported in Flutter/Dart, and we control iteration count.

## Key Rotation Procedure

### When to Rotate
- Annually, in response to security incident, device transfer, or user request

### Rotation Steps
1. Ensure device is online (data will sync from server)
2. Export all collections from current encrypted database
3. Close database, delete old key, generate new key
4. Re-open database with new key, import all data
5. Verify record counts match, test critical flows, sync with server

**Automation**: Rotation can be triggered remotely. Should only occur when online and charging. UI shows progress bar. Rollback to old database on failure.

## Security Considerations

### Threats Mitigated
- Data at rest protection (encrypted database)
- Token theft (secure storage with Keystore)
- Shared device access (encryption prevents casual browsing)
- Device theft (data unreadable without unlock)

### Threats NOT Mitigated
- Root/jailbreak attacks, memory dumps, screen recording, device malware

### Mitigation for Unaddressed Threats
- Require device lock screen, app-level PIN/biometric, ProGuard obfuscation
- Detect rooted devices and warn users, implement certificate pinning

## Trade-offs

| Dimension | Impact |
|-----------|--------|
| **Performance** | ~5-10% slower DB operations, +50-100ms startup (key retrieval) |
| **Battery** | Negligible (AES is hardware-accelerated) |
| **UX positive** | Users feel more secure about data privacy |
| **UX negative** | Database unusable if key lost (factory reset). Mitigated by server sync. |
| **Dev complexity** | Isar encryption is transparent (no schema changes). Key rotation needs careful implementation. |

## Alternatives Considered

| Option | Decision | Reason |
|--------|----------|--------|
| No encryption | Rejected | Unacceptable security risk |
| SQLCipher (SQLite) | Rejected | Requires switching from Isar, more complex |
| File-level (Android encrypted storage) | Rejected | Only works when device locked, blocks background sync |
| User-provided password | Rejected | Poor UX, incompatible with background sync |

## Compliance
- Ethiopia lacks comprehensive data protection law as of 2026; following GDPR principles
- Not PCI applicable (Telebirr for payments, no card data)
- OWASP Mobile Top 10: M2 (Insecure Data Storage) mitigated, M4 (Insecure Auth) mitigated, M9 (Reverse Engineering) partially mitigated

## Approval
- [x] Architect: Approved
- [x] Security: Approved (see archived SECURITY_AUDIT_2026-02-10.md)
- [x] Mobile Agent: Implemented
