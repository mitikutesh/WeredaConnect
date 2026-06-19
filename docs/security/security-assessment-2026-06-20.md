# WeredaConnect Security Assessment

> **Date:** 2026-06-20
> **Conducted by:** Security-team agents (`security-architect` lead + `red-teamer`, `pentester`, `appsec-server`, `mobile-security`, `cloud-security`, `secrets-deps-scanner`)
> **Scope:** Whole application — mobile (Flutter), server (.NET 10 AOT), infra (AWS CDK), admin, website, CI. Authorized testing of the **dev** environment only.
> **Method:** STRIDE threat model + OWASP API Top 10 / MASVS / CIS AWS, with live validation against the dev API and lead re-verification of every Critical/High against source.

## Executive Summary

WeredaConnect's security architecture is fundamentally sound: the escrow state machine correctly gates release/refund behind a system-actor or admin (JWT-claim **AND** DynamoDB-role) check with optimistic-concurrency double-release protection; role isolation, sync ownership binding, server-side field stripping, HMAC-signed pagination tokens, and certificate pinning on the primary client are all implemented correctly **in source**. The single most urgent issue is **not a code defect but a deployment gap**: the F-36 role-isolation fix (commit 91d489b) is present in `main` but was never deployed to the dev Lambda, so live testing confirmed Truckers can read all crops and Farmers can read all trips (ROL-01/02, Critical) — an automatic CRITICAL FAIL per the project constitution. The second Critical is a privilege-escalation chain: the admin `change_role` action writes arbitrary role strings (including `"admin"`) to DynamoDB with no allowlist and no Cognito sync, which `EscrowService.IsSystemOrAdminAsync` then trusts for fund release (CHAIN-01). Crown-jewel money flow itself was confirmed robust under live testing (all roles blocked from self-release; IDOR blocked), and PII has a complete-but-never-invoked AES-256-GCM encryptor leaving on-device data in plaintext (MOB-01/CHAIN-03, High).

**GA verdict: NO-GO** until ROL-01/02 (deploy + access-token rejection), CHAIN-01 (role allowlist), and the auth/payment misconfigurations (DEV_OTP_BYPASS guard, WAF, MFA, staging TELEBIRR_MOCK) are closed. After those, conditional-go pending mobile PII encryption and the supply-chain/CI hardening items.

## Threat Model

| Trust boundary | STRIDE | Threat | Likelihood | Impact |
|---|---|---|---|---|
| Mobile client → API GW | Spoofing | Access token (no `custom:role`) used as bearer to bypass role scoping | Medium | High |
| Authenticated user → Crop/Trip read API | Information Disclosure | Trucker reads all crops / Farmer reads all trips (deployed Lambda lacks F-36) | **High (live-confirmed)** | High |
| Admin → User store → Escrow | Elevation of Privilege | Compromised admin promotes any user to `admin` via `change_role`, then releases escrow | Low (needs admin creds) | **Critical** |
| Escrow actor → Escrow state machine | Tampering | Release funds without delivery confirmation / double-release / amount tamper | Low | Critical |
| Farmer/Buyer → Order sync | Tampering | Non-trucker pushes `pickup_confirmed`/`in_transit`, fakes logistics → buyer confirms → release | Medium | Medium |
| MITM on 3G → token refresh | Spoofing/Info Disclosure | `refreshDio` unpinned; refresh token stolen → 7-day account takeover | Medium | High |
| Physical device access → Isar DB | Information Disclosure | PII (phone, name, location) read in plaintext from on-device DB | Medium | High |
| Attacker → /auth/login, /escrow/* | DoS / Spoofing | Credential stuffing / volumetric abuse; no WAF, only per-user app rate limit | Medium | High |
| CI runner → AWS (OIDC) | Tampering/EoP | Maestro piped curl-to-bash or mutable action tag → secret/OIDC exfil → backdoored Lambda | Low | Critical |
| Misconfig deploy → prod Lambda env | EoP / Auth bypass | `DEV_OTP_BYPASS=true` not caught by guard → universal OTP `123456` | Low | High |
| Cross-user → sync idempotency store | DoS | Forge victim's userId prefix in idempotency key → victim op silently dropped 7d | Low-Medium (live-confirmed) | Medium |
| Compromised Lambda → escrow-audit table | Repudiation | `grantWriteData` allows DeleteItem/UpdateItem → erase financial audit trail | Low | Medium |
| Admin/website browser → npm deps | XSS/RCE | react-router/Astro CVEs in admin/site → admin session theft → escrow abuse | Low-Medium | High |

## Risk Register

| ID | Severity | Title | Component | file:line | Std ref | Confidence |
|---|---|---|---|---|---|---|
| ROL-01 | **Critical** | Trucker can read ALL crop listings (deployed Lambda lacks F-36 fix) | server (deploy) | `CropFunction.cs:91-109`; `pr-validation.yml:132` | API5/API1:2023 | **Confirmed (live)** |
| ROL-02 | **Critical** | Farmer can read ALL trip listings (deployed Lambda lacks F-36 fix) | server (deploy) | `TripFunction.cs:92-110`; `pr-validation.yml:132` | API5/API1:2023 | **Confirmed (live)** |
| CHAIN-01 | **Critical** | Admin `change_role` → self-escalation to `admin` → escrow release | server | `AdminFunction.cs:607-614` → `EscrowService.cs:1099-1118` | A01/A04:2021 | **Confirmed** (corroborated SRV-03, red-team, pentester) |
| ROL-03 | High | Access token (no `custom:role`) bypasses role scoping | server | `AuthHelper.cs:62-87`; `CropFunction.cs:89`; `TripFunction.cs:90` | API2/API5:2023 | **Confirmed (live)** |
| DEPLOY-01 | High | No automated server Lambda deploy pipeline (root cause of ROL-01/02) | server/ci | `pr-validation.yml:132` | API8:2023 | **Confirmed** |
| SRV-01 / CHAIN-04 | High | `DEV_OTP_BYPASS` absent from ProductionBypassGuard; magic OTP `123456` | server+infra | `CognitoAuthService.cs:156-157`; `api-stack.ts:1345-1348` | A05/A07:2021 | **Confirmed** (all 3 server agents) |
| INFRA-01 / CHAIN-09 / SEC-07 | High | No AWS WAF on API Gateway (T-SEC-04 still open) | infra | `api-stack.ts:780-797` (no `wafv2`) | A05:2021 | **Confirmed** (cloud, scanner, red-team, pentester) |
| INFRA-02 / CHAIN-08 | High | `TELEBIRR_MOCK=true` on staging; guard fires only `isProd` | infra | `api-stack.ts:636,672`; `environments.ts:49` | A05:2021 | **Confirmed (live)** |
| MOB-01 / CHAIN-03 | High | FieldEncryptor implemented but never invoked → PII plaintext in Isar | mobile | `field_encryptor.dart`; `offline_auth_cache.dart:171`; `crop_listing_repository.dart` | MASVS-STORAGE-1 | **Confirmed** (grep: zero invocations) |
| MOB-02 / CHAIN-02 | High | `refreshDio` bypasses cert pinning → refresh-token theft on MITM | mobile | `main.dart:73-80` | MASVS-NETWORK-2 / CWE-295 | **Confirmed** |
| CI-01 / CHAIN-10 | High | Maestro CLI piped curl-to-bash in CI (supply chain) | ci | `pr-validation.yml:114` | A06:2021 / CWE-829 | **Confirmed** |
| SCA-01 / CHAIN-12 | High | react-router 7.13.1 multiple high CVEs in admin panel | admin | `admin/package.json` | A06:2021 / CWE-502/79 | **Confirmed** (npm audit) |
| SCA-02 | High | Astro 5.x / Vite high CVEs in website | website | `website/package.json` | A06:2021 | Likely (needs SSR-mode check) |
| INFRA-09 | Medium | Cognito MFA OPTIONAL (not REQUIRED) for prod mobile users | infra | `api-stack.ts:68` | A07:2021 | **Confirmed** |
| CHAIN-05 | Medium | Order logistics transitions lack trucker-only authorization | server | `SyncFunction.cs:3247-3279`, `3080-3092` | A01:2021 | **Confirmed** |
| SRV-02 / SEC-02 / CHAIN-06 | Medium | AdminAuthHelper self-parses JWT payload w/o signature verify (fallback) | server | `AdminAuthHelper.cs:48-95` | A02:2021 / CWE-347 | Likely (mitigated by API GW in current deploy) |
| INFRA-03 / SEC-10 | Medium | No CloudTrail (management-plane audit absent) | infra | `infra/lib/stacks/*` (none) | A09:2021 / CIS 3.x | **Confirmed** |
| INFRA-04 | Medium | API GW access logging not enabled | infra | `api-stack.ts:1292-1298` | A09:2021 / CIS 3.10 | **Confirmed** |
| INFRA-05 / CHAIN-13 | Medium | escrow-audit table grants DeleteItem/UpdateItem to 3 Lambdas | infra | `api-stack.ts:644,680,1195` | A01:2021 / CWE-284 | **Confirmed** |
| INFRA-06 | Medium | DynamoDB AWS-owned key, not CMK | infra | `dynamodb-construct.ts:26-34` | A02:2021 / CIS 2.7 | **Confirmed** |
| INFRA-07 | Medium | S3 uploads/access-logs buckets lack `enforceSSL` | infra | `database-stack.ts:437-482` | A02:2021 / CIS 2.1.1 | **Confirmed** |
| INFRA-08 / SEC-08 | Medium | SNS sandbox exit pending (T-SEC-09) — push alerts/SMS OTP may drop | infra | `api-stack.ts:403-455,71,139` | A05:2021 | Needs validation (no AWS console access) |
| CI-02 / CHAIN-11 | Medium | Third-party GitHub Actions pinned to mutable tags, not SHAs | ci | `.github/workflows/*` | A06:2021 / CWE-829 | **Confirmed** |
| SEC-01 / T-GA-04 / CHAIN-15 | Medium | LocalStack TLS private key in git history (and on disk) not purged | server/infra | commit `e3c2e8e`; `server/localstack-data/cache/server.test.pem.key` | A02:2021 / CWE-312 | **Confirmed** |
| SCA-03 | Medium | amazon-cognito-identity-js → vulnerable js-cookie | admin | `admin/package.json` | A06:2021 | **Confirmed** |
| MOB-03 | Medium | No OS-level pin-set in Android Network Security Config | mobile | `network_security_config.xml:25-30` | MASVS-NETWORK-2 | **Confirmed** |
| MOB-04 | Medium | Fayda deep link `autoVerify=false`, no PKCE → intent hijack | mobile | `AndroidManifest.xml:40` | MASVS-PLATFORM-1 | Likely |
| SCA-04 | Medium | fast-uri path traversal (transitive via aws-cdk-lib, build-time only) | infra | `infra/package.json` | A06:2021 | Likely |
| SEC-03 / CHAIN-07 / SEC-03-LIVE | Low→Med | Sync idempotency key userId prefix not validated → cross-user DoS | server | `SyncFunction.cs:375-388`; `SyncMetadataRepository.cs` | A01:2021 / CWE-284 | **Confirmed (live)** |
| SRV-04 / CHAIN-14 | Low | Fraud-signal status written without allowlist | server | `AdminFunction.cs:432,442` | A03:2021 / CWE-20 | **Confirmed** |
| SRV-05 | Low | `ex.Message` in CloudWatch logs (log-plane PII/path leak) | server | `SyncFunction.cs:421` | A09:2021 | **Confirmed** |
| MOB-05 | Low | `clearAllData()` doesn't delete Isar file; plaintext persists | mobile | `isar_service.dart:395-409` | MASVS-STORAGE-2 | **Confirmed** |
| MOB-06 | Low | Full sync response logged via `debugPrint` (no-op in release) | mobile | `sync_executor.dart:462` | MASVS-STORAGE-3 | **Confirmed** |
| MOB-07 | Low | Web OfflineAuthCache has no offline password verification | mobile | `offline_auth_cache_web.dart:90-91` | MASVS-AUTH-1 | **Confirmed** |
| MOB-08 | Low | Dart obfuscation not enabled in release builds | mobile | `build.gradle:79-81` | MASVS-RESILIENCE-3 | **Confirmed** |
| INFRA-10 | Low | GitHub OIDC trust uses `StringLike` wildcard (all branches/PRs) | infra | `github-actions-stack.ts:86` | A01:2021 / CIS 1.21 | Likely |
| INFRA-11 | Low | Cognito `custom:role` `mutable:true` (not client-writable; defense-in-depth) | infra | `api-stack.ts:80-83` | A01:2021 | Needs validation (mitigated) |
| SCA-05 | Low | Newtonsoft.Json transitive in **test** project only (AOT ban) | server | `WeredaConnect.Server.Tests.csproj` | A06:2021 | Needs validation (test-only) |
| SEC-04 | Low | Dev CORS: localhost + `allowCredentials:true` + temp CloudFront URL | infra | `api-stack.ts:764-796`; `environments.ts:44` | A05:2021 | **Confirmed** |
| SRV-12 / SEC-10 | Low (gap) | Audit logging present for escrow/sync but not admin actions/per-entity | server | `AuditLogger.cs`; `AdminFunction.cs` (no calls) | A09:2021 | **Confirmed** |
| MOB-09 | Info | Client-side role used for UI gating only (server authoritative) | mobile | `browse_trips_screen.dart:63` | MASVS-AUTH-1 | Confirmed (not a vuln) |

## Detailed Findings (Critical & High)

### ROL-01 / ROL-02 — Role isolation bypassed in deployed Lambda (Critical)
**Location:** `server/src/Functions/CropFunction.cs:91-109`, `TripFunction.cs:92-110`; root cause `pr-validation.yml:132`.
**Attack scenario:** Trucker registers, obtains ID token (`custom:role=Trucker`), calls `GET /crops` → HTTP 200 returning all 4 farmers' crops with prices/quantities/locations. Symmetrically, Farmer calls `GET /trips` → all truckers' phones/plates/routes. Both are automatic CRITICAL FAILs per CLAUDE.md.
**Evidence:** Pentester live-confirmed against `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com` with real test-user ID tokens (HTTP 200, cross-tenant data returned). **The F-36 fix IS present in source** — `CropFunction.cs:101-109` and `TripFunction.cs:102-110` both have the `else if (!buyer && !IsAdmin) → 403 CROP_FORBIDDEN/TRIP_FORBIDDEN` block. So the deployed binary predates commit 91d489b.
**Remediation:** Build and deploy server Lambda from `main` HEAD immediately; add `deploy-server.yml` (DEPLOY-01) so merged fixes propagate. Verify post-deploy with the same live tests.
**Re-verification verdict: Confirmed.** Deployment gap, not a source defect — the highest-priority action and a hard GA blocker.

### CHAIN-01 / SRV-03 — Admin role self-escalation chains to escrow release (Critical)
**Location:** `server/src/Functions/AdminFunction.cs:607-614` → `server/src/Services/EscrowService.cs:1099-1118`.
**Attack scenario:** Attacker with a compromised admin credential (more feasible on dev/staging where admin MFA is OFF) calls `POST /admin/users/{id}/action` with `{"action":"change_role","newRole":"admin"}`. The target's DynamoDB `role` becomes `admin`. That account then presents an `admin` JWT and calls `POST /escrow/release`; `IsSystemOrAdminAsync` passes (JWT role `admin` **AND** DynamoDB role `admin`), releasing funds with no delivery confirmation.
**Evidence (re-verified):** `AdminFunction.cs:607-615` writes `body.NewRole` directly with no allowlist (only an empty-check at :608). The change_role path does **not** sync Cognito — it's DynamoDB-only, which both creates Cognito/DynamoDB divergence and is exactly the attribute `EscrowService.cs:1106-1118` trusts. The escrow check is a genuine two-factor gate, but `change_role` lets an admin manufacture the DynamoDB factor for any account.
**Remediation:** Allowlist `{farmer,trucker,buyer}` in the `change_role` case; forbid `admin`. Admin promotion must be a separate, audited, out-of-band action (Cognito group). Enforce admin MFA on staging too. Add `AuditLogger` call for all admin actions.
**Re-verification verdict: Confirmed.** Real chain; requires prior admin compromise, so the realistic GA fix pairs the allowlist with staging MFA.

### ROL-03 — Access token bypasses role scoping (High)
**Location:** `AuthHelper.cs:62-87`; `CropFunction.cs:89`; `TripFunction.cs:90`.
**Attack scenario:** User sends their Cognito **access** token (no `custom:role` claim) instead of the ID token. `GetClaim("custom:role")` returns null; pre-fix this fell through to the full listing. Live test confirmed HTTP 200 with all crops/trips.
**Evidence:** The deployed-Lambda fix (ROL-01) adds an `else → 403` that *would* reject null-role callers. But the `HttpUserPoolAuthorizer` accepts access tokens (validates signature/expiry, not `token_use=id`), leaving an architectural surface even after redeploy.
**Remediation:** (1) Deploy ROL-01 fix. (2) Explicitly reject null/empty role with 403 in both handlers. (3) Validate `token_use=id` in handler or constrain authorizer identity source.
**Re-verification verdict: Confirmed (live).** Largely closed by the redeploy, but add the explicit null-role reject and token_use check for defense-in-depth.

### SRV-01 / CHAIN-04 — DEV_OTP_BYPASS not in ProductionBypassGuard (High)
**Location:** `CognitoAuthService.cs:156-157`; `api-stack.ts:1345-1348`.
**Evidence (re-verified):** `CognitoAuthService.cs:157`: `if (devBypass == "true" && otpCode == "123456")` → `AdminConfirmSignUpAsync` + sets `phone_number_verified=true`. The guard's `FORBIDDEN_ENV_VARS` array (`api-stack.ts:1346-1348`) lists only `DEV_FAYDA_BYPASS` and `TELEBIRR_MOCK`. `DEV_OTP_BYPASS` is absent. (The guard logic itself was recently corrected to resolve the Lazy token and inspect `.variables` — good.)
**Attack scenario:** A hotfix accidentally sets `DEV_OTP_BYPASS=true` on a prod Lambda; `cdk synth` does not error; attacker registers any phone and confirms with `123456` → bulk unverified accounts.
**Remediation:** Add `"DEV_OTP_BYPASS"` to `FORBIDDEN_ENV_VARS`; add a unit test mirroring T-SEC-01/02; replace the fixed `123456` with a Secrets Manager value.
**Re-verification verdict: Confirmed.** One-line guard fix.

### INFRA-01 / CHAIN-09 / SEC-07 — No WAF on API Gateway (High, T-SEC-04 still open)
**Location:** `api-stack.ts:780-797`.
**Evidence (re-verified):** `grep -rni "wafv2|CfnWebACL|WebACLAssociation" infra/lib` returns **zero** matches. Only coarse global throttle (25 rps / burst 50) exists; app-layer rate limiting is per-user and bypassed by multi-IP attacks.
**Attack scenario:** Credential stuffing on `/auth/login` with leaked Ethiopian numbers; volumetric `/escrow/initiate`; no IP-reputation or managed-ruleset blocking.
**Remediation:** Attach `wafv2.CfnWebACL` (REGIONAL) with AmazonIpReputationList + CommonRuleSet + KnownBadInputsRuleSet + rate-based rule (~100/5min/IP on `/auth/*`,`/escrow/*`) via `CfnWebACLAssociation`. ~$6/mo.
**Re-verification verdict: Confirmed.**

### INFRA-02 / CHAIN-08 — TELEBIRR_MOCK=true on staging (High)
**Location:** `api-stack.ts:636,672`; `environments.ts:49`.
**Evidence (re-verified):** `...(envConfig.isProd ? {} : { TELEBIRR_MOCK: "true" })` at both escrow lines; `environments.ts:49` sets staging `isProd:false`. The guard is only attached when `isProd`, so staging escrow runs on the mock gateway. Pentester live-confirmed `telebirrRef='MOCK-HOLD-...'` on a staging-style initiate.
**Remediation:** Gate mock on `name === 'dev'` only; extend the guard to staging (`isProd || name==='staging'`); use a real Telebirr sandbox for staging UAT.
**Re-verification verdict: Confirmed.**

### MOB-01 / CHAIN-03 — PII plaintext in Isar despite complete encryptor (High)
**Location:** `field_encryptor.dart`; `offline_auth_cache.dart:171`; repositories.
**Evidence (re-verified):** `grep` for `fieldEncryptor`/`FieldEncryptor.instance`/`.encrypt(`/`.decrypt(` across `mobile/lib/features` and `mobile/lib/core/auth` returns **zero** invocations. The AES-256-GCM class is fully built and keyed (Keystore) but dead code. `offline_auth_cache.dart:171` writes user fields via `isar.users.put(user)` in plaintext.
**Attack scenario:** Physical/ADB access to a shared rural Android device → extract Isar file → read all phones/names/locations in plaintext.
**Remediation:** Wire `encrypt`/`decrypt` into repositories for PII fields using the existing `legacyPlaintextAllowed` migration path; also delete the physical Isar file in `clearAllData()` (MOB-05).
**Re-verification verdict: Confirmed.**

### MOB-02 / CHAIN-02 — refreshDio bypasses certificate pinning (High)
**Location:** `mobile/lib/main.dart:73-80`.
**Evidence (re-verified):** `refreshDio = Dio(BaseOptions(...))` with no `httpClientAdapter` and no `applyCertPinning(refreshDio)` call; it carries the refresh token to `/auth/refresh` (lines 85-90). The primary `ApiClient().dio` is pinned, but this separate instance uses default system-CA trust.
**Attack scenario:** MITM on a 3G network with a rogue CA intercepts `/auth/refresh`, harvests the 7-day refresh token → persistent account takeover.
**Remediation:** `cert_pinning.applyCertPinning(refreshDio)` immediately after construction, or reuse `ApiClient().dio.httpClientAdapter`. Add a test asserting the adapter is the pinned one.
**Re-verification verdict: Confirmed.**

### CI-01 / CHAIN-10 — Maestro piped curl-to-bash in CI (High)
**Location:** `.github/workflows/pr-validation.yml:114`.
**Evidence (re-verified):** Line 114 `curl -Ls "https://get.maestro.mobile.dev" | bash`. Runner has AWS OIDC and secrets.
**Remediation:** Download a pinned GitHub release, verify SHA-256, then install; or a digest-pinned container.
**Re-verification verdict: Confirmed.**

### DEPLOY-01 — No automated server deploy pipeline (High)
**Location:** `pr-validation.yml:132` (`# TODO: Add deployment script`).
**Evidence (re-verified):** Confirmed present; no `deploy-server.yml`. This is the root cause of ROL-01/02 and means every merged security fix is undeployed indefinitely.
**Remediation:** `deploy-server.yml` on push to `main`: `dotnet publish -c Release -r linux-arm64` → zip → `aws lambda update-function-code`; verify `CodeSha256`.
**Re-verification verdict: Confirmed.**

### SCA-01 / CHAIN-12 — react-router 7.13.1 high CVEs in admin (High)
**Location:** `admin/package.json`.
**Evidence:** npm audit confirmed GHSA-49rj-9fvp-4h2h, -8646-j5j9-6r62, et al. The RCE/turbo-stream and most XSS CVEs apply to SSR/framework mode; the admin runs SPA/library mode, so realistic impact is the CSRF/XSS subset — and admin uses Bearer-from-localStorage (not cookies), reducing CSRF exploitability. Still High due to admin's escrow/role surface.
**Remediation:** Upgrade react-router ≥7.15.1; add `npm audit --audit-level=high` as a CI gate; fix js-cookie via cognito-identity-js bump (SCA-03).
**Re-verification verdict: Confirmed (presence); exploitability moderated by SPA-mode + Bearer-auth — fix anyway.**

### SCA-02 — Astro/Vite high CVEs in website (High)
**Location:** `website/package.json`.
**Evidence:** npm audit; SSR-dependent CVEs (server islands, prerendered error SSRF). Public marketing site; impact depends on whether SSR features are enabled.
**Remediation:** Confirm fully-static deploy; `npm audit fix` (Astro upgrade is a major bump — verify).
**Re-verification verdict: Likely — needs confirmation that website is static-only.**

## Prior Pentest Status

| Item | Status | Evidence |
|---|---|---|
| T-SEC-01 dev-bypass guard (DEV_FAYDA_BYPASS) | **Still holds** | `api-stack.ts:1346` lists `DEV_FAYDA_BYPASS`; guard logic resolves `.variables` correctly; infra test asserts absence in prod |
| T-SEC-02 TELEBIRR_MOCK guard | **Still holds (prod); gap on staging** | Guard blocks in prod (`:1347`); `EscrowFunction.cs:564-580` throws without real gateway. **But staging gets mock** (INFRA-02) |
| T-SEC-03 health-endpoint data leak | **Still holds** | `HealthCheckFunction.cs:35-54` returns only `{status,timestamp}` |
| T-SEC-04 WAF on API GW | **Still pending** | No `wafv2` anywhere (INFRA-01) — grep zero matches |
| T-SEC-05 /version.json exposure | **Still holds** | Blocked via CloudFront Function 404; pentester live-confirmed 404 |
| T-SEC-06 localhost CORS in prod | **Still holds** | `api-stack.ts:765-778` branches on `isProd`; prod = exact HTTPS subdomains; live evil.com origin got no ACAO header |
| T-SEC-07 admin token validity | **Still holds** | 15-min access/id tokens (`api-stack.ts:117-121,189-193`) |
| T-SEC-08 admin MFA | **Still holds (prod)** | Admin pool `Mfa.REQUIRED` in prod (`:136`); OFF in dev/staging (relevant to CHAIN-01 on staging) |
| T-SEC-09 SNS sandbox exit | **Still pending** | No `CfnSMSSandboxPhoneNumber`/exit evidence (INFRA-08); not console-validatable here |
| T-SEC-10 E2E audit logging | **Partial** | `AuditLogger` covers escrow + sync batch; **no admin-action / per-entity logging; no CloudTrail** (SRV-12, INFRA-03) |
| T-GA-03 cert pinning activation | **Partial / regressed surface** | Active on primary `ApiClient.dio`; **bypassed for refreshDio** (MOB-02) |
| T-GA-04 git history purge | **Still pending** | Key in history `e3c2e8e`; **also still on disk** `server/localstack-data/cache/server.test.pem.key` (`-----BEGIN PRIVATE KEY-----`) |

## Defense-in-Depth Gaps & Strategic Recommendations (prioritized)

1. **Ship the fix that already exists.** Deploy server Lambda from `main` (closes ROL-01/02/03) and stand up `deploy-server.yml`. Nothing else matters if merged security fixes never run. Add a post-deploy live-test gate.
2. **Close the admin→escrow chain.** Role allowlist in `change_role`; admin MFA REQUIRED on staging too; per-action `AuditLogger` for all `/admin/*`.
3. **Auth & payment misconfig sweep (GA blockers):** add `DEV_OTP_BYPASS` to the guard; gate `TELEBIRR_MOCK` to dev-only + extend guard to staging; attach WAF; make mobile MFA REQUIRED in prod (or enforce step-up before escrow release).
4. **Edge controls the app layer can't replace:** WAF (rate/IP-reputation/managed rules) is the right place for credential-stuffing and volumetric defense — the per-user DynamoDB limiter is inherently multi-IP-bypassable.
5. **Mobile data-at-rest & in-transit:** wire FieldEncryptor; pin refreshDio; add OS-level pin-set; delete Isar file on clear.
6. **Supply chain:** pin Maestro by SHA; pin all GitHub Actions to commit SHAs; scope OIDC trust to `ref:refs/heads/main`; `npm audit` CI gate; upgrade react-router/Astro/js-cookie.
7. **Audit & forensics integrity:** add CloudTrail (with log-file validation + Object Lock), API GW access logging, and restrict escrow-audit table to `PutItem`-only (replace `grantWriteData`).
8. **Server hardening tail:** validate idempotency-key userId prefix == authenticated user (closes cross-user DoS); add trucker-only auth for `pickup_confirmed`/`in_transit`; allowlist fraud-signal status; log `ex.GetType().Name` not `ex.Message`; remove or hard-gate the AdminAuthHelper JWT self-parse fallback (move groups to a string authorizer claim via pre-token-gen trigger).
9. **Crypto governance:** move DynamoDB to a CMK (or at least AWS-managed key) for revocation/audit; `enforceSSL` on S3 buckets; purge T-GA-04 key from history and rotate.

## Audit Coverage & Limitations

**Covered:** server (`Functions/`, `Services/EscrowService.cs`, `Helpers/Auth*`, `AuditLogger`, `PaginationTokenHelper`), mobile (auth/storage, cert pinning, sync, repositories, manifest/gradle), infra (api/database/web/github-actions/monitoring stacks, constructs, environments), CI workflows, and dependency scans (npm audit, gitleaks, dotnet list).

**Live-validated (by pentester, against dev API with real test-user tokens):** ROL-01/02/03 (cross-role read leaks), escrow self-release blocked for all roles, escrow IDOR blocked, buyerId-impersonation blocked, crop/trip write-role enforcement, idempotency-key poisoning DoS, injection in query params, tampered nextToken rejected, auth rate-limit, /version.json 404, evil-origin CORS, TELEBIRR_MOCK active on staging. **Lead re-verification:** every Critical/High file:line re-read in source plus targeted greps confirming: F-36 fix present in source (so ROL-01/02 = deploy gap), `change_role` no-allowlist + no Cognito sync, escrow two-factor admin gate, DEV_OTP_BYPASS guard omission, WAF/CloudTrail absence, FieldEncryptor zero invocations, refreshDio unpinned, order-transition trucker gap, Maestro/deploy-TODO, staging mock, key on disk + in history.

**Static-only (not live-validated):** all mobile findings (MOB-01..08), CHAIN-01 end-to-end (admin-credential prerequisite not exercised), CloudTrail/MFA/CMK/S3 infra posture, react-router/Astro CVE exploitability, AdminAuthHelper fallback (no non-API-GW path exists today).

**Needs follow-up / could not confirm here:** SNS sandbox status and SMS-OTP deliverability (requires AWS console — INFRA-08/SEC-08); whether the website enables Astro SSR features (gates SCA-02 severity); confirmation that Newtonsoft.Json is excluded from Lambda publish artifacts (SCA-05); `me-south-1` prod stack behavior (only dev `eu-north-1` was live-reachable).

**⚠️ Test artifacts left in dev DynamoDB** by the pentester (a mock escrow, a poisoning test crop, a stored cross-user idempotency key) should be cleaned up — no real funds involved.

**Confidence posture:** Two Criticals (ROL-01/02 live-confirmed deploy gap; CHAIN-01 code-confirmed chain). No Highs downgraded, but SCA-01 exploitability explicitly moderated (SPA mode + Bearer auth) and SCA-02 (SSR-dependent) without lowering severity; SRV-02/CHAIN-06 flagged Likely (not Confirmed) because no current invocation path bypasses the API Gateway authorizer.

---

*Generated by the WeredaConnect security-team agents. Re-run anytime with the `security-audit` workflow or by invoking the individual agents (`security-architect`, `red-teamer`, `pentester`, `appsec-server`, `mobile-security`, `cloud-security`, `secrets-deps-scanner`).*
