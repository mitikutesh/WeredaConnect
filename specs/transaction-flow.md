# Transaction Flow: Farmer → Trucker → Buyer

> Complete end-to-end lifecycle of a crop sale, transport, and payment on WeredaConnect.

---

## The Three Parties

| Role | Description |
|------|-------------|
| **Farmer** | Has crops to sell, located in a rural woreda |
| **Trucker** | Owns a truck, moves goods between woredas |
| **Buyer** | Wants to purchase crops, typically in an urban area |

---

## Phase 1: Listings

### Farmer Creates a Crop Listing

1. Opens **Create Crop Listing** screen
2. Selects crop type from icon grid (teff, wheat, barley, maize, chickpea, lentils, sorghum, coffee, sesame)
3. Enters quantity (quintals), price per kg (ETB), woreda location, description
4. Optionally records a **voice note** (60s max, 32kbps M4A — designed for low-literacy users)
5. Taps Save → listing saved **immediately to local Isar DB** (offline-first), syncs when connected

**Listing status:** `draft → active → reserved → sold → expired`

### Trucker Creates a Trip Listing

1. Opens **Post Trip** screen
2. Enters origin woreda, destination woreda, departure date, capacity (quintals), price per quintal (ETB)
3. Saved offline-first to Isar, syncs when connected

**Trip status:** `open → matched → inTransit → delivered` (or `cancelled`)

---

## Phase 2: Discovery & Matching

- **Buyers** browse crop listings filtered by crop type, woreda, and price
- **Farmers** browse trip listings to find trucks heading their direction
- **Truckers** browse crop listings to find cargo for their routes

All browsing queries Isar first (instant, offline), then refreshes from server when online.

### Matching

When a trip is matched to crop listings:

- Trip: `open → matched`
- Crop listing: `available → reserved`
- One trip can carry multiple farmers' crops

The **trucker** creates a `Delivery` record linking the trip, listing, farmer, trucker, and buyer.

**Delivery status:** `pending → pickedUp → inTransit → delivered` (or `disputed` / `cancelled`)

---

## Phase 3: Pickup (Farmer → Trucker)

### Who Does What

| Actor | Action |
|-------|--------|
| **Farmer** | Generates and **shows QR code** on their phone |
| **Trucker** | **Scans** the farmer's QR code with camera |
| **Trucker** | **Takes photos** of crops being loaded (up to 3) |
| **System** | **Auto-captures GPS** at pickup location |

### QR Code Generation (Farmer's Device — Fully Offline)

1. A device-local 32-byte secret key is retrieved from secure storage
2. An HMAC secret is derived: `SHA256(deliveryId:tripId:listingId:timestamp:deviceKey)`
3. A 32-byte cryptographic random nonce is generated
4. Message constructed: `deliveryId|tripId|listingId|farmerId|timestamp|nonce`
5. HMAC-SHA256 computed and QR code rendered (280×280 pixels)
6. Nonce persisted to Isar for server-side cross-validation during sync

**What the farmer sees:** QR code on screen, delivery ID, their name, date, and a note: *"This QR code works offline."*

### QR Scanning (Trucker's Device — Fully Offline)

1. Camera preview with scan frame overlay and flash toggle
2. Trucker points camera at farmer's QR code
3. Device validates structure, checks `deliveryId` matches expected delivery
4. Physical proximity enforced by the optical scan itself (must be there in person)
5. Nonce extracted for server-side cross-validation when both devices sync

### After Scan + Confirm

- GPS auto-captured (validated within 5km of farmer's registered location)
- Trucker optionally takes photos (JPEG → resized 800px → WebP 75% quality, <200KB each)
- Delivery: `pending → pickedUp`
- Trip: `matched → inTransit`

---

## Phase 4: Buyer Pays Into Escrow (Telebirr)

> This is the only step that **requires internet** — but even the payment intent can be queued offline.

### Who Does What

| Actor | Action |
|-------|--------|
| **Buyer** | Initiates escrow payment via Telebirr |

### Payment Details

| Parameter | Value |
|-----------|-------|
| Method | Telebirr (Ethiopian mobile money) |
| Amount range | 100 – 500,000 ETB |
| Commission | 2.5% (minimum 5 ETB floor) |
| KYC threshold | Fayda ID required for both parties if amount > 10,000 ETB |

### Flow

- **If online:** `POST /escrow/initiate` → Telebirr holds funds → escrow status: `escrowed`
- **If offline:** Payment intent saved locally with status `pending_sync`. UI shows *"Payment queued. Will process when you have internet."* The intent is sent automatically when connectivity returns.

### Escrow State Machine

```
initiated ──→ escrowed ──→ released ──→ completed
    │              │
    │              ├──→ disputed ──→ released (farmer wins)
    │              │            └──→ refunded (buyer wins)
    │              │
    │              └──→ refunded (7-day timeout, no delivery)
    │
    └──→ cancelled (buyer cancels before hold)
```

**What the buyer sees:** Escrow status timeline (stepper widget) with color-coded badges — green for paid/released, yellow for pending, red for disputed.

---

## Phase 5: Delivery (Trucker → Buyer)

### Who Does What

| Actor | Action |
|-------|--------|
| **Buyer** | **Scans QR code** at the delivery location |
| **Trucker or Buyer** | **Takes photos** of delivered crops (up to 3, condition evidence) |
| **System** | **Auto-captures GPS** at dropoff location |

### Flow

Same QR scan flow as pickup, but with `VerificationType.delivery`:

1. Buyer scans QR code displayed on trucker's (or farmer's) device
2. GPS validated against buyer's location or trip destination woreda (5km radius)
3. Photos taken to document crop condition at delivery

### After Scan + Confirm

- Delivery: `inTransit → delivered`
- Trip: `inTransit → delivered`
- **48-hour dispute window** begins
- `confirmedAt` timestamp recorded

---

## Phase 6: Settlement

### Happy Path — No Dispute

If 48 hours pass with no dispute raised:

1. Server-side cron Lambda (runs hourly) detects the expired dispute window
2. Calls `POST /escrow/release` with `trigger=delivery_confirmed`
3. Telebirr releases `farmerPayout` (amount minus 2.5% commission) to farmer's Telebirr account
4. Escrow: `escrowed → released → completed`

### Dispute Path — Within 48 Hours

Either buyer or farmer can raise a dispute:

1. Party selects a reason: `damaged_goods`, `wrong_quantity`, `wrong_crop`, `not_delivered`, `quality_mismatch`, or `other`
2. Adds description (up to 1000 chars, supports Amharic/Oromo/English)
3. Attaches photo evidence (up to 5 references)
4. `POST /escrow/dispute` → escrow: `escrowed → disputed`

**Resolution (by admin):**

| Outcome | Escrow Transition | Who Gets Paid |
|---------|-------------------|---------------|
| Farmer wins | `disputed → released → completed` | Farmer receives payout |
| Buyer wins | `disputed → refunded` | Buyer gets full refund (no commission) |

### Timeout — No Delivery Within 7 Days

1. Server-side cron detects escrow held for 7 days with no delivery completion
2. Auto-refunds full amount to buyer (no commission deducted)
3. Escrow: `escrowed → refunded`

### Reconciliation (Every 6 Hours)

Catches stuck `initiated` escrows where the Telebirr hold result was lost:

- If Telebirr confirms hold → update to `escrowed`
- If Telebirr has no record → mark as `cancelled` with reason `hold_failed`

---

## What Each Party Sees

| Stage | Farmer | Trucker | Buyer |
|-------|--------|---------|-------|
| **Listings** | My crop listings, find trucks | My trip listings, browse crops | Browse crop listings |
| **Pickup** | Shows QR code on screen | Scans QR, takes photos of crops | — |
| **In Transit** | Delivery status tracker | Updates transit status | Escrow status (money held) |
| **Delivery** | — | Hands off goods | Scans QR, takes photos of crops |
| **Payment** | Sees "Pending" → "Released" with payout | Sees trip completed | Sees escrow timeline |
| **Dispute** | Can raise or respond | Photo evidence available | Can raise or respond |

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| QR scan = physical proof of presence | No internet needed; cryptographic nonce prevents replay attacks |
| Photos at both pickup and delivery | Allows comparing crop condition to detect damage or tampering |
| GPS capture at both ends | Validates the trucker actually traveled to the correct locations |
| Escrow holds money until delivery + 48h | Buyer's money is safe until confirmed; farmer is guaranteed payment |
| Everything offline except Telebirr transfer | Even payment intent can be queued offline |
| Idempotency keys on all operations | Prevents double-charges and duplicate operations on flaky 3G |
| Voice notes on listings | Enables low-literacy farmers to describe their crops |

---

## Photo Pipeline

```
Camera (JPEG)
  → Resize (800px max dimension)
  → Convert to WebP (75% quality, <200KB)
  → Store locally: {appDir}/photos/{deliveryId}/{type}-{timestamp}.webp
  → Update Isar delivery record with local path
  → Queue for upload (lower priority than sync)
  → POST /deliveries/{id}/photos (multipart)
  → S3: uploads/photos/{deliveryId}/{type}-{timestamp}.webp
  → Delete local file after successful upload
```

**Limits:** Max 3 photos per verification type (pickup or delivery), max 800px wide, max 200KB each.

---

## Status Lifecycle Summary

```
Crop Listing:  draft → active → reserved → sold → expired
Trip:          open → matched → inTransit → delivered (or cancelled)
Delivery:      pending → pickedUp → inTransit → delivered (or disputed/cancelled)
Escrow:        initiated → escrowed → released → completed
                                    → disputed → released/refunded
                                    → refunded (timeout)
```
