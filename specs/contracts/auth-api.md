# Authentication API Contract

## Version
2.11.0

## Parties
- **Mobile** (client) -- registers users, authenticates online/offline, manages cached tokens
- **Backend** (server) -- proxies to AWS Cognito, validates JWTs, enforces rate limits
- **Infrastructure** (CDK) -- provisions Cognito User Pool, API Gateway throttling, WAF rules

---

## Overview

The authentication API enables phone-based registration and login for Ethiopian users. It uses a hybrid approach: AWS Cognito for online authentication and PBKDF2-based offline verification for cached credentials. All endpoints are accessed via API Gateway with rate limiting enforced at the gateway level.

**Identity verification** (2.2.0): every account is created through **Fayda OIDC**; `POST /auth/register` rejects a request without a `faydaVerificationToken` (`AUTH_021`). Phone ownership is always proven, by an SMS code (`POST /auth/verify-otp`) unless **Fayda OIDC** (Ethiopia's national digital ID) released the same phone number. `POST /user/fayda-link` remains for accounts created before 2.2.0. The 2.0.0 statement that Fayda replaces SMS OTP with no fallback is superseded.

**Base path**: `/auth`

---

## Endpoints

### 1. POST /auth/register

Register a new user with Ethiopian phone number, password, role, woreda, and Fayda verification token.

**Prerequisites**: User must first verify identity via `POST /auth/fayda-verify` to obtain a `faydaVerificationToken`.

**Authentication**: None (public endpoint)

#### Request Body

```json
{
  "phoneNumber": "+251912345678",
  "password": "securePass1",
  "role": "farmer",
  "woreda": "Adama",
  "displayName": "Abebe Kebede",
  "preferredLanguage": "am",
  "faydaVerificationToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6..."
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `phoneNumber` | string | Yes | Ethiopian format: `+251` followed by 9 digits. Regex: `^\+251[0-9]{9}$` |
| `password` | string | Yes | Minimum 8 characters, maximum 128 characters. Must contain at least one lowercase letter and one digit |
| `role` | string | Yes | Enum: `farmer`, `trucker`, `buyer` |
| `woreda` | string | Yes | Non-empty, maximum 100 characters (administrative district name) |
| `displayName` | string | No | Maximum 100 characters. Supports Amharic/Oromo Unicode. |
| `preferredLanguage` | string | No | Enum: `en`, `am`, `om`. Default: `am` |
| `faydaVerificationToken` | string | Yes | Short-lived JWT from `POST /auth/fayda-verify` (30-minute validity). Missing or blank: `400 AUTH_021` (2.2.0) |
| `buyerType` | string | No | **Buyer only.** Enum: `private`, `company`. Must be absent for non-buyer roles. |
| `companyName` | string | No | **Buyer only.** Required when `buyerType` is `company`; rejected otherwise. Maximum 100 characters. |

> **Note (F-32)**: `buyerType`/`companyName` apply only to `buyer` registrations and are echoed back in the response `user` object. The trucker driving-license is intentionally **not** part of this contract — it is captured and kept **local-only on the device** (Isar) until a server-side image-upload pipeline exists.

#### Success Response

**Status**: `201 Created`

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "phoneNumber": "+251912345678",
  "tokens": {
    "accessToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "refreshToken": "eyJjdHkiOiJKV1QiLCJlbmMiOi...",
    "idToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "expiresIn": 900,
    "tokenType": "Bearer"
  },
  "user": {
    "userId": "550e8400-e29b-41d4-a716-446655440000",
    "phoneNumber": "+251912345678",
    "displayName": "Abebe Kebede",
    "role": "farmer",
    "woreda": "Adama",
    "isPhoneVerified": true,
    "faydaVerified": true,
    "faydaVerifiedAt": "2026-03-02T10:00:00Z",
    "createdAt": "2026-03-02T10:00:00Z"
  }
}
```

**Note** (superseded in 2.1.0): tokens are returned directly (`201`) only when the Fayda proof carries the registration phone; otherwise registration answers `202 otpRequired` and the SMS code step follows. See "Additions in 2.1.0".

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `AUTH_001` | Invalid phone number format |
| `400` | `AUTH_002` | Password does not meet requirements |
| `400` | `AUTH_003` | Invalid role (must be farmer, trucker, or buyer) |
| `400` | `AUTH_004` | Woreda is required |
| `400` | `AUTH_021` | Missing Fayda verification token (required since 2.2.0) |
| `409` | `AUTH_005` | Phone number already registered |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |
| `503` | `AUTH_008` | Cognito service unavailable |

**Error body format**:

```json
{
  "error": {
    "code": "AUTH_001",
    "message": "Invalid phone number format. Must be +251 followed by 9 digits.",
    "field": "phoneNumber"
  }
}
```

---

### 2. POST /auth/fayda-verify

Exchange a Fayda OIDC authorization code for a short-lived verification token. This endpoint is called after the user completes Fayda eSignet authentication in their browser.

**Authentication**: None (public endpoint)

#### Request Body

```json
{
  "authorizationCode": "abc123...",
  "state": "random-csrf-state",
  "redirectUri": "weredaconnect://fayda-callback"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `authorizationCode` | string | Yes | OIDC authorization code from Fayda callback |
| `state` | string | Yes | CSRF state parameter (must match the value sent in the authorization request) |
| `redirectUri` | string | Yes | Must match the redirect URI used in the authorization request |

#### Success Response

**Status**: `200 OK`

```json
{
  "verified": true,
  "faydaId": "FAYDA-123456789",
  "verifiedName": "Abebe Kebede",
  "verificationToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6...",
  "expiresAt": "2026-03-02T10:30:00Z"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `verified` | boolean | Always `true` on success |
| `faydaId` | string | User's Fayda national ID |
| `verifiedName` | string | Full name from Fayda identity record |
| `verificationToken` | string | Short-lived JWT (30 min) to include in registration request |
| `expiresAt` | string | ISO 8601 expiration timestamp of the verification token |

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `FAYDA_001` | Invalid or expired authorization code |
| `400` | `FAYDA_002` | State parameter mismatch (possible CSRF attack) |
| `503` | `FAYDA_003` | Fayda service unavailable |
| `409` | `FAYDA_004` | Fayda ID already linked to another account |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |

**Dev bypass**: When `DEV_FAYDA_BYPASS=true` (non-production), sending `authorizationCode: "test-code-123"` returns a mock verification token with `faydaId: "FAYDA-DEV-001"` and `verifiedName: "Test User"`.

---

### 3. POST /auth/login

Authenticate with phone number and password. Returns access, refresh, and ID tokens.

**Authentication**: None (public endpoint)

#### Request Body

```json
{
  "phoneNumber": "+251912345678",
  "password": "securePass1",
  "deviceFingerprint": "a1b2c3d4e5f6..."
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `phoneNumber` | string | Yes | Ethiopian format: `^\+251[0-9]{9}$` |
| `password` | string | Yes | Non-empty |
| `deviceFingerprint` | string | No | SHA-256 hash of device identifiers (for SIM swap detection, future use) |

#### Success Response

**Status**: `200 OK`

```json
{
  "tokens": {
    "accessToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "refreshToken": "eyJjdHkiOiJKV1QiLCJlbmMiOi...",
    "idToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "expiresIn": 900,
    "tokenType": "Bearer"
  },
  "user": {
    "userId": "550e8400-e29b-41d4-a716-446655440000",
    "phoneNumber": "+251912345678",
    "displayName": "Abebe Kebede",
    "role": "farmer",
    "woreda": "Adama",
    "isPhoneVerified": true,
    "createdAt": "2026-02-12T10:00:00Z"
  }
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `AUTH_001` | Invalid phone number format |
| `401` | `AUTH_014` | Invalid credentials (wrong phone or password) |
| `403` | `AUTH_015` | Account locked (too many failed attempts -- 15 minute lockout) |
| `403` | `AUTH_016` | Phone number not verified (OTP not completed) |
| `403` | `AUTH_025` | Account suspended by an administrator |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |
| `503` | `AUTH_008` | Cognito service unavailable |

**Security note**: The `AUTH_014` error intentionally does not reveal whether the phone number exists or the password is wrong. This prevents phone number enumeration attacks.

---

### 4. POST /auth/refresh

Refresh an expired access token using a valid refresh token.

**Authentication**: None (uses refresh token in body)

#### Request Body

```json
{
  "refreshToken": "eyJjdHkiOiJKV1QiLCJlbmMiOi..."
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `refreshToken` | string | Yes | Valid Cognito refresh token |

#### Success Response

**Status**: `200 OK`

```json
{
  "tokens": {
    "accessToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "idToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6...",
    "expiresIn": 900,
    "tokenType": "Bearer"
  }
}
```

**Note**: Cognito does not issue a new refresh token on refresh. The original refresh token remains valid until its 30-day expiry. The client must store and reuse it.

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `AUTH_017` | Missing refresh token |
| `401` | `AUTH_018` | Refresh token expired (30-day TTL exceeded) |
| `401` | `AUTH_019` | Refresh token revoked (user logged out or password changed) |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |

---

### 5. POST /auth/logout

Revoke the caller's own refresh token. The access token remains valid until its 15-minute TTL expires (stateless JWT).

**Authentication**: Required (Bearer access token)

Since 2.7.0 the refresh token in the body must belong to the signed-in user. The server resolves its owner with Cognito `InitiateAuth` (`REFRESH_TOKEN_AUTH`) and compares the minted id token's `sub` with the bearer's `sub`; the minted tokens are discarded, never returned or logged. A token that is unknown, expired or already revoked is reported as logged out (`200`, nothing revoked), so logout stays idempotent. Before 2.7.0 any authenticated user could revoke any refresh token of the app client.

#### Request Headers

```
Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6...
```

#### Request Body

```json
{
  "refreshToken": "eyJjdHkiOiJKV1QiLCJlbmMiOi..."
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `refreshToken` | string | Yes | The refresh token to revoke |

#### Success Response

**Status**: `200 OK`

```json
{
  "loggedOut": true,
  "message": "Refresh token revoked. Access token remains valid until expiry."
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `AUTH_017` | Missing refresh token |
| `401` | `AUTH_020` | Invalid or expired access token, or no caller identity in the request |
| `403` | `AUTH_026` | The refresh token belongs to another user; nothing revoked (2.7.0). The app retries once with a bearer minted from the refresh token (2.9.0) |
| `429` | `AUTH_006` | Rate limit exceeded (API), or Cognito throttled the owner lookup or the revoke; nothing is revoked when the lookup is throttled |
| `500` | `AUTH_007` | Internal server error |
| `503` | `AUTH_008` | Cognito unavailable during the owner lookup or the revoke; nothing is revoked when the lookup fails (2.7.0) |

Clients treat every logout error as non-fatal and clear the local session anyway (the token expires on its own).

#### Client sequence (2.9.0)

1. The app reads the live access and refresh tokens from storage (the token-refresh interceptor rewrites those slots; the copy held since login is stale after 15 minutes) and starts the call before it clears the local session. The call never reads or writes the stored token slots and bypasses the interceptor, and the local clear never waits on the call: logout stays local-first, and the app stops waiting after 5 seconds.
2. When the access token is missing or past its `exp` (30-second margin), the app first mints one with `POST /auth/refresh` and the same refresh token. Minted tokens serve this call only and are never stored. A refused refresh means the refresh token is already inactive, so nothing is sent.
3. A `401` (the gateway answers `{"message":"Unauthorized"}` to an expired bearer) or a `403 AUTH_026` on a held bearer is retried once with a minted bearer, which always belongs to the refresh token's owner. Other errors are not retried.
4. Offline, or without a refresh token, nothing is sent.

Before 2.9.0 the app cleared the session first and sent the access token from login, so any session older than 15 minutes got a `401` and its refresh token stayed live until it expired (SIM-55).

---

## Token Configuration

### Access Token (JWT)

| Property | Value |
|----------|-------|
| Format | JWT (RS256, Cognito-issued) |
| TTL | 15 minutes |
| Issuer | `https://cognito-idp.{region}.amazonaws.com/{userPoolId}` |
| Audience | Cognito App Client ID |
| Transport | `Authorization: Bearer {token}` header |

#### Required JWT Claims

| Claim | Type | Description |
|-------|------|-------------|
| `sub` | string (UUID) | Cognito user subject identifier (maps to userId) |
| `phone_number` | string | User's phone number in E.164 format |
| `custom:role` | string | User role: `farmer`, `trucker`, or `buyer` |
| `custom:woreda` | string | User's administrative district |
| `iss` | string | Cognito User Pool issuer URL |
| `aud` | string | Cognito App Client ID |
| `token_use` | string | Must be `access` |
| `iat` | number | Issued-at timestamp (Unix epoch) |
| `exp` | number | Expiration timestamp (Unix epoch) |
| `auth_time` | number | Time of original authentication (Unix epoch) |

### Refresh Token

| Property | Value |
|----------|-------|
| Format | Opaque (Cognito-managed, encrypted) |
| TTL | 30 days |
| Revocation | On logout, on password change, on admin revoke |
| Rotation | Cognito does not rotate refresh tokens on use |

### ID Token (JWT)

| Property | Value |
|----------|-------|
| Format | JWT (RS256, Cognito-issued) |
| TTL | 15 minutes |
| Purpose | Contains user profile claims for the mobile client |
| Usage | Client-side only. Never sent to backend API. |

#### ID Token Additional Claims

| Claim | Type | Description |
|-------|------|-------------|
| `phone_number_verified` | boolean | Whether phone OTP was completed |
| `custom:faydaId` | string | Ethiopian national Fayda ID (if provided) |
| `name` | string | Display name |

---

## Offline Authentication Contract

### What the Mobile Client Caches

After a successful online login or registration, the mobile client stores the following in encrypted secure storage (Android Keystore / iOS Keychain):

| Data | Storage | Purpose |
|------|---------|---------|
| PBKDF2-hashed password | `flutter_secure_storage` | Offline password verification |
| Access token (JWT) | `flutter_secure_storage` | API calls when token is still valid |
| Refresh token | `flutter_secure_storage` | Token refresh when connectivity returns |
| ID token (JWT) | `flutter_secure_storage` | User profile data for offline display |
| User profile (full) | Isar (encrypted) | Display user info offline |
| Offline session timestamp | `flutter_secure_storage` | Track offline session duration |

### PBKDF2 Password Hash Parameters

| Parameter | Value |
|-----------|-------|
| Algorithm | PBKDF2 with HMAC-SHA256 |
| Iterations | 100,000 |
| Output length | 256 bits (32 bytes) |
| Salt | Device-specific, generated once, stored in secure storage |
| Encoding | Base64 |

### Offline Login Flow

```
1. User enters phone + password
2. Mobile checks connectivity --> OFFLINE
3. Retrieve cached PBKDF2 salt from secure storage
4. Hash input password with PBKDF2 (same parameters)
5. Compare with cached PBKDF2 hash
6. If match:
   a. Load cached user profile from Isar
   b. Load cached tokens from secure storage
   c. Mark session as "offline" (for sync engine awareness)
   d. Record offline login timestamp
   e. Proceed to app in offline mode
7. If no match:
   a. Return AUTH_014 (invalid credentials)
   b. Increment offline attempt counter
   c. Exponential backoff: 1st fail → 1min, 2nd → 5min, 3rd → 15min, 4th → 1hr lockout
   d. After 5 failed offline attempts: clear credentials, require online login
```

### Cache TTL and Invalidation

| Rule | Value | Rationale |
|------|-------|-----------|
| Maximum offline session | 48 hours | Security: force online re-auth after 48 hours (stolen-device mitigation) |
| Cache invalidated on | Logout, password change, 48-hour expiry, 5 failed attempts | Prevent stale/brute-forced credentials |
| Refresh token auto-refresh | Background job when connectivity detected | Keep tokens fresh |
| Offline attempt lockout | Exponential backoff from 1st failure; 5th failure = permanent lockout | Stolen-device mitigation |

### Offline Session Expiry Flow

```
1. User opens app (offline)
2. Check offlineSessionTimestamp
3. If now - offlineSessionTimestamp > 48 hours:
   a. Clear cached credentials
   b. Show "Session expired. Please connect to the internet to log in."
   c. Block app access until online login succeeds
4. If within 48 hours:
   a. Allow offline login as normal
```

### Reconnection Behavior

```
1. Connectivity detected (background listener)
2. Check if access token is expired
3. If expired: call POST /auth/refresh with cached refresh token
4. If refresh succeeds: update cached tokens
5. If refresh fails (token revoked/expired):
   a. Clear cached tokens
   b. Prompt user to re-authenticate online
   c. Do NOT clear Isar data (preserves offline work)
6. Process pending sync queue with fresh tokens
```

---

## Rate Limiting

### API Gateway Level

| Endpoint | Rate Limit | Burst Limit | Per |
|----------|-----------|-------------|-----|
| `POST /auth/register` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/fayda-verify` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/login` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/refresh` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/logout` | 10 req/s | 20 | Account (IP-based) |

### Application Level (Server-Enforced)

| Rule | Configuration | Action |
|------|---------------|--------|
| Failed login lockout | 3 consecutive failures | 15-minute account lockout |
| Fayda verify flood | 10 per IP per hour | Prevent Fayda API abuse |
| Registration flood | 5 registrations per phone per day | Block further attempts |

### WAF Level (Optional, Recommended)

| Rule | Configuration |
|------|---------------|
| IP rate limit | 2,000 requests per 5 minutes per IP |
| Geo restriction | Allow Ethiopia (+251) phone numbers only for registration |

---

## Error Code Reference

| Code | HTTP Status | Description | Client Action |
|------|------------|-------------|---------------|
| `AUTH_001` | 400 | Invalid phone number format | Show validation error on phone field |
| `AUTH_002` | 400 | Password does not meet requirements | Show password requirements |
| `AUTH_003` | 400 | Invalid role | Show role selection |
| `AUTH_004` | 400 | Woreda is required | Show woreda field error |
| `AUTH_005` | 409 | Phone number already registered | Suggest login instead |
| `AUTH_006` | 429 | Rate limit exceeded | Back off, retry with exponential delay |
| `AUTH_007` | 500 | Internal server error | Show generic error, retry |
| `AUTH_008` | 503 | Cognito service unavailable | Show connectivity error, suggest offline login |
| `AUTH_013` | 404 | User/registration not found | Suggest re-registration |
| `AUTH_014` | 401 | Invalid credentials; also login or refresh of a deleted account (2.11.0) | Show generic "incorrect phone or password" |
| `AUTH_015` | 403 | Account locked (15-min lockout) | Show lockout timer |
| `AUTH_016` | 403 | Phone not verified | Resend the SMS code (`POST /auth/resend-otp`) and open the OTP screen (2.1.0) |
| `AUTH_017` | 400 | Missing refresh token | Redirect to login |
| `AUTH_018` | 401 | Refresh token expired | Redirect to login |
| `AUTH_019` | 401 | Refresh token revoked | Redirect to login |
| `AUTH_020` | 401 | Invalid access token | Attempt token refresh, then redirect to login |
| `AUTH_021` | 400 | Missing Fayda verification token on register (required since 2.2.0) | Send the user back to the Fayda step |
| `AUTH_025` | 403 | Account suspended (login, verify-otp, refresh) | Show "contact support", clear the session |
| `AUTH_026` | 403 | Refresh token belongs to another user (logout, 2.7.0) | Retry the logout once with a bearer minted from the refresh token (2.9.0), then clear the local session anyway |
| `ACCOUNT_004` | 410 | `/user/fayda-start` or `/user/fayda-link` by an account that is being or has been deleted; carries `error.deletionState` (2.11.0, `account-deletion.md`) | `in_progress`: offer "Finish deleting"; otherwise wipe local data and go to login |
| `AUTH_009` | 400 | OTP code missing (2.1.0) | Show validation error on the code field |
| `AUTH_011` | 400 | OTP code format invalid, not 6 digits (2.1.0) | Show validation error on the code field |
| `AUTH_012` | 400 | OTP code invalid, expired or attempts exhausted (2.1.0) | Offer resend |
| `AUTH_040` | 403 | Step-up required: password older than the step-up window for this amount (2.1.0), or at any amount when the account's risk tier is MEDIUM or HIGH (2.6.0; built in 2.10.0, FRD-04) | Ask for the password, log in again, retry |
| `AUTH_041` | 403 | Fayda step-up required: a payment above 10,000 ETB needs a Fayda login within the step-up window; at risk tier LOW a password does not count (2.4.0). Also at any amount when the account's risk tier is HIGH; at MEDIUM and HIGH `AUTH_040` follows on the retry (2.6.0; built in 2.10.0, FRD-04). The message names no amount (2.10.0) | Open the Fayda flow (`/user/fayda-start`, `/user/fayda-link`), retry |
| `FRAUD_ACCOUNT_HELD` | 403 | The account is under fraud review (risk tier CRITICAL): the money or create action is refused and nothing is written (2.6.0; built in 2.10.0, FRD-04). Owner: `specs/architecture/fraud-detection-architecture.md` section 17 | Show the message, do not retry, point to support |
| `FAYDA_001` | 400 | Invalid or expired authorization code | Show error, offer retry |
| `FAYDA_002` | 400 | State unknown, expired, reused or foreign; redirect or nonce mismatch (2.1.0) | Show error, restart Fayda flow |
| `FAYDA_003` | 503 | Fayda service unavailable | Show "Fayda unavailable", suggest retry later |
| `FAYDA_004` | 409 | Fayda ID already linked to another account (2.2.0: an abandoned, never-confirmed sign-up does not count) | Show "already registered" message |
| `FAYDA_005` | 403 | fayda-link: the account already holds a different Fayda ID (2.3.0) | Show "does not match your account"; nothing changed |
| `FAYDA_006` | 403 | fayda-link: the Fayda ID's phone number is not the account's phone (2.3.0) | Show "phone does not match"; nothing changed |
| `FAYDA_007` | 403 | fayda-link: Fayda released no phone number and the account has no Fayda ID, so nothing ties the identity to it (2.3.0) | Show "could not confirm"; nothing changed |

---

## Security Requirements

### Phone Number Validation

- Format: `+251` followed by exactly 9 digits
- Regex: `^\+251[0-9]{9}$`
- Total length: 13 characters
- Only Ethiopian numbers accepted for registration
- Phone number is the primary login identifier (not email)

### Password Policy

| Requirement | Value | Rationale |
|-------------|-------|-----------|
| Minimum length | 6 characters | Low-literacy consideration -- keep barrier low |
| Maximum length | 128 characters | Prevent DoS via extremely long passwords |
| Complexity | None required | Low-literacy users struggle with mixed-case and symbols |
| Unicode support | Yes | Allow Amharic/Oromo characters in passwords |

**Note**: The Cognito User Pool password policy may be stricter (e.g., 8 chars, mixed case) for cloud-side storage. The mobile client should guide users to meet the Cognito requirements while keeping the UX simple. If the PO decides to lower Cognito's policy to 6 chars with no complexity, this contract supports that.

### Fayda Verification Configuration

| Parameter | Value |
|-----------|-------|
| Protocol | OIDC Authorization Code Flow via Fayda eSignet |
| Verification token validity | 30 minutes |
| Verification token signing | HMAC-SHA256 with server-managed secret |
| Redirect URI | `weredaconnect://fayda-callback` (web app, mock only: `https://<webapp>/fayda-callback.html`) |
| Required scopes | `openid`, `profile` |
| Dev bypass code | `test-code-123` (when `DEV_FAYDA_BYPASS=true`) |

### Transport Security

- All endpoints served over HTTPS (TLS 1.2+)
- Certificate pinning on mobile client (AWS API Gateway / CloudFront certificate)
- No sensitive data in URL query parameters (always use request body)
- No sensitive data in server logs (mask phone numbers, omit passwords and tokens)

### Token Security

- Access tokens are stateless JWTs validated via Cognito JWKS
- Refresh tokens are opaque and validated by Cognito server-side
- Server validates JWT signature, issuer, audience, expiry, and `token_use` claim
- Server JWT validator is AOT-compatible (no reflection)
- Tokens stored in `flutter_secure_storage` (Android Keystore / iOS Keychain)
- Tokens never stored in Isar database or SharedPreferences
- Tokens never logged or transmitted to analytics

---

## Server Implementation Notes

### JWT Validation (AOT-Compatible)

The server must validate JWTs without reflection-heavy libraries. Use:

```
System.IdentityModel.Tokens.Jwt (8.x)
Microsoft.IdentityModel.Tokens (8.x)
```

Do **not** use: `Microsoft.AspNetCore.Authentication.JwtBearer` (banned per `AOT_COMPLIANCE.md`).

### Cognito Integration

- Use `AWSSDK.CognitoIdentityProvider` for Cognito API calls
- Registration flow (2.1.0): `SignUp`, then either `AdminConfirmSignUp` + `AdminUpdateUserAttributes` (phone_number_verified=true) + `AdminInitiateAuth` (ADMIN_USER_PASSWORD_AUTH) when the Fayda proof carries the registration phone, or a pending user until `POST /auth/verify-otp` confirms it
- No SMS OTP step — Cognito UserPool has `autoVerify: { phone: false }` to prevent SMS on SignUp
- Login uses Cognito `AdminInitiateAuth` with the `ADMIN_USER_PASSWORD_AUTH` flow (2.1.0); the public mobile client has `USER_PASSWORD_AUTH` disabled. The separate admin web client keeps `USER_PASSWORD_AUTH` (it drives the TOTP challenge itself)
- Token refresh uses Cognito `InitiateAuth` with `REFRESH_TOKEN_AUTH` flow
- Logout resolves the token's owner with `InitiateAuth` (`REFRESH_TOKEN_AUTH`), then revokes it with `RevokeToken` (2.7.0)

### JSON Serialization

All request/response DTOs must use `System.Text.Json` source generators:

```csharp
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(AuthError))]
public partial class AuthJsonContext : JsonSerializerContext { }
```

---

## Mobile Implementation Notes

### Authentication Flow Decision Tree

```
App Launch
  |
  +-- Has cached credentials?
  |     |
  |     +-- YES: Check offline session age
  |     |     |
  |     |     +-- < 48 hours: Check connectivity
  |     |     |     |
  |     |     |     +-- ONLINE: Refresh tokens silently --> App
  |     |     |     +-- OFFLINE: Use cached tokens --> App (offline mode)
  |     |     |
  |     |     +-- >= 48 hours: Clear cache --> Login screen
  |     |
  |     +-- NO: Login screen
  |
  +-- Login screen
        |
        +-- Check connectivity
              |
              +-- ONLINE: Cognito login --> Cache credentials --> App
              +-- OFFLINE: PBKDF2 verify --> Cached tokens --> App (offline mode)
```

### State Management

Authentication state should be managed via BLoC with the following states:

| State | Description |
|-------|-------------|
| `AuthInitial` | App just launched, checking cached state |
| `AuthLoading` | Login/register in progress |
| `AuthAuthenticated` | User logged in (online or offline) |
| `AuthOffline` | User logged in via offline cache |
| `AuthUnauthenticated` | No valid session, show login screen |
| `AuthLocked` | Account locked due to failed attempts |
| `AuthError` | Authentication failed with error message |

**Note** (2.1.0): the OTP step is back for registrations without a phone-bound Fayda proof; the client keeps an `AuthOtpVerificationRequired(phoneNumber, expiresAt)` state and an OTP screen.

---

## Invariants

These rules must always hold:

1. **Registration requires internet and proof of phone ownership.** A user cannot register offline. A Fayda verification token (`POST /auth/fayda-verify`) is required at registration (2.2.0; it was optional in 2.1.0). When Fayda released no phone or a different one, the SMS code step is also mandatory. (Superseded 2.0.0 text: "There is no manual fallback.")
2. **Online login is preferred.** If the device has connectivity, always authenticate via Cognito (not cached credentials).
3. **Offline login never creates new sessions.** Offline login only works if the user has previously logged in online and credentials are cached.
4. **Tokens are never stored in plaintext.** All tokens and password hashes must be in `flutter_secure_storage` (hardware-backed).
5. **Failed offline attempts are bounded.** Exponential backoff from 1st failure (1min, 5min, 15min, 1hr). After 5 failed attempts, credentials are cleared and the user must go online.
6. **Offline sessions expire.** After 48 hours without connectivity, the cached session is invalidated (stolen-device mitigation).
7. **Logout clears tokens, not Isar data.** On logout, tokens and password hash are cleared from secure storage, but Isar data (crop listings, trips) is preserved for the next user or re-login.
8. **Phone number is the identity.** All authentication flows use phone number as the primary identifier, never email.
9. **Error messages do not leak information.** Login failures return generic "invalid credentials" regardless of whether the phone number exists.
10. **All auth endpoints are idempotent.** Retrying any request with the same parameters produces the same result (or a safe duplicate detection).

---

## Data Transfer Optimization

Given Ethiopian mobile data costs:

1. **Minimal response payloads.** Auth responses include only essential fields.
2. **No redundant user data.** The `/auth/refresh` endpoint returns only new tokens, not the user profile.
3. **Compressed responses.** Enable gzip for responses over 1KB.
4. **Cache user profile locally.** After login, cache the user profile in Isar to avoid re-fetching.

---

## Additions in 2.1.0 (phone proof, step-up, server-driven Fayda)

The 2.0.0 statement that Fayda replaces SMS OTP with no fallback is superseded: identity
verification is two-tier. Fayda is required above 10,000 ETB (see escrow-protocol.md and
order-lifecycle.md); phone ownership is always proven.

### Registration outcome

`POST /auth/register` now returns one of two shapes:

| Status | When | Body |
|--------|------|------|
| `201` | A `faydaVerificationToken` was supplied and its `phone` claim (Fayda's `phone_number`) equals the registration phone | `{userId, phoneNumber, tokens, user, message}` as before |
| `202` | Fayda released no phone or a different one (2.2.0: a missing token is `400 AUTH_021`) | `{userId, phoneNumber, otpRequired: true, otpExpiresAt, message}`; `tokens` is null |

The verification token is single use (its `jti` is burned at registration) and carries the
Fayda phone when the provider released one. A phone whose earlier registration never
confirmed its code is replaced by the next registration (new password included); a
confirmed phone still returns `AUTH_005`.

### POST /auth/verify-otp

Body `{phoneNumber, otpCode, password}`. The password is checked against Cognito **before**
the code is consumed (a correct password on an unconfirmed user answers "not confirmed",
a wrong one is refused with `AUTH_014` and the code stays valid). The code is bound to the
registration it was issued for; a code minted for another registration of the same phone
is `AUTH_012`. On success the user is confirmed, `isPhoneVerified` is set and
`{tokens, user}` (the login shape) is returned. Codes: 6 digits, valid 5 minutes, 3 attempts
per code, HMAC-hashed at rest. `DEV_OTP_BYPASS=true` (dev only; ignored at runtime in a
production Lambda) accepts `123456`.

| Status | Code | Description |
|--------|------|-------------|
| `400` | `AUTH_009` | Code missing |
| `400` | `AUTH_011` | Code format invalid (not 6 digits) |
| `400` | `AUTH_012` | Code invalid, expired or attempts exhausted |
| `401` | `AUTH_014` | Password wrong (the phone is confirmed; sign in normally) |
| `403` | `AUTH_025` | Account suspended |
| `404` | `AUTH_013` | Profile not found for this phone (register again) |
| `429` | `AUTH_006` | Per-IP cap (30 per 5 minutes) |

### POST /auth/resend-otp

Body `{phoneNumber}`. Always `200 {message, otpExpiresAt}` for a well-formed phone (no
enumeration; `otpExpiresAt` is null when nothing was sent). Limits: 60 s cooldown, 10 sends
per phone per day, 30 per IP per 5 minutes (`429 AUTH_006`, `Retry-After`).

### Login additions

- `403 AUTH_016` (phone not confirmed): the client resends the code and shows the OTP screen.
- Per-IP cap: 20 attempts per 5 minutes (`429 AUTH_006`).
- Request headers `X-Device-Id` (8 to 64 chars `[A-Za-z0-9-]`, one per install) and
  `X-Device-Name` (short model description; the server keeps only letters, digits, space,
  dot, underscore, hyphen and parentheses, at most 40 characters, since the text reaches
  the account holder's SMS), sent on login, register and verify-otp;
  register records the install as the account's first device. The login response carries `newDevice: true` when
  the install has not signed in to the account before (the first device is not new). A new
  device is audited and, with `NEW_DEVICE_SMS_ENABLED=true`, announced by SMS.
- The public Cognito client no longer allows `USER_PASSWORD_AUTH`; the server authenticates
  with `ADMIN_USER_PASSWORD_AUTH`, so lockout, suspension and audit cannot be bypassed. The
  separate admin web client keeps `USER_PASSWORD_AUTH` on purpose: it runs the TOTP
  challenge in the browser.

### Refresh additions

`403 AUTH_025` when the profile is suspended; the refresh token is revoked. Per-IP cap 60
per 5 minutes. Suspension also disables the Cognito user and signs out every session;
`change_role` rewrites `custom:role` and signs the user out.

### Step-up (AUTH_040, AUTH_041)

Freshness means within `STEP_UP_MAX_AGE_SECONDS` (default 300). A password is fresh when
the ID token's `auth_time` is; refreshed tokens keep the original `auth_time`, access tokens
carry none and are never fresh. A Fayda login is fresh when the last successful
`POST /user/fayda-link` for the account is (server-side `faydaAuthenticatedAt` on the user
row, never returned to clients).

**Payments** (2.4.0) are tiered at the NBE Fayda threshold, 10,000 ETB:

| Amount | Needs | Otherwise |
|--------|-------|-----------|
| up to and including 10,000 ETB | a fresh password | `AUTH_040` |
| above 10,000 ETB | a fresh Fayda login (the password is not asked) | `AUTH_041` |

**Disputes** on escrows at or above `STEP_UP_THRESHOLD_ETB` (default 10,000) need a fresh
password (`AUTH_040`).

A `/sync` delivery dispute is sized by its order's `orderTotal`, rounded to the cent as the
escrow amount is (2.8.0, FUP-26). The check never fails open: when the order is missing, or its
total is missing, unreadable, zero or negative, the fresh password is needed at any amount; a
legacy delivery without an `orderId` is sized by the escrow on its `DeliveryIndex`, rounded and
with the same rule for an amount that is missing, unreadable, zero or negative (none: no
step-up, nothing is frozen). When the order or that escrow cannot be read, the operation answers
`DELIVERY_DISPUTE_RETRY: ...` before the escrow freeze, and nothing is written.

| Where | Response |
|-------|----------|
| `POST /escrow/initiate` (payment tiers) | `403 {error:{code:"AUTH_040"}}` or `403 {error:{code:"AUTH_041"}}` |
| `/sync` order update to `payment_submitted` (payment tiers) | operation `status:"error"`, `message:"AUTH_040: ..."` or `"AUTH_041: ..."` |
| `POST /escrow/dispute`, `/sync` delivery `disputed` | `AUTH_040` as above |

Client behaviour: on `AUTH_040` ask for the password, call `POST /auth/login` again, store the
tokens as a normal login, retry. On `AUTH_041` run the Fayda link flow as the account owner
(same rules as KYC, `FAYDA_005/006/007` on a different identity), then retry. A queued sync
operation stays pending until then.

**Risk tier** (2.6.0; built in 2.10.0, FRD-04). The account's risk tier (`specs/architecture/fraud-detection-architecture.md`
sections 14 to 17) raises the payment requirement at any amount:

| Tier | Payment requirement | Codes |
|------|---------------------|-------|
| LOW | the amount tiers above | `AUTH_040` or `AUTH_041` as today |
| MEDIUM | a fresh password at any amount, plus the Fayda login above 10,000 ETB | `AUTH_040`; above 10,000 ETB `AUTH_041` first, `AUTH_040` on the retry |
| HIGH | a fresh Fayda login, then a fresh password, at any amount | `AUTH_041` first, `AUTH_040` on the retry |
| CRITICAL | the payment is refused | `FRAUD_ACCOUNT_HELD` |

Fayda is asked before the password because its round trip takes minutes and the password seconds,
so both proofs stay inside the step-up window. The `AUTH_041` message is amount-neutral since
2.10.0 (`Confirm it's you with Fayda to continue with this payment.`); clients key on the code only
and never see the tier or the score. Disputes keep the `AUTH_040` rule above and are not
tier-gated. `FRAUD_ACCOUNT_HELD` also refuses crop, trip and order create through `/sync`
(`sync-protocol.md` 1.9.0, built in 1.25.0).

Built in 2.10.0 (FRD-04): the tier is the caller's, read once per request (once per `/sync`
batch). A score that could not be read, or a server without the fraud-signals table, is LOW, so
the amount tiers above still apply; a score read cut at its page cap is CRITICAL. Where the codes
come from:

| Where | Order of the checks |
|-------|---------------------|
| `POST /escrow/initiate` | role (`ESCROW_025`), tier: CRITICAL `403 FRAUD_ACCOUNT_HELD`, the step-up above (`AUTH_041`, then `AUTH_040`), velocity (`ESCROW_011`) |
| `/sync` order update to `payment_submitted` | transition rules, tier: CRITICAL `FRAUD_ACCOUNT_HELD: ...`, KYC (`ORDER_KYC_REQUIRED`), the step-up above, the order total check |
| `POST /escrow/order-checkout` | role and body, tier: CRITICAL `403 FRAUD_ACCOUNT_HELD` before any Telebirr call, velocity; no step-up (it ran at `payment_submitted`) |

### Server-driven Fayda OIDC

| Endpoint | Auth | Body | Response |
|----------|------|------|----------|
| `POST /auth/fayda-start` | none | `{redirectUri?}` (`phoneNumber` accepted, ignored) | `200 {state, authorizeUrl, expiresAt}` |
| `POST /auth/fayda-verify` | none | `{authorizationCode, state, redirectUri}` | `200 {verified, verifiedName, verificationToken, expiresAt}` |
| `POST /user/fayda-start` | Bearer | `{}` | `200 {state, authorizeUrl, expiresAt}` |
| `POST /user/fayda-link` | Bearer | `{authorizationCode, state, redirectUri}` | `200 {faydaVerified, faydaVerifiedAt, verifiedName, phoneVerified}` |

The server mints `state`, `nonce` and the PKCE verifier, keeps them for 10 minutes (single
use) and builds the authorize URL from the provider's discovery document with scope
`openid profile phone`. The exchange sends `code_verifier` and checks the ID token's
`nonce`. `FAYDA_002` covers an unknown, expired, reused or foreign `state`, a redirect
mismatch, a nonce mismatch, and a state row missing its nonce, PKCE verifier or redirect
(fail closed). The ID token must be signed with RS256, PS256 or ES256 (no `none`, no HMAC)
and carry `exp`; a discovery document whose `issuer` differs from the configured issuer is
ignored. Accepted redirects are `weredaconnect://fayda-callback` and
`weredaconnect://fayda-link-callback`. Against the mock provider only (`FAYDA_MOCK=true` on
the verify Lambda, refused in prod by the infra guard and at runtime by `DevBypass`), the
web app's callback pages in `FAYDA_MOCK_WEB_REDIRECT_URIS` are also accepted (exact match,
https or loopback http, no query). The app sends its `redirectUri` on start. Per-IP cap 30
per 5 minutes.
Linking a Fayda ID held by another account is `409 FAYDA_004`; linking with a matching
Fayda phone also sets `isPhoneVerified`.

**Identity binding (2.3.0).** The caller's account is known, so the link only accepts the
Fayda identity of that account's owner:

| Account state | Fayda identity returned | Result |
|---------------|-------------------------|--------|
| holds a Fayda ID | same `sub` | `200` (re-verification, `faydaVerifiedAt` kept) |
| holds a Fayda ID | different `sub` | `403 FAYDA_005`, account unchanged |
| no Fayda ID | `phone_number` equal to the account phone (after normalising) | `200`, Fayda ID bound |
| no Fayda ID | `phone_number` different | `403 FAYDA_006`, account unchanged |
| no Fayda ID | no `phone_number` claim | `403 FAYDA_007`, account unchanged (fails closed) |

The Fayda `phone_number` claim is compared in `+251XXXXXXXXX` form; `+251…`, `00251…`,
`251…` (E.164 without the plus), `0…` and the bare 9-digit national number are all read as
the same phone (this also applies to the register phone proof).

The check runs again on the re-read row after a version conflict. Refusals are audited as
`FAYDA_LINK_MISMATCH` (detail `fayda_id`, `phone` or `no_phone_claim`). The payment KYC
gate (`ORDER_KYC_REQUIRED`, see `order-lifecycle.md`) passes only after a successful link,
so a failed or refused verification leaves the payment blocked.

### Rate limits added (per source IP, DynamoDB counters)

| Route family | Cap |
|--------------|-----|
| login | 20 / 5 min |
| register | 10 / hour |
| verify-otp, resend-otp | 30 / 5 min |
| fayda-start, fayda-verify, fayda-link | 30 / 5 min |
| forgot-password, reset-password | 10 / 15 min |
| refresh | 60 / 5 min |

---

## Additions in 2.11.0 (account deletion, ACC-02)

`DELETE /user/account` and its codes (`ACCOUNT_001` to `ACCOUNT_005`) are specified in
`account-deletion.md` 1.2.0; its step-up is the `AUTH_040` rule of 2.1.0 (a password within
`STEP_UP_MAX_AGE_SECONDS`). This contract changes as follows.

- **Login**: an account whose deletion reached `cognito_pending` or `completed` answers
  `401 AUTH_014` with the wrong-password text and no tokens, whatever Cognito said (no
  enumeration). Once the Cognito user is deleted Cognito itself answers `AUTH_014`, and the
  tombstone has no phone, so the old phone resolves to no account. An account in
  `in_progress` logs in normally, so the user can finish the deletion.
- **Refresh**: the same two states answer `401 AUTH_014`, and the refresh token is revoked.
  `in_progress` refreshes normally (the deletion revoked the earlier sessions in its step 6).
- **`/user/fayda-start`, `/user/fayda-link`**: `410 ACCOUNT_004` with `error.deletionState`
  when the caller's account carries `deletionState`, checked before the state row is read or
  the code exchanged, so nothing is consumed or written. When the account cannot be read the
  answer is `503 AUTH_008` (retry), with nothing consumed.
- **Register**: a `displayName` equal to `Deleted user` (trimmed, any case) is refused with
  `400 AUTH_007` on `displayName`; that text is the sentinel copied onto a deleted user's rows,
  so a live account may never look deleted.
- Profile writes (the device list at login, the Fayda link, the phone proof) are conditional
  on the row having no `deletionState`; on a deleting account they fail like a version
  conflict and nothing is written.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-12 | Initial contract: registration, OTP, login, refresh, logout, offline auth, rate limits |
| 2.0.0 | 2026-03-02 | Replace SMS OTP with Fayda OIDC: add `POST /auth/fayda-verify`, remove `POST /auth/verify-otp`, registration returns tokens directly, add FAYDA_001-004 error codes, remove AUTH_009-012 |
| 2.11.0 | 2026-10-23 | Account deletion (ACC-02, `account-deletion.md` 1.2.0): login and refresh of a `cognito_pending` or `completed` account answer `401 AUTH_014` (refresh also revokes the token), `in_progress` logs in; `/user/fayda-start` and `/user/fayda-link` answer `410 ACCOUNT_004` for a deleting or deleted account before any state is read (`503 AUTH_008` when the account cannot be read); register refuses the display name `Deleted user` with `AUTH_007`; profile writes skip a deleting or deleted row |
| 2.10.0 | 2026-10-23 | Risk tier step-up built (FRD-04, the 2.6.0 rules): MEDIUM adds the fresh password at any amount, HIGH the Fayda login and then the password at any amount, CRITICAL refuses payments, checkouts and crop, trip and order creates with `FRAUD_ACCOUNT_HELD`; the caller's tier only, read once per request or `/sync` batch; an unreadable score or a missing fraud-signals table is LOW, a truncated read CRITICAL; `AUTH_041` text is now amount-neutral (`Confirm it's you with Fayda to continue with this payment.`); order of the checks per path |
| 2.9.0 | 2026-10-23 | Client logout sequence (SIM-55): the app starts `POST /auth/logout` with the live stored tokens before it clears the session and never waits on it to clear; a missing or expired access token is first replaced by one minted with `POST /auth/refresh` (never stored); a `401` or `403 AUTH_026` is retried once with a minted bearer; the app maps `AUTH_026`. Server unchanged |
| 2.8.0 | 2026-10-17 | The `/sync` delivery dispute step-up fails closed (FUP-26): an order (or a legacy delivery's escrow) that cannot be read answers `DELIVERY_DISPUTE_RETRY` and nothing is written, where the step-up was skipped before; a missing order, or a missing, unreadable, zero or negative total, needs the fresh password at any amount; a legacy delivery without an `orderId` is sized by its `DeliveryIndex` escrow; the total is rounded to the cent like the escrow amount |
| 2.7.0 | 2026-10-06 | Logout is bound to the caller (SIM-60): the refresh token's owner is resolved with `InitiateAuth REFRESH_TOKEN_AUTH` and must equal the bearer's `sub`, else `403 AUTH_026` and nothing is revoked; an inactive token answers `200` without a revoke; `503 AUTH_008` when Cognito is unavailable. The `/auth/logout` integration moved to payload 1.0 so the handler sees the claims |
| 2.6.0 | 2026-10-06 | Risk tier step-up (planned FRD-04): `AUTH_040` at any amount for MEDIUM, `AUTH_041` then `AUTH_040` at any amount for HIGH, new `FRAUD_ACCOUNT_HELD` (403) for CRITICAL on payments and on crop, trip and order create; `AUTH_041` text amount-neutral; header version corrected (it read 2.3.0 while the footer read 2.4.0) |
| 2.4.0 | 2026-09-29 | Payment step-up is tiered: up to 10,000 ETB a fresh password (`AUTH_040`, now on every payment, not only large ones); above 10,000 ETB a fresh Fayda login instead (`AUTH_041`), recorded by each successful `POST /user/fayda-link`. Disputes unchanged |
| 2.3.0 | 2026-09-29 | `POST /user/fayda-link` binds only the account owner's identity: same Fayda ID as on file, else a Fayda phone equal to the account phone; `FAYDA_005` (different Fayda ID), `FAYDA_006` (phone mismatch), `FAYDA_007` (no phone claim, nothing to compare), all `403` with no write |
| 2.2.0 | 2026-09-28 | Registration requires Fayda: `POST /auth/register` without a `faydaVerificationToken` returns `400 AUTH_021`; the app has no skip on the Fayda step and retries transient Fayda failures before showing an error; the server retries Fayda's token endpoint while the code cannot have been used (connection refused, 502/503/504) and reports any Fayda 5xx as `FAYDA_003` instead of `FAYDA_001`; a Fayda ID held only by an abandoned sign-up (profile phone unverified and Cognito user unconfirmed or gone) no longer blocks `fayda-verify` or `register` with `FAYDA_004`: register replaces it |
| 2.1.0 | 2026-09-26 | Phone proof and step-up: register returns `202 otpRequired` unless Fayda released the same phone; `POST /auth/verify-otp` (code + password) and `POST /auth/resend-otp`; login `AUTH_016` routes to the code; `AUTH_040` step-up on money actions; server-driven Fayda (`/auth/fayda-start`, `/user/fayda-start`, `/user/fayda-link`) with state, nonce and PKCE; single-use phone-bound verification token; refresh refuses suspended accounts (`AUTH_025`); per-IP caps; `X-Device-Id` / `X-Device-Name` and `newDevice` |

---

**Last Updated**: 2026-10-23
**Version**: 2.11.0
