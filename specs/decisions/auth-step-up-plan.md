# Authentication Step-Up and Authorization Hardening

## Version
1.1.0 | 2026-09-29 (payment step-up tiers, see §6)

## Status
Implemented on branch `feat/auth-hardening` (server, infra, mobile, admin). Not deployed.

---

## 1. Decision

Keep one password login per session and put strong authentication where money and identity move:

| Layer | Decision |
|-------|----------|
| Login | Phone + password through the server only. The public Cognito client cannot run a password login itself, so the server's lockout, suspension and audit rules always apply. |
| Session | Silent auto-login stays (rural, shared-device, offline reality). An app lock (device PIN or biometric) guards cold start and returns from background. |
| Identity proof | Phone ownership is proven by SMS code at registration unless Fayda released the same number. Fayda identity is proven by OIDC with server-held state, nonce and PKCE, at registration or later from the profile. |
| Step-up | A payment up to 10,000 ETB needs a password entered within the last 5 minutes (Cognito `auth_time`, `AUTH_040`). A payment above 10,000 ETB needs a Fayda login within the last 5 minutes instead (`AUTH_041`). A dispute at or above 10,000 ETB needs the fresh password. (1.1.0; before, only actions at or above 10,000 ETB asked, and always for the password.) |
| Payout | Money goes only to the registered phone of a verified party. Above 10,000 ETB the recipient must be Fayda-verified; otherwise the release is blocked for an admin. |
| Admin | TOTP MFA required in prod, optional in dev and staging (SMS MFA codes are undeliverable there). Only the admin pool's `admin` group may settle escrows. Admin verdicts are recorded and settled by the cron, so the admin Lambda never moves money. |

Fayda on every login was rejected: it fails on connectivity and provider uptime, and it adds friction for users who log in daily on shared phones. ASP.NET Core Identity was rejected: Cognito already owns password hashing, lockout and reset, and the server is Native AOT.

---

## 2. What changed

### Registration and phone ownership
- `POST /auth/register` returns `202 otpRequired` unless the Fayda proof carries the same phone. `POST /auth/verify-otp` (code + password) confirms the user and mints tokens; `POST /auth/resend-otp` re-issues. Codes are HMAC-SHA256 hashed in the rate-limits table, valid 5 minutes, 60 s resend cooldown, 3 attempts, 10 sends per day.
- `isPhoneVerified` is true only after the code, or a matching Fayda phone. An unconfirmed registration of the same phone is replaced by the next attempt (Cognito user and profile row); a verified profile is never replaced. verify-otp checks the password before consuming the code, and the code is bound to the registration it was issued for, so a re-registration cannot be confirmed by the real owner's code.
- The Fayda verification token carries the Fayda phone and a token id; registration burns the id (single use).

### Fayda OIDC
- The server mints `state`, `nonce` and the PKCE verifier (`POST /auth/fayda-start`, `POST /user/fayda-start`) and returns the full authorize URL. The app carries no Fayda client configuration.
- The exchange checks the stored state (single use, 10 minutes), redirect URI, PKCE verifier and the ID token nonce. Endpoints come from the provider's discovery document.
- `POST /user/fayda-link` verifies an existing account. A matching Fayda phone also proves the phone.
- Dev runs against the mock eSignet provider (`tools/fayda-mock`), selected by `faydaMode: 'mock'` in `infra/lib/config/environments.ts`; the server code is identical in both modes.

### Money gates
- Order `payment_submitted`: both parties phone-verified, both Fayda-verified above 10,000 ETB (`ORDER_KYC_REQUIRED`), then the payment step-up: fresh `auth_time` up to 10,000 ETB (`AUTH_040`), fresh Fayda login above it (`AUTH_041`). See §6.
- `POST /escrow/initiate`: the same payment step-up tiers. `POST /escrow/dispute`: `AUTH_040` at or above the threshold. Delivery disputes through `/sync` use the linked order's total.
- Release resolves the farmer's and trucker's registered phone and blocks unverified recipients (`ESCROW_029`). Refunds go to the buyer's registered phone.
- Telebirr webhook: unparseable timestamp rejected, `merch_code` must match, payer phone (`msisdn`) compared with the buyer's phone; mismatches are audited and rejected when `TELEBIRR_REQUIRE_PAYER_MATCH=true`.

### Sessions and accounts
- Suspend disables the Cognito user and signs out every session; unsuspend re-enables; `POST /auth/refresh` refuses suspended profiles and revokes the refresh token.
- `change_role` rewrites `custom:role` in Cognito and signs the user out.
- Per-IP caps on login, register, OTP, Fayda, password reset and refresh.
- Device tracking: `X-Device-Id` and `X-Device-Name` on login and register; an unknown install is audited and, with `NEW_DEVICE_SMS_ENABLED=true`, announced by SMS. `DELETE /device-tokens` unregisters the push endpoint at logout.

### Authorization
- Sync create conflicts echo the existing crop, trip, order or delivery only to its owner or a party.
- `/escrow/release` and `/escrow/refund` sit on the admin authorizer and require the admin group. Resolved disputes (`dispute_resolved`) are settled by the hourly cron.

### Mobile
OTP screen, step-up dialog, KYC dialog, Fayda verification screen, device identity headers, local auth gate on QR confirm, app lock, device-token unregister at logout, session hygiene fixes.

### Admin web
Login drives TOTP setup and challenge. The admin pool requires MFA in prod and makes it optional in dev and staging: the pool has an SMS configuration, so Cognito always keeps SMS_MFA as a factor and SMS-challenges any REQUIRED-pool admin without a TOTP, and SMS codes are undeliverable outside prod.

---

## 3. Configuration

| Setting | Where | Default |
|---------|-------|---------|
| `STEP_UP_THRESHOLD_ETB` | Lambda env | 10000 |
| `STEP_UP_MAX_AGE_SECONDS` | Lambda env | 300 |
| `SMS_ENABLED` | Lambda env | true (dev: false) |
| `DEV_OTP_BYPASS` | Lambda env (dev only) | accepts 123456 |
| `NEW_DEVICE_SMS_ENABLED` | Lambda env | unset (off) |
| `TELEBIRR_REQUIRE_PAYER_MATCH` | CDK context `telebirrRequirePayerMatch` | off |
| `faydaMode` | `environments.ts` | dev: mock |

Production guard: `DEV_FAYDA_BYPASS`, `DEV_OTP_BYPASS`, `TELEBIRR_MOCK` and `FAYDA_MOCK` are refused in a production deployment by the CDK guard, and the OTP and Fayda bypasses are additionally ignored at runtime by a Lambda whose function name carries the production prefix (`DevBypass`).

---

## 4. Deploy notes

- New Lambda `auth-verify-otp`; new routes `/auth/verify-otp`, `/auth/resend-otp`, `/auth/fayda-start`, `/user/fayda-start`, `/user/fayda-link`, `DELETE /device-tokens`.
- The mobile client's `USER_PASSWORD_AUTH` flow is disabled; `ADMIN_USER_PASSWORD_AUTH` is enabled. Login Lambdas need `COGNITO_USER_POOL_ID`.
- Admin pool MFA is REQUIRED in prod and OPTIONAL in dev and staging (REQUIRED there SMS-challenged every admin with an undeliverable code). Prod admins get an SMS_MFA challenge until they enrol TOTP, so prod SNS must be out of the SMS sandbox before the first prod admin signs in.
- Dev deploys the mock Fayda provider (Function URL, DynamoDB codes table, SSM signing key generated on first call).
- SNS SMS is still sandboxed: keep `SMS_ENABLED=false` and `DEV_OTP_BYPASS=true` in dev; exit the sandbox before staging relies on codes.
- All public auth routes are wired with API Gateway payload format 1.0 (the handlers dispatch on the path and read the caller IP for the per-IP caps).
- Scripts: `scripts/seed-test-users.sh` verifies logins with `ADMIN_USER_PASSWORD_AUTH` (the mobile client no longer allows a password login); `scripts/seed-admin-user.sh` clears the admin's MFA preferences each run so the non-prod password login issues tokens; `scripts/e2e_admin.py` also answers `MFA_SETUP` and, with `ADMIN_TOTP_SECRET`, `SOFTWARE_TOKEN_MFA`.

---

## 5. Follow-ups

- `private_key_jwt` client authentication once the production Fayda credentials arrive.
- Confirm the payer phone field name in Telebirr's live notify payload before enabling `TELEBIRR_REQUIRE_PAYER_MATCH`.
- Fayda re-verification after 12 months (verifayda-ekyc-integration-guide.md).
- Sync idempotency keys scoped to the caller (SEC-03) and `TELEBIRR_MOCK` on staging (INFRA-02) remain open.
- Native-speaker review of the new Amharic and Afan Oromo strings (mobile and admin).

---

## 6. Payment step-up tiers (1.1.0, 2026-09-29)

**Decision.** The password proves who holds the session; Fayda proves who the person is. Above the NBE threshold the payment is confirmed with Fayda, and below it with the password, on every payment.

| Payment | Proof | Error |
|---------|-------|-------|
| up to and including 10,000 ETB | password within 5 minutes (`auth_time`) | `AUTH_040` |
| above 10,000 ETB | Fayda login within 5 minutes; a password does not count | `AUTH_041` |

**Where the Fayda proof lives.** Every successful `POST /user/fayda-link` writes `faydaAuthenticatedAt` on the user row (server-side, `[JsonIgnore]`, never in an API response). The link only succeeds for the account owner's identity (`FAYDA_005/006/007`), so the proof is bound to the person on file, not just to any valid Fayda holder. `/sync` reads it from the buyer row the KYC gate already loads; `/escrow/initiate` reads the buyer row too (fail closed without the user table).

**Client.** `AUTH_041` keeps a queued payment pending like `AUTH_040`; the prompt opens the Fayda screen in payment mode, and success fires the same step-up-completed signal that retries the queue. On web the payment screen resubmits directly. A buyer not yet Fayda-verified gets `buyer_fayda` first; that link is also the fresh login, so one Fayda round trip covers both.

**Rejected.** A single-use grant per payment (extra table and IAM grant; the 5-minute window matches the password rule). Fayda on top of the password above 10,000 ETB (user asked for Fayda instead of the password).

**Trade-offs.** One Fayda login covers any payments made in the next 5 minutes, the same as the password window. Small payments now ask for the password unless the user signed in within 5 minutes.
