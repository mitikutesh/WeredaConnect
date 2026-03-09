# Buyer Experience in WeredaConnect

## Overview

A **Buyer** (typically an urban wholesaler or cooperative) uses WeredaConnect to purchase agricultural goods from rural farmers, with deliveries handled by independent truckers. The buyer's primary workflow centers on the **Deliveries** tab, where they can:

1. **Initiate escrow payments** via Telebirr before goods ship
2. **Track delivery progress** through the 4-stage lifecycle
3. **Confirm delivery** by scanning the trucker's QR code at the drop-off point
4. **Raise disputes** within 48 hours if goods are damaged or incorrect

All operations work **offline-first** — payments queue locally and sync when connectivity returns.

---

## Deliveries Tab

When a buyer logs in, the Deliveries tab auto-selects the **Buyer** role filter and shows only deliveries where they are the buyer participant.

### Filter System

Two rows of filter chips let the buyer narrow their view:

```
 Role:    [ All ] [ Farmer ] [ Trucker ] [*Buyer*]
 Status:  [ All ] [ Pending ] [ Picked Up ] [ In Transit ] [ Delivered ] [ Disputed ]
```

The Buyer chip is pre-selected based on the authenticated user's role.

---

## Delivery Card

Each delivery appears as a card in a scrollable list:

```
┌─────────────────────────────────────────────────┐
│  ┌──────┐                                       │
│  │  📦  │  Delivery #a1b2c3d4              🟠   │
│  │      │  Abebe Bekele → You (Buyer)    Pending │
│  └──────┘  Kebede Transport · Feb 21, 2026      │
│                                                  │
└─────────────────────────────────────────────────┘
```

**Elements:**
- **Status icon** (left): 48x48 colored container with icon
  - 📦 Orange = Pending
  - 🚚 Blue = Picked Up
  - 🛣️ Indigo = In Transit
  - ✅ Green = Delivered
  - ⚠️ Red = Disputed
  - ❌ Grey = Cancelled
- **Delivery ID**: First 8 characters of the UUID
- **Route**: `farmerName → buyerName`
- **Trucker + Date**: Trucker name and creation date
- **Status chip** (right): Colored badge matching the status

Tapping a card opens the **Delivery Detail Screen**.

---

## Delivery Detail Screen

### Status Timeline

A horizontal progress bar shows the 4-stage lifecycle:

```
  (📦)────────(🚚)────────(🛣️)────────(✅)
 Pending    Picked Up   In Transit  Delivered
    ●━━━━━━━━━━●━━━━━━━━━━○           ○
              current
```

Filled circles/lines = reached stages. Current stage is bold. Buyer sees progress in real-time as the trucker advances the delivery.

### Participants Section

```
┌─ Details ──────────────────────────────┐
│  🌾  Farmer    Abebe Bekele            │
│  🚚  Trucker   Kebede Transport        │
│  🏪  Buyer     You (Buyer)             │
└────────────────────────────────────────┘
```

### Pickup Evidence Section

Once the trucker confirms pickup, this section shows:
- **Time**: Pickup timestamp
- **GPS**: Coordinates (lat, lon) or "Not captured"
- **Photo**: Pickup photo evidence (read-only for buyer)

If not yet picked up: "Not yet picked up" in muted text.

### Delivery Evidence Section

Once the buyer confirms delivery, this section shows:
- **Time**: Confirmation timestamp
- **GPS**: Drop-off coordinates
- **Photo**: Drop-off photo evidence

If not yet delivered: "Not yet delivered" in muted text.

---

## Buyer Actions by Status

### 1. Pending — "Initiate Escrow Payment"

When a delivery is created and still pending, the buyer sees:

```
┌─────────────────────────────────────────┐
│  💰  Pay for Delivery           [GREEN] │
│      ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
│                                         │
│  ❌  Cancel Delivery        [RED OUTLINE]│
└─────────────────────────────────────────┘
```

**Pay for Delivery** opens the **Initiate Payment Screen**.

#### Initiate Payment Screen

```
┌─────────────────────────────────────────┐
│               Pay for Delivery          │
├─────────────────────────────────────────┤
│  🛡️  Escrow Payments                   │
│      Your funds are held safely until   │
│      delivery is confirmed.             │
├─────────────────────────────────────────┤
│  Delivery #a1b2c3d4                     │
│  To: Abebe Bekele                       │
├─────────────────────────────────────────┤
│  ┌─────────────────────────────────┐    │
│  │ Total Amount      ETB 50,000.00 │    │
│  │─────────────────────────────────│    │
│  │ Commission (5%)    ETB 2,500.00 │    │
│  │─────────────────────────────────│    │
│  │ Farmer Receives   ETB 47,500.00 │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ⚠️ Fayda ID required for amounts      │
│     above ETB 10,000 (server-enforced)  │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │     ✓  Confirm Payment          │    │
│  └─────────────────────────────────┘    │
│                                         │
│  ℹ️ Funds held in escrow until          │
│     delivery confirmed or dispute       │
│     resolved.                           │
└─────────────────────────────────────────┘
```

**Amount calculation**: `pricePerKg × quantity × 100kg` (looked up from the linked crop listing).

**Commission**: 5% of total, minimum ETB 5.00.

**Fayda ID warning**: Shown for amounts > ETB 10,000 (Ethiopian digital ID requirement).

**Confirmation dialog** asks the buyer to confirm: "Pay ETB 50,000.00 to Abebe Bekele?"

**Offline support**: If connectivity is unavailable, the payment intent is queued locally and synced when the device reconnects. The buyer sees a "Payment queued" confirmation.

After successful payment, the buyer is taken to the **Escrow Status Screen** showing payment progress.

### 2. Picked Up — Read-Only

When the trucker scans the farmer's QR and confirms pickup:

```
┌─────────────────────────────────────────┐
│  Timeline: Pending ━━● Picked Up ━━ ...│
│                                         │
│  Pickup Evidence:                       │
│    Time: Feb 21, 2026 2:30 PM          │
│    GPS:  9.0192, 38.7525               │
│    Photo: [pickup evidence image]       │
│                                         │
│  ❌  Cancel Delivery        [RED OUTLINE]│
└─────────────────────────────────────────┘
```

The buyer can see pickup evidence but has **no action buttons** at this stage (except Cancel). They are waiting for the trucker to mark the delivery as in transit.

### 3. In Transit — "Confirm Delivery" (QR Scan)

This is the buyer's primary action stage. When the trucker arrives at the drop-off location:

```
┌─────────────────────────────────────────┐
│  Timeline: ... ━━● In Transit ━━ ...   │
│                                         │
│  📷  Confirm Delivery            [BLUE] │
│      ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
└─────────────────────────────────────────┘
```

**Confirm Delivery** opens the **QR Scan Screen**.

#### QR Scan Flow

**Phase 1: Scanning**

```
┌─────────────────────────────────────────┐
│  Scan QR (Confirm Delivery)       🔦   │
├─────────────────────────────────────────┤
│                                         │
│         ┌─────────────────┐             │
│         │                 │             │
│         │   📷 Camera     │             │
│         │   Preview       │             │
│         │                 │             │
│         └─────────────────┘             │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ Point camera at trucker's QR    │    │
│  │ code to verify delivery         │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

The buyer points their phone camera at the trucker's QR code. A torch toggle button is available for low-light conditions. The QR token is verified offline using HMAC-SHA256 — **no internet needed**.

**Phase 2: Verified**

```
┌─────────────────────────────────────────┐
│           ✅ QR Verified!               │
│        Delivery #a1b2c3d4               │
│                                         │
│  📍 GPS Location                        │
│     9.0192, 38.7525                     │
│     Accuracy: 12m                       │
│                                         │
│  📷 Photo Evidence                      │
│     [Capture Photo]                     │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │     Confirm Delivery     [GREEN]│    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

After QR verification:
- **GPS** is auto-captured (with fallback warning if unavailable)
- **Photo** capture is optional but recommended for evidence
- **Confirm Delivery** button finalizes the status change to "Delivered"

**Phase 3: Invalid QR**

If the QR code doesn't match the expected delivery:

```
┌─────────────────────────────────────────┐
│           ❌ Verification Failed        │
│                                         │
│     QR code does not match this         │
│     delivery. Please try again.         │
│                                         │
│       [ 🔄 Try Again ]                 │
└─────────────────────────────────────────┘
```

### 4. Delivered — "Raise Dispute"

After confirming delivery, the status moves to Delivered. The buyer has a **48-hour window** to raise a dispute:

```
┌─────────────────────────────────────────┐
│  Timeline: ... ━━━━━━━━● Delivered     │
│                                         │
│  Delivery Evidence:                     │
│    Time: Feb 21, 2026 5:45 PM          │
│    GPS:  9.0350, 38.7480               │
│    Photo: [dropoff evidence image]      │
│                                         │
│  ⚠️  Raise Dispute               [RED] │
│      ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
└─────────────────────────────────────────┘
```

**Raise Dispute** opens the **Dispute Screen** where the buyer enters a reason (1-500 characters). After submission, the delivery status changes to "Disputed" and the escrow funds are frozen until resolution.

The dispute button **disappears after 48 hours** from the confirmed-at timestamp.

---

## Disputed Status

If a dispute is raised (by the buyer or any other participant), the detail screen shows a read-only dispute banner:

```
┌─────────────────────────────────────────┐
│  ⚠️  Disputed                          │
│  "Goods arrived damaged, 30% of teff   │
│   bags had water damage."              │
└─────────────────────────────────────────┘

┌─────────────────────────────────────────┐
│  ℹ️  Dispute Under Review         [RED]│
└─────────────────────────────────────────┘
```

No further buyer actions are available while the dispute is being resolved.

---

## Cancel Delivery

Available when delivery is **Pending** or **Picked Up**. Any participant (farmer, trucker, or buyer) can cancel.

Tapping **Cancel Delivery** shows a confirmation dialog:

```
┌─────────────────────────────────────┐
│  Cancel Delivery?                   │
│                                     │
│  This action cannot be undone.      │
│                                     │
│         [Cancel]     [Confirm]      │
└─────────────────────────────────────┘
```

Cancellation is not available once the delivery is in transit, delivered, or disputed.

---

## Empty State

When the buyer has no deliveries, the screen shows:

```
┌─────────────────────────────────────────┐
│                                         │
│              🚚                         │
│                                         │
│        No deliveries yet                │
│   Deliveries will appear here when      │
│   you purchase from a farmer.           │
│                                         │
│   (Debug mode: [🐛 Seed Test Delivery]) │
└─────────────────────────────────────────┘
```

The debug seed button (only visible in development builds) creates a test delivery pre-advanced to the "In Transit" stage so the buyer can immediately test the QR confirmation flow.

---

## Key Technical Details

| Feature | Detail |
|---------|--------|
| **Offline support** | All operations (payment, confirmation, dispute) work offline via local Isar DB + sync queue |
| **QR verification** | HMAC-SHA256 offline verification — no server roundtrip required |
| **GPS capture** | Auto-captured on QR verification; graceful fallback if unavailable |
| **Photo evidence** | Compressed locally; synced when connectivity available |
| **Escrow commission** | 5% (minimum ETB 5.00), calculated client-side, enforced server-side |
| **Fayda ID** | Required for payments > ETB 10,000 (Ethiopian digital identity) |
| **Dispute window** | 48 hours from delivery confirmation timestamp |
| **Target devices** | Android 8+ (API 26), 2GB RAM, works on $50 phones |
| **Localization** | Amharic, Afan Oromo, English — all UI strings localized |

---

## Buyer Lifecycle Summary

```
                    ┌──────────┐
                    │ Pending  │ ← Delivery created
                    └────┬─────┘
                         │
              ┌──────────┴──────────┐
              │                     │
     💰 Pay (Escrow)          ❌ Cancel
              │
              ▼
                    ┌──────────┐
                    │Picked Up │ ← Trucker scans farmer's QR
                    └────┬─────┘
                         │
                    ❌ Cancel still available
                         │
                         ▼
                    ┌──────────┐
                    │In Transit│ ← Trucker marks in transit
                    └────┬─────┘
                         │
              📷 Scan trucker's QR
                         │
                         ▼
                    ┌──────────┐
                    │Delivered │ ← Buyer confirms delivery
                    └────┬─────┘
                         │
              ⚠️ Raise Dispute (48h window)
                         │
                         ▼
                    ┌──────────┐
                    │ Disputed │ ← Under review
                    └──────────┘
```
