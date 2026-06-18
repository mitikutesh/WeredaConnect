# Authentication System Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R1-R3)

---

## 1. Component Overview

```
┌──────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                        │
│                                                               │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────┐ │
│  │   AuthBloc    │  │  AuthService │  │  OfflineAuthCache  │ │
│  │               │──│  (real or    │──│  (PBKDF2 hash,     │ │
│  │ Events/States │  │   mock)      │  │   secure storage)  │ │
│  └──────────────┘  └──────┬───────┘  └────────────────────┘ │
│                           │                                   │
│  ┌──────────────┐  ┌──────┴───────┐  ┌────────────────────┐ │
│  │  AuthGate     │  │ AuthIntercep │  │  Isar DB           │ │
│  │  (routing)    │  │ tor (token   │  │  (User schema,     │ │
│  │               │  │  injection)  │  │   encrypted)       │ │
│  └──────────────┘  └──────────────┘  └────────────────────┘ │
└──────────────────────────────┬───────────────────────────────┘
                               │
              POST /auth/register    POST /auth/login
              POST /auth/verify-otp  POST /auth/refresh
              POST /auth/logout
                               │
                               ▼
┌──────────────────────────────────────────────────────────────┐
│                  API GATEWAY (HTTP API v2)                     │
│  Cognito JWT Authorizer on protected routes                   │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────┐
│                    AWS Cognito User Pool                       │
│  Phone sign-in | SMS OTP | Custom attributes (role, woreda)  │
│  Password: 8+ chars, upper + lower + digits                   │
│  Token lifetimes: Access 15m | Refresh 7d                     │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. Authentication Flows

### 2.1 Registration (Online Required)

```
User enters phone (+251912345678) + password + name + role + woreda
  │
  ▼
[AuthBloc] ── AuthRegisterRequested
  │
  ▼
[AuthService]
  ├── POST /auth/register → Cognito sends SMS OTP
  └── Return userId + otpExpiresAt
  │
  ▼
[AuthBloc] ── emits AuthOtpVerificationRequired
  │
  ▼
User enters 6-digit OTP code
  │
  ▼
[AuthBloc] ── AuthVerifyOtpRequested
  │
  ▼
[AuthService]
  ├── POST /auth/verify-otp → Cognito verifies
  ├── Receive tokens (access, refresh, ID)
  ├── Cache tokens in Flutter Secure Storage
  ├── Cache password hash (PBKDF2) for offline login
  └── Cache user profile in Isar (encrypted)
  │
  ▼
[AuthBloc] ── emits AuthAuthenticated
```

### 2.2 Online Login

```
User enters phone + password → connectivity detected → ONLINE
  │
  ▼
[AuthService]
  ├── POST /auth/login → Cognito InitiateAuth
  ├── Receive fresh tokens
  ├── Update secure storage
  ├── Update offline cache (PBKDF2 hash + user profile)
  └── Proceed to app
```

### 2.3 Offline Login (Cached Credentials)

```
User enters phone + password → connectivity check → OFFLINE
  │
  ▼
[OfflineAuthCache]
  ├── 1. Check rate limit (5 attempts / 15 min per phone)
  │      └── If rate limited → show lockout message, block
  ├── 2. Hash password with PBKDF2 (100,000 iterations, SHA-256)
  ├── 3. Compare with cached hash (constant-time comparison)
  ├── 4. If match:
  │      ├── Clear failed attempt counter
  │      ├── Load cached user profile from Isar
  │      ├── Generate local session token (device-bound)
  │      └── Mark session as "offline" (sync pending)
  ├── 5. If no match:
  │      ├── Record failed attempt with timestamp
  │      └── Show error message
  └── 6. When online: validate with Cognito, refresh tokens
```

### 2.4 Token Refresh (Background)

```
[AuthInterceptor] detects 401 or token near expiry
  │
  ▼
[AuthService]
  ├── POST /auth/refresh with refreshToken
  ├── Receive new access + ID tokens
  ├── Update secure storage
  └── Retry original request
```

---

## 3. Token Architecture

| Token | TTL | Storage | Purpose |
|-------|-----|---------|---------|
| Access Token (JWT) | 15 min | Flutter Secure Storage | API authorization |
| Refresh Token | 7 days | Flutter Secure Storage | Renew access tokens |
| ID Token (JWT) | 15 min | Flutter Secure Storage | User profile claims |
| Offline Session | 7 days max | Isar (encrypted) | Cache invalidation |

**Token Injection**: `AuthInterceptor` (Dio) auto-adds `Authorization: Bearer {token}` to all requests, catches 401 responses, triggers refresh, and queues concurrent requests during refresh to prevent thundering herd.

---

## 4. Offline Auth Security

### PBKDF2 Password Hashing

| Parameter | Value |
|-----------|-------|
| Algorithm | PBKDF2 with HMAC-SHA256 |
| Iterations | 100,000 (OWASP 2026 standard) |
| Output Length | 256 bits (32 bytes) |
| Salt | 32 bytes, per-phone, random |
| Storage | Flutter Secure Storage (Keystore/Keychain) |
| Comparison | Constant-time (prevents timing attacks) |

### Offline Rate Limiting

| Parameter | Value |
|-----------|-------|
| Max Failed Attempts | 5 per phone number |
| Lockout Duration | 15 minutes |
| Storage | In-memory (resets on app restart) |
| Purpose | Brute force prevention on shared devices |

### Cache Invalidation Triggers
- Explicit logout (user action)
- Password change (server-side revokes tokens)
- 7-day offline limit exceeded
- Token refresh failure (token revoked/expired)

---

## 5. State Management (BLoC)

### Events
| Event | Trigger |
|-------|---------|
| `AuthCheckRequested` | App launch |
| `AuthRegisterRequested` | Registration form submit |
| `AuthVerifyOtpRequested` | OTP code submit |
| `AuthLoginRequested` | Login (auto-detects online/offline) |
| `AuthLoginOnlineRequested` | Force online auth |
| `AuthLoginOfflineRequested` | Force offline auth |
| `AuthRefreshTokenRequested` | Background token refresh |
| `AuthLogoutRequested` | User logout |

### States
| State | Meaning |
|-------|---------|
| `AuthInitial` | Checking cached credentials |
| `AuthLoading` | Operation in progress |
| `AuthAuthenticated` | Logged in (includes `isOfflineAuth` flag) |
| `AuthUnauthenticated` | No valid session |
| `AuthOtpVerificationRequired` | Awaiting OTP |
| `AuthError` | Failed (with `canRetry` flag) |

---

## 6. Role-Based Access

| Role | Icon | Use Case |
|------|------|----------|
| `farmer` | Icons.agriculture | Lists crops, receives payment |
| `trucker` | Icons.local_shipping | Provides transport |
| `buyer` | Icons.shopping_cart | Purchases crops |

Role is captured during registration, stored in Cognito as `custom:role`, cached in Isar User collection, and included in JWT claims. UI conditionally renders features based on role.

---

## 7. Phone Number Validation

| Parameter | Value |
|-----------|-------|
| Pattern | `^\+251[0-9]{9}$` |
| Example | +251912345678 |
| Total Length | 13 characters |
| Validation | Client (regex) + Server (Cognito) |

UI pre-fills `+251` prefix, allows only numeric input.

---

## 8. Mock vs Real Service

| Aspect | Mock (Development) | Real (Production) |
|--------|-------------------|-------------------|
| Toggle | `Environment.useMockAuth = true` | `Environment.useMockAuth = false` |
| OTP | Any 6-digit code (e.g., 123456) | Cognito SMS |
| Password | Any value accepted | Cognito validation |
| Tokens | Fake JWT, 1-hour TTL | Real Cognito JWT |
| Network | No network required | Requires connectivity |
| Delay | Simulated 500ms | Real latency |
| User ID | `mock_` + phone number | Cognito UUID |

Orange dev-mode banner is displayed when mock auth is enabled.

---

## 9. Screens

| Screen | Purpose |
|--------|---------|
| AuthGate | Root router — orchestrates splash/welcome/login/main |
| LoginScreen | Phone + password input, +251 prefix, offline toggle |
| RegisterScreen | Full registration with role selection (3 icon cards) |
| OtpVerificationScreen | 6-digit input, auto-submit, resend countdown |

### UX Patterns (Ethiopian Context)
- Icon-based role selection (low-literacy friendly)
- Large touch targets (48dp minimum)
- Generic error messages ("Invalid phone or password" — no enumeration)
- Language selection accessible from auth screens

---

## 10. Data Persistence

### Secure Storage (Flutter Secure Storage)
| Key | Value |
|-----|-------|
| `current_user_phone` | Active phone number |
| `password_hash_{phone}` | PBKDF2 hash (base64) |
| `salt_{phone}` | PBKDF2 salt (base64) |
| `tokens_{phone}` | JSON (access/refresh/ID tokens) |

### Local Database (Isar — Encrypted)
| Collection | Indexes |
|------------|---------|
| `users` — userId, phoneNumber, name, role, woreda | phoneNumber (unique), userId (unique), role |

---

## 11. API Contract

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/auth/register` | POST | Create user, send SMS OTP |
| `/auth/verify-otp` | POST | Verify OTP, return tokens |
| `/auth/login` | POST | Phone + password auth |
| `/auth/refresh` | POST | Renew access token |
| `/auth/logout` | POST | Revoke refresh token |

Full contract: `specs/contracts/auth-api.md`

---

## 12. Error Codes

| Code | Meaning |
|------|---------|
| AUTH_001 | Invalid phone format |
| AUTH_002 | Password requirements not met |
| AUTH_005 | Phone already registered |
| AUTH_006 | Rate limit exceeded |
| AUTH_011 | Invalid OTP format |
| AUTH_012 | OTP expired |
| AUTH_014 | Invalid credentials (generic) |
| AUTH_021 | Phone not verified |
| AUTH_031 | Invalid token |
| NETWORK_ERROR | No internet connection |
| OFFLINE_NOT_AVAILABLE | Must log in online first |

---

## 13. Security Architecture

| Concern | Mitigation |
|---------|-----------|
| Timing attacks | Constant-time password comparison |
| Brute force (offline) | 5 attempts + 15-minute lockout |
| Brute force (online) | Server-enforced rate limits + WAF |
| User enumeration | Generic error messages |
| Token theft | Flutter Secure Storage (Keystore/Keychain) |
| SIM swap | Future: device fingerprint (reserved in API) |
| Shared devices | Per-phone rate limiting, session isolation |

---

## 14. File Structure

```
mobile/lib/features/auth/
├── bloc/
│   ├── auth_bloc.dart
│   ├── auth_event.dart
│   └── auth_state.dart
├── screens/
│   ├── auth_gate.dart
│   ├── login_screen.dart
│   ├── register_screen.dart
│   └── otp_verification_screen.dart
└── widgets/

mobile/lib/core/auth/
├── auth_service.dart
├── mock_auth_service.dart
├── offline_auth_cache.dart
├── auth_interceptor.dart
└── models/
    ├── auth_tokens.dart
    └── auth_error.dart
```

---

**Cross-references**:
- Encryption plan: `specs/decisions/authentication-encryption-plan.md`
- API contract: `specs/contracts/auth-api.md`
- Certificate pinning: `specs/decisions/cert-pinning.md`

**Last Updated**: 2026-02-21
