# WeredaConnect — Product Requirements Document

## 1. Executive Summary

WeredaConnect is a mobile-first P2P marketplace platform connecting Ethiopian farmers, truckers, and buyers in a trusted agricultural marketplace. The platform facilitates crop trading and transport logistics with a built-in escrow payment system that holds buyer funds until delivery is confirmed, then distributes payments to the farmer and trucker automatically.

**Platform Name:** WeredaConnect
**Target Market:** Ethiopia — agricultural regions and urban buyer centers
**Revenue Model:** 5% commission on total transaction value (crop cost + transport cost)
**Primary Language:** Amharic (with English and Afan Oromo support)
**Currency:** Ethiopian Birr (ETB)

**Core Problem:** Ethiopian farmers sell crops to middlemen at 30-50% below market price because they lack direct access to buyers and reliable transport. Truckers run empty return trips. Buyers overpay due to middleman markups. Post-harvest losses reach 20-50% depending on the crop, costing the economy an estimated $9.6B/year.

**Solution:** A three-sided marketplace with built-in escrow, verification checkpoints, and fraud prevention designed specifically for low-trust, low-literacy, low-connectivity environments.

**Unique Differentiators:**
- **Integrated transport matching** — buyer bundles crop purchase + truck booking + escrow into one transaction
- **Offline-first architecture** — works on intermittent 2G/3G connections
- **Voice-to-text in local languages** — Amharic, Afan Oromo, English for low-literacy users
- **CBE bank escrow** — direct bank transfer handles agricultural-scale transactions without mobile money balance limits
- **QR-based delivery verification** — cryptographic proof of handoff (HMAC-SHA256) with no internet required
- **Fayda ID biometric verification** — VeriFayda 2.0 OIDC for NBE-compliant identity assurance
- **Designed for $50 phones** — 2GB RAM, Android 8+, <50MB APK

---

## 2. Competitive Differentiation & Go-to-Market

### 2.1 Why Not Zemen Gebeya?

Ethio Telecom launched Zemen Gebeya in May 2025 as Ethiopia's first national digital marketplace. It is a **general-purpose e-commerce platform** (Ethiopia's Amazon), not an agricultural trade platform.

| Dimension | Zemen Gebeya | WeredaConnect |
|-----------|-------------|---------------|
| **What it is** | General e-commerce (electronics, cosmetics, furniture, clothing) | Purpose-built agricultural bulk trade platform |
| **Transaction type** | Consumer retail: browse, cart, checkout, deliver a package | B2B agricultural: negotiate bulk, escrow deposit, coordinated pickup, long-haul transport, delivery inspection |
| **Typical order** | 500-5,000 Birr | 50,000-500,000+ Birr |
| **Transport model** | Last-mile urban delivery (1 day in Addis) | Long-haul agricultural trucking (100-600km, 1-3 days) |
| **Payment** | Telebirr-only (balance limits constrain large transactions) | Direct CBE bank transfer (handles agricultural-scale amounts) |
| **Coverage** | Addis Ababa only, gradual city expansion | Starts in agricultural production zones (woredas), connects rural to urban |
| **Sides** | Two-sided: merchant to consumer | Three-sided: farmer to trucker to buyer (transport integrated) |
| **Escrow** | Generic funds release on satisfaction | Agriculture-specific: covers crop AND transport; dual confirmation gates (pickup + delivery) |
| **Platform** | Mini app inside Telebirr SuperApp | Standalone Flutter Android app |

### 2.2 Five Structural Advantages

1. **Integrated Transport Matching (The Moat)** — No other platform bundles crop purchase + truck booking + escrow into one transaction.
2. **Built for Bulk Agricultural Transactions** — Direct bank transfer to CBE handles any transaction size without mobile money balance ceilings.
3. **Rural-Origin, Not Urban-Origin** — Designed for low-bandwidth, offline-capable use on affordable Android phones.
4. **Post-Harvest Loss Reduction** — Pre-arranges buyer AND truck before/at harvest. Attracts government support, NGO partnerships, and development funding.
5. **Farmer-First, Not Telecom-Giant** — Community-first alternative built by people who understand the agricultural supply chain.

### 2.3 Direct Competitors

| Competitor | Overlap | Their Weakness |
|-----------|---------|----------------|
| **GebeyaNet** | High — connects farmers, buyers, transporters | Limited traction. No escrow system documented. |
| **ECX Online Trading** | Medium — commodity exchange (Jan 2025) | Institutional/wholesale focus. Not P2P. |
| **OpenAgriNet (ATI)** | Medium — government DPI for markets | Infrastructure layer, not transactional marketplace. Potential partner. |
| **Lersha** | Low — mechanization and extension | Adjacent market (tractor rental). Agent model to emulate. |

### 2.4 Go-To-Market: Single Corridor Pilot

**Do NOT launch nationwide.** Start with ONE crop corridor and prove the escrow flow works end-to-end.

**Recommended pilot: Teff from Arsi/Bale Zone (Oromia) to Addis Ababa**

Why this corridor:
- Teff is Ethiopia's most culturally important crop (staple for injera) with consistently high demand
- Arsi/Bale is a major teff production zone, 150-250km from Addis Ababa
- Distance is manageable for a single-day truck trip, simplifying the MVP

**Pilot targets:**
- 20-30 pre-recruited farmers (via agricultural extension offices in Arsi zone)
- 5-10 truckers (via trucking associations on the Arsi-Addis route)
- 10-15 buyers (grain wholesalers at Ehil Berenda market, injera houses)
- 2-3 local agents per woreda (extension workers, cooperative leaders)
- **Goal: 5-10 completed escrow transactions in Month 1**

**Expansion sequence:**
1. Arsi/Bale to Addis (teff, wheat) — PILOT
2. East Shewa to Addis (vegetables, pulses)
3. Jimma to Addis (coffee, spices)
4. Gondar/Bahir Dar corridor (teff, sesame)
5. Hawassa/Sidama corridor (coffee, enset, fruits)

---

## 3. User Personas & Roles

### 3.1 The Farmer (Gebere / ገበሬ)

| Attribute | Detail |
|-----------|--------|
| **Age** | 30-60 |
| **Literacy** | Low to none; may not read Amharic script |
| **Device** | Basic phone or low-end smartphone (shared with family) |
| **Tech comfort** | Minimal; may hand phone to son/nephew |
| **Languages** | Amharic or Afan Oromo (rarely English) |
| **Primary need** | Get fair price, get paid reliably |
| **Biggest fear** | Being cheated, not getting paid |

**Registration:** Full name, phone number, Fayda ID (12-digit), location (region/zone/woreda), bank account details (bank name, account number).

**Can do:** List crops for sale, confirm pickup, view earnings, rate truckers.

**Key insight:** The farmer's app should do almost nothing — list crop, receive notifications, receive payment. All complex actions happen on buyer/trucker side.

### 3.2 The Trucker (Shofér / ሾፌር)

| Attribute | Detail |
|-----------|--------|
| **Age** | 25-50 |
| **Literacy** | Moderate; can read basic Amharic |
| **Device** | Smartphone (Android), often Tecno or Samsung A-series |
| **Tech comfort** | Moderate; uses Telebirr, WhatsApp/Telegram |
| **Thinks in** | Routes, capacity (kg/quintals), fuel cost |
| **Primary need** | Fill empty truck space, guaranteed payment |
| **Biggest fear** | Crop not ready when arriving, payment disputes |

**Registration:** Full name, phone number, Fayda ID (12-digit), driver's license number, truck plate number, bank account details.

**Can do:** List availability, accept transport assignments, confirm pickup, confirm delivery, view earnings, rate farmers and buyers.

### 3.3 The Buyer (Gebi / ገቢ)

| Attribute | Detail |
|-----------|--------|
| **Age** | 25-55 |
| **Literacy** | High; business-literate |
| **Device** | Smartphone, Android (possibly tablet) |
| **Tech comfort** | High; uses banking apps, WhatsApp Business |
| **Thinks in** | Kilograms, price per kg, quality grades |
| **Primary need** | Reliable supply at fair price, verified quality |
| **Biggest fear** | Receiving wrong quality/quantity, losing escrow money |

**Registration:** Full name, phone number, Fayda ID (12-digit), business license (optional), delivery location, bank account details.

**Can do:** Search crops, search truckers, create orders, make payments, confirm delivery, rate farmers and truckers.

**Buyers are the ONLY users who initiate and fund transactions.**

### 3.4 Common Crops

Teff, wheat, maize, sorghum, barley, coffee, sesame, chickpeas, lentils, haricot beans.

---

## 4. Transaction Flow — The Chain of Trust

### 4.1 Who Starts the Transaction?

**Current MVP:** The **Trucker** creates deliveries by matching their trips to farmer crop listings, then the buyer pays via escrow. This works for the initial marketplace where truckers actively coordinate logistics.

**Target redesign:** The **Buyer** initiates by placing an order and depositing escrow upfront, then farmer and trucker accept. This is the long-term vision described below.

### 4.2 The Seven-Step Flow

```
Step 1: LISTING (Farmer + Trucker)
  |
Step 2: ORDER (Buyer places order + escrow deposit via CBE bank transfer)
  |
Step 3: ACCEPTANCE (Farmer + Trucker confirm)
  |
Step 4: PICKUP VERIFICATION (QR + GPS + Photo)
  |
Step 5: TRANSIT (GPS tracking when available)
  |
Step 6: DELIVERY VERIFICATION (QR + GPS + Photo)
  |
Step 7: ESCROW RELEASE (Split payment to farmer + trucker)
```

> Steps 4-7 are implemented in the current MVP. Steps 2-3 (buyer-initiated ordering with acceptance workflow) are the redesign target.

### Step 1 — Listing

**Farmer lists crop:**
- Crop type, variety, quantity (quintals), price per quintal
- Location (kebele/woreda), harvest date, available date range
- Optional: photo of crop, voice note description

**Trucker lists availability:**
- Vehicle type, load capacity (quintals), available routes (origin-destination)
- Price per quintal or flat rate, availability dates

### Step 2 — Buyer Places Order

- Buyer browses crop listings (filtered by crop type, woreda, price)
- Selects a crop listing, then selects a trucker whose route matches
- App calculates total:

```
Crop: 50 quintals of teff at 6,000 Birr/quintal = 300,000 Birr
Transport: Arsi to Addis Ababa = 15,000 Birr
Subtotal: 315,000 Birr
Platform fee (5%): 15,750 Birr
Total buyer pays: 330,750 Birr
```

- Buyer is shown payment instructions: "Transfer 330,750 Birr to CBE Account No. [PLATFORM_ESCROW_ACCOUNT], Reference: ORD-[ORDER_ID]"
- Buyer transfers from their bank via CBE mobile/internet banking
- Buyer enters CBE Transaction ID in the app (or uploads transfer receipt screenshot)
- Platform verifies payment automatically via CBE verification proxy
- Once verified: order status → FUNDED, SMS sent to farmer and trucker

> **No money, no order. Period.** Pickup NEVER begins until payment is verified and order status is FUNDED.

> **Current MVP:** Buyer creates a delivery record linking trip, crop listing, farmer, and trucker. Buyer then pays into escrow. See `specs/transaction-flow.md` for current implementation details.

### Step 3 — Farmer + Trucker Accept (Target Redesign)

- Farmer and trucker each receive a notification
- Both must accept within 24 hours or order auto-cancels with full refund
- Order status: `ACCEPTED` → `AWAITING PICKUP`

### Step 4 — Pickup Verification

**MVP (current):**
- Farmer shows QR code on their phone
- Trucker scans QR code with camera (offline HMAC-SHA256 verification)
- GPS auto-captured at pickup location
- Trucker optionally takes photo of loaded goods
- Data captured: quantity loaded, timestamp

**Phase 2 — Agent-Assisted Verification:**
- Platform agent or cooperative representative present at pickup
- Agent weighs crop independently
- Numbered seal placed on truck load; seal number entered into app
- Three-point weight comparison: farmer's listing vs. pickup weight vs. delivery weight

### Step 5 — Transit

- Trucker marks delivery as "in transit"
- GPS tracked when connectivity available
- Route deviation alerts for significant detours (Phase 2)
- Offline mode: app syncs when connectivity returns

### Step 6 — Delivery Verification

- Buyer scans QR code displayed on trucker's device (offline HMAC-SHA256 verification)
- GPS auto-captured at delivery location
- Buyer takes photo of delivered goods
- Trucker also confirms delivery
- **48-hour dispute window begins**

**Phase 2 enhancement:**
- Buyer weighs goods and inputs weight into app
- App compares weights at all three points
- If all within tolerance (+/-2%): auto-release escrow
- If discrepancy OR seal broken: hold escrow, open dispute

### Step 7 — Escrow Release / Disbursement

**Happy path (no dispute within 48 hours):**
- Farmer receives: crop cost (e.g., 300,000 Birr)
- Trucker receives: transport cost (e.g., 15,000 Birr)
- Platform keeps: commission (e.g., 15,750 Birr)
- Both parties prompted to rate each other

**Phase 1:** Manual disbursement — admin initiates bank transfers via CBE mobile/internet banking.
**Phase 2:** CBE bulk transfer files for daily batch disbursement.
**Phase 3:** CBE Connect API for fully automated disbursement.

### 4.3 Cancellation Policy

**Current MVP:** Cancellation available when delivery is `pending` or `pickedUp`. Full refund if escrow is in `initiated` state.

**Target — staged penalties:**

| Stage | Buyer Cancels | Refund |
|-------|---------------|--------|
| Before acceptance | Full refund | 100% |
| After acceptance, before pickup | Penalty applies | 95% |
| After trucker dispatched, before pickup | Higher penalty | 90% |
| After pickup verified | **Cannot cancel** | — |

---

## 5. Order State Machine

```
CREATED
  | (buyer creates order, selects farmer + trucker)
  v
PENDING_PAYMENT
  | (buyer shown payment instructions)
  v
PAYMENT_SUBMITTED
  | (buyer enters transaction ID or uploads receipt)
  v
FUNDED <-- (payment verified by CBE verifier or admin)
  |
  v
PICKUP_CONFIRMED <-- (QR scan: farmer shows, trucker scans; both confirm)
  |
  v
IN_TRANSIT
  |
  v
DELIVERED <-- (QR scan: trucker shows, buyer scans; buyer confirms)
  |
  v
DISBURSING <-- (platform initiates payouts)
  |
  v
SETTLED <-- (all payouts completed)

--- Exception states ---

PAYMENT_FAILED <-- (verification failed, buyer can retry)
CANCELLED <-- (order cancelled before pickup, refund buyer)
DISPUTED <-- (any party raises a dispute after pickup)
DISPUTE_RESOLVED <-- (admin resolves dispute)
```

### Timeout Rules

| Transition | Timeout | Action |
|------------|---------|--------|
| PENDING_PAYMENT | 24 hours | Auto-cancel if buyer doesn't pay |
| PAYMENT_SUBMITTED | 6 hours | Notify buyer if auto-verification fails; allow manual admin verification |
| FUNDED | 48 hours | Allow cancellation if pickup hasn't happened |
| DELIVERED | 48 hours | Auto-release escrow if buyer doesn't confirm after trucker confirms (Phase 2+) |

---

## 6. Payment & Escrow System

### 6.1 Escrow Account

The platform operates a **dedicated CBE (Commercial Bank of Ethiopia) business account** used exclusively as the escrow/collection account. This account is SEPARATE from the platform's operating account.

- All buyer payments flow INTO this account
- All disbursements to farmers and truckers flow OUT of this account
- The platform's 5% commission transfers to the operating account after each settlement

### 6.2 Why CBE Bank Transfer (Not Telebirr)

Agricultural transactions are typically 50,000-500,000+ Birr. Telebirr balance limits constrain large transactions (Level 2 P2P daily limit: 75,000 ETB). Direct CBE bank transfer handles any transaction size without balance ceilings.

Farmers and truckers can have accounts at **any Ethiopian bank**. Inter-bank transfers are handled through EthSwitch. The platform pays the transfer fee — negligible compared to the 5% commission.

> **Note:** The codebase includes an existing Telebirr mock integration (from Phase R4). This will be superseded by the CBE bank transfer model described here. Telebirr may be revisited as a supplementary payment channel for smaller transactions in future phases.

### 6.3 Payment Collection (How Buyer Pays In)

- **Primary method:** Direct bank transfer to the platform's CBE escrow account
- Buyer transfers from any bank (CBE, Awash, Dashen, Cooperative Bank of Oromia, etc.)
- The app displays: CBE account number, bank name, and a unique reference code (ORD-[ORDER_ID])

### 6.4 Payment Verification (How Platform Confirms Money Arrived)

**Method 1: CBE Transaction ID Verification (Primary — Automated)**

After transferring, the buyer enters the CBE Transaction ID (e.g., "FT2603010012345") in the app. The backend calls the CBE verification endpoint with the Transaction ID and the platform's escrow account number.

CBE returns: amount, sender name, receiver name, date, status. Backend checks:
- Amount >= order total? Receiver matches escrow account? Status "Completed"?
- If all pass → order marked "FUNDED" (5-30 seconds for CBE-to-CBE)
- If any fail → buyer notified to check transaction ID and retry

**CRITICAL: The CBE verification endpoint is geo-restricted to Ethiopian IPs.** The platform uses an Ethiopian proxy server (see Architecture section below).

**Method 2: Screenshot Upload + OCR (Secondary)**
Buyer uploads screenshot of bank transfer → OCR extracts transaction ID → proceeds with Method 1.

**Method 3: Manual Admin Verification (Fallback)**
For cases where auto-verification fails (e.g., inter-bank transfers with settlement delay). Admin checks CBE banking manually. May take 30 minutes to a few hours.

**Fake receipt protection:** The system does NOT trust the screenshot — it trusts the CBE verification API. A fake screenshot fails verification because the transaction ID won't correspond to a real deposit.

### 6.5 CBE Verification Proxy Architecture

```
+------------------------------------------------------+
|               AWS BAHRAIN (me-south-1)                |
|                                                       |
|  API Gateway -> .NET AOT Lambda -> DynamoDB / S3      |
|                      |                                |
|  Flutter App ------->|  All app logic, orders,        |
|                      |  users, listings, escrow       |
+----------------------|-------------------------------+
                       |
                       |  HTTPS (encrypted, API key auth)
                       v
+------------------------------------------------------+
|          ETHIOPIAN PROXY SERVER                       |
|          (Small VPS in Addis Ababa)                   |
|                                                       |
|  Lightweight Node.js microservice                     |
|  - Receives verification request from AWS Lambda      |
|  - Calls CBE verification endpoint (Ethiopian IP)     |
|  - Returns result to Lambda                           |
|                                                       |
|  Endpoints:                                           |
|  POST /api/cbe/verify-transaction                     |
|  POST /api/cbe/detect-transaction-id (OCR)            |
|                                                       |
|  Security: API key auth, IP whitelist, rate limiting  |
|  Server: Ubuntu VPS, 1-2 CPU, 1GB RAM                |
+------------------------------------------------------+
```

Total added latency: ~100-300ms for the extra hop — negligible for the user.

Fallback if proxy is down: Queue verification request and retry every 5 minutes. If extended outage, fall back to manual admin verification. CloudWatch alarm on proxy health.

### 6.6 Payment Disbursement (How Platform Pays Out)

**Phase 1: Manual Disbursement**
- When order reaches "DELIVERED" → admin dashboard shows payout details:
  - Farmer: name, bank, account number, amount
  - Trucker: name, bank, account number, amount
  - Platform commission: amount → transfer to operating account
- Admin initiates transfers via CBE mobile/internet banking
- Admin marks order as "DISBURSED"

**Transfer fees (from CBE):**
- CBE to CBE: ~5.75-11.5 Birr (flat)
- CBE to other bank (EthSwitch): ~57.5 Birr
- Bulk/batch transfers: ~3.45 Birr per payout

**Phase 2:** WeBirr for collection + CBE bulk transfer files for daily batch disbursement.
**Phase 3:** CBE Connect API for fully automated disbursement — zero human involvement.

### 6.7 Escrow State Machine

```
initiated --> payment_submitted --> funded --> released --> completed
    |                |                 |
    |                |                 +---> disputed --> released (farmer wins)
    |                |                 |             +--> refunded (buyer wins)
    |                |                 |
    |                |                 +--> refunded (7-day timeout)
    |                |
    |                +--> payment_failed (retry available)
    |
    +--> cancelled (buyer cancels before payment)
```

### 6.8 Commission Structure

- **Rate:** 5% of (crop cost + transport cost)
- **Minimum:** 5 ETB floor
- **Split:** Deducted from the overall transaction total the buyer pays
- **Example:** On a 315,000 Birr subtotal, commission = 15,750 Birr

### 6.9 Legal Considerations

The platform is a **technology/marketplace company**, NOT a financial institution. The CBE escrow account is a dedicated collection/trust account.

- Partner with CBE for the escrow collection account
- Do NOT hold funds in a regular operating account
- Consult a fintech lawyer about the collection account structure
- If transaction volume grows significantly, consider applying for a PSO license from NBE
- The National Bank of Ethiopia regulates payment services — avoid positioning as a PSP in Phase 1

> **Action required before launch:** Consult a lawyer specializing in Ethiopian fintech regulation. Ask: "Do I need an NBE license to hold buyer payments in a dedicated CBE collection account for 1-5 days before disbursing to sellers?"

---

## 7. Fayda Identity Verification

### 7.1 Overview

Fayda (ፋይዳ) is Ethiopia's national digital ID system (Proclamation 1284/2023), managed by NIDP. Every resident gets a unique 12-digit Fayda Identification Number (FIN) with biometric enrollment (fingerprints, facial photo, iris scans). Built on the open-source MOSIP platform.

**NBE mandate:** VeriFayda 2.0 is mandatory for digital financial transactions above 10,000 ETB since January 2026. All accounts must be harmonized by April 8, 2026.

### 7.2 Two Verification Tiers

| Tier | Method | Trust Level | Transaction Limit | Escrow Access |
|------|--------|-------------|-------------------|---------------|
| **Verified** | Fayda OIDC (biometric authentication) | Full | Unlimited | Full escrow for any amount |
| **Basic** | Manual Fayda ID entry (no biometric proof) | Limited | < 10,000 ETB per trade | No escrow — cash on delivery only |

Since agricultural transactions are almost always above 10,000 ETB, all users who want to use escrow must complete Fayda OIDC biometric verification.

### 7.3 Registration Flow

```
Step 1: Role Selection (Farmer / Trucker / Buyer)
Step 2: Fayda Identity Verification (OIDC biometric OR manual entry)
Step 3: Role-Specific Details
Step 4: Personal Info + Phone OTP Verification
Step 5: Registration Complete
```

### 7.4 OIDC Integration

Uses Fayda's **OpenID Connect via eSignet** protocol — the standard and only sanctioned third-party integration path. The app opens an in-app browser to Fayda eSignet, user authenticates with biometrics, Fayda redirects back with authorization code, server exchanges for ID token with verified claims (sub, name, fayda_id).

**Each Fayda ID can only be linked to ONE account** (prevents duplicate accounts).

> **Full technical specification:** See `specs/features/verifayda-integration.md` for the complete OIDC flow, API endpoints, error codes, codebase audit, and implementation plan.

### 7.5 Rural Fayda Enrollment Gap (Critical Risk)

Rural farmers are the least likely to have completed biometric enrollment. Mitigation:
1. Pre-launch survey of Fayda enrollment rates in pilot corridor
2. Partner with Fayda registration centers for temporary enrollment events
3. Work with local Development Agents (DAs) to promote enrollment
4. Agent-assisted OIDC — agents physically guide farmers through the flow
5. Track "% of registered farmers who are Fayda Verified" as a KPI

### 7.6 Data Protection

- Do NOT store biometric data — Fayda handles all biometrics
- Store only Fayda ID number and verification status
- Disclosing resident data without consent is punishable by up to 8 years (Proclamation 1284/2023)
- Register as a Relying Party with NIDP to access the OIDC API

---

## 8. Fraud Prevention System

### 8.1 Farmer-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Inflating quantity | Official weight at pickup/delivery determines payment (Phase 2); photo evidence |
| Quality misrepresentation | Photo at listing, photo at delivery; dispute resolution |
| Selling same crop to multiple buyers | Listing quantity decreases when order is FUNDED (not when created) |

### 8.2 Trucker-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Skimming from load | Numbered seals (Phase 2); weight comparison at 3 points |
| Swapping quality mid-route | Photo comparison at pickup vs. delivery |
| Claiming fake breakdown for extra fees | GPS tracking; no extra fees after acceptance |
| Disappearing with goods | GPS tracking; ID/plate verification; escrow protects buyer |

### 8.3 Buyer-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Claiming short delivery | Trucker also confirms delivery; photo of goods |
| Fake quality complaints | Photo evidence; reputation scoring |
| Price manipulation via fake accounts | One account per Fayda ID; biometric verification |
| Falsely claiming non-delivery | GPS logs, delivery photos, trucker's confirmation. If proven: funds released. |

### 8.4 Collusion Prevention

| Collusion Type | Prevention |
|---|---|
| Buyer + Trucker (claim short delivery) | Three-point weight verification (Phase 2); seal system |
| Farmer + Trucker (fake loading) | Agent verification at pickup (Phase 2); buyer weighs at delivery |
| Bypass platform after connecting | Escrow protection value; loyalty incentives; future credit access |

### 8.5 The Phone Vulnerability

Farmers may hand their phone to someone else. Mitigations:
1. **Separate device confirmation:** Critical actions require confirmation from TWO different phone numbers
2. **30-minute cancel window:** After confirmation, farmer receives SMS: "If you did not agree, call 8099 within 30 minutes"
3. **Payment account lock:** Money always goes to the farmer's registered bank account. Cannot be redirected without in-person verification
4. **Minimal farmer app:** Does almost nothing — reducing the attack surface

### 8.6 Server-Side Fraud Detection (MVP — Implemented)

5 automated rules: duplicate detection, price anomaly, velocity limits, GPS consistency, risk scoring.

### 8.7 Fake Receipt Protection

The system does NOT trust screenshots — it trusts the CBE verification API. A fake receipt fails because the transaction ID won't correspond to a real deposit in the escrow account. Transaction ID + account number + amount must all match.

---

## 9. Dispute Handling

### 9.1 Dispute Scenarios

| Scenario | Who Raises | Resolution |
|----------|-----------|------------|
| Goods damaged in transit | Buyer | If trucker's fault: refund from trucker's portion. If packaging: farmer liable. |
| Wrong quantity delivered | Buyer | Compare pickup vs delivery confirmation. Partial payment proportional to actual. |
| Wrong crop type/quality | Buyer | Photo evidence compared. If farmer misrepresented: full refund to buyer. |
| Trucker never shows up | Farmer/Buyer | Full refund to buyer. Trucker penalized (rating, potential ban). |
| Buyer falsely claims non-delivery | Trucker | GPS logs + delivery photos + trucker confirmation as evidence. If proven: funds released. |
| Buyer doesn't confirm delivery (48h) | Trucker | Auto-confirmation triggered if trucker has confirmed with evidence (Phase 2+). |
| Farmer doesn't have goods ready | Trucker/Buyer | Order cancelled. Full refund. Farmer penalized. |

### 9.2 Dispute Process

1. Any party opens dispute from app with: reason, description, photos
2. Order status → DISPUTED (all fund movement frozen)
3. Platform admin reviews evidence from all parties
4. Admin decides: full release, partial release, or full refund
5. Funds distributed according to decision
6. Order status → DISPUTE_RESOLVED

---

## 10. Revenue Model & Key Business Rules

### 10.1 Revenue Streams

| Revenue Stream | Rate | Description |
|---|---|---|
| Transaction fee | 5% of (crop cost + transport cost) | Deducted from total; min 5 ETB |
| Premium listings | Fixed fee | Farmer/trucker can boost visibility |
| Data/analytics | Subscription | Market price data for institutions |
| Micro-credit interest | TBD | Interest on farmer advances (Phase 3) |
| Insurance commissions | TBD | Transit insurance (Phase 4) |

### 10.2 Key Business Rules

1. **Only buyers can create and fund orders.** Farmers and truckers cannot initiate transactions.
2. **Pickup NEVER begins until payment is verified** and order status is FUNDED.
3. **Both parties must confirm pickup** (farmer AND trucker) before status moves to PICKED_UP.
4. **Buyer must confirm delivery** for funds to be released. Auto-release after 48 hours if trucker confirms (Phase 2+).
5. **Platform commission is always 5%** of (crop cost + transport cost).
6. **Disputes freeze all fund movement.** No disbursement while dispute is open.
7. **Refunds go back to buyer's original bank account** — never as platform credit.
8. **Users can have accounts at any Ethiopian bank.** Platform handles inter-bank transfers.
9. **Each order has a unique reference code** for bank transfer identification.
10. **Orders expire after 24 hours** if not paid.
11. **Listing quantity decreases when order is FUNDED** (not when created, to prevent locking inventory on unpaid orders).
12. **All parties in escrow above 10,000 ETB must have Fayda Verified status** (OIDC biometric). If any party is Basic tier: ESCROW_007 error.
13. **Basic tier users can only trade up to 10,000 ETB** per transaction, no escrow.
14. **Each Fayda ID can only be linked to ONE account.** Duplicate: FAYDA_004 error.

---

## 11. Tax & Business Structure

### 11.1 Business Registration

- Register as PLC with **Ministry of Trade and Regional Integration**. Cost: 30,000-60,000 ETB.
- Obtain **TIN** (requires Fayda ID since December 2023)
- Open CBE dedicated escrow collection account (separate from operating account)
- Apply for Fayda API access at https://id.gov.et/api

### 11.2 Tax Categories

**Important:** Platform "turnover" = commission revenue only, NOT total transaction volume.

| Category | Threshold | Tax Treatment |
|----------|-----------|---------------|
| Category B | Below 2M ETB annual turnover | Tax based on gross sales (2-9%) |
| Category A | Above 2M ETB annual turnover | Proper books and accounts required |

### 11.3 VAT

- **Threshold:** 2M ETB annual commission revenue
- **Rate:** 15% on service fee
- **Timing:** Likely below threshold in Year 1, crossing into VAT territory in Year 2

### 11.4 Corporate Tax

- **Rate:** 30% on net profit
- **Minimum Alternative Tax:** 2.5% of turnover if declared tax falls below 2.5%

### 11.5 Withholding Tax

- **Rate:** 3% on services above 10,000 ETB
- When paying farmers/truckers from escrow exceeding 10,000 ETB, may be required to withhold 3% and remit to tax authority

---

## 12. Platform Agents (Phase 0 — Pre-Launch)

Ethiopian agritech platforms that succeed in rural areas use **local agents** — human intermediaries who help farmers navigate the app. Lersha uses local agents. OpenAgriNet plans voice/agent interfaces.

### 12.1 Agent Program

- Recruit 2-3 local agents per woreda in the pilot corridor
- Agents are existing community figures: agricultural extension workers (DAs), cooperative leaders, local shopkeepers, tech-savvy youth

### 12.2 Agent Responsibilities

- Help farmers register (complete Fayda OIDC verification, take crop photos, create first listing)
- Assist with order confirmation (pickup confirmation, delivery issues)
- Serve as local trust anchor ("the person I call when I have a problem")

### 12.3 Agent Compensation

- Small per-transaction fee (e.g., 100-200 Birr per completed order they facilitated)
- Paid from platform commission
- Long-term: agents become mini-franchise operators

### 12.4 Phase 2 Enhancement — Agent-Assisted Verification

- Agent present at pickup to verify weights
- Has own agent account (separate from farmer/trucker/buyer)
- Compensated per verified transaction

---

## 13. The Middleman (Dallala) Problem

Existing middlemen will actively resist the platform. Expected actions:
- Spreading disinformation about the app
- Offering temporary price increases to poach farmers
- Intimidating early adopters
- Creating fake accounts to manipulate prices

### Counterstrategy

1. **Make farmer benefit obvious** from first transaction (20-30%+ better price)
2. **Word of mouth** — the strongest marketing channel in rural Ethiopia
3. **Partner with cooperatives and kebele leaders** for legitimacy
4. **Future lock-in:** Micro-credit tied to transaction history

---

## 14. Admin Dashboard

Critical for Phase 1 since disbursement is manual.

### 14.1 Orders Management

- List all orders with status filters
- For each order: full details, payment verification status, pickup/delivery status
- Action buttons: "Manually Verify Payment", "Mark as Disbursed"
- Payout details: farmer bank info + amount, trucker bank info + amount
- Export daily payout list as CSV

### 14.2 Dispute Management

- List open disputes with evidence
- Resolution form: decide payout split, add resolution notes
- Notify all parties of resolution

### 14.3 User Management

- List users by role, verify/suspend users, view transaction history and ratings

### 14.4 Financial Dashboard

- Total escrow balance (should match sum of FUNDED but not yet SETTLED orders)
- Daily/weekly/monthly transaction volume
- Platform revenue (total commissions)
- Pending disbursements
- Reconciliation view: escrow account balance vs. system records

---

## 15. Offline & Low-Connectivity Design

### 15.1 Principles

- App is offline-first; syncs when connectivity returns
- Critical actions have SMS/USSD fallbacks (Phase 2)
- Never block a transaction because of poor connectivity
- All data cached locally; eventual consistency
- Payment intents can be queued offline; actual fund movement requires connectivity

### 15.2 Channel Matrix

| Action | App (Online) | SMS | USSD | Voice Call | Hotline |
|---|---|---|---|---|---|
| List crop | Y | — | — | — | Y (agent) |
| Accept order | Y | Y | Y | Y | Y |
| Confirm pickup | Y | Y | — | Y | Y |
| Confirm delivery | Y | Y | — | Y | Y |
| Cancel (30-min window) | Y | — | — | — | Y |
| Check balance | Y | Y | Y | — | Y |
| Receive payment | Auto | Auto | Auto | — | — |

> SMS/USSD/voice channels are Phase 2. MVP uses the app only.

### 15.3 Sync Architecture

```
UI --> Local Isar DB (immediate write)
         |
    SyncQueue (background)
         |
    [Connectivity detected]
         |
    Lambda API --> DynamoDB / S3
         |
    [Conflict resolution: server-wins for financial data, client merges for listings]
```

### 15.4 Mandatory Design Rules

- **APK size:** <50MB. Farmers download on slow 3G with limited storage.
- **Offline caching:** Crop listings, user profile, order status cached in Isar.
- **Action queuing:** Confirm pickup, confirm delivery queued locally if offline. Show "pending sync" indicators.
- **Minimal data per API call:** Pagination, compress images (<200KB per photo), avoid loading unnecessary data.
- **Graceful degradation:** Every screen must work or clearly explain what's unavailable when offline.
- **Image optimization:** Crop photos compressed client-side before upload. Thumbnails in listings; full images on demand.

---

## 16. Language Support

### 16.1 Priority Languages (MVP)

1. **Amharic** (አማርኛ) — primary, ~32M speakers
2. **Afan Oromo** (Afaan Oromoo) — ~40M speakers
3. **English** — for urban buyers

### 16.2 Phase 2 Languages

4. Tigrinya (ትግርኛ)
5. Somali
6. Sidamo

### 16.3 Farmer Interface Design Principles

- Voice prompts in local language
- Iconographic UI (crop icons, truck icons, money icons)
- Minimal text; heavy use of color coding
- Green = good/confirmed, Red = problem/cancel, Yellow = waiting
- Large touch targets (minimum 48dp, preferably 56dp) for low-literacy users
- **Maximum 3 taps** to complete any critical action
- Amharic by default in agricultural regions (detect locale or ask on first launch)

---

## 17. SMS Notification Templates

All SMS in **Amharic** with key numbers in numerals.

| Trigger | Recipient | Message (English equivalent) |
|---------|-----------|------------------------------|
| Order funded | Farmer | "Order ORD-XXXX confirmed. Prepare [qty] quintals of [crop] for pickup." |
| Order funded | Trucker | "New assignment ORD-XXXX. Pick up [qty] quintals from [farmer] at [location]." |
| Pickup confirmed | Buyer | "Your order ORD-XXXX has been picked up. [qty] quintals loaded." |
| Delivery confirmed | Farmer | "Delivery confirmed for ORD-XXXX. Payment of [amount] Birr will be sent to your account." |
| Payment sent | Farmer/Trucker | "Payment of [amount] Birr for ORD-XXXX sent to your [bank] account ending in XXXX." |
| Payment failed | Buyer | "Payment verification failed for ORD-XXXX. Check your transaction ID and try again." |
| Order cancelled | All | "Order ORD-XXXX has been cancelled. [reason]." |

---

## 18. Key Metrics & Targets

### 18.1 Pilot Targets (Month 1)

| Metric | Target |
|---|---|
| Completed escrow transactions | 5-10 |
| Registered farmers | 20-30 |
| Registered truckers | 5-10 |
| Registered buyers | 10-15 |
| Local agents recruited | 2-3 per woreda |

### 18.2 Phase 1 Targets (6 months)

| Metric | Target |
|---|---|
| Registered farmers | 1,000 |
| Registered truckers | 200 |
| Registered buyers | 100 |
| Completed transactions | 500 |
| Average farmer price improvement | 20%+ over dallala price |
| Dispute rate | < 10% |
| Dispute resolution time | < 48 hours |
| Repeat transaction rate | > 40% |
| % farmers Fayda Verified | > 70% |

---

## 19. Technical Stack

| Component | Technology | Reasoning |
|---|---|---|
| Mobile App | Flutter 3.38.x (Dart 3.10.x) | Android only for launch; smooth on $50 phones; <50MB APK |
| Local Database | Isar 4.x | Offline-first; fast reads; Dart-native |
| State Management | BLoC (`flutter_bloc`) | Predictable state; works with offline-first pattern |
| Backend | .NET 10 (Native AOT, C# 13) | <100ms cold start on Lambda ARM64; no reflection |
| Database | DynamoDB | On-demand billing; free-tier friendly; multi-table design |
| File Storage | AWS S3 | Photo/audio evidence; access logging; versioning |
| Infrastructure | AWS CDK (TypeScript) | IaC; reproducible; ARM64 Lambda; me-south-1 region |
| Payment Verification | CBE verification via Ethiopian proxy | Geo-restricted endpoint requires Ethiopian IP |
| Authentication | AWS Cognito | Phone number + OTP; Fayda ID integration |
| Identity | Fayda eSignet (OIDC) | VeriFayda 2.0 biometric identity verification |
| SMS Gateway | Local Ethiopian SMS aggregator | Phase 1; Ethio Telecom SMS API for Phase 2 |

### 19.1 .NET AOT Constraints (Critical)

- **No reflection-based serialization** — use `System.Text.Json` with source generators (`JsonSerializerContext`)
- **No dynamic assembly loading** — all types statically known at compile time
- **HTTP clients:** `System.Net.Http` (built-in, AOT-safe) only
- **JWT validation:** `System.IdentityModel.Tokens.Jwt 8.x` (AOT-compatible)
- **All DTOs registered** in `AppJsonContext : JsonSerializerContext`
- **No Entity Framework** — use Dapper or AWS SDK for DynamoDB
- **No Newtonsoft.Json** — only `System.Text.Json` with source gen
- **Lambda runtime:** `provided.al2023` with ARM64

See `server/AOT_COMPLIANCE.md` for full banned library list.

---

## 20. Architectural Constraints

1. **Offline-first & Local DB** — UI responsive without network; writes go first to Isar; sync queue handles retries, conflicts, and partial uploads
2. **Low-end device performance** — Flutter UI optimized for $50 Androids at 60fps; audio/video compression mandatory; <100MB memory footprint
3. **Native AOT** — No reflection, no EF Core, no Moq, no Newtonsoft.Json; use System.Text.Json source generators
4. **Serverless scalability** — Lambda cold start <100ms (ARM64 AOT); on-demand billing
5. **Security** — Fayda OIDC biometric verification; HMAC-SHA256 QR verification; escrow prevents double-spending; GPS + photo evidence chain
6. **CBE Geo-Restriction** — CBE verification endpoint requires Ethiopian IP; lightweight Node.js proxy on Ethiopian VPS forwards verification requests from AWS Lambda

---

## 21. Non-Functional Requirements

| Requirement | Target |
|---|---|
| Offline functionality | All core features work without network |
| Load time on 3G | < 3 seconds |
| APK size | < 50MB |
| Memory usage | < 100MB active |
| Lambda cold start | < 100ms (currently 95ms) |
| Sync batch processing | < 5 seconds |
| Mobile UI | >= 60fps on 2GB RAM Android 8 |
| Data per session | < 1MB typical usage |
| Target device | Android 8+ (API 26), 2GB RAM, ARM64 |

---

## 22. Ethiopian Context — Critical Reality Checks

### 22.1 Connectivity

- Rural areas have **unstable 2G/3G and intermittent 4G**
- Power outages are common; devices can die mid-operation
- Mobile data costs ~$0.50-1.00 per GB (expensive relative to income)
- Offline-first design is **non-negotiable**

### 22.2 Devices

- Common devices: Tecno Spark, Infinix Hot, Samsung Galaxy A-series ($40-$60)
- RAM: 2-4GB typical; storage 16-32GB (often nearly full)
- Android version: 8.0-12.0 (API 26-31)
- Background tasks can be killed; sync must be resumable

### 22.3 Language and Localization

- Supported languages (launch): **Amharic**, **Afan Oromo**, **English**
- All UI text, audio instructions, and forms must support all three
- Voice-to-Text must work in Amharic and Afan Oromo for low-literacy users
- Ethiopic characters (Ge'ez script) must render without glitches

### 22.4 Trust and Compliance

- Fayda ID is **mandatory for all financial transactions above 10,000 ETB** since January 2026
- CBE bank escrow holds funds; released only on verified delivery
- Identity and transaction verification is critical to prevent fraud
- Data protection under Proclamation 1284/2023 — minimize data collection, no biometric storage

### 22.5 Banking Landscape

- **CBE (Commercial Bank of Ethiopia):** Largest bank, most branches, most rural coverage. Recommend ALL users open CBE accounts for cheapest transfers.
- **EthSwitch:** National payment switch enabling inter-bank transfers. All banks are connected.
- **Telebirr:** Most popular mobile money (52.5M+ users). Consider as future payment channel for smaller transactions.
- **CBE Birr:** CBE's mobile wallet. Free transfers between CBE accounts.

### 22.6 Economic Context

- Rural income: ~$500-1,000/year per capita
- Smartphone cost: $50-150 (significant investment)
- Cash-based economy still dominant; mobile money adoption growing rapidly
- Free app, no paywalls; minimize data costs

---

## 23. Phase Roadmap

### Phase 0: Pre-Launch (Months 1-2)

- Register business with Ministry of Trade, obtain TIN
- Open CBE dedicated escrow collection account
- Apply for Fayda API access (https://id.gov.et/api)
- Deploy CBE verification proxy on Ethiopian VPS
- Recruit pilot corridor participants (farmers, truckers, buyers)
- Recruit 2-3 local agents in Arsi zone
- Survey Fayda enrollment rates among target farmers (go/no-go metric)
- Train agents on app usage, Fayda OIDC flow, listing creation

### Phase 1: MVP & Pilot (Months 2-6)

**Pilot: Teff from Arsi/Bale Zone to Addis Ababa**

- Core user registration with Fayda OIDC + manual fallback
- Crop listings and search, trucker availability and search
- Order creation (three-sided: crop + transport + escrow)
- Payment collection via CBE bank transfer
- Automated payment verification via CBE proxy
- QR-based delivery verification (HMAC-SHA256 offline)
- GPS + photo evidence at pickup and delivery
- Manual disbursement by admin
- SMS notifications for critical events
- Fraud detection (5-rule engine)
- Dispute management (admin-mediated)
- Basic admin dashboard
- Rating system
- Agent-assisted onboarding

### Phase 2: Growth & Expansion (Months 6-18)

- Expand to East Shewa, Jimma corridors
- Scale agent network to 3-5 agents per active woreda
- WeBirr for collection + CBE bulk transfers for batch disbursement
- GPS tracking for in-transit orders
- Push notifications + advanced search with map view
- Three-point weight verification with numbered seals
- Price comparison and market price data
- Multi-language expansion (Tigrinya, Sidamo)
- SMS/USSD interface for basic functions
- Auto-confirmation after 48 hours if buyer doesn't respond

### Phase 3: Scale (Months 18+)

- CBE Connect API for fully automated disbursement
- Chapa integration as additional payment option
- Platform-assisted matching (auto-suggest farmer-trucker-buyer combinations)
- Insurance integration for in-transit damage
- Credit/financing features for buyers (partner with MFIs)
- Voice/IVR interface for illiterate farmers
- Cold-chain tracking for perishable corridors
- Analytics dashboard for farmers (market trends, price forecasting)

---

## 24. Security Requirements

- All API endpoints require JWT authentication (except auth routes)
- Phone number verification via OTP for registration
- Payment transaction IDs validated against CBE — never trusted from user input alone
- Screenshots are supplementary — never sole verification method
- Admin panel requires separate admin authentication with 2FA
- All bank account details encrypted at rest
- HTTPS enforced on all endpoints
- Rate limiting on payment submission endpoints (prevent brute-force transaction ID guessing)
- Order reference codes are non-sequential and non-guessable
- HMAC-SHA256 QR codes for offline delivery verification
- Fayda OIDC tokens validated against JWKS endpoint

---

## 25. Testing Scenarios

### 25.1 Transaction & Escrow Tests

1. **Happy path:** Buyer creates order → pays via CBE → verified → QR pickup → QR delivery → disbursement → settled
2. **Payment verification failure:** Buyer enters wrong transaction ID → system rejects → buyer retries with correct ID
3. **Inter-bank transfer:** Buyer pays from Awash Bank to CBE → delayed arrival → manual admin verification
4. **Dispute — damaged goods:** Buyer opens dispute after delivery → admin reviews → partial refund
5. **Order timeout:** Buyer creates order but never pays → auto-cancels after 24 hours
6. **Trucker no-show:** Order funded but trucker doesn't arrive → farmer/buyer cancel → full refund
7. **Double payment prevention:** Buyer submits two transaction IDs → system only accepts first valid one
8. **Concurrent orders:** Two buyers order from same listing with only enough quantity for one
9. **Delivery confirmation timeout:** Trucker confirms but buyer doesn't → auto-release after 48 hours (Phase 2+)

### 25.2 Fayda Identity Tests

10. **OIDC happy path:** Verify with Fayda → biometrics → redirected back → verified → continues registration
11. **OIDC state mismatch:** Callback has different state → CSRF rejection → error shown
12. **OIDC expired code:** Authorization code expires → FAYDA_001 → user retries
13. **Fayda service down:** eSignet unavailable → FAYDA_003 → manual fallback works
14. **Duplicate Fayda ID:** Already linked to another account → FAYDA_004 error
15. **Manual entry fallback:** Fayda ID entered manually → Basic tier → can't use escrow > 10K ETB
16. **Basic tier escrow block:** Basic buyer tries 50K ETB order → ESCROW_007 → prompted to verify
17. **Basic tier upgrade:** Later completes OIDC → upgraded to Verified → full escrow access
18. **Verified buyer with Basic farmer:** ESCROW_007 → "Farmer must verify their Fayda ID"

---

**Document Version:** 3.0.0
**Created:** 2026-02-09
**Last Updated:** 2026-03-02
**Status:** Canonical product definition
