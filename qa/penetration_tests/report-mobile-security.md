# Mobile Client & Flutter Security Audit (OWASP MASVS)

**Date**: 2026-11-19  
**Target**: WeredaConnect Mobile Client (`mobile/`)  
**Platform**: Flutter / Android / iOS / Web  
**Standards**: OWASP Mobile Application Security Verification Standard (MASVS v2.0)  
**Overall Posture**: **EXCELLENT** — Rating: **9.4 / 10**

---

## 1. Executive Summary

An in-depth security and adversarial client review was conducted across the Flutter mobile application in accordance with the OWASP MASVS standard. The assessment covered:
1. **Network Security & CA Pinning (MASVS-NETWORK)**
2. **Data Storage & At-Rest Cryptography (MASVS-STORAGE & MASVS-CRYPTO)**
3. **Authentication & Session Tokens (MASVS-AUTH)**
4. **Platform & Intent Security (MASVS-PLATFORM)**

All 105 dedicated mobile security test cases passed cleanly, and static analysis reported zero warnings (`flutter analyze` clean).

---

## 2. In-Depth Domain Assessments

### 2.1 Network & Certificate Pinning (MASVS-NETWORK) — **COMPLIANT**
- **Architecture**: The application uses custom CA pinning via Dart's `SecurityContext` in `mobile/lib/core/network/cert_pinning_io.dart`.
- **Implementation**:
  ```dart
  final context = SecurityContext()
    ..setTrustedCertificatesBytes(utf8.encode(AmazonTrustRoots.pem));
  final client = HttpClient(context: context);
  client.badCertificateCallback = (cert, host, port) => false; // Fails closed
  ```
- **Strengths**:
  - Unlike standard system trust stores that trust hundreds of global CAs (subject to compromise or local network interception in 3G/rural environments), WeredaConnect trusts **only** Amazon Trust Services roots.
  - Mitigates leaf certificate rotation bricking (which occurs with static leaf hashes) while completely eliminating proxy/MITM interception.
  - Live chain verification script (`scripts/check_cert_pins.sh`) routinely verifies that live API Gateway endpoints chain to the bundled roots.
- **Cleartext Traffic**: Android `network_security_config.xml` strictly enforces `cleartextTrafficPermitted="false"`.

---

### 2.2 Local Data Storage & Field-Level Encryption (MASVS-STORAGE) — **COMPLIANT**
- **Storage Engines**: Isar 3.1 local database + `FlutterSecureStorage`.
- **Encryption Architecture**:
  - Because Isar 3.1 does not provide native AES database encryption, WeredaConnect implements **Field-Level Authenticated Encryption** (`FieldEncryptor.dart`).
  - **Cipher**: AES-256-GCM using fresh 12-byte nonces generated per field write.
  - **AAD Binding**: Additional Authenticated Data is bound to `(collection, column, rowKey)`.
    - Prevents ciphertext transplant attacks (e.g. copying an encrypted balance or phone number into another user's row).
  - **Tamper & Downgrade Resistance**: Requires `enc:v1:` cipher prefix. Missing prefixes, truncated ciphertexts, or invalid MACs throw `FieldDecryptionException` and fail closed.
  - **Hardware Key Storage**: The AES-256 master key is managed via `FlutterSecureStorage` (backed by Android Keystore / iOS Keychain) with an HMAC integrity tag. Key corruption or tampering triggers immediate database wipe and re-authentication.

---

### 2.3 Token Security & Session Management (MASVS-AUTH) — **COMPLIANT**
- **Token Storage**: Mobile JWTs (access, id, refresh tokens) are stored in Android `EncryptedSharedPreferences` via `AndroidOptions(encryptedSharedPreferences: true)`.
- **Refresh Synchronization**: `TokenRefresh` implements lock synchronization to prevent concurrent token refresh collisions across background isolates or app lifecycles.
- **Biometric & Local Auth**: Biometric / PIN authentication (`LocalAuthGate`) protects critical actions like QR delivery confirmation and session unlock.

---

### 2.4 Android Manifest & Platform Hygiene (MASVS-PLATFORM) — **COMPLIANT**
- **Backup Prevention**:
  ```xml
  android:allowBackup="false"
  android:fullBackupContent="false"
  ```
  Prevents extraction of sensitive local app data via ADB backup or Google cloud backup.
- **Component Exposure**:
  - `MainActivity` is the single exported activity, limited to deep link verification schemes (`weredaconnect://fayda-callback`, `weredaconnect://payment`).
  - Background location is deliberately avoided; foreground service with persistent notification (`FOREGROUND_SERVICE_LOCATION`) is used only during active trip check-ins.

---

## 3. Automated Verification Results

- `flutter analyze`: **0 issues found**.
- `flutter test` (Security suite): **105 passed, 0 failures**.
  - Verified: Downgrade attack rejection, AAD cross-row/cross-column swap prevention, MAC tamper rejection, token refresh race conditions, and local auth lockouts.

---

## 4. Final Security Posture Summary

With Steps 1, 2, and 3 completed:
1. **Adversarial Personas (Step 1)**: Live dev tests confirmed detection and enforcement of duplicate crop spamming, GPS spoofing/teleportation jumps, and dispute freezes.
2. **API & Financial Escrow (Step 2)**: 9,223 tests passing; dual-pool Cognito isolation prevents unauthorized fund release; strict BOLA checks across all routes.
3. **Mobile Client (Step 3)**: 105 tests passing; hardware-backed token storage; AES-256-GCM field encryption with AAD; CA-pinned BoringSSL transport.
