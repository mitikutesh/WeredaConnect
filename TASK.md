# TASK.md - Current Mission

## Current Status

**Phase R4: COMPLETE** (started Feb 17, 2026)
**Branch:** `fix-feat` (current), `telebirr-mock` merged to `main` via PR #15
**Last PO Audit:** 2026-03-19 (validated with automated build/test/security checks)
**Adversarial Audit (2026-06-17):** 34 + 71 confirmed defects fixed on `fix/fix-bug` (all build/test gates green). Cross-module / decision-gated items triaged to the backlog (`specs/features/BACKLOG.md` §9): **F-32/F-33/F-34** (manual-fix bugs) and **F-35/F-36/F-37** (re-verification flagged items). **F-36** (restrict /crops & /trips browse-all to Buyer+Admin — a CRITICAL-domain cross-role leak) is now **✅ fixed under Option A** (403 for non-buyers + unauthenticated; server 716 tests pass; contracts reconciled). Remaining backlog: **F-32, F-33, F-34, F-35, F-37** (F-35/F-37 await PO decisions). Re-verification pass complete (76 confirmed → 72 fixed incl. F-36, 1 false positive, 3 flagged remaining).
**MVP Completion:** ~95%
**Launch Target:** Sprint 1 (ship-ready) → Sprint 2 (security) → Sprint 3 (production)
**Sprint Plan:** `specs/features/sprint-plan-2026-03-19.md`
**Isar Hardening Roadmap (2026-05-13):** `specs/features/isar-hardening/README.md` — 7-sprint program closing all 17 Isar gaps surfaced in the May security review. Supersedes the 4h estimate for `S3-04` below.

**Buyer Intent Matching (2026-06-19, branch `buyer-intenet`):** 5-step series.
Steps 1–4 committed (mobile weighted scorer → count-delta alerts → haversine geo →
server `GET /match` + contract → web path uses `/match`). **Step 5 = push alerts (FCM via
SNS)** in progress: docs done (`.ai/push-notifications-setup.md`,
`specs/architecture/push-notifications-architecture.md`), code pending (intent sync to
server, `POST /device-tokens`, crops Stream → match-on-insert Lambda, mobile
`firebase_messaging`). Requires a Firebase project + FCM secret before deploy.

**In-App Messaging (2026-07-09):** context-scoped chat (buyer↔farmer, buyer↔trucker)
+ phone Call/Copy + FCM push on new message, built across infra/server/mobile
(create-only `message` entity, `messages` table + stream, `MessageFunction`,
`MessageStreamFunction`, `features/messaging/**`, shared `contact_actions_sheet`).
**This also finishes the mobile FCM client** (Step 5 last-mile: `firebase_*` deps,
`push_notification_service.dart`, Gradle/manifest). Contract `specs/contracts/messaging.md`;
runbook `.ai/push-notifications-setup.md` updated. Not committed/deployed — needs
`cdk deploy` + manual SNS platform app. Deferred: tap-to-thread deep-link,
order/delivery detail Message buttons.

**Three-Party Escrow, Commitments and Reputation (design approved 2026-09-30, P0 in progress):**
- **Tasks:** EPIC ESC below (ESC-00 to ESC-47). ESC-00 is done: decisions D1 to D9 are answered in spec §14.
- **ESC-01** (live order funding) is done: #91 merged 2026-10-01 and deployed to dev.
- **Design:** `specs/features/three-party-escrow-commitments-reputation.md`.
- **Code survey:** `specs/reports/escrow-flow-survey-2026-09-29.md`.

**Next steps:** the rest of P0 money safety (ESC-04, ESC-06, ESC-07, ESC-09, ESC-10; ESC-02, ESC-03, ESC-05, ESC-08, ESC-12 part A, ESC-13 and ESC-44 merged 2026-10-08/09), plus ESC-45 (Telebirr redelivery rules) and the ESC-44 live sandbox run. Live payouts now fail closed into the admin manual payout queue (ESC-02); live money stays off until ESC-45 lands and the ESC-44 sandbox run passes. ESC-12 part B is FUP-01.

---

## PO Audit Summary (2026-03-19)

### What Changed Since Last Audit (Mar 5)

Branch `telebirr-mock` merged to `main` via PR #15. Subsequent work on `fix-feat`:
- **Role-based visibility fixes** — server-side enforcement on crops/trips APIs
- **Web platform improvements** — response parsing, auth persistence, Dio fixes, trip matching
- **Admin dashboard alignment** — API response keys, escrow names enrichment
- **Playwright E2E tests** — 27 tests for admin dashboard
- **Security hardening** — input validation, auth error mapping, rate limiting fixes
- **Deploy script** — web app build + S3 deploy added
- **AI infrastructure** — centralized completeness rules, agent restructuring

### Audit Corrections (validated findings)

Previous reports listed 7 web bugs — **validation confirmed all were already fixed**:
- Web repos use correct fallback pattern `data['crops'] ?? data['items']`
- `kIsWeb` guard exists on all Isar access paths
- Web matching service fully implemented with API calls
- `AuthorizerFunction.cs` does not exist (false alarm)

**Actual remaining issues**: 3 test failures, 1 hardcoded string, dead JwtValidator code, security gaps.

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | DONE | Feb 2026 |
| R2: Auth + Data Models | DONE | Feb 14, 2026 |
| R3: Sync + First Feature | DONE (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow + Fraud | DONE | Mar 2026 |

### R4 Feature Status (Verified Mar 19)

| Feature | Server | Mobile | Web | Infra | Tests | Overall |
|---------|--------|--------|-----|-------|-------|---------|
| F-11 Trip Listings | DONE | DONE | DONE | DONE | DONE | 100% |
| F-12 Delivery Verification | DONE (presigned S3) | DONE (5 screens) | DONE | DONE | DONE | 100% |
| F-13 Telebirr Escrow | DONE (mock gateway) | DONE (4 screens) | DONE | DONE | DONE | 100% |
| F-14 Fraud Detection | PARTIAL (4 rules + signal writes; risk scoring, tiers, holds NOT built, see EPIC FRD) | DONE (error handling) | N/A | DONE | DONE | ~50% |
| F-15 Conflict Resolution | DONE | DONE (4 widgets) | DONE | N/A | DONE (49 tests) | 100% |
| F-18 S3 Logging | DONE | N/A | N/A | DONE | N/A | 100% |

### Build Health (Verified Mar 19)

| Component | Build | Tests | Issues |
|-----------|-------|-------|--------|
| Server (.NET 10) | 0 errors, 0 warnings | 684 pass, 0 fail | Clean |
| Mobile (Flutter) | 0 errors, 0 lint issues | 732 pass, 3 fail | Trip BLoC cancel tests |
| Flutter Web | Builds clean | No dedicated tests | Wasm warnings (expected) |
| Admin (React) | Builds clean | 27 E2E pass | Clean |
| Infra (CDK) | Synths clean | — | Clean |

---

## Sprint Plan

Full details: `specs/features/sprint-plan-2026-03-19.md`

### Sprint 1: Ship-Ready (3-4 days)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S1-01 | Fix 3 failing trip BLoC tests | 1h | Pending |
| S1-02 | Fix hardcoded payment account string | 30min | Pending |
| S1-03 | Clean up dead JwtValidator code | 30min | Pending |
| S1-04 | Update stale STATE.md and TASK.md | 1h | Pending |
| S1-05 | Deploy to dev environment | 2h | Pending |
| S1-06 | Merge fix-feat to main | 1h | Pending |
| F-34 | Login role-default fix — role-visibility breach (BACKLOG §9, PRIORITY #1) | ~1d | ✅ Done (2026-06-18) |
| F-36 | Restrict /crops & /trips browse-all to Buyer+Admin — cross-role leak (BACKLOG §9; Option A, 403) | ~0.5-1d | ✅ Done (2026-06-18) |

### Sprint 2: Security Hardening (5-7 days)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S2-01 | Activate certificate pinning | 2h | Pending |
| S2-02 | Configure AWS WAF | 3h | Pending |
| S2-03 | Structured audit logging (Phase 1) | 2 days | Pending |
| S2-04 | Audit logging — compliance (Phase 2) | 3 days | Pending |
| S2-05 | Git history purge | 1h | Pending |
| S2-06 | Exit SNS sandbox | 1h + wait | Pending |
| F-33 | Web escrow by-delivery endpoint (BACKLOG §9) | ~1-1.5d | 🟡 all code committed (`a6363c4` server+infra, `16aae1f` mobile); awaiting deploy+verify |
| F-32 | Registration role-field wiring — mobile+server+contracts (BACKLOG §9; Option A — buyer fields server-side, license local-only) | ~1.5-2.5d | ✅ Done (2026-06-18) |
| F-35 | Admin audit-log global feed — server-only bounded Scan via "all" sentinel (BACKLOG §9; decision: Scan over GSI) | ~1-1.5d | ✅ Done (2026-06-18) |

### Sprint 3: Production Readiness (2-3 weeks)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S3-01 | Replace mock Telebirr with real API | TBD | 🟡 In progress — offline prep done on `feat/telebirr-sandbox-e2e` (gated `enableTelebirrLive` infra: VPC+t4g.nano NAT instance+EIP, public webhook route, creds secret; mobile opens `toPayUrl`+polls+deep-link). **Blocked** on: Telebirr RSA public key + source-IP allow-list confirmation (sandbox `196.188.120.3:38443` unreachable from abroad), server deploy (DEPLOY-01), and the payout/refund disbursement leg. Runbook: `.ai/telebirr-testing.md` Tier 3. |
| S3-02 | Wire real Cognito auth | 1-2 weeks | Pending |
| S3-03 | Reduce offline session TTL to 48h | 1h | Pending |
| S3-04 | Implement Isar field-level encryption | **expanded — see Isar Hardening roadmap** | Pending |
| S3-05 | Add Flutter web E2E tests | 4h | Pending |
| S3-06 | OTP resend + forgot password | 8h | Pending |
| F-37 | Fix E2E webapp auth-injection (false-green) — Option C prod-gated test seam (BACKLOG §9; seam absent in prod builds, verified) | ~1-1.5d | ✅ Done (2026-06-18) |
| F-38 | Trucker driving-license S3 upload (KYC) — Scope B follow-up to F-32; reuse delivery-photo presigned-S3 pipeline (BACKLOG §9) | ~1.5-2d | Pending |

---

## Isar Hardening Roadmap (2026-05-19 → 2026-08-20)

**Source:** `specs/features/isar-hardening/` · **Status:** Proposed, awaiting PO sign-off
**Trigger:** May 2026 deep security review uncovered 17 gaps; the 4-hour `S3-04` estimate was insufficient.

| Sprint | Dates | Goal | Pts | Risk |
|--------|-------|------|-----|------|
| HS-1 | May 19 – May 30 | Encryptor v1: AAD + prefix + key-integrity HMAC | 15 | ✅ **COMPLETE** (PRs #18 #19 #20 #21 #22) |
| HS-2 | Jun 02 – Jun 13 | Wire encryption into User + Crop repositories | 18 | High |
| HS-3 | Jun 16 – Jun 27 | One-shot v1→v2 migration (dev-cohort gated) | 16 | **Critical** |
| HS-4 | Jun 30 – Jul 11 | Sync queue session gating + idempotency wiring | 17 | Medium |
| HS-5 | Jul 14 – Jul 25 | Biometric gate + WorkManager hardening | 14 | Medium |
| HS-6 | Jul 28 – Aug 08 | Observability + release tooling | 13 | Low |
| HS-7 | Aug 11 – Aug 20 | Key rotation + Keystore platform channel | 12 | Low |

**Detail:**
- Stories: `specs/features/isar-hardening/sprints.md` (28 JIRA-ready stories) · CSV: `specs/features/isar-hardening/stories.csv`
- Migration rollout runbook: `specs/features/isar-hardening/sprint-3-rollout.md`
- Threat model + acceptance specs: `docs/mobile/isar-implementation.md`

**Top open PO decisions before HS-1 kickoff:**
1. Pin `cryptography: 2.7.0` exact (no `^`) — confirm
2. HMAC pepper strategy for `phoneSearchHash` — default: app-static secret via build env
3. Approve airtime budget for HS-3 dev cohort (15 users × 50 ETB = 750 ETB)
4. Designate on-call engineer for HS-3 staged-rollout window (Jul 04 – Jul 10)

---

## Security Posture: 6.5/10

### Audit 2026-06-20 (security-team agents)

Full automated security-team run against `buyer-intenet` branch. Verdict: **GA NO-GO** — 2 Critical + 8 High findings block production launch. Full report: `docs/security/security-assessment-2026-06-20.md`.

### Fixed on `feat/auth-hardening` (2026-09-26, NOT deployed; decision record `specs/decisions/auth-step-up-plan.md`)
- [x] Phone ownership at signup: SMS code (`/auth/verify-otp`, `/auth/resend-otp`) unless Fayda released the same phone; `isPhoneVerified` is honest
- [x] Fayda OIDC: server-held state, nonce, PKCE, discovery; single-use phone-bound verification token; link-later (`/user/fayda-start`, `/user/fayda-link`); mock eSignet provider for dev (`tools/fayda-mock`, `faydaMode`)
- [x] KYC gate on the order-funded leg (`ORDER_KYC_REQUIRED`) and `AUTH_040` step-up (fresh `auth_time`) on payment submit, escrow initiate and disputes
- [x] **ROL-03** closed at the source: public Cognito client has `USER_PASSWORD_AUTH` off; the server logs in with `ADMIN_USER_PASSWORD_AUTH`
- [x] **CHAIN-01** follow-ups: admin TOTP MFA required in every environment (admin web drives setup); `change_role` rewrites Cognito `custom:role` and signs out
- [x] Suspend disables the Cognito user and signs out everywhere; refresh refuses suspended profiles
- [x] Sync create-conflict echo scoped to the owner or a party (role-visibility IDOR)
- [x] `/escrow/release` and `/refund` on the admin authorizer (admin group only); resolved disputes settled by the cron (money was stranded before)
- [x] Payouts to the registered phone of a verified party (`ESCROW_029`); Telebirr webhook `merch_code`, fail-closed timestamp, payer binding
- [x] Per-IP caps on the public auth routes; device tracking with new-device notice; `DELETE /device-tokens` at logout
- [x] Mobile: OTP screen, step-up and KYC dialogs, Fayda verification screen, local-auth gate on QR confirm, app lock, session hygiene
- Still open from the June assessment: **INFRA-02** (`TELEBIRR_MOCK` on staging), **T-SEC-WAF** (per-IP caps are the interim), **SCA-01/02**

### Fixed (from pentest 2026-03-05)
- [x] Password policy (min 12, uppercase + symbols)
- [x] Cognito deletion protection
- [x] OAuth implicit flow removed
- [x] Mobile refresh token reduced (30d → 7d)
- [x] CSP unsafe-eval removed
- [x] Health endpoint stripped of version/region
- [x] Separate admin Cognito pool (no self-signup)
- [x] MFA required on admin pool (TOTP)
- [x] CI guard for DEV_FAYDA_BYPASS in production
- [x] CI guard for TELEBIRR_MOCK in production
- [x] version.json blocked from CloudFront
- [x] Localhost CORS stripped for production
- [x] Admin token validity reduced to 15 min

### Unfixed (CRITICAL)
- [ ] **ROL-01/ROL-02**: Role isolation not deployed — server enforces buyer-only browse but the fix is only on `buyer-intenet`; no server deploy pipeline means the gap is live in the currently-deployed Lambda. Blocks GA. — Sprint 2 / DEPLOY-01 prerequisite
- [ ] **CHAIN-01**: `change_role` admin endpoint allowed privilege escalation to any role (incl. admin) → escrow release. **Allowlist fix applied 2026-06-20 (AdminFunction.cs, ADMIN_011); pending deploy.** Follow-up: admin MFA on staging + per-action audit logging. — Sprint 2

### Unfixed (HIGH priority)
- [ ] **T-SEC-CERT**: Certificate pinning disabled (MITM risk) — Sprint 2 (re-confirmed 2026-06-20, see assessment; cross-ref MOB-02 refreshDio unpinned)
- [ ] **T-SEC-WAF**: AWS WAF not configured — Sprint 2 (re-confirmed 2026-06-20, see assessment; cross-ref INFRA-01. NOTE: API is an HTTP API — WAF can't attach directly; needs /api/* via CloudFront or REST migration)
- [ ] **T-SEC-AUDIT**: Audit logging incomplete (escrow only) — Sprint 2 (re-confirmed 2026-06-20, see assessment)
- [ ] **T-SEC-GIT**: Git history contains exposed private key — Sprint 2 (re-confirmed 2026-06-20, see assessment; key also still on disk: server/localstack-data/cache/server.test.pem.key)
- [ ] **T-SEC-SNS**: SNS in sandbox mode — Sprint 2 (re-confirmed 2026-06-20, see assessment)
- [ ] **ROL-03**: Access-token role bypass — clients sending the Cognito access token (no `custom:role`) bypass role scoping. Enforce ID-token-only / reject null role on Lambda authorizers. — Sprint 2
- [x] **DEPLOY-01** (done 2026-07-09, `deploy-server.yml`): No server deploy pipeline: CDK stack exists but no CI/CD step publishes the .NET Lambda binary; every server fix (incl. ROL-01/02, CHAIN-01) is dead until this is wired. Sprint 2.
- [ ] **SRV-01**: `DEV_OTP_BYPASS` env-var guard. **Fix applied 2026-06-20** (added to ProductionBypassGuard FORBIDDEN_ENV_VARS, api-stack.ts). — verify on next synth
- [ ] **INFRA-02**: `TELEBIRR_MOCK=true` present in staging environment config (guard only fires for `isProd`) — Sprint 2
- [x] **CI-01** (done 2026-10-06, #110): Maestro install uses `curl | bash` (unauthenticated remote execution in CI, pr-validation.yml:114); pin a release download + verify SHA. Sprint 2.
- [ ] **SCA-01/SCA-02**: Dependency CVEs — react-router (admin) + Astro/Vite (website) high CVEs; add `npm audit --audit-level=high` CI gate. — Sprint 2

### Unfixed (MEDIUM priority)
- [ ] **T-SEC-TTL**: 7-day offline session TTL (too long) — Sprint 3
- [ ] **T-SEC-ISAR**: Isar database unencrypted on disk — HS-1 (encryption foundation) **complete** as of 2026-05-13; HS-2..HS-7 in flight. 7-sprint program: `specs/features/isar-hardening/README.md` (ship target 2026-08-20). Until HS-2 wires the primitive into User + CropListing repositories, PII is still plaintext at rest in those collections. (re-confirmed 2026-06-20, see assessment; cross-ref MOB-01 — FieldEncryptor built but never invoked)
- [ ] **T-SEC-JWT**: Dead JwtValidator with testing bypass — Sprint 1

---

## Completed Features (All Phases)

### R1: Security Foundation
F-01 (runbook), F-02 (gitleaks), F-16 (Lambda deploy), F-19 (CI security)

### R2: Auth + Data Models
F-03 (Cognito + mock), F-04 (Isar encryption key), F-06 (rate limiting), F-07 (6 Isar schemas), F-09 (input validation)

### R3: Sync + First Feature
F-08 (sync engine), F-10 (crop listings), F-17 (least-privilege IAM)

### R4: Transaction Flow
- F-11 Trip Listings
- F-12 Delivery Verification (QR + GPS + photo)
- F-13 Telebirr Escrow (mock gateway + cron)
- F-14 Fraud Detection (4 rules writing weighted signals; risk scoring not built, EPIC FRD)
- F-15 Conflict Resolution UI
- F-18 S3 Access Logging + Versioning

### Bonus (beyond original scope)
- Voice note recording/playback
- Role-based navigation
- Data isolation enforcement
- Admin dashboard (11 pages, React)
- Playwright E2E tests (27 tests)
- Multi-user E2E testing support
- Containerized .NET AOT deployment

---

## Key Decision Log

| Decision | Rationale | Date |
|----------|-----------|------|
| Server-wins conflict resolution | Simplest for solo farmers (rare conflicts) | Feb 21 |
| HMAC-SHA256 over JWT for QR | Smaller payload, better on cheap cameras | Feb 18 |
| 5km GPS threshold | GPS inaccuracy + farm-to-road distance | Feb 18 |
| Mock auth/Telebirr for MVP | Enables E2E testing without vendor dependency | Feb 17 |
| Voice notes (bonus) | Low-literacy users benefit; minimal overhead | Feb 23 |
| Separate admin Cognito pool | Shared pool allowed self-registration into admin | Mar 5 |
| Sprint plan (3 sprints) | Ship-ready → Security → Production | Mar 19 |

---

## Synthetic Fleet Simulation Bugs (2026-09-24) - @Scope tasks

Source: 13-agent synthetic-user fleet against deployed dev (server `dc95aca`, webapp `881c64d`).
Full report with root causes, evidence pointers and what passed: `specs/reports/synthetic-fleet-2026-09-24.md`.
One task per deduplicated bug. Severity: P0 money/ownership/data breach or app unusable; P1 role flow
broken or money rule unenforced; P2 wrong behaviour or contract deviation a conformant client hits; P3 cosmetic/docs/tests.
Every task must follow `.ai/completeness-rules.md` (all 3 roles, mobile+web pair, tests, contracts).

| ID | Sev | Scope | Task | Done when |
|---|---|---|---|---|
| SIM-01 | P0 | @Mobile | ✅ DONE 2026-09-24 (CA pinning to Amazon Trust Services roots; verified on emulator). Fix certificate pinning: leaf pin stale since the 2026-06-26 API Gateway rotation AND `cert_pinning_io.dart` only ever sees the root cert (no trusted roots), so the intermediate pin can never match; Android app cannot log in. | Debug + release Android builds log in against dev; pin check evaluates the served chain (leaf/intermediate), pins documented with a rotation runbook and a CI check against the live chain. |
| SIM-02 | P0 | @Server | ✅ DONE 2026-09-25 (changed `farmerId`/`farmerName`/`farmerPhone` rejected with `CROP_VALIDATION` before any write, echoed values stripped; contract 1.1.0; 5 tests). Crop update: strip/reject identity fields (`farmerId`, `farmerName`, `farmerPhone`) on update; ownership is immutable. | Update with a different `farmerId` -> `CROP_UNAUTHORIZED`/validation error; test in `server/tests`. |
| SIM-03 | P0 | @Server | ✅ DONE 2026-09-25 (same guard for `truckerId`/`truckerName`/`truckerPhone`, `TRIP_VALIDATION`; contract 1.1.0; 3 tests). Trip update: same for `truckerId`, `truckerName`, `truckerPhone`. | Reassign attempt rejected; test added. |
| SIM-04 | P0 | @Server | ✅ DONE 2026-09-25 (handler: buyer role only `ESCROW_025`, fail closed; service: caller == delivery buyer `ESCROW_026`, farmerId == delivery farmer `ESCROW_027`, amount == linked order `orderTotal` `ESCROW_028`, client orderId may only echo the delivery's, split from the linked order; mobile Pay button now offers `orderTotal`; contract 1.2.0; 12 tests). `POST /escrow/initiate`: require caller == delivery.buyerId, `buyerId`/`farmerId` == delivery's, amount == order total (or reject when an order-funded escrow exists), buyer role only. | Farmer/trucker/stranger initiate -> 403; mismatched parties/amount -> 4xx; legitimate buyer path unchanged; tests. |
| SIM-05 | P1 | @Server | ✅ DONE 2026-09-25 (order create loads the listing and trip: existence, `available`/`open|matched` status, farmer/trucker ownership, cropType, price, stock in kg vs quintals, transport = kg/100 x pricePerQuintal, tripId+truckerId together; server computes all money fields and overwrites farmer/trucker names; contract 1.2.0 + create-validation table; 15 tests). Order create: load listing (+trip): enforce `farmerId` == listing owner, `cropType` == listing, `pricePerUnit` == listing price (or server-set), 0 < `quantity` <= stock, `transportCost` >= 0 and derived from trip x quantity, `tripId` required with `truckerId`, referenced ids must exist. | Each invalid case rejected with `ORDER_VALIDATION`; server computes the money fields. |
| SIM-06 | P1 | @Server | ✅ DONE 2026-09-25 (update allow-list: status + reason/payment companions only; every identity and pricing field immutable in all statuses incl. post-funded, echoed unchanged values dropped; orderId echo must match entityId, updatedAt dropped; unknown keys rejected; update-time field validation; contract 1.3.0; 15 test cases). Order update: field allow-list + immutability for `listingId`, `buyerId`, `farmerId`, `truckerId`, `tripId`, `quantity`, `pricePerUnit`, `transportCost` after create (and all money fields after `funded`); reject unknown fields. | Mutation attempts rejected; `orderTotal` never drifts from its inputs; tests incl. post-funding. |
| SIM-07 | P1 | @Server @Docs | ✅ DONE 2026-09-25 (wire unit fixed as kilograms, which the shipped mobile/web clients already send; `TryDecreaseListingQuantityAsync` now subtracts kg/100 quintals; documented in order-lifecycle.md Units + data-models.md; server test 500 kg on 60 quintals leaves 55; match already compares quintals to quintals). Unit mismatch on funding: `TryDecreaseListingQuantityAsync` subtracts kg order quantity from quintal stock. Pick one wire unit for order quantity, document it in `order-lifecycle.md`/`data-models.md`, convert consistently (pricing, stock, transport, match). | A 5-quintal in-app order reduces a 60-quintal listing to 55; unit documented; mobile/web/server tests. |
| SIM-08 | P1 | @Server @Docs | ✅ DONE 2026-09-25 (`SyncOperationRequest.clientVersion` added and honoured at every update/delete site, `data.version` still accepted, op-level wins; order updates without any version rejected with `ORDER_VALIDATION`; web order client now reads and sends the current version; sync-protocol.md 1.3.0; server + web tests). Sync versioning: honour op-level `clientVersion` (contract) or update `sync-protocol.md` to `data.version`; never apply an order update without a version check (no fallback to stored version). | Contract-conformant update succeeds; versionless order update -> validation error; tests. |
| SIM-09 | P1 | @Server | ✅ DONE 2026-09-25 (order must be accepted/pickup_confirmed/in_transit, so `funded` is NOT allowed; trucker flow requires the order's trucker == caller and rejects orders without a trucker; one active delivery per order via conditional `activeDeliveryId` claim on the order row, released on cancel/soft delete; participant/order/listing/trip/type fields immutable on update; contract 1.3.0; tests). Delivery guards: creator must be the order's `truckerId` (or the buyer for self-pickup), at most one active delivery per order, participant ids immutable on update, and require order status >= `accepted` (decide: `funded` allowed?) before delivery create. | Each gap covered by a rejected test case; contract `delivery-verification-api.md` updated. |
| SIM-10 | P2 | @Server @Docs | ✅ DONE 2026-09-25 (server accepts `deliveryId` as an echo that must equal entityId and drops client `updatedAt`/`pickedUpAt`/`confirmedAt`, which it stamps itself; this also unblocked the mobile dispute call, which echoed deliveryId + updatedAt and was rejected as unknown fields; contract 1.3.1; 5 tests). Accept the contract's delivery payload (`deliveryId`, `confirmedAt`, `pickedUpAt`) or fix the contract examples; align mobile/web clients. | Contract example payloads succeed end to end. |
| SIM-11 | P2 | @Server @Docs | ✅ DONE 2026-09-25 (delivery pickedUp/inTransit/delivered advance the linked order server-side as a system actor, forward only and best-effort, so self-pickup goes accepted → delivered without in_transit; the order's trucker may set the three logistics statuses; a client `delivered` is refused unless the active delivery is delivered; contracts order-lifecycle 1.4.0 + delivery-verification 1.4.0; tests). Order/delivery lifecycle coupling: advance the order on delivery transitions (or let the trucker set logistics statuses as the contract says); gate order `delivered` on delivery state; self-pickup path must not require `in_transit`. | Full with-truck and self-pickup flows leave order and delivery consistent without manual stepping; ratings unblock automatically. |
| SIM-12 | P2 | @Server | ✅ DONE 2026-09-25 (escrow dispute mirrors onto the order `delivered → disputed` and the delivery via a new conditional `TryMarkDisputedAsync`; a delivery dispute via sync moves the order to disputed AND freezes the escrow through `EscrowService.MarkDisputedFromDeliveryAsync`, closing the auto-release hole; ratings already blocked on non-delivered orders, covered by a test; contracts escrow 1.2.1, order 1.4.1, delivery 1.4.1; tests). Dispute propagation: set order (and delivery) to `disputed`; block ratings while disputed. | Dispute -> order `disputed`, rating -> `RATING_FORBIDDEN`. |
| SIM-13 | P2 | @Server | ✅ DONE 2026-09-25 (`ICropRepository.TryRestoreStockAsync`: per-listing `stockRestoredFor` order-id set + version CAS with retry, `reserved → available`; hooked into funded → rejected/cancelled in sync and into the escrow cron's stale-funded cancel; cron Lambda gains `CROPS_TABLE` + RW grant in CDK (needs deploy); decrement now stores quantity as a number; contracts order 1.4.2 + escrow 1.2.2; repo/sync/cron tests incl. version-conflict retry). Restore listing stock when a funded order is cancelled/rejected/refunded (idempotent, conditional update). | Stock returns to pre-order value; race-safe test. |
| SIM-14 | P2 | @Server | ✅ DONE 2026-09-25 (`DeliveryDto.orderId` mapped on list + detail; mobile Isar schema + web mirror gain `orderId`, set on local create, parsed on pull/web/conflict; delivery screen resolves the order by `orderId` first, trip match as fallback; admin has no delivery type to update; contracts delivery-verification 1.4.2 + data-models 1.0.2; tests). Add `orderId` to the delivery DTO (list + detail) and to `data-models.md`; check mobile/admin models. | Field present on both endpoints; clients link delivery -> order. |
| SIM-15 | P2 | @Server @Mobile | ✅ DONE 2026-09-25 (`EscrowDto` + history rows map `truckerPayout`/`truckerId` (+ `farmerPayout` on rows); sparse `TruckerStatusIndex` GSI on the escrows table (needs deploy) + `QueryByTruckerAsync` behind `?role=trucker`; the order's trucker is a party for by-id/by-delivery; delivery linkage backfills a missing `truckerId` from the order; mobile Isar + web `Payment` gain the legs + `payoutFor(role)`, Payments list and Profile show each role its own leg, escrow detail adds a Trucker Receives row (3 languages); admin detail shows the trucker payout; contract escrow 1.2.3; server/mobile tests). Escrow money views: map `truckerPayout`, store/index `truckerId` on escrow rows so `GET /escrow/history?role=trucker` and by-delivery work for the trucker; farmer/trucker Payments and Profile show their own share only. | Trucker sees payout; farmer view reconciles (amount = farmer + trucker + commission). |
| SIM-16 | P2 | @Server @Docs | ✅ DONE 2026-09-25 (`AuthHelper.GetRole`; `GET /orders` and `GET /escrow/history` scope by the token's role claim when no party filter / `role=` is given; `GET /deliveries` scopes by role too (buyer → ParticipantIndex with `status` filter, farmer → FarmerIdCreatedAtIndex, trucker → new sparse `TruckerIdCreatedAtIndex` GSI, needs deploy) with `truckerId=` (caller only) and `role=` override, legacy status query kept for tokens without a role claim; self-pickup order-funded escrows covered by the farmer scope (regression test); mobile pull adds the trucker scope; contracts delivery 1.5.0 / escrow 1.2.4 / order 1.4.3; tests). Default list scoping: `GET /orders`, `/escrow/history`, `/deliveries` scope by the caller's role (farmer/trucker/buyer) when no filter is given; include self-pickup escrows in farmer history. | Unfiltered reads return the caller's own rows for all 3 roles; contracts updated. |
| SIM-17 | P2 | @Server | ✅ DONE 2026-09-25 (`ValidateCropFields` gains the geo + date rules for create AND update: `latitude` 3-15 / `longitude` 33-48 when present (null/empty = absent), `createdAt` ISO 8601 and not in the future with a 24h skew allowance for offset-less local time; neither rule existed on the server before; contract crop-listing 1.1.1; sync tests). Run the same geo/date validation on crop update as on create (Ethiopia bounds, no future dates). | latitude 20 / createdAt 2027 on update -> `CROP_VALIDATION`. |
| SIM-18 | P2 | @Infra | ✅ DONE 2026-09-25 (CI: `deploy-server.yml` dev runs end with an idempotent `seed-admin-user.sh` step fed by the deploy's `--outputs-file` (strict login check, secret-drift warning) + a non-blocking `scripts/e2e_admin.py` that logs in via Cognito and reads every dashboard endpoint; IAM `SeedAdminUser` grant (non-prod) in `github-actions-stack.ts` + test; script bug fixed (bash `GROUPS` is read-only, the group check always warned); docs in test-users/deployment/checklist. The actual dev seeding + functional run happen on the next merge to main, since this Mac has no AWS credentials). Seed the dev admin user (`scripts/seed-admin-user.sh` in CloudShell) and add it to the CI deploy or a documented post-deploy step; then run the admin dashboard e2e functionally. | Admin login works on dev-admin; Dashboard/Users/Escrows/Transactions/Audit/Fraud/Disputes render fleet data. |
| SIM-19 | P2 | @Mobile | ✅ DONE 2026-09-25 (browse cards render the seller's real `GET /ratings?rateeId=` aggregate as "4.6 (12)", hidden while loading or when the farmer has none; one cached fetch per farmer per screen; the fake Escrow badge is gone; `ListingCard` gains `ratingCount`; summary reads ask for `limit=1` on mobile and web; widget tests + RatingBloc provider in the browse and Find Crops tests). Replace the hardcoded 4.5-star rating and Escrow badge on crop cards with real `GET /ratings?rateeId=` data (or hide when none). | Cards show actual averages; test. |
| SIM-20 | P2 | @Mobile | ✅ DONE 2026-09-25 (payout-only views landed with SIM-15: Payments list `payoutFor(role)`, Profile crop cost; this task adds the cancelled-order labelling: server `refundPending` (escrowed + refundReason or expired) + `refundReason` on `EscrowDto` and history rows (rows also `expiresAt`), mobile/web `Payment` gains `orderId`/`refundPending`/`refundReason` + `isRefundPending` (client fallback on expiry), `EscrowStatusBadge` reads "Refund Pending" (3 languages) on list + detail, blank "Del. #" becomes "Order #"; contract escrow 1.2.5; server + mobile tests). Farmer Payments/Profile must show farmer payout only (not gross incl. transport); cancelled-order escrows must not display as "Funds Held Safely". | Values match `farmerPayout`; refunded/expiring escrows labelled correctly. |
| SIM-21 | P2 | @Mobile | ✅ DONE 2026-09-25 (web router `webRedirect` sends a wrong-role URL to `/` using `webRoutesForRole` (farmer: own crops; trucker: trips; buyer: market + all trips; shared orders/deliveries/payments/messages/profile/settings); Home stats + Recent Activity issue one role-scoped GET per resource with identical params (orders were fetched twice for every role and never by truckerId; trucker trips 3 to 1; trucker deliveries now `truckerId=`; buyer deliveries `mine=true`), `ApiClient.get` coalesces identical in-flight GETs so the stats/activity/hero reads share one request; inbox resolver skips crop lookups for truckers, dedupes concurrent resolves and remembers 401/403 for the session; tests for the route set, redirect, coalescing and resolver). Add a client-side role guard to the web router (redirect wrong-role routes) and stop wrong-role/duplicate API calls on Home and Inbox (trucker inbox 403 crop lookups). | No 403s in the browser network log for any role; one call per resource per load. |
| SIM-22 | P2 | @Mobile @l10n | ✅ DONE 2026-09-26 (chip theme label styles now carry NotoSans + the NotoSansEthiopic fallback like the text theme, so Amharic chip labels no longer size to a late-loading fallback font; shared-row chips ellipsize instead of clipping mid-glyph; `CropType.tryFromKey` + `labelFor` render the stored key as the localized name on the browse card, order card, order detail, order summary and the four Home activity lines (unknown keys stay raw); tests). Fix clipped Amharic filter chips; localize crop type names on listing/order cards in am/om (keys exist in `app_en.arb`?). | Chips readable at 360 px; no raw English crop names in am/om. |
| SIM-23 | P2 | @Mobile | ✅ DONE 2026-09-26 (new `SecureConnectionException` + `isSecureConnectionFailure` (badCertificate, or a wrapped HandshakeException/TlsException, matched by name so the file stays web-safe); `ApiClient` and `AuthService` classify a pin failure as `SECURE_CONNECTION_FAILED` instead of a network error, so `login()` no longer falls back to offline login and shows the new secure-connection message; `AppErrorCode.forbidden` (403) and `.secureConnection` with localized messages; browse crops/trips error subtitles use `localizeLoadError`, which keeps the "will sync when online" copy only for real connectivity failures; 3 languages; tests). Error mapping: TLS/pin or 403 failures must not be reported as "offline"; show a network/security or permission message. | `/error-mapping` audit passes for these paths. |
| SIM-24 | P2 | @Server | ✅ DONE 2026-09-26 (`InitiateAsync` 6b: an active escrow found by deliveryId that is the order-funded record (`source=order` or `escrow-order-*` id) now answers `409 ESCROW_024`/`orderId` before the `ESCROW_009` duplicate-delivery answer. Client half: the shared `ApiClient` threw a TypeError on the server's `{"error":{code,message}}` envelope, so no escrow code ever reached the UI; it now parses the envelope and carries `code` on the typed exceptions, both escrow repositories keep the server's `ESCROW_*` code (`escrowCodeOf`), `ESCROW_024` (+025/026/028) localized in 3 languages, admin i18n key; contract 1.3.1; server + mobile tests). Return `ESCROW_024` (funds already held for this order) when a delivery's order already has an active order-funded escrow, before the `ESCROW_009` check. | Buyer initiate on a funded order -> 409 `ESCROW_024`. |
| SIM-25 | P3 | @Server | Serialize `serverData` numbers/arrays with proper JSON types in `/sync` success and conflict results. | `version`, prices, `matchedListings` typed; mobile conflict resolver tests still pass. |
| SIM-26 | P3 | @Server @Docs | Reconcile crop read model with `crop-listing-api.md`: `quantityKg` naming, `{crop}` wrapper, default status filter (`available`), `nextToken`/`hasMore`, `limit` max 50. | Contract and server agree; e2e/webapp specs updated. |
| SIM-27 | P3 | @Server @Docs | Auth/escrow error codes: field-specific codes for missing fields (not `AUTH_007 Invalid JSON`), `AUTH_019` for revoked refresh tokens, phone-format rule (accept and document `09...` normalisation or reject), Fayda token requirement per environment. | Contract tables match responses. |
| SIM-28 | P3 | @Server | Idempotency: a reused key with a different payload -> error (not silent `duplicate`); race loser gets `conflict` + `serverData`. | Tests for both. |
| SIM-29 | P3 | @Server | Escrow record hygiene: bump version on link/confirm, `409 ESCROW_020` on second dispute, expose `refundReason`. | Per `escrow-protocol.md`. |
| SIM-30 | P3 | @Server @Docs | Commission: round to 2 dp; align contract (2.5 % deducted) with implementation (5 % added) or vice versa. | One documented rule; rounding test. |
| SIM-31 | P3 | @Server | ✅ DONE 2026-10-08 (#93). Exclude cancelled/deleted trips from `GET /trips` list (consistent with detail 404). | List and detail agree. |
| SIM-32 | P3 | @Mobile | ✅ DONE 2026-10-08 (#117). Login form: clear validation errors on edit (Android); make the web error visible and in the accessibility tree. | Manual + widget test. |
| SIM-33 | P3 | @Mobile | Display hygiene: guard impossible money/quantity values, count "Available Crops" across pages, never show raw ids/junk location, show trucker name on trip cards. | Screens verified for all 3 roles on web. |
| SIM-34 | P3 | @Server | ✅ DONE 2026-10-06 (#106). Validate `since` on `GET /messages` (400 on non-ISO). | Test. |
| SIM-35 | P3 | @Infra | Serve `/version.json` on the dev webapp (or drop it from the cache strategy docs). | 200 with build info. |
| SIM-36 | P3 | @Tests | ✅ DONE 2026-10-09 (#120; admin nav part was already fixed by #115). Fix stale e2e assertions: admin nav count (10 vs 11), webapp listing-flow invalid crop type, login spec passing on redirect timeout. | `e2e` suites green for the right reasons. |
| SIM-37 | P3 | @Mobile @Server | Sync `preferredLanguage` to the server profile when changed in Settings. | Language persists across devices. |

## EPIC FRD: Fraud Risk Engine (2026-09-26) - @Scope tasks

Goal: suspicious activity holds the money or forces stronger identity proof (step-up, Fayda re-auth) before it moves.
Principle: deterministic rules decide, AI only advises. No AI output ever releases, refunds or blocks money on its own.

Current state (verified 2026-09-26): `FraudDetectionService` has 4 rules (listing velocity, duplicate listing, price 3 sigma, GPS) plus payment velocity, and writes weighted signals to the fraud-signals table. Nothing sums the weights, no risk tiers exist, no escrow can be `held`, and admin Fraud Review actions only mark a signal reviewed. The spec is `specs/architecture/fraud-detection-architecture.md` (Rule 5 tiers never implemented).

Depends on: `feat/auth-hardening` (worktree `.claude/worktrees/auth-hardening`, uncommitted) for `AUTH_040` step-up, `KycPolicy`, `POST /user/fayda-link`, device identity headers and real suspension. Land it first.

Priority: P0 money moves despite a known red flag; P1 detection or enforcement gap on a money path; P2 evidence, admin tooling or coverage; P3 deferred / needs data.
Every task must follow `.ai/completeness-rules.md` (all 3 roles, mobile+web pair, tests, contracts, 3 languages).
FRD-01 v3 (`specs/architecture/fraud-detection-architecture.md`) supersedes the wording of FRD-02, FRD-04 and FRD-05 where they differ.

Stories:
- **S1 Risk score + enforcement**: turn signals into a per-user score and act on it at every money action.
- **S2 Signal coverage**: every rejected invariant and suspicious transaction pattern becomes a signal.
- **S3 Delivery + payout evidence**: prove pickup and delivery, delay payouts by risk.
- **S4 Relationship detection**: async detection of collusion and self-trading across accounts.
- **S5 Adversarial E2E**: synthetic fraud personas prove detection and enforcement end to end.
- **S6 AI advisory (deferred)**: labelled outcomes first, then an LLM reviewer that only adds advisory signals.

| ID | Story | Pri | Scope | Task | Done when |
|---|---|---|---|---|---|
| FRD-01 | S1 | P1 | @Docs | ✅ DONE 2026-10-08 (#112). Update `fraud-detection-architecture.md` to v3 (score formula, tiers, enforcement matrix, held status, decay, AI boundary) and bump `escrow-protocol.md`, `order-lifecycle.md`, `auth-api.md`, `sync-protocol.md` for the new codes and status. Correct STATE.md/ROADMAP.md claims that risk scoring is done. | Contracts reviewed before any code; new codes (`AUTH_041`, `FRAUD_ACCOUNT_HELD`, escrow `held`) documented with owners. |
| FRD-02 | S1 | P0 | @Server | Implement `GetUserRiskScoreAsync`: sum weights of `pending` signals in the 30-day window (one Query by `userId`), tiers LOW 0-29, MEDIUM 30-59, HIGH 60-99, CRITICAL 100+. Whitelisted users skip soft-rule weights. | Unit tests per tier boundary, whitelist and expired signals; one DynamoDB Query per call; cold start budget kept. |
| FRD-03 | S1 | P0 | @Server @Infra | New escrow status `held` with `holdReason`/`heldAt`/`heldBy`: auto-release and settlement crons skip it; only an admin (group check) moves `held` to release or refund; transitions audited in escrow-audit and transaction-audit. | Held escrow never settles by cron (test); admin release/refund from held works; order and delivery views show it for all 3 roles. |
| FRD-04 | S1 | P0 | @Server | Risk policy gate at order `payment_submitted`, `POST /escrow/initiate`, escrow release and payout: MEDIUM forces step-up (`AUTH_040`) at any amount; HIGH forces fresh Fayda re-auth (`faydaAuthTime` stamped by `/user/fayda-link`, new `AUTH_041`) plus step-up; CRITICAL puts active escrows in `held` and rejects new listings, trips, orders with `FRAUD_ACCOUNT_HELD`. | Each tier covered by tests on every gated path, sync and REST; LOW path unchanged; farmer, trucker and buyer each tested. |
| FRD-05 | S1 | P1 | @Server @Admin | Admin fraud actions get side effects: `reject` suspends the user (Cognito disable + global sign-out from auth-hardening) and holds their active escrows; `approve` clears the signal from the score; `whitelist` sets a per-user soft-rule bypass with an expiry. All written to transaction-audit. | Each action changes score/escrow/user state as specified; tests; admin UI reflects the result. |
| FRD-06 | S1 | P1 | @Mobile @l10n | Client handling: `AUTH_041` opens the Fayda re-auth flow then retries; `FRAUD_ACCOUNT_HELD` message; "Under review" badge on order and escrow detail/list for `held`. Mobile and web together, 3 languages, offline queue treats these as non-retryable. | Widget tests; web build passes; no raw codes shown; all 3 roles verified. |
| FRD-07 | S1 | P2 | @Admin @Infra | Fraud Review rework: GSI on fraud-signals `status` (replace the Scan), signals grouped by user with risk score and tier, a held-escrow queue, link to the transaction timeline. | Page loads without Scan; admin e2e covers approve/reject/whitelist and held release/refund. |
| FRD-08 | S2 | P1 | @Server | Emit signals from existing rejects: sync create conflict on another user's id (IDOR probe), role/action 403s, identity/money field mutation attempts (SIM-02/03/06 guards), stale-version replays, repeated QR handoff failures, Telebirr webhook signature failures and payer mismatch (auth-hardening writes escrow-audit only), requests with a suspended user's token. Rate-limit signal writes per user/type. | Each source writes one typed signal with weight; best-effort (a failed write never changes the response); tests. |
| FRD-09 | S2 | P1 | @Server | Transaction pattern rules: amount above 3 sigma of the user's own history (escrow-protocol spec), new account (under 7 days) with a large amount, repeated amounts just under the 10K KYC threshold, cancel-after-fund rate, per-buyer dispute rate, delivery time impossible for the trip distance. | Each rule has thresholds in config, a signal type and weight, and tests for hit and miss. |
| FRD-10 | S2 | P2 | @Server | Score decay: a completed delivery with no flags subtracts 5 (spec), a signal resolved as false positive drops its weight, scores never go below 0. | Tests for decay and floor. |
| FRD-11 | S3 | P1 | @Server @Mobile | Pickup proof: farmer-side QR scan at pickup (mirror of the delivery handoff QR: secret table, single use, fail closed) with GPS stamped at scan; optional cargo photo. Works offline (scan queued, verified on sync). Self-pickup path included. | Pickup without a valid scan cannot move a delivery to `pickedUp`; contract `delivery-verification-api.md` bumped; tests for trucker and self-pickup flows. |
| FRD-12 | S3 | P2 | @Mobile @Server | Optional delivery photo at handoff via the existing presigned S3 upload, attached to the delivery and shown in dispute evidence. Compressed for 3G. | Photo visible to buyer, farmer, trucker of that delivery and admin; size cap test. |
| FRD-13 | S3 | P1 | @Server | Risk-based payout delay: escrow gains `releaseNotBefore`; new or flagged farmer/trucker gets a 24-72h hold after delivery confirm, trusted users (history, no flags) shorter; crons respect it. Thresholds in config. | Cron test: no payout before `releaseNotBefore`; trusted vs new user timings tested; payout views show expected date. |
| FRD-14 | S3 | P2 | @Admin @Server | Dispute evidence package: one admin view stitching audit timeline, pickup and delivery GPS, QR scans, photos and order messages for a delivery. | Admin can resolve a dispute from one screen; e2e test. |
| FRD-15 | S4 | P2 | @Server @Infra | Hourly EventBridge Lambda for relationship patterns: repeated farmer+buyer pairs, same device/phone/IP on both sides of an order (device ids from auth-hardening), confirm-then-dispute buyers, self-trading. DynamoDB queries only, no graph database. Writes signals. | Seeded fixtures trigger each pattern; runs within Lambda limits on dev data volume; cost noted. |
| FRD-16 | S4 | P3 | @Infra | CloudWatch metric per signal type from logs plus an anomaly-detection alarm on total signal volume to the existing alarm topic. | Alarm fires in a synthetic spike test; `cdk synth` passes. |
| FRD-17 | S5 | P1 | @Tests | Adversarial personas in the synthetic fleet: listing spammer, self-trader, IDOR prober, payer mismatch, GPS spoofer, confirm-then-dispute buyer. Assert expected signal, tier, challenge (`AUTH_040`/`AUTH_041`) and hold. | Fleet report shows every persona detected and enforced; runs against dev after deploy. |
| FRD-18 | S6 | P3 | @Server @Admin | Capture labelled outcomes: admin dispute resolutions and fraud actions stored as labels linked to their signals and transactions, exportable for later analysis. | Every resolved case has a label; export script documented. |
| FRD-19 | S6 | P3 | @Server @Infra | Deferred: Bedrock LLM reviewer in a separate non-AOT Lambda, async on HIGH/CRITICAL users only. Input: audit timeline; output: an advisory signal with a capped weight plus an admin-facing explanation. Never moves money. Check Bedrock model availability in eu-north-1 and me-south-1 first. | Decision record written; reviewer output visible in Fraud Review; kill switch in config. |
| FRD-20 | S6 | P3 | @Server | Deferred: ML risk scoring once enough labelled outcomes exist (from FRD-18). Replaces or complements the fixed weights, rules stay authoritative. | Decision record with dataset size, evaluation metrics and rollback plan. |

Order: FRD-01, then FRD-02/03/04 together (smallest useful slice), then FRD-05/06/08, then S3, then S4/S5, S6 last.

---

## EPIC ESC: Three-Party Escrow, Commitments and Reputation (2026-09-29) - @Scope tasks

Goal: one buyer payment is held before anyone commits; the farmer and the trucker each accept before a deadline; a failed trucker gives the buyer a choice; money is never stuck; broken commitments cost reputation.
Design (source of truth): `specs/features/three-party-escrow-commitments-reputation.md` (APPROVED 2026-09-30; the decisions are in §14). Code survey with every finding, its file:line and its owning task: `specs/reports/escrow-flow-survey-2026-09-29.md`.

Product rules set by the user on 2026-09-29 (ask before changing them):
- Money is held BEFORE the farmer and the trucker confirm (fund-first). The trucker must explicitly accept.
- If everyone accepts except the trucker, the buyer chooses: another truck, continue without a truck (self-pickup), or cancel. This is the only case in which a buyer can cancel a paid order.
- A farmer or trucker who cancels or declines a paid order placed against their listing gets penalty points and a lower rating, and can be tagged "Low reputation".

Verified 2026-09-29 (P0 blockers before any live money):
- **Fake live payouts.** Live payouts and refunds return success without moving money (`TelebirrSandboxGateway.cs:146-160`).
- **No live order funding.** Live payments never fund an order; only the mock path calls `CreateFromFundedOrderAsync`.
- **Dispute verdicts can't settle.** The settlement lock at `EscrowRepository.cs:141` rejects `dispute_resolved`.
- **Unpaid trucker leg never retried.** `truckerPayoutPending` is written but never read.
- **Money stuck forever.** When the trucker disappears after the farmer accepts, the buyer's money stays held indefinitely (`SyncFunction.cs:260`, `EscrowCronFunction.cs:460`).

Builds on `main` plus `fix/payment-fayda-stepup-pickup-qr` (`AUTH_040`/`AUTH_041` payment step-up and the pickup QR on order detail; pushed, PR pending). Overlaps EPIC FRD:
- ESC-33 feeds FRD-09 and FRD-13.
- A suspended buyer's escrow uses FRD-03 `held`.
- The pickup QR that FRD-11 asks for mostly exists since SIM-41.

It absorbs three open SIM tasks: SIM-47 (into ESC-07), SIM-48 (into ESC-11) and SIM-53 (into ESC-23).

Phases (spec §13):
- **P0** money safety: ships alone and needs no decisions.
- **P1** commitments and the transport choice.
- **P2** reputation.
- **P3** pickup and drop-off evidence, and no-shows.
- **P4** optional extras.

Every task follows `.ai/completeness-rules.md` (all 3 roles, mobile+web pair, tests, contracts, 3 languages) and goes through `/implement`.

| ID | Phase | Scope | Task | Done when |
|---|---|---|---|---|
| ESC-00 | Pre | @PO | ✅ DONE 2026-09-30. The user answered D1 to D9, recorded in spec §14: D1, D2, D3, D8 and D9 yes; D5 and D7 later; D6 goes to legal; D4 allows a pricier replacement truck when the extra is no more than the commission, covered from the commission leg. Decide D1 to D9 in spec §14 and write the answers into the spec. | Every §14 row has a decision; D6 (cargo loss) has a business or legal owner. |
| ESC-01 | P0 | @Server @Infra | ✅ DONE 2026-10-01 (#91, deployed to dev; follow-ups in ESC-47). What changed:<br>- **Checkout:** new `POST /escrow/order-checkout` with a server-generated merch_order_id and one open checkout per order (a retry re-signs the same URL). The mapping is written before Telebirr is called and is never deleted.<br>- **Webhook:** the order path classifies by payment reference in any status, funds with a status-conditional write, repairs a redelivery idempotently, flags duplicate or unmatched payments with an alarm and an audit row, and records an inbox row first.<br>- **Shared service:** `OrderFundingService` is used by both mock and live funding, with an idempotent stock decrement (`stockDecrementedFor`). The Telebirr references are server-only order fields.<br>- **Also:** `payment_failed` is system-only; the webhook Lambda gets the orders, crops and transaction-audit tables; the `{prefix}-order-payment-problems` alarm.<br>- **Review fixes (blind review, same day):** a failed mapping lookup is alarmed (`ORDER_PAYMENT_UNPROCESSED` with both ids), never dropped; a redelivery on a closed order (rejected, cancelled, settled and so on) repairs nothing; `openUntil` is 130 minutes after Telebirr answered; a `Failed` notification is only audited and keeps the checkout open; payment clock: `paymentSubmittedAt` stamped by the server, no new checkout attempt after 20h, the order cron moves `payment_submitted` to `payment_failed` after 24h; live `payment_submitted` refuses totals outside 100 to 500,000 ETB; bookkeeping after funding logs `ORDER_PAYMENT_BOOKKEEPING_FAILED` (outside the alarm); the payer is judged only on the funding payment; 400-day TTL on checkout rows; hashed 30-character merch_order_id; sanitized log ids; the phone lookup runs before the mapping is written; 4 stock CAS attempts with jitter; velocity counts every attempt that reaches Telebirr.<br>- **Re-review fixes (second blind review):** the 20h limit is checked before every Telebirr call, also when an interrupted attempt is finished late, and counts from `createdAt` for orders without `paymentSubmittedAt`; each checkout mapping records the order's `paymentSubmittedAt`, so a payment from an earlier attempt never funds a later one (`unmatched:stale_checkout`); a redelivery of a payment already judged `unmatched:*` or `duplicate` keeps that verdict; a malformed checkout mapping throws (alarmed) instead of falling through to the escrow path; an unprocessed payment gets a best-effort `order_payment_unprocessed` audit row; `/escrow/initiate` refuses an order that is funded or paid (`ESCROW_024`) even when its escrow record is missing; alarm and contract say where each case is recorded and when an unprocessed payment may be refunded.<br>- **Contracts:** escrow-protocol 1.5.0, order-lifecycle 1.7.0.<br>- **Tests:** server 1562 green, infra 59 green (5 suites), `cdk synth` green with and without `enableTelebirrLive`.<br>- **Known gaps:** ESC-47 (retry after `payment_failed` on an old order, and a possibly missing `payment_verified` transaction-audit row after a repair).<br>Client UI is ESC-44; Telebirr redelivery rules are ESC-45. Original task: live order funding, where `TelebirrWebhookFunction` moves the order `payment_submitted -> funded` on a verified payment through the same path as `TryAutoFundMockOrderAsync` (stock decrement, `CreateFromFundedOrderAsync`, audit). Replays are no-ops; the payment gates are unchanged. | A non-mock webhook funds an order exactly once and creates its escrow; replay test; contracts updated. |
| ESC-02 | P0 | @Server @Admin | ✅ DONE 2026-10-08 (#115, deployed to dev). Fail-closed live payouts: `TelebirrSandboxGateway` release and refund return `PAYOUT_NOT_SUPPORTED` instead of a fake success. Blocked payouts go to a manual payout queue: `GET /admin/payouts/manual`, `POST /admin/payouts/{escrowId}/{ref}/complete` with `{externalRef}`, and a `ManualPayoutsPage` in 3 languages, all audited. | No escrow reaches `completed` or `refunded` without a real or admin-entered reference; tests; admin e2e. |
| ESC-03 | P0 | @Server | ✅ DONE 2026-10-09 (#119; also delivers ESC-09's verdict order move and ESC-10's cron order/delivery repos; follow-ups FUP-16, FUP-17). Dispute verdicts settle: the settlement lock accepts `dispute_resolved` (`EscrowRepository.cs:141`); `Resolution` is validated; the order and delivery follow the verdict. Test the real condition expression, not a mocked lock. | `farmer_wins` releases and `buyer_wins` refunds, both from the sweep and from manual admin calls. |
| ESC-04 | P0 | @Server | Trucker leg retry: the cron finds `truckerPayoutPending` escrows and retries with the stable ref once the trucker is verified; the audit keeps the block reason; the escrow is not `completed` until every leg is paid. | A blocked trucker leg is paid after verification (test); no leg can stay unpaid silently. |
| ESC-05 | P0 | @Server | ✅ DONE 2026-10-09 (#124, deployed to dev; unknown outcomes park in the manual payout queue as GATEWAY_OUTCOME_UNKNOWN, hourly stale-lock recovery, unknown-outcome alarm; before staging or prod run the two scans in the PR body, FUP-23). No double pay, no stuck lock: on a timeout or unknown gateway outcome, keep the lock and mark the escrow for a status check or an admin, never a blind retry. The refund status write retries like the release write. A recovery sweep handles locks older than 15 minutes. Includes the blind retry after `GATEWAY_REF_MISSING` plus a failed queue write (the farmer leg releases the lock and retries although money may have moved). The separate unknown-outcome metric and alarm are done here too. | A timeout test proves no second payout; crash-window tests; alarm on unknown outcomes. |
| ESC-06 | P0 | @Server @Mobile @l10n | Stopgap for stuck accepted orders: no pickup 48h after the trip departs (7 days after acceptance for self-pickup) counts as a transport failure, and the buyer may cancel for a full refund (stock restored, pending delivery cancelled, slot released). Order escrows are never extended past funded + 21 days; an admin queue entry and an alarm take over instead. Mobile and web show Cancel only in that case. | Money can no longer be held indefinitely (test at +21 days); cancel tested for trucker and self-pickup orders; all 3 roles verified. |
| ESC-07 | P0 | @Server @Infra | Disputes always freeze money: remove the client `delivered -> disputed` order path, so disputes come only from a delivery dispute or `/escrow/dispute`. Give the escrow Lambda write grants on orders and deliveries (includes SIM-47). | Every dispute path freezes the escrow and marks the order and delivery disputed; tests; `cdk synth`. |
| ESC-08 | P0 | @Server @Mobile | ✅ DONE 2026-10-09 (#122, deployed to dev; slot claim and delivery written in one transaction; post-pickup cancel goes through a dispute verdict, admin cancel is FUP-19). Delivery rules: the delete action obeys `DeliveryTransitions` (after pickup only an admin can cancel) and is audited. Mobile farmer and buyer cancel send `update` (today they send `delete`, which only the trucker may send). The delivery slot claim is rolled back when the delivery write fails. A trucker delivery must match the order's trip and listing. | Tests per role and status; no orphan `activeDeliveryId`; cancel works on both mobile and web. |
| ESC-09 | P0 | @Server | (Verdict part done in ESC-03, #119.) Order status follows the money: a release moves the order `delivered -> disbursing -> settled`; an admin verdict moves it to `dispute_resolved`; `payment_failed` becomes system-only, as the contract says (that last part is done in ESC-01, 2026-09-30). | The order status matches the escrow after release, refund and verdict (tests); the admin stage is correct. |
| ESC-10 | P0 | @Server | (Cron order and delivery repositories done in ESC-03, #119.) Sweep hygiene: escrow sweeps stop re-reading the same first 100 skipped rows; blocked releases write their audit entry once, not hourly; `/escrow/initiate` escrows are not refunded mid-transit without an order and delivery check; the cron's `EscrowService` gets the order and delivery repositories. | A test with 150 blocked rows still processes the rest; audit volume is bounded. |
| ESC-11 | P0 | @Server | ✅ DONE 2026-10-01 (#92, with SIM-48). Scope sync idempotency keys to the caller (SIM-48). | A key claimed first by another user no longer drops your operation (test); SIM-48 closed. |
| ESC-12 | P0 | @Mobile | ✅ PART A DONE 2026-10-09 (#118: every ORDER_* refusal except ORDER_KYC_REQUIRED reverts and is explained). Part B is FUP-01. Client rejection handling: `ORDER_*` and the new permanent codes revert the optimistic Isar change, show a localized reason and stop retrying. Rejected ops no longer block `pullOrders`. The accept, reject and cancel guards throw instead of reporting success, and enqueue failures surface. On web, a `conflict` result counts as a failure, and trip cancel, start and deliver check the sync result. | Executor and bloc tests per code; mobile and web both show the reason; the queue never gets stuck. |
| ESC-13 | P0 | @Infra | MERGED 2026-10-08 (#113), deployed to dev. Still owed for done-when: run `scripts/money_alarms_synthetic.sh --fire --wait-clear` with valid AWS credentials, plus the spoof check in the PR #113 body. Money alarms: a log metric for settlement failures (`release_failed`, `refund_failed`, `trucker_release_failed`, unknown outcomes) and for `ORDER_ESCROW_LINK_FAILED`; alarms on admin queue age and on manual payouts older than 24h. | The alarms fire in a synthetic test; `cdk synth`. |
| ESC-14 | P1 | @Docs | Contracts before P1 code: order-lifecycle 2.0.0, escrow-protocol 2.0.0, trip-listing-api 2.0.0, delivery-verification-api 2.0.0, sync-protocol (commands, permanent codes, minimum app version) and data-models. Fix the header and footer version drift and the doc mismatches in survey §9. | Reviewed before P1 code; every new status, field, command and error code is documented. |
| ESC-15 | P1 | @Server | Commitment model: order fields (spec 4.4), `OrderCommitmentService`, sync commands (spec 10.1), v2 transitions and actors, and the legacy `status` mapping for old farmer clients. Also: buyer cancel rules (`ORDER_CANCEL_NOT_ALLOWED`), a fresh sign-in on `cancelCommitted`, explicit `fulfillment` and `pickupDate`, and `cancelledBy`/`faultParty`. | Table-driven tests for every status, command and role; an old farmer client can still accept and reject. |
| ESC-16 | P1 | @Server @Infra | Deadlines: `OrderDeadlines` (T1 to T12), sparse `DeadlineIndex` and `AdminQueueIndex`, `OrderCronFunction` every 15 minutes, SSM timing parameters actually read, the escrow sweep stops extending order escrows, and a 24h lead time at order create. | Each deadline kind tested, including re-runs and CAS races; no Scan; `cdk synth`. |
| ESC-17 | P1 | @Server @Mobile | Trip bookings: `bookedQuintals`/`bookedOrderIds` reserved atomically at funding; `matched` on the first accepted booking; date, price and route frozen once booked. Cancelling a trip runs the transport-failure flow for each of its orders via `TripIdIndex` (`QueryByTripAsync`). Departed unbooked trips close. Start and delivered rules. Browse shows `matched` trips with room and a future departure. Mobile and web cancel a trip the same way. | Race test on the last capacity; cascade test with 3 orders; browse and freeze tests. |
| ESC-18 | P1 | @Server | Listing guards: atomic stock decrement at funding (closes the oversell gap; the loser is rejected with a full refund and no penalty); delete blocked while orders are active; quantity cannot drop below the held stock. | Concurrent funding test; delete and quantity guard tests. |
| ESC-19 | P1 | @Server | Transport failure flow: `awaiting_transport` with the buyer's 3 options. Another truck, under the eligibility rules: a cheaper one refunds the difference; a pricier one is allowed when the extra is no more than the commission left, and is covered by a `shift#commission-to-transport` ledger row (D4; confirm that reading with the user before building). Or self-pickup with a `pickupDate` and a transport refund; or cancel with a full refund. At most 3 trucks, T5 and T6, farmer cancel without penalty in this status, and the order and escrow rebound with CAS. | Every option and every failure trigger tested; the ledger invariant holds. |
| ESC-20 | P1 | @Server @Infra | Escrow ledger: `escrow-ledger` table with `LedgerOpenIndex`, the write-ahead protocol (spec 4.9), per-leg refunds and releases, an invariant check and a recovery sweep. Also: the admin per-leg verdict (which makes `partial_refund` work), an automatic refund of duplicate payments, and audit rows for every movement. Retires the whole-escrow lock for order escrows. | Invariant tests on every path; unknown outcomes never retried blindly; admin partial verdict e2e. |
| ESC-21 | P1 | @Server @Infra | Order notifications: an orders stream (new and old images), `OrderNotifyStreamFunction`, `PushText` in en/am/om, and the push types in spec 4.12 with deep-link data. Depends on SIM-37 for the right language. | Handler tests from recorded stream images; each push type received on an Android device in dev. |
| ESC-22 | P1 | @Mobile | Client models and sync: the order, trip, delivery and payment schemas plus their web models and barrels. That covers the new fields, `awaitingTransport`, an unknown-status fallback that shows "Updating" with no actions, and `dispute_resolved` on payments. Parsers and the conflict resolver tolerate missing optional fields; Isar indexes `truckerId`; the new error codes are mapped. | Model and parser tests; `order.g.dart` regenerated; web build passes. |
| ESC-23 | P1 | @Mobile @l10n | UI for all 3 roles on mobile and web:<br>- the action matrix (spec 4.3), commitment rows with a deadline countdown, and the transport-choice sheet<br>- trucker Accept/Decline on order detail and on trip bookings<br>- cancel warnings that show the penalty<br>- place-order names the trucker and lets the buyer change it<br>- Requests and Action-needed cards on Home; a push opens the order (`/orders/:id`)<br>- Pay Now for `pending_payment` and `payment_failed` (done in ESC-44, #123); order detail pulls from the server<br>- remove the stale crop-detail Create Delivery button; localize the sync line and activity statuses<br>- the commission preview matches the server (closes SIM-53)<br>- blocs registered in `main.dart` and `main_web.dart`<br>Strings from spec §9, in 3 languages. | Widget tests per status and role; `flutter analyze`, tests and the web build green; checked by hand with the 3 test users. |
| ESC-24 | P1 | @Admin @Server | Admin: `rejected` and `awaiting_transport` in `transaction.ts` and `AdminFunction.DeriveStage`; a per-leg verdict UI; a ledger view on escrow and transaction detail; the trucker payout field (the mapper omits it today); `StuckOrdersPage`; i18n in en/am/om. | Admin tests; e2e covers the verdict and stuck-order views. |
| ESC-25 | P1 | @Server @Mobile | Rollout: a minimum app version gate on order commands (`APP_UPDATE_REQUIRED`), migration of in-flight orders (spec K2), and the feature flag `threePartyCommitments`. | An old client gets `APP_UPDATE_REQUIRED` (test); migration dry run on dev data. |
| ESC-26 | P1 | @Tests | Synthetic fleet personas, run on dev: the happy path; a trucker declines and the buyer picks another truck; a trucker cancels a trip with several orders; a buyer switches to self-pickup; a buyer cancels after a transport failure. | The fleet report shows each persona end to end, with the money reconciling. |
| ESC-27 | P2 | @Docs | New `reputation-api.md` 1.0.0; `rating-api.md` 1.1.0; `user-entity.md` 2.0.0 (matching the real users row); new `specs/architecture/reputation-architecture.md`. | Reviewed before P2 code. |
| ESC-28 | P2 | @Server @Infra | Reputation core:<br>- a `reputation-events` table with `AppealStatusIndex`<br>- `ReputationStreamFunction` as the second orders stream consumer<br>- `ReputationPolicy`: points, griefing caps, tiers, effective rating<br>- `ReputationService`: the aggregate on the users row, and a stored rating sum and count updated on rating create<br>- a daily job for expiry, earn-back, lifting restrictions and healing aggregates<br>- the flags `reputationPenalties` and `timeoutPenalties` | Policy unit tests per rule; a replayed stream event never counts twice; daily job tests. |
| ESC-29 | P2 | @Server | Reputation reads and gates:<br>- owner reputation copied onto crop and trip rows when it changes<br>- `ReputationGate` on listing and trip create and on accept (`REPUTATION_RESTRICTED`)<br>- restricted parties hidden from browse and match<br>- `GET /reputation/*` and the appeal endpoint<br>- rating summaries filtered by `rateeRole` and readable only by buyers, the party and admins<br>- suspension also blocks marketplace actions, not only sign-in | Gate tests for all 3 roles; browse test; endpoint tests. |
| ESC-30 | P2 | @Server @Mobile | Matching reliability: `reliabilityPct` replaces the broken `farmerReliability` in `MatchScorer` (Dart) and `MatchScoring` (C#), and trucker reliability ranks the trip suggestions. | Parity tests and golden vectors updated on both sides. |
| ESC-31 | P2 | @Mobile @l10n | Reputation UI:<br>- badge and effective rating on the listing card, crop detail, trip card, suggested trip and match cards<br>- a low-reputation confirm before ordering<br>- a My reputation screen with events, expiry and appeal<br>- Profile shows the user's own rating instead of "-"<br>- web counterparts, behind the flag `reputationPublicTags` | Widget tests; web build; 3 roles checked (farmers and truckers only ever see their own). |
| ESC-32 | P2 | @Admin @Server | Admin reputation: a user panel (points, events, excuse, tag or untag, restrict or lift, each with a reason and an expiry) and the appeals queue. Every action is audited. | Admin tests; e2e for excuse and appeal. |
| ESC-33 | P2 | @Server | FRD hooks: `NO_SHOW` and `DELIVERY_OVERDUE` write fraud signals; the buyer-griefing signal feeds FRD-09; FRD-13's payout delay reads the tier; `GPS_ANOMALY` is attributed to the trucker, not to the buyer who confirms. | Signal tests. |
| ESC-34 | P3 | @Server @Mobile @l10n | Arrival check-ins and pickup judgement: "I have arrived" (trucker or self-pickup buyer) and "Crops are ready" (farmer), each with GPS. Pickup is judged at T7 plus a 24h grace, per the table in spec 4.8. `NO_SHOW` points; late evidence goes to the admin queue; flag `pickupClassification`. | Every table row tested, including a late offline sync; widget tests. |
| ESC-35 | P3 | @Server @Mobile | Drop-off QR: a trucker-issued nonce in the secret store; the buyer scans it to confirm delivery; a 6-digit manual code fallback for both QRs. First check on a device whether trucker deliveries can be confirmed today (survey 4.5). | A trucker delivery completes end to end on Android and web, by QR and by code. |
| ESC-36 | P3 | @Server @Mobile | Silent buyer and overdue delivery: a delivery location on the order; the trucker's drop-off check-in with GPS and a photo; T9 auto-confirm or dispute; T8 opens a `delivery_overdue` dispute and restricts the trucker. | Tests for auto-confirm and each fallback; a fleet persona. |
| ESC-37 | P4 | @Server @Mobile | Top-up payment for a replacement truck whose extra cost is more than the commission left (D4; smaller extras are covered from the commission in ESC-19). | The buyer pays the difference beyond the commission; ledger `topup` row; tests. |
| ESC-38 | P4 | @Server @Mobile | Mutual cancel: the buyer asks, the farmer and any accepted trucker agree, full refund, no penalty (D7). | Tests; all 3 roles. |
| ESC-39 | P4 | @Server @Mobile | Weigh-in at pickup: the actual quantity, approved by the buyer, with an automatic partial refund of the difference. | Tests; the ledger reconciles. |
| ESC-40 | P4 | @Server | Money penalties: pay the trucker for a wasted trip when the farmer is absent, and charge a self-pickup buyer who doesn't come (D5). Needs ESC-43 first. | Tests; terms updated. |
| ESC-41 | P4 | @Admin @Server | Bulk excuse by woreda and date range, for region-wide events. | Admin e2e. |
| ESC-42 | P4 | @Server @Infra | ❌ DROPPED 2026-09-30: D8 says no SMS. Kept here for reference: an SMS fallback for `order_request` when a party has no push device, with a monthly cost cap. | Not planned. |
| ESC-43 | P1 | @PO @Docs | Terms of service covering the no-cancel rule, penalty points, refunds and withheld payouts, checked against Ethiopian consumer-protection and NBE payment rules. | Terms published before P1 or P2 goes live. |
| ESC-44 | P0 | @Mobile @l10n | MERGED 2026-10-09 (#123, deployed to dev; live end-to-end on Android and web still owed: needs a Telebirr sandbox run from an allow-listed network, with ESC-45. Deploy the server before any manual staging or prod web or Android build). Live order checkout in the app (mobile and web). With the live gateway, after `payment_submitted` syncs, call `POST /escrow/order-checkout` and open `toPayUrl` (reuse the `EscrowStatusScreen` launch pattern). Offer "Open Telebirr again" while `openUntil` is ahead, then poll the order until `funded`. Hide the mock reference field in live mode. Map `ESCROW_030` (including "payment time ran out", 20h after submit), `ESCROW_031`, `ESCROW_032` and `ESCROW_033` to messages in 3 languages, and the live `ORDER_VALIDATION` total-out-of-range refusal on `payment_submitted`. After `payment_failed`, a retry sends `pending_payment` and `payment_submitted` back to back (see ESC-47). The payment screen is still demo-only today (`payment_instructions_screen.dart` generates a mock reference). | A live-mode buyer pays an order end to end on Android and web (dev sandbox where reachable); widget and bloc tests; web build. |
| ESC-45 | P0 | @Server @Docs | Confirm Telebirr's live behaviour, then harden the webhook. To confirm: the notify retry policy and whether a retry keeps the original timestamp (the webhook rejects anything older than 300s), whether a query-order API exists, merch_order_id limits, what preOrder does with a repeated merch_order_id, and whether a re-signed H5 URL for the same prepay_id is accepted. Then: answer 5xx after signature checks on transient failures, add an inbox replay sweep for rows left `received` or `unprocessed`, and bound the gateway calls by the Lambda's remaining time. | Decisions recorded in `specs/decisions/`; the webhook recovers a transient failure without manual work (test); timeout test. |
| ESC-46 | P1 | @Server @Admin | CBE bank transfers in live mode: either an admin verification queue (the buyer's reference, checked against the bank statement, funds the order through `OrderFundingService`) or CBE hidden in live mode. Orders above Telebirr's 500,000 ETB per-payment limit need this path (in live mode `payment_submitted` refuses them with `ORDER_VALIDATION`, and the checkout returns `ESCROW_001`). Until then the server should refuse `cbe_transfer` on `payment_submitted` in live mode (FUP-20). | Decision recorded; the chosen path is tested; clients show the right option in live mode. |
| ESC-47 | P1 | @Server | (Item (1) done in ESC-44, #123: the server accepts `payment_failed -> payment_submitted` and the client sends one op.) ESC-01 follow-ups. (1) Retry after `payment_failed`: the retry goes `payment_failed → pending_payment → payment_submitted`, and the `pending_payment` auto-cancel counts from `createdAt`, so an order older than 24h is cancelled at the next hourly run unless the buyer submits again at once. Either allow `payment_failed → payment_submitted` directly (the payment gates and the payment clock run as usual) or count that auto-cancel from the last status change. (2) When the write after funding fails and a redelivery takes the repair path, no `payment_verified` transaction-audit row is written (the escrow audit still has the payment); write it on repair when it is missing, or accept the gap in the contract. | A buyer can retry a failed payment on an old order without it being auto-cancelled (test); the repair path leaves a `payment_verified` row, or the gap is documented. |

Order:
1. ESC-00 (done 2026-09-30).
2. P0, starting with ESC-01 (done 2026-10-01), 02, 03 and 05 (all of these before any live money), then ESC-44 and ESC-45 so live order payments are usable and recoverable. Do ESC-11 and ESC-12 early, because every later task relies on them.
3. ESC-14 and ESC-43.
4. ESC-15 to ESC-26. ESC-22 comes before ESC-23, and ESC-25 before the server flag goes on.
5. P2, starting with ESC-27.
6. P3.
7. P4 only on demand.

---

## In-App Account Deletion (2026-09-28) - @Scope tasks

Why: Apple App Store guideline 5.1.1(v) rejects any app that allows sign-up without in-app account deletion (blocker for the iOS app). Google Play also requires in-app deletion plus a web deletion link. Verified 2026-09-28: no deletion endpoint or UI exists in `mobile/lib` or `server/src`.
Principle: delete or anonymize personal data, but keep money and audit records (escrow, escrow-audit, transaction-audit, orders) with the user id pseudonymized, since they back financial and dispute history.
Every task must follow `.ai/completeness-rules.md` (all 3 roles, mobile+web pair, tests, contracts, 3 languages).

| ID | Pri | Scope | Task | Done when |
|---|---|---|---|---|
| ACC-01 | P1 | @Docs | ✅ DONE 2026-10-06 (#107). Write the deletion contract `specs/contracts/account-deletion.md`: endpoint, error codes, per-table policy (delete: device tokens, messages, drafts, crop/trip listings not in an active order, Fayda link; anonymize: users row, orders, deliveries, ratings; retain untouched: escrow, escrow-audit, transaction-audit), retention period, and the "blocked while active" rule. | Contract reviewed; every DynamoDB table has a listed action. |
| ACC-02 | P1 | @Server @Infra | `DELETE /user/account`: requires step-up re-auth (`AUTH_040` from auth-hardening, or password/OTP re-entry); rejects with a typed error while the user has an active order, unreleased escrow, open delivery or open dispute; otherwise runs the ACC-01 policy, deletes SNS endpoints, revokes refresh tokens and calls Cognito `AdminDeleteUser`. Idempotent and resumable (a partial failure can be retried). Writes a transaction-audit entry. Register DTOs in `AppJsonContext.cs`; add API route + IAM grants in CDK. | Tests for each role, the active-order/escrow block, idempotent retry, and token revocation; `cdk synth` passes; deleted user cannot log in. |
| ACC-03 | P1 | @Mobile | Profile > "Delete account" for Farmer, Trucker and Buyer: explains what is deleted and what is kept, confirm step, re-auth, shows the blocked reason when active items exist. Online-only action (clear offline message, never queued in sync). On success wipe Isar, secure storage, offline auth cache and push token, then go to login. Web counterpart via the web auth/storage pair. Strings in am/om/en `.arb`. | Works for all 3 roles on Android and web; `flutter analyze`, tests and web build pass; bloc + widget tests added. |
| ACC-04 | P2 | @Web @Docs | Public web deletion request page (Play Store "delete account" URL) linked from the website and privacy policy, pointing to the same flow after login. | URL live on dev website; privacy policy updated. |
| ACC-05 | P2 | @Admin | Admin sees deleted accounts as anonymized ("Deleted user") in orders, disputes and audit views without errors. | Admin e2e covers an order whose buyer was deleted. |

Order: ACC-01, then ACC-02, then ACC-03 (needs ACC-02 deployed to dev), then ACC-04/05.

---

## Synthetic Simulation Bugs (2026-09-28) - @Scope tasks

Source: 2-agent simulation on deployed dev (API run tag `simapi0928`, web UI via Playwright), new + seeded farmer/trucker/buyer.
Role visibility PASSED everywhere (API + UI, both directions). IDs continue the SIM series. Severity as in the 2026-09-24 section.
Every task must follow `.ai/completeness-rules.md` (all 3 roles, mobile+web pair, tests, contracts, 3 languages).

| ID | Sev | Scope | Task | Done when |
|---|---|---|---|---|
| SIM-38 | P0 | @Mobile @Server | ✅ DONE 2026-09-28 (`25546e7`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Web cannot create any delivery: web ids are `trp_<ms>_<hash>` / `dlv_...` but the server requires UUIDs (`SyncFunction.cs:2966`, self-pickup copies entityId into tripId at 3202). Handoff, QR, delivered, escrow release and ratings unreachable on web. | Web trip + delivery ids are UUIDs; trucker delivery and self-pickup persist on dev; tests. |
| SIM-39 | P1 | @Server | ✅ DONE 2026-09-28 (`ee4cdb0`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Sync create with an existing order/trip id adds a duplicate row (keys are id + createdAt, createdAt stamped now, so `attribute_not_exists` always passes). `GET /orders` listed one order 3 times. | Re-create returns duplicate/conflict, no new row; crops checked too; tests. |
| SIM-40 | P1 | @Server | ✅ DONE 2026-09-28 (`333ea3a`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Crop/trip create publishes client-sent `farmerName`/`farmerPhone`/`truckerName`/`truckerPhone`. | Server stamps them from the users table on create; tests. |
| SIM-41 | P1 | @Server | ✅ DONE 2026-09-28 (`68fba66`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Trucker can supply his own `qrNonce` on delivery create and confirm pickup without the farmer's QR (`SyncFunction.cs:201`, 3900-3947 legacy fallback). | Only the server secret-store nonce authorizes pickup; client qrNonce ignored; tests. |
| SIM-42 | P1 | @Tools @Infra | ✅ DONE 2026-09-28 (`5ef9fb1`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Dev Fayda mock has 5 fixed FINs, each binds to one account; dev can no longer register anyone. | Mock accepts any 16-digit FIN and derives a synthetic identity; fixed FINs keep their scenarios. |
| SIM-43 | P1 | @Mobile | ✅ DONE 2026-09-28 (`b520df1`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Crop create without GPS sends 0,0 although a place was picked from autocomplete; user sees "An error occurred" (`create_crop_listing_screen.dart:119`). | Selected place lat/lng used; clear message when no location; tests. |
| SIM-44 | P1 | @Mobile | ✅ DONE 2026-09-28 (`c642126`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Role FAB (Sell Crops / Post Trip / New Buying Intent) floats over pushed screens and covers Send, Save, Continue, Place Order, Call; taps open the FAB target (`adaptive_shell.dart:143,191`, `main_navigation.dart:196`). | FAB only on the tab root screens; widget test. |
| SIM-45 | P1 | @Mobile | ✅ DONE 2026-09-28 (`53c3fbb`, PR #67 merged eeb15db, DEPLOYED dev 2026-09-28). Web orders over 10,000 ETB can never be paid: "Verify with Fayda" is a dead end on web (`fayda_verification_screen.dart:62`). | Web runs the Fayda link flow (mock on dev) and the payment proceeds; tests. |
| SIM-46 | P2 | @Server | ✅ DONE 2026-10-06 (#109). Delivery create takes buyer/farmer phones from the client (`SyncFunction.cs:3093`). | Phones come from the order/users rows; test. |
| SIM-47 | P2 | @Server @Infra | ✅ DONE 2026-10-03 (#97). `POST /escrow/dispute` leaves order + delivery `delivered` and ratings allowed; escrow Lambda has read-only grants, write error swallowed (`api-stack.ts:1046`, `EscrowService.cs:1431`). | Order + delivery become disputed; failure surfaces; tests. |
| SIM-48 | P2 | @Server | ✅ DONE 2026-10-01 (#92, with ESC-11). Sync idempotency keys are global: another user can pre-claim your key and your op is dropped as duplicate (`SyncFunction.cs:636`). | Keys scoped to the caller; test. |
| SIM-49 | P2 | @Mobile | Buyer told to verify Fayda when the server says the FARMER must (`api_exception.dart:49-67`). | Message names the right party; test. |
| SIM-50 | P2 | @Mobile | Trip departure date shown one day early on web (UTC formatted without `toLocal`, `web_trip_listing_repository.dart:333`, `trip_card.dart:161`). | Local date shown; test. |
| SIM-51 | P2 | @Mobile | ✅ DONE 2026-10-08 (#95). Buyer search matches only location; crop and farmer names return 0 and an "offline" empty state (`browse_listings_screen.dart:247`). | Search covers crop, farmer, location; correct empty state. |
| SIM-52 | P2 | @Mobile | ✅ DONE 2026-10-06 (#108). Intent matches say "No trips yet" when trips exist ("Adama, Oromia" vs "Adama"). | Woreda matching normalized; test. |
| SIM-53 | P2 | @Mobile | ✅ DONE 2026-10-08 (#94). Order preview total charges commission on transport (18375) but server charges crop only (18350). | Preview equals server total. |
| SIM-54 | P2 | @Mobile | ✅ DONE 2026-10-06 (#105). Payment reference is the first 8 chars of the order id, "ORD_1790" for every web order (`payment_instructions_screen.dart:56`). | Unique reference per order. |
| SIM-55 | P2 | @Mobile | Web logout gets 401 from `POST /auth/logout`; refresh token likely not revoked server side. Root cause (SIM-60 review): `_clearSession()` in auth_bloc.dart deletes the refresh token before `logout` sends the stored 15-minute access token, so after 15 idle minutes the interceptor cannot refresh and the gateway answers 401. Also add an `AUTH_026` mapping in `auth_error.dart`. | Logout 200 and token revoked. |
| SIM-56 | P2 | @Mobile @Server | No profile edit UI and no profile get/update route; no crop edit/delete UI; buyer My Requests empty after reload (no list-intents endpoint). | Each feature present or explicitly descoped in TASK. |
| SIM-57 | P2 | @Mobile | Web is not offline-capable: no create queue, no cached browse, offline reload is a blank page. | Decision recorded; queue + cached browse or documented limitation. |
| SIM-58 | P2 | @L10n | ✅ DONE 2026-10-03 (#96). am `tapToListCrops` has a Thai char, om `syncedAgo` has Cyrillic, recent-activity statuses in English in am/om. | Strings fixed in all 3 files. |
| SIM-59 | P2 | @Mobile | Trip "To" autocomplete: tapping a suggestion opens the date picker (overlap). | Tap selects; widget test. |
| SIM-60 | P3 | @Server | ✅ DONE 2026-10-08 (#111). Logout can revoke another user's refresh token; trip update accepts past departure; intent update needs buyerId echoed; buyer sets `pickup_confirmed` without delivery; photos contract drift; device token accepts any platform. | One test per item. |
| SIM-61 | P3 | @Mobile | Minor UI: Enter does not submit login/chat, duplicate place suggestions, odd woreda names ("Boru Debre Birhan", "Adama" with macron), trucker banner copy, crop icons, past trips listed Open, 12 identical GETs per keystroke, Copy console error, "5 kg" for quintals, Trades 0, tab tap no pop to root, Logout under nav, Fayda popup close leaves spinner, password hint cut, notifications always empty. | Each fixed or split out. |

---

## Follow-ups from the agent team run (2026-10-08) - @Scope tasks

Found while shipping SIM-31/32/36/51/53/60, FRD-01, ESC-02, ESC-03, ESC-05, ESC-08, ESC-12, ESC-13, ESC-44 and CI-AUDIT (#114). Each came out of a plan validation or an adversarial review and was left out of that PR on purpose.

| ID | Sev | Scope | Task | Done when |
|---|---|---|---|---|
| FUP-01 | P1 | @Mobile @l10n | ESC-12 part B: client handling for `REPUTATION_RESTRICTED`, `TRIP_HAS_BOOKINGS`, `TRIP_CAPACITY_EXCEEDED`, `CROP_HAS_ACTIVE_ORDERS`, `APP_UPDATE_REQUIRED`, specific texts for every code in spec section 8, and trip and crop revert. Blocks ESC-15/17/18/25/29 and the minimum-version gate. | Executor and bloc tests per code on mobile and web. |
| FUP-02 | P1 | @Server @Admin | Manual refund of a payment the webhook did not apply: ESC-02 reserved the audit action `order_payment_refunded_manual` (escrow-protocol 1.8.0) for the account-deletion flow. Build the admin action and its audit row (or fold into ESC-20). | Admin can record the refund; audited; test. |
| FUP-05 | P2 | @Admin @Server | Manual payout queue has no claim step, so two admins can pay the same leg in the Telebirr portal; no cross-escrow uniqueness for `externalRef`. | Claim or lock per leg; duplicate refs refused. |
| FUP-06 | P2 | @Infra @Server | (Unknown-outcome metric and alarm, and the killed-Lambda token, done in ESC-05, #124.) Remaining money alarm gap from ESC-13: an edge-triggered "newly overdue" line (a standing ManualPayoutsOverdue ALARM masks new legs). The alarm SNS topic has no subscriber in code: confirm one exists per env. | Alarms fire for each case in the synthetic script. |
| FUP-08 | P3 | @Mobile | Delivery ops queued before SIM-60 with a local photo path keep failing on replay; strip a non-server `photoRef` in the sync executor. | Old queued op replays; test. |
| FUP-09 | P3 | @Server | SIM-11 `delivered` order gate still reads the delivery with an eventually consistent query (SyncFunction, near the SIM-60 consistent-read change). | Consistent read; test. |
| FUP-10 | P2 | @Tests | `e2e/webapp/role-isolation.spec.ts`: create tests never read the per-op status, trip and crop deletes send no version (e2e listings and trips leak into buyer browse), a login failure is swallowed as "API unavailable". Role visibility is a CRITICAL gate. | Spec fails when isolation breaks; cleanup verified. |
| FUP-11 | P2 | @CI | Run the Playwright `e2e/` specs in CI (today no workflow runs them; "E2E Tests" only builds an APK). Needs a deployed target, the shared login rate limit and the fraud velocity budget handled. | A CI job runs them and fails on regressions. |
| FUP-12 | P2 | @CI @Mobile | Flaky mobile tests: `sync_executor_security_test.dart` "a completed step-up re-triggers sync of the held op", `crop_listing_bloc_test.dart` (reported as a "(tearDownAll)" failure), `buyer_intent_matching_test.dart` "C1 failed pull" (fixed 300ms sleeps), and the fixed-sleep pattern in auth_interceptor, sync_executor, escrow_payment, crop_listing and conflict_resolution tests. Port the bounded `waitForStates` helper. | 20 consecutive CI runs without a flake. |
| FUP-13 | P3 | @CI @Infra | CI hygiene: the infra vuln-scan step parses `npm audit --json` fail-open (unparseable output counts as 0); `deploy-server.yml` runs `npm ci` with deploy credentials and install scripts on (use `--ignore-scripts`); `infra/test/fayda-mock.test.ts` runs in no CI step; aws-cdk-lib 2.273.x (bundled brace-expansion fix) deferred to Dependabot after a soak. | Each item fixed or closed with a reason. |
| FUP-14 | P3 | @Mobile | Web UX from SIM-32: on Flutter 3.38.9 resizing desktop to phone width empties the web semantics tree; the web phone layout has no language selector; register/forgot/reset fields still truncate long errors (set `errorMaxLines` in the theme). | Each checked on dev-app. |
| FUP-15 | P3 | @Docs | Stale CDK version (2.170.x) in CLAUDE.md, `.ai/quick-start.md` and `.claude/agents/infra.md`; CDK and TypeScript versions in `.ai/tech_stack.md` (stale against the lockfile). | Docs match the versions locked in `infra/package-lock.json` (aws-cdk-lib, typescript). |
| FUP-16 | P1 | @Admin @Server | ESC-03 follow-ups: an admin action to resolve an order that cannot follow its dispute verdict (today a goods-moving buyer_wins needs the audited `overrideOrderMismatch` refund and a pre-delivery farmer_wins waits for delivery), and a decision on refusing `/escrow/dispute` before delivery. | Admin can resolve every mismatch case without the override; decision recorded. |
| FUP-17 | P1 | @Ops | Before ESC-03 (#119) reaches staging or prod, count `status=dispute_resolved` escrows per env (scan command in the PR #119 body). The first sweep settles the backlog, 100 rows per hour; on a live env each row enters the manual payout queue and the ESC-13 overdue alarm. | Counts recorded per env before deploy. |
| FUP-18 | P2 | @Mobile | Mobile delivery create and confirm still queue a `cropListing` side-effect op that the server cannot route (it expects `crop_listing`), so it loops on every reconnect (`delivery_repository.dart` near :293 and :549; the cancel case was removed in ESC-08). | No unroutable op is queued; test. |
| FUP-19 | P2 | @Admin @Server | Admin delivery-cancel action after pickup (spec E9, ESC-24 scope). Today the path is a dispute verdict or the admin_override refund, and the delivery stays pickedUp or inTransit. | Admin can cancel with an audited reason; tests. |
| FUP-20 | P1 | @Server | Refuse `cbe_transfer` on `payment_submitted` in live mode until ESC-46 builds CBE verification, so a build with the wrong live flag cannot take a bank transfer with no funding path. | Live mode refuses it with a typed code; test. |
| FUP-21 | P2 | @Server | Pay then cancel queued offline in one sync batch: the second op validates against an eventually consistent order read (`ProcessOrderUpdateAsync` in SyncFunction, near :5548) and can act on a stale `created`. Use a consistent read on the order update path. | Same-batch chain test passes. |
| FUP-22 | P3 | @Mobile @Server | `Environment.current` maps CI's `prod` to development config (`environment.dart:25-31`), so prod web builds run with development settings; and the live total-out-of-range refusal on `payment_submitted` is matched on English text, not a dedicated code. | Prod builds use production config; dedicated code mapped in en/am/om. |
| FUP-23 | P1 | @Ops | Before ESC-05 (#124) reaches staging or prod, run the stale-lock scan and the dispute_resolved lock-vs-verdict scan in the PR #124 body (the first hourly recovery parks every stale lock it finds). | Scan results recorded per env before deploy. |
| FUP-24 | P3 | @Mobile | `SecurityEvents.pendingStepUp` is cleared only by a completed step-up or a logout (ESC-44 review), so a cancelled prompt can leave a stale pending state. | Cleared on cancel too; test. |
| FUP-25 | P3 | @Mobile | A rejected delivery cancel op still counts in the Home "Sync failed" number because the queue stores no error message (ESC-08). The delivery itself is restored and the reason shown. | Reverted-and-explained delivery refusals leave the failed count; test. |

---

## Deployment

- **API:** `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`
- **Region:** eu-north-1 (Stockholm) — dev only; staging/prod remain me-south-1
- **Lambda:** 15 functions, ARM64 Native AOT
- **DynamoDB:** 12+ tables (on-demand billing)
- **Web:** CloudFront + S3 (webapp + admin + website)

---

**Last Updated:** 2026-09-30 (ESC-00 decisions recorded, ESC-01 live order funding implemented and review-fixed, ESC-44..ESC-47 follow-ups added; 2026-09-29 EPIC ESC three-party escrow, commitments and reputation ESC-00..ESC-43 added, design + code survey linked; 2026-09-28 synthetic simulation SIM-38..SIM-61 added; in-app account deletion ACC-01..ACC-05 added; 2026-09-26 EPIC FRD fraud risk engine FRD-01..FRD-20 added, F-14 status corrected; 2026-09-24 synthetic fleet tasks SIM-01..SIM-37; prior PO audit 2026-03-19)
