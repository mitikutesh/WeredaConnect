# WeredaConnect Feature Backlog

**Purpose**: Complete feature inventory with prioritization scoring for all 31 features (F-01 through F-31). This file contains the full scoring details, deferred roadmap phases, and the priority ranking table. For the active roadmap and sprint status, see [ROADMAP.md](ROADMAP.md).

**Last Updated**: 2026-02-16

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
- **Next Engineering Task**: Install `gitleaks` as a pre-commit hook. Add `.gitleaks.toml` configuration. Add to CI/CD pipeline as well. Document in `.agents/security-sop.md`.
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
