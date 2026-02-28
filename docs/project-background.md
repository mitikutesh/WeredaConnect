# WeredaConnect — Product Requirements Document

## 1. Executive Summary

WeredaConnect is a mobile-first platform connecting Ethiopian farmers, truckers, and buyers in a trusted agricultural marketplace. The platform holds funds in escrow until all parties fulfill their obligations, eliminating the need for intermediaries (dallala) and ensuring fair pricing for farmers.

**Core Problem:** Ethiopian farmers sell crops to middlemen at 30-50% below market price because they lack direct access to buyers and reliable transport. Truckers run empty return trips. Buyers overpay due to middleman markups. Post-harvest losses reach 20-50% depending on the crop, costing the economy an estimated $9.6B/year.

**Solution:** A three-sided marketplace with built-in escrow, verification checkpoints, and fraud prevention designed specifically for low-trust, low-literacy, low-connectivity environments.

**Unique Differentiators:**
- **Offline-first architecture** — works on intermittent 2G/3G connections
- **Voice-to-text in local languages** — Amharic, Afan Oromo, English for low-literacy users
- **Built-in Telebirr escrow** — no money, no order; full payment protection
- **QR-based delivery verification** — cryptographic proof of handoff with no internet required
- **Designed for $50 phones** — 2GB RAM, Android 8+, <50MB APK

---

## 2. User Personas

### 2.1 The Farmer (Gebere / ገበሬ)

| Attribute | Detail |
|-----------|--------|
| **Age** | 30-60 |
| **Literacy** | Low to none; may not read Amharic script |
| **Device** | Basic phone or low-end smartphone (shared with family) |
| **Tech comfort** | Minimal; may hand phone to son/nephew |
| **Languages** | Amharic or Afan Oromo (rarely English) |
| **Primary need** | Get fair price, get paid reliably |
| **Biggest fear** | Being cheated, not getting paid |

**Key insight:** The farmer's app should do almost nothing — list crop, receive notifications, receive payment. All complex actions happen on buyer/trucker side.

### 2.2 The Trucker (Shofér / ሾፌር)

| Attribute | Detail |
|-----------|--------|
| **Age** | 25-50 |
| **Literacy** | Moderate; can read basic Amharic |
| **Device** | Smartphone (Android), often Tecno or Samsung A-series |
| **Tech comfort** | Moderate; uses Telebirr, WhatsApp/Telegram |
| **Thinks in** | Routes, capacity (kg/quintals), fuel cost |
| **Primary need** | Fill empty truck space, guaranteed payment |
| **Biggest fear** | Crop not ready when arriving, payment disputes |

### 2.3 The Buyer (Gebi / ገቢ)

| Attribute | Detail |
|-----------|--------|
| **Age** | 25-55 |
| **Literacy** | High; business-literate |
| **Device** | Smartphone, Android (possibly tablet) |
| **Tech comfort** | High; uses banking apps, WhatsApp Business |
| **Thinks in** | Kilograms, price per kg, quality grades |
| **Primary need** | Reliable supply at fair price, verified quality |
| **Biggest fear** | Receiving wrong quality/quantity, losing escrow money |

---

## 3. Transaction Flow — The Chain of Trust

### 3.1 Who Starts the Transaction?

**The Buyer initiates.** The buyer has the money, the demand, and the sophistication to navigate a multi-step process. The farmer's role is simplified to listing and confirming.

### 3.2 The Seven-Step Flow

```
Step 1: LISTING (Farmer + Trucker)
  |
Step 2: ORDER (Buyer places order + escrow deposit)
  |
Step 3: ACCEPTANCE (Farmer + Trucker confirm)
  |
Step 4: PICKUP VERIFICATION (QR + GPS + Photo)
  |
Step 5: TRANSIT (GPS tracking when available)
  |
Step 6: DELIVERY VERIFICATION (QR + GPS + Photo)
  |
Step 7: ESCROW RELEASE (Split payment)
```

### Step 1 — Listing

**Farmer lists crop:**
- Crop type (teff, wheat, maize, sorghum, chickpea, lentils, coffee, sesame, barley)
- Quantity in kg (or quintals — the app converts internally)
- Price per quintal (or accepts market price)
- Location (kebele/woreda)
- Available date range
- Optional: photo of crop, voice note description

**Trucker lists availability:**
- Current location and destination(s)
- Available space (in quintals)
- Truck type and plate number
- Available date range
- Price per quintal

### Step 2 — Buyer Places Order

- Buyer browses crop listings (filtered by crop type, woreda, price)
- Selects a crop listing
- Selects a trucker whose route matches
- App calculates total: (crop cost) + (transport cost) + (platform fee)
- **Buyer deposits FULL amount into escrow via Telebirr immediately**
- Order status: `PENDING ACCEPTANCE`

> **No money, no order. Period.** If the buyer can't afford the full amount, they shouldn't be placing the order. This protects the farmer and trucker from wasted effort.

### Step 3 — Farmer + Trucker Accept

- Farmer and trucker each receive a notification
- Both must accept within 24 hours or order auto-cancels with full escrow refund
- Order status: `ACCEPTED` → `AWAITING PICKUP`

### Step 4 — Pickup Verification

**MVP (current):**
- Farmer shows QR code on their phone
- Trucker scans QR code with camera (offline HMAC-SHA256 verification)
- GPS auto-captured at pickup location
- Trucker optionally takes photo of loaded goods
- Delivery status: `pending` → `pickedUp`

**Phase 2 enhancement — Agent-Assisted Verification:**
- Platform agent or cooperative representative present at pickup
- Agent weighs crop independently using cooperative/market scales
- Agent enters verified weight into app via their account
- Numbered seal placed on truck load; seal number entered into app
- Three-point weight comparison: farmer's listing vs. pickup weight vs. delivery weight

### Step 5 — Transit

- Trucker marks delivery as "in transit"
- GPS tracked when connectivity available
- Route deviation alerts for significant detours (Phase 2)
- Offline mode: app syncs when connectivity returns

### Step 6 — Delivery Verification

- Buyer scans QR code displayed on trucker's device (offline verification)
- GPS auto-captured at delivery location
- Buyer takes photo of delivered goods
- Delivery status: `inTransit` → `delivered`
- **48-hour dispute window begins**

**Phase 2 enhancement:**
- Buyer weighs goods and inputs weight into app
- App compares weights at all three points (farmer listing vs. pickup vs. delivery)
- If all within tolerance (±2%): auto-release escrow
- If discrepancy OR seal broken: hold escrow, open dispute

### Step 7 — Escrow Release

**Happy path (no dispute within 48 hours):**
- Farmer paid for confirmed weight, minus platform commission
- Trucker paid transport fee, minus platform commission
- Platform fee retained (2-5%, configurable)
- Payments disbursed via Telebirr
- Both parties prompted to rate each other

**Dispute path:**
- Either party raises dispute with reason and photo evidence
- Escrow frozen until resolution
- Admin reviews evidence and resolves

### 3.3 Cancellation Policy

| Stage | Buyer Cancels | Refund |
|-------|---------------|--------|
| Before acceptance | Full refund | 100% |
| After acceptance, before pickup | Penalty applies | 95% |
| After trucker dispatched, before pickup | Higher penalty | 90% |
| After pickup verified | **Cannot cancel** | — |

### 3.4 Edge Cases

**What if farmer and trucker never accept?**
Auto-cancel + auto-refund after 24 hours. Buyer notified: "No farmer/trucker available. Full refund processed."

**What if buyer doesn't have enough in Telebirr?**
The Telebirr API rejects the payment. App shows: "Insufficient balance." No order is created. No notifications sent to farmer or trucker.

**What if no delivery within 7 days?**
Auto-refund full amount to buyer. Escrow: `escrowed` → `refunded`.

---

## 4. Fraud Prevention System

### 4.1 Farmer-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Inflating quantity | Official weight at pickup/delivery determines payment (Phase 2); photo evidence |
| Quality misrepresentation | Photo at listing, photo at delivery; dispute resolution |
| Selling same crop to multiple buyers | Listing locked once order is accepted |

### 4.2 Trucker-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Skimming from load | Numbered seals (Phase 2); weight comparison at 3 points |
| Swapping quality mid-route | Photo comparison at pickup vs. delivery |
| Claiming fake breakdown for extra fees | GPS tracking; no extra fees after acceptance |
| Disappearing with goods | GPS tracking; ID/plate verification; escrow protects buyer |

### 4.3 Buyer-Side Fraud Prevention

| Fraud Type | Prevention |
|---|---|
| Claiming short delivery | Trucker also confirms delivery; photo of goods |
| Fake quality complaints | Photo evidence; reputation scoring |
| Price manipulation via fake accounts | One account per phone number; Fayda ID verification |

### 4.4 Collusion Prevention

| Collusion Type | Prevention |
|---|---|
| Buyer + Trucker (claim short delivery) | Three-point weight verification (Phase 2); seal system |
| Farmer + Trucker (fake loading) | Agent verification at pickup (Phase 2); buyer weighs at delivery |
| Bypass platform after connecting | Escrow protection value; loyalty incentives; future credit access |

### 4.5 The Phone Vulnerability

Farmers may hand their phone to someone else (trucker, middleman, family member). Mitigations:

1. **Separate device confirmation:** Critical actions require confirmation from TWO different phone numbers (farmer confirms from their phone, trucker from theirs)
2. **30-minute cancel window:** After any confirmation, the farmer receives SMS: "If you did not agree, call 8099 within 30 minutes to cancel"
3. **Payment account lock:** Money always goes to the farmer's registered Telebirr number. Cannot be redirected without in-person verification
4. **Minimal farmer app:** The farmer's app does almost nothing — reducing the attack surface

### 4.6 Server-Side Fraud Detection (MVP — Implemented)

The platform runs 5 automated fraud detection rules:
1. **Duplicate detection** — flags repeated identical transactions
2. **Price anomaly** — flags prices significantly above/below market averages
3. **Velocity limits** — flags unusually high transaction frequency
4. **GPS consistency** — validates locations match expected routes
5. **Risk scoring** — composite score triggers manual review above threshold

---

## 5. Payment Architecture

### 5.1 Primary Integration

**Telebirr API (H5 Web Payment)** — Ethiopia's dominant mobile money platform with 51.5M+ subscribers.

**Key consideration:** Operate as a MERCHANT, not P2P, to access higher transaction limits. A single crop transaction can easily exceed the 75,000 ETB P2P daily limit.

### 5.2 Telebirr Limits (as of 2025)

| Level | Daily Balance | Daily Aggregate | P2P Daily | Merchant QR Daily |
|---|---|---|---|---|
| Level 1 | 5,000 ETB | — | — | — |
| Level 2 | 150,000 ETB | 300,000 ETB | 75,000 ETB | 250,000 ETB |
| Level 3+ | Higher | Higher | Higher | Higher |

### 5.3 Escrow Design (Recommended: Option C — Platform Merchant Account)

```
Buyer's Telebirr --> Platform Merchant Telebirr Account (escrow hold)
                         |
                    [Delivery confirmed + 48h dispute window passes]
                         |
              +----------+----------+
              |          |          |
         Farmer's    Trucker's   Stays in
         Telebirr    Telebirr    platform account
         (crop $)    (transport)  (commission)
```

**How it works:**
1. Receive the buyer's payment into merchant account
2. Hold it (sits in merchant balance for 1-5 days)
3. Transfer to farmer and trucker when delivery is confirmed and dispute window passes

**Cost:** ~15 ETB per transaction (5 ETB in + 5 ETB to farmer + 5 ETB to trucker).

### 5.4 The Full Escrow Timeline

```
STEP 1: Buyer browses listings — no money involved

STEP 2: Buyer taps "Place Order"
        Full amount locked: Crop cost + Transport + Platform fee
        Deducted from buyer's Telebirr immediately
        Order status: PENDING ACCEPTANCE

STEP 3: Farmer + Trucker accept
        Money stays in escrow

STEP 4: Pickup verified (QR + GPS + photo)
        Money stays in escrow
        Buyer CANNOT cancel after this point

STEP 5: In transit — money stays in escrow

STEP 6: Delivery confirmed by buyer (QR + GPS + photo)
        48-hour dispute window begins

STEP 7: Escrow releases
        Farmer receives: crop payment minus commission
        Trucker receives: transport fee minus commission
        Platform keeps: 2-5% commission
```

### 5.5 Escrow State Machine (Implemented)

```
initiated --> escrowed --> released --> completed
    |              |
    |              +---> disputed --> released (farmer wins)
    |              |             +--> refunded (buyer wins)
    |              |
    |              +--> refunded (7-day timeout, no delivery)
    |
    +--> cancelled (buyer cancels before hold)
```

### 5.6 Farmer Payment Accessibility

Many farmers don't have bank accounts. Payment options:
1. **Telebirr via USSD (*127#)** — works on any phone, no smartphone needed
2. **Agent cash-out** — farmer walks to nearest Telebirr agent for cash
3. **Cooperative account** — payment to farmer's cooperative, farmer collects cash

### 5.7 Commission Structure (MVP)

- **Rate:** 2.5% of transaction total (minimum 5 ETB floor)
- **Split:** Deducted from the overall transaction
- **Fayda ID:** Required for transactions above 10,000 ETB

### 5.8 Legal Considerations

When holding buyer money in a merchant account for 1-5 days before releasing, the platform is effectively operating an escrow service. In Ethiopia, this may require:
- A payment system operator license from NBE (National Bank of Ethiopia)
- Operating under a licensed partner's umbrella
- Structuring it so Telebirr technically holds the funds (platform controls the release trigger)

> **Action required before launch:** Consult a lawyer specializing in Ethiopian fintech regulation. Ask specifically: "Do I need an NBE license to hold buyer payments in my Telebirr merchant account for 1-5 days before disbursing to sellers?"

---

## 6. Tax & Business Structure

### 6.1 Business Registration

Register as a PLC (Private Limited Company) from day one. Cost: 30,000-60,000 ETB. Provides legal protection, credibility with partners (Telebirr, banks), and a clean structure.

### 6.2 Tax Categories

**Important:** Platform "turnover" = commission revenue only, NOT total transaction volume.

| Category | Threshold | Tax Treatment |
|----------|-----------|---------------|
| Category B | Below 2M ETB annual turnover | Tax based on gross sales (2-9%) |
| Category A | Above 2M ETB annual turnover | Proper books and accounts required |

### 6.3 VAT

- **Threshold:** 2M ETB annual commission revenue triggers mandatory VAT registration
- **Rate:** 15% on service fee
- **Timing:** Likely below threshold in Year 1, crossing into VAT territory in Year 2

### 6.4 Corporate Tax

- **Rate:** 30% on net profit (revenue minus expenses)
- **Minimum Alternative Tax:** 2.5% of turnover applies if declared tax falls below 2.5%

### 6.5 Withholding Tax

- **Rate:** 3% on services above 10,000 ETB
- When paying farmers/truckers from escrow exceeding 10,000 ETB, the platform may be required to withhold 3% and remit to tax authority

### 6.6 Tax-Adjusted Revenue Per Transaction (Worst Case, Year 2+)

```
Transaction value:                    14,250 ETB
4% commission:                           428 ETB

Deductions:
  VAT (15% on commission):               -64 ETB
  Telebirr fee:                           -5 ETB
  SMS/voice costs:                        -5 ETB
  ─────────────────────────────────────────────
  Gross after VAT + costs:               354 ETB

At year end on profits:
  Corporate tax (30% on net profit):    ~106 ETB
  ─────────────────────────────────────────────
  Net after all taxes:                 ~248 ETB per transaction
```

---

## 7. Offline & Low-Connectivity Design

### 7.1 Principles

- App is offline-first; syncs when connectivity returns
- Critical actions have SMS/USSD fallbacks
- Never block a transaction because of poor connectivity
- All data cached locally; eventual consistency
- Even payment intents can be queued offline

### 7.2 Channel Matrix

| Action | App (Online) | SMS | USSD (*127#) | Voice Call | Hotline |
|---|---|---|---|---|---|
| List crop | Y | — | — | — | Y (agent) |
| Accept order | Y | Y | Y | Y | Y |
| Confirm pickup | Y | Y | — | Y | Y |
| Confirm delivery | Y | Y | — | Y | Y |
| Cancel (30-min window) | Y | — | — | — | Y |
| Check balance | Y | Y | Y | — | Y |
| Receive payment | Auto | Auto | Auto | — | — |

> SMS/USSD/voice channels are Phase 2 features. MVP uses the app only.

### 7.3 Sync Architecture

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

---

## 8. Language Support

### 8.1 Priority Languages (MVP)

1. **Amharic** (አማርኛ) — primary, ~32M speakers
2. **Afan Oromo** (Afaan Oromoo) — ~40M speakers
3. **English** — for urban buyers

### 8.2 Phase 2 Languages

4. Tigrinya (ትግርኛ)
5. Somali
6. Sidamo

### 8.3 Farmer Interface Design Principles

- Voice prompts in local language
- Iconographic UI (crop icons, truck icons, money icons)
- Minimal text; heavy use of color coding
- Green = good/confirmed, Red = problem/cancel, Yellow = waiting
- Large touch targets for low-literacy users on small screens

---

## 9. Platform Agents (Phase 2)

### 9.1 Role

- Verify pickup weights at loading points
- Help farmers list crops and onboard to the platform
- Handle disputes on the ground
- Build trust in new areas

### 9.2 Agent Requirements

- Registered with kebele ID + Fayda ID biometric verification
- Trained by platform team
- Has own agent account (separate from farmer/trucker/buyer)
- Compensated per verified transaction (small fee from platform cut)

### 9.3 Agent Network Strategy

- Start at major agricultural cooperative hubs
- Partner with existing cooperative staff
- Expand along major trucking routes
- Target: one agent per major market/collection point

---

## 10. The Middleman (Dallala) Problem

Existing middlemen will actively resist the platform. Expected actions:
- Spreading disinformation about the app
- Offering temporary price increases to poach farmers
- Intimidating early adopters
- Creating fake accounts to manipulate prices

### Counterstrategy

1. **Make farmer benefit obvious** from first transaction (20-30%+ better price)
2. **Word of mouth** — the strongest marketing channel in rural Ethiopia
3. **Partner with cooperatives and kebele leaders** for legitimacy
4. **Future lock-in:** Micro-credit tied to transaction history (farmers who build history on WeredaConnect can access loans, worth far more than saving 5% on one sale)

---

## 11. Revenue Model

| Revenue Stream | Rate | Description |
|---|---|---|
| Transaction fee | 2-5% per transaction | Configurable; split between platform commission |
| Premium listings | Fixed fee | Farmer/trucker can boost listing visibility |
| Data/analytics | Subscription | Market price data for institutions |
| Micro-credit interest | TBD | Interest on farmer advances (Phase 3) |
| Insurance commissions | TBD | Transit insurance (Phase 4) |

---

## 12. Key Metrics

| Metric | Target (6 months) |
|---|---|
| Registered farmers | 1,000 |
| Registered truckers | 200 |
| Registered buyers | 100 |
| Completed transactions | 500 |
| Average farmer price improvement | 20%+ over dallala price |
| Dispute rate | < 10% |
| Dispute resolution time | < 48 hours |
| Repeat transaction rate | > 40% |

---

## 13. MVP Feature Roadmap

### Phase 1: MVP (Months 1-3) — Current Implementation

- Buyer-initiated orders with escrow
- Farmer listing (crop type + quantity + price + location + voice notes)
- Trucker listing (route + capacity + price)
- Telebirr integration for escrow (initiate, hold, release, refund)
- QR-based delivery verification (HMAC-SHA256 offline)
- GPS + photo evidence at pickup and delivery
- Fraud detection (5-rule engine)
- Sync conflict resolution UI
- Phone number verification
- Amharic + Afan Oromo + English
- Role-based navigation (Farmer, Trucker, Buyer)

### Phase 2: Trust Layer (Months 4-6)

- Numbered seal system for truck loads
- Three-point weight verification (listing vs. pickup vs. delivery)
- Platform agents at major cooperative hubs
- Rating/reputation system
- Route-based trucker matching (auto-suggest)
- SMS/USSD/voice confirmation channels for farmers
- Push notifications

### Phase 3: Scale (Months 7-12)

- GPS tracking in transit with route deviation alerts
- Micro-credit/advance payments tied to transaction history
- ECX quality grading integration
- Cooperative bulk listing
- Multi-language expansion (Tigrinya, Somali)
- Analytics dashboard for all users

### Phase 4: Ecosystem (Year 2)

- Insurance for crop transit via AICE APIs
- Weather/market price alerts
- Input marketplace (seeds, fertilizer)
- Export buyer integration
- Warehouse receipt system

---

## 14. Ethiopian Context — Critical Reality Checks

### 14.1 Connectivity

- Rural areas have **unstable 2G/3G and intermittent 4G**
- Power outages are common; devices can die mid-operation
- Mobile data costs ~$0.50-1.00 per GB (expensive relative to income)
- Offline-first design is **non-negotiable**

### 14.2 Devices

- Common devices: Tecno, Infinix, Samsung A-series ($40-$60)
- RAM: 2-4GB typical; storage 16-32GB (often nearly full)
- Android version: 8.0-12.0 (API 26-31)
- Background tasks can be killed; sync must be resumable

### 14.3 Language and Localization

- Supported languages (launch): **Amharic**, **Afan Oromo**, **English**
- All UI text, audio instructions, and forms must support all three
- Voice-to-Text must work in Amharic and Afan Oromo for low-literacy users
- Ethiopic characters (Ge'ez script) must render without glitches

### 14.4 Trust and Compliance

- Fayda ID is **mandatory for all financial transactions** since Jan 2026
- Telebirr acts as the **escrow holder**; funds released only on verified delivery
- Identity and transaction verification is critical to prevent fraud

### 14.5 Economic Context

- Rural income: ~$500-1,000/year per capita
- Smartphone cost: $50-150 (significant investment)
- Cash-based economy still dominant; Telebirr adoption growing rapidly
- Free app, no paywalls; minimize data costs

---

## 15. Technical Stack

| Component | Technology | Reasoning |
|---|---|---|
| Mobile App | Flutter 3.38.x | Cross-platform; smooth on $50 phones; <50MB APK |
| Local Database | Isar | Offline-first; fast reads; Dart-native |
| State Management | BLoC | Predictable state; works with offline-first pattern |
| Backend | .NET 10 (Native AOT) | <100ms cold start on Lambda ARM64; no reflection |
| Database | DynamoDB | On-demand billing; free-tier friendly; multi-table design |
| File Storage | AWS S3 | Photo/audio evidence; access logging; versioning |
| Infrastructure | AWS CDK (TypeScript) | IaC; reproducible; ARM64 Lambda; me-south-1 region |
| Payment | Telebirr API | Dominant mobile money in Ethiopia; 51.5M subscribers |
| Authentication | AWS Cognito | Phone number + OTP; Fayda ID integration |
| SMS Gateway | Africa's Talking / Twilio | USSD + SMS support for Ethiopia (Phase 2) |

---

## 16. Architectural Constraints

1. **Offline-first & Local DB** — UI responsive without network; writes go first to Isar; sync queue handles retries, conflicts, and partial uploads
2. **Low-end device performance** — Flutter UI optimized for $50 Androids at 60fps; audio/video compression mandatory; <100MB memory footprint
3. **Native AOT** — No reflection, no EF Core, no Moq, no Newtonsoft.Json; use System.Text.Json source generators. See `server/AOT_COMPLIANCE.md`
4. **Serverless scalability** — Lambda cold start <100ms (ARM64 AOT); on-demand billing
5. **Security** — Fayda ID verification; HMAC-SHA256 QR verification; escrow prevents double-spending; GPS + photo evidence chain

---

## 17. Non-Functional Requirements

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

---

**Document Version:** 2.0.0
**Created:** 2026-02-09
**Last Updated:** 2026-02-28
**Status:** Canonical product definition
