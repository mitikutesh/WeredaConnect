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
| F-14 Fraud Detection | DONE (5 rules) | DONE (error handling) | N/A | DONE | DONE | 100% |
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
- [ ] **DEPLOY-01**: No server deploy pipeline — CDK stack exists but no CI/CD step publishes the .NET Lambda binary; every server fix (incl. ROL-01/02, CHAIN-01) is dead until this is wired. — Sprint 2
- [ ] **SRV-01**: `DEV_OTP_BYPASS` env-var guard. **Fix applied 2026-06-20** (added to ProductionBypassGuard FORBIDDEN_ENV_VARS, api-stack.ts). — verify on next synth
- [ ] **INFRA-02**: `TELEBIRR_MOCK=true` present in staging environment config (guard only fires for `isProd`) — Sprint 2
- [ ] **CI-01**: Maestro install uses `curl | bash` (unauthenticated remote execution in CI, pr-validation.yml:114) — pin a release download + verify SHA. — Sprint 2
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
- F-14 Fraud Detection (5 rules engine)
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
| SIM-23 | P2 | @Mobile | Error mapping: TLS/pin or 403 failures must not be reported as "offline"; show a network/security or permission message. | `/error-mapping` audit passes for these paths. |
| SIM-24 | P2 | @Server | Return `ESCROW_024` (funds already held for this order) when a delivery's order already has an active order-funded escrow, before the `ESCROW_009` check. | Buyer initiate on a funded order -> 409 `ESCROW_024`. |
| SIM-25 | P3 | @Server | Serialize `serverData` numbers/arrays with proper JSON types in `/sync` success and conflict results. | `version`, prices, `matchedListings` typed; mobile conflict resolver tests still pass. |
| SIM-26 | P3 | @Server @Docs | Reconcile crop read model with `crop-listing-api.md`: `quantityKg` naming, `{crop}` wrapper, default status filter (`available`), `nextToken`/`hasMore`, `limit` max 50. | Contract and server agree; e2e/webapp specs updated. |
| SIM-27 | P3 | @Server @Docs | Auth/escrow error codes: field-specific codes for missing fields (not `AUTH_007 Invalid JSON`), `AUTH_019` for revoked refresh tokens, phone-format rule (accept and document `09...` normalisation or reject), Fayda token requirement per environment. | Contract tables match responses. |
| SIM-28 | P3 | @Server | Idempotency: a reused key with a different payload -> error (not silent `duplicate`); race loser gets `conflict` + `serverData`. | Tests for both. |
| SIM-29 | P3 | @Server | Escrow record hygiene: bump version on link/confirm, `409 ESCROW_020` on second dispute, expose `refundReason`. | Per `escrow-protocol.md`. |
| SIM-30 | P3 | @Server @Docs | Commission: round to 2 dp; align contract (2.5 % deducted) with implementation (5 % added) or vice versa. | One documented rule; rounding test. |
| SIM-31 | P3 | @Server | Exclude cancelled/deleted trips from `GET /trips` list (consistent with detail 404). | List and detail agree. |
| SIM-32 | P3 | @Mobile | Login form: clear validation errors on edit (Android); make the web error visible and in the accessibility tree. | Manual + widget test. |
| SIM-33 | P3 | @Mobile | Display hygiene: guard impossible money/quantity values, count "Available Crops" across pages, never show raw ids/junk location, show trucker name on trip cards. | Screens verified for all 3 roles on web. |
| SIM-34 | P3 | @Server | Validate `since` on `GET /messages` (400 on non-ISO). | Test. |
| SIM-35 | P3 | @Infra | Serve `/version.json` on the dev webapp (or drop it from the cache strategy docs). | 200 with build info. |
| SIM-36 | P3 | @Tests | Fix stale e2e assertions: admin nav count (10 vs 11), webapp listing-flow invalid crop type, login spec passing on redirect timeout. | `e2e` suites green for the right reasons. |
| SIM-37 | P3 | @Mobile @Server | Sync `preferredLanguage` to the server profile when changed in Settings. | Language persists across devices. |

---

## Deployment

- **API:** `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`
- **Region:** eu-north-1 (Stockholm) — dev only; staging/prod remain me-south-1
- **Lambda:** 15 functions, ARM64 Native AOT
- **DynamoDB:** 12+ tables (on-demand billing)
- **Web:** CloudFront + S3 (webapp + admin + website)

---

**Last Updated:** 2026-09-24 (synthetic fleet simulation bug tasks SIM-01..SIM-37 added; prior PO audit 2026-03-19)
