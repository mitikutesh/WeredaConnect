# Authentication API Contract

## Version
1.0.0

## Parties
- **Mobile** (client) -- registers users, authenticates online/offline, manages cached tokens
- **Backend** (server) -- proxies to AWS Cognito, validates JWTs, enforces rate limits
- **Infrastructure** (CDK) -- provisions Cognito User Pool, API Gateway throttling, WAF rules

---

## Overview

The authentication API enables phone-based registration and login for Ethiopian users. It uses a hybrid approach: AWS Cognito for online authentication and PBKDF2-based offline verification for cached credentials. All endpoints are accessed via API Gateway with rate limiting enforced at the gateway level.

**Base path**: `/auth`

---

## Endpoints

### 1. POST /auth/register

Register a new user with Ethiopian phone number, password, role, and woreda.

**Authentication**: None (public endpoint)

#### Request Body

```json
{
  "phoneNumber": "+251912345678",
  "password": "securePass1",
  "role": "farmer",
  "woreda": "Adama",
  "displayName": "Abebe Kebede",
  "preferredLanguage": "am"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `phoneNumber` | string | Yes | Ethiopian format: `+251` followed by 9 digits. Regex: `^\+251[0-9]{9}$` |
| `password` | string | Yes | Minimum 6 characters, maximum 128 characters |
| `role` | string | Yes | Enum: `farmer`, `trucker`, `buyer` |
| `woreda` | string | Yes | Non-empty, maximum 100 characters (administrative district name) |
| `displayName` | string | No | Maximum 100 characters. Supports Amharic/Oromo Unicode. |
| `preferredLanguage` | string | No | Enum: `en`, `am`, `om`. Default: `am` |

#### Success Response

**Status**: `201 Created`

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "phoneNumber": "+251912345678",
  "otpRequired": true,
  "otpExpiresAt": "2026-02-12T10:05:00Z",
  "message": "OTP sent via SMS. Please verify your phone number."
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `AUTH_001` | Invalid phone number format |
| `400` | `AUTH_002` | Password does not meet requirements |
| `400` | `AUTH_003` | Invalid role (must be farmer, trucker, or buyer) |
| `400` | `AUTH_004` | Woreda is required |
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

### 2. POST /auth/verify-otp

Verify the SMS OTP code sent during registration.

**Authentication**: None (public endpoint, but requires valid userId from registration)

#### Request Body

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "phoneNumber": "+251912345678",
  "otpCode": "123456"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `userId` | string | Yes | UUID v4 returned from registration |
| `phoneNumber` | string | Yes | Must match the registered phone number |
| `otpCode` | string | Yes | Exactly 6 digits. Regex: `^[0-9]{6}$` |

#### Success Response

**Status**: `200 OK`

```json
{
  "verified": true,
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
| `400` | `AUTH_009` | Invalid OTP format (must be 6 digits) |
| `400` | `AUTH_010` | OTP expired (5-minute window exceeded) |
| `400` | `AUTH_011` | Invalid OTP code (wrong code) |
| `400` | `AUTH_012` | Maximum OTP attempts exceeded (3 attempts) |
| `404` | `AUTH_013` | User not found or registration expired |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |

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

Invalidate the user's refresh token. The access token remains valid until its 15-minute TTL expires (stateless JWT).

**Authentication**: Required (Bearer access token)

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
| `401` | `AUTH_020` | Invalid or expired access token |
| `429` | `AUTH_006` | Rate limit exceeded |
| `500` | `AUTH_007` | Internal server error |

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
   c. After 3 failed offline attempts: require online login
```

### Cache TTL and Invalidation

| Rule | Value | Rationale |
|------|-------|-----------|
| Maximum offline session | 7 days | Security: force online re-auth after 7 days |
| Cache invalidated on | Logout, password change, 7-day expiry | Prevent stale credentials |
| Refresh token auto-refresh | Background job when connectivity detected | Keep tokens fresh |
| Offline attempt limit | 3 failed attempts | Then require online login to reset |

### Offline Session Expiry Flow

```
1. User opens app (offline)
2. Check offlineSessionTimestamp
3. If now - offlineSessionTimestamp > 7 days:
   a. Clear cached credentials
   b. Show "Session expired. Please connect to the internet to log in."
   c. Block app access until online login succeeds
4. If within 7 days:
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
| `POST /auth/verify-otp` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/login` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/refresh` | 10 req/s | 20 | Account (IP-based) |
| `POST /auth/logout` | 10 req/s | 20 | Account (IP-based) |

### Application Level (Server-Enforced)

| Rule | Configuration | Action |
|------|---------------|--------|
| Failed login lockout | 3 consecutive failures | 15-minute account lockout |
| OTP attempts | 3 per OTP code | New OTP required |
| OTP resend | 1 per 60 seconds | Prevent SMS spam |
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
| `AUTH_009` | 400 | Invalid OTP format | Show OTP format hint (6 digits) |
| `AUTH_010` | 400 | OTP expired | Offer to resend OTP |
| `AUTH_011` | 400 | Invalid OTP code | Show remaining attempts |
| `AUTH_012` | 400 | Maximum OTP attempts exceeded | Offer to resend new OTP |
| `AUTH_013` | 404 | User/registration not found | Suggest re-registration |
| `AUTH_014` | 401 | Invalid credentials | Show generic "incorrect phone or password" |
| `AUTH_015` | 403 | Account locked (15-min lockout) | Show lockout timer |
| `AUTH_016` | 403 | Phone not verified | Redirect to OTP verification |
| `AUTH_017` | 400 | Missing refresh token | Redirect to login |
| `AUTH_018` | 401 | Refresh token expired | Redirect to login |
| `AUTH_019` | 401 | Refresh token revoked | Redirect to login |
| `AUTH_020` | 401 | Invalid access token | Attempt token refresh, then redirect to login |

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

### OTP Configuration

| Parameter | Value |
|-----------|-------|
| Length | 6 digits |
| Expiry | 5 minutes |
| Max attempts per code | 3 |
| Resend cooldown | 60 seconds |
| Delivery | SMS via AWS SNS to Ethio Telecom |

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
- Registration and OTP verification flow through Cognito `SignUp` and `ConfirmSignUp` APIs
- Login uses Cognito `InitiateAuth` with `USER_PASSWORD_AUTH` flow
- Token refresh uses Cognito `InitiateAuth` with `REFRESH_TOKEN_AUTH` flow
- Logout uses Cognito `RevokeToken` API

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
  |     |     +-- < 7 days: Check connectivity
  |     |     |     |
  |     |     |     +-- ONLINE: Refresh tokens silently --> App
  |     |     |     +-- OFFLINE: Use cached tokens --> App (offline mode)
  |     |     |
  |     |     +-- >= 7 days: Clear cache --> Login screen
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
| `AuthOtpRequired` | Registration requires OTP verification |
| `AuthUnauthenticated` | No valid session, show login screen |
| `AuthLocked` | Account locked due to failed attempts |
| `AuthError` | Authentication failed with error message |

---

## Invariants

These rules must always hold:

1. **Registration requires internet.** A user cannot register offline. This is the only flow that requires connectivity.
2. **Online login is preferred.** If the device has connectivity, always authenticate via Cognito (not cached credentials).
3. **Offline login never creates new sessions.** Offline login only works if the user has previously logged in online and credentials are cached.
4. **Tokens are never stored in plaintext.** All tokens and password hashes must be in `flutter_secure_storage` (hardware-backed).
5. **Failed offline attempts are bounded.** After 3 failed offline login attempts, the user must go online.
6. **Offline sessions expire.** After 7 days without connectivity, the cached session is invalidated.
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

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-12 | Initial contract: registration, OTP, login, refresh, logout, offline auth, rate limits |

---

**Last Updated**: 2026-02-12
**Version**: 1.0.0