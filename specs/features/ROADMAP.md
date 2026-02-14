# WeredaConnect Product Roadmap

**Version**: 1.1.0
**Date**: 2026-02-14
**Author**: Product Owner Agent
**Status**: Active — Sprint 3 in progress
**Review Cadence**: Bi-weekly (every 2 sprints)
**Last Review**: 2026-02-14 (Sprint 3 kickoff)

---

## 🎯 CURRENT SPRINT STATUS (Sprint 3: Feb 14-28, 2026)

**Sprint Goal**: Deploy and Secure the Backend, Lay Mobile Foundation

### Progress Overview
| Phase | Target | Actual | Status |
|-------|--------|--------|--------|
| **R1: Security Foundation** | 100% by Week 1 | 50% (2/4 done) | 🟡 IN PROGRESS |
| **R2: Auth + Data Models** | 80% by Week 2 | 50% (server done, mobile missing) | 🟡 IN PROGRESS |

### Sprint 3 Completion Tracking
- [ ] **A1**: F-01 Purge git history (2hr) — 🔴 BLOCKER
- [ ] **A2**: F-16 Deploy Lambda to AWS (1 day) — 🔴 BLOCKER
- [ ] **A3**: F-19 CI/CD security scans (4hr) — 🟡 HIGH
- [ ] **B1**: F-07 Create Isar schemas (2 days) — 🔴 FOUNDATION
- [ ] **B2**: F-04 Isar encryption (1 day) — 🔴 MUST DO
- [ ] **B3**: F-03 Mobile auth service + UI (3 days) — 🔴 MUST DO
- [ ] **B4**: F-07 Add 3 missing DynamoDB tables (3hr) — 🟡 HIGH
- [ ] **C1**: F-05 Certificate pinning (4hr) — 🟢 STRETCH GOAL

**See `SPRINT_PLANNING.md` for detailed sprint plan and acceptance criteria.**

---

## 1. Executive Assessment: Where Are We?

### Current Layer: Pre-Layer 1 (Foundation Incomplete)

WeredaConnect has completed **project scaffolding and governance** (Phases 1-8) but has **zero functional features implemented**. The codebase contains:

| Component | What Exists | What Is Missing |
|-----------|------------|-----------------|
| **Server** | HealthCheckFunction.cs, AppJsonContext.cs, GlobalUsings.cs | All domain logic, auth, sync endpoints, data access, validation |
| **Mobile** | main.dart (hello-world shell), l10n files (3 languages) | Isar schemas, sync engine, auth flow, all feature screens, BLoC states |
| **Infra** | CDK stacks (DynamoDB x3, S3, API Gateway shell) | Lambda deployment, Cognito, rate limiting, IAM per-function, monitoring alarms |
| **Specs** | Sync protocol contract, auth-encryption plan (proposed), docker evaluation, threat model | Escrow contract, crop listing contract, trip contract, delivery contract |

### Gap Analysis

The project has **extensive documentation and planning** but **no executable business logic**. This is both a strength (well-designed foundation) and a risk (no user-testable artifact after 8 phases).

**Critical Gaps Blocking Layer 1:**
1. No authentication system (users cannot register or log in)
2. No data models (no Isar schemas, no DynamoDB entity definitions)
3. No sync engine (offline-first promise is undeliverable)
4. No API endpoints beyond health check
5. Lambda not deployable (commented out in CDK)
6. 1 CRITICAL + 7 HIGH security findings unresolved
7. Private key still in git history (CRITICAL S-01)

### What Has Been Done Well
- Governance framework is mature and consistent
- Multi-agent conflict prevention is well-designed
- Offline-first architecture is thoroughly documented
- Ethiopian context is deeply understood and documented
- Technology choices are sound (.NET 10 AOT, Isar, BLoC)
- Sync protocol contract is production-quality

---

## 2. Build Order Compliance Check

The roadmap **must** follow: Trust > Reliability > Core Flow > Revenue > Expansion.

| Build Phase | Required Before Starting | Current Status |
|-------------|------------------------|----------------|
| **Trust** | Nothing | NOT STARTED -- Security remediation, auth, encryption all pending |
| **Reliability** | Trust foundation | NOT STARTED -- Sync engine, retry logic, conflict resolution not built |
| **Core Flow** | Reliability proven | NOT STARTED -- No crop listing, trip, delivery, or payment flow exists |
| **Revenue** | Core flow working | NOT STARTED -- Telebirr escrow, transaction fees not implemented |
| **Expansion** | Revenue flowing | NOT STARTED -- Ratings, analytics, cooperative dashboards deferred |

**Verdict**: We are at the very beginning of Layer 1. The roadmap must start with trust and security fundamentals.

---

## 3. Feature Inventory and Prioritization Scoring

### Scoring Model

Each feature scored 1-5 across five dimensions:
- **Revenue Impact (R)**: Does this directly enable transaction flow and monetization?
- **Risk Reduction (RR)**: Does this prevent fraud, data loss, or security breach?
- **User Adoption (UA)**: Does this make the app usable and attractive to farmers/truckers?
- **Compliance (C)**: Is this required by Ethiopian 2026 digital policy or security standards?
- **Technical Complexity (TC)**: How much effort? (reverse-weighted: 5 = trivial, 1 = massive)

**Priority Score = (R + RR + UA + C) - TC**

Higher score = higher priority. Maximum possible = 19 (all 5s, complexity 1). Minimum = -1 (all 1s, complexity 5).

### PO 5-Gate Filter

Before scoring, each feature must pass at least 3 of 5 gates:
1. Does it reduce fraud risk?
2. Does it improve offline reliability?
3. Does it reduce operational complexity?
4. Does it directly enable revenue?
5. Does it align with Ethiopian 2026 digital policy?

Features failing 3+ gates are marked DEFERRED.

---

### 3.1 Security Remediation Features (Trust Layer)

#### F-01: Purge Private Key from Git History (S-01)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 5 | CRITICAL: Exposed private key enables MITM, impersonation |
| User Adoption | 1 | Users don't see this |
| Compliance | 5 | Mandatory before any deployment |
| Complexity | 5 | Single git filter-branch/BFG operation |
| **Priority Score** | **7** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: This is a ticking time bomb. Anyone who clones the repo has the private key.
- **Risk If Delayed**: Complete compromise of any TLS/signing operations that used this key.
- **Dependencies**: None
- **Next Engineering Task**: Run BFG Repo-Cleaner to remove `server/localstack-data/cache/server.test.pem.key` from all history. Force-push cleaned history. Rotate any certificates or keys that were derived from this private key.
- **Responsible Agent**: @Security

#### F-02: Pre-commit Secret Detection Hooks (X-02)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 5 | Prevents future secret leaks |
| User Adoption | 1 | Infrastructure only |
| Compliance | 4 | Security best practice, audit requirement |
| Complexity | 5 | Install gitleaks, configure .pre-commit-config.yaml |
| **Priority Score** | **6** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Must prevent recurrence of S-01 before any further development.
- **Risk If Delayed**: Another secret leak, especially as auth credentials and API keys are introduced.
- **Dependencies**: F-01 (purge first, then prevent)
- **Next Engineering Task**: Install `gitleaks` as a pre-commit hook. Add `.gitleaks.toml` configuration. Add to CI/CD pipeline as well. Document in `.agents/security-sop.md`.
- **Responsible Agent**: @Security

#### F-03: API Authentication -- Cognito User Pool + JWT (I-01)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 4 | Required for all transaction flows (identity = revenue enabler) |
| Risk Reduction | 5 | Without auth, any client can call any API endpoint |
| User Adoption | 5 | Users need accounts to use the platform |
| Compliance | 5 | Fayda ID mandate requires identity verification |
| Complexity | 2 | Multi-service: Cognito CDK, Lambda authorizer, mobile auth flow, offline caching |
| **Priority Score** | **17** | |

- **PO Gates**: Fraud risk (YES), Offline (YES -- offline auth caching), Ops complexity (YES), Revenue (YES), Policy (YES) = 5/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Every subsequent feature depends on knowing who the user is. No auth = no trust = no transactions.
- **Risk If Delayed**: Nothing else can be built safely. Open API endpoints are a critical vulnerability.
- **Dependencies**: F-01, F-02 (security hygiene first)
- **Next Engineering Task**: This is a large feature. Decompose into sub-tasks:
  1. CDK: Create `auth-stack.ts` with Cognito User Pool (phone-based sign-up, SMS OTP, Ethiopian phone format)
  2. Server: Create `AuthorizerFunction.cs` with AOT-compatible JWT validation
  3. Server: Create `JwtValidator.cs` using System.IdentityModel.Tokens.Jwt
  4. Mobile: Create `AuthService` with online/offline login branching
  5. Mobile: Create `OfflineAuthCache` with PBKDF2 password hashing and secure storage
  6. Mobile: Create login/registration UI screens (low-literacy, icon-heavy, Amharic/Oromo)
  7. Integration: Wire Cognito authorizer to API Gateway
- **Responsible Agent**: @Infra (CDK), @Server (Lambda authorizer), @Mobile (auth flow + UI)
- **Existing Plan**: `specs/decisions/authentication-encryption-plan.md` -- APPROVED for implementation. The hybrid Cognito + offline caching approach is sound. Proceed as designed.

#### F-04: Isar Database Encryption (M-01)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Indirect -- protects user data that enables transactions |
| Risk Reduction | 5 | Unencrypted local DB on shared/stolen device = full data exposure |
| User Adoption | 3 | Users expect data privacy (documented in local_reality) |
| Compliance | 4 | Ethiopian data privacy expectations, shared device reality |
| Complexity | 4 | Isar supports encryption natively; key management via Android Keystore |
| **Priority Score** | **10** | |

- **PO Gates**: Fraud risk (YES), Offline (YES), Ops complexity (NO), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Must be enabled BEFORE any user data is written to the database. Retrofitting encryption on existing data is painful.
- **Risk If Delayed**: Any data written before encryption is enabled will be in plaintext. On shared devices (common in Ethiopia), other users could access crop listings, prices, and personal info.
- **Dependencies**: Isar schemas must be designed first (F-07)
- **Next Engineering Task**: Implement `_getEncryptionKey()` using `flutter_secure_storage` backed by Android Keystore. Pass encryption key to `Isar.open()`. Write integration test verifying DB file is unreadable without key.
- **Responsible Agent**: @Mobile

#### F-05: Certificate Pinning (M-03)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 5 | Prevents MITM attacks on 3G networks (critical in Ethiopian context) |
| User Adoption | 1 | Invisible to users |
| Compliance | 4 | Security standard for financial apps |
| Complexity | 4 | Dio adapter configuration; need cert rotation strategy |
| **Priority Score** | **7** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (NO), Revenue (NO), Policy (YES) = 2/5 -- BORDERLINE
- **Layer**: 1 - Foundation
- **Why Now**: 3G networks in Ethiopia are susceptible to interception. With auth tokens and financial data flowing, MITM protection is essential.
- **Risk If Delayed**: Auth tokens, crop data, and eventually payment data could be intercepted.
- **Dependencies**: F-03 (need API domain to pin against); can be implemented alongside auth
- **Next Engineering Task**: Configure `IOHttpClientAdapter` in Dio with SHA-256 pin for AWS API Gateway/CloudFront certificate. Add backup pin. Document cert rotation procedure. Note: Cannot be fully tested until API domain is provisioned.
- **Responsible Agent**: @Mobile

#### F-06: API Rate Limiting (I-02)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Protects platform stability for paying users |
| Risk Reduction | 5 | Prevents brute-force attacks on auth, DDoS on sync endpoints |
| User Adoption | 2 | Invisible unless rate-limited (bad UX) |
| Compliance | 3 | Security best practice |
| Complexity | 4 | API Gateway throttling config + optional WAF |
| **Priority Score** | **8** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Auth endpoints are brute-force targets. Rate limiting must be in place before auth goes live.
- **Risk If Delayed**: Brute-force password attacks, API abuse, unexpected AWS costs from DDoS.
- **Dependencies**: F-03 (auth endpoints must exist to rate-limit)
- **Next Engineering Task**: Add throttling configuration to API Gateway in `api-stack.ts`. Set default 100 req/s with 200 burst. Set auth endpoints to 10 req/s with 20 burst. Optionally add WAF rate-based rule (2000 req/5min per IP).
- **Responsible Agent**: @Infra

### 3.2 Data Model and Offline Foundation (Trust + Reliability Layers)

#### F-07: Core Domain Models -- Isar Schemas + DynamoDB Entity Definitions
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 4 | Data models define every entity in the transaction flow |
| Risk Reduction | 4 | Well-designed schemas prevent data corruption and sync conflicts |
| User Adoption | 5 | Users need to create listings, view trips, manage profiles |
| Compliance | 3 | Must support Fayda ID fields, Telebirr reference fields |
| Complexity | 2 | Large surface area: User, CropListing, Trip, Delivery, Payment, SyncOperation models across mobile + server |
| **Priority Score** | **14** | |

- **PO Gates**: Fraud risk (YES -- data integrity), Offline (YES), Ops complexity (YES), Revenue (YES), Policy (YES) = 5/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Every feature depends on data models. Without schemas, nothing can be built.
- **Risk If Delayed**: All feature development is blocked.
- **Dependencies**: None (but encryption F-04 should be enabled when opening DB)
- **Next Engineering Task**: Design and implement the following models:

  **Mobile (Isar schemas in `mobile/lib/core/database/`):**
  - `User` -- userId, phoneNumber, name, faydaId, role (farmer/trucker/buyer), woreda, createdAt, updatedAt, version
  - `CropListing` -- listingId, farmerId, cropType, quantity (quintals), pricePerKg, location (lat/lng), description, audioNoteRef, status (draft/active/sold/expired), createdAt, updatedAt, version
  - `Trip` -- tripId, truckerId, fromWoreda, toWoreda, departureDate, capacity, pricePerQuintal, status (open/matched/in_transit/delivered/cancelled), matchedListings[], createdAt, updatedAt, version
  - `Delivery` -- deliveryId, tripId, listingId, qrToken, gpsPickup, gpsDropoff, photoEvidenceRef, status (pending/picked_up/in_transit/delivered/disputed), confirmedAt, version
  - `Payment` -- paymentId, deliveryId, farmerId, buyerId, amount, telebirrRef, status (escrowed/released/disputed/refunded), createdAt, version
  - `SyncOperation` -- operationId, idempotencyKey, entityType, entityId, action, data, status (pending/syncing/synced/failed), retryCount, createdAt

  **Server (DynamoDB entity definitions in `server/src/Models/`):**
  - Mirror of above with DynamoDB-specific annotations
  - Add GSI definitions for query patterns (e.g., crops by woreda, trips by status)

  **Infrastructure (DynamoDB table updates in `infra/lib/stacks/database-stack.ts`):**
  - Add Deliveries table, Payments table, SyncMetadata table
  - Add GSIs: CropsTable GSI (status-createdAt), TripsTable GSI (status-departureDate)

- **Responsible Agent**: @Mobile (Isar), @Server (models), @Infra (DynamoDB tables + GSIs)

#### F-08: Sync Engine Foundation
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 4 | Without sync, offline data never reaches server -- no transactions possible |
| Risk Reduction | 5 | Prevents data loss when users go offline (most common scenario) |
| User Adoption | 5 | Core promise: "works offline" -- this is the mechanism |
| Compliance | 2 | Not directly compliance-related |
| Complexity | 1 | Most complex feature: queue management, retry logic, conflict resolution, idempotency, batch processing, connectivity detection |
| **Priority Score** | **15** | |

- **PO Gates**: Fraud risk (YES -- idempotency prevents duplicates), Offline (YES -- this IS offline), Ops complexity (YES), Revenue (YES), Policy (NO) = 4/5 PASS
- **Layer**: 1 - Foundation (crosses into Layer 2 - Reliability)
- **Why Now**: The sync engine IS the offline-first promise. Without it, WeredaConnect is just another online-only app.
- **Risk If Delayed**: The entire value proposition collapses. Farmers with no connectivity cannot use the app.
- **Dependencies**: F-07 (data models), F-03 (auth tokens for API calls)
- **Next Engineering Task**: Implement in phases:

  **Phase A -- Local Queue (Mobile):**
  - `SyncQueue` class in `mobile/lib/core/sync/` that persists operations to Isar
  - `ConnectivityMonitor` using `connectivity_plus` to detect online/offline state
  - Enqueue operations when offline, with idempotency keys generated at action time

  **Phase B -- Sync Executor (Mobile):**
  - `SyncExecutor` that processes queue when connectivity is detected
  - Batch operations per sync protocol contract
  - Exponential backoff on failure
  - Retry logic (max 3 retries per operation, then mark as failed)

  **Phase C -- Server Sync Endpoint:**
  - `SyncFunction.cs` Lambda handler for POST /sync
  - Idempotency key check against DynamoDB SyncMetadata table
  - Version-based conflict resolution per sync protocol contract
  - Return conflict data for client to resolve

  **Phase D -- Conflict Resolution (Mobile):**
  - Handle `SYNC_CONFLICT` responses
  - Accept server version (default strategy)
  - Show conflict UI for user-resolvable cases (e.g., price changed while offline)

- **Responsible Agent**: @Mobile (queue + executor + conflict UI), @Server (sync endpoint), @Architect (contract amendments if needed)

#### F-09: Input Validation Middleware (S-04)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Prevents garbage data from corrupting transaction records |
| Risk Reduction | 5 | Prevents injection attacks, malformed data, API abuse |
| User Adoption | 2 | Invisible to well-behaved users |
| Compliance | 4 | OWASP API Top 10 requirement |
| Complexity | 3 | Manual validators (AOT-compatible), Ethiopian phone format, crop type enums |
| **Priority Score** | **10** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation
- **Why Now**: Must exist before any data-accepting endpoints go live. Validation is not something to retrofit.
- **Risk If Delayed**: Injection attacks, corrupt data in DynamoDB, sync failures from malformed payloads.
- **Dependencies**: F-07 (must know the data models to validate them)
- **Next Engineering Task**: Create `server/src/Validation/RequestValidator.cs` with static validation methods. No reflection. Ethiopian phone format (+251XXXXXXXXX). Crop type enum validation. Quantity/price range checks. String length limits. Create `server/src/Models/ValidationResult.cs`. Add validation to all Lambda handler entry points.
- **Responsible Agent**: @Server

### 3.3 Core Transaction Flow (Layer 1 completion + Layer 3)

#### F-10: Crop Listing Flow (Create + Browse)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 5 | This is the entry point for the entire marketplace |
| Risk Reduction | 3 | Validated listings reduce fraud |
| User Adoption | 5 | Primary user action: "I have crops to sell" |
| Compliance | 2 | Standard marketplace functionality |
| Complexity | 2 | Full-stack: Mobile UI + BLoC + Isar + sync + Lambda CRUD + DynamoDB |
| **Priority Score** | **13** | |

- **PO Gates**: Fraud risk (YES -- listing validation), Offline (YES -- offline create), Ops complexity (YES), Revenue (YES), Policy (NO) = 4/5 PASS
- **Layer**: 1 - Foundation (Core Transaction Flow)
- **Why Now**: First user-visible feature. Proves the entire stack works end-to-end. Validates offline-first architecture.
- **Risk If Delayed**: No user-testable feature exists. Cannot validate architecture assumptions.
- **Dependencies**: F-07 (data models), F-08 (sync engine), F-03 (auth), F-09 (validation)
- **Next Engineering Task**:

  **Mobile:**
  - Create `CropListingScreen` with form: crop type (dropdown), quantity (number), price (number), location (auto GPS), description (text), audio note (record button)
  - Create `CropListingBLoC` with states: initial, loading, created, error
  - Save to Isar immediately (offline-first)
  - Enqueue sync operation
  - Create `CropBrowseScreen` with filterable list (by crop type, woreda, price range)

  **Server:**
  - Create `CropFunction.cs` with handlers: CreateCrop, GetCrops, GetCropById
  - Validate input (F-09)
  - Store in DynamoDB CropsTable
  - Return sync-compatible response

  **Infra:**
  - Uncomment Lambda in CDK, configure CropFunction
  - Add API Gateway route: POST /crops, GET /crops, GET /crops/{id}

- **Responsible Agent**: @Mobile (UI + BLoC + Isar), @Server (Lambda handlers), @Infra (API routes)

#### F-11: Trip Listing Flow (Create + Browse + Match)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 5 | Transport is the marketplace's other side |
| Risk Reduction | 3 | Verified truckers reduce fraud |
| User Adoption | 5 | Truckers need to post available capacity |
| Compliance | 2 | Standard marketplace |
| Complexity | 2 | Full-stack, similar to F-10 but with route/date matching logic |
| **Priority Score** | **13** | |

- **PO Gates**: Fraud risk (YES), Offline (YES), Ops complexity (YES), Revenue (YES), Policy (NO) = 4/5 PASS
- **Layer**: 1 - Foundation (Core Transaction Flow)
- **Why Now**: Without trips, crop listings have no transport. The marketplace needs both sides.
- **Risk If Delayed**: One-sided marketplace (listings with no fulfillment).
- **Dependencies**: F-10 (crop listings should exist to match against), F-07, F-08, F-03
- **Next Engineering Task**: Similar structure to F-10 but for Trip entity. Include basic matching: show crop listings in the same woreda/route corridor. Manual matching (trucker selects listings to carry).
- **Responsible Agent**: @Mobile, @Server, @Infra

#### F-12: Delivery Verification (QR Handshake + GPS)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 5 | Delivery confirmation triggers payment release |
| Risk Reduction | 5 | Prevents "I delivered but didn't get paid" / "They didn't deliver but claimed they did" |
| User Adoption | 4 | Users need proof of delivery |
| Compliance | 4 | Ethiopian digital commerce requirements; insurance trigger |
| Complexity | 2 | QR generation/scanning, GPS capture, photo upload, offline token generation |
| **Priority Score** | **16** | |

- **PO Gates**: Fraud risk (YES), Offline (YES -- QR works offline), Ops complexity (YES), Revenue (YES), Policy (YES) = 5/5 PASS
- **Layer**: 2 - Stability and Trust
- **Why Now**: This is the trust mechanism. Without verifiable delivery, escrow cannot be released, and the entire payment flow is blocked.
- **Risk If Delayed**: No way to confirm deliveries. Payment disputes become unresolvable. Platform trust collapses.
- **Dependencies**: F-10, F-11 (crops and trips must exist), F-08 (sync)
- **Next Engineering Task**:

  **Mobile:**
  - QR code generation at pickup (contains: deliveryId, tripId, listingId, timestamp, farmer signature)
  - QR code scanning at delivery (buyer/receiver confirms)
  - GPS coordinate capture at pickup and delivery
  - Photo evidence capture (optional but encouraged)
  - All stored locally first, synced when online

  **Server:**
  - `DeliveryFunction.cs` with: CreateDelivery, ConfirmPickup, ConfirmDelivery
  - GPS distance validation (pickup GPS must be near farmer location, delivery GPS near buyer location)
  - Delivery status state machine enforcement

- **Responsible Agent**: @Mobile (QR + GPS + camera), @Server (validation + state machine)

#### F-13: Telebirr Escrow Integration (Hold + Release)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 5 | This IS the revenue mechanism. Transaction fees come from here. |
| Risk Reduction | 5 | Escrow prevents payment fraud -- buyers can't refuse to pay, sellers can't run with money |
| User Adoption | 5 | Financial safety is the #1 trust factor |
| Compliance | 5 | Fayda ID mandatory for financial transactions (Jan 2026 policy) |
| Complexity | 1 | External API integration (Telebirr), idempotent escrow operations, offline payment initiation, regulatory compliance |
| **Priority Score** | **19** | |

- **PO Gates**: Fraud risk (YES), Offline (YES -- payment intent queued), Ops complexity (YES), Revenue (YES), Policy (YES) = 5/5 PASS
- **Layer**: 1 - Foundation (but depends on delivery flow)
- **Why Now**: This is the highest-scoring feature because it enables revenue, prevents fraud, and satisfies compliance simultaneously. However, it has the highest complexity and depends on delivery verification.
- **Risk If Delayed**: No revenue. No financial trust. Platform is a listings board without transactions.
- **Dependencies**: F-12 (delivery verification triggers release), F-03 (auth/Fayda ID), F-07 (Payment model)
- **Next Engineering Task**:

  **Server:**
  - Create `EscrowFunction.cs` with: InitiateEscrow, ReleaseEscrow, DisputeEscrow, RefundEscrow
  - Idempotent operations (critical -- network failures during payment are common)
  - Telebirr API integration (hold funds on buyer confirmation, release on delivery verification)
  - Audit trail in DynamoDB for every escrow state change

  **Mobile:**
  - Payment initiation screen (buyer sees amount, confirms via Telebirr)
  - Payment status tracking (escrowed -> released -> complete)
  - Dispute flow (raise dispute within 48 hours of delivery)

  **Contracts:**
  - Define `specs/contracts/escrow-protocol.md` (Architect must create)

- **Responsible Agent**: @Server (Telebirr integration), @Mobile (payment UI), @Architect (escrow contract)

### 3.4 Stability and Trust Features (Layer 2)

#### F-14: Fraud Detection Checks
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Protects revenue by preventing fraudulent transactions |
| Risk Reduction | 5 | Detects listing spam, price manipulation, fake deliveries |
| User Adoption | 3 | Users benefit indirectly from platform safety |
| Compliance | 4 | Required for financial platform |
| Complexity | 2 | Rules engine, anomaly detection, velocity checks |
| **Priority Score** | **13** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (YES), Policy (YES) = 4/5 PASS
- **Layer**: 2 - Stability and Trust
- **Why Now**: As real transactions begin, fraud attempts will follow immediately.
- **Risk If Delayed**: Financial losses, platform reputation damage, user churn.
- **Dependencies**: F-10, F-11, F-12, F-13 (need transaction flow to monitor)
- **Next Engineering Task**: Implement server-side rules: duplicate listing detection, price anomaly flags (>3 standard deviations from regional average), velocity limits (max 5 listings/hour per user), GPS consistency checks (listing location vs user's registered woreda).
- **Responsible Agent**: @Server, @Security

#### F-15: Conflict Resolution UI (Sync Conflicts)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Prevents data loss that blocks transactions |
| Risk Reduction | 4 | Prevents silent data overwrite, user confusion |
| User Adoption | 4 | Users must understand and resolve conflicts |
| Compliance | 2 | Not compliance-related |
| Complexity | 3 | UI design for low-literacy users, conflict visualization |
| **Priority Score** | **10** | |

- **PO Gates**: Fraud risk (YES), Offline (YES), Ops complexity (YES), Revenue (YES), Policy (NO) = 4/5 PASS
- **Layer**: 2 - Stability and Trust
- **Why Now**: As sync engine goes live, conflicts will occur. Users need a way to resolve them.
- **Risk If Delayed**: Silent data loss or user confusion when edits conflict.
- **Dependencies**: F-08 (sync engine)
- **Next Engineering Task**: Design conflict resolution UI with visual diff (side-by-side comparison). Use icons and colors (green = yours, blue = server's). Auto-resolve where possible (server wins for financial data, user choice for listings). Voice prompts for low-literacy users.
- **Responsible Agent**: @Mobile

### 3.5 Infrastructure Hardening (Layer 1-2, woven throughout)

#### F-16: Lambda Deployment Pipeline
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | No server = no transactions |
| Risk Reduction | 3 | Proper deployment prevents downtime |
| User Adoption | 3 | Backend availability is prerequisite for features |
| Compliance | 2 | Operational requirement |
| Complexity | 3 | CDK Lambda construct, .NET AOT publish, deployment script |
| **Priority Score** | **8** | |

- **PO Gates**: Fraud risk (NO), Offline (NO), Ops complexity (YES), Revenue (YES), Policy (NO) = 2/5 -- BORDERLINE but REQUIRED as enabler
- **Layer**: 1 - Foundation (infrastructure prerequisite)
- **Why Now**: Lambda is commented out. Until it deploys, there is no backend.
- **Risk If Delayed**: All server features are blocked.
- **Dependencies**: None (can be done with HealthCheckFunction)
- **Next Engineering Task**: Fix .NET 10 AOT publish for Linux ARM64. Uncomment Lambda in CDK. Deploy HealthCheckFunction to dev environment. Verify cold start <2s. Create deployment script in `scripts/deploy.sh`.
- **Responsible Agent**: @Infra, @Server

#### F-17: Least-Privilege IAM per Lambda (I-03)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 4 | Limits blast radius of compromised Lambda |
| User Adoption | 1 | Invisible to users |
| Compliance | 4 | AWS security best practice, audit requirement |
| Complexity | 4 | Per-function IAM roles in CDK |
| **Priority Score** | **6** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 2 - Stability and Trust
- **Dependencies**: F-16 (Lambda must be deployed first)
- **Next Engineering Task**: Create separate IAM roles per Lambda function in CDK. HealthCheck: read-only on all tables. CropFunction: read-write on CropsTable only. SyncFunction: read-write on specific tables per entity type. EscrowFunction: read-write on Payments table + Telebirr API access.
- **Responsible Agent**: @Infra

#### F-18: S3 Access Logging + Versioning (I-06, I-07)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 3 | Audit trail for uploaded content; versioning prevents accidental deletion |
| User Adoption | 1 | Invisible |
| Compliance | 3 | Audit trail requirement |
| Complexity | 5 | Simple CDK configuration change |
| **Priority Score** | **3** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 2 - Stability and Trust
- **Dependencies**: None
- **Next Engineering Task**: Enable access logging on uploads bucket (target: separate logging bucket). Enable versioning on production uploads bucket (not dev). Add to `database-stack.ts`.
- **Responsible Agent**: @Infra

#### F-19: Security Scanning in CI/CD (X-03)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 4 | Catches vulnerable dependencies before deployment |
| User Adoption | 1 | Invisible |
| Compliance | 4 | Security hygiene |
| Complexity | 4 | npm audit, dotnet list --vulnerable, GitHub Actions workflow |
| **Priority Score** | **6** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (YES) = 3/5 PASS
- **Layer**: 2 - Stability and Trust
- **Dependencies**: F-02 (pre-commit hooks should be in place first)
- **Next Engineering Task**: Add security scanning step to GitHub Actions. Run `npm audit` for infra, `dotnet list package --vulnerable` for server. Fail pipeline on CRITICAL/HIGH vulnerabilities. Add Dependabot configuration with auto-merge for patch versions only.
- **Responsible Agent**: @Security, @Infra

### 3.6 Optimization Features (Layer 3)

#### F-20: Voice-to-Text Crop Listing (Amharic + Afan Oromo)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 4 | Enables low-literacy users to create listings = larger user base = more transactions |
| Risk Reduction | 2 | Reduces incorrect manual entry |
| User Adoption | 5 | GAME CHANGER for rural farmers who cannot type |
| Compliance | 2 | Accessibility feature |
| Complexity | 1 | Speech recognition in Amharic/Oromo, NLP for crop categorization, audio compression |
| **Priority Score** | **12** | |

- **PO Gates**: Fraud risk (NO), Offline (YES -- must work offline), Ops complexity (NO), Revenue (YES), Policy (NO) = 2/5 DEFERRED to Layer 3
- **Layer**: 3 - Optimization (signature feature but requires stable core flow)
- **Why Not Now**: The core listing flow (typed input) must work first. Voice-to-text is an enhancement.
- **Risk If Delayed**: Lower adoption among low-literacy farmers initially. But a broken voice feature is worse than no voice feature.
- **Dependencies**: F-10 (crop listing flow), offline speech recognition model
- **Next Engineering Task**: Research offline-capable speech recognition for Amharic/Oromo. Evaluate: Google ML Kit (online), Vosk (offline, community models), Whisper.cpp (offline, need Amharic model). Audio compression to Opus 16-32kbps for upload.
- **Responsible Agent**: @Mobile

#### F-21: ProGuard Obfuscation + Release Build Hardening (M-04)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | No direct revenue impact |
| Risk Reduction | 3 | Makes reverse engineering harder |
| User Adoption | 1 | Invisible |
| Compliance | 3 | Mobile security standard |
| Complexity | 4 | ProGuard rules, --obfuscate, --split-debug-info |
| **Priority Score** | **4** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (NO), Revenue (NO), Policy (YES) = 2/5 DEFERRED but needed before production
- **Layer**: 3 - Optimization (pre-production hardening)
- **Dependencies**: Working app to obfuscate
- **Next Engineering Task**: Add ProGuard rules in `mobile/android/app/proguard-rules.pro`. Add `--obfuscate --split-debug-info=build/debug-info` to release build. Test that obfuscation doesn't break Isar code generation.
- **Responsible Agent**: @Mobile

#### F-22: Crash Reporting (M-05)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Faster bug fixes = fewer lost transactions |
| Risk Reduction | 3 | Detects production issues before users report them |
| User Adoption | 3 | More stable app = better retention |
| Compliance | 2 | Operational requirement |
| Complexity | 4 | Sentry or Crashlytics integration |
| **Priority Score** | **6** | |

- **PO Gates**: Fraud risk (NO), Offline (YES -- offline crash queue), Ops complexity (YES), Revenue (NO), Policy (NO) = 2/5 DEFERRED but needed before production
- **Layer**: 3 - Optimization
- **Dependencies**: Working app
- **Next Engineering Task**: Integrate Sentry Flutter SDK. Configure offline crash buffering (crashes stored locally, sent when online). Set up source map upload for obfuscated builds. Ensure Sentry payload <10KB per crash report.
- **Responsible Agent**: @Mobile

#### F-23: Notification Batching
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Timely notifications drive engagement and transactions |
| Risk Reduction | 2 | Reduces missed pickups/deliveries |
| User Adoption | 4 | Users need to know when crops are matched, trips start |
| Compliance | 1 | Nice to have |
| Complexity | 3 | Firebase Cloud Messaging, batch scheduling, offline queue |
| **Priority Score** | **7** | |

- **PO Gates**: Fraud risk (NO), Offline (YES -- batched for connectivity), Ops complexity (YES), Revenue (YES), Policy (NO) = 3/5 PASS but low priority
- **Layer**: 3 - Optimization
- **Dependencies**: F-10, F-11, F-12 (events to notify about)
- **Next Engineering Task**: Integrate FCM. Batch notifications server-side (max 1 push per 15 minutes per user to save battery). Queue notifications for offline users. Include Amharic/Oromo message templates.
- **Responsible Agent**: @Server (scheduling), @Mobile (FCM integration)

### 3.7 Growth Features (Layer 4)

#### F-24: Rating and Review System
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Trust signals increase transaction volume |
| Risk Reduction | 3 | Bad actors identified through low ratings |
| User Adoption | 3 | Users want to vet counterparties |
| Compliance | 1 | Not required |
| Complexity | 3 | Review model, aggregation, display |
| **Priority Score** | **7** | |

- **PO Gates**: Fraud risk (YES), Offline (YES -- cache ratings), Ops complexity (NO), Revenue (YES), Policy (NO) = 3/5 PASS but DEFERRED to Layer 4
- **Layer**: 4 - Growth
- **Dependencies**: F-10, F-11, F-12, F-13 (completed transactions to rate)

#### F-25: Cooperative Dashboards
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Cooperatives aggregate farmers = larger transactions |
| Risk Reduction | 2 | Cooperative oversight reduces fraud |
| User Adoption | 3 | Cooperatives are key distribution channel in rural Ethiopia |
| Compliance | 2 | Not required |
| Complexity | 2 | Web dashboard, read-only views, aggregation queries |
| **Priority Score** | **8** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (YES), Policy (NO) = 3/5 PASS but DEFERRED to Layer 4
- **Layer**: 4 - Growth
- **Dependencies**: F-10, F-13 (data to display in dashboard)

#### F-26: Analytics and Insights
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Informs pricing, identifies growth opportunities |
| Risk Reduction | 2 | Identifies fraud patterns |
| User Adoption | 2 | Users don't directly benefit |
| Compliance | 1 | Not required |
| Complexity | 2 | CloudWatch metrics, custom dashboards, data aggregation |
| **Priority Score** | **5** | |

- **PO Gates**: Fraud risk (YES), Offline (NO), Ops complexity (YES), Revenue (NO), Policy (NO) = 2/5 DEFERRED
- **Layer**: 4 - Growth
- **Dependencies**: Production traffic to analyze

#### F-27: Insurance Trigger Integration (AICE)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Insurance premium adds revenue, reduces dispute costs |
| Risk Reduction | 4 | Coverage for theft, spoilage reduces platform liability |
| User Adoption | 4 | Major trust signal for high-value shipments |
| Compliance | 3 | Aligns with Ethiopian financial inclusion goals |
| Complexity | 1 | External API integration, trigger logic, claims processing |
| **Priority Score** | **13** | |

- **PO Gates**: Fraud risk (YES), Offline (YES -- trigger queued), Ops complexity (YES), Revenue (YES), Policy (YES) = 5/5 PASS
- **Layer**: 3-4 (Optimization/Growth -- depends on stable delivery flow)
- **Dependencies**: F-12 (delivery verification), F-13 (escrow for premium collection)
- **Note**: High score but AICE API availability and terms must be confirmed before implementation begins.

### 3.8 Hardening Features (LOW priority, pre-production)

#### F-28: DynamoDB KMS Encryption (I-04)
| Score | 3 | Low urgency -- S3-managed encryption sufficient for MVP |
- **Layer**: 3 - Optimization
- **Dependencies**: Production deployment

#### F-29: S3 KMS Encryption (I-05)
| Score | 3 | Low urgency -- S3-managed encryption sufficient for MVP |
- **Layer**: 3 - Optimization
- **Dependencies**: Production deployment

#### F-30: VPC Deployment Evaluation (I-08)
| Score | 3 | Must test cold start impact; defer unless compliance requires |
- **Layer**: 3 - Optimization
- **Dependencies**: F-16 (Lambda working first)

#### F-31: Dynamic Image Cache Sizing (M-06)
| Score | 2 | Nice optimization, not blocking |
- **Layer**: 4 - Growth
- **Dependencies**: Working app with images

---

## 4. Prioritized Roadmap -- Sequenced Phases

### Phase R1: Security Foundation (Week 1-2) — **50% COMPLETE** ⚠️

**Goal**: Eliminate critical security debt and establish trust infrastructure.
**Layer**: 1 - Foundation (Trust)
**Status**: In Progress (Sprint 3, Week 1)

| Order | Feature | Score | Blocked By | Status | Sprint 3 |
|-------|---------|-------|------------|--------|----------|
| R1.1 | F-01: Purge private key from git history | 7 | None | ❌ 0% | **A1** (Week 1) |
| R1.2 | F-02: Pre-commit secret detection hooks | 6 | F-01 | ✅ 100% | Done |
| R1.3 | F-16: Lambda deployment pipeline | 8 | None | ⚠️ 70% | **A2** (Week 1) |
| R1.4 | F-19: Security scanning in CI/CD | 6 | F-02 | ❌ 0% | **A3** (Week 1) |

**Exit Criteria** (Original):
- ~~Git history clean of secrets~~ — ❌ NOT DONE (critical blocker)
- ~~Pre-commit hooks installed and tested~~ — ✅ DONE
- ~~HealthCheckFunction deployed to AWS dev environment~~ — ❌ NOT DONE (CDK ready, not deployed)
- ~~Cold start measured and <2s~~ — ❌ NOT MEASURED
- ~~CI/CD pipeline runs security scans on every PR~~ — ❌ NOT DONE

**Exit Criteria Target**: Feb 21, 2026 (Sprint 3, Week 1)
**Estimated Effort**: 5-7 days (solo developer) — **Currently on Day 5**

---

### Phase R2: Authentication and Data Models (Week 3-5) — **50% COMPLETE** ⚠️

**Goal**: Users can register, log in (online and offline), and the data layer is ready for features.
**Layer**: 1 - Foundation (Trust + Data)
**Status**: In Progress (Sprint 3, Week 1-2)

| Order | Feature | Score | Server | Mobile | Infra | Overall | Sprint 3 |
|-------|---------|-------|--------|--------|-------|---------|----------|
| R2.1 | F-07: Core domain models | 14 | N/A | ❌ 0% | ⚠️ 70% | 33% | **B1+B4** (Week 1) |
| R2.2 | F-04: Isar encryption | 10 | N/A | ❌ 0% | N/A | 0% | **B2** (Week 2) |
| R2.3 | F-03: Auth (Cognito+JWT) | 17 | ✅ 100% | ❌ 0% | ✅ 100% | 60% | **B3** (Week 2) |
| R2.4 | F-06: Rate limiting | 8 | ✅ 100% | N/A | ✅ 100% | 100% | Done |
| R2.5 | F-09: Input validation | 10 | ✅ 100% | N/A | N/A | 100% | Done |
| R2.6 | F-05: Cert pinning | 7 | N/A | ❌ 0% | N/A | 0% | **C1** (Week 2, stretch) |

**Exit Criteria Status**:
- ~~All 6 Isar schemas generated and tested~~ — ❌ NOT DONE (Sprint 3 B1)
- ~~DynamoDB tables updated with Deliveries, Payments, SyncMetadata~~ — ❌ NOT DONE (Sprint 3 B4)
- ~~User can register with Ethiopian phone number (+251...)~~ — ⚠️ SERVER YES, MOBILE NO (Sprint 3 B3)
- ~~User can log in online (Cognito) and offline (cached credentials)~~ — ⚠️ SERVER YES, MOBILE NO (Sprint 3 B3)
- ~~JWT validation works in Lambda authorizer~~ — ✅ DONE (169 tests passing)
- ~~All API endpoints require authentication~~ — ✅ DONE (CDK configured)
- ~~Rate limiting active on auth endpoints~~ — ✅ DONE (RateLimitService + API Gateway)
- ~~Input validation on all Lambda handlers~~ — ✅ DONE (RequestValidator)
- ~~Certificate pinning configured in Dio client~~ — ❌ NOT DONE (Sprint 3 C1, stretch goal)

**Exit Criteria Target**: Feb 28, 2026 (Sprint 3 end) — **80%+ completion**
**Estimated Effort**: 15-20 days (solo developer) — **Currently on Day 10** (server complete, mobile starting)

**Note on F-03 Sequencing**: The existing authentication-encryption plan (`specs/decisions/authentication-encryption-plan.md`) is **approved for implementation as designed**. Key evaluation:
- Hybrid Cognito + offline caching: CORRECT for Ethiopian context
- 15-min access / 30-day refresh tokens: CORRECT for intermittent connectivity
- PBKDF2 offline password hashing: CORRECT (AOT-compatible, proven)
- SIM swap protection: IMPORTANT but can be Phase R4 refinement
- Shared device support: IMPORTANT but can ship with password-only switching initially

---

### Phase R3: Sync Engine and First Feature (Week 6-9)
**Goal**: The offline-first promise works. Users can create and browse crop listings offline, and data syncs when online.
**Layer**: 1 - Foundation (Reliability) + Core Transaction Flow

| Order | Feature | Score | Blocked By | Delivers |
|-------|---------|-------|------------|----------|
| R3.1 | F-08: Sync engine foundation (Phase A+B: local queue + executor) | 15 | F-07, F-03 | Offline capability |
| R3.2 | F-10: Crop listing flow (create + browse) | 13 | F-08, F-09 | First user feature |
| R3.3 | F-08: Sync engine (Phase C: server endpoint) | (cont.) | R3.1 | Server sync |
| R3.4 | F-08: Sync engine (Phase D: conflict resolution) | (cont.) | R3.3 | Conflict handling |
| R3.5 | F-17: Least-privilege IAM per Lambda | 6 | F-16 | Security hardening |

**Exit Criteria**:
- Farmer can create a crop listing OFFLINE on a 2GB Android device
- Listing appears in local database immediately
- When connectivity returns, listing syncs to server
- Another user can browse listings (from server)
- Sync conflicts are detected and resolved (server wins by default)
- Idempotency keys prevent duplicate listings on retry
- Each Lambda has minimum-necessary IAM permissions

**Milestone**: This is the **first end-to-end demonstration** of WeredaConnect's value proposition. If this works, the architecture is validated.

**Estimated Effort**: 20-25 days (solo developer)

---

### Phase R4: Complete Transaction Flow (Week 10-14)
**Goal**: The full Crop > Trip > Delivery > Payment flow works end-to-end.
**Layer**: 2 - Stability and Trust + Core Transaction Flow

| Order | Feature | Score | Blocked By | Delivers |
|-------|---------|-------|------------|----------|
| R4.1 | F-11: Trip listing flow | 13 | F-10 | Transport marketplace |
| R4.2 | F-12: Delivery verification (QR + GPS) | 16 | F-10, F-11 | Trust mechanism |
| R4.3 | F-13: Telebirr escrow integration | 19 | F-12 | Revenue |
| R4.4 | F-14: Fraud detection checks | 13 | F-10, F-11, F-12 | Platform safety |
| R4.5 | F-15: Conflict resolution UI | 10 | F-08 | User experience |
| R4.6 | F-18: S3 access logging + versioning | 3 | None | Audit trail |

**Exit Criteria**:
- Trucker can post trip availability
- System suggests matching crop listings for a trip route
- At pickup: QR code generated, GPS captured, photo taken
- At delivery: QR code scanned, GPS captured, delivery confirmed
- Telebirr escrow holds funds on buyer confirmation
- Escrow releases funds on verified delivery
- Fraud detection flags suspicious activity
- Sync conflicts show user-friendly resolution UI
- S3 uploads bucket has access logging and versioning enabled

**Milestone**: This is the **minimum viable product (MVP)**. At this point, WeredaConnect can facilitate a real transaction from crop listing to payment.

**Estimated Effort**: 25-30 days (solo developer)

---

### Phase R5: Production Hardening (Week 15-17)
**Goal**: Prepare for production deployment and real users.
**Layer**: 3 - Optimization

| Order | Feature | Score | Blocked By | Delivers |
|-------|---------|-------|------------|----------|
| R5.1 | F-22: Crash reporting (Sentry) | 6 | Working app | Observability |
| R5.2 | F-21: ProGuard obfuscation | 4 | Working app | Reverse engineering protection |
| R5.3 | F-23: Notification batching | 7 | F-10, F-11, F-12 | User engagement |
| R5.4 | F-28: DynamoDB KMS encryption | 3 | Production | Encryption upgrade |
| R5.5 | F-29: S3 KMS encryption | 3 | Production | Encryption upgrade |
| R5.6 | F-30: VPC evaluation | 3 | F-16 | Network security |

**Exit Criteria**:
- Crash reports flowing to Sentry dashboard
- Release APK obfuscated and <50MB
- Notifications delivered in Amharic/Oromo
- Production encryption upgraded to KMS
- VPC cold start impact measured and documented

**Estimated Effort**: 15-20 days (solo developer)

---

### Phase R6: Growth and Expansion (Week 18+)
**Goal**: Expand features for larger user base and cooperative partnerships.
**Layer**: 4 - Growth

| Order | Feature | Score | Blocked By | Delivers |
|-------|---------|-------|------------|----------|
| R6.1 | F-20: Voice-to-text crop listing | 12 | F-10 | Low-literacy access |
| R6.2 | F-27: Insurance trigger (AICE) | 13 | F-12, F-13 | Risk coverage |
| R6.3 | F-24: Rating and review system | 7 | F-13 | Trust signals |
| R6.4 | F-25: Cooperative dashboards | 8 | F-10, F-13 | B2B channel |
| R6.5 | F-26: Analytics and insights | 5 | Production traffic | Business intelligence |
| R6.6 | F-31: Dynamic image cache sizing | 2 | Working app | Memory optimization |

**Note**: Layer 4 CANNOT begin until Layer 1 is stable in production simulation (per build order philosophy).

**Estimated Effort**: 30+ days (solo developer, ongoing)

---

## 5. Top 7 Next Priority Features (Immediate Focus)

These are the **next 7 features to implement**, in exact order:

### Priority 1: F-01 -- Purge Private Key from Git History
- **Why Now**: CRITICAL security vulnerability. Blocks all other work.
- **Risk If Delayed**: Anyone cloning repo has the private key.
- **Dependencies**: None.
- **Layer**: 1 - Foundation.
- **Score**: R=1, RR=5, UA=1, C=5, TC=5 = **7**
- **Next Task**: Run BFG Repo-Cleaner. Force-push cleaned history. Rotate keys.
- **Agent**: @Security

### Priority 2: F-02 -- Pre-commit Secret Detection Hooks
- **Why Now**: Prevents recurrence of F-01 as auth secrets are introduced.
- **Risk If Delayed**: Future secret leaks as development accelerates.
- **Dependencies**: F-01.
- **Layer**: 1 - Foundation.
- **Score**: R=1, RR=5, UA=1, C=4, TC=5 = **6**
- **Next Task**: Install gitleaks. Configure `.pre-commit-config.yaml`. Test with known secrets.
- **Agent**: @Security

### Priority 3: F-16 -- Lambda Deployment Pipeline
- **Why Now**: No backend exists without this. All server features are blocked.
- **Risk If Delayed**: Server development is completely blocked.
- **Dependencies**: None.
- **Layer**: 1 - Foundation.
- **Score**: R=3, RR=3, UA=3, C=2, TC=3 = **8**
- **Next Task**: Fix .NET 10 AOT publish. Uncomment Lambda in CDK. Deploy HealthCheck. Measure cold start.
- **Agent**: @Infra, @Server

### Priority 4: F-07 -- Core Domain Models (Isar + DynamoDB)
- **Why Now**: Every feature depends on data models. This is the foundation for everything.
- **Risk If Delayed**: All feature development blocked.
- **Dependencies**: None.
- **Layer**: 1 - Foundation.
- **Score**: R=4, RR=4, UA=5, C=3, TC=2 = **14**
- **Next Task**: Design and implement 6 Isar schemas + DynamoDB models + table GSIs.
- **Agent**: @Mobile, @Server, @Infra

### Priority 5: F-03 -- API Authentication (Cognito + JWT + Offline)
- **Why Now**: Identity is the foundation of trust. Every API call needs auth.
- **Risk If Delayed**: Open API endpoints. No user identity. No trust.
- **Dependencies**: F-16 (Lambda deployed), F-07 (User model).
- **Layer**: 1 - Foundation.
- **Score**: R=4, RR=5, UA=5, C=5, TC=2 = **17**
- **Next Task**: CDK auth stack, server JWT validator, mobile auth service with offline caching.
- **Agent**: @Infra, @Server, @Mobile

### Priority 6: F-04 -- Isar Database Encryption
- **Why Now**: Must be enabled BEFORE user data is written. Retrofitting is painful.
- **Risk If Delayed**: User data in plaintext on shared devices.
- **Dependencies**: F-07 (Isar schemas).
- **Layer**: 1 - Foundation.
- **Score**: R=2, RR=5, UA=3, C=4, TC=4 = **10**
- **Next Task**: Implement encryption key generation with Android Keystore. Enable Isar encryption.
- **Agent**: @Mobile

### Priority 7: F-08 -- Sync Engine Foundation (Phase A: Local Queue)
- **Why Now**: This is the offline-first promise. Start with the local queue before server endpoint.
- **Risk If Delayed**: "Offline-first" is marketing only, not reality.
- **Dependencies**: F-07 (data models).
- **Layer**: 1 - Foundation.
- **Score**: R=4, RR=5, UA=5, C=2, TC=1 = **15**
- **Next Task**: Implement SyncQueue + ConnectivityMonitor in mobile. Persist queue to Isar.
- **Agent**: @Mobile

---

## 6. Contracts Required from Architect

Before Phase R4 can begin, the Architect agent must create:

| Contract | Location | Needed By |
|----------|----------|-----------|
| Escrow Protocol | `specs/contracts/escrow-protocol.md` | Phase R4.3 (F-13) |
| Crop Listing API | `specs/contracts/crop-listing-api.md` | Phase R3.2 (F-10) |
| Trip Listing API | `specs/contracts/trip-listing-api.md` | Phase R4.1 (F-11) |
| Delivery Verification API | `specs/contracts/delivery-verification-api.md` | Phase R4.2 (F-12) |
| Auth API | `specs/contracts/auth-api.md` | Phase R2.3 (F-03) |

The sync protocol contract (`specs/contracts/sync-protocol.md`) already exists and is sufficient for Phase R3.

---

## 7. Risk Register

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| Cognito SMS delivery in Ethiopia (Ethio Telecom) | HIGH | MEDIUM | Test SMS OTP with Ethiopian numbers early. Have fallback (voice OTP). |
| .NET 10 AOT publish fails on Linux ARM64 | HIGH | LOW | Already scaffolded; need to verify dotnet publish works end-to-end. |
| Isar 4.x encryption key management edge cases | MEDIUM | MEDIUM | Test: key rotation, app reinstall, device reset, migration. |
| Telebirr API availability and documentation | HIGH | MEDIUM | Contact Telebirr developer relations early. Build mock API for development. |
| AICE insurance API terms and availability | MEDIUM | HIGH | Defer to Phase R6. Confirm API availability before planning. |
| Solo developer burnout (25+ week roadmap) | HIGH | HIGH | Strict phase gates. Ship MVP by Phase R4. Celebrate milestones. |
| Flutter SDK not installed on dev machine | LOW | KNOWN | Install before Phase R2. Blocks all mobile work. |
| Node.js 18 vs 20 LTS mismatch | LOW | KNOWN | Upgrade to 20.x before Phase R1. Blocks CDK if breaking changes exist. |
| Offline speech recognition in Amharic quality | MEDIUM | HIGH | Defer to Phase R6. Research models early but don't block on it. |

---

## 8. Decision Log

| Decision | Rationale | Date |
|----------|-----------|------|
| Auth-encryption plan APPROVED as designed | Hybrid Cognito + offline caching is the right approach for Ethiopian context. No changes needed. | 2026-02-11 |
| Security remediation woven into roadmap (not separate) | Security is a feature, not a tax. It must be sequenced with business features, not treated as a backlog. | 2026-02-11 |
| Sync engine split into 4 sub-phases | Reduces risk. Local queue (Phase A) delivers value immediately. Server endpoint (Phase C) can be iterated. | 2026-02-11 |
| Voice-to-text deferred to Layer 4 | High adoption impact but high complexity and dependency on core flow. Typed input must work first. | 2026-02-11 |
| MVP defined as Phase R4 exit | Full Crop > Trip > Delivery > Payment flow with escrow. This is the minimum set of features that delivers WeredaConnect's value proposition. | 2026-02-11 |
| SIM swap protection deferred to Phase R4 refinement | Important but not blocking for initial auth. Password + device binding is sufficient for early users. | 2026-02-11 |
| Insurance integration deferred to Phase R6 | AICE API availability is uncertain. Core flow must work without insurance. | 2026-02-11 |

---

## 9. Success Metrics by Phase

### Phase R1 (Security Foundation)
- Zero secrets in git history
- Pre-commit hooks catch test secrets
- Lambda cold start <2s
- CI/CD pipeline green

### Phase R2 (Auth + Data Models)
- User registration success rate >95% (online)
- Offline login success rate >99% (cached users)
- All 6 Isar schemas generate without error
- DynamoDB tables created with correct GSIs

### Phase R3 (Sync + First Feature)
- Crop listing created offline in <1s
- Sync completes within 5s of connectivity restoration
- Zero data loss in 100 offline-online-offline cycles
- Conflict detection rate 100% (no silent overwrites)

### Phase R4 (Complete Flow -- MVP)
- End-to-end transaction: listing > trip > delivery > payment in <10 minutes
- QR delivery verification works offline
- Escrow hold and release are idempotent (no double charges)
- Fraud detection flags >80% of test fraud scenarios
- App size <50MB, loads in <3s on 3G

---

## 10. Timeline Summary

| Phase | Weeks | Calendar | Milestone |
|-------|-------|----------|-----------|
| **R1: Security Foundation** | 1-2 | Feb 2026 | Backend deployable, secrets purged |
| **R2: Auth + Data Models** | 3-5 | Mar 2026 | Users can register and log in |
| **R3: Sync + First Feature** | 6-9 | Mar-Apr 2026 | Offline crop listing works E2E |
| **R4: Complete Flow (MVP)** | 10-14 | Apr-May 2026 | Full transaction flow with payment |
| **R5: Production Hardening** | 15-17 | Jun 2026 | Production-ready |
| **R6: Growth + Expansion** | 18+ | Jul 2026+ | Voice, insurance, dashboards |

**Total to MVP**: ~14 weeks (solo developer)
**Total to Production**: ~17 weeks

---

## 11. Immediate Next Actions (This Week)

1. **@Security**: Execute F-01 (purge private key) and F-02 (pre-commit hooks)
2. **@Infra + @Server**: Begin F-16 (Lambda deployment pipeline)
3. **@Architect**: Begin designing contracts for auth API (`specs/contracts/auth-api.md`) and crop listing API (`specs/contracts/crop-listing-api.md`)
4. **Developer Environment**: Install Flutter SDK, upgrade Node.js to 20.x LTS
5. **Update TASK.md**: Replace current Phase 8 tasks with Phase R1 tasks from this roadmap

---

## 12. Appendix: Complete Feature Priority Ranking

| Rank | ID | Feature | Score | Layer | Phase |
|------|----|---------|-------|-------|-------|
| 1 | F-13 | Telebirr Escrow Integration | 19 | 1-2 | R4 |
| 2 | F-03 | API Authentication (Cognito + JWT) | 17 | 1 | R2 |
| 3 | F-12 | Delivery Verification (QR + GPS) | 16 | 2 | R4 |
| 4 | F-08 | Sync Engine Foundation | 15 | 1-2 | R3 |
| 5 | F-07 | Core Domain Models | 14 | 1 | R2 |
| 6 | F-10 | Crop Listing Flow | 13 | 1 | R3 |
| 7 | F-11 | Trip Listing Flow | 13 | 1 | R4 |
| 8 | F-14 | Fraud Detection Checks | 13 | 2 | R4 |
| 9 | F-27 | Insurance Trigger (AICE) | 13 | 3-4 | R6 |
| 10 | F-20 | Voice-to-Text Crop Listing | 12 | 3 | R6 |
| 11 | F-04 | Isar Database Encryption | 10 | 1 | R2 |
| 12 | F-09 | Input Validation Middleware | 10 | 1 | R2 |
| 13 | F-15 | Conflict Resolution UI | 10 | 2 | R4 |
| 14 | F-06 | API Rate Limiting | 8 | 1 | R2 |
| 15 | F-16 | Lambda Deployment Pipeline | 8 | 1 | R1 |
| 16 | F-25 | Cooperative Dashboards | 8 | 4 | R6 |
| 17 | F-01 | Purge Private Key | 7 | 1 | R1 |
| 18 | F-05 | Certificate Pinning | 7 | 1 | R2 |
| 19 | F-23 | Notification Batching | 7 | 3 | R5 |
| 20 | F-24 | Rating System | 7 | 4 | R6 |
| 21 | F-02 | Pre-commit Secret Detection | 6 | 1 | R1 |
| 22 | F-17 | Least-Privilege IAM | 6 | 2 | R3 |
| 23 | F-19 | Security Scanning CI/CD | 6 | 2 | R1 |
| 24 | F-22 | Crash Reporting | 6 | 3 | R5 |
| 25 | F-26 | Analytics and Insights | 5 | 4 | R6 |
| 26 | F-21 | ProGuard Obfuscation | 4 | 3 | R5 |
| 27 | F-18 | S3 Access Logging + Versioning | 3 | 2 | R4 |
| 28 | F-28 | DynamoDB KMS Encryption | 3 | 3 | R5 |
| 29 | F-29 | S3 KMS Encryption | 3 | 3 | R5 |
| 30 | F-30 | VPC Evaluation | 3 | 3 | R5 |
| 31 | F-31 | Dynamic Image Cache Sizing | 2 | 4 | R6 |

**Note**: Features are sequenced by dependency chain and build order, not strictly by score. F-13 (Telebirr, score 19) ranks highest but appears in Phase R4 because it depends on delivery verification, which depends on trip listing, which depends on crop listing, which depends on sync engine, which depends on data models and auth.

---

**Document Version**: 1.1.0
**Created**: 2026-02-11
**Last Updated**: 2026-02-14 (Sprint 3 kickoff assessment)
**Author**: Product Owner Agent
**Current Sprint**: Sprint 3 (Feb 14-28, 2026)
**Next Review**: Feb 28, 2026 (Sprint 3 retrospective)
**Active Collaboration**: See `SPRINT_PLANNING.md` for detailed sprint breakdown