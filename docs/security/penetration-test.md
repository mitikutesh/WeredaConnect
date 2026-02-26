# WeredaConnect Penetration Test Report

**Date**: 2026-02-24
**Assessor**: AI-assisted adversarial review (Claude Opus 4.6)
**Branch**: `telebirr-mock`
**Overall Safety Rating**: 5.5 / 10

---

## Executive Summary

WeredaConnect demonstrates good security awareness in several areas (HMAC-signed pagination tokens, idempotency keys, server-side escrow validation, PBKDF2 password hashing). However, there are significant gaps that an attacker would exploit, particularly around unauthenticated data exposure, a dangerous dormant code path, missing certificate pinning, and inconsistent security implementations across endpoints. For a financial platform handling real money (Telebirr escrow), the application is **not yet production-ready** from a security standpoint.

---

## Attack Surface Map

```
┌─────────────────────────────────────────────────────┐
│                  PUBLIC INTERNET                     │
├──────────┬──────────┬───────────┬───────────────────┤
│ /crops   │ /trips   │ /auth/*   │ /health           │
│ NO AUTH  │ NO AUTH  │ NO AUTH   │ NO AUTH            │
├──────────┴──────────┴───────────┴───────────────────┤
│              API Gateway (CORS: *)                   │
├──────────┬──────────┬───────────┬───────────────────┤
│/sync     │/deliver* │/escrow/*  │/auth/logout       │
│ JWT AUTH │ JWT AUTH │ JWT AUTH  │ JWT AUTH           │
├──────────┴──────────┴───────────┴───────────────────┤
│          Cognito HttpUserPoolAuthorizer              │
└─────────────────────────────────────────────────────┘
```

---

## CRITICAL Findings

### 1. Dormant Auth Bypass — JWT Signature Validation Disabled

- **File**: `server/src/Functions/AuthorizerFunction.cs:50` + `server/src/Auth/JwtValidator.cs:102-105`
- **Severity**: CRITICAL (dormant — HIGH risk of accidental activation)
- **Category**: Authentication Bypass

The custom Lambda authorizer skips JWT signature validation:

```csharp
// AuthorizerFunction.cs:50
_jwtValidator = new CognitoJwtValidator(userPoolId, region, clientId, isTestingEnvironment: true);

// JwtValidator.cs:102-105 — accepts ANY JWT when isTestingEnvironment=true
if (signingKey is null && isTestingEnvironment)
{
    _validationParameters.SignatureValidator = (token, _) => new JwtSecurityToken(token);
}
```

**Mitigating factor**: The CDK routes use `HttpUserPoolAuthorizer` (Cognito-managed), NOT this custom authorizer. The comment at line 47 says "This custom authorizer is not wired in CDK."

**Why it's still critical**: If anyone wires this authorizer into CDK (a single line change), ALL authenticated endpoints instantly become fully compromised. An attacker could forge any JWT with arbitrary `sub`, `custom:role`, and `custom:woreda` claims to impersonate any user, initiate payments, and release escrows.

**Exploit scenario**:
1. Attacker crafts a JWT: `{"sub": "victim-id", "custom:role": "admin", "custom:woreda": "any"}`
2. Signs it with any key (or none — signature is ignored)
3. Calls `POST /escrow/release` to release funds to themselves
4. Complete authentication bypass on ALL endpoints

**Recommendation**: Delete this class entirely or add a build-time guard that prevents `isTestingEnvironment: true` outside test projects.

---

### 2. Unauthenticated Crop & Trip Endpoints Expose Farmer PII

- **File**: `infra/lib/stacks/api-stack.ts:492-538` (CDK routes) + `server/src/Functions/CropFunction.cs:70`
- **Severity**: HIGH
- **Category**: Broken Access Control

Four endpoints have **no JWT authorizer** in the CDK configuration:

```typescript
// api-stack.ts:492-502 — NO authorizer
httpApi.addRoutes({
  path: "/crops",
  methods: [apigateway.HttpMethod.GET],
  integration: new integrations.HttpLambdaIntegration("CropsListInt", cropFn.function, ...),
  // ← no authorizer: jwtAuthorizer
});
```

Same for `/crops/{id}`, `/trips`, `/trips/{id}`.

**Attacker's playbook**:
1. Call `GET /crops?woreda=Addis+Ababa` — enumerate all farmers and their crop inventories, prices, quantities
2. Call `GET /trips?woreda=Addis+Ababa` — enumerate all truckers and their routes
3. Build a complete map of who is selling what, where, at what price
4. Use this intelligence for price manipulation, targeted scams, or physical theft of agricultural goods

**Impact**: In rural Ethiopian communities, knowing what a specific farmer has in stock, their location, and pricing is actionable intelligence for theft or scam operations.

**Recommendation**: Add `authorizer: jwtAuthorizer` to all four routes. If public browsing is a feature requirement, strip PII (farmer IDs, phone numbers) from unauthenticated responses.

---

### 3. Wildcard CORS Allows Cross-Origin Attacks

- **File**: `infra/lib/stacks/api-stack.ts:401-408`
- **Severity**: HIGH
- **Category**: Security Misconfiguration

```typescript
corsPreflight: {
  allowOrigins: ["*"],        // ← ANY website can call this API
  allowMethods: [apigateway.CorsHttpMethod.POST, apigateway.CorsHttpMethod.GET],
  allowHeaders: ["Content-Type", "Authorization"],
},
```

**Attacker's playbook**: Even though this is a mobile-first API, the wildcard CORS means any malicious website can make authenticated cross-origin requests if tokens are accessible. If a user's JWT is ever stored in a web context (admin dashboard, future web app), an attacker can create a phishing page that:
1. Reads the user's token
2. Calls `POST /escrow/initiate` to initiate fraudulent payments
3. Calls `POST /sync` with malicious data mutations

**Recommendation**: For a mobile-only API, either restrict CORS to your actual admin domain or remove it entirely:

```typescript
allowOrigins: ["https://admin.weredaconnect.com"],
```

---

## HIGH Findings

### 4. CropFunction Pagination Tokens Are Unsigned (Forgeable)

- **File**: `server/src/Functions/CropFunction.cs:288-307`
- **Severity**: HIGH
- **Category**: Broken Access Control / Inconsistent Security

CropFunction uses plain base64 tokens while Trip/Delivery functions use HMAC-signed tokens via `PaginationTokenHelper`:

```csharp
// CropFunction.cs:288-307 — VULNERABLE: plain base64, no HMAC
private static Dictionary<string, AttributeValue> DecodeNextToken(string nextToken)
{
    var bytes = Convert.FromBase64String(nextToken);
    var json = System.Text.Encoding.UTF8.GetString(bytes);
    var tokenData = JsonSerializer.Deserialize(json, ...);
    // No signature validation!
}
```

**Attacker's playbook**: Since `/crops` is also unauthenticated (Finding #2), an attacker can:
1. Forge pagination tokens to scan the entire DynamoDB crops table
2. Inject arbitrary DynamoDB `ExclusiveStartKey` values to start scanning from any position
3. Extract the complete crop listing database without rate limiting

**Recommendation**: Use `PaginationTokenHelper.DecodeNextToken()` consistently across all endpoints.

---

### 5. No Certificate Pinning — MITM on Ethiopian 3G Networks

- **File**: `mobile/lib/core/network/api_client.dart`
- **Severity**: HIGH
- **Category**: Insufficient Transport Layer Protection

Certificate pinning is explicitly disabled with placeholder hashes:

```dart
static const List<String> certificatePins = [
  'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', // Placeholder
  'sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=', // Placeholder
];
```

**Attacker's playbook**: On rural 3G networks common in Ethiopia:
1. Set up a rogue base station or ARP spoof on shared WiFi
2. Present a fraudulent TLS certificate (signed by a compromised CA)
3. Intercept all API traffic including JWT tokens, payment data, personal information
4. Use stolen tokens to impersonate users and manipulate escrow payments

**Impact**: Particularly dangerous because the app handles financial transactions (Telebirr escrow) and the target deployment environment (rural Ethiopia) has limited network security infrastructure.

**Recommendation**: Implement proper certificate pinning with real SHA-256 hashes from your API Gateway certificate before production deployment.

---

### 6. Hardcoded API Gateway URL in Client

- **File**: `mobile/lib/core/network/api_config.dart:20-23`
- **Severity**: MEDIUM-HIGH
- **Category**: Information Disclosure

```dart
static const String baseUrl = String.fromEnvironment(
  'API_BASE_URL',
  defaultValue: 'https://s0om0hib4i.execute-api.me-south-1.amazonaws.com',
);
```

The specific API Gateway ID (`s0om0hib4i`) is hardcoded. Any attacker who reverse-engineers the APK (trivial with `apktool`) immediately knows the exact API endpoint for:
- Reconnaissance and API mapping
- Automated attacks against specific endpoints
- DDoS targeting

**Recommendation**: Remove the hardcoded default. Require `--dart-define=API_BASE_URL` at build time with no fallback.

---

## MEDIUM Findings

### 7. Escrow Idempotency Race Window

- **File**: `server/src/Services/EscrowService.cs:77-90`
- **Severity**: MEDIUM
- **Category**: Race Condition / Double-Charge Risk

There's a TOCTOU window between checking the idempotency key and storing it:

```csharp
// Step 1: Check if exists (line 77)
var existing = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
if (existing != null) { return cached; }
// ... validation happens ...
// Step 2: Store (much later, after generating escrowId)
var stored = await _idempotencyRepo.StoreAsync(...);
```

**Mitigating factor**: The `StoreAsync` likely uses DynamoDB's `attribute_not_exists` conditional write, which provides eventual atomic protection. But the gap between check and store still allows a narrow race window.

**Exploit scenario**:
1. Buyer sends two simultaneous `POST /escrow/initiate` with same idempotency key
2. Both pass the `GetAsync()` check (key doesn't exist yet)
3. First request's `StoreAsync()` succeeds, second fails with conflict
4. But if the first request fails after Telebirr hold but before storing idempotency as "completed," retry logic may re-charge

**Recommendation**: Move the `StoreAsync` immediately after the initial `GetAsync` check, before any Telebirr calls.

---

### 8. Isar Database Unencrypted on Disk

- **File**: `mobile/lib/core/database/isar_service.dart:82-90`
- **Severity**: MEDIUM
- **Category**: Data Exposure

An encryption key is generated and stored but **never used**:

```dart
// Get or generate encryption key (for future use)
// Note: Isar 3.1 does not support built-in encryption.
await _getEncryptionKey();

_isar = await Isar.open([...], directory: dir.path);
// ← No encryption parameter
```

All user data (payments, deliveries, crop listings, personal info) is stored in plaintext on disk. On a stolen or rooted device, all data is immediately accessible.

**Recommendation**: Implement field-level encryption for sensitive Isar schemas using the `cryptography` package, or migrate to an encrypted database solution.

---

### 9. Password Temporarily Stored in Memory During Registration

- **File**: `mobile/lib/features/auth/bloc/auth_bloc.dart:39-46`
- **Severity**: MEDIUM
- **Category**: Sensitive Data Exposure

The code comment says "Password never stored in BLoC state" but the implementation contradicts it:

```dart
// SECURITY: Password never stored in BLoC state to prevent memory exposure
String? _pendingPassword;  // ← Actually stores password here
```

On a debuggable device or via memory dump, the password is accessible between registration initiation and OTP verification.

**Recommendation**: Don't store the raw password. Derive a key from it immediately or hash it, and clear the field in `AppLifecycleState.paused`.

---

### 10. 7-Day Offline Session with Weak Brute-Force Protection

- **File**: `mobile/lib/core/auth/offline_auth_cache.dart:42-46`
- **Severity**: MEDIUM
- **Category**: Insufficient Authentication Controls

```dart
static const _maxFailedAttempts = 5;
static const _lockoutDuration = Duration(minutes: 15);
static const _offlineSessionTtl = Duration(days: 7);
```

A stolen device gives an attacker 7 days of offline access. The 5-attempt / 15-minute lockout can be circumvented by clearing app data or waiting. No exponential backoff, no permanent lockout, no notification to the account owner.

**Recommendation**:
- Reduce offline session TTL to 24-48 hours
- Implement exponential backoff (1 min → 5 min → 15 min → 1 hour)
- Persist failed attempt counters across app reinstalls (via device keystore)
- After 10 total failures in 24 hours, require online re-authentication

---

## What's Done Well (Positive Findings)

| Area | Implementation | Rating |
|---|---|---|
| Escrow authorization | Server validates `buyerId == JWT sub` before initiation | Good |
| Release/Refund access control | `IsSystemOrAdmin()` check enforced server-side | Good |
| HMAC pagination tokens | Trip/Delivery use proper signed tokens via `PaginationTokenHelper` | Good |
| PBKDF2 password hashing | 100,000 iterations with secure random salt | Good |
| Idempotency keys | Format-validated, stored in DynamoDB with conditional writes | Good |
| Self-trade prevention | `buyerId != farmerId` enforced both client and server | Good |
| Fayda ID verification | High-value transactions require verified identity | Good |
| Amount validation | Server enforces 100-500,000 ETB bounds | Good |
| API Gateway JWT auth | Cognito `HttpUserPoolAuthorizer` on sensitive routes | Good |
| Fraud detection | Velocity checks, amount anomaly detection, device fingerprinting | Good |
| Rate limiting | Server-side rate limit service with DynamoDB backing | Good |

---

## Attacker Prioritized Kill Chain

If attacking this application today:

1. **Reconnaissance** (0 effort): Decompile APK → extract API Gateway URL from `api_config.dart`
2. **Data Harvesting** (0 auth needed): Call `GET /crops` and `GET /trips` with forged pagination tokens → build complete farmer/trucker database
3. **Network Interception** (moderate effort): MITM on 3G network → steal JWT tokens (no cert pinning)
4. **Account Takeover**: Use stolen JWT to call authenticated endpoints
5. **Financial Fraud**: If escalated to admin/system role somehow, release escrow funds

---

## Priority Remediation Roadmap

| Priority | Finding | Effort | Impact |
|---|---|---|---|
| **P0 — Before any production** | #5 Implement cert pinning | Medium | Blocks MITM on payment data |
| **P0 — Before any production** | #1 Delete dormant AuthorizerFunction or fix it | Low | Removes catastrophic risk |
| **P1 — Before launch** | #2 Add auth to crops/trips endpoints | Low | Stops unauthenticated data harvesting |
| **P1 — Before launch** | #3 Restrict CORS origins | Low | Blocks cross-origin attacks |
| **P1 — Before launch** | #4 Use HMAC pagination in CropFunction | Low | Consistent security model |
| **P1 — Before launch** | #6 Remove hardcoded API URL | Low | Basic OPSEC |
| **P2 — Near-term** | #7 Fix idempotency race | Medium | Prevents potential double-charges |
| **P2 — Near-term** | #8 Encrypt local database | Medium | Protects data on stolen devices |
| **P2 — Near-term** | #9 Clear password from memory | Low | Defense in depth |
| **P2 — Near-term** | #10 Strengthen offline brute-force protection | Low | Reduce stolen device risk |

---

## Remediation Tasks

### P0 — Before Any Production Deployment

- [x] **SEC-01: Delete or fix dormant AuthorizerFunction** (Finding #1)
  - **Owner**: Server
  - **Files**: `server/src/Functions/AuthorizerFunction.cs`, `server/src/Auth/JwtValidator.cs`
  - **Action**: Either delete `AuthorizerFunction` entirely (it's unused in CDK) or implement proper JWKS fetching from Cognito (`https://cognito-idp.{region}.amazonaws.com/{userPoolId}/.well-known/jwks.json`). Set `isTestingEnvironment: false` and provide a real `SecurityKey`.
  - **Acceptance**: No code path exists where `isTestingEnvironment: true` is used outside of unit tests.

- [ ] **SEC-02: Implement certificate pinning** (Finding #5)
  - **Owner**: Mobile
  - **Files**: `mobile/lib/core/network/api_client.dart`
  - **Action**: Replace placeholder SHA-256 hashes with real certificate pins from the API Gateway TLS certificate. Pin to at least 2 certificates (primary + backup for rotation). Enable the pinning code path.
  - **Acceptance**: App rejects connections with unknown certificates. Verified with a MITM proxy (e.g. Charles Proxy) — connection must fail.

### P1 — Before Launch

- [x] **SEC-03: Add authentication to crops/trips endpoints** (Finding #2)
  - **Owner**: Infra + Server
  - **Files**: `infra/lib/stacks/api-stack.ts` (lines 492-538)
  - **Action**: Add `authorizer: jwtAuthorizer` to the `/crops`, `/crops/{id}`, `/trips`, `/trips/{id}` routes in CDK. Alternatively, if public browsing is a product requirement, strip PII (farmer IDs, phone numbers, exact locations) from unauthenticated responses in `CropFunction` and `TripFunction`.
  - **Acceptance**: Unauthenticated `GET /crops` returns 401, or returns data with no PII.

- [x] **SEC-04: Restrict CORS to specific origins** (Finding #3)
  - **Owner**: Infra
  - **Files**: `infra/lib/stacks/api-stack.ts` (lines 401-408)
  - **Action**: Replace `allowOrigins: ["*"]` with an explicit list of allowed origins (e.g. `["https://admin.weredaconnect.com"]`). If no web frontend exists, remove `corsPreflight` entirely — mobile apps don't use CORS.
  - **Acceptance**: `OPTIONS` preflight from an unknown origin returns no `Access-Control-Allow-Origin` header.

- [x] **SEC-05: Use HMAC-signed pagination tokens in CropFunction** (Finding #4)
  - **Owner**: Server
  - **Files**: `server/src/Functions/CropFunction.cs` (lines 288-307)
  - **Action**: Replace the local `DecodeNextToken`/`EncodeNextToken` methods with `PaginationTokenHelper.DecodeNextToken()` and `PaginationTokenHelper.EncodeNextToken()` (already used by Trip/Delivery functions). Delete the local methods.
  - **Acceptance**: Tampered base64 pagination tokens are rejected with `400 CROP_VALIDATION`. Only HMAC-signed tokens are accepted.

- [x] **SEC-06: Remove hardcoded API Gateway URL** (Finding #6)
  - **Owner**: Mobile
  - **Files**: `mobile/lib/core/network/api_config.dart` (lines 20-23)
  - **Action**: Remove the `defaultValue` from `String.fromEnvironment('API_BASE_URL')`. Add a startup check in `main.dart` that fails fast if `API_BASE_URL` is empty. Document the required `--dart-define` flags in the build instructions.
  - **Acceptance**: Building the app without `--dart-define=API_BASE_URL=...` produces a clear error, not a silent fallback.

### P2 — Near-Term Hardening

- [ ] **SEC-07: Fix escrow idempotency race condition** (Finding #7)
  - **Owner**: Server
  - **Files**: `server/src/Services/EscrowService.cs` (lines 77-90, 184-191)
  - **Action**: Restructure the flow to: (1) Generate escrowId, (2) `StoreIfNotExists(idempotencyKey, escrowId, "processing")`, (3) Only if store succeeds, proceed to Telebirr call. This makes the idempotency check and claim atomic via DynamoDB's `attribute_not_exists` conditional write.
  - **Acceptance**: Two simultaneous requests with the same idempotency key result in exactly one Telebirr `HoldFunds` call.

- [ ] **SEC-08: Encrypt local database or sensitive fields** (Finding #8)
  - **Owner**: Mobile
  - **Files**: `mobile/lib/core/database/isar_service.dart`, `mobile/lib/core/database/schemas/*.dart`
  - **Action**: Since Isar 3.1 doesn't support built-in encryption, implement field-level encryption for sensitive data (payment amounts, phone numbers, addresses) using the `encrypt` or `cryptography` package. Use the already-generated encryption key from `FlutterSecureStorage`.
  - **Acceptance**: Extracting the Isar database file from a rooted device does not reveal plaintext PII or financial data.

- [x] **SEC-09: Clear password from AuthBloc memory** (Finding #9)
  - **Owner**: Mobile
  - **Files**: `mobile/lib/features/auth/bloc/auth_bloc.dart` (lines 39-46)
  - **Action**: (1) Remove the misleading security comment. (2) Clear `_pendingPassword` immediately after OTP verification completes (both success and failure). (3) Clear all `_pending*` fields on `AppLifecycleState.paused`. (4) Consider hashing the password immediately on receipt rather than storing it raw.
  - **Acceptance**: After OTP verification, `_pendingPassword` is null. Memory inspection shows no plaintext password after registration flow completes.

- [x] **SEC-10: Strengthen offline brute-force protection** (Finding #10)
  - **Owner**: Mobile
  - **Files**: `mobile/lib/core/auth/offline_auth_cache.dart` (lines 42-46, 137-144)
  - **Action**: (1) Reduce `_offlineSessionTtl` from 7 days to 24-48 hours. (2) Implement exponential backoff: 1 min → 5 min → 15 min → 1 hour. (3) Persist failed attempt counter in `FlutterSecureStorage` (survives app reinstall). (4) After 10 total failures in 24 hours, require online re-authentication.
  - **Acceptance**: After 5 failed offline attempts, lockout duration increases exponentially. Counter persists across app restarts. After 10 failures, offline login is disabled until next online auth.

### Stretch — Future Hardening

- [ ] **SEC-11: Add expiration to pagination tokens**
  - **Owner**: Server
  - **Files**: `server/src/Helpers/PaginationTokenHelper.cs`
  - **Action**: Include a Unix timestamp in the HMAC payload. In `DecodeNextToken`, reject tokens older than 24 hours.
  - **Acceptance**: A pagination token captured and replayed after 24 hours is rejected.

- [ ] **SEC-12: Add version-based optimistic locking to escrow mutations**
  - **Owner**: Server
  - **Files**: `server/src/Services/EscrowService.cs`
  - **Action**: Include `version` in Release/Dispute/Refund requests. Before state transitions, validate `request.version == currentEscrow.version`. Use DynamoDB conditional update with version check.
  - **Acceptance**: Two concurrent release requests on the same escrow — only one succeeds, the other returns `409 ESCROW_VERSION_CONFLICT`.

- [ ] **SEC-13: Implement QR nonce server-side validation**
  - **Owner**: Server + Mobile
  - **Files**: `server/src/Functions/SyncFunction.cs`, `mobile/lib/features/delivery_verification/data/services/qr_verification_service.dart`
  - **Action**: Store the QR nonce in the delivery record when generated. During sync, validate that the submitted nonce matches the stored one and has not expired (1-hour TTL). Reject mismatches.
  - **Acceptance**: Replaying a previously-used QR code nonce returns an error. Expired nonces (>1 hour) are rejected.

---

## Methodology

- **Scope**: Full-stack review (Flutter mobile, .NET server, AWS CDK infrastructure)
- **Approach**: White-box source code analysis with adversarial mindset
- **Tools**: Static analysis of source code, CDK configuration review, API route mapping
- **Focus areas**: Authentication/authorization, payment security, data exposure, transport security
- **Exclusions**: Physical security, social engineering, third-party dependency CVEs

---

*Report generated 2026-02-24. Re-assess after P0/P1 remediations are complete.*
