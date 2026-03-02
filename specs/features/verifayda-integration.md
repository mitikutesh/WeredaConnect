# Feature: VeriFayda 2.0 Identity Verification During Registration

## Version
0.2.0 (Reviewed)

## What

Integrate Ethiopia's **VeriFayda 2.0** eKYC system into the WeredaConnect registration flow so users are identity-verified before they can trade. Instead of collecting Fayda ID scans locally and deferring verification, the app will verify users in real-time during registration using the Fayda eSignet OIDC flow.

## Why

- **NBE mandate**: VeriFayda 2.0 is mandatory for digital financial transactions above 10,000 ETB since January 2026. All accounts must be harmonized by April 8, 2026.
- **Trust**: Verified identities increase buyer/seller confidence in the marketplace.
- **Escrow gate**: The server already requires `faydaId` for escrow transactions >10,000 ETB (error `ESCROW_007`). Currently this is a stored string with no actual verification.
- **Fraud reduction**: Real identity verification prevents fake accounts and transaction fraud (complements F-14 fraud detection).

## Current State

| Component | Status |
|-----------|--------|
| `FayidaIdScreen` (mobile) | Captures Fayda ID number + front/back photos, stores locally |
| `AuthRegisterRequested` event | Carries `faydaId` field but never sends it to Fayda API |
| Server `UserDto` | Stores `FaydaId` as optional string field |
| Cognito ID token | Has `custom:faydaId` claim |
| Escrow server | Checks `FaydaId` is non-empty for >10K ETB transactions |

**Gap**: No actual API call to VeriFayda to validate the identity. The system trusts whatever the user types.

### Codebase Audit (Reviewed 2026-03-01)

| Item | Status | Notes |
|------|--------|-------|
| Deep link scheme (`weredaconnect://`) | NOT configured | AndroidManifest.xml has only LAUNCHER intent filter |
| `url_launcher` package | NOT installed | pubspec.yaml has no browser/URL launcher packages |
| Escrow `ESCROW_007` check | Uses string presence | `EscrowService.cs:150` checks `!string.IsNullOrEmpty(FaydaId)` — must change to boolean `FaydaVerified` |
| User Isar schema | Has `faydaId`, missing `faydaVerified` | `user.dart:31` — needs 2 new fields |
| Server `UserDto` | Has `FaydaId`, missing `FaydaVerified` | `UserDto.cs:26` — needs 2 new fields |
| `RegisterRequest` DTO | No verification token field | `AuthDtos.cs:6-14` — needs optional `faydaVerificationToken` |
| `auth_service.dart` register method | No Fayda data sent to server | Lines 65-120 — `faydaVerificationToken` not in request body |
| SSM Parameter Store pattern | Exists (`api-stack.ts:275`) | Easy to add 3 Fayda params following existing convention |
| Secrets Manager pattern | Exists (`api-stack.ts:225`) | Use for `client-secret` (more secure than SSM) |
| `System.Net.Http` AOT compliance | Allowed | Not in banned list; built-in to .NET, no package needed |
| JWT validation package | Already installed | `System.IdentityModel.Tokens.Jwt 8.3.0` in csproj |
| Fayda eSignet OIDC | Confirmed real | GitHub: `National-ID-Program-Ethiopia/fayda-auth-go`, `fayda-auth-sdk` |

---

## Proposed Approach

### Option A: OIDC with Fayda eSignet (Recommended)

Use Fayda's **OpenID Connect (OIDC) via eSignet** protocol. This is the standard integration path for Ethiopian apps.

**Flow**:
1. User reaches Fayda verification step in registration
2. App opens an in-app browser / WebView to the Fayda eSignet authorization URL
3. User authenticates with Fayda (biometric: face/fingerprint + OTP)
4. Fayda redirects back to the app with an authorization code
5. Server exchanges the code for an ID token containing verified claims
6. Server stores verified Fayda ID + verification timestamp
7. User continues with the rest of registration

**Why recommended**:
- Standard OIDC — well-documented, secure, no custom crypto
- Fayda handles all biometric verification (face, fingerprint, OTP)
- We get verified claims (name, ID number) directly from Fayda
- No need to store/transmit raw biometric data
- Compatible with AOT (.NET handles OIDC token exchange with `System.Net.Http`)
- Reference implementations exist on GitHub (`fayda-auth-go`, `fayda-auth-sdk`)
- EthSwitch blocks direct merchant API access — OIDC via eSignet is the only sanctioned third-party path

**Important prerequisite**: Fayda OIDC verification requires the user to have **already enrolled biometrics in person** at a Fayda registration center. The OIDC flow authenticates against existing enrollment — it does not perform remote enrollment. Users without prior biometric enrollment must use the manual entry fallback.

### Option B: Server-Side API Verification

Server calls Fayda's REST API with the user's Fayda ID number to verify.

**Why not recommended**:
- Requires Fayda API credentials on our server
- Less secure — we handle the ID number as a string, no biometric proof
- User could enter someone else's ID number
- Doesn't leverage Fayda's full eKYC (biometrics)

### Option C: Defer Verification (Status Quo + Enhancements)

Keep collecting Fayda ID locally, add server-side format validation, verify later.

**Why not recommended**:
- Doesn't meet NBE mandate for real verification
- No biometric proof of identity
- Delays trust establishment

---

## Detailed Design (Option A)

### Registration Flow Changes

```
Current:                          Proposed:
1. Role Selection                 1. Role Selection
2. Fayida ID (local capture)     2. Fayda Verification (OIDC) ← NEW
3. Role Details                   3. Role Details
4. Personal Info + SMS OTP        4. Personal Info + SMS OTP
```

Step 2 transforms from local ID capture to a real verification flow:

```
┌─────────────────────────────────────────────────────────┐
│  Step 2: Fayda Identity Verification                    │
│                                                         │
│  ┌─────────────────────────────────────────────────┐    │
│  │  🪪  Verify your Fayda ID                       │    │
│  │                                                 │    │
│  │  To trade on WeredaConnect, we need to verify   │    │
│  │  your identity using Ethiopia's national        │    │
│  │  Fayda digital ID system.                       │    │
│  │                                                 │    │
│  │  You will be redirected to Fayda to:            │    │
│  │  • Confirm your identity (face or fingerprint)  │    │
│  │  • Enter an OTP sent to your phone              │    │
│  │                                                 │    │
│  │  ┌─────────────────────────────────────────┐    │    │
│  │  │   [ Verify with Fayda ]  (green btn)    │    │    │
│  │  └─────────────────────────────────────────┘    │    │
│  │                                                 │    │
│  │  ─── OR enter manually (reduced trust) ───     │    │
│  │                                                 │    │
│  │  [ Fayda ID Number: _________________ ]         │    │
│  │  (Manual entry — limited to < 10K ETB trades)   │    │
│  └─────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

### Two Verification Tiers

| Tier | Method | Trust Level | Transaction Limit |
|------|--------|-------------|-------------------|
| **Verified** | Fayda OIDC (biometric) | Full | Unlimited |
| **Basic** | Manual ID entry (no biometric) | Limited | < 10,000 ETB per trade |

This approach avoids blocking users who can't do OIDC verification (no biometric enrollment, poor connectivity, old phone) while incentivizing full verification.

**Who falls into each tier**:
- **Verified**: Users who have visited a Fayda registration center and enrolled biometrics (face/fingerprint), AND have internet during registration
- **Basic**: Users without Fayda biometric enrollment, or users with poor connectivity, or users on very old devices that struggle with WebView

### Mobile Changes

#### New/Modified Files

| File | Action | Description |
|------|--------|-------------|
| `lib/features/auth/screens/registration/fayida_id_screen.dart` | **Replace** | New screen with "Verify with Fayda" OIDC button + manual fallback |
| `lib/features/auth/screens/registration/registration_flow.dart` | **Modify** | Handle OIDC callback, store verification result |
| `lib/features/auth/bloc/auth_event.dart` | **Modify** | Add `faydaVerified` boolean + `faydaVerificationToken` to `AuthRegisterRequested` |
| `lib/features/auth/bloc/auth_bloc.dart` | **Modify** | Pass verification token to server |
| `lib/core/auth/auth_service.dart` | **Modify** | Include verification token in register request |
| `lib/core/auth/fayda_oidc_service.dart` | **Create** | Handles OIDC authorization flow (URL builder, callback parser, token exchange via server) |
| `lib/core/database/schemas/user.dart` | **Modify** | Add `faydaVerified` bool, `faydaVerifiedAt` DateTime (currently has `faydaId`, `faydaIdFrontImagePath`, `faydaIdBackImagePath` — image fields become obsolete with OIDC) |
| `android/app/src/main/AndroidManifest.xml` | **Modify** | Add intent filter for `weredaconnect://fayda-callback` deep link |
| `pubspec.yaml` | **Modify** | Add `url_launcher` package dependency |
| `lib/shared/l10n/app_en.arb` | **Modify** | New l10n keys |
| `lib/shared/l10n/app_am.arb` | **Modify** | Amharic translations |
| `lib/shared/l10n/app_om.arb` | **Modify** | Afan Oromo translations |

#### OIDC Flow (Mobile Side)

```dart
// fayda_oidc_service.dart — simplified
class FaydaOidcService {
  // 1. Build authorization URL
  Uri buildAuthorizationUrl({required String state, required String nonce});

  // 2. Launch in-app browser (url_launcher or flutter_custom_tabs)
  Future<void> launchFaydaAuth(Uri authUrl);

  // 3. Handle redirect callback (deep link: weredaconnect://fayda-callback)
  FaydaAuthResult parseCallback(Uri callbackUri);

  // 4. Send auth code to OUR server for token exchange
  Future<FaydaVerificationResult> exchangeCode(String authCode);
}
```

#### Deep Link Configuration

- **Android**: Intent filter for `weredaconnect://fayda-callback`
- **Scheme**: `weredaconnect`
- **Path**: `/fayda-callback`
- **Parameters**: `code`, `state`, `error`

### Server Changes

#### New/Modified Files

| File | Action | Description |
|------|--------|-------------|
| `server/src/Functions/FaydaVerifyFunction.cs` | **Create** | New Lambda: exchanges auth code with Fayda, returns verified claims |
| `server/src/Services/FaydaOidcService.cs` | **Create** | OIDC token exchange + claim extraction (AOT-safe HTTP) |
| `server/src/Models/FaydaDtos.cs` | **Create** | Request/response DTOs with `JsonSerializerContext` |
| `server/src/Functions/RegisterFunction.cs` | **Modify** | Accept `faydaVerificationToken`, validate, store verified status |
| `server/src/Models/UserDto.cs` | **Modify** | Add `FaydaVerified`, `FaydaVerifiedAt` fields |
| `server/src/AppJsonContext.cs` | **Modify** | Add new DTOs to source gen context |
| `server/src/Services/EscrowService.cs` | **Modify** | Change Fayda check from `!string.IsNullOrEmpty(FaydaId)` to `FaydaVerified == true` (line ~150) |

#### New API Endpoint

```
POST /auth/fayda-verify
```

**Request**:
```json
{
  "authorizationCode": "abc123...",
  "state": "random-state-value",
  "redirectUri": "weredaconnect://fayda-callback"
}
```

**Response** (200 OK):
```json
{
  "verified": true,
  "faydaId": "ETH-FAYDA-123456789",
  "verifiedName": "Abebe Kebede",
  "verificationToken": "eyJ...",
  "expiresAt": "2026-03-01T12:00:00Z"
}
```

The `verificationToken` is a short-lived JWT signed by our server, proving the Fayda verification happened. The mobile app passes this token during the subsequent `/auth/register` call.

**Error Codes**:
| Status | Code | Description |
|--------|------|-------------|
| `400` | `FAYDA_001` | Invalid or expired authorization code |
| `400` | `FAYDA_002` | State mismatch (CSRF protection) |
| `502` | `FAYDA_003` | Fayda service unavailable |
| `422` | `FAYDA_004` | Fayda ID already linked to another account |
| `500` | `AUTH_007` | Internal server error |

#### Server OIDC Token Exchange (AOT-Safe)

```csharp
// FaydaOidcService.cs — uses System.Net.Http (no reflection)
public class FaydaOidcService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;     // from SSM Parameter Store
    private readonly string _clientSecret; // from SSM Parameter Store
    private readonly string _tokenEndpoint;
    private readonly string _userinfoEndpoint;

    public async Task<FaydaVerificationResult> ExchangeCodeAsync(
        string authorizationCode, string redirectUri)
    {
        // 1. POST to Fayda token endpoint (standard OIDC)
        // 2. Validate ID token signature (JWKS)
        // 3. Extract claims: sub, name, fayda_id
        // 4. Return verification result
    }
}
```

### Infrastructure Changes

| Resource | Action | Description |
|----------|--------|-------------|
| SSM Parameter Store | **Add** | `/weredaconnect/fayda/client-id`, `/weredaconnect/fayda/issuer-url` |
| Secrets Manager | **Add** | `/weredaconnect/fayda/client-secret` (use Secrets Manager, not SSM, for the client secret — following existing `pagination-secret` pattern in `api-stack.ts:225`) |
| API Gateway | **Add** | `POST /auth/fayda-verify` route |
| Lambda | **Add** | `FaydaVerifyFunction` (ARM64 AOT) |
| IAM | **Modify** | Lambda needs `ssm:GetParameter` for Fayda secrets |

---

## Offline Behavior

**Registration requires internet** (existing invariant from auth-api.md). The Fayda OIDC flow inherently requires connectivity since it redirects to Fayda's servers.

**Fallback for poor connectivity**:
- Manual Fayda ID entry (basic tier) — stored locally, verified later when online
- User can still register and trade up to 10K ETB without full Fayda verification
- When connectivity returns, app prompts: "Verify your Fayda ID to unlock unlimited trading"

**Verified status syncs**:
- `faydaVerified` and `faydaVerifiedAt` stored in Isar user schema
- Synced from server response during registration
- Escrow checks `faydaVerified` flag, not just `faydaId` string presence

---

## Localization Keys (New)

| Key | English | Amharic | Afan Oromo |
|-----|---------|---------|------------|
| `faydaVerifyTitle` | Verify Your Identity | ማንነትዎን ያረጋግጡ | Eenyummaa Keessan Mirkaneessaa |
| `faydaVerifyDescription` | Verify with Ethiopia's Fayda digital ID to unlock all features | በኢትዮጵያ ፋይዳ ዲጂታል መታወቂያ ያረጋግጡ | Fayda dijitaalaa Itoophiyaatiin mirkaneessaa |
| `verifyWithFayda` | Verify with Fayda | በፋይዳ ያረጋግጡ | Faydaan Mirkaneessi |
| `faydaVerifying` | Verifying your identity... | ማንነትዎን በማረጋገጥ ላይ... | Eenyummaa keessan mirkaneessaa jira... |
| `faydaVerified` | Identity Verified | ማንነት ተረጋግጧል | Eenyummaan Mirkanaa'eera |
| `faydaVerificationFailed` | Verification failed. Try again or enter manually. | ማረጋገጥ አልተሳካም። እንደገና ይሞክሩ ወይም በእጅ ያስገቡ። | Mirkaneessuun hin milkoofne. Irra deebi'ii yaali. |
| `faydaManualEntry` | Or enter Fayda ID manually | ወይም የፋይዳ መታወቂያ ቁጥር በእጅ ያስገቡ | Yookiin lakkoofsa Fayda harkaan galchaa |
| `faydaManualWarning` | Manual entry limits trading to 10,000 ETB per transaction | በእጅ ማስገባት ግብይትን በ10,000 ብር ይገድባል | Harkaan galchuun daldala birrii 10,000tti daangeessa |
| `faydaAlreadyLinked` | This Fayda ID is linked to another account | ይህ የፋይዳ መታወቂያ ከሌላ መለያ ጋር ተገናኝቷል | Fayda ID kun herrega biraatti walqabateera |
| `unlockFullTrading` | Verify your Fayda ID to unlock unlimited trading | ያልተገደበ ንግድ ለመክፈት የፋይዳ መታወቂያዎን ያረጋግጡ | Daldala daangaa hin qabne banuuf Fayda ID mirkaneessaa |

---

## Testing Plan

### Mobile Tests

| Test | Type | Description |
|------|------|-------------|
| OIDC URL construction | Unit | Verify authorization URL has correct params (client_id, redirect_uri, scope, state, nonce) |
| Callback parsing | Unit | Parse success and error callback URIs |
| State mismatch rejection | Unit | Reject callbacks where state doesn't match |
| Manual entry validation | Unit | Fayda ID format validation |
| Registration with verified Fayda | Widget | Full flow: verify → register → OTP |
| Registration with manual Fayda | Widget | Manual entry → register → OTP (basic tier) |
| Fayda service unavailable | Widget | Show error, offer manual fallback |
| Offline during Fayda step | Widget | Show "internet required" message |

### Server Tests

| Test | Type | Description |
|------|------|-------------|
| Token exchange success | Unit | Mock Fayda endpoint, verify claims extracted |
| Token exchange failure | Unit | Invalid code → FAYDA_001 |
| State mismatch | Unit | Wrong state → FAYDA_002 |
| Fayda unavailable | Unit | Timeout/error → FAYDA_003 |
| Duplicate Fayda ID | Unit | ID already linked → FAYDA_004 |
| Register with verification token | Unit | Token validated, user marked verified |
| Register without verification | Unit | User created with basic tier |
| Escrow gate: verified user | Integration | Verified user can create >10K ETB escrow |
| Escrow gate: basic user | Integration | Basic user blocked from >10K ETB escrow |

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| **Fayda API not yet available** for third-party integration | Blocks feature | Keep manual entry fallback; implement OIDC flow against mock endpoint; swap to real endpoint when available |
| **Fayda service downtime** | Users can't register with full verification | Manual entry fallback + retry prompt |
| **User has no Fayda biometric enrollment** | Can't complete OIDC flow | Manual entry as permanent fallback (basic tier); show help: "Visit your nearest Fayda registration center to enroll" |
| **Low-end phones can't run WebView** | Some users blocked from OIDC | Manual entry as permanent fallback (basic tier) |
| **Fayda API integration approval delay** | 2+ working days per docs | Apply early at https://id.gov.et/api; develop against mock meanwhile |
| **Pioneer territory** — no published agritech apps have integrated Fayda OIDC yet | Unknown edge cases | Thorough testing against real endpoint; budget for API quirks |
| **EthSwitch blocks direct API access** | Can't use internal EthSwitch APIs | Already mitigated — plan uses public Fayda eSignet OIDC, not EthSwitch internal APIs |
| **Data usage for OIDC redirect** | Extra KB on 3G | Minimal (~3-5 KB total); biometric processing happens on Fayda's servers |
| **AOT compliance** | Build failure | Use `System.Net.Http` (built-in) + `System.Text.Json` source gen; JWT validation via existing `System.IdentityModel.Tokens.Jwt 8.3.0` |
| **Fayda ID linked to multiple phone numbers** | Identity confusion | Server checks for existing `FaydaId` link before allowing new registration (error `FAYDA_004`) |
| **User updates/deletes Fayda biometric after registration** | Stale verification | Verification is point-in-time; add `faydaVerifiedAt` timestamp for audit; consider periodic re-verification for GA |

---

## Prerequisites

Before implementation:

1. **Apply for Fayda API access** at https://id.gov.et/api (2 working day response time)
2. **Receive**: client_id, client_secret, OIDC discovery URL
3. **Decide**: Use `url_launcher` or `flutter_custom_tabs` for OIDC redirect on mobile
4. **Confirm**: Deep link scheme `weredaconnect://` doesn't conflict with existing config

---

## Implementation Order

1. **Server**: Create `FaydaOidcService` + `FaydaVerifyFunction` with mock Fayda endpoint
2. **Server**: Modify `RegisterFunction` to accept verification token
3. **Server**: Update `UserDto` + DynamoDB schema for `FaydaVerified`
4. **Mobile**: Create `FaydaOidcService` (URL builder, callback handler)
5. **Mobile**: Rewrite `FayidaIdScreen` with OIDC button + manual fallback
6. **Mobile**: Update auth BLoC + events + Isar schema
7. **Mobile**: Add deep link configuration (AndroidManifest)
8. **Mobile**: Localization (all 3 languages)
9. **Infra**: SSM parameters + API Gateway route + Lambda
10. **Tests**: Unit + widget + integration tests
11. **Swap**: Replace mock Fayda endpoint with real when API access granted

---

## Open Questions

1. **Has the Fayda API integration application been submitted?** (Critical path — 2 day lead time)
2. **What scopes does Fayda eSignet support?** (Need: `openid`, `profile`, likely `fayda_id` — check `fayda-auth-sdk` on GitHub for reference)
3. **Does Fayda support `login_hint`?** (Pre-fill phone number from registration step)
4. **What's the Fayda ID format?** (Need regex for client-side validation of manual entry)
5. **Should basic tier (manual entry) be permanent or time-limited?** (e.g., "verify within 30 days or account suspended")
6. **What percentage of target users (rural farmers) have completed Fayda biometric enrollment?** (Affects how many users will actually use OIDC vs manual entry)

## Validation Summary

### Confirmed Valid
- OIDC via Fayda eSignet is the correct and officially supported integration path
- Reference implementations exist: `fayda-auth-go` (Go), `fayda-auth-sdk` (Node.js) on GitHub
- Two-tier approach (verified + basic) aligns with NBE mandate for >10K ETB threshold
- AOT compliance verified — all proposed server libraries are allowed
- SSM/Secrets Manager patterns exist in codebase, ready to extend
- JWT validation package already installed

### Corrections Applied (v0.2.0)
- Added: Biometric enrollment is in-person prerequisite for OIDC (cannot enroll remotely)
- Added: `url_launcher` package and AndroidManifest deep link config are missing (must add)
- Added: EscrowService.cs needs boolean check, not string presence check
- Added: `faydaIdFrontImagePath` / `faydaIdBackImagePath` become obsolete with OIDC
- Changed: Fayda client secret should use Secrets Manager (not SSM) per existing pattern
- Added: Pioneer risk — no published agritech integrations with Fayda OIDC yet
- Added: Edge cases — Fayda ID multi-phone linking, post-enrollment biometric changes

### Critical Path
```
Apply for Fayda API access (https://id.gov.et/api)
         ↓ (2 working days)
Receive client_id, client_secret, OIDC discovery URL
         ↓
Develop against mock endpoint (parallel)
         ↓
Swap mock → real Fayda endpoint
         ↓
E2E test with real Ethiopian phone numbers
```

---

**Last Updated**: 2026-03-01
**Status**: Reviewed — awaiting approval
