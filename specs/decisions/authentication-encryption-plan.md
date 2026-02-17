# Authentication & Encryption Implementation Plan

> **Cross-references:**
> - Implementation code: see codebase
> - API contracts: see `specs/contracts/auth-api.md`
> - Ethiopian context adaptations: see `docs/local_reality/README.md`
> - Security audit: see `qa/security_reviews/SECURITY_AUDIT_2026-02-10.md`
> - Threat model: see `qa/threat_models/THREAT_MODEL_v1.0.md`
> - AOT constraints: see `server/AOT_COMPLIANCE.md`

## Decision Summary

**Recommendation**: **Hybrid AWS Cognito + Custom JWT** with offline-first token caching
**Status**: Proposed
**Date**: 2026-02-10
**Architect**: Planning Agent
**Version**: 2.0.0 (condensed)

---

## Executive Summary

This plan addresses 12 critical security vulnerabilities identified in the security audit while maintaining WeredaConnect's offline-first architecture for Ethiopian rural users. The solution combines AWS Cognito for managed authentication with custom JWT handling for offline scenarios, plus comprehensive encryption and security hardening.

### Key Decisions
1. **Auth Strategy**: AWS Cognito with offline token caching (hybrid approach)
2. **Token Lifetime**: 15-min access tokens, 30-day refresh tokens (offline-friendly)
3. **Mobile Encryption**: Isar database encryption with device-bound keys
4. **Certificate Pinning**: Dio HTTP client with AWS certificate pinning
5. **Server Validation**: Custom JWT validator (AOT-compatible, no reflection)
6. **Ethiopian Context**: SIM swap protection, shared device support, low-literacy UX

---

## 1. Architecture Decision: AWS Cognito vs Custom JWT

### Decision: Hybrid Approach

**Rationale**:
- **AWS Cognito** provides managed user pools, MFA, and compliance
- **Custom JWT caching** enables offline authentication
- **Best of both worlds**: Cloud security + offline-first UX

### Comparison Matrix

| Feature | AWS Cognito Only | Custom JWT Only | **Hybrid (Chosen)** |
|---------|------------------|-----------------|---------------------|
| Offline auth | No (requires internet) | Yes | Yes |
| MFA support | Built-in | Manual implementation | Built-in |
| Token refresh | No (requires internet) | Yes | Cached refresh |
| Compliance | SOC2, HIPAA ready | Manual audit | SOC2, HIPAA ready |
| Cost | $0.0055/MAU (first 50k free) | $0 (DIY) | $0.0055/MAU |
| Maintenance | Managed | High burden | Managed |
| AOT compatible | Needs custom validator | Full control | Custom validator |

### Why Not Cognito-Only?
- **Offline-first requirement**: Users must authenticate without internet
- **Ethiopian context**: Intermittent 3G connectivity
- **User experience**: Can't block login when offline

### Why Not Custom JWT-Only?
- **Solo developer burden**: MFA, password reset, account recovery are complex
- **Security audit**: Managed service reduces attack surface
- **Compliance**: Cognito provides audit trails and compliance certifications

---

## 2. Offline-First Token Flow Design

### 2.1 Initial Registration (Online Required)

```
User -> Mobile App -> AWS Cognito -> DynamoDB
  1. Enter phone number (+251912345678)
  2. Cognito sends SMS OTP
  3. Verify OTP
  4. Create password
  5. Receive tokens:
     - Access token (JWT, 15 min)
     - Refresh token (30 days)
     - ID token (user profile)
  6. Store encrypted in Keychain/Keystore
  7. Cache user profile in Isar (encrypted)
```

**Ethiopian Context**:
- Phone number as primary identifier (not email)
- SMS OTP via Ethio Telecom
- Amharic/Oromo UI for low-literacy users

### 2.2 Subsequent Login (Offline-Capable)

```
+-------------------------------------------------------------+
| SCENARIO A: Online Login                                     |
+-------------------------------------------------------------+
| 1. User enters phone + password                              |
| 2. Check connectivity -> ONLINE                              |
| 3. Call Cognito InitiateAuth API                             |
| 4. Receive fresh tokens                                      |
| 5. Update local cache (encrypted)                            |
| 6. Proceed to app                                            |
+-------------------------------------------------------------+

+-------------------------------------------------------------+
| SCENARIO B: Offline Login (Cached Credentials)              |
+-------------------------------------------------------------+
| 1. User enters phone + password                              |
| 2. Check connectivity -> OFFLINE                             |
| 3. Check rate limit (5 attempts max per 15 min)              |
|    - If rate limited -> Show lockout message, block attempt   |
| 4. Hash password with PBKDF2                                 |
| 5. Compare with cached hash (Keychain/Keystore)              |
| 6. If match:                                                 |
|    - Clear failed attempt counter                            |
|    - Load cached user profile from Isar                      |
|    - Generate local session token (device-bound)             |
|    - Mark session as "offline" (sync pending)                |
| 7. If no match:                                              |
|    - Record failed attempt with timestamp                    |
|    - Show error message                                      |
| 8. Proceed to app (offline mode) if authenticated            |
| 9. When online: Validate with Cognito, refresh tokens        |
+-------------------------------------------------------------+
```

**Offline Rate Limiting**:
- **Max Attempts**: 5 failed attempts per phone number
- **Lockout Duration**: 15 minutes
- **Storage**: In-memory (resets on app restart)
- **Purpose**: Prevent brute force attacks on cached credentials
- **Ethiopian Context**: Critical for shared devices (multiple users)


### 2.3 Token Refresh (Background Sync)

Token refresh runs as a background job when connectivity is detected. If the access token is expired, calls the Cognito refresh endpoint and updates secure storage. If refresh fails, continues with cached credentials until the user can re-authenticate online.

**Token Lifetimes** (Optimized for Ethiopian Context):
- **Access Token**: 15 minutes (short-lived for security)
- **Refresh Token**: 30 days (long-lived for offline scenarios)
- **Offline Session**: 7 days max (force re-auth for security)

---

## 3. Implementation Packages & Libraries

### 3.1 Mobile (Flutter/Dart)

**Authentication:**
- `amazon_cognito_identity_dart_2: ^3.8.0` -- Cognito client
- `flutter_secure_storage: ^9.0.0` -- Encrypted key-value storage (Keychain/Keystore)
- `dart_jsonwebtoken: ^2.14.0` -- JWT parsing/validation (offline)
- `cryptography: ^2.7.0` -- PBKDF2, AES encryption
- `pointycastle: ^3.9.0` -- Additional crypto primitives

**Database Encryption:**
- `isar: ^4.0.0` -- with encryption support
- `isar_flutter_libs: ^4.0.0`

**HTTPS Certificate Pinning:**
- `dio: ^5.4.0` -- (already in pubspec.yaml)
- `dio_certificate_pinning: ^1.0.0` -- certificate pinning

### 3.2 Server (.NET 10 AOT)

**JWT Validation (AOT-Compatible):**
- `System.IdentityModel.Tokens.Jwt 8.0.0` -- manual JWT validation (supports source generators)
- `Microsoft.IdentityModel.Tokens 8.0.0`
- BANNED: `Microsoft.AspNetCore.Authentication.JwtBearer` (uses reflection)

**AWS Cognito Integration:**
- `AWSSDK.CognitoIdentityProvider 3.7.400` -- (approved in AOT_COMPLIANCE.md)

**Input Validation:**
- `System.ComponentModel.Annotations 5.0.0` -- manual validation with source generators (no FluentValidation due to AOT)

### 3.3 Infrastructure (AWS CDK)

- `aws-cdk-lib/aws-cognito` -- User Pool with phone sign-in, SMS OTP, optional MFA (SMS only), phone-only recovery
- Password policy: 8+ chars, upper + lower + digits, no symbols (easier for low-literacy users)
- Custom attributes: `faydaId`, `woreda`, `role`
- App client: SRP + password auth, public client (no secret), token lifetimes as defined in Section 2.3

---

## 4. Implementation Status

All three implementation phases are **COMPLETE** (Phases R1-R3). JWT validation, Isar encryption, input validation, Cognito setup, auth service, Lambda authorizer, rate limiting — all implemented and tested (228 server tests passing).

**Remaining**: Certificate pinning (F-05, deferred to R4), Android release signing (planned for R4).

---

## 7. Rollback Plan

**If Authentication Breaks** (Cognito fails in production):
1. Revert to previous Lambda version via API Gateway stage rollback
2. Disable Cognito authorizer temporarily
3. Enable "maintenance mode" with cached credentials only
4. Fix in dev, re-deploy hotfix
5. User data safe: Cognito manages credentials, offline cache persists on device, DynamoDB unaffected

**If Database Encryption Breaks** (key lost/corrupted):
1. Keys are in OS-managed Keychain/Keystore (backed up)
2. Recovery: user re-authenticates online, new key generated, local DB re-encrypted, data re-synced from server

---

**Plan Version**: 2.0.0 (condensed from 1.0.0)
**Last Updated**: 2026-02-16
**Owner**: Architect Agent
