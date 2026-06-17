# WeredaConnect Feature Backlog

**Purpose**: Complete feature inventory with prioritization scoring for all 31 features (F-01 through F-31). This file contains the full scoring details, deferred roadmap phases, and the priority ranking table. For the active roadmap and sprint status, see [ROADMAP.md](ROADMAP.md).

**Last Updated**: 2026-06-18 (Section 9 — audit-remediation bugs F-32/F-33/F-34; re-verification flagged items F-35/F-36/F-37; F-38 driving-license KYC upload follow-up)

---

## Scoring Model

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

## 1. Security Remediation Features (Trust Layer)

### F-01: Purge Private Key from Git History (S-01)
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

### F-02: Pre-commit Secret Detection Hooks (X-02)
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
- **Next Engineering Task**: Install `gitleaks` as a pre-commit hook. Add `.gitleaks.toml` configuration. Add to CI/CD pipeline as well. Document in `.ai/workflows.md`.
- **Responsible Agent**: @Security

### F-03: API Authentication -- Cognito User Pool + JWT (I-01)
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

### F-04: Isar Database Encryption (M-01)
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

### F-05: Certificate Pinning (M-03)
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

### F-06: API Rate Limiting (I-02)
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

---

## 2. Data Model and Offline Foundation (Trust + Reliability Layers)

### F-07: Core Domain Models -- Isar Schemas + DynamoDB Entity Definitions
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

### F-08: Sync Engine Foundation
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

### F-09: Input Validation Middleware (S-04)
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

---

## 3. Core Transaction Flow (Layer 1 completion + Layer 3)

### F-10: Crop Listing Flow (Create + Browse)
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

### F-11: Trip Listing Flow (Create + Browse + Match)
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

### F-12: Delivery Verification (QR Handshake + GPS)
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

### F-13: Telebirr Escrow Integration (Hold + Release)
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

---

## 4. Stability and Trust Features (Layer 2)

### F-14: Fraud Detection Checks
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

### F-15: Conflict Resolution UI (Sync Conflicts)
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

---

## 5. Infrastructure Hardening (Layer 1-2, woven throughout)

### F-16: Lambda Deployment Pipeline
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

### F-17: Least-Privilege IAM per Lambda (I-03)
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

### F-18: S3 Access Logging + Versioning (I-06, I-07)
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

### F-19: Security Scanning in CI/CD (X-03)
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

---

## 6. Optimization Features (Layer 3)

### F-20: Voice-to-Text Crop Listing (Amharic + Afan Oromo)
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

### F-21: ProGuard Obfuscation + Release Build Hardening (M-04)
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

### F-22: Crash Reporting (M-05)
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

### F-23: Notification Batching
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

---

## 7. Growth Features (Layer 4)

### F-24: Rating and Review System
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

### F-25: Cooperative Dashboards
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

### F-26: Analytics and Insights
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

### F-27: Insurance Trigger Integration (AICE)
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

---

## 8. Hardening Features (LOW priority, pre-production)

### F-28: DynamoDB KMS Encryption (I-04)
| Score | 3 | Low urgency -- S3-managed encryption sufficient for MVP |
- **Layer**: 3 - Optimization
- **Dependencies**: Production deployment

### F-29: S3 KMS Encryption (I-05)
| Score | 3 | Low urgency -- S3-managed encryption sufficient for MVP |
- **Layer**: 3 - Optimization
- **Dependencies**: Production deployment

### F-30: VPC Deployment Evaluation (I-08)
| Score | 3 | Must test cold start impact; defer unless compliance requires |
- **Layer**: 3 - Optimization
- **Dependencies**: F-16 (Lambda working first)

### F-31: Dynamic Image Cache Sizing (M-06)
| Score | 2 | Nice optimization, not blocking |
- **Layer**: 4 - Growth
- **Dependencies**: Working app with images

---

## 9. Audit Remediation — 2026-06-17 Adversarial Audit (Confirmed Bugs)

Three confirmed defects from the 2026-06-17 repository-wide adversarial audit that each require a **cross-file / cross-module** change (not a one-spot patch). All three survived adversarial verification against the live code and were re-confirmed during PO scoping. **PO priority order: F-34 (#1) → F-33 (#2) → F-32 (#3)** — F-34 is ranked above its raw score because it is the only LIVE breach of the CRITICAL role-visibility invariant (automatic-FAIL domain). Source: PO investigator workflow `wf_061429c4-fa8`.

### F-32: Registration Drops Trucker License + Buyer Type/Company Name ✅ DONE
- **Status**: ✅ **IMPLEMENTED & validated 2026-06-18** (commit `cb1a24e`). **Scope decision resolved → Option A**: `buyerType`/`companyName` are now persisted server-side (mobile UI → BLoC → AuthService → request body → `RegisterRequest`/validator/`UserDto`/`UserRepository` → DynamoDB, echoed in the response); the trucker driving-license is kept **LOCAL-ONLY** in Isar (captured + cached on-device via `cacheCredentials`/`saveUserToIsar`, never transmitted) until an image-upload pipeline exists. Role-conditional validation added (buyerType ∈ {private,company}; companyName required iff company; both rejected for non-buyers). Contracts (`auth-api.md`, `user-entity.md`) updated. Tests: 6 validator + 2 repo round-trip (server) + 1 BLoC forwarding (mobile). Server build + **727 tests**; `flutter analyze` clean, **856 tests**, web build OK. **Follow-up:** Scope B (driving-license S3 upload + `drivingLicenseRef`) is tracked as **F-38** below.
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Company-buyer identity (type + company name) underpins invoicing/KYC for higher-value B2B purchases — enables, doesn't directly generate |
| Risk Reduction | 4 | Capturing buyer/company identity + (eventually) trucker license verification strengthens fraud/trust around escrow-backed deliveries; silent data loss today is a real integrity gap |
| User Adoption | 3 | Removes a confusing silent-data-loss experience for 2 of 3 roles; unblocks trucker-verification / company-buyer features |
| Compliance | 4 | Buyer-type/company identity + verified licenses align with KYC and Ethiopia's 2026 digital-ID/financial-integrity direction |
| Complexity | 3 | Multi-module (mobile UI+BLoC+service+cache+web pair, server DTO+validation+handler+repo, 2 contracts, 5 test files) but all additive/nullable; mobile schemas already carry the columns |
| **Priority Score** | **11** | |

- **PO Gates**: Fraud (YES), Offline (NO — registration already requires connectivity), Ops complexity (NO — adds fields/validation), Revenue (YES), Policy (YES) = 3/5 PASS
- **Layer**: 1 - Foundation (onboarding integrity)
- **Severity / Audit**: HIGH — Data Loss / Cross-Module Contract Gap. Confirmed at `mobile/lib/features/auth/screens/registration/registration_flow.dart:283-302` (`_handleRegister`).
- **Why Now**: Truckers and company buyers fill in role-specific data (driving license, buyer type, company name) that is **silently dropped at submit** — no error shown. `_handleRegister` builds `AuthRegisterRequested` with only phone/password/name/role/woreda/faydaToken/language; the event class already declares the three extra fields, but the BLoC, auth-service, and the entire server have no destination for them.
- **Risk If Delayed**: Trucker driving-license verification (a fraud control for who moves goods + handles escrow) is never captured server-side; company-buyer KYC identity is lost. Blocks any future trucker-verification / company-buyer feature and weakens marketplace/escrow fraud posture. Users believe profiles are complete.
- **Dependencies**: **PO decision gate** — driving-license is a LOCAL camera file path (meaningless server-side). Choose scope (A) persist `buyerType`/`companyName` server-side now + keep license LOCAL-only in Isar (recommended, shippable), or (B) also build S3 image-upload + `drivingLicenseRef` (track separately, +2-3d). Architect must sign off the additive `auth-api.md` + `user-entity.md` contract changes before server code lands.
- **Next Engineering Task** (scope A):
  1. Contracts first: add role-conditional `buyerType` (enum private|company) + `companyName` to `specs/contracts/auth-api.md` register request/response and `specs/contracts/user-entity.md`; note license as local-only.
  2. Server: add nullable `BuyerType`/`CompanyName` to `RegisterRequest` (`AuthDtos.cs`) + `UserDto.cs`; role-conditional rules in `ValidateRequest`/`RequestValidator.cs`; map onto `userDto` in `RegisterFunction.cs`; null-guarded `ToItem`/`FromItem` in `UserRepository.cs`. (No new `AppJsonContext` registration — scalar props on already-registered records.)
  3. Mobile: forward `event.drivingLicensePath/buyerType/companyName` from `_onAuthRegisterRequested`; add optional params to `auth_service.register()` and include non-null `buyerType`/`companyName` in the HTTP body (NEVER send the device license path); persist via `cacheCredentials` in `offline_auth_cache.dart` **and** its `_web.dart` pair; in `_handleRegister` pass the three values guarded by `_selectedRole`.
  4. Tests: `RegisterFunctionTests.cs`, `UserRepositoryTests.cs`, `auth_bloc_test.dart`, NEW `registration_flow_test.dart`, NEW `auth_service_test.dart`. Verify all 3 roles + `flutter build web` + `dotnet build/test`.
- **Responsible Agent**: @Mobile, @Server, @Reviewer (role-visibility + cross-platform are CRITICAL gates)
- **Target Sprint**: Sprint 2 (after the scope-decision gate)

### F-38: Trucker Driving-License S3 Upload (KYC) — Scope B follow-up to F-32
*(Follow-up **feature**, not an audit defect. Spawned by the F-32 Option-A decision, which deliberately kept the driving-license local-only because no registration upload pipeline existed.)*

| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Indirect — verified trucker identity underpins trust in who moves goods + handles escrow-backed deliveries; enabling, not directly revenue-generating |
| Risk Reduction | 4 | Captures trucker KYC server-side (currently the license never leaves the device), a real fraud/identity control for the logistics + escrow flow |
| User Adoption | 2 | Trucker-only onboarding step; modest UX surface |
| Compliance | 4 | KYC for a payments/escrow marketplace; aligns with Ethiopia's 2026 digital-ID / financial-integrity direction |
| Complexity | 3 | Has a ready template (the F-12 delivery-photo presigned-S3 pipeline); work is a near-copy plus sensitive-PII handling + DTO/contract wiring |
| **Priority Score** | **9** | |

- **PO Gates**: Fraud (YES), Offline (NO — upload needs connectivity; license is captured offline and uploaded on reconnect), Ops (NO), Revenue (NO), Policy (YES) = 2/5 PASS
- **Layer**: 1-2 - Trust / KYC
- **Type / Severity**: FEATURE (KYC enablement) — MEDIUM. Not a defect; F-32 already prevents data loss locally.
- **Why Now / Why Later**: F-32 (DONE) captures the license to local Isar but never transmits it, so trucker identity can't be verified server-side. This ticket completes the trucker-verification story. It is **not urgent** — the data is safe on-device — so it slots into production hardening alongside real-auth/KYC work.
- **Reuse (the template already exists)**: model on the F-12 delivery-photo pipeline — `DeliveryFunction.cs` `POST /deliveries/{id}/photos` issues a presigned S3 PUT URL via `IAmazonS3.GetPreSignedURL` (key `uploads/photos/{deliveryId}/...`) against the existing `uploadsBucket` (`grantReadWrite`, `UPLOADS_BUCKET` env), and `SyncFunction` validates the key format. No new bucket needed — reuse `uploadsBucket` with a `uploads/kyc/{userId}/...` prefix.
- **Risk If Delayed**: Trucker driving-license verification remains impossible server-side; KYC/fraud posture on the logistics + escrow flow stays weaker.
- **Dependencies**: F-32 (DONE — license captured to Isar). Reuses the existing `uploadsBucket` + presigned-URL pattern. No new infra bucket.
- **Next Engineering Task**:
  1. Server: add a presigned-PUT endpoint for KYC docs (e.g. `POST /uploads/driving-license` returning a presigned URL for key `uploads/kyc/{userId}/driving-license-{timestamp}.{ext}`), modeled on `DeliveryFunction.HandlePhotoUpload`. Add `DrivingLicenseRef` to `RegisterRequest`/`UserDto` (trucker-only validation in `RequestValidator`); persist + marshal in `UserRepository` (mirrors F-32 buyer fields). Register the auth/upload Lambda for `uploadsBucket` presign on the `kyc/` prefix (least-privilege). Add key-format validation (mirror `SyncFunction` photo regex).
  2. Mobile: after capturing the license (`role_details_screen`/`photo_manager`), request a presigned URL and upload to S3 (reuse the delivery-photo upload path), then send `drivingLicenseRef` in registration (or a follow-up profile update). Keep `drivingLicensePath` locally; set `drivingLicenseRef` once uploaded.
  3. Security/compliance: KYC images are sensitive PII — confirm `uploadsBucket` SSE + block-public-access (already set), restrict GET to admin via presigned-GET only, and add access logging/retention notes. Reviewer gate for PII handling.
  4. Contracts: update `auth-api.md` + `user-entity.md` (`drivingLicenseRef` becomes a transmitted field; supersedes the "local-only" note).
  5. Tests: server presigned-URL handler + validation + repo round-trip; mobile upload + ref-wiring; verify all 3 roles (only trucker uploads).
- **Responsible Agent**: @Server, @Mobile, @Infra, @Reviewer (PII handling gate)
- **Target Sprint**: Sprint 3 (production readiness) — pairs with real-auth/KYC hardening (S3-02); not MVP-blocking.

### F-33: Web Escrow "By Delivery" Returns the Wrong Escrow
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 3 | Escrow is the monetized core flow; showing the wrong payment against a delivery erodes trust in money movement but doesn't directly block a sale |
| Risk Reduction | 4 | Removes a financial-data misrepresentation (wrong amount/status/dispute window shown for a delivery) |
| User Adoption | 3 | Web + default app entrypoint affected; correct, cross-platform-consistent payment status supports adoption |
| Compliance | 4 | New endpoint upholds the CRITICAL role-visibility invariant (party-or-admin reads) and aligns with the documented escrow contract |
| Complexity | 4 | GSI + repo query already exist; near-copy of `GetByIdAsync` plus one route + a mobile repo swap, spread across modules |
| **Priority Score** | **10** | |

- **PO Gates**: Fraud (YES), Offline (YES — native Isar lookup preserved), Ops complexity (NO — adds one endpoint), Revenue (YES), Policy (NO) = 3/5 PASS
- **Layer**: 1-2 - Core transaction / trust
- **Severity / Audit**: HIGH — Logic. Confirmed at `mobile/lib/features/escrow_payment/data/repositories/web_escrow_repository.dart:84-98` → `server/src/Functions/EscrowFunction.cs:383-429` (`HandleHistoryAsync`).
- **Why Now**: The web repo calls `GET /escrow/history?deliveryId=<id>&limit=1` and returns `items.first`, but the server's history handler **never reads `deliveryId`** — it returns the user's single most-recent escrow regardless of delivery. Whenever a user holds >1 escrow, the escrow status screen reliably shows an unrelated payment (wrong amount/status/dispute window). The native repo does this correctly via a local Isar `deliveryIdEqualTo` lookup; there is no server-backed equivalent. `GetByDeliveryIdAsync` + the `DeliveryIndex` GSI already exist but are never exposed via a route.
- **Risk If Delayed**: Buyers/farmers see misleading financial state per delivery — may dispute or believe a payment is (in)complete against the wrong delivery; inconsistent vs native app.
- **Dependencies**: None blocking, but a **hard ordering**: server `EscrowService.GetByDeliveryAsync` + `EscrowFunction` route must land, then the infra `api-stack.ts` route deployed, **before** the mobile web-repo rewrite (the endpoint must exist when the web repo calls it). Reuses existing `DeliveryIndex` GSI and `EscrowDto` (already AOT-registered).
- **Next Engineering Task**:
  1. Server: add `EscrowService.GetByDeliveryAsync(deliveryId, userId, role)` mirroring `GetByIdAsync` — 404 `ESCROW_016` if null, enforce party-or-admin (403 `ESCROW_022`), return `EscrowDto`.
  2. Server: add `GET /escrow/by-delivery/{deliveryId}` branch + `HandleGetByDeliveryAsync` in `EscrowFunction.cs` (distinct two-segment path; must not be shadowed by `/escrow/{id}` — add routing test).
  3. Infra: add the authenticated route in `infra/lib/stacks/api-stack.ts` (JWT authorizer, HttpLambdaIntegration). Deploy.
  4. Mobile: rewrite `WebEscrowRepository.getEscrowByDelivery` to call the new endpoint, parse one `EscrowDto`, return null on 404. Keep native Isar lookup as source of truth.
  5. Contract: add `GET /escrow/by-delivery/{deliveryId}` to `specs/contracts/escrow-protocol.md`; note `deliveryId` is NOT a supported `/escrow/history` param.
  6. Tests: `EscrowServiceTests.cs`, `EscrowFunctionTests.cs` (routing + role), NEW `web_escrow_repository_test.dart`, `escrow_bloc_test.dart`. Verify buyer/farmer can read, trucker gets 403.
- **Responsible Agent**: @Server, @Mobile, @Infra, @Reviewer
- **Target Sprint**: Sprint 2

### F-34: Login Defaults Role to Farmer on Cache Miss — Role-Visibility Breach ✅ DONE
- **Status**: ✅ **IMPLEMENTED & validated 2026-06-18** (commit `786ad23`). `AuthResult` now carries the authoritative role/name/woreda; `AuthService.loginOnline` resolves the role before the best-effort cache write and fails closed on an unrecognized role; `loginOffline` carries the cached role; all three `AuthBloc` login handlers use `result.role` and emit `AuthError` when it is null (the `?? UserRole.farmer` default is gone); web `getCachedUser` returns null on an unknown role. Mobile-only; `flutter analyze` clean, **855 tests pass** (incl. 2 new F-34 tests), web build OK.
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Indirect — a Buyer mis-scoped as Farmer can't browse all listings/transact, but it's edge-triggered (cache miss / first login) |
| Risk Reduction | 4 | Fixes a violation of the CRITICAL role-visibility invariant (automatic-FAIL domain); removes a silent data-scope mis-assignment |
| User Adoption | 4 | Fires exactly at fresh-device first login — hits new Truckers/Buyers at onboarding and erodes trust in the dashboard they see |
| Compliance | 4 | Role visibility (Farmer own-crops, Trucker own-trips, Buyer all) is the project's hard compliance gate; restores correct enforcement at the auth layer |
| Complexity | 4 | 4 mobile source files + 2 test files; no server/contract/schema change — authoritative role already ships in the login response |
| **Priority Score** | **10** | |

- **PO Gates**: Fraud (YES), Offline (YES — offline login still derives role from cache), Ops complexity (YES — replaces a silent-failure class with a deterministic error), Revenue (NO), Policy (YES) = 4/5 PASS
- **Layer**: 1 - Foundation (identity integrity)
- **Severity / Audit**: HIGH (elevated from MEDIUM by the role-visibility override). Confirmed at `mobile/lib/features/auth/bloc/auth_bloc.dart:288` (`_onAuthLoginRequested`); same pattern at ~345 (online) and ~402 (offline).
- **Why Now — PRIORITY #1**: On successful login the BLoC emits `AuthAuthenticated(role: user?.role ?? UserRole.farmer)` where `user` comes from the local cache. On a fresh device (or cache miss/corruption / a swallowed Isar write failure), a Trucker or Buyer is **silently rendered as a Farmer** — wrong dashboard, wrong nav, and wrong data scope (a Buyer loses the ability to browse ALL crops/trips and is shown create-crop affordances they must not have). This is a LIVE breach of the automatic-FAIL role-visibility invariant. The server login response already carries the authoritative role (`LoginResponse.User.Role`, required, AOT-registered) and `AuthService.loginOnline` already parses it — but it is dropped between the service and the BLoC. Cheapest high-impact fix of the three; mobile-only, no dependencies.
- **Risk If Delayed**: Every first login by a real Trucker/Buyer on a new device risks silent mis-scoping with no error signal — a recurring role-visibility violation at the exact onboarding moment.
- **Dependencies**: None. Server already returns role; no server/infra/contract work. Can land immediately and independently.
- **Next Engineering Task**:
  1. Extend `AuthResult` (`mobile/lib/core/auth/models/auth_result.dart`) with nullable `UserRole role`, `String? userName`, `String? woreda`; thread through `AuthResult.success(...)`.
  2. `AuthService.loginOnline`: convert the already-parsed `user['role']` to `UserRole` and pass into `AuthResult.success`; on **unknown** role return `AuthResult.failure` (do NOT silently default to farmer); move role parsing OUT of the swallowed cache try/catch. `loginOffline`: populate role/name/woreda from the cached user it already loads.
  3. `AuthBloc` (`_onAuthLoginRequested` ~288, `_onAuthLoginOnlineRequested` ~345, `_onAuthLoginOfflineRequested` ~402): use `result.role`; **remove `?? UserRole.farmer`**. If `isSuccess && role == null` → emit `AuthError` (re-login required), never authenticate with a guessed role. Cache read only for userName/woreda display.
  4. `offline_auth_cache_web.dart` `getCachedUser`: return null on unrecognized role string instead of defaulting to farmer (platform pair; keep Isar/`dart:io`-free).
  5. Tests: `auth_bloc_test.dart` (assert role from result for all 3 roles + cache-null + null-role-fails), NEW `auth_service_test.dart`. Verify all 3 dev test users on a clean install; `flutter analyze`/`test`/`build web`.
- **Responsible Agent**: @Mobile, @Reviewer (role-visibility CRITICAL gate)
- **Target Sprint**: Sprint 1 (ship-ready) — block merge until fixed

---

#### Re-verification pass findings (2026-06-17) — flagged items requiring a PO/architect decision

The 2026-06-17 re-verification surfaced three items that needed a decision rather than a mechanical fix. PO ranking: **F-36 (#1) → F-35 (#2) → F-37 (#3)** — F-36 outranks higher-scored peers because it was a LIVE breach of the CRITICAL role-visibility domain. **F-36 is now ✅ DONE** (implemented 2026-06-18 under Option A); F-35 and F-37 remain open pending their decisions. Source: PO investigator workflow `wf_dcf1339e-bf2`.

### F-36: Restrict GET /crops & GET /trips browse-all to Buyer (+Admin) — close cross-role data leak ✅ DONE
- **Status**: ✅ **IMPLEMENTED & validated 2026-06-18 under Option A** (browse-all = Buyer/Admin; farmers/truckers/other roles/unauthenticated → **403**). Both list + by-id handlers in `CropFunction.cs` and `TripFunction.cs` now use a positive allowlist; the unit tests that encoded the leak were rewritten to assert 403 (server: **716 tests pass**); contract docs reconciled (`data-models.md`, `crop-listing-api.md`, `trip-listing-api.md`, `smoke-tests-aws-dev.md`). PO decision **RESOLVED → Option A, 403 for restricted roles and unauthenticated** (the public/unauthenticated-browse promise is dropped).
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 2 | Indirect — protecting farmer inventory + trucker contact/pricing from cross-role scraping preserves marketplace fairness/trust, doesn't add a revenue stream |
| Risk Reduction | 5 | Maximum — closes a confirmed cross-role data-confidentiality leak in a CRITICAL domain, including unauthenticated full-catalog access |
| User Adoption | 3 | Farmers/truckers gain assurance their commercial data is scoped to buyers; no UI change (no friction, no new feature) |
| Compliance | 5 | Maximum — required to satisfy the CRITICAL CLAUDE.md role table and align code with the smoke-test spec; data-minimization per Ethiopia 2026 policy |
| Complexity | 4 | Low effort — two handlers, four methods, allowlist swap; no new DTOs/AOT/DB/infra; bulk is test rewrites + doc reconciliation |
| **Priority Score** | **11** | |

- **PO Gates**: Fraud (YES), Offline (NO), Ops (NO), Revenue (NO), Policy (YES) = 2/5 PASS *(low gate count, but remediates a CRITICAL-domain confidentiality leak + a code-vs-spec contradiction)*
- **Layer**: 1 - Foundation (role-visibility integrity)
- **Severity / Audit**: HIGH (server-layer leak in a CRITICAL domain). Confirmed at `server/src/Functions/CropFunction.cs` `HandleGetCropsAsync` (86-99) + `HandleGetCropByIdAsync` (187-198); `server/src/Functions/TripFunction.cs` `HandleGetTripsAsync` (87-100) + `HandleGetTripByIdAsync` (255-266).
- **Why Now**: The read endpoints owner-scope exactly ONE role each (`farmer`→own crops, `trucker`→own trips) and let **every other caller fall through to the unfiltered query**. So a **Trucker can enumerate every farmer's crop catalog** (qty/price/location/woreda/name), a **Farmer can enumerate every trucker's trips** (route/capacity/price/phone), and **unauthenticated callers can browse both** — directly violating the role table (only Buyer sees ALL). Passing unit tests (`HandleGetCrops_TruckerRole_CanBrowseAllCrops`, `…_Unauthenticated_…`, `HandleGetTrips_FarmerRole_CanBrowseAllTrips`) currently **encode the bug as correct**.
- **Risk If Delayed**: Ongoing cross-tenant scraping of farmer inventory and trucker contacts/pricing; a constitution-level (automatic-FAIL) role-visibility violation remains shippable.
- **✅ PO DECISION (resolved 2026-06-18)**: **Option A** chosen — browse-all restricted to **Buyer + Admin**; restricted roles AND unauthenticated callers get **403**; the "public/unauthenticated browse" promise is dropped (contract docs updated). Option B (keep open browse) rejected (would require rewriting the CRITICAL role table).
- **Dependencies**: PO policy decision (blocking). Reuses `AdminAuthHelper.IsAdmin` (exists). No infra/DTO/DB change. AWS dev deploy to run the smoke-test verification.
- **Next Engineering Task** (Option A): (1) `HandleGetCropsAsync` — positive allowlist: `farmer`→force self; `buyer`/admin→browse-all; else **403 `CROP_FORBIDDEN`**. (2) Same for `HandleGetCropByIdAsync`, `HandleGetTripsAsync` (→`TRIP_FORBIDDEN`), `HandleGetTripByIdAsync` (preserve `truckerPhone` hiding + status query branch). (3) Rewrite the bug-encoding tests in `CropFunctionTests.cs`/`TripFunctionTests.cs` to assert restricted behavior (never weaken owner-isolation asserts). (4) Update handler XML docs + reconcile `trip-listing-api.md` (14, 113), `crop-listing-api.md`, `data-models.md:77`, `smoke-tests-aws-dev.md` (137, 172). (5) `dotnet build`+`test`; AWS dev smoke pass.
- **Responsible Agent**: @Server (primary), @Reviewer (CRITICAL role-visibility sign-off), @Architect/PO (policy + contract reconciliation)
- **Target Sprint**: **Sprint 1** (ship-ready) — alongside F-34; do not deploy to dev (S1-05) until it lands.

### F-35: Admin audit log always empty — add a "recent audit entries across all escrows" capability ✅ DONE
- **Status**: ✅ **IMPLEMENTED & validated 2026-06-18** (commit `ab9e2a0`). **Decision resolved → bounded Scan** (not the GSI option): the existing `escrowId="all"` sentinel on `GET /admin/escrows/{id}/audit` now routes to `EscrowAuditRepository.GetRecentAcrossEscrowsAsync` — a bounded table Scan (capped at 2000, logged if hit), sorted newest-first, returning the 100 most recent entries across all escrows. **Server-only, read-only**: no change to the escrow write path, no GSI/schema migration, no infra/admin changes; shows all existing history immediately. **Scale path** (when audit volume grows): a constant-partition or date-bucketed GSI + a dedicated `/admin/audit` route. `dotnet build` clean, **719 tests pass** (incl. 3 new `EscrowAuditRepositoryTests`).
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | Admin reporting; no direct revenue path |
| Risk Reduction | 4 | Restores escrow audit-trail visibility for dispute/fraud investigation; currently a blind spot |
| User Adoption | 2 | Admin/compliance-staff only; no farmer/trucker/buyer surface |
| Compliance | 5 | NBE/regulatory audit-trail reporting depends on this; a real compliance gap |
| Complexity | 3 | Cross-module (infra GSI/scan + server repo/handler/DTO/context + admin hook/page) but well-scoped; audit data already written correctly |
| **Priority Score** | **9** | |

- **PO Gates**: Fraud (YES), Offline (NO), Ops (NO), Revenue (NO), Policy (YES) = 2/5 PASS
- **Layer**: 2 - Trust / compliance
- **Severity / Audit**: HIGH (admin-only; no role-visibility surface; data intact). Confirmed at `admin/src/pages/AuditLogPage.tsx:13` (`useEscrowAudit("all")`) → `AdminFunction.cs` `HandleEscrowAuditAsync` → `EscrowAuditRepository.GetAuditTrailAsync` (PK-equality `escrowId="all"`).
- **Why Now**: The page calls `GET /admin/escrows/all/audit`; the server runs a partition-key Query for `escrowId="all"`, but rows use the real `escrowId` as PK — so the result is **always empty in every environment**. There is **no endpoint** that lists audit entries across escrows.
- **Risk If Delayed**: Admins have no working escrow audit trail — dispute investigation, fraud review, and regulatory reporting are blocked.
- **🔶 OPEN PO/ARCHITECT DECISION**: query strategy — **(a) GSI** constant partition `'ALL'` + `timestamp` sort *(recommended at current low traffic; date-bucket partition as the scale path)*, or **(b) bounded Scan** (no schema/write change, but unordered + full-table cost); and **historical backfill** of pre-existing rows lacking the GSI attribute (one-off job vs go-forward-only).
- **Dependencies**: PO/architect decision above. If GSI: infra GSI + `EscrowService.AppendAuditAsync` write-path attribute must deploy **before** the new endpoint returns go-forward data. Pairs with Sprint 2 audit-logging work (S2-03/S2-04). `escrowAuditTable` already `grantReadData` to the admin Lambda (no IAM change).
- **Next Engineering Task**: (1) infra — add `AuditRecentIndex` GSI in `database-stack.ts` (on `.table` directly; the construct doesn't expose GSI props). (2) server — `AppendAuditAsync` writes `auditPartition='ALL'`; new `GetRecentAcrossEscrowsAsync(limit, startKey)` in the repo (append-only, don't touch per-escrow read); new `GET /admin/audit` branch + `HandleListRecentAuditAsync` (reuse `ParseLimit`/`PaginationTokenHelper`); new `AdminAuditListResponse` registered in `AppJsonContext.cs`. (3) infra — add `/admin/audit` to `adminGetPaths`. (4) admin — `useRecentAudit()` hook → `GET /admin/audit`; switch `AuditLogPage`. (5) tests — NEW `AdminAuditTests.cs` (NSubstitute) + `AuditLogPage.test.tsx`.
- **Responsible Agent**: @Server, @Infra, @Mobile (admin React), @Reviewer
- **Target Sprint**: **Sprint 2** (security) — pairs with structured audit logging.

### F-37: E2E webapp authenticated tests cannot inject auth (false-green coverage)
| Dimension | Score | Rationale |
|-----------|-------|-----------|
| Revenue | 1 | Test infrastructure; no revenue path |
| Risk Reduction | 4 | Restores real authenticated-UI + UI-level role-isolation coverage on web; the CRITICAL role rule currently has no working UI guard |
| User Adoption | 1 | No user-facing change |
| Compliance | 2 | Indirectly supports the role-visibility CRITICAL gate + tests-follow-code; itself internal QA |
| Complexity | 4 | 3 e2e files + a small production-gated web bootstrap seam; no server/contract/DB change |
| **Priority Score** | **4** | |

- **PO Gates**: Fraud (NO), Offline (NO), Ops (YES), Revenue (NO), Policy (NO) = 1/5 PASS
- **Layer**: 2 - Trust (test reliability)
- **Severity / Audit**: MEDIUM (test-only; no end-user impact). Confirmed at `e2e/webapp/home.spec.ts` (~29-37) and `e2e/webapp/role-isolation.spec.ts` (UI + Responsive describes).
- **Why Now**: The tests inject tokens into raw `localStorage` keys, but the Flutter web app restores sessions from `current_user_phone` → `cached_tokens_<phone>` via **flutter_secure_storage** (AES-GCM, `FlutterSecureStorage.` prefix) — the namespaces never intersect. The app stays unauthenticated, the "authenticated" assertions sit behind a `!url.includes('/login')` guard inside a warn-and-skip `try/catch`, so the suite reports **green while asserting nothing** — there is currently ZERO real authenticated-UI / UI role-isolation coverage.
- **Risk If Delayed**: A regression in web session restore, role-based routing, or the CRITICAL role-visibility rule would ship undetected through a green gate.
- **🔶 OPEN PO DECISION**: auth-injection strategy — **(C)** a **production-gated, window-exposed test-only seam** (`window.__weredaTestSeedAuth`) writing through the real `OfflineAuthCache` + `current_user_phone` paths *(recommended)*; **(A)** drive the real CanvasKit login UI (faithful but flaky/slow — keep at most one smoke test); **(B)** replicate flutter_secure_storage's AES-GCM localStorage format (infeasible/brittle). PO must accept a **non-production-only auth bypass** (must be a no-op in production builds).
- **Dependencies**: PO decision above. Hard prerequisite for / should land with existing **S3-05** ("Add Flutter web E2E tests"). Most valuable **after F-36** so the restored role-isolation UI coverage guards the corrected behavior. Live dev API + seeded test users + a dev/staging web build that includes the gated seam.
- **Next Engineering Task**: (1) gated seam in `main.dart` web bootstrap (registered only when `!Environment.config.isProduction`), writing via `OfflineAuthCache` + `current_user_phone` then dispatching `AuthCheckRequested`; optional extract to `web_test_auth_seam.dart` (conditional import). (2) `seedAuth(page, role)` + `verifyAuthenticated(page)` in `e2e/helpers/auth.ts`; throw loudly if the seam is absent. (3) Rewrite `home.spec.ts` + `role-isolation.spec.ts` to use `seedAuth`, remove the warn-and-skip + `/login` escape hatch, assert non-`/login` per role. (4) mobile test locking the seam off in production. (5) `flutter build web` + webapp Playwright run.
- **Responsible Agent**: @Mobile (web seam), @Reviewer
- **Target Sprint**: **Sprint 3** (production) — with S3-05, after F-36.

### Tech-Debt Note (NOT a bug): unconditional `dart:io` import in `photo_evidence_widget.dart`
- **Status**: Reclassified / **DISPROVEN** as a defect. The audit verifier claimed the unconditional `import 'dart:io'` at `photo_evidence_widget.dart:1` "breaks the web build." `flutter build web` passes (exit 0) with the file in the tree, so the build-breaking claim is false — same web-tolerance that correctly refuted sibling `role_details_screen.dart`. **No fix required; not scheduled.**
- **Why tracked**: Latent platform-coupling smell — a widget importing `dart:io` directly is fragile if the web target ever hardens (e.g. Wasm/strict-web). The current dart2js web build tolerates unused `dart:io` symbols.
- **Optional action**: guard behind a conditional import / `kIsWeb` branch when/if the web target hardens. Severity LOW, no sprint, no PO ID assigned.

---

## Deferred Roadmap Phases

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

## Complete Feature Priority Ranking

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

### Audit Remediation Bugs (2026-06-17) — separate priority track

Confirmed bugs from the 2026-06-17 adversarial audit (Section 9), tracked apart from the feature ranking. **PO rank reflects the CRITICAL role-visibility override, not raw score.**

| PO Rank | ID | Bug | Score | Severity | Sprint |
|---------|----|----|-------|----------|--------|
| 1 | F-34 ✅ | Login defaults role to Farmer on cache miss — role-visibility breach | 10 | HIGH | Sprint 1 — ✅ DONE |
| 2 | F-33 | Web escrow "by delivery" returns wrong escrow | 10 | HIGH | Sprint 2 |
| 3 | F-32 ✅ | Registration drops trucker license + buyer type/company name | 11 | HIGH | Sprint 2 — ✅ DONE (Option A; license local-only) |

F-34 outranks higher-scored F-32 because it is the only LIVE breach of the automatic-FAIL role-visibility invariant, is mobile-only, and has no dependencies. Hard ordering exists only within F-33 (server + infra deploy must precede the mobile rewrite). F-32 is gated on a PO scope decision (driving-license local-only vs S3 upload). None depend on each other or touch the Isar Hardening (HS-1..HS-7) critical path.

**Re-verification pass (2026-06-17) — flagged items needing a PO/architect decision:**

| PO Rank | ID | Item | Score | Severity | Sprint | Blocking decision |
|---------|----|----|-------|----------|--------|-------------------|
| 1 | F-36 ✅ | Restrict GET /crops & /trips browse-all to Buyer (+Admin) — cross-role data leak | 11 | HIGH | Sprint 1 | ✅ DONE — Option A (restrict, 403) |
| 2 | F-35 ✅ | Admin audit log always empty — add global audit endpoint | 9 | HIGH | Sprint 2 — ✅ DONE | Resolved → bounded Scan (server-only) |
| 3 | F-37 | E2E webapp auth-injection broken (false-green coverage) | 4 | MEDIUM | Sprint 3 | Auth-seam strategy (Option C) |

F-36 leads (role-visibility CRITICAL domain overrides raw score): a Trucker can scrape all crops, a Farmer all trips, and unauthenticated callers can browse both — with passing unit tests that currently encode the leak. F-36 & F-35 are independent and parallelizable once their decisions land; F-37 depends on the auth-seam decision and is most valuable sequenced after F-36.
